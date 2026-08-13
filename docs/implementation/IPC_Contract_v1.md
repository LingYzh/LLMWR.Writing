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

Use current-user-only restriction and non-inheritable handles. One duplex connection per client.

## Bootstrap auth

- CSPRNG ≥256-bit secret.
- never command-line.
- preferred: inherited anonymous bootstrap pipe/handle from launcher.
- fallback: directly spawned child-only environment value, cleared immediately.
- separate UI/Core and Runtime/Core secret.
- rotate on reconnect/restart.

## Framing

`uint32 little-endian length` + exact UTF-8 JSON bytes. Max 1 MiB. Invalid/oversize length or malformed UTF-8/JSON => structured protocol error then disconnect. Large content is referenced by BlobRef.

## Envelope

```json
{
  "protocolVersion":1,
  "messageType":"request|response|event|control",
  "requestId":"uuidv7",
  "correlationId":"uuidv7",
  "projectId":"uuidv7",
  "workspaceInstanceId":"...",
  "runId":"optional",
  "timestampMs":0,
  "payload":{}
}
```

## Handshake

Client `Hello{protocolMin,protocolMax,bootstrapToken,clientKind,processInstanceId}`. Server `HelloAck{negotiatedProtocol,serverCapabilities}`. No version overlap => `IPC_PROTOCOL_NO_COMMON_VERSION`.

## Public DTO naming

`XxxRequest/XxxResponse/XxxEvent`; one-to-one with public Core command/query. Initial set includes OpenProject, GetProjectState, SubmitCandidate, CancelSubmission, AcceptAuthority, ApplyNarrativeChangeSet, RegisterProjectFile, ReconcileRegistryEntry, SearchNarrative, RestoreHistoryEntry, ActivateExtension, CreateRunSession, RevokeRunSession.

IPC DTOs never serialize Domain entities directly.

## Error

```json
{"code":"AUTH_ILLEGAL_TRANSITION","message":"...","details":{},"retryable":false}
```

Families: IPC, AUTH, REGISTRY, SECURITY, PROJECT, AGENT, PROVIDER, EDITOR, STORAGE.

## JSON

System.Text.Json source-generated metadata; camelCase properties; string/camelCase enums. Unknown optional fields ignored. Unknown semantic message type => protocol error.

## Cancellation

Control `Cancel{correlationId}` is best effort. It never claims rollback after Authority commit.

## Events/backpressure

Core event feed is monotonic. Subscriber ring = 256. Overflow emits `GapEvent{fromSeq,toSeq}`; client requests snapshot and resumes from returned sequence. Slow consumers never block Authority Core.

## Reconnect

100ms exponential backoff to 5s max + jitter. Reauthenticate, handshake, `GetStateSnapshot(lastKnownSeq)`, resubscribe. Agent run resume happens only after principal/session binding is restored.

## Heartbeat

Default 5s; 3 missed => evict. Tunable implementation detail.

## RunSession binding

Agent command includes Core-issued opaque handle. Core checks channel/session + token hash + runId + workerInstanceId + project scope + expiry/revocation. Caller-supplied role/capability is ignored for authorization.

## BlobRef

`{digest,size,locator}` where locator is an artifact/read handle resolved by Core, never a general filesystem path capability.

## Contract tests

Golden JSON for every v1 message, optional-field compatibility, malformed frame/JSON, max size, auth rejection, protocol mismatch, gap/snapshot, cancellation, reconnect, and RunSession binding.
