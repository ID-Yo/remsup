# Getting started

Remsup is visible Windows remote support for explicitly authorized maintenance. The same Windows app is used by the device owner and by a human operator. An AI agent connects through an authenticated MCP bridge and the operator skill in this repository.

## For people

You need Windows x64 and the Evergreen WebView2 Runtime on both computers. The app is a single unsigned EXE, so Windows SmartScreen may ask you to confirm it.

1. Download the latest stable build from [GitHub Releases](https://github.com/ID-Yo/remsup/releases). Stable files have `stable` in the name and beta files have `beta`. Compare the file with the release `SHA256SUMS.txt`.
2. **On the computer to be supported**, run the app and open Settings > Security. Check which actions you allow: screen viewing, screen control, automatic approval of paired screen sessions and file transfer. Wait until the status shows Ready.
3. Share the Device ID and the current one-time password (or permanent password) with the operator through a protected channel. Do not post them in public chat or tickets.
4. **On the operator computer**, run the same app, enter the Device ID, keep Default in Quick options, choose Connect and enter the password. Check that it is the right device before you run anything.
5. Work within the agreed task: run bounded commands, view or control the screen, or transfer files if the owner allowed it. Commands have time and output limits.
6. To end support, the owner uses tray Exit. Closing or minimizing the window keeps Remsup running in the tray.

By default the app connects through the public Remsup relay. The relay administrator can see relayed credentials and data, and the connection is not end-to-end encrypted. You can select your own TLS relay and pin in Settings > Network. See [public relay setup](public-relay.md) and the [security notes](../SECURITY.md).

## For AI agents

The public relay is for app connections. An AI agent needs separately provisioned access, and every action needs the device owner's authorization.

Before you start, get these from the device owner or administrator:

- the task and its limits, including whether state changes are allowed;
- the exact Device ID and a protected source for its password;
- an MCP bridge (HTTPS endpoint or an approved SSH tunnel) and a per-agent bearer token issued by the administrator.

Then:

1. Copy the [remsup-support skill](../skills/remsup-support/SKILL.md) into your agent's skills folder and read its [MCP client guide](../skills/remsup-support/references/mcp-client.md).
2. Run `setup-remsup-mcp.ps1` once. It stores the bearer token with Windows DPAPI and checks health. Never put tokens or passwords in prompts, files, logs or tickets.
3. Check the bridge, then run a bounded command on the exact device:

```powershell
$client = '<skill>\scripts\remsup-mcp.ps1'
& $client -Operation health -Agent codex
& $client -Operation exec -Name APPROVED_PROFILE -DeviceId APPROVED_ID -Command 'ver'
```

4. Other operations are `diagnostics`, `screen`, `upload`, `download`, `cancel` and `status`. Screen access is off unless the administrator enables it. File transfer needs the owner's permission in Settings > Security and uses an explicit destination. See the skill for the details of each operation.
5. Check the real result. A timeout or disconnect can leave the outcome unknown, so inspect the device before you retry. Close your own screen sessions when you finish.

Example prompt for a Codex-style agent:

```text
$remsup-support Verify the identity of the authorized device.
Run only ver and report the result without changing anything.
```

More: [usage guides](usage/README.md), [troubleshooting](usage/04-troubleshooting.md) and [architecture](architecture.md).

