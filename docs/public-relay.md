# Public relay — v1.51 experimental

The ready Windows x64 app uses `91.227.77.177:443` over TLS 1.2/1.3. Its built-in SPKI SHA256 pin is `b826918726ce38a6825ad8ea9df38180dad007a9d724673d6df6a5d3efe8d0f4`. These public values identify the relay and its trusted public key. The shipped pin was compared against both the configured certificate and the certificate presented by the live TLS listener on 2026-10-13. Never accept a changed pin without independently verifying it.

1. Download the v1.51 release EXE or ZIP and compare its hash with the release SHA256SUMS. The EXE is unsigned. Windows x64 and installed Evergreen WebView2 are required; no SDK is needed to run it.
2. On the receiving computer, run the visible app. A fresh configuration starts support using the public relay; wait for receiving readiness. Existing custom Network settings are preserved. Share the Device ID and current OTP only with the authorized operator through a protected channel.
3. On the operator computer, run the same app. Enter that Device ID, use Default in Quick options, Connect and provide the protected pairing password. Default follows Settings > Network. Check the device identity before authorized maintenance; commands have time/output limits.
4. Review receiving Settings > Security before sharing credentials. Existing defaults allow screen viewing/control and automatic screen approval after pairing. Disable automatic approval if each screen session must be approved locally. Remote settings editing is OFF unless the local owner enables it. Device passwords authorize commands without a separate per-command receiver prompt.
5. Stop support when the task ends and use tray Exit to close the app. The window X may hide it to the tray. Startup is an optional visible current-user setting; no hidden Windows service or automatic elevation is added.

## Separate services

The public relay is for human/operator app connections. It is not a public unauthenticated MCP endpoint. AI agents need a separately configured MCP bridge with administrator-issued bearer authentication and device-owner permission. This download contains no bearer tokens, device passwords or relay private keys. See the [operator skill](../skills/remsup-support/SKILL.md).

Automatic updates are OFF by default. This public-relay candidate has no approved automatic update channel. Use the release downloads manually. Existing installer/stable path and verified rollback-capable update machinery remain; a configured deployment requires its own exact approved artifact manifest/hash and pinned update service. Do not point this app at another distribution's channel.

## Self-hosting and limits

You can select a custom TLS relay and verified pin in Settings > Network. The C backend must stay private behind a TLS terminator; see [architecture](architecture.md) and [stunnel example](../relay/stunnel/remsup.conf.example). v1.50 remains the earlier self-hosted distribution.

TLS protects each connection to the relay. Relay administrators can inspect pairing credentials and command/screen traffic; this is not end-to-end encryption. The protocol-key shape filter is not authentication. Malformed headers/keys cause a temporary IP block after more than three bad attempts; comprehensive abuse quotas, enrollment and revocation are not provided by this MVP. Availability has no service-level guarantee.

## Isolated developer runtime review

`--fixture-root <absolute-owned-folder> --fixture-live-relay` explicitly enables the normal pinned TLS receiver and real command execution while keeping settings, logs and vault inside that folder. It requires a protected `device-id.txt` with a fresh test-only ID, and protected `settings.txt` with a test-only permanent password. It cannot be combined with the plaintext loopback `--fixture-relay`. It does not install, alter startup or update the app. Synthetic screen surfaces remain in this review mode; it does not prove actual desktop capture. Never use customer credentials in fixture scripts or evidence. Stop/Exit and remove only the owned fixture after the authorized test.
