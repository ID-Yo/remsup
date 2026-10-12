# Agent access

Install `skills/remsup-support` into your agent's skill directory and read SKILL.md. Provision an authenticated MCP endpoint or separately authorized non-root SSH tunnel. The setup helper requires administrator-supplied relay/profile endpoints and securely entered token; no default service or credentials are included.

Use setup-remsup-mcp.ps1 for protected per-agent DPAPI configuration, then remsup-mcp.ps1 with the exact saved profile name. Command files contain only the reviewed command, never passwords. Every command is bounded and independent. Check outcome, exit status and truncation. The skill documents screen session cleanup and unknown outcomes.
