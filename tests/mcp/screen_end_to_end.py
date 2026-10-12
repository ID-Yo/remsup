"""Run only under the owned C# loopback fixture; no real device or desktop."""
import asyncio
import base64
import json
import sys
import time
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mcp"))
import remsup_mcp_server as server

output = Path(sys.argv[2])


def call(name, args, caller):
    with patch.object(server, "screen_caller", return_value=caller):
        result = asyncio.run(server.MCP.call_tool("remsup_screen_" + name, args))
    if name == "capture":
        blocks = result[0] if isinstance(result, tuple) else result
        return next(block for block in blocks if block.type == "image")
    if isinstance(result, tuple):
        return result[1] if result[1] is not None else json.loads(result[0][0].text)
    if isinstance(result, dict):
        return result
    return json.loads(result[0].text)


def request(caller, control):
    return call("request", {"device_id": "111222333", "otp": "synthetic-only", "monitor_id": "synthetic", "control": control}, caller)["session_id"]


def granted(session, caller):
    for _ in range(30):
        state = call("status", {"session_id": session}, caller)
        if state["state"] == "Granted":
            return state
        time.sleep(0.1)
    raise AssertionError("Local consent not observed")


session = request("synthetic-no-consent", True)
try:
    call("capture", {"session_id": session}, "synthetic-no-consent")
    raise AssertionError("Capture without consent")
except Exception as exc:
    if isinstance(exc, AssertionError):
        raise
time.sleep(1.5)
session = request("synthetic-view", True)
state = granted(session, "synthetic-view")
assert state["can_view"] and not state["can_control"]
image = call("capture", {"session_id": session}, "synthetic-view")
output.joinpath("view-only.png").write_bytes(base64.b64decode(image.data))
call("close", {"session_id": session}, "synthetic-view")
time.sleep(1.5)
session = request("synthetic-control", True)
state = granted(session, "synthetic-control")
assert state["can_control"]
started = time.perf_counter()
image = call("capture", {"session_id": session}, "synthetic-control")
latency = time.perf_counter() - started
first = base64.b64decode(image.data)
output.joinpath("before.png").write_bytes(first)
# Inspect the image for eight seconds; keep only the existing lease alive.
for _ in range(8):
    time.sleep(1)
    call("status", {"session_id": session}, "synthetic-control")
entry = server.SCREEN._sessions[session]
# Read metadata from a fresh SDK capture to obtain its exact frame reference,
# then retain it across the real eight-second inspection interval below.
with patch.object(server, "screen_caller", return_value="synthetic-control"):
    content = server.remsup_screen_capture(session)
metadata = json.loads(content[0].text)
for _ in range(8):
    time.sleep(1)
    call("status", {"session_id": session}, "synthetic-control")
call("mouse", {"session_id": session, "frame_id": metadata["frame_id"], "geometry_version": metadata["geometry_version"], "action": "click", "x": 320, "y": 175, "scroll_steps": 0}, "synthetic-control")
time.sleep(1.1)
image = call("capture", {"session_id": session}, "synthetic-control")
second = base64.b64decode(image.data)
assert second != first
output.joinpath("after.png").write_bytes(second)
elapsed = time.perf_counter() - started
call("close", {"session_id": session}, "synthetic-control")
time.sleep(1.5)
session = request("synthetic-stop", False)
granted(session, "synthetic-stop")
time.sleep(2.5)
try:
    call("capture", {"session_id": session}, "synthetic-stop")
    raise AssertionError("Capture after local Stop")
except Exception as exc:
    if isinstance(exc, AssertionError):
        raise
server.SCREEN.close_all()
print(json.dumps({"sdk_capture_latency_seconds": round(latency, 3), "frame_inspection_seconds": 8, "cycle_seconds": round(elapsed, 3), "changed_capture": True, "default_off_and_no_consent_rejected": True, "view_only_and_local_stop_verified": True, "synthetic_owned_visual_only": True}))
