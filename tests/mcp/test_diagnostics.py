import base64
import json
import os
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import patch

os.environ.setdefault("REMSUP_MCP_TOKEN", "synthetic-test-token")
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))
import remsup_mcp_server as server
import remsup_relay_client as relay


def sample_diagnostics():
    return {
        "schema_version": 1,
        "status": "ok",
        "version": "v1.48",
        "lan": {"configured": True, "enabled": True, "ready": False, "warning": "private warning"},
        "relay": {"state": "RelayWaiting", "ready": True},
        "updates": {
            "enabled": True,
            "last_check": {"utc": "2026-10-12T12:00:00Z", "result": "Current", "version": "v1.48", "error_code": ""},
            "last_download": {"utc": "2026-10-12T12:00:00Z", "version": "v1.48", "sha256": "a" * 64},
            "last_install": {"utc": "2026-10-12T12:00:00Z", "result": "Installed", "error_code": ""},
            "latest_errors": [{"utc": "2026-10-12T12:00:00Z", "code": "check-failed"}],
        },
    }


class FakeSocket:
    def __init__(self, response):
        self.response = bytearray(response if isinstance(response, bytes) else response.encode("ascii"))
        self.sent = bytearray()
        self.closed = False
        self.timeout = None

    def sendall(self, data):
        self.sent.extend(data)

    def recv(self, size):
        if not self.response:
            return b""
        chunk = self.response[:size]
        del self.response[:size]
        return bytes(chunk)

    def settimeout(self, value):
        self.timeout = value

    def close(self):
        self.closed = True

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


class DiagnosticsTests(unittest.TestCase):
    def test_diagnostics_uses_only_exact_command_request_and_projects_safe_schema(self):
        payload = sample_diagnostics()
        payload["device_id"] = "DEVICE_SENTINEL"
        payload["otp"] = "PASSWORD_SENTINEL"
        payload["raw_error"] = "TOKEN_SENTINEL"
        encoded = base64.b64encode(json.dumps(payload).encode("utf-8")).decode("ascii")
        fake = FakeSocket("REMSUPDIAG1 " + encoded + "\n")
        with patch.object(relay, "open_operator_socket", return_value=fake) as opened, \
             patch.object(server, "screen_caller", return_value="synthetic-agent"), \
             patch.object(server, "diagnostics_over_relay", wraps=server.diagnostics_over_relay), \
             patch.object(server, "append_audit") as audit:
            result = server.remsup_diagnostics("123 456 789", "PASSWORD_SENTINEL")
        self.assertEqual(fake.sent, b"REMSUPDIAG1\n")
        self.assertEqual(opened.call_args.args[:3], (server.CONFIG.relay_host, server.CONFIG.relay_port, "123456789"))
        self.assertTrue(opened.call_args.kwargs["bounded_handshake"])
        self.assertNotIn("DEVICE_SENTINEL", json.dumps(result))
        self.assertNotIn("PASSWORD_SENTINEL", json.dumps(result))
        self.assertNotIn("TOKEN_SENTINEL", json.dumps(result))
        self.assertEqual(set(result), {"schema_version", "status", "version", "lan", "relay", "updates"})
        self.assertEqual(result["lan"]["warning"], "LAN diagnostics unavailable.")
        self.assertEqual(audit.call_count, 1)
        event = audit.call_args.args[0]
        self.assertEqual(set(event), {"tool", "caller", "result_code", "duration_ms"})
        self.assertEqual(event["caller"], __import__("hashlib").sha256(b"synthetic-agent").hexdigest()[:12])
        self.assertNotIn("123456789", str(event))
        self.assertNotIn("PASSWORD_SENTINEL", str(event))

    def test_legacy_receiver_and_missing_provider_return_controlled_statuses(self):
        for wire, expected in (("REMSUP1 ERROR wrong-channel\n", ("unsupported", "wrong-channel")),
                               ("REMSUPDIAG1 ERROR unavailable\n", ("unavailable", "unavailable"))):
            fake = FakeSocket(wire)
            with patch.object(relay, "open_operator_socket", return_value=fake):
                with self.assertRaises(relay.DiagnosticsError) as caught:
                    relay.diagnostics_over_relay("relay", 443, "123456789", "synthetic-only", 5)
            self.assertEqual((caught.exception.status, caught.exception.code), expected)
            self.assertEqual(fake.sent, b"REMSUPDIAG1\n")

    def test_oversized_decoded_response_is_rejected_before_json_processing(self):
        encoded = base64.b64encode(b"x" * (relay.MAX_DIAGNOSTICS_BYTES + 1)).decode("ascii")
        fake = FakeSocket("REMSUPDIAG1 " + encoded + "\n")
        with patch.object(relay, "open_operator_socket", return_value=fake):
            with self.assertRaises(relay.DiagnosticsError) as caught:
                relay.diagnostics_over_relay("relay", 443, "123456789", "synthetic-only", 5)
        self.assertEqual(caught.exception.status, "error")
        self.assertEqual(caught.exception.code, "response-too-large")

    def test_decoded_response_at_exact_limit_is_accepted(self):
        document = sample_diagnostics()
        payload = json.dumps(document, separators=(",", ":")).encode("utf-8")
        payload += b" " * (relay.MAX_DIAGNOSTICS_BYTES - len(payload))
        encoded = base64.b64encode(payload).decode("ascii")
        fake = FakeSocket("REMSUPDIAG1 " + encoded + "\n")
        with patch.object(relay, "open_operator_socket", return_value=fake):
            result = relay.diagnostics_over_relay("relay", 443, "123456789", "synthetic-only", 5)
        self.assertEqual(result["version"], "v1.48")

    def test_registered_diagnostics_tool_has_exact_arguments_and_requires_auth(self):
        names = {tool.name: tool for tool in __import__("asyncio").run(server.MCP.list_tools())}
        self.assertIn("remsup_diagnostics", names)
        self.assertEqual(set(names["remsup_diagnostics"].inputSchema["properties"]), {"device_id", "otp"})
        with patch.object(server, "get_access_token", return_value=None), \
             patch.object(server, "diagnostics_over_relay") as remote:
            with self.assertRaises(PermissionError):
                server.remsup_diagnostics("123456789", "synthetic-only")
        remote.assert_not_called()

    def test_diagnostics_pairing_header_is_bounded_and_credentials_are_printable(self):
        fake = FakeSocket("OK\n")
        with patch.object(relay.socket, "create_connection", return_value=fake):
            sock = relay.open_operator_socket("relay", 443, "123 456 789", "aB123", timeout=5, bounded_handshake=True)
        self.assertEqual(sock, fake)
        for device_id, otp in (("１２３４５６７８９", "password"), ("12345678", "password"), ("123456789", "bad password"), ("123456789", "x" * 129)):
            with patch.object(relay.socket, "create_connection") as connect:
                with self.assertRaises(ValueError):
                    relay.open_operator_socket("relay", 443, device_id, otp, timeout=5, bounded_handshake=True)
                connect.assert_not_called()

    def test_mcp_rejects_invalid_diagnostics_credentials_before_network(self):
        for device_id, otp in (("１２３４５６７８９", "password"), ("12345678", "password"), ("123456789", "bad password")):
            with patch.object(server, "screen_caller", return_value="synthetic-agent"), \
                 patch.object(server, "diagnostics_over_relay") as remote, \
                 patch.object(server, "append_audit"):
                result = server.remsup_diagnostics(device_id, otp)
            self.assertEqual(result, {"status": "denied", "error_code": "invalid-request"})
            remote.assert_not_called()

    def test_diagnostics_pairing_response_is_bounded(self):
        fake = FakeSocket(b"X" * (relay.MAX_PAIRING_LINE_BYTES + 2))
        with patch.object(relay.socket, "create_connection", return_value=fake):
            with self.assertRaises(relay.DiagnosticsError) as caught:
                relay.open_operator_socket("relay", 443, "123456789", "password", timeout=5, bounded_handshake=True)
        self.assertEqual(caught.exception.code, "response-too-large")
        self.assertTrue(fake.closed)

    def test_bounded_line_uses_one_total_deadline_for_slow_drip(self):
        class DrippingSocket(FakeSocket):
            def recv(self, size):
                time.sleep(0.02)
                return super().recv(size)

        started = time.monotonic()
        with self.assertRaises(TimeoutError):
            relay._recv_bounded_line(DrippingSocket(b"OK\n"), 16, timeout=0.01)
        self.assertLess(time.monotonic() - started, 0.2)

    def test_diagnostics_tool_failure_status_schema_and_audit_are_allowlisted(self):
        events = []
        with patch.object(server, "screen_caller", return_value="synthetic-agent"), \
             patch.object(server, "diagnostics_over_relay", side_effect=relay.DiagnosticsError("unsupported", "wrong-channel")), \
             patch.object(server, "append_audit", side_effect=events.append):
            result = server.remsup_diagnostics("123456789", "synthetic-only")
        self.assertEqual(result, {"status": "unsupported", "error_code": "wrong-channel"})
        self.assertEqual(events[0]["result_code"], "wrong-channel")
        self.assertEqual(set(events[0]), {"tool", "caller", "result_code", "duration_ms"})

    def test_malformed_response_and_raw_transport_error_never_escape_tool(self):
        events = []
        with patch.object(server, "screen_caller", return_value="synthetic-agent"), \
             patch.object(server, "diagnostics_over_relay", side_effect=RuntimeError("PASSWORD_SENTINEL TOKEN_SENTINEL")), \
             patch.object(server, "append_audit", side_effect=events.append):
            result = server.remsup_diagnostics("123456789", "PASSWORD_SENTINEL")
        self.assertEqual(result, {"status": "error", "error_code": "diagnostics-unavailable"})
        self.assertNotIn("PASSWORD_SENTINEL", str(result))
        self.assertNotIn("TOKEN_SENTINEL", str(events))

    def test_authenticated_caller_is_required_before_any_remote_or_shell_call(self):
        with patch.object(server, "screen_caller", side_effect=PermissionError("authenticated-caller-required")), \
             patch.object(server, "diagnostics_over_relay") as diagnostics, \
             patch.object(server, "exec_command_over_relay") as execute:
            with self.assertRaises(PermissionError):
                server.remsup_diagnostics("123456789", "synthetic-only")
        diagnostics.assert_not_called()
        execute.assert_not_called()

    def test_invalid_known_field_is_rejected_and_unknown_nested_fields_are_dropped(self):
        payload = sample_diagnostics()
        payload["updates"]["last_install"]["raw_error"] = "PASSWORD_SENTINEL"
        payload["updates"]["last_check"]["result"] = "PASSWORD_SENTINEL"
        with patch.object(server, "screen_caller", return_value="synthetic-agent"), \
             patch.object(server, "diagnostics_over_relay", return_value=payload), \
             patch.object(server, "append_audit"):
            result = server.remsup_diagnostics("123456789", "synthetic-only")
        self.assertEqual(result, {"status": "error", "error_code": "diagnostics-unavailable"})
        self.assertNotIn("PASSWORD_SENTINEL", str(result))

    def test_error_check_with_empty_version_is_projected_as_null(self):
        payload = sample_diagnostics()
        payload["updates"]["last_check"].update(result="Error", version="", error_code="check-failed")
        result = server.project_diagnostics(payload)
        self.assertEqual(result["updates"]["last_check"]["version"], None)


if __name__ == "__main__":
    unittest.main()
