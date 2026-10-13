# Architecture

`desktop/host` is the visible WPF/WebView2 desktop host; `desktop/ui` is its embedded React UI. Shared support, TLS, command and screen contracts live under `shared`. `client/portable-exe` and `operator/desktop` supply shared configuration/store code and legacy compatibility test inputs.

`relay/remsup-relay.c` pairs authorized receivers/operators. Deploy the backend only on loopback behind a TLS terminator; the stunnel configuration is an example, with certificate/key paths supplied by the administrator. `mcp` exposes bounded support tools with required bearer authentication. Its configuration comes from environment variables; no tokens are supplied.

Tests use synthetic fixtures and loopback sessions. Local CI builds the single EXE, checks host/UI contracts, legacy compatibility, relay lifecycle, MCP policies, operator wrapper and dependencies. These tests do not establish connectivity to any customer device or production server.

The v1.51 app includes the public pinned TLS relay defaults. The explicit isolated live-relay review mode uses the normal receiver/command implementation; ordinary fixtures remain synthetic/loopback. See [public relay setup](public-relay.md).
