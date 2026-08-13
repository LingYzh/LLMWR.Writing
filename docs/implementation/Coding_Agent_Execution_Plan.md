# Coding Agent Execution Plan

**Status**: `READY FOR CODING AGENT EXECUTION`

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## Mandatory pre-read

1. Product v0.5.2 FROZEN.
2. Technical Architecture v0.1 FROZEN.
3. Implementation_Design_v0.1.
4. relevant companion spec.
5. repo AGENTS.md.

Conflict with a higher-precedence baseline => stop/report, never improvise.

## Package protocol

Read package → inspect repo → produce plan → review against invariants → modify only allowed dirs → run required tests → report diff/evidence/risk. User owns Git commit/push.

New dependency requires human approval. Migration requires separate review. IPC change updates contract tests first. FSM change updates model-based tests. Security-boundary weakening triggers Architecture Review and must not proceed.

## M0 Repository/process shell

- WP00 scaffold: solution/build/eng/docs/AGENTS; clean restore/build.
- WP01 Contracts/process shells: DTO envelope, process startup, pipe hello/heartbeat; contract + reconnect smoke.

## M1 Authority minimum loop

- WP02 DB migration v1: physical schema + constraints + file-backed WAL tests.
- WP03 blob store/transaction coordinator: SHA-256 stage/verify/rename + PENDING→COMMIT→materialize + fault injector.
- WP04 pure Authority FSM: Candidate/Chapter/ProjectSubmission/Barrier model-based tests.
- WP05 chapter vertical slice: Draft→Candidate→fake Review→user accept→DB commit→Current Manuscript, including crash after commit.

## M2 Narrative/Registry/Projection

- WP06 Narrative Change/impact skeleton, atomic multi-object/UNCERTAIN.
- WP07 deterministic projection + Registry/retrieval filter.
- WP08 watcher/reconcile/startup rescan/Git batching.

## M3 Security/IPC

- WP09 CallerPrincipal/RunSession/Capability/hard deny.
- WP10 sandbox Restricted Token+AppContainer+Job+broker; security review required.
- WP11 full IPC v1 multiplex/backpressure/snapshot/cancel/session binding.

## M4 Agent Runtime

- WP12 Scheduler/checkpoint/resume/depth=4/concurrency=4 adaptive.
- WP13 Oversight/Result Artifact/Dependencies/Runtime Grill/Specialist/Background Task.
- WP14 Provider SPI + fake provider + certification + prompt compiler.

## M5 Editor

- WP15 WebView2 local origin + typed secure bridge.
- WP16 TXT/MD CodeMirror/full save/autosave/lease/crash recovery.
- WP17 DOCX adapter + 20-fixture corpus.

## M6 Project services

- WP18 Local History retention/restore.
- WP19 Git adapter/credentials/hook trust/watcher batch.
- WP20 Backup/Archive/Final Package snapshot leases/closure/provenance manifest.

## M7 Extensions/hardening

- WP21 AGENTS/Skills/Plugins/MCP activation/security.
- WP22 recovery/health/Reconstruction E2E.
- WP23 MSIX/portable/CI/performance/release.

## Parallelism

After foundational contracts stabilize: WP06 and WP09 may run in parallel; WP14 may start against frozen IPC/fakes; WP17 can proceed after Editor contract; Git/History can proceed after file/Registry APIs. Never parallelize two packages that both edit migration v1, IPC v1, Authority FSM, or Security enforcement contract.

## Completion report

Package ID; goal; files changed; dependencies; invariants touched; tests/results; fault/security cases; known limitations; packages unblocked.

## Definition of Done

Domain/application behavior + persistence/IPC versioning + Core authorization + failure/recovery + required tests + provenance/observability + user-visible error/dirty/recovery representation + docs updated.

## MVP order

Scaffold → Authority → Registry/Projection/Reconcile → Security/IPC → Agent → TXT/MD → DOCX → Git/History/Backup → Extensions/MCP → Recovery/Reconstruction → Packaging/Performance.
