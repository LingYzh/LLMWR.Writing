# Dependency Register

All third-party dependencies require explicit human approval. Resolved transitive versions are
captured in the owning project's NuGet lock file; vulnerability state is verified with NuGet
audit during the owning work package.

| Package | Approved version | Purpose | License | Security/update owner | Replacement boundary |
|---|---:|---|---|---|---|
| `Microsoft.Data.Sqlite` | 8.0.29 | Frozen-design ADO.NET provider for the project SQLite database, migration runner, and hand-written SQL persistence adapters. | MIT | LLMW.Writing maintainers | Infrastructure SQLite adapter only |
| `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.12 | Explicit patched native SQLite bundle pin; prevents resolution to the provider's older 2.1.6 minimum. | Apache-2.0 | LLMW.Writing maintainers | Infrastructure SQLite adapter only |
| `Microsoft.WindowsAppSDK` | 2.4.0 | Current supported stable Windows App SDK / WinUI 3 host, including WebView2 APIs for the unpackaged WP15 UI process. | MIT | LLMW.Writing maintainers | `LLMW.Writing.UI` executable host only |

## Lock files

| Project | Lock file | Owns |
|---|---|---|
| `LLMW.Writing.Infrastructure` | `src/LLMW.Writing.Infrastructure/packages.lock.json` | SQLite provider graph (`Microsoft.Data.Sqlite` and the pinned native bundle) |
| `LLMW.Writing.UI` | `src/LLMW.Writing.UI/packages.lock.json` | Windows App SDK / WinUI 3 / WebView2 graph |

Do not infer the UI transitive graph from the Infrastructure lock file. Do not edit either lock file by hand; regenerate with `dotnet restore` on the owning project.

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

## WP15 Windows App SDK provenance and UI graph

First-party stable identification for `Microsoft.WindowsAppSDK` 2.4.0 (keep; do not downgrade to 2.3.1):

- Windows App SDK GitHub release: https://github.com/microsoft/WindowsAppSDK/releases/tag/v2.4.0 — published 2026-08-13 and marked as the latest stable release on the 2.x line. This is the sufficient first-party provenance for keeping 2.4.0.
- WinUI 3 GitHub release: https://github.com/microsoft/microsoft-ui-xaml/releases/tag/winui3%2Frelease%2F2.4.0

Microsoft Learn search/crawl of the downloads table may lag a GitHub stable release and is not required to list 2.4.0 for this pin to remain valid. Package existence on nuget.org is not by itself a stable-support statement.

The authoritative UI graph is `src/LLMW.Writing.UI/packages.lock.json`, produced by `dotnet restore` (not hand-edited). WP15 corrective pass recorded:

Direct: `Microsoft.WindowsAppSDK` 2.4.0.

Resolved transitives from `dotnet list src/LLMW.Writing.UI/LLMW.Writing.UI.csproj package --include-transitive`:

| Resolved package | Version | Dependency kind |
|---|---:|---|
| `Microsoft.WindowsAppSDK` | 2.4.0 | Direct |
| `Microsoft.Web.WebView2` | 1.0.3719.77 | Transitive (via `Microsoft.WindowsAppSDK.WinUI` 2.3.6) |
| `Microsoft.WindowsAppSDK.WinUI` | 2.3.6 | Transitive |
| `Microsoft.WindowsAppSDK.Runtime` | 2.4.0 | Transitive |
| `Microsoft.WindowsAppSDK.Foundation` | 2.3.9 | Transitive |
| `Microsoft.WindowsAppSDK.Base` | 2.0.4 | Transitive |
| `Microsoft.WindowsAppSDK.AI` | 2.4.4 | Transitive |
| `Microsoft.WindowsAppSDK.Search` | 2.4.4 | Transitive |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | 2.1.6 | Transitive |
| `Microsoft.WindowsAppSDK.DWrite` | 2.1.0 | Transitive |
| `Microsoft.WindowsAppSDK.ML` | 2.1.74 | Transitive |
| `Microsoft.WindowsAppSDK.Widgets` | 2.0.5 | Transitive |
| `Microsoft.Windows.AI.MachineLearning` | 2.1.74 | Transitive |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.26100.4654 | Transitive |
| `Microsoft.Windows.SDK.BuildTools.MSIX` | 1.7.251221100 | Transitive |
| `System.Numerics.Tensors` | 9.0.0 | Transitive |

`dotnet list src/LLMW.Writing.UI/LLMW.Writing.UI.csproj package --vulnerable --include-transitive` reported no known vulnerable packages against nuget.org.
