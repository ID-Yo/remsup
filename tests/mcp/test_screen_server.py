import asyncio
import base64
import json
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch
from mcp.types import ImageContent, TextContent

os.environ.setdefault("REMSUP_MCP_TOKEN", "synthetic-test-token")
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))
import remsup_mcp_server as server


class ScreenServerTests(unittest.TestCase):
    def test_drop_audit_does_not_echo_unknown_reason(self):
        events=[]
        with patch.object(server,'append_audit',side_effect=events.append):
            server.audit_screen_drop('synthetic-agent','invalid-screen-state')
            server.audit_screen_drop('synthetic-agent','PRIVATE_SENTINEL')
        self.assertEqual([x['result_code'] for x in events],['invalid-screen-state','refused-or-failed']);self.assertNotIn('PRIVATE_SENTINEL',str(events));self.assertNotIn('synthetic-agent',str(events))

    def test_unknown_and_unavailable_audit_are_distinct(self):
        events=[]
        with patch.object(server,'screen_caller',return_value='synthetic-agent'),patch.object(server,'append_audit',side_effect=events.append):
            for message in ('screen-input-outcome-unknown; do not retry automatically','screen-session-unavailable'):
                def failed(_):raise RuntimeError(message)
                with self.assertRaises(RuntimeError):server.screen_call('mouse',failed)
        self.assertEqual([x['result_code'] for x in events],['outcome-unknown','screen-session-unavailable'])

    def test_refusal_audit_records_only_allowlisted_code(self):
        from remsup_screen_sessions import ConfirmedInputRefusal
        events=[]
        def refused(_):raise ConfirmedInputRefusal('protected-window',False)
        with patch.object(server,'screen_caller',return_value='synthetic-agent'),patch.object(server,'append_audit',side_effect=events.append):
            with self.assertRaises(ConfirmedInputRefusal):server.screen_call('mouse',refused)
        self.assertEqual(events[0]['result_code'],'protected-window')
        self.assertNotIn('synthetic-agent',str(events));self.assertNotIn('coordinate',str(events))
    def test_lifespan_closes_registry_without_leftover_cleanup_task(self):
        async def exercise():
            before = set(asyncio.all_tasks())
            with patch.object(server.SCREEN, "close_all") as close:
                async with server.screen_lifespan(None):
                    self.assertGreater(len(asyncio.all_tasks()), len(before))
                close.assert_called_once()
            self.assertEqual(set(asyncio.all_tasks()), before)
        asyncio.run(exercise())
    def test_registered_capture_delivers_image_content_in_memory(self):
        from test_screen_tools import synthetic_png
        image = synthetic_png()
        class SyntheticBackend:
            def operation(self, caller, session_id, operation):
                self_called = (caller, session_id, operation)
                if self_called != ("synthetic-agent", "synthetic-session", "capture"):
                    raise AssertionError("Unexpected synthetic invocation")
                return [TextContent(type="text", text=json.dumps({"monitor_id": "synthetic", "width": 2, "height": 2})), ImageContent(type="image", mimeType="image/png", data=base64.b64encode(image).decode("ascii"))]
        with patch.object(server, "SCREEN", SyntheticBackend()), patch.object(server, "screen_caller", return_value="synthetic-agent"), patch.object(server, "append_audit"):
            result = asyncio.run(server.MCP.call_tool("remsup_screen_capture", {"session_id": "synthetic-session"}))
        # FastMCP must preserve image content, rather than stringify a model/file path.
        blocks = result[0] if isinstance(result, tuple) else result
        self.assertEqual(blocks[1].type, "image")
        self.assertEqual(base64.b64decode(blocks[1].data), image)

    def test_tools_registered_and_screen_auth_required(self):
        names = {tool.name for tool in asyncio.run(server.MCP.list_tools())}
        for name in ("capabilities", "request", "status", "capture", "mouse", "text", "key", "close"):
            self.assertIn("remsup_screen_" + name, names)
        with patch.object(server, "get_access_token", return_value=None):
            with self.assertRaises(PermissionError):
                server.remsup_screen_capabilities()

    def test_production_screen_refused_before_connection_and_audit_redacted(self):
        events = []
        with patch.object(server, "screen_caller", return_value="synthetic-agent"), patch.object(server, "append_audit", side_effect=events.append):
            self.assertFalse(server.remsup_screen_capabilities()["available"])
            with self.assertRaisesRegex(RuntimeError, "screen-test-mode-disabled"):
                server.remsup_screen_request("PRIVATE_DEVICE_SENTINEL", "PRIVATE_PASSWORD_SENTINEL", "PRIVATE_MONITOR_SENTINEL", True)
        self.assertEqual(len(events), 2)
        self.assertEqual(set(events[1]), {"tool", "caller", "result_code", "duration_ms"})
        self.assertNotIn("PRIVATE", str(events))
        self.assertNotIn("synthetic-agent", str(events))

    def test_existing_bearer_verifier_still_rejects_unknown_token(self):
        verifier = server.StaticTokenVerifier({"synthetic-agent": "synthetic-only"})
        self.assertIsNone(asyncio.run(verifier.verify_token("unknown")))
        token = asyncio.run(verifier.verify_token("synthetic-only"))
        self.assertEqual(token.client_id, "synthetic-agent")
        self.assertEqual(token.scopes, ["remsup:exec"])


if __name__ == "__main__":
    unittest.main()
