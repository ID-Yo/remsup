import json
import os
import time
import hashlib
import re
import asyncio
import inspect
from functools import partial, wraps
import anyio
from contextlib import asynccontextmanager
from datetime import datetime, timedelta, timezone
from typing import Union

from mcp.server.auth.provider import AccessToken, TokenVerifier
from mcp.server.auth.settings import AuthSettings
from mcp.server.fastmcp import FastMCP
from mcp.server.auth.middleware.auth_context import get_access_token
from mcp.types import ImageContent, TextContent

from remsup_config import RemsupMcpConfig, load_config
from remsup_relay_client import DiagnosticsError, diagnostics_over_relay, exec_command_over_relay, normalize_device_id, validate_diagnostics_credentials
from remsup_screen_sessions import ScreenSessions, ConfirmedInputRefusal, SETTINGS_TYPES, failure_code
from remsup_screen_transport import connect_screen


CONFIG = load_config()

_SUPPORT_STATES = frozenset(("Stopped", "Connecting", "RelayWaiting", "SessionActive", "Reconnecting", "Failed"))
_CHECK_RESULTS = frozenset(("Current", "Available", "Error", "Disabled"))
_INSTALL_RESULTS = frozenset(("Installed", "Deferred", "RolledBack", "RecoveryFailed", "Failed"))
_ERROR_CODES = frozenset(("", "check-failed", "download-failed", "readiness-failed", "install-failed", "recovery-failed", "registration-repair", "diagnostics-unavailable"))
_DIAGNOSTIC_FAILURE_CODES = frozenset(("wrong-channel", "unavailable", "no-agent", "busy", "relay-denied", "invalid-request", "response-too-large", "invalid-response", "incomplete-response", "transport-unavailable", "relay-error"))
_LAN_WARNINGS = frozenset((
    "LAN IP is inactive. Refresh addresses and choose this computer's active private IPv4.",
    "LAN port must be 1-65535.",
    "The network is Public — change it to Private in Windows Settings before using LAN.",
    "LAN requires a Windows Private network. Check the network profile in Windows Settings.",
    "The scoped LAN firewall rule is missing. Select Prepare LAN firewall for this address and app.",
    "LAN identity is unavailable. Restore this Windows user's protected identity; it was not regenerated.",
    "LAN listener could not start. Check whether the address/port is already in use.",
    "LAN diagnostics unavailable.",
))


def _diagnostic_utc(value):
    if not isinstance(value, str) or len(value) > 40 or not (value.endswith("Z") or value.endswith("+00:00")):
        raise ValueError("invalid diagnostics timestamp")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None or parsed.utcoffset() != timedelta(0):
        raise ValueError("non-UTC diagnostics timestamp")
    return value


def _diagnostic_version(value, optional=False):
    if optional and value in (None, ""):
        return None
    if not isinstance(value, str) or len(value) > 32 or not re.fullmatch(r"v1\.\d{1,6}", value):
        raise ValueError("invalid diagnostics version")
    return value


def _diagnostic_error_code(value):
    return value if value in _ERROR_CODES else "diagnostics-unavailable"


def project_diagnostics(document):
    """Validate the receiver contract and return only reviewed safe fields."""
    if not isinstance(document, dict) or type(document.get("schema_version")) is not int or document.get("schema_version") != 1 or document.get("status") != "ok":
        raise ValueError("invalid diagnostics envelope")
    lan = document.get("lan")
    relay = document.get("relay")
    updates = document.get("updates")
    if not isinstance(lan, dict) or not isinstance(relay, dict) or not isinstance(updates, dict):
        raise ValueError("invalid diagnostics sections")
    for key in ("configured", "enabled", "ready"):
        if type(lan.get(key)) is not bool:
            raise ValueError("invalid LAN diagnostics")
    warning = lan.get("warning")
    if not isinstance(warning, str):
        raise ValueError("invalid LAN diagnostics")
    if warning not in _LAN_WARNINGS:
        warning = "LAN diagnostics unavailable." if warning else ""
    state = relay.get("state")
    ready = relay.get("ready")
    if state not in _SUPPORT_STATES or type(ready) is not bool:
        raise ValueError("invalid relay diagnostics")
    if type(updates.get("enabled")) is not bool:
        raise ValueError("invalid update diagnostics")

    last_check = updates.get("last_check")
    if last_check is not None:
        if not isinstance(last_check, dict) or last_check.get("result") not in _CHECK_RESULTS:
            raise ValueError("invalid update check")
        last_check = {
            "utc": _diagnostic_utc(last_check.get("utc")),
            "result": last_check["result"],
            "version": _diagnostic_version(last_check.get("version"), optional=True),
            "error_code": _diagnostic_error_code(last_check.get("error_code")),
        }

    last_download = updates.get("last_download")
    if last_download is not None:
        if not isinstance(last_download, dict):
            raise ValueError("invalid update download")
        sha256 = last_download.get("sha256")
        if not isinstance(sha256, str) or not re.fullmatch(r"[0-9a-f]{64}", sha256):
            raise ValueError("invalid update download")
        last_download = {
            "utc": _diagnostic_utc(last_download.get("utc")),
            "version": _diagnostic_version(last_download.get("version")),
            "sha256": sha256,
        }

    last_install = updates.get("last_install")
    if last_install is not None:
        if not isinstance(last_install, dict) or last_install.get("result") not in _INSTALL_RESULTS:
            raise ValueError("invalid update install")
        last_install = {
            "utc": _diagnostic_utc(last_install.get("utc")),
            "result": last_install["result"],
            "error_code": _diagnostic_error_code(last_install.get("error_code")),
        }

    latest_errors = updates.get("latest_errors")
    if not isinstance(latest_errors, list) or len(latest_errors) > 8:
        raise ValueError("invalid update error history")
    projected_errors = []
    for item in latest_errors:
        if not isinstance(item, dict):
            raise ValueError("invalid update error history")
        projected_errors.append({
            "utc": _diagnostic_utc(item.get("utc")),
            "code": _diagnostic_error_code(item.get("code")),
        })

    return {
        "schema_version": 1,
        "status": "ok",
        "version": _diagnostic_version(document.get("version")),
        "lan": {"configured": lan["configured"], "enabled": lan["enabled"], "ready": lan["ready"], "warning": warning},
        "relay": {"state": state, "ready": ready},
        "updates": {
            "enabled": updates["enabled"],
            "last_check": last_check,
            "last_download": last_download,
            "last_install": last_install,
            "latest_errors": projected_errors,
        },
    }


def _diagnostics_status(error):
    if isinstance(error, DiagnosticsError):
        if error.status in ("unsupported", "unavailable", "denied", "error") and error.code in _DIAGNOSTIC_FAILURE_CODES:
            return {"status": error.status, "error_code": error.code}
    return {"status": "error", "error_code": "diagnostics-unavailable"}


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
            pool = "_command_workers" if fn.__name__ in ("remsup_exec_command", "remsup_diagnostics") else "_screen_workers"
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


@MCP.tool()
def remsup_settings_read(session_id: str) -> dict:
    """Read the allowlisted ordinary receiver settings through an active granted screen-control session."""
    return screen_call("settings.read", lambda caller: SCREEN.operation(caller, session_id, "settings.read"))


@MCP.tool()
def remsup_settings_set(session_id: str, name: str, value: Union[bool, int, str]) -> dict:
    """Set one allowlisted ordinary receiver setting through an active granted screen-control session."""
    audit_name = name if isinstance(name, str) and name in SETTINGS_TYPES else None
    return screen_call("settings.set", lambda caller: SCREEN.operation(caller, session_id, "settings.set", name=name, value=value), audit_name)


def append_audit(event: dict) -> None:
    os.makedirs(os.path.dirname(CONFIG.audit_log_path), exist_ok=True)
    event["timestamp"] = datetime.now(timezone.utc).isoformat()
    with open(CONFIG.audit_log_path, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(event, separators=(",", ":")) + "\n")


def screen_call(operation, call, setting_name=None):
    caller = screen_caller()
    started = time.monotonic()
    result_code = "refused-or-failed"
    try:
        result = call(caller)
        result_code = result.get('settings_error') if isinstance(result,dict) and result.get('settings_error') else result.get('error') if isinstance(result,dict) and result.get('error') in ('input-not-applied','outcome-unknown') else 'already-closed' if isinstance(result,dict) and result.get('result')=='already-closed' else 'ok'
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
        prefix = 'remsup_settings_' if operation.startswith('settings.') else 'remsup_screen_'
        suffix = operation.removeprefix('settings.') if operation.startswith('settings.') else operation
        event = {"tool": prefix + suffix,
                 "caller": hashlib.sha256(caller.encode("utf-8")).hexdigest()[:12],
                 "result_code": result_code,
                 "duration_ms": int((time.monotonic() - started) * 1000)}
        if setting_name is not None:
            event['setting_name'] = setting_name
        append_audit(event)


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


@MCP.tool()
def remsup_diagnostics(device_id: str, otp: str) -> dict:
    """Read a bounded, sanitized receiver status snapshot over the authenticated COMMAND channel."""
    caller = screen_caller()
    started = time.monotonic()
    result_code = "ok"
    try:
        try:
            clean_id = validate_diagnostics_credentials(device_id, otp)
        except ValueError:
            result_code = "invalid-request"
            return {"status": "denied", "error_code": result_code}
        document = diagnostics_over_relay(
            CONFIG.relay_host,
            CONFIG.relay_port,
            clean_id,
            otp,
            CONFIG.command_timeout_seconds,
        )
        return project_diagnostics(document)
    except Exception as exc:
        safe = _diagnostics_status(exc)
        result_code = safe.get("error_code", "diagnostics-unavailable")
        return safe
    finally:
        append_audit({
            "tool": "remsup_diagnostics",
            "caller": hashlib.sha256(caller.encode("utf-8")).hexdigest()[:12],
            "result_code": result_code,
            "duration_ms": int((time.monotonic() - started) * 1000),
        })


if __name__ == "__main__":
    MCP.run(transport=CONFIG.transport)
