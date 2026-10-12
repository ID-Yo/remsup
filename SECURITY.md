# Security and consent

Remsup is visible support for authorized Windows maintenance. Stop support and tray Exit are user controls. Closing/minimizing the window may keep support running in the tray. Unattended access requires explicit owner permission. Windows startup is an optional current-user setting.

Windows relay and direct LAN connections use TLS with a verified SPKI SHA256 pin. The C relay backend is plaintext and must stay loopback behind the TLS terminator. The protocol-key shape is a scanner filter, not authentication. Device ID plus OTP/permanent password pairs endpoints; the relay administrator can inspect bridged data. Deployers must review enrollment, revocation, credential handling, audit and rate limits for their use case.

The Python MCP server requires bearer authentication and keeps bounded command/output policy. Keep it loopback behind an authorized SSH tunnel or separately secured HTTPS proxy. Provision per-agent tokens using protected configuration. Do not commit actual secrets. LAN listening is opt-in with active Windows Private networking and a scoped firewall rule; UAC remains an owner action.

This snapshot is not production security approval. No public default server or certificate is supplied. Discuss vulnerabilities privately with the repository owner; do not attach credentials, customer data or raw private logs.
