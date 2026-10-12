"""Explicit controlled-test plaintext screen transport; requires remote opt-in/consent."""
from remsup_relay_client import open_operator_socket


class ScreenSocketStream:
    def __init__(self, sock):
        self.sock = sock

    def read(self, size):
        return self.sock.recv(size)

    def write(self, data):
        self.sock.sendall(data)

    def close(self):
        self.sock.close()


def connect_screen(relay_host, relay_port, device_id, otp):
    clean_id = "".join(c for c in device_id if "0" <= c <= "9")
    if len(clean_id) != 9 or not otp or len(otp) > 128 or any(ord(c) <= 32 or ord(c) > 126 for c in otp):
        raise ValueError("invalid-screen-pairing-input")
    sock = open_operator_socket(relay_host, relay_port, clean_id, otp, channel="SCREEN")
    try:
        sock.settimeout(10)
        sock.sendall(b"REMSUPSCREEN1\n")
        response = bytearray()
        while not response.endswith(b"\n"):
            data = sock.recv(1)
            if not data or len(response) >= 512:
                raise RuntimeError("screen-handshake-refused")
            response.extend(data)
        if bytes(response) != b"REMSUPSCREEN1 OK\n":
            raise RuntimeError("remote-screen-test-mode-disabled-or-unsupported")
        return ScreenSocketStream(sock)
    except Exception:
        sock.close()
        raise
