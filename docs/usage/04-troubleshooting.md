# Troubleshooting

Reserved endpoint placeholders require configuration. Verify the administrator's endpoint and certificate pin; never disable TLS verification or MCP authentication. Health/tool discovery proves bridge access, not receiver availability.

For LAN, check receiving Network settings, active Private network, exact address/port and scoped firewall rule. Preparing/removing firewall rules requires explicit owner confirmation and Windows elevation. Runtime diagnostics are stored locally under the user's Remsup logs directory; do not publish raw logs.

A timeout/disconnect may leave a command outcome unknown. Do not automatically retry; inspect bounded status and resolve access first. Close only task-owned screen sessions, tunnels and helper processes after work.
