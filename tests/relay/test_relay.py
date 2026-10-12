"""Ordinary relay lifecycle checks; synthetic sockets only, no device commands.

Run in an isolated network namespace on a shared host. --binary uses the exact
compiled candidate; otherwise gcc builds current source in a temporary folder.
"""
import argparse
import pathlib
import socket
import subprocess
import tempfile
import time
import unittest

KEY = "".join("1" if item == "D" else "A" for item in "LDLDDLLDLDLLDDLDLLDLDLDDLLDLDL")
PARSER = argparse.ArgumentParser()
PARSER.add_argument("--binary")
ARGS, REST = PARSER.parse_known_args()


def read_line(peer):
    data = bytearray()
    while not data.endswith(b"\n"):
        chunk = peer.recv(1)
        if not chunk:
            break
        data.extend(chunk)
    return bytes(data)


def receive(peer, size):
    data = bytearray()
    while len(data) < size:
        chunk = peer.recv(size - len(data))
        if not chunk:
            break
        data.extend(chunk)
    return bytes(data)


class RelayLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="remsup-relay-tests-")
        cls.binary = ARGS.binary or str(pathlib.Path(cls.temp.name) / "relay")
        if not ARGS.binary:
            source = pathlib.Path(__file__).resolve().parents[2] / "relay" / "remsup-relay.c"
            subprocess.run(["gcc", "-O2", "-pthread", "-Wall", "-Wextra", "-Werror", "-o", cls.binary, str(source)], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def setUp(self):
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            self.port = reserved.getsockname()[1]
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            self.proxy_port = reserved.getsockname()[1]
        self.log = tempfile.TemporaryFile()
        self.process = subprocess.Popen([self.binary, str(self.port), "0.0.0.0", str(self.proxy_port)], stdout=self.log, stderr=self.log)
        self.peers = []
        deadline = time.monotonic() + 3
        while True:
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=.1):
                    break
            except OSError:
                if time.monotonic() > deadline:
                    self.fail("Owned relay did not start")
                time.sleep(.02)

    def tearDown(self):
        for peer in self.peers:
            peer.close()
        if self.process.poll() is None:
            self.process.terminate()
        self.process.wait(timeout=3)
        self.log.close()

    def connect(self, role, device="111222333", password="synthetic-only", channel="COMMAND"):
        peer = socket.create_connection(("127.0.0.1", self.port), timeout=3)
        self.peers.append(peer)
        suffix = " SCREEN" if channel == "SCREEN" else "" if channel == "COMMAND" else f" {channel}"
        peer.sendall(f"{role} {device} {password} {KEY}{suffix}\n".encode("ascii"))
        return peer

    def connect_agent(self, device="111222333", password="synthetic-only", channel="COMMAND"):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            peer = self.connect("AGENT", device, password, channel)
            response = read_line(peer)
            if response == b"WAIT\n":
                return peer
            peer.close()
            if response != b"ERR busy\n":
                self.fail(f"unexpected agent registration response: {response!r}")
            time.sleep(.02)
        self.fail("relay did not release the prior channel reservation")

    def pair(self, agent, device="111222333", password="synthetic-only", channel="COMMAND"):
        operator = self.connect("OPERATOR", device, password, channel)
        self.assertEqual(read_line(operator), b"OK\n")
        self.assertEqual(read_line(agent), b"OK\n")
        request = b"synthetic request\n"
        response = b"synthetic response\n"
        operator.sendall(request)
        self.assertEqual(receive(agent, len(request)), request)
        agent.sendall(response)
        self.assertEqual(receive(operator, len(response)), response)
        agent.shutdown(socket.SHUT_RDWR)
        agent.close()
        self.assertEqual(operator.recv(1), b"")
        operator.close()
        self.assertIsNone(self.process.poll(), "Relay died after ordinary pairing")

    def proxy(self, source="198.51.100.1", header=None):
        peer = socket.create_connection(("127.0.0.1", self.proxy_port), timeout=3)
        self.peers.append(peer)
        if header is None:
            header = f"PROXY TCP4 {source} 192.0.2.1 50000 443\r\n"
        peer.sendall(header.encode("ascii"))
        return peer

    def test_proxy_registration_pairs_with_plain_mcp_listener(self):
        for channel in ("COMMAND", "SCREEN"):
            agent = self.proxy()
            suffix = " SCREEN" if channel == "SCREEN" else ""
            agent.sendall(f"AGENT 111222333 synthetic-only {KEY}{suffix}\n".encode())
            self.assertEqual(read_line(agent), b"WAIT\n")
            self.pair(agent, channel=channel)

    def test_proxy_ban_is_scoped_to_real_ip(self):
        for _ in range(4):
            bad = self.proxy()
            bad.sendall(b"bad-header\n")
            self.assertEqual(read_line(bad), b"ERR bad-header\n")
        blocked = self.proxy()
        self.assertEqual(read_line(blocked), b"ERR blocked\n")
        other = self.proxy("198.51.100.2")
        other.sendall(f"OPERATOR 111222333 synthetic-only {KEY}\n".encode())
        self.assertEqual(read_line(other), b"ERR no-agent\n")
        mcp = self.connect("OPERATOR")
        self.assertEqual(read_line(mcp), b"ERR no-agent\n")

    def test_proxy_requires_valid_trusted_header(self):
        for header in (f"AGENT 111222333 synthetic-only {KEY}\n", "PROXY UNKNOWN\n", "PROXY TCP4 wrong 192.0.2.1 3 443\n", "PROXY TCP4 198.51.100.1 192.0.2.1 3 443 EXTRA\n"):
            peer = self.proxy(header=header)
            self.assertEqual(peer.recv(1), b"")
        peer = socket.socket()
        self.peers.append(peer)
        peer.settimeout(3)
        peer.bind(("127.0.0.2", 0))
        peer.connect(("127.0.0.1", self.proxy_port))
        self.assertEqual(peer.recv(1), b"")
        mcp = self.connect("OPERATOR")
        self.assertEqual(read_line(mcp), b"ERR no-agent\n")

    def test_100_sequential_pairings_and_reregistrations(self):
        for _ in range(100):
            agent = self.connect_agent()
            self.pair(agent)

    def test_disconnected_waiting_agent_is_not_paired(self):
        agent = self.connect("AGENT")
        self.assertEqual(read_line(agent), b"WAIT\n")
        agent.shutdown(socket.SHUT_RDWR)
        agent.close()
        time.sleep(1.2)
        operator = self.connect("OPERATOR")
        self.assertEqual(read_line(operator), b"ERR no-agent\n")
        self.assertEqual(operator.recv(1), b"")
        self.assertIsNone(self.process.poll())

    def test_dead_duplicate_does_not_hide_fresh_registration(self):
        fresh = self.connect_agent()
        stale = self.connect("AGENT")
        self.assertEqual(read_line(stale), b"ERR busy\n")
        stale.shutdown(socket.SHUT_RDWR)
        stale.close()
        self.pair(fresh)
        replacement = self.connect_agent()
        self.pair(replacement)

    def test_wrong_password_does_not_remove_waiting_agent(self):
        agent = self.connect_agent()
        wrong = self.connect("OPERATOR", password="wrong-synthetic")
        self.assertEqual(read_line(wrong), b"ERR no-agent\n")
        self.pair(agent)

    def test_unknown_and_extra_registration_fields_are_rejected(self):
        for suffix, expected in ((" UNKNOWN", b"ERR bad-channel\n"), (" SCREEN EXTRA", b"ERR bad-header\n")):
            peer = socket.create_connection(("127.0.0.1", self.port), timeout=3)
            self.peers.append(peer)
            peer.sendall(f"AGENT 111222333 synthetic-only {KEY}{suffix}\n".encode("ascii"))
            self.assertEqual(read_line(peer), expected)
            self.assertEqual(peer.recv(1), b"")

    def test_command_and_screen_reservations_are_independent(self):
        command = self.connect_agent()

        wrong_route = self.connect("OPERATOR", channel="SCREEN")
        self.assertEqual(read_line(wrong_route), b"ERR no-agent\n")
        self.assertEqual(wrong_route.recv(1), b"")

        screen = self.connect_agent(channel="SCREEN")

        screen_operator = self.connect("OPERATOR", channel="SCREEN")
        self.assertEqual(read_line(screen_operator), b"OK\n")
        self.assertEqual(read_line(screen), b"OK\n")
        request = b"REMSUPSCREEN1\n"
        screen_operator.sendall(request)
        self.assertEqual(receive(screen, len(request)), request)

        duplicate_screen = self.connect("AGENT", channel="SCREEN")
        self.assertEqual(read_line(duplicate_screen), b"ERR busy\n")
        self.assertEqual(duplicate_screen.recv(1), b"")

        command_operator = self.connect("OPERATOR")
        self.assertEqual(read_line(command_operator), b"OK\n")
        self.assertEqual(read_line(command), b"OK\n")
        command_operator.sendall(b"REMSUP1 EXEC test\n")
        self.assertEqual(receive(command, len(b"REMSUP1 EXEC test\n")), b"REMSUP1 EXEC test\n")

        command_operator.shutdown(socket.SHUT_RDWR)
        command_operator.close()
        screen_operator.sendall(b"screen-still-open\n")
        self.assertEqual(receive(screen, len(b"screen-still-open\n")), b"screen-still-open\n")
        screen_operator.shutdown(socket.SHUT_RDWR)
        screen_operator.close()
        self.assertEqual(command.recv(1), b"")
        self.assertEqual(screen.recv(1), b"")

        replacement = self.connect_agent(channel="SCREEN")
        self.pair(replacement, channel="SCREEN")

    def test_active_same_channel_operator_is_busy_until_bridge_finishes(self):
        agent = self.connect_agent()
        operator = self.connect("OPERATOR")
        self.assertEqual(read_line(operator), b"OK\n")
        self.assertEqual(read_line(agent), b"OK\n")

        duplicate = self.connect("OPERATOR")
        self.assertEqual(read_line(duplicate), b"ERR busy\n")
        self.assertEqual(duplicate.recv(1), b"")

        operator.shutdown(socket.SHUT_RDWR)
        operator.close()
        self.assertEqual(agent.recv(1), b"")
        replacement = None
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            probe = self.connect("AGENT")
            response = read_line(probe)
            if response == b"WAIT\n":
                replacement = probe
                break
            probe.close()
            time.sleep(.02)
        self.assertIsNotNone(replacement, "active reservation released after bridge teardown")
        self.pair(replacement)


if __name__ == "__main__":
    unittest.main(argv=[__file__, *REST])
