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


KEYS = ('ctrl+l', 'ctrl+a', 'ctrl+s', 'ctrl+shift+s', 'enter', 'tab', 'shift+tab', 'escape', 'backspace', 'delete', 'up', 'down', 'left', 'right', 'home', 'end')
REFUSAL_CODES = frozenset(('permission-required','mouse-disabled','typing-disabled','control-required','session-mismatch','stale-frame','geometry-changed','geometry-version-mismatch','coordinates-out-of-range','invalid-action','invalid-scroll','input-rate-limit','protected-window','desktop-unavailable','dpi-unavailable','monitor-unavailable','local-input-active','foreground-required','foreground-monitor-mismatch','modifiers-held','keyboard-context-changed','invalid-keyboard-operation','key-not-allowed','text-size-limit','invalid-text','lease-expired'))


class ConfirmedInputRefusal(PermissionError, ValueError):
    def __init__(self,code,channel_terminated=False):
        self.code=code if code in REFUSAL_CODES else 'remote-tool-refused'
        self.channel_terminated=bool(channel_terminated)
        super().__init__('screen-input-refused-before-dispatch: '+json.dumps({'error':self.code,'dispatched':False,'channel_terminated':self.channel_terminated},separators=(',',':')))


class ScreenSessions:
    def __init__(self, factory=None, clock=time.monotonic, on_drop=None):
        self._on_drop = on_drop
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
            if operation not in ("status", "capture", "mouse", "text", "key", "close", "settings.read", "settings.set"):
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
            elif operation == "mouse":
                if set(fields) != {"frame_id", "geometry_version", "action", "x", "y", "scroll_steps"}:
                    raise ValueError("invalid-screen-input")
                uuid.UUID(fields["frame_id"])
                for field in ("geometry_version", "x", "y", "scroll_steps"):
                    if type(fields[field]) is not int:
                        raise ValueError("invalid-screen-input")
                if fields["geometry_version"] < 1 or not 0 <= fields["x"] < 1920 or not 0 <= fields["y"] < 1920:
                    raise ValueError("invalid-screen-input")
                if fields["action"] not in ("click", "double-click", "right-click", "scroll"):
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
                if operation in ("mouse", "text", "key") and response.get("dispatched") is False and isinstance(response.get("error"), str):
                    code=response['error']
                    terminated=code in ('geometry-changed','lease-expired') or code not in REFUSAL_CODES
                    if terminated:self._drop(session_id,code if code in REFUSAL_CODES else 'closed')
                    raise ConfirmedInputRefusal(code,terminated)
                if operation in ('mouse','text','key') and response.get('error') in ('input-not-applied','outcome-unknown'):
                    dispatched=response.get('dispatched')
                    if 'dispatched' not in response or response.get('channel_terminated') is not False or (response['error']=='input-not-applied' and dispatched is not True) or (response['error']=='outcome-unknown' and dispatched is not None):
                        raise ValueError('screen-response-mismatch')
                    entry['last']=self._clock()
                    return {'error':response['error'],'dispatched':dispatched,'channel_terminated':False,'warning':'Do not retry automatically; take and inspect a new capture.'}
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
                self._drop(session_id,'outcome-unknown' if operation in ('mouse','text','key') else failure_code(exc))
                if operation in ("mouse", "text", "key"):
                    raise RuntimeError("screen-input-outcome-unknown; do not retry automatically") from exc
                raise

    def close_all(self):
        with self._gate:
            for key in list(self._sessions):
                self._drop(key)
