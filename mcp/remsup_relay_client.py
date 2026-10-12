import random
import socket
import string
import base64


PROTOCOL_KEY_MASK = "LDLDDLLDLDLLDDLDLLDLDLDDLLDLDL"


def normalize_device_id(device_id: str) -> str:
    return "".join(ch for ch in device_id if ch.isdigit())


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


def open_operator_socket(relay_host: str, relay_port: int, device_id: str, otp: str, timeout: int = 15, channel: str = "COMMAND") -> socket.socket:
    clean_id = normalize_device_id(device_id)
    if not clean_id:
        raise ValueError("device_id must contain digits")
    if not otp:
        raise ValueError("otp is required")
    if channel not in ("COMMAND", "SCREEN"):
        raise ValueError("invalid-relay-channel")
    sock = socket.create_connection((relay_host, relay_port), timeout=timeout)
    suffix = " SCREEN" if channel == "SCREEN" else ""
    header = f"OPERATOR {clean_id} {otp} {make_protocol_key()}{suffix}\n"
    sock.sendall(header.encode("ascii"))
    response = recv_line(sock)
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
