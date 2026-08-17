# Dependency Register

All third-party dependencies require explicit human approval. Resolved transitive versions are
captured in `src/LLMW.Writing.Infrastructure/packages.lock.json`; vulnerability state is verified
with NuGet audit during the owning work package.

| Package | Approved version | Purpose | License | Security/update owner | Replacement boundary |
|---|---:|---|---|---|---|
| `Microsoft.Data.Sqlite` | 8.0.29 | Frozen-design ADO.NET provider for the project SQLite database, migration runner, and hand-written SQL persistence adapters. | MIT | LLMW.Writing maintainers | Infrastructure SQLite adapter only |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.12 | Explicit patched native SQLite bundle pin; prevents resolution to the provider's older 2.1.6 minimum. | Apache-2.0 | LLMW.Writing maintainers | Infrastructure SQLite adapter only |
| `Microsoft.WindowsAppSDK` | 2.4.0 | Current supported stable Windows App SDK / WinUI 3 host, including WebView2 APIs for the unpackaged WP15 UI process. | MIT | LLMW.Writing maintainers | `LLMW.Writing.UI` executable host only |

## WP02 resolved dependency audit

The authoritative resolved graph is the adjacent NuGet lock file. WP02 completion records:

- the direct and transitive package graph from `dotnet list package --include-transitive`;
- the NuGet vulnerability audit from `dotnet list package --vulnerable --include-transitive`;
- the native SQLite version returned by the opened provider connection.

The approved bundle must remain at `2.1.12`. Compatibility failure must stop the work package;
downgrade to a known vulnerable native bundle is not permitted.

Resolved by the WP02 locked restore:

| Resolved package | Version | Dependency kind | License |
|---|---:|---|---|
| `Microsoft.Data.Sqlite` | 8.0.29 | Direct | MIT |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.12 | Direct security pin | Apache-2.0 |
| `Microsoft.Data.Sqlite.Core` | 8.0.29 | Transitive | MIT |
| `SQLitePCLRaw.core` | 2.1.12 | Transitive | Apache-2.0 |
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.12 | Transitive native runtime | Apache-2.0 |
| `SQLitePCLRaw.provider.e_sqlite3` | 2.1.12 | Transitive native provider | Apache-2.0 |
| `System.Memory` | 4.5.3 | Transitive | MIT |

Exact content hashes and dependency edges are locked in
`src/LLMW.Writing.Infrastructure/packages.lock.json`.
