import sys
import unittest
from pathlib import Path


import os
import base64
from unittest.mock import patch


os.environ.setdefault("REMSUP_MCP_TOKEN", "test-token")
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))

import remsup_relay_client


class FakeSocket:
    def __init__(self, response):
        self.response = bytearray(response.encode("ascii"))
        self.sent = b""
        self.closed = False

    def sendall(self, data):
        self.sent += data

    def recv(self, size):
        if not self.response:
            return b""
        chunk = self.response[:size]
        del self.response[:size]
        return bytes(chunk)

    def close(self):
        self.closed = True

    def settimeout(self, value):
        self.timeout = value

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        self.closed = True


class CommandPolicyTests(unittest.TestCase):
    def test_command_and_screen_registration_are_explicit_and_never_fallback(self):
        for channel, suffix in (("COMMAND", b""), ("SCREEN", b" SCREEN")):
            fake = FakeSocket("OK\n")
            with patch.object(remsup_relay_client.socket, "create_connection", return_value=fake):
                remsup_relay_client.open_operator_socket("relay", 8443, "111222333", "synthetic-only", channel=channel)
            fields = fake.sent.rstrip(b"\n").split(b" ")
            self.assertEqual(len(fields), 4 if channel == "COMMAND" else 5)
            self.assertTrue(fake.sent.endswith(suffix + b"\n"))
        with patch.object(remsup_relay_client.socket, "create_connection") as connect:
            with self.assertRaisesRegex(ValueError, "invalid-relay-channel"):
                remsup_relay_client.open_operator_socket("relay", 8443, "111222333", "synthetic-only", channel="OTHER")
            connect.assert_not_called()

    def test_exec_command_over_relay_parses_result(self):
        stdout = base64.b64encode(b"DESKTOP\r\n").decode("ascii")
        fake = FakeSocket(f"REMSUP1 RESULT 0 0 42 {stdout} -\n")
        original = remsup_relay_client.open_operator_socket
        remsup_relay_client.open_operator_socket = lambda *args, **kwargs: fake
        try:
            result = remsup_relay_client.exec_command_over_relay("relay", 8443, "345 345 345", "otp", "hostname", 30, 262144)
        finally:
            remsup_relay_client.open_operator_socket = original

        self.assertIn(b"REMSUP1 EXEC 30 262144 ", fake.sent)
        self.assertEqual(fake.timeout, 40)
        self.assertEqual(result["device_id"], "345345345")
        self.assertEqual(result["exit_code"], 0)
        self.assertEqual(result["stdout"], "DESKTOP\r\n")
        self.assertEqual(result["stderr"], "")
        self.assertFalse(result["truncated"])


if __name__ == "__main__":
    unittest.main()
