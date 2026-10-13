# Local permission for remote settings editing

The receiver defaults `AllowRemoteSettings` to false. Settings > Security exposes a local checkbox with a native confirmation before enabling. It is saved separately from drafts; turning it off persists immediately and rejects subsequent requests. Only the local embedded UI can invoke the permission action. Existing native checks continue to refuse input into all Remsup-owned windows, including the checkbox. Diagnostics remains read-only.

The authenticated caller must own an active SCREEN session with control rights, within the existing consent/expiry/interactive-desktop checks. `settings.read` and `settings.set` are structured operations in this session. A read returns only automatic-update and relay/LAN configuration; it never returns passwords, IDs, vault records or private identity. A write changes exactly one name/value:

| Name | Allowed value |
|---|---|
| `autoUpdates` | Boolean |
| `relayHost` | Valid existing hostname/IP, at most 253 characters |
| `relayPort` | Integer 1–65535 |
| `relayPin` | Existing TLS pin validation; no plaintext fallback |
| `lanAddress` | Empty or active private IPv4, at most 45 characters |
| `lanPort` | Integer 1–65535 |
| `lanEnabled` | `false` only; enabling support is local-only |

Screen view/control/automatic consent, remote-edit permission, passwords/OTP, saved-device vault, startup, install/uninstall, manual update checks, firewall and administrator actions are outside this interface. The visible local Windows/UAC flows remain unchanged. Updating relay/LAN configuration does not prepare firewall rules, grant LAN permission or change the Windows network profile. Existing receiving/LAN identity checks remain enforced.

Receiver serializes writes on its UI dispatcher, rechecks the session control and local permission immediately before a staged atomic save, and retains credentials and other settings internally. The bounded existing log records only fixed setting names and results, including local enable/revoke; no setting values or raw failures. Local drafts are refreshed after a remote save. Network changes are refused while a command or outbound check is active. The accepted change ends its SCREEN lease, waits up to ten seconds for channel gates to drain, and reconnects without interrupting an active command. An atomic reservation prevents new inbound COMMAND/SCREEN admission between the last idle check, settings save and reconnect; local outbound command starts are held during that window. A network setting is persisted first and reconnect is scheduled after the acknowledgement; routes can close before acknowledgement. Such a disconnect is an unknown outcome: never replay automatically, reconnect through an owner-known route and read the persisted setting. A failed reconnect is logged as a fixed code, without reverting to plaintext or assuming delivery.

Source fixtures verify default refusal, persisted local opt-in, granted control, ordinary update preference change, forbidden permission/password/vault/admin writes, immediate revocation, strict wire fields and rendered OFF description. No device owner's permission was enabled during implementation.
