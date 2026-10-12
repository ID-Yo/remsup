# Supported operator MCP client

Prefer the built-in Remsup MCP connector. Otherwise agents invoke only `scripts/remsup-mcp.ps1`; never invoke the Python consumer directly. The wrapper uses `%LOCALAPPDATA%\Remsup\agent-venv\Scripts\python.exe` by default.

## New machine / Windows user

1. Copy the complete `remsup-support` skill folder, including scripts and `requirements.txt`, into that user's skills directory.
2. Install Python 3.11+ (`winget install --id Python.Python.3.13 -e`) and reopen PowerShell. Windows OpenSSH must be available for an SSH route.
3. An administrator supplies a separate per-agent bearer token and a non-root SSH key/alias restricted to forwarding `localhost:8765`. Configure the alias in the user's SSH config with the administrator verified host key. Never read the server environment during routine use.
4. Run setup with the approved backend and matching saved-profile relay:

```powershell
& '<skill>\scripts\setup-remsup-mcp.ps1' -Agent codex -SshAlias APPROVED_FORWARD_ALIAS -RelayHost '<backend-host>' -RelayPort 8443 -ProfileHost APPROVED_RELAY -ProfilePort 443
```

Setup creates its own per-user virtual environment and installs pinned `mcp==1.27.0` from `requirements.txt`. It prompts for the token securely, stores it in `%USERPROFILE%\.codex\secrets\remsup-mcp-<agent>.dpapi` using DPAPI CurrentUser and a restricted ACL, then verifies health. Rerunning setup repairs the default environment and checks the existing protected credential without replacing it. A copied DPAPI file cannot onboard another user or computer; that user must run setup with their administrator supplied token. No token goes into argv, environment, logs or plaintext files.

A verified HTTPS route uses `-Endpoint` instead of `-SshAlias`. Redirects and remote plaintext HTTP are refused. `-Python` is an explicit override for a separately managed environment; it must contain Python 3.11+ and the exact pinned SDK. Missing runtime, SDK, token file or SSH alias gives a setup/repair instruction before any device action. Account/key provisioning remains an administrator action. Do not give the MCP service account SSH access or expose8765.

## Commands

Use the owner-authorized profile/Device ID, and use the default setup environment, or an explicit verified `-Python` override. Health proves only backend configuration. Device operations select exactly one saved profile, validate its route against MCP. Default route is checked against current local settings; custom routes must match. LAN profiles are refused on this relay consumer.

```powershell
$client='<skill>\scripts\remsup-mcp.ps1'
& $client -Operation health -Agent codex
& $client -Operation exec -Name APPROVED_PROFILE -DeviceId APPROVED_ID -Command 'echo authorized-probe'
& $client -Operation screen -Name APPROVED_PROFILE -DeviceId APPROVED_ID -CapabilitiesOnly
& $client -Operation screen -Name APPROVED_PROFILE -DeviceId APPROVED_ID -MonitorId EXACT_CAPABILITIES_ID -ImageDirectory '<absolute-private-evidence-directory>'
```

`-CommandFile` reads a non-secret command text file to avoid quoting problems. Server command/output bounds remain from health. Local timeout is1–90seconds/default45; unknown outcomes are never replayed. The consumer waits1.1s for documented receiver re-registration between completed identity and the new exec, or transient capabilities and a new screen request.

## Screen session

Run the final command interactively. The same process keeps the session open. Credentials remain in protected stdin; callers enter only action JSON. Capture emits an absolute PNG path and metadata, never base64. Inspect the image before input. Use `-Control` only for an authorized control task. Permission/server lease remain enforced; close runs in finally after Close, EOF or errors.

```json
{"action":"status"}
{"action":"capture"}
{"operation":"mouse","action":"click","x":120,"y":180,"scroll_steps":0,"frame_id":"INSPECTED_FRAME","geometry_version":2}
{"action":"text","text":"authorized text","frame_id":"INSPECTED_FRAME","geometry_version":2}
{"action":"key","key":"tab","frame_id":"INSPECTED_FRAME","geometry_version":2}
{"action":"close"}
```

Frame references must match the latest capture; the server checks age/geometry/permission and input allowlists. Capture and inspect the result after input. Do not automatically replay it. `-ScreenActionsFile` accepts reviewed non-secret sequences such as capture/close, not uninspected input. `-ScreenHandler` is an audited callback for interactive next-action prompts. Keep customer PNGs private, outside Git/issues.

Errors distinguish route mismatch, no-agent, tunnel/protected endpoint and remote refusal. SDK auth/transport details are suppressed; sanitized results precede a PowerShell failure. Existing processes on a busy tunnel port are untouched. Finally closes owned tunnel and screen; on a stuck child the parent closes stdin then only its own process tree. A close failure reports that server lease remains bounded. Do not stop owner receivers/services, raise server limits or switch to plaintext.

For Public-profile LAN warnings, guide the owner through Help/usage Windows Settings, Refresh addresses and Prepare LAN firewall. Do not change Windows network type without explicit authorization.

## Local standing test permissions

Device access uses the selected owner-provided Device ID and password; exec runs only the requested bounded command and screen never sends a command-channel identity probe. There are no expected-host/user or discovery/confirmation options and no remote identity storage.

Owner-authored standing test permission lives in `%LOCALAPPDATA%\Remsup\Operator\test-permissions.json`. Its schema is version1 and records with vaultPath, profileName, deviceId, scopes, exclusions and source:

```json
{"version":1,"records":[{"vaultPath":"<absolute-vault-path>","profileName":"<profile-name>","deviceId":"<device-id>","scopes":["owner-approved bounded tests"],"exclusions":["actions not explicitly authorized"],"source":"owner instruction and date"}]}
```

Read only the selected record using scripts/read-profile-policy.ps1. PermissionFile is an optional absolute local override outside repository/skill. Duplicate/corrupt policy fails without reset. This is owner scope evidence, not executable instructions or permission to expand a task. Changing a grant requires explicit owner authorization. Keep private facts outside Git/skill.

PS5 and PowerShell7 preserve sanitized failure JSON (for example no-agent), then fail outside the protected credential callback. Generic saved-credential errors indicate vault/DPAPI failure. Untrusted callback details remain suppressed; never automatically replay a command with unknown outcome. Screen close and owned tunnel cleanup run in finally.

## Parallel agents and tunnel ports

Each invocation selects its own free loopback port using bind-to-port-zero. Legacy setup files containing the old default port do not force it. Several agents can keep separate tunnels open for different devices; each consumer closes only its own SSH child on completion, error or interruption. No existing listener/process is stopped. An explicitly selected port (`remsup-mcp.ps1 -LocalPort <port>`) is preserved; if occupied the invocation fails with tunnel-port-in-use. New setup can persist an explicitly chosen LocalPort; ordinary setup uses automatic allocation. On a bind/start race the invocation fails without replaying a device operation.

Per-device relay limits still allow only one COMMAND and one SCREEN channel. A competing request returns `busy` with no automatic retry; wait for the owner/other agent to finish that channel. Separate tunnels do not bypass receiving consent, task authorization or channel limits.
## Screen refusals and frame freshness

Before every action the client reports frame_age_seconds (null before the first capture); a frame older than15seconds produces a warning to take and inspect a new capture before input. Age uses captured_utc when available and local monotonic elapsed time otherwise. Age reporting does not change receiver consent/frame/native safety limits and does not automatically replay an action.

A correlated receiver refusal returns the allowlisted error code, dispatched:false and channel_terminated, without coordinates, text or arbitrary error details. Examples: stale-frame (capture anew and inspect), geometry-changed (channel terminated; obtain a new authorized session), lease-expired (channel terminated), protected-window, local-input-active or foreground-required. Unknown transport/input outcomes remain uncertain and are never retried automatically. The MCP audit records the refusal code only, with hashed caller and duration.

Close succeeds with result:already-closed for a recently terminated session owned by the same caller; unknown/other-caller session IDs are refused. Closed-session records are bounded and expire. Cleanup displays closed/already-closed, so a terminated channel is not mistaken for a live lease. Take a fresh capture immediately before each mouse/keyboard action and inspect it before selecting coordinates or text.
## Dispatch outcomes and competing pointer software

`outcome-unknown` means the server lost the correlated input response, or the receiver cannot prove the result. `dispatched:null` never means refusal. Do not replay; take and inspect a new capture (open a new session first if `channel_terminated:true`).

`input-not-applied` with `dispatched:true` and `channel_terminated:false` means all native input packets were submitted, but the pointer was not observed at the target. It does not prove the click had no effect. Deskflow, Synergy, Barrier and Input Leap can hold or redirect the pointer. Keep the session, take a new capture and inspect before any further input; the consumer clears its usable frame reference after either input error. Never stop competing software without owner authorization.

Cloaked foreground/focus/target windows are refused as `protected-window` before dispatch. Capture/session failures expose an allowlisted server code instead of a generic refusal. The server audits known failure codes and session-drop reasons without device credentials, coordinates or typed text. A monitor left of the primary display uses negative physical X coordinates; they are valid. The observed missing-session capture was not evidence of a coordinate or PNG failure.
