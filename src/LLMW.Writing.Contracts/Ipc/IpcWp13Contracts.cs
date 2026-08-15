namespace LLMW.Writing.Contracts.Ipc;

public sealed record RequestTaskCompletionRequest(string TaskId, RunSessionProof? Session);

public sealed record RequestTaskCompletionResponse(string Outcome, string? ResultArtifactId, string[] Failures);

public sealed record SubmitResultArtifactRequest(
    string TaskId,
    string Status,
    string ConclusionJson,
    string FindingsJson,
    string EvidenceJson,
    string UncertaintyJson,
    string DiagnosticsJson,
    string FreshnessJson,
    RunSessionProof? Session);

public sealed record SubmitResultArtifactResponse(string ResultArtifactId, string Status);

public sealed record GetResultArtifactRequest(string TaskId, string? ResultArtifactId);

public sealed record GetResultArtifactResponse(
    string ResultArtifactId,
    string TaskId,
    string Status,
    string ConclusionJson,
    string FindingsJson,
    string EvidenceJson,
    string UncertaintyJson,
    string DiagnosticsJson,
    string FreshnessJson,
    long ProducedAtMs);

public sealed record GetTaskHandoffRequest(string ConsumerTaskId, bool IncludeEvidence);

public sealed record TaskHandoffEdgeDto(
    string ResultArtifactId,
    string DependencyKind,
    string DependencyStatus,
    string FreshnessState,
    bool BlocksDispatch,
    bool BlocksCompletion,
    bool HasWarning);

public sealed record GetTaskHandoffResponse(
    string ConsumerTaskId,
    string[] ResultArtifactIds,
    string[] EvidenceIds,
    TaskHandoffEdgeDto[] Edges,
    string[] Warnings,
    bool IncludeTranscript);

public sealed record CreateResultDependencyRequest(
    string ConsumerTaskId,
    string ProducerTaskId,
    string DependencyKind);

public sealed record CreateResultDependencyResponse(string DependencyId, string Status);

public sealed record UpdateResultDependencyRequest(string DependencyId, string DependencyKind);

public sealed record UpdateResultDependencyResponse(string DependencyId, string DependencyKind, string Status);

public sealed record ProposeResultDependencyChangeRequest(
    string DependencyId,
    string ProposedKind,
    string Reason,
    RunSessionProof? Session);

public sealed record ProposeResultDependencyChangeResponse(bool Recorded, string EffectiveKind);

public sealed record RefreshResultDependencyStatusRequest(string? ProducerTaskId, string? ConsumerTaskId);

public sealed record RefreshResultDependencyStatusResponse(int UpdatedCount);

public sealed record GetEffectiveOversightRequest(string? ProjectId, string? StorylineId, string? TaskId);

public sealed record GetEffectiveOversightResponse(
    string NarrativeAuthority,
    string RuntimePermissionMode,
    string WinningScope,
    string? WinningScopeId,
    bool Active);

public sealed record SetOversightOverrideRequest(
    string ScopeKind,
    string? ScopeId,
    string NarrativeAuthority,
    string RuntimePermissionMode,
    string? EffectiveAfterCheckpointId);

public sealed record SetOversightOverrideResponse(string OverrideId, bool Active);

public sealed record ListPendingApprovalsRequest(string? RunId);

public sealed record PendingApprovalDto(
    string ApprovalId,
    string RunId,
    string? TaskId,
    string ApprovalKind,
    string Status,
    string? PayloadDigest);

public sealed record ListPendingApprovalsResponse(PendingApprovalDto[] Items);

public sealed record ResolveRuntimeGrillRequest(
    string ApprovalId,
    string Resolution,
    string? Option,
    RunSessionProof? Session);

public sealed record ResolveRuntimeGrillResponse(string Status, string Resolution, string? ResumeDecision);

public sealed record ListSpecialistsRequest(string? ScopeKind);

public sealed record SpecialistSummaryDto(
    string ProfileId,
    string ScopeKind,
    string Name,
    string DisplayName,
    int Version,
    bool Enabled);

public sealed record ListSpecialistsResponse(SpecialistSummaryDto[] Items);

public sealed record GetSpecialistRequest(string ProfileId, string? ScopeKind);

public sealed record GetSpecialistResponse(string ProfileId, string ScopeKind, string DefinitionJson, bool Enabled);

public sealed record CreateSpecialistRequest(string ScopeKind, string DefinitionJson);

public sealed record CreateSpecialistResponse(string ProfileId, string[] ValidationErrors);

public sealed record UpdateSpecialistRequest(string ProfileId, string ScopeKind, string DefinitionJson);

public sealed record UpdateSpecialistResponse(string ProfileId, string[] ValidationErrors);

public sealed record DuplicateSpecialistRequest(string ProfileId, string SourceScopeKind, string TargetScopeKind);

public sealed record DuplicateSpecialistResponse(string ProfileId, string BaseDefinitionDigest);

public sealed record ValidateSpecialistRequest(string DefinitionJson);

public sealed record ValidateSpecialistResponse(bool Valid, string[] Errors);

public sealed record CreateSpecialistTestRunRequest(string ProfileId, string ScopeKind);

public sealed record CreateSpecialistTestRunResponse(string Outcome, string? ChildRunId);

public sealed record ListBackgroundTasksRequest(string? OwnerRunId);

public sealed record BackgroundTaskDto(
    string BackgroundTaskId,
    string OwnerRunId,
    string? OwnerTaskId,
    string Kind,
    string Status,
    string? ExecutionJson,
    string? CheckpointId,
    long StartedAtMs,
    long? CompletedAtMs,
    long? DurationMs);

public sealed record ListBackgroundTasksResponse(BackgroundTaskDto[] Items);

public sealed record GetBackgroundTaskRequest(string BackgroundTaskId);

public sealed record GetBackgroundTaskResponse(BackgroundTaskDto Task);

public sealed record StopBackgroundTaskRequest(string BackgroundTaskId);

public sealed record StopBackgroundTaskResponse(bool Stopped, string Status);
