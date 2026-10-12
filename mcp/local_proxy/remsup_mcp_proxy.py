"""
Local stdio MCP proxy.

Use this only when an MCP client requires local stdio and cannot connect to the
remote Remsup MCP endpoint directly.
"""

import os
import sys

import requests


REMOTE_URL = os.environ["REMSUP_REMOTE_MCP_URL"]
TOKEN = os.environ["REMSUP_MCP_TOKEN"]


def main() -> int:
    for line in sys.stdin:
        response = requests.post(
            REMOTE_URL,
            data=line.encode("utf-8"),
            headers={
                "Authorization": f"Bearer {TOKEN}",
                "Content-Type": "application/json",
            },
            timeout=60,
        )
        response.raise_for_status()
        sys.stdout.write(response.text)
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

