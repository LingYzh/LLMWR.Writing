namespace LLMW.Writing.Contracts.Ipc;

public sealed record CancelRequest(Guid CorrelationId);

public sealed record CancelResponse(Guid CorrelationId, bool Accepted, string State)
{
    public const string StateUnknown = "unknown";
    public const string StateCancelling = "cancelling";
    public const string StateCancelled = "cancelled";
    public const string StateAlreadyCompleted = "alreadyCompleted";
}

public sealed record GetStateSnapshotRequest(long LastKnownSeq, string? LastEventStreamId);

public sealed record IpcTransportSnapshot(int ProtocolVersion, string[] Capabilities);

public sealed record GetStateSnapshotResponse(
    string EventStreamId,
    long SnapshotSeq,
    bool ResyncRequired,
    IpcTransportSnapshot Snapshot);

public sealed record SubscribeEventsRequest(string EventStreamId, long AfterSeq);

public sealed record SubscribeEventsResponse(string EventStreamId, long AfterSeq, long HeadSeq);

public sealed record GapEvent(string EventStreamId, long FromSeq, long ToSeq);

public sealed record CoreNoticeEvent(string EventStreamId, long Seq, string Name, string? Detail);

public sealed record OpenProjectRequest(string RequestedPath);

public sealed record OpenProjectResponse(string ProjectId);

public sealed record GetProjectStateRequest(string ProjectId);

public sealed record GetProjectStateResponse(string ProjectId, string Status);

public sealed record SubmitCandidateRequest(string ChapterId, string DraftPath, string IdempotencyKey, RunSessionProof? Session);

public sealed record SubmitCandidateResponse(string CandidateId);

public sealed record CancelSubmissionRequest(string SubmissionId, RunSessionProof? Session);

public sealed record CancelSubmissionResponse(bool Cancelled);

public sealed record AcceptAuthorityRequest(string CandidateId, string IdempotencyKey, string AcceptedBy);

public sealed record AcceptAuthorityResponse(string CandidateId, string TransactionState);

public sealed record ApplyNarrativeChangeSetRequest(string ChangeSetId, string DecisionKind, string ActorId);

public sealed record ApplyNarrativeChangeSetResponse(string ChangeSetId);

public sealed record RegisterProjectFileRequest(string RelativePath);

public sealed record RegisterProjectFileResponse(string PathId);

public sealed record ReconcileRegistryEntryRequest(string PathId);

public sealed record ReconcileRegistryEntryResponse(string PathId, string Status);

public sealed record SearchNarrativeRequest(string Text, int Limit, RunSessionProof? Session);

public sealed record SearchNarrativeHit(
    string ObjectId,
    string ArtifactDigest,
    string SectionKey,
    string? Title,
    string CurrentStatus,
    double Rank);

public sealed record SearchNarrativeResponse(SearchNarrativeHit[] Hits);

public sealed record RestoreHistoryEntryRequest(
    string HistoryId,
    string EditorSessionId,
    string ExpectedPersistedDigest);

public sealed record RestoreHistoryEntryResponse(
    string HistoryId,
    string PersistedDigest,
    long PersistedRevision,
    bool Restored);

/// <summary>
/// Path-free extension activation command. OperationId is a UUIDv7-shaped idempotency identity;
/// the renderer cannot nominate a script, executable, filesystem target, or credential.
/// </summary>
public sealed record ActivateExtensionRequest(string ExtensionId, string OperationId);

public sealed record ActivateExtensionResponse(string ExtensionId, bool Activated, bool ProjectTrusted);

public sealed record DeactivateExtensionRequest(string ExtensionId, string OperationId);

public sealed record DeactivateExtensionResponse(string ExtensionId, bool Activated, bool ProjectTrusted);

public sealed record TrustProjectExtensionsRequest(string OperationId);

public sealed record TrustProjectExtensionsResponse(bool ProjectTrusted);

public sealed record RevokeProjectExtensionsTrustRequest(string OperationId);

public sealed record RevokeProjectExtensionsTrustResponse(bool ProjectTrusted);

public sealed record ListExtensionsRequest();

public sealed record ExtensionStatusResponse(
    string ExtensionId,
    string Kind,
    string Scope,
    string Version,
    bool Activated,
    bool Invalidated);

public sealed record ListExtensionsResponse(
    bool ProjectTrusted,
    ExtensionStatusResponse[] Extensions,
    string[] Diagnostics);
