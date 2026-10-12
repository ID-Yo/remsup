import io
import sys
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))
from remsup_screen_protocol import read_metadata, write_metadata
from remsup_screen_sessions import ScreenSessions


class SyntheticChannel:
    def __init__(self):
        self.session_id = str(uuid.uuid4())
        self.incoming = io.BytesIO()
        self.sent = bytearray()
        self.closed = False

    def write(self, data):
        self.sent.extend(data)
        request = read_metadata(io.BytesIO(data))
        if request["operation"] == "request":
            response = io.BytesIO()
            write_metadata(response, {"version": 1, "request_id": request["request_id"], "session_id": self.session_id, "state": "Pending"})
            self.incoming = io.BytesIO(response.getvalue())

    def read(self, size):
        return self.incoming.read(size)

    def close(self):
        self.closed = True


class ScreenSessionsTests(unittest.TestCase):
    def test_second_negative_monitor_status_then_capture(self):
        from test_screen_http import SyntheticStream
        from mcp.types import ImageContent
        class Monitors(SyntheticStream):
            def write(self,data):
                request=read_metadata(io.BytesIO(data))
                if request['operation']=='capabilities':
                    self.reply=io.BytesIO();write_metadata(self.reply,{'version':1,'request_id':request['request_id'],'monitors':[{'monitor_id':'primary','x':0,'y':0,'width':1920,'height':1080},{'monitor_id':'left','x':-1920,'y':0,'width':1920,'height':1080}]});self.reply.seek(0);return
                super().write(data)
                if request['operation']=='capture':
                    metadata=read_metadata(self.reply);metadata['monitor_id']='left';image=self.reply.read();self.reply=io.BytesIO();write_metadata(self.reply,metadata);self.reply.write(image);self.reply.seek(0)
        stream=Monitors();service=ScreenSessions(lambda *_:stream)
        self.assertEqual(service.capabilities('agent','synthetic','synthetic')['monitors'][1]['x'],-1920)
        stream=Monitors();session=service.request('agent','synthetic','synthetic','left',True)['session_id'];stream.grant()
        self.assertEqual(service.operation('agent',session,'status')['state'],'Granted')
        image=service.operation('agent',session,'capture');self.assertTrue(any(isinstance(x,ImageContent) for x in image));self.assertFalse(stream.closed)
        service.operation('agent',session,'close')
    def test_drop_reason_is_bounded_and_owner_only(self):
        events=[];stream=SyntheticChannel();service=ScreenSessions(lambda *_:stream,on_drop=lambda caller,reason:events.append((caller,reason)))
        sid=service.request('agent','synthetic','synthetic','monitor',True)['session_id']
        service._drop(sid,'invalid-screen-state')
        self.assertEqual(events,[('agent','invalid-screen-state')])
        self.assertEqual(service.operation('agent',sid,'close')['close_reason'],'invalid-screen-state')
        with self.assertRaises(PermissionError):service.operation('other',sid,'close')

    def test_dispatched_failure_keeps_session_for_new_capture(self):
        class FailedChannel(SyntheticChannel):
            def write(self,data):
                request=read_metadata(io.BytesIO(data));response=io.BytesIO()
                result={'version':1,'request_id':request['request_id'],'session_id':self.session_id,'state':'Granted','remaining_ms':600000}
                if request['operation']=='mouse':result={'version':1,'request_id':request['request_id'],'session_id':self.session_id,'error':'input-not-applied','dispatched':True,'channel_terminated':False}
                write_metadata(response,result);self.incoming=io.BytesIO(response.getvalue())
        channel=FailedChannel();service=ScreenSessions(lambda *_:channel)
        session=service.request('agent','synthetic','synthetic','monitor',True)['session_id']
        result=service.operation('agent',session,'mouse',frame_id=str(uuid.uuid4()),geometry_version=1,action='click',x=0,y=0,scroll_steps=0)
        self.assertEqual(result['error'],'input-not-applied');self.assertFalse(channel.closed)
        self.assertEqual(service.operation('agent',session,'status')['state'],'Granted')

    def test_refusal_codes_and_caller_owned_idempotent_close(self):
        from remsup_screen_sessions import ConfirmedInputRefusal
        for code in ('stale-frame','geometry-changed','protected-window','local-input-active'):
            class RefusingChannel(SyntheticChannel):
                def write(self,data):
                    request=read_metadata(io.BytesIO(data));response=io.BytesIO()
                    result={'version':1,'request_id':request['request_id'],'session_id':self.session_id,'state':'Granted','remaining_ms':600000}
                    if request['operation']=='mouse':result={'version':1,'request_id':request['request_id'],'session_id':self.session_id,'error':code,'dispatched':False}
                    write_metadata(response,result);self.incoming=io.BytesIO(response.getvalue())
            channel=RefusingChannel();service=ScreenSessions(lambda *_:channel)
            session=service.request('agent','synthetic','synthetic','monitor',True)['session_id']
            with self.assertRaises(ConfirmedInputRefusal) as failure:
                service.operation('agent',session,'mouse',frame_id=str(uuid.uuid4()),geometry_version=1,action='click',x=0,y=0,scroll_steps=0)
            self.assertEqual(failure.exception.code,code)
            self.assertEqual(failure.exception.channel_terminated,code=='geometry-changed')
            service.operation('agent',session,'close')
            self.assertEqual(service.operation('agent',session,'close')['result'],'already-closed')
            with self.assertRaises(PermissionError):service.operation('other',session,'close')
    def test_expired_lease_reports_terminated_and_close_succeeds(self):
        from remsup_screen_sessions import ConfirmedInputRefusal
        now=[0];channel=SyntheticChannel();service=ScreenSessions(lambda *_:channel,clock=lambda:now[0])
        session=service.request('agent','synthetic','synthetic','monitor',False)['session_id'];now[0]=34
        with self.assertRaises(ConfirmedInputRefusal) as failure:service.operation('agent',session,'capture')
        self.assertEqual(failure.exception.code,'lease-expired');self.assertTrue(failure.exception.channel_terminated)
        self.assertEqual(service.operation('agent',session,'close')['result'],'already-closed')
    def test_refusal_allowlist_matches_native_and_does_not_echo_unknown_data(self):
        import re
        from remsup_screen_sessions import REFUSAL_CODES, ConfirmedInputRefusal
        source=(Path(__file__).resolve().parents[2]/'desktop/host/ScreenChannel.cs').read_text(encoding='utf-8')
        body=source.split('static bool RefusedBeforeDispatch',1)[1].split('internal bool ProcessOne',1)[0]
        self.assertEqual(set(re.findall(r'case "([a-z-]+)"',body)),set(REFUSAL_CODES)-{'lease-expired'})
        self.assertNotIn('PRIVATE_SENTINEL',str(ConfirmedInputRefusal('PRIVATE_SENTINEL',True)))
    def test_closed_history_is_bounded_and_expires(self):
        now=[0];service=ScreenSessions(lambda *_:SyntheticChannel(),clock=lambda:now[0]);ids=[]
        for _ in range(70):
            session=service.request('agent','synthetic','synthetic','monitor',False)['session_id'];ids.append(session)
            service._drop(session)
        with self.assertRaises(PermissionError):service.operation('agent',ids[0],'close')
        self.assertEqual(service.operation('agent',ids[-1],'close')['result'],'already-closed')
        now[0]=631
        with self.assertRaises(PermissionError):service.operation('agent',ids[-1],'close')
    def test_initial_autoaccepted_grant_has_bounded_owner_deadline(self):
        class AutoChannel(SyntheticChannel):
            def write(self, data):
                super().write(data)
                request = read_metadata(io.BytesIO(data))
                response = io.BytesIO()
                write_metadata(response, {"version": 1, "request_id": request["request_id"], "session_id": self.session_id, "state": "Granted", "remaining_ms": 600000})
                self.incoming = io.BytesIO(response.getvalue())
        now = [0]
        channel = AutoChannel()
        service = ScreenSessions(lambda *_: channel, clock=lambda: now[0])
        self.assertEqual(service.request("agent", "synthetic", "synthetic", "monitor", True)["state"], "Granted")
        now[0] = 600
        service.expire()
        self.assertTrue(channel.closed)

    def test_keyboard_contract_matches_native(self):
        import re
        from remsup_screen_sessions import KEYS
        source = (Path(__file__).resolve().parents[2] / "shared" / "ScreenKeyboard.cs").read_text(encoding="utf-8")
        declaration = source.split("Keys", 1)[1].split(";", 1)[0]
        self.assertEqual(tuple(re.findall(r'"([a-z+]+)"', declaration)), KEYS)

    def test_no_production_factory_and_no_unauthenticated_request(self):
        service = ScreenSessions()
        with self.assertRaises(RuntimeError):
            service.request("agent", "synthetic-id", "synthetic-only", "monitor", False)
        service = ScreenSessions(lambda *_: self.fail("must not open connection"))
        with self.assertRaises(PermissionError):
            service.request("", "synthetic-id", "synthetic-only", "monitor", False)

    def test_caller_isolation_and_pending_expiry_close_synthetic_stream(self):
        now = [0]
        channel = SyntheticChannel()
        service = ScreenSessions(lambda *_: channel, lambda: now[0])
        result = service.request("agent-a", "synthetic-id", "synthetic-only", "monitor", False)
        sent = bytes(channel.sent)
        with self.assertRaises(PermissionError):
            service.operation("agent-b", result["session_id"], "status")
        self.assertEqual(bytes(channel.sent), sent)
        self.assertFalse(channel.closed)
        now[0] = 33
        with self.assertRaises(PermissionError):
            service.operation("agent-a", result["session_id"], "capture")
        self.assertTrue(channel.closed)

    def test_input_disconnect_unknown_outcome_and_no_retry(self):
        channel = SyntheticChannel()
        service = ScreenSessions(lambda *_: channel)
        result = service.request("agent", "synthetic-id", "synthetic-only", "monitor", True)
        before = len(channel.sent)
        with self.assertRaisesRegex(RuntimeError, "outcome-unknown"):
            service.operation("agent", result["session_id"], "mouse", frame_id=str(uuid.uuid4()), geometry_version=1, action="click", x=0, y=0, scroll_steps=0)
        after = len(channel.sent)
        self.assertGreater(after, before)
        with self.assertRaises(PermissionError):
            service.operation("agent", result["session_id"], "mouse")
        self.assertEqual(len(channel.sent), after)
        self.assertTrue(channel.closed)

    def test_heartbeat_observes_grant_once_and_never_extends_deadline(self):
        class GrantedChannel(SyntheticChannel):
            def write(self, data):
                request = read_metadata(io.BytesIO(data))
                if request["operation"] == "request":
                    return super().write(data)
                self.sent.extend(data)
                response = io.BytesIO()
                # Deliberately repeats the full remaining time: it must not renew.
                write_metadata(response, {"version": 1, "request_id": request["request_id"], "session_id": self.session_id, "state": "Granted", "remaining_ms": 600000})
                self.incoming = io.BytesIO(response.getvalue())
        now = [0]
        channel = GrantedChannel()
        service = ScreenSessions(lambda *_: channel, lambda: now[0])
        session = service.request("agent", "synthetic-id", "synthetic-only", "monitor", True)["session_id"]
        now[0] = 30  # Late local grant may be observed on the next heartbeat.
        service.heartbeat()
        self.assertEqual(service._sessions[session]["deadline"], 630)
        now[0] = 61
        service.heartbeat()
        self.assertEqual(service._sessions[session]["deadline"], 630)
        self.assertFalse(channel.closed)
        now[0] = 630
        service.heartbeat()
        self.assertTrue(channel.closed)
        with self.assertRaises(PermissionError):
            service.operation("agent", session, "status")

    def test_correlated_capture_rate_refusal_preserves_channel_and_deadline(self):
        class RefusedCapture(SyntheticChannel):
            def write(self, data):
                request = read_metadata(io.BytesIO(data))
                if request["operation"] == "request":
                    return super().write(data)
                self.sent.extend(data)
                response = io.BytesIO()
                if request["operation"] == "capture":
                    value = {"version": 1, "request_id": request["request_id"], "session_id": self.session_id, "error": "capture-rate-limit", "dispatched": False}
                else:
                    value = {"version": 1, "request_id": request["request_id"], "session_id": self.session_id, "state": "Granted", "remaining_ms": 600000}
                write_metadata(response, value)
                self.incoming = io.BytesIO(response.getvalue())
        now = [0]
        channel = RefusedCapture()
        service = ScreenSessions(lambda *_: channel, lambda: now[0])
        session = service.request("agent", "synthetic-id", "synthetic-only", "monitor", True)["session_id"]
        service.operation("agent", session, "status")
        deadline = service._sessions[session]["deadline"]
        now[0] = 1
        with self.assertRaisesRegex(ValueError, "capture-rate-limit"):
            service.operation("agent", session, "capture")
        self.assertFalse(channel.closed)
        self.assertEqual(service._sessions[session]["deadline"], deadline)
        self.assertEqual(service.operation("agent", session, "status")["state"], "Granted")
        service.close_all()

    def test_mismatched_capture_refusal_drops_channel(self):
        class MismatchedCapture(SyntheticChannel):
            def write(self, data):
                request = read_metadata(io.BytesIO(data))
                if request["operation"] == "request":
                    return super().write(data)
                response = io.BytesIO()
                write_metadata(response, {"version": 1, "request_id": str(uuid.uuid4()), "session_id": self.session_id, "error": "capture-rate-limit", "dispatched": False})
                self.incoming = io.BytesIO(response.getvalue())
        channel = MismatchedCapture()
        service = ScreenSessions(lambda *_: channel)
        session = service.request("agent", "synthetic-id", "synthetic-only", "monitor", True)["session_id"]
        with self.assertRaisesRegex(ValueError, "invalid-image-metadata"):
            service.operation("agent", session, "capture")
        self.assertTrue(channel.closed)

    def test_capacity_and_cleanup(self):
        channels = []
        def factory(*_):
            channel = SyntheticChannel(); channels.append(channel); return channel
        service = ScreenSessions(factory)
        for _ in range(4):
            service.request("agent", "synthetic-id", "synthetic-only", "monitor", False)
        with self.assertRaises(RuntimeError):
            service.request("agent", "synthetic-id", "synthetic-only", "monitor", False)
        self.assertEqual(len(channels), 4)
        service.close_all()
        self.assertTrue(all(channel.closed for channel in channels))


if __name__ == "__main__":
    unittest.main()
