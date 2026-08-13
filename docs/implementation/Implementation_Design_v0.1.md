# LLMW.Writing Implementation Design v0.1 — Execution Baseline

**Date**: 2026-08-13  
**Status**: `IMPLEMENTATION DESIGN EXECUTION BASELINE`  
**Architecture baseline**: `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md`  
**Decision baseline**: `Phase 4 — Implementation Design Grilling Decisions v0.1` (Q123–Q154)

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## 0. Compilation verdict and freeze-safe normalization

The Phase 4 answers are internally compatible with the frozen Architecture; no Root Architecture Conflict was found. Three wording-level normalizations are applied without changing intent:

1. IPC bootstrap secrets are never placed in command-line arguments. Preferred delivery is an inherited anonymous bootstrap channel; a directly spawned child-only environment value may be used as fallback and cleared immediately after read.
2. AppContainer project access is implemented using AppContainer SID + minimum NTFS ACL grants and broker/path policy. The implementation does not assume a generic arbitrary-folder “capability” API.
3. Git integration remains `IGitService` + LibGit2Sharp, but the binding/native libgit2 pair must pass compatibility/security tests and use a currently patched native baseline.

## 1. Non-negotiable implementation invariants

- Authority Core is the only Project Authority writer.
- SQLite commit is the only Authority linearization point.
- Current Manuscript and Git projections are recoverable materializations, not direct Authority.
- Draft is concurrently editable; one physical Draft file has one writer lease.
- Registry availability is a mandatory retrieval gate.
- Narrative Authority delegation is separate from machine/tool permission.
- Core independently verifies CallerPrincipal/RunSession/capability.
- Shell/arbitrary executable requires OS-enforced isolation and fails closed.
- WebView2 renderer is untrusted and receives only a narrow typed bridge.
- Model certification defines a ceiling; runtime may downgrade, never upgrade.

## 2. Repository topology

```text
LLMW.Writing/
├─ LLMW.Writing.sln
├─ Directory.Build.props
├─ Directory.Packages.props
├─ global.json
├─ pnpm-workspace.yaml
├─ build.ps1
├─ AGENTS.md
├─ eng/Versions.props
├─ docs/{product,architecture,implementation,adr}/
├─ src/
│  ├─ LLMW.Writing.Contracts/
│  ├─ LLMW.Writing.Domain/
│  ├─ LLMW.Writing.Application/
│  ├─ LLMW.Writing.Infrastructure/
│  ├─ LLMW.Writing.Core/
│  ├─ LLMW.Writing.AgentRuntime/
│  ├─ LLMW.Writing.Worker/
│  ├─ LLMW.Writing.UI/
│  └─ web-editor/
├─ tests/
│  ├─ LLMW.Writing.Domain.Tests/
│  ├─ LLMW.Writing.Application.Tests/
│  ├─ LLMW.Writing.Infrastructure.Tests/
│  ├─ LLMW.Writing.Contracts.Tests/
│  ├─ LLMW.Writing.IntegrationTests/
│  ├─ LLMW.Writing.E2E.Tests/
│  ├─ fixtures/
│  └─ corpus/
└─ tools/{db,fault-injection,project-inspector}/
```

Detailed scaffold: `Repository_Scaffold_Spec.md`.

## 3. Assembly boundary

```text
Domain
  ↑
Application
  ↑
Infrastructure

Contracts ← UI / Core / AgentRuntime / Worker
```

- Domain has zero UI/DB/IPC/Provider/MCP/OpenXML/Git/Windows-sandbox dependencies.
- IPC DTOs are contract records, never Domain entities serialized directly.
- SQLite/OpenXML/libgit2/MCP/provider native/SDK types do not escape Infrastructure adapters.
- Executable projects do not reference other executable implementations.
- `InternalsVisibleTo` is test-only.

## 4. Bounded contexts

| Context | Primary ownership |
|---|---|
| Authority | transactions, FSM, candidate/review/acceptance/snapshot/barrier, NarrativeChangeSet, oversight authority |
| Narrative | Storyline/Arc/Chapter/NarrativeObject, obligation/dependency semantics |
| Registry | registration, trusted baseline, retrieval availability, reconcile |
| Security | CallerPrincipal, Capability, hard deny, ProjectTrust, executable activation |
| AgentRuntime | WorkflowRun/Run/Task/Attempt, scheduler, checkpoint, provider, prompt, specialist |
| Projection | deterministic project files and parser/materializer |
| Editor | document operations, same-file lease, TXT/MD/DOCX abstractions |
| Identity | Project/Workspace/Run/Task/Object identities |
| Infrastructure | SQLite, blobs, filesystem, Git, OpenXML, MCP, providers, Windows sandbox |

God-services are prohibited. Public mutation is expressed as a narrow Command/handler.

## 5. CQRS-lite contract

Expected business failures return typed Results, not exceptions. Queries are side-effect-free. Commands carry CallerPrincipal, optional idempotency key, and CancellationToken. Domain Event is in-process only; Authority Event is durable audit/provenance.

## 6. Persistence

- `Microsoft.Data.Sqlite` for DB access.
- no EF Core.
- parameterized hand-written SQL for writes.
- Dapper/source-generated/manual record mapping for reads.
- one transaction-context object is shared across all stores participating in an Authority transaction.
- file-backed SQLite is mandatory for WAL/crash tests.
- FTS5 is external-content and rebuildable; index failure does not roll back already committed Authority.

Physical v1 schema: `Database_Schema_v1.md`.

## 7. Authority transaction pipeline

```text
validate principal + eligibility + lock
→ create PENDING transaction record
→ stage/verify immutable blobs
→ BEGIN SQLite transaction
→ mutate Authority state
→ append Authority Events
→ update current pointers
→ COMMIT SQLite          ← only logical commit point
→ materialize Current Manuscript
→ materialize deterministic projections
→ verify digests
→ mark COMPLETE
```

Commit succeeded + materialization failed => `COMMITTED_BUT_DIRTY`. Recovery rolls forward. Repair limit 3; exhaustion => `RECOVERY_REQUIRED`, Authority read-only.

## 8. FSM implementation

Use a reusable pure `StateMachine<TState,TEvent>` plus explicit tables for Candidate, Chapter, ProjectSubmission, RevisionBarrier, Arc, Storyline, and FinalAcceptance. The FSM evaluates legal transition/guards only; side effects stay in handlers. Narrative Change uses the same transaction coordinator. User and delegated acceptance share transitions and differ only in provenance.

## 9. Narrative Change Set

Working Change Set is durable. Operations: ADD/MODIFY/REMOVE/REINTRODUCE. Before side = revision ref + digest; After side = blob payload ref. A multi-object set commits atomically. Dependency presence assessment/Impact Analysis happens before commit. `UNCERTAIN` remains explicit. Partial apply is prohibited.

## 10. Projection

Author-facing durable files: Markdown body + strict YAML frontmatter. Machine-facing durable views: deterministic JSON.

Canonical generated profile: UTF-8, LF, NFC, fixed key order, explicit UUID, stable text enums, distinct null/missing, namespaced custom fields, schema version. Unknown compatible fields are preserved with warning. External changes always enter Reconcile.

## 11. Blob store

Path: `.llmw/objects/<first-two-hex>/<remaining-hex>`. Stage in same directory, flush, hash-verify, atomic rename, deduplicate. Metadata stays in DB. Refcount is maintained transactionally and periodic mark/sweep is the correctness fallback. Backup/archive uses snapshot blob leases to pin closure while copying.

## 12. Registry/retrieval

```text
Registry availability filter
→ FTS/search
→ context selection
```

No Registry availability => no normal retrieval. Trusted baseline updates only after Core self-write or user-confirmed reconcile.

## 13. Watcher

`FileSystemWatcher` primary; 5s polling fallback. Normalize events, default debounce 300ms. Self-write suppression uses operation token primarily and path+digest fallback. Rename loss/overflow => full rescan. Git runs inside watcher batch begin/end markers.

## 14. IPC

Named Pipes, one duplex connection/client, 4-byte LE length + UTF-8 JSON, 1 MiB cap, large data by BlobRef. Current-user restriction + bootstrap auth. Event subscriber ring = 256 with explicit GapEvent; reconnect = authenticated snapshot + event sequence. RunSessionHandle is bound to channel/session/run/worker/project and rechecked by Core.

See `IPC_Contract_v1.md`.

## 15. Oversight and capability

Two axes remain orthogonal:

```text
NarrativeDecisionAuthority = AUTHOR_CONFIRMED_REQUIRED | AGENT_DELEGATED
RuntimePermissionMode = ASK | ACCEPT_EDITS | AUTO_APPROVE_SCOPED | BYPASS_PERMISSIONS
```

User-facing MANUAL/ACCEPT_EDITS/AUTO/BYPASS maps to those axes. Override precedence: Task > Storyline > Project > Application. Changes take effect at next safe checkpoint and are forward-only.

Effective machine capability is computed by Core from Product/Role/Permission/Tool/Extension/Trust/path constraints minus Hard Denies. `Script.Execute` and scoped `Authority.Accept` are explicit capabilities. `BYPASS_PERMISSIONS` never creates Narrative Authority.

## 16. Sandbox

v1 worker shell host: Restricted Token + AppContainer/LowBox + Job Object + trusted broker. Per-project AppContainer SID gets minimum NTFS ACL to designated sandbox surfaces; per-run broker policy narrows further. Generic network is denied by default; credentials are brokered. Failure to establish isolation disables shell/script rather than falling back unsandboxed.

See `Security_Enforcement_Design.md`.

## 17. Agent runtime

Durable runtime tables are rebuilt into an in-memory scheduler. Task DAG = adjacency list + Result Dependencies. Default concurrency 4 adaptive; max depth 4. One Run = one Worker process, never reused. Child spawn checks depth/concurrency/capability. Required stale Result Dependency blocks/replans. Runtime Grill is a persisted paused state.

Checkpoint v1 includes plan/DAG/agent state/structured summary/latest 20 critical messages/truncated tool refs/approvals/context refs/evidence/input digests/prompt/provider identity. Unknown side effect is never automatically retried.

See `Agent_Runtime_Implementation_Spec.md`.

## 18. Provider/certification/prompt

Providers implement a canonical adapter interface and error taxonomy. Retry only retryable failures. Fallback is role-configured and cannot cross a content-policy incompatible provider profile.

Certification is application-level and binds provider/model/revision/prompt baseline/eval version to a maximum Conservative/Guarded/Adaptive ceiling. Unknown model => UNCERTIFIED => Conservative. Runtime can only downgrade.

Prompt compiler uses layered Canonical Prompt IR. User override replaces/appends Behavioral Prompt only. Runtime Policy/Base Role remain non-editable authority/capability contracts.

## 19. Editor

Native WinUI host is trusted; WebView2 renderer is untrusted. One local app origin, navigation allowlist, no generic host objects, typed/versioned WebMessage bridge, per-message origin/schema validation, strict CSP, release DevTools off.

TXT/MD: CodeMirror 6, renderer editing state + Core durable persistence, full-text save v1, 500ms autosave, UTF-8/LF write policy. DOCX: Open XML SDK + paragraph/run AST + anchor mapping; unsupported advanced parts preserve-only if untouched, warning if touched.

See `Editor_Implementation_Spec.md`.

## 20. Git/backup/archive/history

Git is isolated behind `IGitService`; LibGit2Sharp/native pair is security-patched and compatibility-tested. Git hooks default disabled and require Project Trust + explicit activation.

Backup = SQLite online snapshot + snapshot ID + blob lease/closure/checksum + temp→final publish + 5 rotations. Archive = filtered DB projection + reachable blobs + minimum Authority provenance stubs. Local History = shared blob store + 200 versions/file + 30 days + 2 GiB/project.

## 21. Build/CI

Single build entry `build.ps1`, web first then dotnet. pnpm workspace, pinned Node/.NET SDK, Central Package Management, Windows CI, unit/integration/E2E suites, MSIX + portable packaging, CI-only signing secrets.

## 22. Change control

Implementation-detail changes allowed without Architecture Review: helper/class names, secondary indexes, exact debounce, compatible library minor/patch, non-semantic helper filenames, performance tuning that preserves correctness.

Architecture Review required for: Authority semantics/commit point, process ownership, persistence Source of Truth, principal/capability model, Project Trust boundary, sandbox weakening, WebView trust widening, SQLite↔projection authority relationship, certification ceiling rule.

## 23. Phase 4 document set

- `Implementation_Design_v0.1.md`
- `Repository_Scaffold_Spec.md`
- `Database_Schema_v1.md`
- `IPC_Contract_v1.md`
- `Security_Enforcement_Design.md`
- `Agent_Runtime_Implementation_Spec.md`
- `Editor_Implementation_Spec.md`
- `Test_and_Fault_Injection_Plan.md`
- `Coding_Agent_Execution_Plan.md`
