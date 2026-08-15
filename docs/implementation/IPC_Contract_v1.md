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
```

Use current-user-only restriction and non-inheritable handles. One duplex connection per client. Each endpoint admits at most one authenticated connection at a time.

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

Known but not-yet-implemented business commands return structured `IPC_COMMAND_UNAVAILABLE` without executing. Unknown `semanticType` returns `IPC_UNSUPPORTED_SEMANTIC_TYPE` and disconnects.

IPC DTOs never serialize Domain entities directly.

## Error

```json
{"code":"AUTH_ILLEGAL_TRANSITION","message":"...","details":{},"retryable":false}
```

Families: IPC, AUTH, REGISTRY, SECURITY, PROJECT, AGENT, PROVIDER, EDITOR, STORAGE.

WP11 codes include: `IPC_UNSUPPORTED_SEMANTIC_TYPE`, `IPC_DUPLICATE_REQUEST`, `IPC_UNKNOWN_CORRELATION`, `IPC_QUEUE_OVERLOAD`, `IPC_RESYNC_REQUIRED`, `IPC_COMMAND_UNAVAILABLE`, `IPC_PROTOCOL_VIOLATION`, `IPC_CANCELLED`, `AUTH_BOOTSTRAP_REPLAY`, `SECURITY_INVALID_SESSION`, `SECURITY_SESSION_EXPIRED`, `SECURITY_SESSION_REVOKED`, `SECURITY_BINDING_MISMATCH`, `SECURITY_TRUSTED_BINDING_UNAVAILABLE`.

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

## BlobRef

`{digest,size,locator}` where locator is an artifact/read handle resolved by Core, never a general filesystem path capability.

## Contract tests

Golden JSON for every v1 message, semantic discriminator, optional-field compatibility, malformed frame/JSON, max size, auth rejection, protocol mismatch, multiplex correlation, gap/snapshot/epoch, cancellation, reconnect, and RunSession binding/TTL clamp.
