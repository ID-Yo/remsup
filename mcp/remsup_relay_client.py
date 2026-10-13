import random
import socket
import string
import base64
import json
import time


PROTOCOL_KEY_MASK = "LDLDDLLDLDLLDDLDLLDLDLDDLLDLDL"
MAX_DIAGNOSTICS_BYTES = 16384
MAX_DIAGNOSTICS_LINE_BYTES = len("REMSUPDIAG1 ") + ((MAX_DIAGNOSTICS_BYTES + 2) // 3) * 4
MAX_PAIRING_LINE_BYTES = 511


class DiagnosticsError(RuntimeError):
    def __init__(self, status: str, code: str):
        self.status = status
        self.code = code
        super().__init__(code)


def normalize_device_id(device_id: str) -> str:
    return "".join(ch for ch in device_id if ch.isdigit())


def validate_diagnostics_credentials(device_id: str, otp: str) -> str:
    if not isinstance(device_id, str) or len(device_id) > 32 or not all(ch.isascii() and (ch.isdigit() or ch in " -()") for ch in device_id):
        raise ValueError("invalid-device-id")
    clean_id = normalize_device_id(device_id)
    if len(clean_id) != 9 or not clean_id.isascii() or not clean_id.isdigit():
        raise ValueError("invalid-device-id")
    if not isinstance(otp, str) or not 1 <= len(otp) <= 128 or not otp.isascii() or any(ord(ch) <= 32 or ord(ch) > 126 for ch in otp):
        raise ValueError("invalid-otp")
    return clean_id


def make_protocol_key() -> str:
    rng = random.SystemRandom()
    chars = []
    for ch in PROTOCOL_KEY_MASK:
        chars.append(rng.choice(string.digits if ch == "D" else string.ascii_letters))
    return "".join(chars)


def recv_line(sock: socket.socket) -> str:
    data = b""
    while not data.endswith(b"\n"):
        chunk = sock.recv(1)
        if not chunk:
            break
        data += chunk
    return data.decode("ascii", errors="replace").strip()


def open_operator_socket(relay_host: str, relay_port: int, device_id: str, otp: str, timeout: int = 15, channel: str = "COMMAND", bounded_handshake: bool = False) -> socket.socket:
    clean_id = normalize_device_id(device_id)
    if not clean_id:
        raise ValueError("device_id must contain digits")
    if not otp:
        raise ValueError("otp is required")
    if channel not in ("COMMAND", "SCREEN"):
        raise ValueError("invalid-relay-channel")
    if bounded_handshake:
        clean_id = validate_diagnostics_credentials(device_id, otp)
    sock = socket.create_connection((relay_host, relay_port), timeout=timeout)
    suffix = " SCREEN" if channel == "SCREEN" else ""
    header = f"OPERATOR {clean_id} {otp} {make_protocol_key()}{suffix}\n"
    sock.sendall(header.encode("ascii"))
    try:
        if bounded_handshake:
            raw_response = _recv_bounded_line(sock, MAX_PAIRING_LINE_BYTES, timeout)
            response = raw_response.decode("ascii", errors="strict")
        else:
            response = recv_line(sock)
    except Exception:
        sock.close()
        raise
    if response != "OK":
        sock.close()
        raise RuntimeError(f"relay refused session: {response}")
    return sock


def exec_command_over_relay(
    relay_host: str,
    relay_port: int,
    device_id: str,
    otp: str,
    command: str,
    timeout_seconds: int,
    max_output_bytes: int,
) -> dict:
    started_sock = open_operator_socket(relay_host, relay_port, device_id, otp)
    with started_sock:
        started_sock.settimeout(timeout_seconds + 10)
        command_b64 = base64.b64encode(command.encode("utf-8")).decode("ascii")
        header = f"REMSUP1 EXEC {timeout_seconds} {max_output_bytes} {command_b64}\n"
        started_sock.sendall(header.encode("ascii"))
        response = recv_line(started_sock)

    parts = response.split(" ", 6)
    if len(parts) >= 3 and parts[0] == "REMSUP1" and parts[1] == "ERROR":
        raise RuntimeError("client error: " + " ".join(parts[2:]))
    if len(parts) != 7 or parts[0] != "REMSUP1" or parts[1] != "RESULT":
        raise RuntimeError(f"unexpected client response: {response}")

    stdout = "" if parts[5] == "-" else base64.b64decode(parts[5].encode("ascii")).decode("utf-8", errors="replace")
    stderr = "" if parts[6] == "-" else base64.b64decode(parts[6].encode("ascii")).decode("utf-8", errors="replace")
    return {
        "device_id": normalize_device_id(device_id),
        "exit_code": int(parts[2]),
        "stdout": stdout,
        "stderr": stderr,
        "truncated": parts[3] == "1",
        "duration_ms": int(parts[4]),
    }


def _recv_bounded_line(sock: socket.socket, maximum: int, timeout: float | None = None) -> bytes:
    data = bytearray()
    deadline = time.monotonic() + timeout if timeout is not None else None
    while True:
        if deadline is not None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise socket.timeout("line read timed out")
            sock.settimeout(remaining)
        chunk = sock.recv(1)
        if not chunk:
            raise DiagnosticsError("error", "incomplete-response")
        if chunk == b"\n":
            return bytes(data)
        if len(data) >= maximum:
            raise DiagnosticsError("error", "response-too-large")
        data.extend(chunk)


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate-json-key")
        value[key] = item
    return value


def _reject_json_constant(_):
    raise ValueError("invalid-json-number")


def diagnostics_over_relay(
    relay_host: str,
    relay_port: int,
    device_id: str,
    otp: str,
    timeout_seconds: int,
) -> dict:
    """Request one bounded runtime snapshot on the authenticated COMMAND channel."""
    try:
        validate_diagnostics_credentials(device_id, otp)
        sock = open_operator_socket(relay_host, relay_port, device_id, otp, timeout=timeout_seconds, bounded_handshake=True)
    except RuntimeError as exc:
        # The relay's pairing response is untrusted and must never be returned.
        response = str(exc).removeprefix("relay refused session: ")
        if response == "ERR no-agent":
            raise DiagnosticsError("unavailable", "no-agent") from None
        if response == "ERR busy":
            raise DiagnosticsError("unavailable", "busy") from None
        if response in ("ERR blocked", "ERR bad-header", "ERR bad-channel", "ERR bad-protocol-key", "ERR bad-role"):
            raise DiagnosticsError("denied", "relay-denied") from None
        raise DiagnosticsError("error", "relay-error") from None
    except (OSError, TimeoutError):
        raise DiagnosticsError("unavailable", "transport-unavailable") from None
    except ValueError:
        raise DiagnosticsError("denied", "invalid-request") from None

    try:
        with sock:
            sock.settimeout(timeout_seconds + 10)
            sock.sendall(b"REMSUPDIAG1\n")
            raw = _recv_bounded_line(sock, MAX_DIAGNOSTICS_LINE_BYTES, timeout_seconds + 10)
    except DiagnosticsError:
        raise
    except (OSError, TimeoutError):
        raise DiagnosticsError("unavailable", "transport-unavailable") from None

    try:
        response = raw.decode("ascii", errors="strict")
    except UnicodeDecodeError:
        raise DiagnosticsError("error", "invalid-response") from None
    if response == "REMSUP1 ERROR wrong-channel":
        raise DiagnosticsError("unsupported", "wrong-channel")
    if response == "REMSUPDIAG1 ERROR unavailable":
        raise DiagnosticsError("unavailable", "unavailable")
    prefix = "REMSUPDIAG1 "
    if not response.startswith(prefix):
        raise DiagnosticsError("error", "invalid-response")
    encoded = response[len(prefix):]
    if not encoded or len(encoded) > ((MAX_DIAGNOSTICS_BYTES + 2) // 3) * 4:
        raise DiagnosticsError("error", "response-too-large" if encoded else "invalid-response")
    try:
        payload = base64.b64decode(encoded.encode("ascii"), validate=True)
        if len(payload) > MAX_DIAGNOSTICS_BYTES:
            raise DiagnosticsError("error", "response-too-large")
        if base64.b64encode(payload).decode("ascii") != encoded:
            raise ValueError("non-canonical-base64")
        document = json.loads(payload.decode("utf-8", errors="strict"), object_pairs_hook=_unique_object, parse_constant=_reject_json_constant)
    except DiagnosticsError:
        raise
    except (ValueError, UnicodeDecodeError, json.JSONDecodeError):
        raise DiagnosticsError("error", "invalid-response") from None
    if not isinstance(document, dict):
        raise DiagnosticsError("error", "invalid-response")
    return document
