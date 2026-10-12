import json
import os
from dataclasses import dataclass


@dataclass(frozen=True)
class RemsupMcpConfig:
    relay_host: str
    relay_port: int
    client_tokens: dict[str, str]
    audit_log_path: str
    command_timeout_seconds: int
    max_output_bytes: int
    host: str
    port: int
    transport: str
    public_url: str
    screen_test_mode: bool = False


def _load_client_tokens() -> dict[str, str]:
    clients_json = os.environ.get("REMSUP_MCP_CLIENTS_JSON", "").strip()
    if clients_json:
        parsed = json.loads(clients_json)
        if not isinstance(parsed, dict):
            raise RuntimeError("REMSUP_MCP_CLIENTS_JSON must be a JSON object")
        tokens = {str(name): str(token).strip() for name, token in parsed.items() if str(token).strip()}
        if tokens:
            return tokens

    token = os.environ.get("REMSUP_MCP_TOKEN", "").strip()
    if token:
        return {"default": token}

    raise RuntimeError("REMSUP_MCP_CLIENTS_JSON or REMSUP_MCP_TOKEN is required")


def load_config() -> RemsupMcpConfig:
    return RemsupMcpConfig(
        relay_host=os.environ.get("REMSUP_RELAY_HOST", "127.0.0.1"),
        relay_port=int(os.environ.get("REMSUP_RELAY_PORT", "8443")),
        client_tokens=_load_client_tokens(),
        audit_log_path=os.environ.get("REMSUP_AUDIT_LOG", "/var/log/remsup/mcp-audit.jsonl"),
        command_timeout_seconds=int(os.environ.get("REMSUP_COMMAND_TIMEOUT_SECONDS", "30")),
        max_output_bytes=int(os.environ.get("REMSUP_MAX_OUTPUT_BYTES", str(256 * 1024))),
        host=os.environ.get("REMSUP_MCP_HOST", "127.0.0.1"),
        port=int(os.environ.get("REMSUP_MCP_PORT", "8765")),
        transport=os.environ.get("REMSUP_MCP_TRANSPORT", "streamable-http"),
        public_url=os.environ.get("REMSUP_MCP_PUBLIC_URL", "http://127.0.0.1:8765"),
        screen_test_mode=os.environ.get("REMSUP_SCREEN_TEST_MODE", "0") == "1",
    )
