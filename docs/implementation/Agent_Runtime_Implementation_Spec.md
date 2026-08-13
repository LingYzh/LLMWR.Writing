# Agent Runtime Implementation Spec

**Status**: `EXECUTION BASELINE`

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## Object graph

```text
WorkflowRun
└─ Run (one WorkerProcess)
   ├─ Task → Attempt
   ├─ Checkpoint*
   ├─ ResultArtifact*
   ├─ Evidence*
   ├─ ToolCall*
   └─ BackgroundTask*
```

Sub-agent = child Run with separate WorkerProcess. Workers are never reused across Runs.

## Scheduler

Durable state in project DB; in-memory view is rebuildable. Task DAG uses adjacency/result-dependency rows. Ready queue uses dependency readiness + priority + stable creation order. Default concurrency 4 adaptive; max depth 4. Spawn checks depth/concurrency/Agent.Spawn capability. Cancel cascades and revokes RunSession handles. Startup cleans orphan workers and rebuilds queue.

## Completion Contract

Task completion may require deterministic conditions (artifact/schema/tests) and semantic conditions (review/uncertainty). Required Result Dependency missing/stale prevents completion. Advisory/Optional does not hard-block by default.

## Result Artifact

Fields: Task, Status, Conclusion, Findings, Evidence, Uncertainty, Diagnostics, Affected Narrative Objects, Recommended Follow-ups, freshness/provenance. Result ≠ Canon. Downstream gets Result Artifact by default; full transcript only on deep audit.

## Checkpoint v1

After tool call and semantic milestone: Plan, DAG/state, Agent state, structured compacted summary, latest 20 critical messages, truncated tool refs (256 KiB head+tail), approvals, context pointers, artifact/evidence refs, input digest set, prompt/provider/model identity. Secrets excluded.

## Freshness

Inputs include Authority revision, relevant object/artifact digests, PromptConfigId/EffectivePromptDigest, AGENTS digest, Skill digests, provider/model identity, Required Result Artifact digests.

```text
unchanged → CONTINUE
changed but plan valid → REPLAN
plan invalid → RESTART_TASK
structural invalidation → RESTART_RUN
```

Unrelated Draft changes do not automatically stale a Task.

## Unknown side effect

If side-effect completion cannot be determined, set UNKNOWN and prohibit automatic retry. UI/user must resolve before continuation.

## Runtime Grill

When approved Plan + delegated Authority no longer uniquely determines the next action: persist `PAUSED_RUNTIME_GRILL`, obtain user/delegated decision, rerun freshness, resume. Runtime may not invent a new product rule to escape the pause.

## Oversight

Override precedence Task > Storyline > Project > Application. Mode changes are forward-only at next safe checkpoint. Switching to AUTO re-evaluates old pending approvals; it does not blindly auto-approve them.

## Provider adapter

Canonical `IModelProvider` exposes capabilities + streaming generation. Canonical events include text delta, tool call, structured output, usage, warning/completion/error. Retry only retryable taxonomy; fallback must remain Content-Mode/policy compatible.

## Certification/routing

Application-level record key: provider + model + revision + shipped prompt baseline + eval dataset version. State CERTIFIED/STALE/UNCERTIFIED. Ceiling Conservative/Guarded/Adaptive. Unknown custom model => Conservative. Runtime can only downgrade.

## Prompt compiler

Layer order RuntimePolicy → BaseRole → Behavioral → ContentOverlay → ProjectContext → Skills → Workflow → Task → User. User override modifies Behavioral only. Static config creates PromptConfigId; effective static project/skill/provider compiler context creates EffectivePromptDigest. Dynamic user/task text remains Run provenance.

## Specialist profiles

Persistent: Built-in immutable, User, Project. Task/session Specialist = temporary child Run, not persistent profile. Profile stores role contract, behavioral prompt, applicable stages, requested capability subset, routing hints, enabled/version/provenance. Test Run uses normal Runtime/security paths and cannot create Narrative Authority by itself.

## Background tasks

Persist owner Run/Task, kind/status, duration, worker/tool/sub-agent identity, last checkpoint. UI may view/stop; users do not directly inject prompts into sub-agent sessions.

## Tests

Queue rebuild, max depth/concurrency, spawn denial, cancellation cascade, stale Required dependency, checkpoint classifications, UNKNOWN side effect, Runtime Grill, certification no-upgrade, provider fallback compatibility.
