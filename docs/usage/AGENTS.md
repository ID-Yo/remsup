# Remsup usage contract for agents

Read this [README.md](README.md) and all four guides: [agent connection](03-agent-guide.md) и [troubleshooting](04-troubleshooting.md). They describe the v1.25 candidate from repository source, not proof that a release, endpoint or two-device cutover is live.

- Obtain the owner's explicit task, target Device ID , time/command limits, current OTP or permanent password, and approved MCP/SSH route. Never guess credentials, scan for devices or treat MCP health as pairing proof.
- Keep MCP bearer token separate from device OTP/password. Use protected configuration or the saved-profile helper. Never print or copy secrets into prompts, files, command output, logs, Issues or reports.
- Verify TLS endpoint/pin before maintenance. Windows relay defaults to pinned TLS on port 443. Do not fall back to the old plaintext public port if TLS fails. Explicit LAN transport remains plaintext and needs its own authorization.
- MCP is bearer-authenticated at VPS loopback `127.0.0.1:8765`; reach it through the authorized SSH route. Never expose that listener publicly or disable authentication. The MCP process connects to the C relay's separate loopback listener; the Windows client connects to the pinned TLS listener.
- TLS protects the network hop to the relay, not against the relay administrator: the relay can read credentials, commands/results and screen traffic. Phase 2 relay tokens, general audit/revocation, enrollment and end-to-end secrecy remain incomplete.
- Screen capture/control is separate from command authorization. Receiving Security settings control view, mouse/keyboard and default-on approval after pairing. Use unattended approval only with explicit device-owner authorization. MCP screen tools retain their separate `REMSUP_SCREEN_TEST_MODE=1` server gate, off by default.
- Remsup is a visible support application with tray Exit and explicit screen disconnect. Minimize keeps it running. No stealth, credential theft, security bypass, privilege escalation trick, destructive action or unapproved maintenance.
- Use one bounded command at a time. Do not assume persistent shell state or elevation. A timeout/disconnect can leave the remote result unknown; verify before any retry.
- Check actual target results. Close only task-owned SSH tunnels/jobs when finished; preserve owner-managed client, relay and MCP services. Do not alter customer data, passwords, firewall or server settings outside the approved task.

The [remsup-support skill](../../skills/remsup-support/SKILL.md) is a shortcut to this contract, not an authorization or credential source. For development, repository-level `AGENTS.md` and governance remain in force.
