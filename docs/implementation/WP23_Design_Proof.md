# WP23 Design Proof — MSIX / Portable / CI / Performance / Release

**Status:** WP23 EXECUTION PROOF

**Accepted base:** `743ee91` (`Merge pull request #10 from LingYzh/wp22-recovery-reconstruction`)

**Branch:** `wp23-msix-portable-release`

Precedence: Product FROZEN > Architecture FROZEN > Implementation Design > Test/Fault Plan > this proof > local detail.

No Frozen conflict requiring Architecture Review was found. WP23 adds release engineering only. It does not change Authority semantics, the SQLite commit point, process ownership, IPC v1, an Authority FSM, Project Trust, the capability formula, or database schema v1. No third-party dependency was introduced.

## 1. Package architecture

```text
one Release build / one source graph
  -> self-contained win-x64 payload
       LLMW.Writing.UI.exe
       core/LLMW.Writing.Core.exe
       runtime/LLMW.Writing.AgentRuntime.exe
       worker/LLMW.Writing.Worker.exe
       web-editor/app/*
  -> MSIX staging + AppxManifest + generated visual assets
  -> Portable staging + portable.marker + data/
```

MSIX and Portable therefore execute the same UI/Application/Core/Domain/Infrastructure binaries. There is no second portable business implementation. Core and Runtime discovery first accepts the existing explicit development override, then the release-relative executable layout, then the repository development layout.

Installed mode keeps application state under the Windows LocalAppData/package location. `portable.marker` changes only the application-data root to `<executable>/data`; the native UI passes that absolute root to its child Core. Project format and Project Authority data are identical between distributions. Credential portability remains excluded: Credential Manager/DPAPI-backed provider, MCP, or Git credentials can require re-authentication after moving the Portable directory.

Version ownership remains `eng/Versions.props`. The build converts `VersionPrefix` to the required four-component MSIX identity version and emits SHA-256/size provenance in `release-manifest.json`.
All WP23 release entry points force the repository-controlled NuGet cache and verify the published Core's audited WP19 native Git SHA-256, preventing a user-level cache containing the upstream compatibility package from changing release bytes.

## 2. MSIX identity, lifecycle, and capability declaration

- Identity: `LLMW.Writing`
- Architecture: `x64`
- Minimum OS: Windows 10 1809 (`10.0.17763.0`)
- Desktop runtime: `packagedClassicApp`, `mediumIL`
- Lifecycle verification: isolated random test identity, clean install of `0.0.9.0`, upgrade to the current package version, package activation, packaged-payload project workflow, and uninstall.
- Release signing: optional `LLMW_MSIX_SIGNING_CERT_PATH` / `LLMW_MSIX_SIGNING_CERT_PASSWORD`. The packaging PowerShell process immediately converts the environment password to `SecureString` and clears its plaintext environment/local reference before starting child processes. It imports the PFX temporarily into `Cert:\CurrentUser\My`, validates private key, Code Signing EKU, validity, and exact Subject/manifest Publisher equality, signs by thumbprint, verifies the signature, and removes only certificate entries introduced by that invocation. SignTool receives no password. Signing material is never added to the repository, package payload, Core/Runtime/Worker environment, logs, renderer, or release manifest.

Capability: `runFullTrust`

Reason: the trusted WinUI desktop host must start the separate Authority Core and Agent Runtime processes and preserve the frozen desktop process topology.

Threat impact: the native desktop package remains a medium-integrity desktop trust zone. The untrusted WebView2 renderer receives no package capability, filesystem API, Core pipe, credential, shell, MCP, host object, or generic native proxy; all existing origin/schema/navigation/CSP checks remain unchanged.

Alternative considered: an AppContainer-only UWP-style application would prevent the required classic desktop child-process topology and contradict the frozen WinUI/Core/Runtime architecture. `broadFileSystemAccess`, library, network, and renderer capabilities were rejected and are asserted absent by package tests.

The package-test identity uses Windows' documented unsigned-development marker only for ephemeral CI lifecycle testing and never replaces the signed release identity. Production distribution must be signed by a certificate whose subject matches the configured Publisher.

## 3. Portable distribution

The Portable ZIP is self-contained for `win-x64`, can be extracted and launched without an installer, contains the same composite process payload as MSIX, and carries `portable.marker` plus `data/README.txt`. Tests verify executable presence, absence of MSIX identity, native UI launch, portable data-root selection, packaged Core IPC handshake, Project open, Runtime workflow/task creation, WP22 startup recovery, and schema evidence.

## 4. CI flow and artifacts

```text
PR / wp* push
  -> Hosted Core gate
       restore + web test/build + compile
       Contracts + Domain + Application + Infrastructure + UI
  -> Hosted Integration gate
       real DB/filesystem/process integration (hosted WP10 OS enforcement delegated)
  -> Release package gate
       MSIX + Portable
       package security/lifecycle/E2E
       performance regression gate
  -> artifacts
       build ZIP + build manifest
       suite logs + JSON summaries
       MSIX + Portable ZIP + release manifest
       package/E2E/performance JSON evidence
```

The existing `Windows Sandbox Security` workflow remains the mandatory self-hosted gate for Restricted Token + AppContainer + Job Object enforcement that GitHub-hosted Windows cannot provide. Any test threatening Authority correctness, trust, capability, sandbox, provenance, or recovery remains release-blocking.

## 5. Release verification

`eng/packaging/Test-Wp23Packages.ps1` verifies:

1. release-manifest SHA-256 for both artifacts;
2. Portable extraction, launch, child-process layout, data root, and no MSIX identity;
3. MSIX unpack/manifest validation and exact capability allowlist;
4. isolated install, upgrade, activation, installed-payload workflow in the package identity context, and uninstall on an elevated Windows release runner;
5. a fresh file-backed project: migration, Core start, authenticated UI/Runtime IPC, Project open, workflow/run/task creation;
6. a seeded pre-commit transaction: startup recovery convergence without Authority fabrication;
7. `PRAGMA user_version = 1` and `schema_migrations = 1`.
8. certificate-store signing, exact Publisher/Subject validation, signature verification, introduced-certificate cleanup, pre-existing-certificate preservation, and a source regression guard against SignTool `/p`.

## 6. Performance baseline and regression detection

Committed thresholds live in `eng/performance/WP23.baseline.json`. `Measure-Wp23.ps1` measures the self-contained Portable release, writes JSON evidence, and fails when any threshold is exceeded. It measures:

- cold and warm UI input-idle startup;
- cold and warm Core IPC-ready startup;
- Project open composition, which includes migration preflight, WP22 recovery, registry/repository services, extensions, editor, Git, package service, and watcher startup;
- WP22 startup recovery overhead against a clean Project open;
- fresh database migration cost.

Local reference measurement on 2026-08-26:

| Metric | Measured | Threshold |
|---|---:|---:|
| cold UI startup | 460.83 ms | 5000 ms |
| warm UI startup | 311.30 ms | 5000 ms |
| cold Core ready | 342.92 ms | 5000 ms |
| warm Core ready | 225.70 ms | 5000 ms |
| Project open | 135.67 ms | 5000 ms |
| startup recovery overhead | 10.42 ms | 1500 ms |
| migration | 89.76 ms | 3000 ms |

This is a measurement/regression baseline, not an optimization claim. Correctness gates take precedence over timing.

## 7. Security review

- Renderer privilege unchanged: no filesystem capability, broad filesystem capability, secret access, Core pipe, shell, provider, MCP, or generic native proxy.
- Release WebView DevTools remain disabled; CSP and local-origin restrictions are unchanged.
- Portable application data redirection is native-host configuration; it is not a renderer-selected path or IPC field.
- Bootstrap credentials stay child-only environment values and are cleared by Core/Runtime as before; no secret enters command-line arguments.
- Package manifest asserts no `broadFileSystemAccess`, library, or network capability.
- The release payload preserves the OS-enforced Worker sandbox; no unsandboxed fallback is added.
- Project Trust and extension activation remain separate; extracting/installing/opening a project does not execute project-provided content.

## 8. WP22 carry-forward release architecture notes

### CF-WP22-N1 — single Core owner assumption

`ProjectRecoveryCoordinator` continues to assume one Authority Core owner. WP23 documents that a future background writer, multi-instance Core, or parallel Core runtime must re-evaluate the recovery consistency window. WP23 does not change recovery architecture or process ownership.

### CF-WP22-N2 — coordinator responsibility boundary

`ProjectRecoveryCoordinator` remains Chapter Submission Recovery only. It must not grow into a generic Agent/MCP/Provider/Plugin recovery framework. WP23 adds no such recovery responsibility.

## 9. Migration

Schema unchanged. No SQL migration, schema table, IPC contract, or descriptor version changed. Verification remains:

```sql
PRAGMA user_version;                    -- 1
SELECT COUNT(*) FROM schema_migrations; -- 1
```

## 10. Known limitations

- The committed release MSIX is unsigned unless CI/release signing inputs are supplied; production distribution must sign it.
- Executable unsigned MSIX lifecycle testing requires an elevated Windows 11 release runner. Non-elevated developer machines can run every static/package/Portable/E2E/performance check but cannot complete OS deployment policy verification.
- Only `win-x64` is produced in WP23. ARM64 is not claimed.
- WebView2 Evergreen remains the default runtime; no Fixed Version bundle is shipped.
- The self-contained composite payload repeats parts of the .NET runtime in process subdirectories. It avoids an installed .NET dependency and duplicates no LLMW business logic, but artifact-size optimization remains future release engineering.
- A production update orchestrator must conservatively refuse deployment while LLMW processes/Agent Runs are active; direct administrator side-loading remains outside application control.
