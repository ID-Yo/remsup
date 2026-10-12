import json
import os
import sys
import threading
import time
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

import httpx
import uvicorn

os.environ.setdefault("REMSUP_MCP_TOKEN", "synthetic-test-token")
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))
import remsup_mcp_server as server
from remsup_screen_sessions import ScreenSessions
from remsup_config import RemsupMcpConfig
from remsup_screen_protocol import write_metadata
import io

class SyntheticStream:
    def __init__(self):
        self.buffer = bytearray()
        self.reply = io.BytesIO()
        self.closed = False
        self.session = str(uuid.uuid4())
        self.state = "Pending"
        self.deadline = time.monotonic() + 30
        self.last_io = time.monotonic()
        self.inputs = 0
        self.captures = 0
        self.last_capture = 0.0
        self.heartbeat_count = 0
        self.frame = str(uuid.uuid4())
        self.fail_mouse = False
        self.typed = ""
        self.key = ""
        self.refuse = False
    def grant(self):
        self.state = "Granted"
        self.deadline = time.monotonic() + 600
    def write(self, data):
        if self.closed or time.monotonic() - self.last_io >= 10:
            self.closed = True
            raise EOFError("synthetic Windows channel idle timeout")
        self.last_io = time.monotonic()
        self.buffer.extend(data)
        if len(self.buffer) < 4:
            return
        size = int.from_bytes(self.buffer[:4], "big")
        if len(self.buffer) < size + 4:
            return
        request = json.loads(self.buffer[4:])
        self.buffer.clear()
        self.reply = io.BytesIO()
        operation = request["operation"]
        if time.monotonic() >= self.deadline:
            self.state = "Revoked"
        if operation == "status":
            self.heartbeat_count += 1
        if operation in ("capture", "mouse", "text", "key") and self.state != "Granted":
            raise PermissionError("permission-required")
        if operation in ("mouse", "text", "key") and self.refuse:
            write_metadata(self.reply, {"version": 1, "request_id": request["request_id"], "session_id": self.session, "error": "stale-frame", "dispatched": False})
            self.reply.seek(0)
            return
        if operation == "mouse":
            self.inputs += 1
            if self.fail_mouse:
                raise EOFError("synthetic unknown input outcome")
        if operation == "text":
            self.typed += request["text"]
        if operation == "key":
            self.key = request["key"]
        if operation == "capture":
            if time.monotonic() - self.last_capture < 1:
                write_metadata(self.reply, {"version": 1, "request_id": request["request_id"], "session_id": self.session, "error": "capture-rate-limit", "dispatched": False})
                self.reply.seek(0)
                return
            self.last_capture = time.monotonic()
            from test_screen_tools import synthetic_png
            import struct
            self.captures += 1
            self.frame = str(uuid.uuid4())
            write_metadata(self.reply, {"version": 1, "request_id": request["request_id"], "session_id": self.session, "frame_id": self.frame, "monitor_id": "synthetic", "geometry_version": 1, "width": 2, "height": 2, "captured_utc": "2026-10-09T00:00:00Z", "mime_type": "image/png"})
            png = synthetic_png()
            self.reply.write(struct.pack(">I", len(png)) + png)
            self.reply.seek(0)
            return
        write_metadata(self.reply, {"version": 1, "request_id": request["request_id"], "session_id": self.session, "state": self.state, "can_view": self.state == "Granted", "can_control": self.state == "Granted", "remaining_ms": max(0, int((self.deadline-time.monotonic())*1000))})
        self.reply.seek(0)
    def read(self, size):
        return self.reply.read(size)
    def close(self):
        self.closed = True

class ScreenHttpTests(unittest.TestCase):
    def test_authenticated_stateless_http_keeps_screen_until_close_expiry_or_shutdown(self):
        import socket
        streams = []
        now = [0.0]
        def factory(*_):
            stream = SyntheticStream()
            streams.append(stream)
            return stream
        registry = ScreenSessions(factory, lambda: time.monotonic() + now[0])
        config = RemsupMcpConfig(client_tokens={"owner": "owner-token", "other": "other-token"}, relay_host="127.0.0.1", relay_port=1, audit_log_path="unused", command_timeout_seconds=30, max_output_bytes=1024, host="127.0.0.1", port=0, transport="streamable-http", public_url="http://127.0.0.1")
        mcp = server.build_mcp(config)
        for name in ("request", "status", "capture", "mouse", "text", "key", "close"):
            mcp.add_tool(getattr(server, "remsup_screen_" + name), name="remsup_screen_" + name)
        mcp.add_tool(server.remsup_exec_command)
        sock = socket.socket()
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
        runtime = uvicorn.Server(uvicorn.Config(mcp.streamable_http_app(), log_level="error", ws="none"))
        thread = threading.Thread(target=lambda: runtime.run(sockets=[sock]))
        with patch.object(server, "SCREEN", registry), patch.object(server, "append_audit"):
            thread.start()
            try:
                for _ in range(100):
                    if runtime.started:
                        break
                    time.sleep(.05)
                self.assertTrue(runtime.started)
                self.assertTrue(mcp.settings.stateless_http)
                with httpx.Client(base_url=f"http://127.0.0.1:{port}") as client:
                    def call(name, args, token="owner-token"):
                        response = client.post("/mcp", headers={"Authorization": "Bearer " + token, "Accept": "application/json, text/event-stream"}, json={"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "remsup_screen_" + name, "arguments": args}})
                        self.assertEqual(response.status_code, 200)
                        return response.json()["result"]
                    self.assertEqual(client.post("/mcp").status_code, 401)
                    listing = client.post("/mcp", headers={"Authorization": "Bearer owner-token", "Accept": "application/json, text/event-stream"}, json={"jsonrpc": "2.0", "id": 3, "method": "tools/list"}).json()["result"]["tools"]
                    tools = {tool["name"]: tool for tool in listing}
                    self.assertEqual(set(tools["remsup_exec_command"]["inputSchema"]["properties"]), {"device_id", "otp", "command"})
                    self.assertEqual(set(tools["remsup_screen_request"]["inputSchema"]["properties"]), {"device_id", "otp", "monitor_id", "control"})
                    self.assertEqual(set(tools["remsup_screen_capture"]["inputSchema"]["properties"]), {"session_id"})
                    def request():
                        result = call("request", {"device_id": "111222333", "otp": "synthetic-only", "monitor_id": "synthetic", "control": False})
                        self.assertFalse(result.get("isError", False), result)
                        return json.loads(result["content"][0]["text"])["session_id"]
                    session = request()
                    streams[-1].grant()
                    capture = call("capture", {"session_id": session})
                    self.assertFalse(capture.get("isError", False), capture)
                    limited = call("capture", {"session_id": session})
                    self.assertTrue(limited.get("isError", False), limited)
                    self.assertIn("capture-rate-limit", limited["content"][0]["text"])
                    self.assertFalse(streams[-1].closed)
                    self.assertEqual(streams[-1].captures, 1)
                    self.assertFalse(call("status", {"session_id": session}).get("isError", False))
                    before_deadline = streams[-1].deadline
                    before_captures = streams[-1].captures
                    # Real authenticated HTTP remains responsive while a blocking
                    # command backend occupies its separate bounded worker.
                    entered, release = threading.Event(), threading.Event()
                    def long_command(*_):
                        entered.set()
                        if not release.wait(12):
                            raise RuntimeError("owned command wait timed out")
                        return {"exit_code": 0, "stdout": "synthetic-long-ok", "stderr": "", "truncated": False, "duration_ms": 5000}
                    def exec_http():
                        with httpx.Client(timeout=15) as commands:
                            return commands.post(f"http://127.0.0.1:{port}/mcp", headers={"Authorization": "Bearer owner-token", "Accept": "application/json, text/event-stream"}, json={"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "remsup_exec_command", "arguments": {"device_id": "111222333", "otp": "synthetic-only", "command": "synthetic long request"}}})
                    from concurrent.futures import ThreadPoolExecutor
                    with patch.object(server, "exec_command_over_relay", side_effect=long_command), ThreadPoolExecutor(max_workers=1) as pool:
                        pending = pool.submit(exec_http)
                        try:
                            self.assertTrue(entered.wait(2), "long EXEC reached its backend")
                            started = time.monotonic()
                            self.assertFalse(call("status", {"session_id": session}).get("isError", False))
                            self.assertLess(time.monotonic() - started, 2, "EXEC must not block screen HTTP")
                            with ThreadPoolExecutor(max_workers=2) as callers:
                                owner = callers.submit(call, "status", {"session_id": session}, "owner-token")
                                other = callers.submit(call, "status", {"session_id": session}, "other-token")
                                self.assertFalse(owner.result(2).get("isError", False))
                                self.assertTrue(other.result(2).get("isError", False), "worker caller contexts must remain isolated")
                            time.sleep(4)
                            self.assertFalse(pending.done())
                            self.assertGreater(streams[-1].heartbeat_count, 1, "heartbeat runs during EXEC")
                            self.assertFalse(call("capture", {"session_id": session}).get("isError", False))
                        finally:
                            release.set()
                        result = pending.result(3).json()["result"]
                        self.assertFalse(result.get("isError", False), result)
                    before_captures = streams[-1].captures
                    time.sleep(32)  # Real wall delay greater than Windows10s/MCP30s.
                    self.assertEqual(streams[-1].deadline, before_deadline, "heartbeat cannot renew owner lease")
                    self.assertEqual(streams[-1].captures, before_captures, "no dummy capture")
                    result = call("status", {"session_id": session})
                    self.assertFalse(result.get("isError", False), result)
                    self.assertFalse(streams[-1].closed)
                    self.assertGreater(streams[-1].heartbeat_count, 3)
                    # Frame-age guard remains: deliberately capture a fresh frame
                    # after inspection before dispatching the mouse action.
                    capture = call("capture", {"session_id": session})
                    self.assertFalse(capture.get("isError", False), capture)
                    result = call("mouse", {"session_id": session, "frame_id": streams[-1].frame, "geometry_version": 1, "action": "click", "x": 0, "y": 0, "scroll_steps": 0})
                    self.assertFalse(result.get("isError", False), result)
                    self.assertEqual(streams[-1].inputs, 1)
                    reference = {"session_id": session, "frame_id": streams[-1].frame, "geometry_version": 1}
                    self.assertFalse(call("text", {**reference, "text": "Тест 😀 test1_remote.txt"}).get("isError", False))
                    self.assertEqual(streams[-1].typed, "Тест 😀 test1_remote.txt")
                    self.assertFalse(call("key", {**reference, "key": "ctrl+shift+s"}).get("isError", False))
                    self.assertEqual(streams[-1].key, "ctrl+shift+s")
                    from remsup_screen_sessions import KEYS
                    for key in KEYS:
                        self.assertFalse(call("key", {**reference, "key": key}).get("isError", False))
                        self.assertEqual(streams[-1].key, key)
                    self.assertTrue(call("key", {**reference, "key": "win+r"}).get("isError", False))
                    streams[-1].refuse = True
                    refused = call("text", {**reference, "text": "must not type"})
                    self.assertTrue(refused["isError"])
                    self.assertIn("refused-before-dispatch", refused["content"][0]["text"])
                    self.assertFalse(streams[-1].closed)
                    self.assertEqual(streams[-1].typed, "Тест 😀 test1_remote.txt")
                    streams[-1].refuse = False
                    self.assertTrue(call("key", {**reference, "key": "win+r"})["isError"])
                    self.assertTrue(call("text", {**reference, "text": "x" * 129})["isError"])
                    self.assertTrue(call("status", {"session_id": session}, "other-token")["isError"])
                    self.assertFalse(streams[-1].closed)
                    self.assertFalse(call("close", {"session_id": session}).get("isError", False))
                    self.assertTrue(streams[-1].closed)
                    for revoke in ("Stop", "expiry"):
                        session = request()
                        streams[-1].grant()
                        self.assertFalse(call("status", {"session_id": session}).get("isError", False))
                        if revoke == "Stop":
                            streams[-1].state = "Revoked"
                        else:
                            streams[-1].deadline = time.monotonic() - 1
                        for _ in range(100):
                            if streams[-1].closed:
                                break
                            time.sleep(.05)
                        self.assertTrue(streams[-1].closed, revoke)
                        self.assertTrue(call("status", {"session_id": session})["isError"])
                    session = request()
                    streams[-1].grant()
                    self.assertFalse(call("status", {"session_id": session}).get("isError", False))
                    streams[-1].fail_mouse = True
                    fields = {"session_id": session, "frame_id": streams[-1].frame, "geometry_version": 1, "action": "click", "x": 0, "y": 0, "scroll_steps": 0}
                    self.assertTrue(call("mouse", fields)["isError"])
                    self.assertTrue(call("mouse", fields)["isError"])
                    self.assertEqual(streams[-1].inputs, 1, "unknown input outcome never retried")
                    closed=call('close',{'session_id':session})
                    self.assertFalse(closed.get('isError',False))
                    self.assertIn('already-closed',closed['content'][0]['text'])
                    session = request()
                    now[0] += 34
                    for _ in range(40):
                        if streams[-1].closed:
                            break
                        time.sleep(.05)
                    self.assertTrue(streams[-1].closed, "application cleanup must preserve pending expiry")
                    self.assertTrue(call("status", {"session_id": session})["isError"])
                    session = request()
                    self.assertFalse(streams[-1].closed)
            finally:
                runtime.should_exit = True
                thread.join(10)
                sock.close()
            self.assertFalse(thread.is_alive())
            self.assertTrue(all(stream.closed for stream in streams))
            self.assertTrue(streams[-1].closed, "application shutdown closes active session")
            self.assertEqual(registry._sessions, {})
