# IPC Contract v1

**Protocol**: `llmw-writing-ipc/1`  
**Transport**: Windows Named Pipes

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## Pipe names

```text
llmw-<app-id>-<workspace-instance-id>-core
llmw-<app-id>-<workspace-instance-id>-runtime
llmw-<app-id>-<workspace-instance-id>-w-<launch-binding-id>
```

Use current-user-only restriction and non-inheritable handles on UI and Runtime endpoints. Worker endpoints are Core-owned per launch binding: one duplex connection per Worker, ACL'd to the current user plus the sandbox AppContainer SID of that Worker. Each endpoint admits at most one authenticated connection at a time.

## Bootstrap auth

- CSPRNG ≥256-bit secret.
- never command-line.
- preferred: inherited anonymous bootstrap pipe/handle from launcher.
- fallback: directly spawned child-only environment value, cleared immediately.
- separate UI/Core and Runtime/Core secret.
- rotate on reconnect/restart:
  - Core loads the launcher-provided secret at process start and clears the child environment.
  - Rotation is **staged**, not committed at `Authenticate` time.
  - A successful Hello **issues** `HelloAck.rotatedBootstrapToken` as a one-generation pending credential and reserves the endpoint (`activeConnections = 1`). The pre-Hello credential remains `current` until the client sends the first post-Hello frame (heartbeat, cancel, or request) on that connection, which **commits** the pending credential.
  - If the connection dies before that confirmation, Core `Release`s the reservation without committing. The client may reconnect with the pre-Hello credential **or** the issued pending credential. A new Hello replaces the unused pending credential. This recovers HelloAck loss without keeping an unlimited old-token window.
  - After a committed rotation, the previous current credential is no longer accepted.
  - Rotation happens only after protocol negotiation, bootstrap token comparison, and `clientKind` binding all succeed. A rejected Hello (wrong token, wrong `clientKind`, or no common protocol) must not consume or rotate the current secret.
  - A second concurrent Hello on the same endpoint is `AUTH_BOOTSTRAP_REPLAY` and disconnects.
  - After Core process restart the launcher-provided environment secret is loaded again. A client that still holds only a previous rotated token may retry **once** with the original launcher secret after `AUTH_BOOTSTRAP_REJECTED`; it must not treat that retry as a business-mutation replay.
- `Hello.processInstanceId` is routing/provenance only and is never authentication truth.

## Framing

`uint32 little-endian length` + exact UTF-8 JSON bytes. Max 1 MiB. Zero-length, oversize (rejected before body allocation), truncated header/body, malformed UTF-8/JSON, unsupported protocol, or unknown `semanticType` => structured protocol error then disconnect. After an oversize/malformed frame the connection is not treated as still synchronized. Large content is referenced by BlobRef.

## Envelope

```json
{
  "protocolVersion":1,
  "messageType":"request|response|event|control",
  "semanticType":"hello",
  "requestId":"uuidv7",
  "correlationId":"uuidv7",
  "projectId":"uuidv7",
  "workspaceInstanceId":"...",
  "runId":"optional",
  "timestampMs":0,
  "payload":{}
}
```

`messageType` is only the generic envelope class. `semanticType` is the required stable semantic discriminator. Payloads are selected from `semanticType`; implementations must not guess DTO type from JSON shape, must not use reflection over Domain types, and must not trust CLR type names from the peer.

Envelope `projectId` / `runId` / Hello `processInstanceId` are routing claims until Core cross-checks them against trusted connection/session state. They never replace `AuthenticatedChannelContext` or `CallerPrincipal`.

## Handshake

Client control `semanticType=hello` payload `Hello{protocolMin,protocolMax,bootstrapToken,clientKind,processInstanceId}`.

Server control `semanticType=helloAck` payload `HelloAck{negotiatedProtocol,serverCapabilities,eventStreamId,connectionId,rotatedBootstrapToken}`.

No version overlap => `IPC_PROTOCOL_NO_COMMON_VERSION` and does not rotate the bootstrap secret. Wrong `clientKind` or bootstrap => `AUTH_BOOTSTRAP_REJECTED` without rotation. Handshake completes before ordinary commands are accepted.

v1 `serverCapabilities`: `heartbeat`, `multiplex`, `snapshot`, `cancel`, `events`.

`eventStreamId` identifies the Core process in-memory event stream epoch. Sequence numbers from a different `eventStreamId` must not be compared.

## Public DTO naming

`XxxRequest/XxxResponse/XxxEvent`; one-to-one with public Core command/query. Initial set includes OpenProject, GetProjectState, SubmitCandidate, CancelSubmission, AcceptAuthority, ApplyNarrativeChangeSet, RegisterProjectFile, ReconcileRegistryEntry, SearchNarrative, RestoreHistoryEntry, ActivateExtension, CreateRunSession, RevokeRunSession.

WP11 transport additions: GetStateSnapshot, SubscribeEvents, Cancel, GapEvent, CoreNoticeEvent.

WP12 Runtime scheduler additions: LoadSchedulerSnapshot, CreateWorkflowRun, CreateRun, CreateTask, DispatchReadyTask, CancelRuntimeScope, RetryTask, PersistCheckpoint, ClassifyResume, LaunchRunWorker, ReleaseRunWorker, ReconcileRunWorkers, SpawnChildRun.

Known but not-yet-implemented business commands return structured `IPC_COMMAND_UNAVAILABLE` without executing. Unknown `semanticType` returns `IPC_UNSUPPORTED_SEMANTIC_TYPE` and disconnects.

IPC DTOs never serialize Domain entities directly.

## Error

```json
{"code":"AUTH_ILLEGAL_TRANSITION","message":"...","details":{},"retryable":false}
```

Families: IPC, AUTH, REGISTRY, SECURITY, PROJECT, AGENT, PROVIDER, EDITOR, STORAGE.

WP11 codes include: `IPC_UNSUPPORTED_SEMANTIC_TYPE`, `IPC_DUPLICATE_REQUEST`, `IPC_UNKNOWN_CORRELATION`, `IPC_QUEUE_OVERLOAD`, `IPC_RESYNC_REQUIRED`, `IPC_COMMAND_UNAVAILABLE`, `IPC_PROTOCOL_VIOLATION`, `IPC_CANCELLED`, `AUTH_BOOTSTRAP_REPLAY`, `SECURITY_INVALID_SESSION`, `SECURITY_SESSION_EXPIRED`, `SECURITY_SESSION_REVOKED`, `SECURITY_BINDING_MISMATCH`, `SECURITY_TRUSTED_BINDING_UNAVAILABLE`.

WP12 codes include: `SECURITY_RUNTIME_MANAGEMENT_DENIED`, `AGENT_SPAWN_DENIED`, `AGENT_DEPTH_LIMIT`, `AGENT_DEPTH_SPOOF`, `AGENT_UNKNOWN_SIDE_EFFECT`, `AGENT_CHECKPOINT_UNSUPPORTED`, `AGENT_ILLEGAL_TRANSITION`.

Do not include bootstrap tokens, RunSession tokens, secrets, full stack traces, or filesystem security paths in error messages.

## JSON

System.Text.Json source-generated metadata; camelCase properties; string/camelCase enums. Unknown optional fields ignored. Unknown semantic message type => protocol error then disconnect.

## Multiplexing and backpressure

One reader owns frame parsing per connection. One serialized write pump emits frames; callers must not write header/body pairs concurrently.

Multiple in-flight requests are allowed (max 32). Responses may arrive out of request order and are correlated by `requestId` / `correlationId`. Duplicate active request IDs => `IPC_DUPLICATE_REQUEST`. Unknown response correlation is ignored for trust purposes and surfaced as `IPC_UNKNOWN_CORRELATION` on the control path where a caller is waiting.

Traffic classes:

| Class | Capacity | Saturation |
|---|---|---|
| Response / protocol-critical control / heartbeat / cancel | 64 | fail-closed: `IPC_QUEUE_OVERLOAD` then disconnect; never silent drop |
| Snapshot / resync | 8 | fail-closed for that snapshot; never silent drop |
| Ordinary events | subscriber ring 256 | GapEvent + NeedsResync; never block Authority Core |

A slow event subscriber must not block Authority progress. Event flood must not starve responses, cancel, heartbeat, or snapshot.

Outbound pipe writes use a bounded cancellable lifetime (`WriteTimeoutMs` = 2000). Connection shutdown cancels in-flight writes and best-effort-drains fatal protocol errors for at most `DrainTimeoutMs` = 2000, then disposes the connection so the endpoint can accept again. Authority publish never waits on a stalled peer.

## Cancellation

Control `semanticType=cancel` payload `Cancel{correlationId}` is best effort. Response state is one of `unknown`, `cancelling`, `cancelled`, `alreadyCompleted`. It never claims rollback after Authority commit. Duplicate cancel is idempotent. Cancel of an unknown correlation returns `unknown` without disconnecting. Cancellation does not bypass final security rechecks.

## Events / Gap / snapshot

Core owns the event sequence for one `eventStreamId` (per Core process instance).

- First ordinary event `seq` = 1. `snapshotSeq = 0` means no ordinary events are covered.
- Ordinary events are strictly increasing by 1 with no silent skips.
- Subscriber ring capacity = 256 retained ordinary events.
- Overflow of a live subscriber emits one `GapEvent{eventStreamId,fromSeq,toSeq}` where `fromSeq` and `toSeq` are **inclusive** of the exact missing ordinary seq range, then the subscription enters `NeedsResync`.
- Repeated overflow coalesces into that single outstanding gap; Core does not enqueue an unbounded GapEvent list.
- While `NeedsResync`, later ordinary events are not presented as a complete trustworthy stream.
- Snapshot/resync uses the snapshot traffic class and must still make progress when the ordinary event path is saturated.

Reconnect:

```text
connect → bootstrap authenticate → Hello/HelloAck (eventStreamId)
→ GetStateSnapshot(lastKnownSeq, lastEventStreamId)
→ snapshotSeq + eventStreamId
→ SubscribeEvents(eventStreamId, afterSeq=snapshotSeq)
→ resume seq > snapshotSeq
```

If `lastEventStreamId` does not match the current epoch, Core sets `resyncRequired=true` and the client must not treat previous seq values as missing messages to replay. Snapshot payloads are typed transport DTOs, never Domain entities, and are not Authority Source of Truth.

The production Runtime reconnect loop (`IpcReconnectClient`) must perform this snapshot/subscribe restore after every successful Hello. It must keep `lastKnownSeq` / `lastEventStreamId` across reconnects, treat `GapEvent` and local client-buffer overflow as `NeedsResync`, and must not present later ordinary events as a continuous stream until snapshot/resubscribe completes.

Client event delivery uses a bounded buffer (`ClientEventBufferCapacity` = 32). The pipe reader must not `Wait` on that buffer. If an ordinary event cannot be retained, the client records an explicit local discontinuity (`NeedsResync`) rather than silently dropping a sequence number. Recovery is the same snapshot/resubscribe path. A slow application consumer must not block responses, heartbeat, cancel, or Authority publish.

Safe automatic replay after reconnect: Hello, heartbeat, GetStateSnapshot, SubscribeEvents. Business mutations are not auto-replayed.

## Heartbeat

Default 5s; 3 missed => evict. Heartbeat is a control message and coexists with in-flight requests under multiplexing.

Reconnect backoff: 100ms exponential to 5s max + jitter.

## RunSession binding and TTL

Agent command includes Core-issued opaque handle (`RunSessionProof`). Core checks authenticated channel/session + token hash + runId + workerInstanceId + project scope + expiry/revocation + durable run + durable role reload + current Runtime Permission. Caller-supplied role/capability/project root is ignored for authorization.

`CreateRunSessionRequest.expiresAtMs` is an optional **requested upper bound**. Core owns:

- `DefaultTtl` = 1 hour
- `MaximumTtl` = 8 hours
- `DefaultTtl <= MaximumTtl`

Issuance:

```text
requested = expiresAtMs present ? fromUnix(expiresAtMs) : now + DefaultTtl
if requested <= now → fail
actualExpiresAt = min(requested, now + MaximumTtl)
```

Persisted and returned expiry is `actualExpiresAt`. A huge caller timestamp cannot exceed `MaximumTtl`. Session issuance without a Core-owned trusted launch record / channel binding fails closed (`SECURITY_TRUSTED_BINDING_UNAVAILABLE`). Principal kind is Core-owned: Runtime cannot become `USER_INTERACTIVE`; IPC cannot select `CORE_INTERNAL`.

## WP12 Runtime scheduler commands

These are coarse Runtime-orchestration operations. They are not generic SQL/CRUD, do not serialize Domain entities, and do not expose `ISandboxHost`, raw SQLite, trusted-binding mutation, or caller-selected principals.

Authenticated Agent Runtime channel + Core-owned Runtime launch binding is required. They do **not** mint `CORE_INTERNAL`.

`openProject` binds an **existing** valid LLMW project. `RequestedPath` is a user request, not Project identity and not sandbox truth. Core canonicalizes the path, rejects reparse/junction escape, requires `<root>/project.llmw.json`, reads `projectId` from that descriptor (UUIDv7), validates `formatVersion`/`schemaVersion` = 1, and opens only `<root>/.llmw/project.db` that is already schema v1. Future/unsupported descriptor or DB versions are refused without mutation. OpenProject does not create a Project, does not create `project.db`, and does not derive `ProjectId` from the path.

Before a successful OpenProject, Agent Runtime `createRunSession` fails closed (`SECURITY_TRUSTED_BINDING_UNAVAILABLE`) and `spawnChildRun` cannot obtain a Core-issued `RunSessionProof`. After successful existing-project composition, Core publishes the **same** project `RunSession` service to the already-running Agent Runtime IPC server, the scheduler, and every Worker IPC server. A Runtime connection established before OpenProject must resolve that live service without reconnect. A later Runtime reconnect uses the same project authority; a disconnected channel's sessions remain revoked. Runtime remains project-scoped (no fixed `BoundRunId`). Worker issuance stays `LaunchBindingId` → `BoundRunId`.

OpenProject does **not** establish Project Trust, tool grants, extension grants, or product capability enablement. A valid Core-issued `RunSessionProof` lets `spawnChildRun` reach ordinary `Agent.Spawn` evaluation; missing authoritative trust/grant layers fail closed (`AGENT_SPAWN_DENIED`). Project content/open success is not Project Trust.

`createRun` is **root Run** management creation. `ParentRunId` MUST be null; a non-null parent is `AGENT_SPAWN_DENIED` and creates no row. Child Runs are created only by `spawnChildRun` after a Core-issued parent `RunSessionProof`, parent Run/task identity, `Agent.Spawn`, cancellation/UNKNOWN gates, derived depth, shared budget, and `child.WorkflowRunId == parent.WorkflowRunId`. Callers cannot choose `depth`.

Capacity-full but otherwise authorized `spawnChildRun` persists a durable child Run with `status=queued` and returns that `childRunId`. It does not launch a Worker and does not occupy a dispatch slot. Restart/rebuild must observe the same queued child.

`launchRunWorker` requires a Scheduler dispatch reservation: Run `starting`, Task `running`, Attempt `starting`, with matching identities. It is not an independent launch API and must not create a Worker for a Run that never dispatched. Adaptive budget decrease does not revoke an already-reserved Starting Run.

Worker trusted launch records are keyed by a Core-owned `launchBindingId` (16 hex), not by `AuthenticatedClientKind` alone. Each record may carry a composition-owned `BoundRunId`. A Worker cannot bind by kind, cannot select another Worker's record via payload `runId`/`workerInstanceId`/`channelId`, and cannot issue a `RunSession` for a different Run than `BoundRunId`.

| semanticType | Request | Response |
|---|---|---|
| `loadSchedulerSnapshot` | optional `workflowRunId` | rebuildable snapshot + READY/blocked projection |
| `createWorkflowRun` | optional `storylineId` | durable workflow identity |
| `createRun` | `workflowRunId`, `role`; `parentRunId` must be null | root run only (`depth=0`); non-null parent denied |
| `createTask` | `runId`, `taskKind`, `priority` | durable task |
| `dispatchReadyTask` | `taskId` | atomic READY→RUNNING + Attempt, or `queued` |
| `cancelRuntimeScope` | `scopeKind`=`workflowRun`\|`run`\|`task`, `scopeId` | cascade cancel |
| `retryTask` | `taskId` | same Task, new Attempt; `blockedUnknown` if UNKNOWN |
| `persistCheckpoint` | run/task + v1 payload | checkpoint id |
| `classifyResume` | run + freshness flags | `CONTINUE`/`REPLAN`/`RESTART_TASK`/`RESTART_RUN`/`BLOCK_UNKNOWN` |
| `launchRunWorker` | durable `runId`/`taskId`/`attemptId` | Core-owned `workerInstanceId`/`launchBindingId` |
| `releaseRunWorker` | Core-owned `workerInstanceId` | released |
| `reconcileRunWorkers` | empty | orphan/liveness classifications |
| `spawnChildRun` | parent run/task + optional requested depth + session | `spawned`/`queued` with durable `childRunId`, or denied |

Envelope `runId` / `workerInstanceId` / `channelId` claims never select the trusted launch record. Worker pipes are `llmw-writing-<workspace>-w-<16-hex-launchBindingId>` and admit one connection. Bootstrap for a Worker is a Core-issued one-shot secret distinct from UI/Runtime/Core bootstrap tokens.

Queue saturation returns `outcome=queued`. Missing `Agent.Spawn` returns `AGENT_SPAWN_DENIED`. Illegal depth 5 returns `AGENT_DEPTH_LIMIT`. Depth spoof returns `AGENT_DEPTH_SPOOF`. UNKNOWN side effect returns `AGENT_UNKNOWN_SIDE_EFFECT` and does not create an automatic Attempt.

## WP13 Oversight / Result / Specialist / Background commands

These are typed semantic commands. They are not generic CRUD/SQL, do not serialize Domain entities, and do not expose a user-to-Specialist direct-message API.

There is no `sendMessageToSpecialist`, `appendSpecialistInstruction`, or `forceSpecialistComplete`.

Authorization classes:

- **USER_INTERACTIVE (UI channel):** `setOversightOverride`, Specialist create/update/duplicate/validate/test-run, Background stop/list/get, `getEffectiveOversight`, `getResultArtifact`, `listPendingApprovals`, `resolveRuntimeGrill` (Author-required).
- **Agent Runtime management channel:** `createResultDependency`, `updateResultDependency`, `refreshResultDependencyStatus`, `getTaskHandoff`, query surfaces shared with UI.
- **Agent RunSession:** `submitResultArtifact`, `requestTaskCompletion`, `proposeResultDependencyChange`, `resolveRuntimeGrill` only when effective Oversight is `AGENT_DELEGATED`.

Agent cannot set `AGENT_DELEGATED` on itself. Built-in Specialist mutation is rejected (`SPECIALIST_IMMUTABLE`). Result Artifact is untrusted analysis data and does not become Canon.

`background_tasks.kind` stores versioned canonical JSON `BackgroundExecutionKindV1` (kind + execution identity). Application Oversight defaults and User Library Specialists are not stored as the global Source of Truth inside a single `project.db`.

Forward-only Oversight: `oversight_overrides.effective_after_checkpoint_id` NULL is immediately active when no matching execution is in-flight. During in-flight execution, Core stores a documented pending-bind token `pending:{overrideId}` (not a Checkpoint v1 id). Activation is execution-scoped: a Task override becomes active only after that Task's own safe checkpoint; Storyline/Project pending tokens activate per in-flight Run that has crossed a safe checkpoint after the override was created. A Run or Task created after the policy change starts under the new policy immediately and must not inherit the pinned old policy until its first checkpoint. An unrelated Task A checkpoint must not activate a Task B override. `setOversightOverride.effectiveAfterCheckpointId` is non-authoritative wire compatibility; Core ignores caller-selected existing checkpoint ids and chooses the activation boundary.

REQUIRED Result dependencies are CURRENT only when the producer Task is formally COMPLETED and the referenced Result is that completion's frozen Result with acceptable freshness. A provisional Result from a Running producer does not satisfy REQUIRED. `getTaskHandoff.edges` includes every dependency edge, including missing Results (`resultArtifactId` omitted/null) with kind, Core-derived status, freshness, and block/warning flags.

| semanticType | Request | Response |
|---|---|---|
| `requestTaskCompletion` | `taskId` + session | `outcome` pass/fail/semantic-review + failures |
| `submitResultArtifact` | task + canonical JSON columns + session | `resultArtifactId` |
| `getResultArtifact` | `taskId` | canonical Result Artifact columns |
| `getTaskHandoff` | consumer task | Result refs, optional evidence, **all** dependency edges (including missing Results) with kind/status/freshness/block/warning; transcript omitted by default |
| `createResultDependency` | consumer/producer/`required`\|`advisory`\|`optional` | dependency id + Core-derived status |
| `updateResultDependency` | orchestrator `dependencyId` + `dependencyKind` | effective kind + Core-recomputed status (caller status is not trusted) |
| `proposeResultDependencyChange` | proposed kind + reason | recorded; effective kind unchanged unless orchestrator applies |
| `refreshResultDependencyStatus` | producer/consumer | updated count |
| `getEffectiveOversight` | project/storyline/task | both axes + winning scope from Core-owned records |
| `setOversightOverride` | scope + both axes; `effectiveAfterCheckpointId` ignored | override id; USER_INTERACTIVE only |
| `listPendingApprovals` | optional run | pending tool + runtime_grill items |
| `resolveRuntimeGrill` | approval + resolution | status; author or delegated per Oversight |
| `listSpecialists` | optional scope | summaries (built-in/user/project) |
| `getSpecialist` | profile id | definition JSON |
| `createSpecialist` | scope + definition JSON | profile id or validation errors |
| `updateSpecialist` | profile id + definition | rejects built-in |
| `duplicateSpecialist` | source + target scope | new profile + base digest |
| `validateSpecialist` | definition JSON | structured errors |
| `createSpecialistTestRun` | profile | child run or `SPECIALIST_TEST_UNAVAILABLE` |
| `listBackgroundTasks` | optional owner run | durable rows |
| `getBackgroundTask` | id | status/identity/checkpoint/duration |
| `stopBackgroundTask` | id | cancels owned execution only |

WP13 error codes include: `OVERSIGHT_MUTATION_DENIED`, `COMPLETION_CONTRACT_FAILED`, `SEMANTIC_REVIEW_REQUIRED`, `RESULT_REQUIRED_STALE`, `RESULT_FROZEN`, `TASK_OWNERSHIP_DENIED`, `ILLEGAL_COMPLETION_LIFECYCLE`, `RUNTIME_GRILL_AUTHOR_REQUIRED`, `RUNTIME_GRILL_ALREADY_RESOLVED`, `RUNTIME_GRILL_OPTION_REJECTED`, `RUNTIME_GRILL_OWNERSHIP_DENIED`, `SPECIALIST_IMMUTABLE`, `SPECIALIST_VALIDATION_FAILED`, `SPECIALIST_IDENTITY_MISMATCH`, `SPECIALIST_TEST_UNAVAILABLE`, `BACKGROUND_ILLEGAL_TRANSITION`, `BACKGROUND_STOP_UNAVAILABLE`.

## WP14 Provider invocation state commands

Agent Runtime owns Prompt compile, Provider routing, Provider HTTP, streaming, and credential injection at send time. Authority Core remains the only `project.db` writer. These commands are the typed Core IPC/port for invocation provenance and Task execution truth. They are not generic CRUD/SQL, do not serialize Domain entities, and MUST NOT carry Provider API secrets, `Authorization` headers, or `x-api-key` values.

These commands inherit WP09/WP11 caller identity. Agent Runtime must present an authenticated channel plus a Core-issued RunSession. A caller-supplied `runId` is never authority: `principal.RunId`, `request.RunId`, `Task.RunId`, and (when supplied) `Attempt` ownership must agree. Missing/invalid RunSession is denied. Cross-run callers are denied.

Provider network invocation is not replayed merely because IPC reconnects. `persistProviderInvocation` is idempotent on stable `invocationId` across historical checkpoints (safe provenance persist may be retried; the HTTP send is not). `snapshotGeneration` is the Core Task snapshot generation used to compile/send, never `invocationId`. `getTaskExecutionSnapshot` is a read and may be replayed after reconnect. `authorizeToolProposal` is not auto-replayed.

No credential, sandbox, or Shell execute payload crosses this boundary. A Prompt Tool Schema means the model may propose the tool; Core RunSession/principal/capability remains the execution authority. OpenProject is not Project Trust.

| semanticType | Request | Response |
|---|---|---|
| `getTaskExecutionSnapshot` | `runId`, `taskId`, optional `attemptId` | Core-owned snapshot: ownership, attempt legality, REQUIRED frozen Results + freshness, packet digest, `snapshotGeneration` |
| `persistProviderInvocation` | stable `invocationId` + safe snapshot/record JSON (no secrets) + input digest object + snapshot generation | `checkpointId`; `idempotentReplay=true` when the same invocation was already persisted |
| `authorizeToolProposal` | run/task + tool name + arguments JSON + mapped capability name (`Shell.Execute`, `MCP.Call`, `Git.Execute`, …) + optional session proof | `authorized` / `denied` / `awaitingAuthorization`; Core evaluates RunSession + principal + that capability. Missing Core facts never authorize. |

WP14 error codes include: `TASK_OWNERSHIP_DENIED`, `RESULT_REQUIRED_STALE`, `PROVIDER_SECRET_FORBIDDEN`, `INVOCATION_IDENTITY_CONFLICT`, `SECURITY_INVALID_SESSION`, `SECURITY_BINDING_MISMATCH`, `ILLEGAL_COMPLETION_LIFECYCLE`.

Agent RunSession mutations (`submitResultArtifact`, `requestTaskCompletion`, `proposeResultDependencyChange`) require the durable Task to belong to `principal.RunId`. Envelope `runId` plus a payload TaskId from another Run is denied. `createWorkflowRun.storylineId` is persisted on `workflow_runs.storyline_id` when the Storyline exists. Runtime Grill resolution reloads the durable pause request from Checkpoint; the caller may select only a persisted option. Background stop maps by `BackgroundExecutionKind` and never falls back to cancelling the owner Run.

## WP16 Draft editor commands

These commands are the typed Core IPC for user TXT/MD Draft editing. They are not generic filesystem RPC, not Authority mutations, and not WebView bridge types. Ordinary user editing requires an authenticated USER_INTERACTIVE UI channel and a Core-issued EditorSession. A RunSession is not required. EditorSession and writer leases are Core in-memory runtime state; Core restart invalidates them. Draft bytes remain the persisted truth. Save is a business mutation and is **not** in `IsSafeToReplayAfterReconnect`.

Request `chapterId` + `draftFileName` is request data only. Core independently validates Draft root, chapter identity, extension, canonical containment, and reparse policy. Renderer/UI absolute paths are never authority. `BlobRef` identifies uploaded UTF-8 bytes; it does not select a filesystem target.

| semanticType | Request | Response |
|---|---|---|
| `openDraftEditorSession` | `chapterId`, `draftFileName`, `requestWritable` | EditorSession + relative Draft identity + digests + writable/lease |
| `getDraftEditorSessionState` | `editorSessionId` | current persisted digest/revision/lease/writable |
| `releaseDraftEditorSession` | `editorSessionId` | `released` (idempotent) |
| `beginEditorContentUpload` | session + `saveOperationId` + declared UTF-8 length + SHA-256 | `uploadId`, max chunk |
| `editorContentUploadChunk` | `uploadId`, index, count, base64 | accepted index |
| `commitEditorContentUpload` | `uploadId` | `BlobRef{digest,size,locator}` |
| `saveDraftEditorSession` | session + `saveOperationId` + expected digest + `BlobRef` | persisted digest/revision; `idempotentReplay` |

WP16 error codes include: `EDITOR_SESSION_INVALID`, `EDITOR_DOCUMENT_NOT_WRITABLE`, `EDITOR_LEASE_CONFLICT`, `EDITOR_LEASE_LOST`, `EDITOR_STALE_BASE`, `EDITOR_SAVE_IDENTITY_CONFLICT`, `EDITOR_UPLOAD_INVALID`, `EDITOR_UPLOAD_HASH_MISMATCH`, `EDITOR_DOCUMENT_TOO_LARGE`, `EDITOR_ENCODING_UNSUPPORTED`, `EDITOR_PATCH_INVALID`, `EDITOR_PATCH_SEQUENCE`, `EDITOR_RECOVERY_AVAILABLE`, `EDITOR_RECOVERY_BASE_CHANGED`, `EDITOR_SAVE_OUTCOME_UNKNOWN`.

Maximum IPC frame remains 1 MiB. Editor documents may use bounded chunked upload up to a 32 MiB UTF-8 resource-safety bound.

## BlobRef

`{digest,size,locator}` where locator is an artifact/read handle resolved by Core, never a general filesystem path capability.

## Contract tests

Golden JSON for every v1 message, semantic discriminator, optional-field compatibility, malformed frame/JSON, max size, auth rejection, protocol mismatch, multiplex correlation, gap/snapshot/epoch, cancellation, reconnect, and RunSession binding/TTL clamp.
