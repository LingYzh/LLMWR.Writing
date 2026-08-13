# Test and Fault Injection Plan

**Status**: `MANDATORY ENGINEERING ACCEPTANCE PLAN`

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## Test tiers

Unit; Contract; Infrastructure Integration; Multi-process; Security; E2E; Fault Injection.

## Authority FSM

Model-based legal transitions and all illegal transitions. Explicit Review FAIL/Cancel. Historical Revision barrier fail/cancel/remediation. Arc/Storyline/Final Acceptance. User/delegated acceptance must create equal Authority state with different provenance.

## Atomic commit crash matrix

Inject before/after: stage blob; PENDING row; BEGIN DB; candidate/change/review/acceptance writes; events; pointer; DB COMMIT; manuscript materialize; projection materialize; verify; COMPLETE.

Invariant: pre-commit => no Authority change; post-commit => Authority committed + roll-forward; never two current revisions/active Authority transactions.

## Storage/DB

Disk full, file locked, access denied, missing/corrupt blob, orphan GC, snapshot lease vs mark/sweep, migration interruption/checksum mismatch, WAL/FULL file-backed crash, FTS drop/rebuild.

## Registry/watcher

Unregistered not retrievable; dirty baseline; confirmed reconcile; rename heuristic; lost events/overflow/offline changes; 1000-file Git storm; self-write suppression correctness.

## IPC/principal

Current-user restriction, bad bootstrap, protocol mismatch, malformed frame/JSON, 1 MiB edge, GapEvent/snapshot, reconnect, forged role/capability/runId, wrong/stolen/revoked RunSessionHandle.

## Sandbox/security

Project-outside path, junction/reparse escape, child-process escape, network denial, secret exposure, kill process tree, sandbox unavailable fail-closed, Full Accept hard-deny/trust, untrusted extension/migration/hook.

## WebView2

Wrong origin/schema rejected, navigation/frame blocked, external link native flow, host object absent, project content cannot become executable bridge command.

## Agent runtime

Scheduler rebuild, depth/concurrency, spawn denial, cancel cascade/orphan cleanup, stale Required Result, UNKNOWN side effect, Runtime Grill, checkpoint CONTINUE/REPLAN/RESTART.

## Provider/model/prompt

Retry/rate-limit/malformed/policy refusal; incompatible content-policy fallback blocked; certification stale; runtime cannot upgrade ceiling; Prompt Override cannot grant capability; SFW widening blocked/diagnosed; shipped+override upgrade; PromptConfigId golden vectors.

## DOCX

Every corpus fixture: no-edit round-trip; supported edit reopen in Word/LibreOffice; untouched unsupported preservation; touched warning; anchor mapping. Also corrupt/encrypted refusal.

## Git/backup/archive/history

Repo root/monorepo, malicious/conflict paths, hooks activation, patched native smoke, DB snapshot + blob closure checksum, GC lease, archive history exclusion + provenance stubs, pack/unpack identity, Local History Draft restore, Manuscript restore→Reconcile.

## Reconstruction

Existing manuscript/project → read-only reconstruction → structure recovery → retroactive review/acceptance → frontier → ordinary editor workflow. Ambiguous narrative structure requires user/delegated decision.

## Final package

Verify Snapshot ID, Storyline ID, Accepted Version/At, logical files, per-file digests, Final Review ID, Warnings at Acceptance, manifest version. Modified file => verification mismatch but remains usable.

## CI gates

PR: Build + Unit + Contract + fast Integration. Protected/release: plus file-backed DB crash, process IPC, sandbox/security, DOCX corpus, Git, fake MCP/provider, selected E2E. Nightly: full fault matrix, scale/performance, blob scrub/GC, full DOCX.

Any failure threatening Authority correctness, trust, capability, sandbox, provenance, or recovery is release-blocking.
