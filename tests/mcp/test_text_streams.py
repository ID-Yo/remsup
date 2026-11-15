import io
import sys
import threading
import time
import unittest
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))
import remsup_screen_sessions as sessions
from remsup_screen_protocol import read_metadata, write_metadata
from remsup_screen_sessions import ConfirmedInputRefusal, ScreenSessions, utf16_units


class StreamChannel:
    """Receiver double with text streams. script(op, n) decides the answer for the n-th request of that operation."""
    def __init__(self, script=None, limit=20000):
        self.session_id = str(uuid.uuid4())
        self.stream_id = str(uuid.uuid4())
        self.script = script or (lambda op, n: "ok")
        self.limit = limit
        self.incoming = io.BytesIO()
        self.closed = False
        self.counts = {}
        self.begins, self.packets, self.ends = [], [], []
        self.typed = 0
        self.total = 0

    def write(self, data):
        request = read_metadata(io.BytesIO(data))
        op = request["operation"]
        self.counts[op] = self.counts.get(op, 0) + 1
        base = {"version": 1, "request_id": request["request_id"], "session_id": self.session_id}
        if op == "request":
            answer = {**base, "state": "Pending", "text_stream_max_utf16": self.limit}
        else:
            answer = {**base, "state": "Granted", "remaining_ms": 600000, "can_view": True, "can_control": True}
            verdict = self.script(op, self.counts[op])
            if op == "text.begin":
                self.begins.append(request)
                self.total = request["total_units"]
                self.typed = 0
            elif op == "text.chunk":
                if verdict == "ok":
                    self.packets.append(request)
            elif op == "text.end":
                self.ends.append(request)
            if verdict == "drop":
                self.incoming = io.BytesIO()
                return
            if isinstance(verdict, tuple):
                answer = {**base, "error": verdict[0], "dispatched": False, "channel_terminated": False, "stream_open": verdict[1]}
            elif verdict == "unknown":
                answer = {**base, "error": "outcome-unknown", "dispatched": None, "channel_terminated": False}
            elif op == "text.begin":
                answer.update({"stream_id": self.stream_id, "typed_units": 0, "total_units": self.total, "complete": False})
            elif op in ("text.chunk", "text.end"):
                if op == "text.chunk":
                    self.typed += utf16_units(request["text"])
                answer.update({"stream_id": self.stream_id, "typed_units": self.typed, "total_units": self.total, "complete": self.typed == self.total})
        out = io.BytesIO()
        write_metadata(out, answer)
        self.incoming = io.BytesIO(out.getvalue())

    def read(self, size):
        return self.incoming.read(size)

    def close(self):
        self.closed = True


def open_session(channel, sleep=lambda s: None):
    service = ScreenSessions(lambda *_: channel, sleep=sleep)
    session = service.request("agent", "synthetic", "synthetic", "monitor", True)["session_id"]
    return service, session


def sample_text(units):
    base = "Тест 123 \u00e9 abc \U0001F600 {}[]"
    out = ""
    while utf16_units(out) < units:
        out += base
    while utf16_units(out) > units:
        out = out[:-1]
    return out


class TextStreamTests(unittest.TestCase):
    def test_long_text_is_one_stream_with_one_frame_and_exact_content(self):
        channel = StreamChannel()
        service, session = open_session(channel)
        text = sample_text(5000)
        # a surrogate pair straddling a packet boundary must move whole into the next packet
        text = text[:127] + "\U0001F600" + text[129:]
        frame = str(uuid.uuid4())
        result = service.text("agent", session, frame, 1, text)
        self.assertEqual(result["result"], "complete")
        self.assertTrue(result["complete"])
        self.assertEqual((result["typed_units"], result["total_units"]), (utf16_units(text), utf16_units(text)))
        self.assertEqual(len(channel.begins), 1)
        self.assertEqual(channel.begins[0]["frame_id"], frame)
        self.assertEqual(channel.begins[0]["total_units"], utf16_units(text))
        self.assertEqual("".join(p["text"] for p in channel.packets), text)
        self.assertEqual([p["sequence"] for p in channel.packets], list(range(len(channel.packets))))
        self.assertTrue(all(utf16_units(p["text"]) <= 128 and "frame_id" not in p for p in channel.packets))
        self.assertEqual(len(channel.ends), 1)
        self.assertEqual(service.operation("agent", session, "status")["state"], "Granted")

    def test_packets_are_paced(self):
        sleeps = []
        channel = StreamChannel()
        service, session = open_session(channel, sleeps.append)
        service.text("agent", session, str(uuid.uuid4()), 1, "a" * 400)
        self.assertEqual(sleeps, [0.25] * 3)

    def test_short_text_stays_one_plain_request_on_a_stream_receiver(self):
        channel = StreamChannel()
        service, session = open_session(channel)
        service.text("agent", session, str(uuid.uuid4()), 1, "hello")
        self.assertNotIn("text.begin", channel.counts)
        self.assertEqual(channel.counts["text"], 1)

    def test_limits(self):
        channel = StreamChannel()
        service, session = open_session(channel)
        service.text("agent", session, str(uuid.uuid4()), 1, "z" * 20000)
        with self.assertRaisesRegex(ValueError, "screen-text-too-long: .* at most 20000"):
            service.text("agent", session, str(uuid.uuid4()), 1, "z" * 20001)
        legacy = StreamChannel(limit=0)
        service, session = open_session(legacy)
        with self.assertRaisesRegex(ValueError, "screen-text-too-long: .* at most 1024"):
            service.text("agent", session, str(uuid.uuid4()), 1, "z" * 1025)

    def test_refusal_mid_stream_reports_progress_and_keeps_the_session(self):
        channel = StreamChannel(lambda op, n: ("keyboard-context-changed", False) if op == "text.chunk" and n == 3 else "ok")
        service, session = open_session(channel)
        result = service.text("agent", session, str(uuid.uuid4()), 1, "q" * 600)
        self.assertEqual(result["result"], "stopped")
        self.assertEqual(result["error"], "keyboard-context-changed")
        self.assertIs(result["dispatched"], False)
        self.assertIs(result["channel_terminated"], False)
        self.assertEqual((result["typed_units"], result["chunks_sent"]), (256, 2))
        self.assertIn("foreground window", result["reason"])
        self.assertFalse(channel.closed)
        self.assertEqual(service.operation("agent", session, "status")["state"], "Granted")

    def test_begin_refusal_is_a_plain_refusal_and_keeps_the_session(self):
        channel = StreamChannel(lambda op, n: ("stale-frame", False) if op == "text.begin" else "ok")
        service, session = open_session(channel)
        with self.assertRaises(ConfirmedInputRefusal) as caught:
            service.text("agent", session, str(uuid.uuid4()), 1, "w" * 300)
        self.assertEqual(caught.exception.code, "stale-frame")
        self.assertFalse(caught.exception.channel_terminated)
        self.assertIn("older than 30 seconds", str(caught.exception))
        self.assertEqual(service.operation("agent", session, "status")["state"], "Granted")
        # the busy flag was released: a new stream can begin
        channel.script = lambda op, n: "ok"
        self.assertEqual(service.text("agent", session, str(uuid.uuid4()), 1, "w" * 300)["result"], "complete")

    def test_rate_limit_refusal_retries_the_same_packet(self):
        channel = StreamChannel(lambda op, n: ("input-rate-limit", True) if op == "text.chunk" and n == 2 else "ok")
        service, session = open_session(channel)
        text = "".join(chr(0x430 + i % 32) for i in range(400))
        result = service.text("agent", session, str(uuid.uuid4()), 1, text)
        self.assertEqual(result["result"], "complete")
        self.assertEqual("".join(p["text"] for p in channel.packets), text)
        self.assertEqual(channel.counts["text.chunk"], 5)

    def test_ended_stream_is_not_retried(self):
        channel = StreamChannel(lambda op, n: ("text-stream-expired", False) if op == "text.chunk" and n == 2 else "ok")
        service, session = open_session(channel)
        result = service.text("agent", session, str(uuid.uuid4()), 1, "e" * 400)
        self.assertEqual((result["result"], result["error"], result["typed_units"]), ("stopped", "text-stream-expired", 128))
        self.assertEqual(channel.counts["text.chunk"], 2)

    def test_unknown_outcome_stops_without_replay_and_drops_the_session(self):
        channel = StreamChannel(lambda op, n: "drop" if op == "text.chunk" and n == 2 else "ok")
        service, session = open_session(channel)
        result = service.text("agent", session, str(uuid.uuid4()), 1, "u" * 400)
        self.assertEqual((result["result"], result["error"], result["typed_units"]), ("stopped", "outcome-unknown", 128))
        self.assertIsNone(result["dispatched"])
        self.assertTrue(result["channel_terminated"])
        self.assertTrue(channel.closed)
        self.assertEqual(channel.counts["text.chunk"], 2)

    def test_receiver_reported_unknown_outcome_stops_the_stream(self):
        channel = StreamChannel(lambda op, n: "unknown" if op == "text.chunk" and n == 2 else "ok")
        service, session = open_session(channel)
        result = service.text("agent", session, str(uuid.uuid4()), 1, "k" * 400)
        self.assertEqual((result["result"], result["error"], result["typed_units"]), ("stopped", "outcome-unknown", 128))
        self.assertEqual(channel.counts["text.chunk"], 2)

    def test_cancel_and_progress_while_running(self):
        saved = sessions.TEXT_WAIT
        sessions.TEXT_WAIT = 0.2
        gate = threading.Event()
        try:
            channel = StreamChannel()
            service, session = open_session(channel, lambda s: gate.wait(5))
            running = service.text("agent", session, str(uuid.uuid4()), 1, "r" * 1000)
            self.assertEqual(running["result"], "running")
            self.assertEqual((running["typed_units"], running["total_units"]), (128, 1000))
            with self.assertRaisesRegex(RuntimeError, "screen-text-stream-busy"):
                service.text("agent", session, str(uuid.uuid4()), 1, "r" * 300)
            self.assertEqual(service.text_progress("agent", session)["result"], "running")
            with self.assertRaises(PermissionError):
                service.text_cancel("other", session)
            outcome = []
            worker = threading.Thread(target=lambda: outcome.append(service.text_cancel("agent", session)))
            worker.start()
            time.sleep(0.1)
            gate.set()
            worker.join(5)
            self.assertEqual(outcome[0]["result"], "cancelled")
            self.assertTrue(outcome[0]["cancel_requested"])
            self.assertEqual(outcome[0]["typed_units"], 128)
            self.assertEqual(channel.counts["text.chunk"], 1)
            self.assertEqual(len(channel.ends), 1)
            self.assertEqual(service.text_progress("agent", session)["result"], "cancelled")
            self.assertEqual(service.operation("agent", session, "status")["state"], "Granted")
        finally:
            sessions.TEXT_WAIT = saved
            gate.set()

    def test_first_call_hands_control_back_within_seconds(self):
        # The default wait is a few seconds, so click, progress and cancel can follow a long text at once.
        self.assertTrue(0 < sessions.TEXT_WAIT <= 5)
        gate = threading.Event()
        try:
            channel = StreamChannel()
            service, session = open_session(channel, lambda s: gate.wait(20))
            started = time.monotonic()
            running = service.text("agent", session, str(uuid.uuid4()), 1, "r" * 5000)
            self.assertLess(time.monotonic() - started, 10)
            self.assertEqual(running["result"], "running")
            started = time.monotonic()
            threading.Timer(0.1, gate.set).start()
            cancelled = service.text_cancel("agent", session)
            self.assertLess(time.monotonic() - started, 6)
            self.assertEqual(cancelled["result"], "cancelled")
        finally:
            gate.set()

    def test_typed_units_count_whole_packets_so_they_need_not_be_multiples_of_128(self):
        # A surrogate pair is never split: the packet that would end inside one carries 127 units.
        saved = sessions.TEXT_WAIT
        sessions.TEXT_WAIT = 0.2
        gate = threading.Event()
        try:
            channel = StreamChannel()
            service, session = open_session(channel, lambda s: gate.wait(5))
            text = "a" * 127 + "\U0001F600" + "b" * 10
            running = service.text("agent", session, str(uuid.uuid4()), 1, text)
            self.assertEqual((running["result"], running["typed_units"], running["total_units"]), ("running", 127, 139))
            gate.set()
            done = service.text_progress("agent", session, 5)
            self.assertEqual((done["result"], done["typed_units"]), ("complete", 139))
            self.assertEqual([utf16_units(p["text"]) for p in channel.packets], [127, 12])
        finally:
            sessions.TEXT_WAIT = saved
            gate.set()

    def test_progress_waits_for_completion(self):
        saved = sessions.TEXT_WAIT
        sessions.TEXT_WAIT = 0.1
        gate = threading.Event()
        try:
            channel = StreamChannel()
            service, session = open_session(channel, lambda s: gate.wait(5))
            self.assertEqual(service.text("agent", session, str(uuid.uuid4()), 1, "p" * 300)["result"], "running")
            threading.Timer(0.1, gate.set).start()
            done = service.text_progress("agent", session, 5)
            self.assertEqual((done["result"], done["typed_units"]), ("complete", 300))
            with self.assertRaises(ValueError):
                service.text_progress("agent", session, 46)
        finally:
            sessions.TEXT_WAIT = saved
            gate.set()

    def test_no_stream_means_no_progress(self):
        channel = StreamChannel()
        service, session = open_session(channel)
        self.assertEqual(service.text_progress("agent", session)["result"], "none")
        self.assertEqual(service.text_cancel("agent", session)["result"], "none")


if __name__ == "__main__":
    unittest.main()
