"""Caller-owned screen connections. Transport is supplied only for explicitly enabled controlled tests."""
import threading
import json
import time
import uuid

from remsup_screen_protocol import CaptureRateRefusal, capture_content, read_metadata, write_metadata


SERVER_ERROR_CODES = frozenset(('screen-session-unavailable','invalid-screen-state','invalid-screen-permission-deadline','screen-response-mismatch','screen-request-refused','desktop-unavailable','geometry-changed','screen-connection-closed','invalid-image-metadata','unsupported-png','capture-failed'))
SETTINGS_ERROR_CODES = frozenset(('settings-disabled','settings-forbidden','settings-invalid-value','permission-required','control-required','settings-busy','settings-unavailable','outcome-unknown'))
SETTINGS_TYPES = {
    'autoUpdates': bool,
    'relayHost': str,
    'relayPort': int,
    'relayPin': str,
    'lanAddress': str,
    'lanPort': int,
    'lanEnabled': bool,
}
SETTINGS_STRING_LIMITS = {'relayHost': 253, 'relayPin': 100, 'lanAddress': 45}

def failure_code(exc):
    code=str(exc)
    if code.startswith('settings-outcome-unknown'):
        return 'outcome-unknown'
    if getattr(exc, 'error', None) == 'outcome-unknown':
        return 'outcome-unknown'
    return 'outcome-unknown' if code.startswith('screen-input-outcome-unknown') else code if code in SERVER_ERROR_CODES else 'refused-or-failed'


def validate_setting(name, value):
    if not isinstance(name, str) or name not in SETTINGS_TYPES:
        raise ValueError('settings-forbidden')
    expected = SETTINGS_TYPES[name]
    if type(value) is not expected:
        raise ValueError('settings-invalid-value')
    if expected is str and len(value) > SETTINGS_STRING_LIMITS[name]:
        raise ValueError('settings-invalid-value')
    if expected is int and not 1 <= value <= 65535:
        raise ValueError('settings-invalid-value')
    if name == 'lanEnabled' and value is not False:
        raise ValueError('settings-forbidden')


def project_settings(value):
    """Validate and project only the receiver's explicitly readable settings."""
    if not isinstance(value, dict) or not set(SETTINGS_TYPES).issubset(value):
        raise ValueError('settings-unavailable')
    projected = {}
    for name, expected in SETTINGS_TYPES.items():
        item = value[name]
        if type(item) is not expected:
            raise ValueError('settings-unavailable')
        if expected is str and len(item) > SETTINGS_STRING_LIMITS[name]:
            raise ValueError('settings-unavailable')
        if expected is int and not 1 <= item <= 65535:
            raise ValueError('settings-unavailable')
        projected[name] = item
    return projected


KEYS = ('ctrl+l', 'ctrl+a', 'ctrl+c', 'ctrl+v', 'ctrl+x', 'ctrl+z', 'ctrl+y', 'ctrl+w', 'ctrl+f', 'ctrl+s', 'ctrl+shift+s', 'enter', 'tab', 'shift+tab', 'escape', 'backspace', 'delete', 'up', 'down', 'left', 'right', 'home', 'end')
REFUSAL_CODES = frozenset(('permission-required','mouse-disabled','typing-disabled','control-required','session-mismatch','stale-frame','geometry-changed','geometry-version-mismatch','coordinates-out-of-range','invalid-action','invalid-scroll','input-rate-limit','protected-window','desktop-unavailable','dpi-unavailable','monitor-unavailable','local-input-active','foreground-required','foreground-monitor-mismatch','modifiers-held','keyboard-context-changed','invalid-keyboard-operation','key-not-allowed','text-size-limit','invalid-text','text-stream-ended','text-stream-mismatch','text-stream-expired','text-stream-active','text-stream-unsupported','text-stream-sequence','text-stream-overrun','lease-expired'))


MAX_TEXT_UNITS = 128      # one receiver text packet
LEGACY_STREAM_UNITS = 1024  # receivers without text streams: at most eight packets, kept inside the 30 s frame window
MAX_STREAM_UNITS = 20000    # receivers with text streams: one frame and one target binding for the whole text
TEXT_WAIT = 3.0             # seconds the text call waits before it reports a running stream, so click, progress and cancel can follow
MAX_PROGRESS_WAIT = 45.0     # longest wait one progress call accepts
INPUT_OPERATIONS = ('mouse', 'text', 'key', 'text.begin', 'text.chunk', 'text.end')
INPUT_SPACING = 0.25      # receiver rejects input requests closer than 200 ms


def utf16_units(value):
    return len(value.encode("utf-16-le")) // 2


def split_text(value, limit=MAX_TEXT_UNITS):
    """Split at Unicode scalar boundaries; no packet exceeds limit UTF-16 units."""
    parts, current, units = [], [], 0
    for ch in value:
        width = utf16_units(ch)
        if units + width > limit:
            parts.append("".join(current))
            current, units = [], 0
        current.append(ch)
        units += width
    if current:
        parts.append("".join(current))
    return parts


class PartialTextInput(RuntimeError):
    """A text stream stopped after some packets were typed; never retried."""
    def __init__(self, error, chunks_sent, chunks_total, units_sent, channel_terminated=None):
        self.error = error
        warning = 'Earlier packets were typed. Do not retry automatically; capture and inspect a new image.'
        if error == 'outcome-unknown':
            warning = 'Earlier packets were typed and the result of the next packet is unknown. Do not retry automatically; capture and inspect a new image.'
        super().__init__('screen-text-stream-stopped: ' + json.dumps({'error': error, 'partial': True, 'chunks_sent': chunks_sent, 'chunks_total': chunks_total, 'units_sent': units_sent, 'channel_terminated': channel_terminated, 'warning': warning}, separators=(',', ':')))


REFUSAL_REASONS = {
    'stale-frame': 'The inspected frame is older than 30 seconds or no longer the latest; capture a new frame and retry.',
    'text-size-limit': 'The text is longer than the receiver accepts in one request.',
    'invalid-text': 'The text contains characters the receiver does not type.',
    'input-rate-limit': 'Input arrived faster than the receiver accepts (one request per 200 ms).',
    'keyboard-context-changed': 'The foreground window or focused control changed, so nothing more was typed.',
    'foreground-required': 'No eligible foreground application window; bring the target window to the front.',
    'local-input-active': 'A modifier key was held at the remote computer; typing stopped.',
    'protected-window': 'The target is a protected (elevated or secure) window that Remsup does not type into.',
    'geometry-changed': 'The screen layout changed; the session was ended and must be requested again.',
    'typing-disabled': 'Typing is switched off on the remote computer.',
    'control-required': 'The session has no control permission.',
    'text-stream-ended': 'The text stream is no longer open; nothing more was typed.',
    'text-stream-expired': 'The text stream was idle for more than 20 seconds and was closed.',
    'text-stream-active': 'Another text stream is already open on this session.',
    'text-stream-unsupported': 'This receiver version cannot type text longer than 128 units in one stream.',
    'text-stream-sequence': 'A text packet arrived out of order; the stream was closed.',
    'text-stream-overrun': 'More text was sent than the stream announced; the stream was closed.',
    'text-stream-mismatch': 'The text stream id does not match the open stream.',
    'lease-expired': 'The screen session lease ended.',
}


class ConfirmedInputRefusal(PermissionError, ValueError):
    def __init__(self,code,channel_terminated=False,stream_open=None):
        self.code=code if code in REFUSAL_CODES else 'remote-tool-refused'
        self.channel_terminated=bool(channel_terminated)
        self.stream_open=stream_open
        body={'error':self.code,'dispatched':False,'channel_terminated':self.channel_terminated}
        if self.code in REFUSAL_REASONS:body['reason']=REFUSAL_REASONS[self.code]
        super().__init__('screen-input-refused-before-dispatch: '+json.dumps(body,separators=(',',':')))


class ScreenSessions:
    def __init__(self, factory=None, clock=time.monotonic, on_drop=None, sleep=time.sleep):
        self._on_drop = on_drop
        self._sleep = sleep
        self._streaming = set()
        self._jobs = {}
        self._factory = factory
        self._clock = clock
        self._gate = threading.RLock()
        self._sessions = {}
        self._closed = {}

    @property
    def available(self):
        return self._factory is not None

    def capabilities(self, caller, device_id, otp):
        if not caller:
            raise PermissionError("authenticated-caller-required")
        if self._factory is None:
            return {"available": False, "reason": "screen-test-mode-disabled", "encrypted": False}
        with self._gate:
            stream = self._factory(device_id, otp)
            try:
                request_id = str(uuid.uuid4())
                write_metadata(stream, {"version": 1, "request_id": request_id, "operation": "capabilities"})
                response = read_metadata(stream)
                if response.get("request_id") != request_id or response.get("version") != 1:
                    raise ValueError("screen-response-mismatch")
                return {**response, "available": True, "encrypted": False, "controlled_test_only": True}
            finally:
                stream.close()

    def _expire(self):
        now = self._clock()
        for key, job in list(self._jobs.items()):
            if job['done'].is_set() and now - job['finished'] >= 630:self._jobs.pop(key, None)
        for key,entry in list(self._closed.items()):
            if now-entry['at']>=630:self._closed.pop(key,None)
        # Pending has one heartbeat grace to observe a last-moment local grant;
        # the remote still rejects approval after its own 30-second deadline.
        for key, entry in list(self._sessions.items()):
            if now >= entry["deadline"] + (3 if entry["state"] == "Pending" else 0) or now - entry["created"] >= 630:
                self._drop(key,'lease-expired')

    def expire(self):
        with self._gate:
            self._expire()

    def heartbeat(self):
        # One lock serializes heartbeat and tool framing on every owned stream.
        with self._gate:
            self._expire()
            for key, entry in list(self._sessions.items()):
                if self._clock() - entry["last"] < 3:
                    continue
                try:
                    self.operation(entry["caller"], key, "status")
                except Exception as exc:
                    self._drop(key,failure_code(exc))

    def _permission_deadline(self, entry, response):
        state = response.get("state")
        if state in ("Off", "Revoked"):
            return False
        if state not in ("Pending", "Granted"):
            raise ValueError("invalid-screen-state")
        remaining = response.get("remaining_ms", 30000 if state == "Pending" else None)
        limit = 30000 if state == "Pending" else 600000
        if type(remaining) is not int or not 0 < remaining <= limit:
            raise ValueError("invalid-screen-permission-deadline")
        for key in ('can_view', 'can_control'):
            if key in response and type(response[key]) is not bool:
                raise ValueError('screen-response-mismatch')
        entry['can_view'] = response.get('can_view') is True
        entry['can_control'] = response.get('can_control') is True
        deadline = self._clock() + remaining / 1000
        if state == "Granted" and entry["state"] == "Pending":
            entry["deadline"] = deadline  # First observed owner grant only.
        else:
            entry["deadline"] = min(entry["deadline"], deadline)
        entry["state"] = state
        return True

    def _drop(self, key, reason='closed'):
        entry = self._sessions.pop(key, None)
        if entry is not None:
            self._closed[key]={'caller':entry['caller'],'reason':reason,'at':self._clock()}
            while len(self._closed)>64:self._closed.pop(next(iter(self._closed)))
            try:entry["stream"].close()
            finally:
                if self._on_drop:self._on_drop(entry['caller'],reason)

    def _settings_response(self, session_id, request_id, entry, error):
        return {'version': 1, 'request_id': request_id, 'session_id': session_id,
                'state': entry['state'], 'can_view': entry.get('can_view') is True,
                'can_control': entry.get('can_control') is True,
                'remaining_ms': max(0, int((entry['deadline'] - self._clock()) * 1000)),
                'settings_error': error}

    def request(self, caller, device_id, otp, monitor_id, control):
        if not caller:
            raise PermissionError("authenticated-caller-required")
        if self._factory is None:
            raise RuntimeError("screen-test-mode-disabled")
        if type(control) is not bool or not isinstance(monitor_id, str) or not 1 <= len(monitor_id) <= 128:
            raise ValueError("invalid-screen-request")
        with self._gate:
            self._expire()
            if len(self._sessions) >= 4:
                raise RuntimeError("screen-capacity-limit")
            stream = self._factory(device_id, otp)
            try:
                request_id = str(uuid.uuid4())
                label = caller[:128]
                if any(ord(c) < 32 or ord(c) == 127 for c in label):
                    raise ValueError("invalid-operator-label")
                write_metadata(stream, {"version": 1, "request_id": request_id, "operation": "request", "monitor_id": monitor_id, "control": control, "operator_label": label})
                response = read_metadata(stream)
                session_id = response["session_id"]
                uuid.UUID(session_id)
                if response.get("state") not in ("Pending", "Granted") or response.get("version") != 1 or response.get("request_id") != request_id:
                    raise RuntimeError("screen-request-refused")
                if session_id in self._sessions:
                    raise RuntimeError("duplicate-screen-session")
                now = self._clock()
                entry = {"caller": caller, "stream": stream, "created": now, "last": now, "state": "Pending", "deadline": now + 30, "can_view": False, "can_control": False}
                if not self._permission_deadline(entry, response):
                    raise RuntimeError("screen-request-refused")
                limit = response.get('text_stream_max_utf16', 0)
                entry['text_stream_max'] = limit if type(limit) is int and 0 <= limit <= MAX_STREAM_UNITS else 0
                self._sessions[session_id] = entry
                return response
            except Exception:
                stream.close()
                raise

    def operation(self, caller, session_id, operation, **fields):
        with self._gate:
            self._expire()
            entry = self._sessions.get(session_id)
            closed=self._closed.get(session_id)
            if caller and entry is None and closed and closed['caller']==caller:
                if operation=='close':return {'result':'already-closed','channel_terminated':True,'close_reason':closed['reason']}
                if operation.startswith('settings.'):
                    return {'version':1,'request_id':str(uuid.uuid4()),'session_id':session_id,'state':'Revoked','can_view':False,'can_control':False,'remaining_ms':0,'settings_error':'permission-required'}
                if closed['reason']=='lease-expired':raise ConfirmedInputRefusal('lease-expired',True)
            if not caller or entry is None or entry["caller"] != caller:
                raise PermissionError("screen-session-unavailable")
            request_id = str(uuid.uuid4())
            if operation not in ("status", "capture", "mouse", "text", "key", "text.begin", "text.chunk", "text.end", "close", "settings.read", "settings.set", "cadence"):
                raise ValueError("invalid-screen-operation")
            if operation == 'settings.read':
                if fields:
                    return self._settings_response(session_id, request_id, entry, 'settings-forbidden')
            elif operation == 'settings.set':
                if set(fields) != {'name', 'value'}:
                    return self._settings_response(session_id, request_id, entry, 'settings-forbidden')
                try:
                    validate_setting(fields['name'], fields['value'])
                except ValueError as exc:
                    return self._settings_response(session_id, request_id, entry, str(exc))
            elif operation == "cadence":
                if set(fields) != {"interval_ms"}:
                    raise ValueError("invalid-screen-input")
                if type(fields["interval_ms"]) is not int or not 50 <= fields["interval_ms"] <= 1000:
                    raise ValueError("interval-out-of-range")
            elif operation in ("text", "key"):
                if set(fields) != {"frame_id", "geometry_version", operation}:
                    raise ValueError("invalid-screen-input")
                uuid.UUID(fields["frame_id"])
                if type(fields["geometry_version"]) is not int or fields["geometry_version"] < 1:
                    raise ValueError("invalid-screen-input")
                value = fields[operation]
                if operation == "key":
                    if value not in KEYS:
                        raise ValueError("key-not-allowed")
                elif not isinstance(value, str) or not 1 <= len(value.encode("utf-16-le")) // 2 <= 128 or any((ord(c) < 32 and c not in "\r\n\t") or 127 <= ord(c) <= 159 for c in value):
                    raise ValueError("invalid-screen-text")
            elif operation == "text.begin":
                if set(fields) != {"frame_id", "geometry_version", "total_units"}:
                    raise ValueError("invalid-screen-input")
                uuid.UUID(fields["frame_id"])
                if type(fields["geometry_version"]) is not int or fields["geometry_version"] < 1 or type(fields["total_units"]) is not int or not 1 <= fields["total_units"] <= MAX_STREAM_UNITS:
                    raise ValueError("invalid-screen-input")
            elif operation == "text.chunk":
                if set(fields) != {"stream_id", "sequence", "text"}:
                    raise ValueError("invalid-screen-input")
                uuid.UUID(fields["stream_id"])
                if type(fields["sequence"]) is not int or not 0 <= fields["sequence"] <= MAX_STREAM_UNITS:
                    raise ValueError("invalid-screen-input")
                value = fields["text"]
                if not isinstance(value, str) or not 1 <= utf16_units(value) <= MAX_TEXT_UNITS or any((ord(c) < 32 and c not in (chr(13), chr(10), chr(9))) or 127 <= ord(c) <= 159 for c in value):
                    raise ValueError("invalid-screen-text")
            elif operation == "text.end":
                if set(fields) != {"stream_id"}:
                    raise ValueError("invalid-screen-input")
                uuid.UUID(fields["stream_id"])
            elif operation == "mouse":
                if set(fields) != {"frame_id", "geometry_version", "action", "x", "y", "scroll_steps"}:
                    raise ValueError("invalid-screen-input")
                uuid.UUID(fields["frame_id"])
                for field in ("geometry_version", "x", "y", "scroll_steps"):
                    if type(fields[field]) is not int:
                        raise ValueError("invalid-screen-input")
                if fields["geometry_version"] < 1 or not 0 <= fields["x"] < 1920 or not 0 <= fields["y"] < 1920:
                    raise ValueError("invalid-screen-input")
                if fields["action"] not in ("click", "double-click", "right-click", "scroll", "move", "down", "up", "left-down", "left-up", "right-down", "right-up", "middle-down", "middle-up"):
                    raise ValueError("invalid-screen-input")
                if fields["action"] == "scroll":
                    if not -10 <= fields["scroll_steps"] <= 10 or fields["scroll_steps"] == 0:
                        raise ValueError("invalid-screen-input")
                elif fields["scroll_steps"] != 0:
                    raise ValueError("invalid-screen-input")
            elif fields:
                raise ValueError("invalid-screen-operation")
            if operation.startswith('settings.') and (entry['state'] != 'Granted' or not entry.get('can_view') or not entry.get('can_control')):
                code = 'permission-required' if entry['state'] != 'Granted' else 'control-required'
                return self._settings_response(session_id, request_id, entry, code)
            request = {"version": 1, "request_id": request_id, "operation": operation, "session_id": session_id}
            request.update(fields)
            # fields are built by fixed tool signatures, never caller-provided JSON.
            settings_write_started = False
            try:
                if operation.startswith('settings.'):
                    status_id = str(uuid.uuid4())
                    write_metadata(entry['stream'], {"version": 1, "request_id": status_id, "operation": "status", "session_id": session_id})
                    status = read_metadata(entry['stream'])
                    if status.get('version') != 1 or status.get('request_id') != status_id or status.get('session_id') != session_id:
                        raise ValueError('screen-response-mismatch')
                    if not self._permission_deadline(entry, status) or status.get('state') != 'Granted':
                        if status.get('state') in ('Off', 'Revoked'):
                            self._drop(session_id, 'permission-ended')
                        return self._settings_response(session_id, request_id, entry, 'permission-required')
                    if entry.get('can_view') is not True:
                        return self._settings_response(session_id, request_id, entry, 'permission-required')
                    if entry.get('can_control') is not True:
                        return self._settings_response(session_id, request_id, entry, 'control-required')
                if operation == 'settings.set':
                    settings_write_started = True
                write_metadata(entry["stream"], request)
                response = capture_content(entry["stream"], request["request_id"], session_id) if operation == "capture" else read_metadata(entry["stream"])
                if operation != "capture" and (response.get("version") != 1 or response.get("request_id") != request["request_id"] or response.get("session_id") != session_id):
                    raise ValueError("screen-response-mismatch")
                if operation.startswith('settings.'):
                    expected = {'version', 'request_id', 'session_id', 'state', 'can_view', 'can_control', 'remaining_ms', 'settings', 'settings_error'}
                    if set(response) != expected or type(response.get('can_view')) is not bool or type(response.get('can_control')) is not bool:
                        raise ValueError('screen-response-mismatch')
                    if not self._permission_deadline(entry, response):
                        result = self._settings_response(session_id, request['request_id'], entry, 'permission-required')
                        self._drop(session_id, 'permission-ended')
                        return result
                    error = response.get('settings_error', '')
                    if not isinstance(error, str) or error not in ('', *SETTINGS_ERROR_CODES):
                        raise ValueError('screen-response-mismatch')
                    if error and response['settings'] is not None:
                        raise ValueError('screen-response-mismatch')
                    result = {'version': 1, 'request_id': request['request_id'], 'session_id': session_id,
                              'state': response['state'], 'can_view': entry['can_view'],
                              'can_control': entry['can_control'], 'remaining_ms': max(0, int((entry['deadline'] - self._clock()) * 1000))}
                    if error:
                        result['settings_error'] = error
                    elif operation == 'settings.read':
                        result['settings'] = project_settings(response.get('settings'))
                    else:
                        raw_result = response.get('settings')
                        if isinstance(raw_result, dict):
                            if raw_result.get('result') != 'applied' or set(raw_result) - {'result', 'connection_may_close'}:
                                raise ValueError('screen-response-mismatch')
                            result['result'] = 'applied'
                            if 'connection_may_close' in raw_result:
                                if type(raw_result['connection_may_close']) is not bool:
                                    raise ValueError('screen-response-mismatch')
                                result['connection_may_close'] = raw_result['connection_may_close']
                        elif raw_result == 'applied':
                            result['result'] = 'applied'
                        else:
                            raise ValueError('screen-response-mismatch')
                    entry['last'] = self._clock()
                    return result
                if operation in INPUT_OPERATIONS and response.get("dispatched") is False and isinstance(response.get("error"), str):
                    code=response['error']
                    terminated=code in ('geometry-changed','lease-expired') or code not in REFUSAL_CODES
                    if terminated:self._drop(session_id,code if code in REFUSAL_CODES else 'closed')
                    raise ConfirmedInputRefusal(code,terminated,response.get('stream_open') if type(response.get('stream_open')) is bool else None)
                if operation in INPUT_OPERATIONS and response.get('error') in ('input-not-applied','outcome-unknown'):
                    dispatched=response.get('dispatched')
                    if 'dispatched' not in response or response.get('channel_terminated') is not False or (response['error']=='input-not-applied' and dispatched is not True) or (response['error']=='outcome-unknown' and dispatched is not None):
                        raise ValueError('screen-response-mismatch')
                    entry['last']=self._clock()
                    return {'error':response['error'],'dispatched':dispatched,'channel_terminated':False,'warning':'Do not retry automatically; take and inspect a new capture.'}
                if operation == "cadence":
                    if not self._permission_deadline(entry, response):
                        self._drop(session_id, 'permission-ended')
                        raise ConfirmedInputRefusal('permission-ended', True)
                    entry['last'] = self._clock()
                    return {'version': 1, 'request_id': request['request_id'], 'session_id': session_id, 'interval_ms': response.get('interval_ms', fields['interval_ms']), 'ok': True}
                entry["last"] = self._clock()
                if operation == "close" or (operation != "capture" and not self._permission_deadline(entry, response)):
                    self._drop(session_id,'closed' if operation=='close' else 'permission-ended')
                return response
            except (ConfirmedInputRefusal, CaptureRateRefusal):
                raise
            except Exception as exc:
                if operation.startswith('settings.'):
                    code = 'outcome-unknown' if settings_write_started else 'settings-unavailable'
                    self._drop(session_id, 'outcome-unknown' if code == 'outcome-unknown' else failure_code(exc))
                    return self._settings_response(session_id, request['request_id'], entry, code)
                self._drop(session_id,'outcome-unknown' if operation in INPUT_OPERATIONS else failure_code(exc))
                if operation in INPUT_OPERATIONS:
                    raise RuntimeError("screen-input-outcome-unknown; do not retry automatically") from exc
                raise

    def text(self, caller, session_id, frame_id, geometry_version, text):
        """Type text of up to MAX_STREAM_UNITS units as sequential packets of at most MAX_TEXT_UNITS.

        Every packet is a normal 'text' operation with the same frame; the next packet is sent only after the
        previous one is acknowledged and INPUT_SPACING has elapsed. Any refusal or unknown result stops the stream."""
        if not isinstance(text, str):
            raise ValueError("invalid-screen-text")
        try:
            units = utf16_units(text)
        except UnicodeEncodeError:
            raise ValueError("invalid-screen-text") from None
        if units < 1 or any((ord(c) < 32 and c not in "\r\n\t") or 127 <= ord(c) <= 159 for c in text):
            raise ValueError("invalid-screen-text")
        with self._gate:
            known = self._sessions.get(session_id)
            limit = known.get('text_stream_max', 0) if known is not None and known['caller'] == caller else 0
        cap = limit if limit > 0 else LEGACY_STREAM_UNITS
        if units > cap:
            raise ValueError("screen-text-too-long: this receiver accepts at most %d UTF-16 units per text request" % cap)
        parts = split_text(text)
        if limit > 0 and len(parts) > 1:
            return self._text_job(caller, session_id, frame_id, geometry_version, units, parts)
        if len(parts) == 1:
            return self.operation(caller, session_id, "text", frame_id=frame_id, geometry_version=geometry_version, text=text)
        with self._gate:
            if session_id in self._streaming:
                raise RuntimeError("screen-text-stream-busy")
            self._streaming.add(session_id)
        sent_units = 0
        try:
            for index, part in enumerate(parts):
                if index:
                    self._sleep(INPUT_SPACING)
                try:
                    result = self.operation(caller, session_id, "text", frame_id=frame_id, geometry_version=geometry_version, text=part)
                except ConfirmedInputRefusal as refusal:
                    if index == 0:
                        raise
                    raise PartialTextInput(refusal.code, index, len(parts), sent_units, refusal.channel_terminated) from refusal
                except Exception as exc:
                    if index == 0:
                        raise
                    # operation() already dropped the session, so the channel is terminated.
                    raise PartialTextInput('outcome-unknown', index, len(parts), sent_units, True) from exc
                if isinstance(result, dict) and result.get("error") in ("input-not-applied", "outcome-unknown"):
                    return {**result, "partial": index > 0, "chunks_sent": index, "chunks_total": len(parts), "units_sent": sent_units}
                sent_units += utf16_units(part)
            return {**result, "chunks_sent": len(parts), "chunks_total": len(parts), "units_sent": sent_units}
        finally:
            with self._gate:
                self._streaming.discard(session_id)

    # Long text (#136). The receiver types the stream against the target bound at begin, one packet at a time. A worker thread
    # sends the packets so one tool call never outlives the caller's timeout; progress and cancel are separate calls.
    @staticmethod
    def _job_view(job):
        view = {'result': job['state'], 'stream_id': job['stream_id'], 'complete': job['state'] == 'complete', 'typed_units': job['typed'],
                'total_units': job['total'], 'chunks_sent': job['chunks'], 'chunks_total': job['chunks_total']}
        if job.get('error'):
            view.update({'error': job['error'], 'dispatched': job['dispatched'], 'channel_terminated': job['terminated']})
            if job['error'] in REFUSAL_REASONS:view['reason'] = REFUSAL_REASONS[job['error']]
            view['warning'] = 'Typed units are those confirmed before the stop. Do not retry automatically; capture and inspect a new image.'
        elif job['state'] == 'running':
            view['warning'] = 'Text is still being typed. Call remsup_screen_text_progress to follow it or remsup_screen_text_cancel to stop it.'
        return view

    def _finish_job(self, job, state, error=None, dispatched=False, terminated=False):
        job['state'] = state
        if error:
            job['error'], job['dispatched'], job['terminated'] = error, dispatched, terminated

    def _text_job(self, caller, session_id, frame_id, geometry_version, units, parts):
        with self._gate:
            if session_id in self._streaming:
                raise RuntimeError("screen-text-stream-busy")
            self._streaming.add(session_id)
        try:
            begin = self.operation(caller, session_id, "text.begin", frame_id=frame_id, geometry_version=geometry_version, total_units=units)
            stream_id = begin.get('stream_id') if isinstance(begin, dict) else None
            if not isinstance(stream_id, str):
                with self._gate:
                    self._streaming.discard(session_id)
                return begin
            uuid.UUID(stream_id)
        except BaseException:
            with self._gate:
                self._streaming.discard(session_id)
            raise
        job = {'caller': caller, 'session': session_id, 'stream_id': stream_id, 'state': 'running', 'total': units, 'typed': 0, 'chunks': 0,
               'chunks_total': len(parts), 'cancel': threading.Event(), 'done': threading.Event(), 'finished': 0.0}
        with self._gate:
            self._jobs[session_id] = job
        threading.Thread(target=self._run_text_job, args=(job, parts), daemon=True).start()
        job['done'].wait(TEXT_WAIT)
        return self._job_view(job)

    def _end_stream(self, job):
        try:
            self.operation(job['caller'], job['session'], "text.end", stream_id=job['stream_id'])
        except Exception:
            pass

    def _run_text_job(self, job, parts):
        caller, session_id = job['caller'], job['session']
        try:
            for index, part in enumerate(parts):
                if index:
                    self._sleep(INPUT_SPACING)
                if job['cancel'].is_set():
                    self._end_stream(job)
                    self._finish_job(job, 'cancelled')
                    return
                attempts = 0
                while True:
                    try:
                        result = self.operation(caller, session_id, "text.chunk", stream_id=job['stream_id'], sequence=index, text=part)
                    except ConfirmedInputRefusal as refusal:
                        # A rate-limit refusal dispatched nothing and leaves the stream open: wait and send the same packet again.
                        if refusal.code == 'input-rate-limit' and refusal.stream_open is True and attempts < 3 and not refusal.channel_terminated:
                            attempts += 1
                            self._sleep(INPUT_SPACING)
                            continue
                        self._finish_job(job, 'stopped', refusal.code, False, refusal.channel_terminated)
                        return
                    except PermissionError as exc:
                        if str(exc) == 'screen-session-unavailable':
                            self._finish_job(job, 'stopped', 'session-ended', False, True)
                            return
                        self._finish_job(job, 'stopped', 'outcome-unknown', None, True)
                        return
                    except Exception:
                        self._finish_job(job, 'stopped', 'outcome-unknown', None, True)
                        return
                    break
                if isinstance(result, dict) and result.get('error') in ('input-not-applied', 'outcome-unknown'):
                    self._finish_job(job, 'stopped', result['error'], result.get('dispatched'), False)
                    return
                expected = job['typed'] + utf16_units(part)
                if not isinstance(result, dict) or result.get('typed_units') != expected:
                    self._end_stream(job)
                    self._finish_job(job, 'stopped', 'screen-response-mismatch', None, False)
                    return
                job['typed'] = expected
                job['chunks'] = index + 1
            self._end_stream(job)
            self._finish_job(job, 'complete')
        finally:
            job['finished'] = self._clock()
            with self._gate:
                self._streaming.discard(session_id)
            job['done'].set()

    def _own_job(self, caller, session_id):
        with self._gate:
            self._expire()
            job = self._jobs.get(session_id)
            if job is not None and (not caller or job['caller'] != caller):
                raise PermissionError("screen-session-unavailable")
            return job

    def text_progress(self, caller, session_id, wait=0):
        job = self._own_job(caller, session_id)
        if job is None:
            return {'result': 'none', 'complete': False, 'typed_units': 0, 'total_units': 0}
        if type(wait) not in (int, float) or not 0 <= wait <= MAX_PROGRESS_WAIT:
            raise ValueError("invalid-screen-input")
        if wait and not job['done'].is_set():
            job['done'].wait(wait)
        return self._job_view(job)

    def text_cancel(self, caller, session_id):
        job = self._own_job(caller, session_id)
        if job is None:
            return {'result': 'none', 'complete': False, 'typed_units': 0, 'total_units': 0}
        job['cancel'].set()
        job['done'].wait(5)
        return {**self._job_view(job), 'cancel_requested': True}

    def close_all(self):
        with self._gate:
            for job in self._jobs.values():
                job['cancel'].set()
            for key in list(self._sessions):
                self._drop(key)
