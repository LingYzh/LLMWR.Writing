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
    Converters = [typeof(IpcMessageTypeJsonConverter), typeof(IpcClientKindJsonConverter), typeof(EditorFormatKindJsonConverter), typeof(EditorLeaseOwnerKindJsonConverter), typeof(HistoryCheckpointTriggerKindJsonConverter)])]
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
[JsonSerializable(typeof(IpcEnvelope<RequestTaskCompletionRequest>), TypeInfoPropertyName = "RequestTaskCompletionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RequestTaskCompletionResponse>), TypeInfoPropertyName = "RequestTaskCompletionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SubmitResultArtifactRequest>), TypeInfoPropertyName = "SubmitResultArtifactRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SubmitResultArtifactResponse>), TypeInfoPropertyName = "SubmitResultArtifactResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetResultArtifactRequest>), TypeInfoPropertyName = "GetResultArtifactRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetResultArtifactResponse>), TypeInfoPropertyName = "GetResultArtifactResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetTaskHandoffRequest>), TypeInfoPropertyName = "GetTaskHandoffRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetTaskHandoffResponse>), TypeInfoPropertyName = "GetTaskHandoffResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateResultDependencyRequest>), TypeInfoPropertyName = "CreateResultDependencyRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateResultDependencyResponse>), TypeInfoPropertyName = "CreateResultDependencyResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<UpdateResultDependencyRequest>), TypeInfoPropertyName = "UpdateResultDependencyRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<UpdateResultDependencyResponse>), TypeInfoPropertyName = "UpdateResultDependencyResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ProposeResultDependencyChangeRequest>), TypeInfoPropertyName = "ProposeResultDependencyChangeRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ProposeResultDependencyChangeResponse>), TypeInfoPropertyName = "ProposeResultDependencyChangeResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RefreshResultDependencyStatusRequest>), TypeInfoPropertyName = "RefreshResultDependencyStatusRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<RefreshResultDependencyStatusResponse>), TypeInfoPropertyName = "RefreshResultDependencyStatusResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetEffectiveOversightRequest>), TypeInfoPropertyName = "GetEffectiveOversightRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetEffectiveOversightResponse>), TypeInfoPropertyName = "GetEffectiveOversightResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SetOversightOverrideRequest>), TypeInfoPropertyName = "SetOversightOverrideRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SetOversightOverrideResponse>), TypeInfoPropertyName = "SetOversightOverrideResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListPendingApprovalsRequest>), TypeInfoPropertyName = "ListPendingApprovalsRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListPendingApprovalsResponse>), TypeInfoPropertyName = "ListPendingApprovalsResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ResolveRuntimeGrillRequest>), TypeInfoPropertyName = "ResolveRuntimeGrillRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ResolveRuntimeGrillResponse>), TypeInfoPropertyName = "ResolveRuntimeGrillResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListSpecialistsRequest>), TypeInfoPropertyName = "ListSpecialistsRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListSpecialistsResponse>), TypeInfoPropertyName = "ListSpecialistsResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetSpecialistRequest>), TypeInfoPropertyName = "GetSpecialistRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetSpecialistResponse>), TypeInfoPropertyName = "GetSpecialistResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateSpecialistRequest>), TypeInfoPropertyName = "CreateSpecialistRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateSpecialistResponse>), TypeInfoPropertyName = "CreateSpecialistResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<UpdateSpecialistRequest>), TypeInfoPropertyName = "UpdateSpecialistRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<UpdateSpecialistResponse>), TypeInfoPropertyName = "UpdateSpecialistResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<DuplicateSpecialistRequest>), TypeInfoPropertyName = "DuplicateSpecialistRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<DuplicateSpecialistResponse>), TypeInfoPropertyName = "DuplicateSpecialistResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ValidateSpecialistRequest>), TypeInfoPropertyName = "ValidateSpecialistRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ValidateSpecialistResponse>), TypeInfoPropertyName = "ValidateSpecialistResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateSpecialistTestRunRequest>), TypeInfoPropertyName = "CreateSpecialistTestRunRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateSpecialistTestRunResponse>), TypeInfoPropertyName = "CreateSpecialistTestRunResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListBackgroundTasksRequest>), TypeInfoPropertyName = "ListBackgroundTasksRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListBackgroundTasksResponse>), TypeInfoPropertyName = "ListBackgroundTasksResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetBackgroundTaskRequest>), TypeInfoPropertyName = "GetBackgroundTaskRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetBackgroundTaskResponse>), TypeInfoPropertyName = "GetBackgroundTaskResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<StopBackgroundTaskRequest>), TypeInfoPropertyName = "StopBackgroundTaskRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<StopBackgroundTaskResponse>), TypeInfoPropertyName = "StopBackgroundTaskResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetTaskExecutionSnapshotRequest>), TypeInfoPropertyName = "GetTaskExecutionSnapshotRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetTaskExecutionSnapshotResponse>), TypeInfoPropertyName = "GetTaskExecutionSnapshotResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<PersistProviderInvocationRequest>), TypeInfoPropertyName = "PersistProviderInvocationRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<PersistProviderInvocationResponse>), TypeInfoPropertyName = "PersistProviderInvocationResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<AuthorizeToolProposalRequest>), TypeInfoPropertyName = "AuthorizeToolProposalRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<AuthorizeToolProposalResponse>), TypeInfoPropertyName = "AuthorizeToolProposalResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<OpenDraftEditorSessionRequest>), TypeInfoPropertyName = "OpenDraftEditorSessionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<OpenDraftEditorSessionResponse>), TypeInfoPropertyName = "OpenDraftEditorSessionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetDraftEditorSessionStateRequest>), TypeInfoPropertyName = "GetDraftEditorSessionStateRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetDraftEditorSessionStateResponse>), TypeInfoPropertyName = "GetDraftEditorSessionStateResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReleaseDraftEditorSessionRequest>), TypeInfoPropertyName = "ReleaseDraftEditorSessionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ReleaseDraftEditorSessionResponse>), TypeInfoPropertyName = "ReleaseDraftEditorSessionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<BeginEditorContentDownloadRequest>), TypeInfoPropertyName = "BeginEditorContentDownloadRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<BeginEditorContentDownloadResponse>), TypeInfoPropertyName = "BeginEditorContentDownloadResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<EditorContentDownloadChunkRequest>), TypeInfoPropertyName = "EditorContentDownloadChunkRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<EditorContentDownloadChunkResponse>), TypeInfoPropertyName = "EditorContentDownloadChunkResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<BeginEditorContentUploadRequest>), TypeInfoPropertyName = "BeginEditorContentUploadRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<BeginEditorContentUploadResponse>), TypeInfoPropertyName = "BeginEditorContentUploadResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<EditorContentUploadChunkRequest>), TypeInfoPropertyName = "EditorContentUploadChunkRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<EditorContentUploadChunkResponse>), TypeInfoPropertyName = "EditorContentUploadChunkResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CommitEditorContentUploadRequest>), TypeInfoPropertyName = "CommitEditorContentUploadRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CommitEditorContentUploadResponse>), TypeInfoPropertyName = "CommitEditorContentUploadResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SaveDraftEditorSessionRequest>), TypeInfoPropertyName = "SaveDraftEditorSessionRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<SaveDraftEditorSessionResponse>), TypeInfoPropertyName = "SaveDraftEditorSessionResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitStatusRequest>), TypeInfoPropertyName = "GetGitStatusRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitStatusResponse>), TypeInfoPropertyName = "GetGitStatusResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitDiffSummaryRequest>), TypeInfoPropertyName = "GetGitDiffSummaryRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitDiffSummaryResponse>), TypeInfoPropertyName = "GetGitDiffSummaryResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitCurrentBranchRequest>), TypeInfoPropertyName = "GetGitCurrentBranchRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitCurrentBranchResponse>), TypeInfoPropertyName = "GetGitCurrentBranchResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListGitCommitHistoryRequest>), TypeInfoPropertyName = "ListGitCommitHistoryRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ListGitCommitHistoryResponse>), TypeInfoPropertyName = "ListGitCommitHistoryResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitCommitMetadataRequest>), TypeInfoPropertyName = "GetGitCommitMetadataRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<GetGitCommitMetadataResponse>), TypeInfoPropertyName = "GetGitCommitMetadataResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CommitGitChangesRequest>), TypeInfoPropertyName = "CommitGitChangesRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CommitGitChangesResponse>), TypeInfoPropertyName = "CommitGitChangesResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateProjectBackupRequest>), TypeInfoPropertyName = "CreateProjectBackupRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<ProjectPackageResponse>), TypeInfoPropertyName = "ProjectPackageResponseEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateProjectArchiveRequest>), TypeInfoPropertyName = "CreateProjectArchiveRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<CreateFinalPackageRequest>), TypeInfoPropertyName = "CreateFinalPackageRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<VerifyFinalPackageRequest>), TypeInfoPropertyName = "VerifyFinalPackageRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<VerifyFinalPackageResponse>), TypeInfoPropertyName = "VerifyFinalPackageResponseEnvelope")]
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
[JsonSerializable(typeof(RequestTaskCompletionRequest))]
[JsonSerializable(typeof(SubmitResultArtifactRequest))]
[JsonSerializable(typeof(GetResultArtifactRequest))]
[JsonSerializable(typeof(GetTaskHandoffRequest))]
[JsonSerializable(typeof(TaskHandoffEdgeDto))]
[JsonSerializable(typeof(CreateResultDependencyRequest))]
[JsonSerializable(typeof(UpdateResultDependencyRequest))]
[JsonSerializable(typeof(ProposeResultDependencyChangeRequest))]
[JsonSerializable(typeof(RefreshResultDependencyStatusRequest))]
[JsonSerializable(typeof(GetEffectiveOversightRequest))]
[JsonSerializable(typeof(SetOversightOverrideRequest))]
[JsonSerializable(typeof(ListPendingApprovalsRequest))]
[JsonSerializable(typeof(ResolveRuntimeGrillRequest))]
[JsonSerializable(typeof(ListSpecialistsRequest))]
[JsonSerializable(typeof(GetSpecialistRequest))]
[JsonSerializable(typeof(CreateSpecialistRequest))]
[JsonSerializable(typeof(UpdateSpecialistRequest))]
[JsonSerializable(typeof(DuplicateSpecialistRequest))]
[JsonSerializable(typeof(ValidateSpecialistRequest))]
[JsonSerializable(typeof(CreateSpecialistTestRunRequest))]
[JsonSerializable(typeof(ListBackgroundTasksRequest))]
[JsonSerializable(typeof(GetBackgroundTaskRequest))]
[JsonSerializable(typeof(StopBackgroundTaskRequest))]
[JsonSerializable(typeof(GetTaskExecutionSnapshotRequest))]
[JsonSerializable(typeof(GetTaskExecutionSnapshotResponse))]
[JsonSerializable(typeof(PersistProviderInvocationRequest))]
[JsonSerializable(typeof(PersistProviderInvocationResponse))]
[JsonSerializable(typeof(AuthorizeToolProposalRequest))]
[JsonSerializable(typeof(AuthorizeToolProposalResponse))]
[JsonSerializable(typeof(FrozenRequiredResultDto))]
[JsonSerializable(typeof(OpenDraftEditorSessionRequest))]
[JsonSerializable(typeof(OpenDraftEditorSessionResponse))]
[JsonSerializable(typeof(GetDraftEditorSessionStateRequest))]
[JsonSerializable(typeof(GetDraftEditorSessionStateResponse))]
[JsonSerializable(typeof(ReleaseDraftEditorSessionRequest))]
[JsonSerializable(typeof(ReleaseDraftEditorSessionResponse))]
[JsonSerializable(typeof(BeginEditorContentDownloadRequest))]
[JsonSerializable(typeof(BeginEditorContentDownloadResponse))]
[JsonSerializable(typeof(EditorContentDownloadChunkRequest))]
[JsonSerializable(typeof(EditorContentDownloadChunkResponse))]
[JsonSerializable(typeof(BeginEditorContentUploadRequest))]
[JsonSerializable(typeof(BeginEditorContentUploadResponse))]
[JsonSerializable(typeof(EditorContentUploadChunkRequest))]
[JsonSerializable(typeof(EditorContentUploadChunkResponse))]
[JsonSerializable(typeof(CommitEditorContentUploadRequest))]
[JsonSerializable(typeof(CommitEditorContentUploadResponse))]
[JsonSerializable(typeof(SaveDraftEditorSessionRequest))]
[JsonSerializable(typeof(SaveDraftEditorSessionResponse))]
[JsonSerializable(typeof(GetGitStatusRequest))]
[JsonSerializable(typeof(GetGitStatusResponse))]
[JsonSerializable(typeof(GetGitDiffSummaryRequest))]
[JsonSerializable(typeof(GetGitDiffSummaryResponse))]
[JsonSerializable(typeof(GetGitCurrentBranchRequest))]
[JsonSerializable(typeof(GetGitCurrentBranchResponse))]
[JsonSerializable(typeof(ListGitCommitHistoryRequest))]
[JsonSerializable(typeof(ListGitCommitHistoryResponse))]
[JsonSerializable(typeof(GetGitCommitMetadataRequest))]
[JsonSerializable(typeof(GetGitCommitMetadataResponse))]
[JsonSerializable(typeof(CommitGitChangesRequest))]
[JsonSerializable(typeof(CommitGitChangesResponse))]
[JsonSerializable(typeof(CreateProjectBackupRequest))]
[JsonSerializable(typeof(CreateProjectArchiveRequest))]
[JsonSerializable(typeof(CreateFinalPackageRequest))]
[JsonSerializable(typeof(ProjectPackageResponse))]
[JsonSerializable(typeof(VerifyFinalPackageRequest))]
[JsonSerializable(typeof(VerifyFinalPackageResponse))]
[JsonSerializable(typeof(GitStatusEntryResponse))]
[JsonSerializable(typeof(GitCommitSummaryResponse))]
[JsonSerializable(typeof(GitCommitMetadataResponse))]
[JsonSerializable(typeof(IpcBlobRef))]
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
