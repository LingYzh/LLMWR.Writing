# Repository Scaffold Spec

**Status**: `EXECUTION BASELINE`

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## Root

```text
LLMW.Writing.sln
Directory.Build.props
Directory.Packages.props
global.json
pnpm-workspace.yaml
package.json
build.ps1
AGENTS.md
.editorconfig
.gitignore
eng/Versions.props
```

`eng/Versions.props` is the product-version source. NuGet dependency versions live in `Directory.Packages.props` to avoid duplicate version ownership.

## Projects

| Project | Type | Allowed references |
|---|---|---|
| LLMW.Writing.Contracts | classlib | BCL |
| LLMW.Writing.Domain | classlib | BCL |
| LLMW.Writing.Application | classlib | Domain, Contracts |
| LLMW.Writing.Infrastructure | classlib | Application, Domain, Contracts + adapters |
| LLMW.Writing.Core | exe | Application, Infrastructure, Contracts |
| LLMW.Writing.AgentRuntime | exe | Contracts + application abstractions/adapters |
| LLMW.Writing.Worker | exe | worker-safe Contracts/runtime abstractions |
| LLMW.Writing.UI | WinUI exe | Contracts + UI facade |
| web-editor | pnpm workspace | CodeMirror/editor packages |

No executable project references another executable implementation.

## Source ownership

```text
Domain/{Authority,Narrative,Registry,Security,Identity}/
Application/{Authority,Narrative,Registry,Security,AgentRuntime,Projection,Editor}/
Infrastructure/{Persistence,Projection,FileSystem,Git,Docx,MCP,Providers,Security/Windows,IPC}/
```

## Build defaults

- Nullable enabled.
- Implicit usings enabled.
- warnings-as-errors with documented whitelist.
- `.editorconfig` + analyzers.
- deterministic compiler settings where supported.
- generated serializer/IPC metadata under generated/obj only.

## Web editor

`src/web-editor/` is a pnpm workspace. Build output is staged into a generated UI asset directory and never hand-edited. `build.ps1` rebuilds it before WinUI package build.

## Tests

- Domain.Tests — pure FSM/value-object/aggregate tests.
- Application.Tests — handlers/policies with fake ports.
- Infrastructure.Tests — SQLite/blob/Git/DOCX/provider/MCP.
- Contracts.Tests — golden protocol/serialization.
- IntegrationTests — Core/Runtime/filesystem/DB.
- E2E.Tests — packaged/native workflows and sandbox.

## Dependency rule

Any new dependency requires human approval plus an entry in `docs/implementation/dependency-register.md` containing purpose, license, security/update owner, and replacement boundary. Native SDK types never escape adapter layers.

## Repository AGENTS.md minimum rules

1. read Product FROZEN, Architecture FROZEN, Implementation Design before changes;
2. do not alter frozen invariants without Architecture Review;
3. modify only the active work-package directories;
4. run required tests;
5. no new dependencies without approval;
6. no automatic Git commit/push;
7. schema/IPC/FSM/security changes are review-sensitive;
8. preserve fault-injection hooks and fixtures.

## Build entry

```text
./build.ps1 -Target Build
./build.ps1 -Target Test
./build.ps1 -Target IntegrationTest
./build.ps1 -Target Package
./build.ps1 -Target All
```

Failure in web, .NET, tests, or packaging returns non-zero and stops the pipeline.
