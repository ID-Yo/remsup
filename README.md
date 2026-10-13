# Remsup

Visible Windows remote support for explicitly authorized maintenance. The receiver can stop support and exit from the tray. Commands have time and output bounds. Screen viewing/control follows the receiving user's settings and consent policy. No hidden service, automatic elevation or endpoint security bypass is provided.

The v1.51 experimental Windows app uses the public Remsup TLS relay by default. Anyone may use this relay for support explicitly authorized by the device owner. No account, MCP token or device credential is included. Relay administrators can see relayed credentials and command data; this is not a claim of production security or end-to-end encryption.

Download the reviewed ready app from [GitHub Releases](https://github.com/ID-Yo/remsup/releases), verify its SHA256SUMS, and run the EXE on Windows x64 with the Evergreen WebView2 Runtime installed. SDK, Node and Python are needed only for source builds or separate MCP tooling. See [public relay setup](docs/public-relay.md). v1.50 remains the separate self-hosted experimental distribution.

## Build and test

Windows x64, PowerShell 7 plus Windows PowerShell, .NET SDK from `global.json`, Node 24 and Python 3.13 are required. Windows .NET Framework compiler is used for compatibility tests. Relay tests require Linux/WSL with gcc and Python 3. The installed Evergreen WebView2 Runtime is required to run the desktop application; it is not bundled.

```powershell
python -m venv .venv
.venv/Scripts/python -m pip install -r mcp/requirements.txt
./scripts/build-modern-desktop.ps1 -Publish -Version v1.51 -Output <new-absolute-output-folder>
./scripts/ci-local.ps1 -Python <venv-python> -OutputRoot <new-absolute-ci-folder>
```

The distribution is one self-contained Windows EXE with embedded UI and third-party notices. Extraction of .NET managed/native runtime files is automatic. Legacy client/operator sources and builds remain compatibility test inputs, not an additional release distribution requirement. Build products are ignored and must not be committed.

The icon is generated locally from `assets/remsup.svg`; the checkout contains no binary image or EXE artifacts. [Architecture and configuration](docs/architecture.md), [usage](docs/usage/README.md), [security](SECURITY.md), [licensing/provenance](THIRD_PARTY_NOTICES.md) and the [support skill](skills/remsup-support/SKILL.md) describe the supported scope.

## Configuration

The default relay is `91.227.77.177:443`, pinned to SPKI SHA256 `b826918726ce38a6825ad8ea9df38180dad007a9d724673d6df6a5d3efe8d0f4`. These are public routing/trust metadata, not credentials. No manual relay setup is needed on a fresh v1.51 app. Existing saved custom endpoints and pins remain unchanged. Settings > Network can select your own verified TLS relay. Administrators may provision `REMSUP_RELAY_HOST` and `REMSUP_RELAY_PIN` together before startup; process-only environment overrides do not persist after login.

Automatic updates default to OFF. The app makes no update network request without an administrator-provisioned `REMSUP_UPDATE_HOST` and `REMSUP_UPDATE_PIN`; `REMSUP_UPDATE_PORT` defaults to 8444. The current public relay does not establish an approved update channel for this artifact. Download new releases manually. Existing explicit update preferences are preserved, but an unconfigured channel remains disabled. The existing fixed-resource, hash verification, stable per-user installation and rollback handoff mechanics remain available for separately configured deployments.

## Source export

`export-allowlist.json` enumerates every reviewed source file. `python scripts/scan-public.py` checks tracked content without printing matched credentials. Add `--review` to inventory address/version literals, device-like numbers, emails, URLs and SSH routes by location/category for manual review. Synthetic fixtures and schema/vendor URLs need contextual review. `python scripts/export-source.py <new-directory>` exports committed allowlisted blobs only, without `.git`. This scanner supplements manual review; it does not prove absence of every possible secret.

Remsup source and original artwork are licensed under the [MIT License](LICENSE), copyright 2026 ID-Yo. Dependencies retain their own terms; see [third-party notices](THIRD_PARTY_NOTICES.md).

## Runtime diagnostics and remote settings

[Read-only runtime diagnostics](docs/architecture/runtime-diagnostics.md) reports bounded readiness and updater outcomes without credentials. [Remote ordinary settings editing](docs/architecture/remote-settings-permission.md) requires a local owner opt-in, OFF by default, and an authenticated SCREEN control lease. Permission changes, credentials, firewall and administrator actions remain local.
