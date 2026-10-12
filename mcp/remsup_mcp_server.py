import json
import os
import time
import hashlib
import asyncio
import inspect
from functools import partial, wraps
import anyio
from contextlib import asynccontextmanager
from datetime import datetime, timezone

from mcp.server.auth.provider import AccessToken, TokenVerifier
from mcp.server.auth.settings import AuthSettings
from mcp.server.fastmcp import FastMCP
from mcp.server.auth.middleware.auth_context import get_access_token
from mcp.types import ImageContent, TextContent

from remsup_config import RemsupMcpConfig, load_config
from remsup_relay_client import exec_command_over_relay, normalize_device_id
from remsup_screen_sessions import ScreenSessions, ConfirmedInputRefusal, failure_code
from remsup_screen_transport import connect_screen


CONFIG = load_config()


class StaticTokenVerifier(TokenVerifier):
    def __init__(self, client_tokens: dict[str, str]):
        self._token_to_client = {token: client for client, token in client_tokens.items()}

    async def verify_token(self, token: str) -> AccessToken | None:
        client_id = self._token_to_client.get(token)
        if not client_id:
            return None
        return AccessToken(token=token, client_id=client_id, scopes=["remsup:exec"])


@asynccontextmanager
async def screen_lifespan(_):
    async def cleanup():
        while True:
            await asyncio.sleep(1)
            await asyncio.to_thread(SCREEN.heartbeat)
    task = asyncio.create_task(cleanup())
    try:
        yield {}
    finally:
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        SCREEN.close_all()


class RemsupFastMCP(FastMCP):
    def add_tool(self, fn, **kwargs):
        # FastMCP calls sync tools on its event loop. Bound blocking relay work
        # separately so an EXEC cannot prevent screen status or HTTP dispatch.
        if not inspect.iscoroutinefunction(fn):
            original = fn
            pool = "_command_workers" if fn.__name__ == "remsup_exec_command" else "_screen_workers"
            if not hasattr(self, pool):
                setattr(self, pool, anyio.CapacityLimiter(4))
            limiter = getattr(self, pool)
            @wraps(fn)
            async def worker(**arguments):
                return await anyio.to_thread.run_sync(partial(original, **arguments), limiter=limiter)
            fn = worker
        return super().add_tool(fn, **kwargs)

    def streamable_http_app(self):
        app = super().streamable_http_app()
        manager_lifespan = app.router.lifespan_context

        @asynccontextmanager
        async def application_lifespan(application):
            # Stateless MCP server lifespans are per HTTP request. Registry
            # cleanup belongs to the ASGI app, outside those request lifespans.
            async with screen_lifespan(application):
                async with manager_lifespan(application):
                    yield

        app.router.lifespan_context = application_lifespan
        return app


def build_mcp(config: RemsupMcpConfig = CONFIG) -> FastMCP:
    return RemsupFastMCP(
        "remsup",
        host=config.host,
        port=config.port,
        token_verifier=StaticTokenVerifier(config.client_tokens),
        auth=AuthSettings(
            issuer_url=config.public_url,
            resource_server_url=config.public_url,
            required_scopes=["remsup:exec"],
        ),
        stateless_http=True,
        json_response=True,
    )


MCP = build_mcp(CONFIG)
# Controlled-test transport is enabled only by the explicit server flag.
# Production transport security still requires TLS epic #27; it must
# be completed and verified before production use.
def audit_screen_drop(caller,reason):
    allowed={'closed','permission-ended','lease-expired','outcome-unknown','refused-or-failed','geometry-changed'}
    append_audit({'tool':'remsup_screen_drop','caller':hashlib.sha256(caller.encode('utf-8')).hexdigest()[:12],'result_code':reason if reason in allowed or failure_code(RuntimeError(reason))==reason else 'refused-or-failed'})

SCREEN = ScreenSessions((lambda device_id, otp: connect_screen(CONFIG.relay_host, CONFIG.relay_port, device_id, otp)) if CONFIG.screen_test_mode else None,on_drop=audit_screen_drop)


def screen_caller() -> str:
    token = get_access_token()
    if token is None or not token.client_id:
        raise PermissionError("authenticated-caller-required")
    return token.client_id


@MCP.tool()
def remsup_screen_capabilities(device_id: str = "", otp: str = "") -> dict:
    return screen_call("capabilities", lambda caller: SCREEN.capabilities(caller, device_id, otp))


@MCP.tool()
def remsup_screen_request(device_id: str, otp: str, monitor_id: str, control: bool = False) -> dict:
    return screen_call("request", lambda caller: SCREEN.request(caller, device_id, otp, monitor_id, control))


@MCP.tool()
def remsup_screen_status(session_id: str) -> dict:
    return screen_call("status", lambda caller: SCREEN.operation(caller, session_id, "status"))


@MCP.tool()
def remsup_screen_capture(session_id: str) -> list[TextContent | ImageContent]:
    return screen_call("capture", lambda caller: SCREEN.operation(caller, session_id, "capture"))


@MCP.tool()
def remsup_screen_mouse(session_id: str, frame_id: str, geometry_version: int, action: str, x: int, y: int, scroll_steps: int = 0) -> dict:
    return screen_call("mouse", lambda caller: SCREEN.operation(caller, session_id, "mouse", frame_id=frame_id, geometry_version=geometry_version, action=action, x=x, y=y, scroll_steps=scroll_steps))


@MCP.tool()
def remsup_screen_text(session_id: str, frame_id: str, geometry_version: int, text: str) -> dict:
    """Type up to128 UTF-16 units into the eligible foreground application on the selected monitor; active keyboard permission required."""
    return screen_call("text", lambda caller: SCREEN.operation(caller, session_id, "text", frame_id=frame_id, geometry_version=geometry_version, text=text))


@MCP.tool()
def remsup_screen_key(session_id: str, frame_id: str, geometry_version: int, key: str) -> dict:
    """Eligible foreground application: ctrl+l/a/s, ctrl+shift+s, enter, tab/shift+tab, escape, backspace/delete, arrows, home/end. Never auto-retry uncertain input."""
    return screen_call("key", lambda caller: SCREEN.operation(caller, session_id, "key", frame_id=frame_id, geometry_version=geometry_version, key=key))


@MCP.tool()
def remsup_screen_close(session_id: str) -> dict:
    return screen_call("close", lambda caller: SCREEN.operation(caller, session_id, "close"))


def append_audit(event: dict) -> None:
    os.makedirs(os.path.dirname(CONFIG.audit_log_path), exist_ok=True)
    event["timestamp"] = datetime.now(timezone.utc).isoformat()
    with open(CONFIG.audit_log_path, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(event, separators=(",", ":")) + "\n")


def screen_call(operation, call):
    caller = screen_caller()
    started = time.monotonic()
    result_code = "refused-or-failed"
    try:
        result = call(caller)
        result_code = result.get('error') if isinstance(result,dict) and result.get('error') in ('input-not-applied','outcome-unknown') else 'already-closed' if isinstance(result,dict) and result.get('result')=='already-closed' else 'ok'
        if isinstance(result,dict) and result.get('close_reason') in ('permission-ended','outcome-unknown','invalid-screen-state','invalid-screen-permission-deadline','screen-response-mismatch','refused-or-failed'):
            result_code='already-closed:'+result['close_reason']
        return result
    except ConfirmedInputRefusal as refusal:
        result_code=refusal.code
        raise
    except Exception as exc:
        result_code=failure_code(exc)
        raise
    finally:
        append_audit({"tool": "remsup_screen_" + operation,
                      "caller": hashlib.sha256(caller.encode("utf-8")).hexdigest()[:12],
                      "result_code": result_code,
                      "duration_ms": int((time.monotonic() - started) * 1000)})


@MCP.tool()
def remsup_health() -> dict:
    return {
        "ok": True,
        "relay_host": CONFIG.relay_host,
        "relay_port": CONFIG.relay_port,
        "command_timeout_seconds": CONFIG.command_timeout_seconds,
        "max_output_bytes": CONFIG.max_output_bytes,
        "clients": sorted(CONFIG.client_tokens.keys()),
    }


@MCP.tool()
def remsup_exec_command(device_id: str, otp: str, command: str) -> dict:
    clean_id = normalize_device_id(device_id)
    started = time.monotonic()
    exit_code = 1
    stdout = ""
    stderr = ""
    truncated = False
    error = ""

    try:
        result = exec_command_over_relay(
            CONFIG.relay_host,
            CONFIG.relay_port,
            clean_id,
            otp,
            command,
            CONFIG.command_timeout_seconds,
            CONFIG.max_output_bytes,
        )
        exit_code = int(result["exit_code"])
        stdout = str(result["stdout"])
        stderr = str(result["stderr"])
        truncated = bool(result["truncated"])
        return {
            "device_id": clean_id,
            "exit_code": exit_code,
            "stdout": stdout,
            "stderr": stderr,
            "truncated": truncated,
            "duration_ms": int(result["duration_ms"]),
        }
    except Exception as exc:
        error = str(exc)
        raise
    finally:
        append_audit({
            "tool": "remsup_exec_command",
            "device_id": clean_id,
            "exit_code": exit_code,
            "duration_ms": int((time.monotonic() - started) * 1000),
            "stdout_bytes": len(stdout.encode("utf-8", errors="replace")),
            "stderr_bytes": len(stderr.encode("utf-8", errors="replace")),
            "truncated": truncated,
            "error": error,
        })


if __name__ == "__main__":
    MCP.run(transport=CONFIG.transport)
