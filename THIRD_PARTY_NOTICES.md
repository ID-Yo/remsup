# Licensing and provenance review

No license has been assigned to Remsup source or artwork in this snapshot. An absent license is not permission to redistribute. Keep the repository private until the owner selects terms and confirms contributor and artwork rights. Cleaning Git history does not establish ownership or remove attribution obligations.

The retained SVG was described as an original vector master in development documentation and is rendered by a local System.Drawing script. No external icon pack or font is included. An ownership/assignment statement was not found; owner attestation is still required. Development memory/template scaffolding and historical documentation were excluded, and the short public documentation was rewritten for this snapshot.

Dependencies are obtained from upstream registries; they are not vendored source. `desktop/ui/package-lock.json` and `desktop/host/packages.lock.json` pin desktop dependency versions. `docs/npm-license-inventory.json` records all lockfile-declared npm licenses, including development and optional platform packages; declarations should be checked against installed package terms. MCP/requests direct versions are pinned in requirements files, but Python transitive dependencies are not locked. Resolve and audit those versions again before shipping a Python environment.

| Dependency | Terms observed locally | Distribution |
|---|---|---|
| React, ReactDOM, scheduler | MIT | UI bundle; notices embedded by build |
| Vite | MIT plus upstream bundled notices | Build tooling; LICENSE.md embedded |
| TypeScript | Apache-2.0 | Build tooling |
| source-map-js | BSD-3-Clause | Build tooling |
| picocolors | ISC | Build tooling |
| WebView2 SDK | Microsoft WebView2 SDK license | Host dependency; SDK terms embedded |
| .NET runtime / Windows desktop runtime | MIT and runtime third-party notices | Self-contained EXE; notices embedded |
| WebView2 Evergreen Runtime | Separately installed Microsoft runtime | Not bundled; deployer/user installs it under its own terms |
| MCP Python SDK, requests | MIT, Apache-2.0 respectively | Operator/server environment, not the Windows EXE |

The build fails if required runtime notice files are absent. Review embedded notices before distributing an EXE. Package metadata and the notice inventory do not grant a license for first-party code, guarantee complete rights clearance or constitute legal advice.
