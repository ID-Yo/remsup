# Remsup

Visible Windows remote support for explicitly authorized maintenance. The receiver can stop support and exit from the tray. Commands have time and output bounds. Screen viewing/control follows the receiving user's settings and consent policy. No hidden service, automatic elevation or endpoint security bypass is provided.

This source snapshot is prepared for review. It ships no credentials, relay service or release executable. Use only devices and infrastructure you are authorized to administer. Relay operators can see relayed credentials and command data; this is a controlled-test product, not a claim of production security.

## Build and test

Windows x64, PowerShell 7 plus Windows PowerShell, .NET SDK from `global.json`, Node 24 and Python 3.13 are required. Windows .NET Framework compiler is used for compatibility tests. Relay tests require Linux/WSL with gcc and Python 3. The installed Evergreen WebView2 Runtime is required to run the desktop application; it is not bundled.

```powershell
python -m venv .venv
.venv/Scripts/python -m pip install -r mcp/requirements.txt
./scripts/build-modern-desktop.ps1 -Publish -Version v1.50 -Output <new-absolute-output-folder>
./scripts/ci-local.ps1 -Python <venv-python> -OutputRoot <new-absolute-ci-folder>
```

The distribution is one self-contained Windows EXE with embedded UI and third-party notices. Extraction of .NET managed/native runtime files is automatic. Legacy client/operator sources and builds remain compatibility test inputs, not an additional release distribution requirement. Build products are ignored and must not be committed.

The icon is generated locally from `assets/remsup.svg`; the checkout contains no binary image or EXE artifacts. [Architecture and configuration](docs/architecture.md), [usage](docs/usage/README.md), [security](SECURITY.md), [licensing/provenance](THIRD_PARTY_NOTICES.md) and the [support skill](skills/remsup-support/SKILL.md) describe the supported scope.

## Configuration

The default relay is the reserved `relay.example.invalid`, with an all-zero placeholder pin that is not a certificate trust anchor. Configure a real relay and verified SPKI SHA256 pin in Settings > Network. Packagers may provision `REMSUP_RELAY_HOST` and `REMSUP_RELAY_PIN` before starting the process. Port 443 is the default; the Settings port is configurable. No production endpoint is built in.

Updates use `updates.example.invalid:8444` and an all-zero placeholder pin. Set `REMSUP_UPDATE_HOST`, `REMSUP_UPDATE_PORT` and `REMSUP_UPDATE_PIN` to an administrator-provisioned pinned HTTPS service before startup, or disable automatic checks in Settings. The updater accepts only fixed release resources, validates hashes and uses a rollback-capable single-EXE handoff. No update service or release is supplied here.

## Source export

`export-allowlist.json` enumerates every reviewed source file. `python scripts/scan-public.py` checks tracked content without printing matched credentials. Add `--review` to inventory address/version literals, device-like numbers, emails, URLs and SSH routes by location/category for manual review. Synthetic fixtures and schema/vendor URLs need contextual review. `python scripts/export-source.py <new-directory>` exports committed allowlisted blobs only, without `.git`. This scanner supplements manual review; it does not prove absence of every possible secret.

Remsup source and original artwork are licensed under the [MIT License](LICENSE), copyright 2026 ID-Yo. Dependencies retain their own terms; see [third-party notices](THIRD_PARTY_NOTICES.md).

## Runtime diagnostics and remote settings

[Read-only runtime diagnostics](docs/architecture/runtime-diagnostics.md) reports bounded readiness and updater outcomes without credentials. [Remote ordinary settings editing](docs/architecture/remote-settings-permission.md) requires a local owner opt-in, OFF by default, and an authenticated SCREEN control lease. Permission changes, credentials, firewall and administrator actions remain local.
