using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LLMW.Writing.Contracts.Ipc;

public static class IpcJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] Serialize<TPayload>(IpcEnvelope<TPayload> envelope, JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (string.IsNullOrWhiteSpace(envelope.SemanticType))
        {
            throw new ArgumentException("IPC envelopes require an explicit semanticType.", nameof(envelope));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);
        IpcFrameHeader.ValidateLength(payload.Length);
        return payload;
    }

    public static byte[] SerializeWire(IpcWireEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJsonContext.Default.IpcWireEnvelope);
        IpcFrameHeader.ValidateLength(payload.Length);
        return payload;
    }

    public static IpcEnvelope<TPayload> Deserialize<TPayload>(
        ReadOnlySpan<byte> payload,
        JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        IpcFrameHeader.ValidateLength(payload.Length);

        var json = StrictUtf8.GetString(payload);
        var envelope = JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new JsonException("IPC envelope JSON must not be null.");
        if (string.IsNullOrWhiteSpace(envelope.SemanticType))
        {
            throw new JsonException("IPC envelope JSON must include semanticType.");
        }

        return envelope;
    }

    public static IpcWireEnvelope DeserializeWire(ReadOnlySpan<byte> payload)
    {
        IpcFrameHeader.ValidateLength(payload.Length);
        var json = StrictUtf8.GetString(payload);
        var envelope = JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcWireEnvelope)
            ?? throw new JsonException("IPC envelope JSON must not be null.");
        if (string.IsNullOrWhiteSpace(envelope.SemanticType))
        {
            throw new JsonException("IPC envelope JSON must include semanticType.");
        }

        return envelope;
    }

    public static TPayload DeserializePayload<TPayload>(JsonElement payload, JsonTypeInfo<TPayload> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonSerializer.Deserialize(payload, typeInfo)
            ?? throw new JsonException("IPC payload JSON must not be null.");
    }

    public static string GetString(ReadOnlySpan<byte> utf8)
    {
        return StrictUtf8.GetString(utf8);
    }

    public static byte[] GetBytes(string json)
    {
        return StrictUtf8.GetBytes(json);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(IpcMessageTypeJsonConverter), typeof(IpcClientKindJsonConverter)])]
[JsonSerializable(typeof(IpcWireEnvelope), TypeInfoPropertyName = "IpcWireEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<HelloRequest>), TypeInfoPropertyName = "HelloRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<HelloAck>), TypeInfoPropertyName = "HelloAckEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<Heartbeat>), TypeInfoPropertyName = "HeartbeatEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<HeartbeatAck>), TypeInfoPropertyName = "HeartbeatAckEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<IpcError>), TypeInfoPropertyName = "ErrorEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CancelRequest>), TypeInfoPropertyName = "CancelRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CancelResponse>), TypeInfoPropertyName = "CancelResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetStateSnapshotRequest>), TypeInfoPropertyName = "GetStateSnapshotRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetStateSnapshotResponse>), TypeInfoPropertyName = "GetStateSnapshotResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SubscribeEventsRequest>), TypeInfoPropertyName = "SubscribeEventsRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SubscribeEventsResponse>), TypeInfoPropertyName = "SubscribeEventsResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GapEvent>), TypeInfoPropertyName = "GapEventEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CoreNoticeEvent>), TypeInfoPropertyName = "CoreNoticeEventEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateRunSessionRequest>), TypeInfoPropertyName = "CreateRunSessionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateRunSessionResponse>), TypeInfoPropertyName = "CreateRunSessionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RevokeRunSessionRequest>), TypeInfoPropertyName = "RevokeRunSessionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RevokeRunSessionResponse>), TypeInfoPropertyName = "RevokeRunSessionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<OpenProjectRequest>), TypeInfoPropertyName = "OpenProjectRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<OpenProjectResponse>), TypeInfoPropertyName = "OpenProjectResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetProjectStateRequest>), TypeInfoPropertyName = "GetProjectStateRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetProjectStateResponse>), TypeInfoPropertyName = "GetProjectStateResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SubmitCandidateRequest>), TypeInfoPropertyName = "SubmitCandidateRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SubmitCandidateResponse>), TypeInfoPropertyName = "SubmitCandidateResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CancelSubmissionRequest>), TypeInfoPropertyName = "CancelSubmissionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CancelSubmissionResponse>), TypeInfoPropertyName = "CancelSubmissionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<AcceptAuthorityRequest>), TypeInfoPropertyName = "AcceptAuthorityRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<AcceptAuthorityResponse>), TypeInfoPropertyName = "AcceptAuthorityResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ApplyNarrativeChangeSetRequest>), TypeInfoPropertyName = "ApplyNarrativeChangeSetRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ApplyNarrativeChangeSetResponse>), TypeInfoPropertyName = "ApplyNarrativeChangeSetResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RegisterProjectFileRequest>), TypeInfoPropertyName = "RegisterProjectFileRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RegisterProjectFileResponse>), TypeInfoPropertyName = "RegisterProjectFileResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReconcileRegistryEntryRequest>), TypeInfoPropertyName = "ReconcileRegistryEntryRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReconcileRegistryEntryResponse>), TypeInfoPropertyName = "ReconcileRegistryEntryResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SearchNarrativeRequest>), TypeInfoPropertyName = "SearchNarrativeRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SearchNarrativeResponse>), TypeInfoPropertyName = "SearchNarrativeResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RestoreHistoryEntryRequest>), TypeInfoPropertyName = "RestoreHistoryEntryRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RestoreHistoryEntryResponse>), TypeInfoPropertyName = "RestoreHistoryEntryResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ActivateExtensionRequest>), TypeInfoPropertyName = "ActivateExtensionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ActivateExtensionResponse>), TypeInfoPropertyName = "ActivateExtensionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<LoadSchedulerSnapshotRequest>), TypeInfoPropertyName = "LoadSchedulerSnapshotRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<LoadSchedulerSnapshotResponse>), TypeInfoPropertyName = "LoadSchedulerSnapshotResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateWorkflowRunRequest>), TypeInfoPropertyName = "CreateWorkflowRunRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateWorkflowRunResponse>), TypeInfoPropertyName = "CreateWorkflowRunResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateRunRequest>), TypeInfoPropertyName = "CreateRunRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateRunResponse>), TypeInfoPropertyName = "CreateRunResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateTaskRequest>), TypeInfoPropertyName = "CreateTaskRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateTaskResponse>), TypeInfoPropertyName = "CreateTaskResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<DispatchReadyTaskRequest>), TypeInfoPropertyName = "DispatchReadyTaskRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<DispatchReadyTaskResponse>), TypeInfoPropertyName = "DispatchReadyTaskResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CancelRuntimeScopeRequest>), TypeInfoPropertyName = "CancelRuntimeScopeRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CancelRuntimeScopeResponse>), TypeInfoPropertyName = "CancelRuntimeScopeResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RetryTaskRequest>), TypeInfoPropertyName = "RetryTaskRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RetryTaskResponse>), TypeInfoPropertyName = "RetryTaskResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<PersistCheckpointRequest>), TypeInfoPropertyName = "PersistCheckpointRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<PersistCheckpointResponse>), TypeInfoPropertyName = "PersistCheckpointResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ClassifyResumeRequest>), TypeInfoPropertyName = "ClassifyResumeRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ClassifyResumeResponse>), TypeInfoPropertyName = "ClassifyResumeResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<LaunchRunWorkerRequest>), TypeInfoPropertyName = "LaunchRunWorkerRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<LaunchRunWorkerResponse>), TypeInfoPropertyName = "LaunchRunWorkerResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReleaseRunWorkerRequest>), TypeInfoPropertyName = "ReleaseRunWorkerRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReleaseRunWorkerResponse>), TypeInfoPropertyName = "ReleaseRunWorkerResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReconcileRunWorkersRequest>), TypeInfoPropertyName = "ReconcileRunWorkersRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReconcileRunWorkersResponse>), TypeInfoPropertyName = "ReconcileRunWorkersResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SpawnChildRunRequest>), TypeInfoPropertyName = "SpawnChildRunRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SpawnChildRunResponse>), TypeInfoPropertyName = "SpawnChildRunResponseEnvelope")]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(HelloAck))]
[JsonSerializable(typeof(Heartbeat))]
[JsonSerializable(typeof(HeartbeatAck))]
[JsonSerializable(typeof(IpcError))]
[JsonSerializable(typeof(CancelRequest))]
[JsonSerializable(typeof(CancelResponse))]
[JsonSerializable(typeof(GetStateSnapshotRequest))]
[JsonSerializable(typeof(GetStateSnapshotResponse))]
[JsonSerializable(typeof(SubscribeEventsRequest))]
[JsonSerializable(typeof(SubscribeEventsResponse))]
[JsonSerializable(typeof(GapEvent))]
[JsonSerializable(typeof(CoreNoticeEvent))]
[JsonSerializable(typeof(CreateRunSessionRequest))]
[JsonSerializable(typeof(CreateRunSessionResponse))]
[JsonSerializable(typeof(RevokeRunSessionRequest))]
[JsonSerializable(typeof(RevokeRunSessionResponse))]
[JsonSerializable(typeof(OpenProjectRequest))]
[JsonSerializable(typeof(SearchNarrativeRequest))]
[JsonSerializable(typeof(SearchNarrativeResponse))]
[JsonSerializable(typeof(RunSessionProof))]
[JsonSerializable(typeof(LoadSchedulerSnapshotRequest))]
[JsonSerializable(typeof(LoadSchedulerSnapshotResponse))]
[JsonSerializable(typeof(CreateWorkflowRunRequest))]
[JsonSerializable(typeof(CreateRunRequest))]
[JsonSerializable(typeof(CreateTaskRequest))]
[JsonSerializable(typeof(DispatchReadyTaskRequest))]
[JsonSerializable(typeof(CancelRuntimeScopeRequest))]
[JsonSerializable(typeof(RetryTaskRequest))]
[JsonSerializable(typeof(PersistCheckpointRequest))]
[JsonSerializable(typeof(ClassifyResumeRequest))]
[JsonSerializable(typeof(LaunchRunWorkerRequest))]
[JsonSerializable(typeof(ReleaseRunWorkerRequest))]
[JsonSerializable(typeof(ReconcileRunWorkersRequest))]
[JsonSerializable(typeof(SpawnChildRunRequest))]
public sealed partial class IpcJsonContext : JsonSerializerContext;

public sealed class IpcMessageTypeJsonConverter : JsonStringEnumConverter<IpcMessageType>
{
    public IpcMessageTypeJsonConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

public sealed class IpcClientKindJsonConverter : JsonStringEnumConverter<IpcClientKind>
{
    public IpcClientKindJsonConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}
