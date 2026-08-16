namespace LLMW.Writing.Contracts.Ipc;

public sealed record FrozenRequiredResultDto(
    string ResultId,
    bool Required,
    bool Stale,
    bool Missing,
    string? Digest,
    string? Text);

public sealed record GetTaskExecutionSnapshotRequest(string RunId, string TaskId, string? AttemptId);

public sealed record GetTaskExecutionSnapshotResponse(
    string SnapshotGeneration,
    bool OwnershipValid,
    bool AttemptLegal,
    string? PacketDigest,
    FrozenRequiredResultDto[] RequiredResults);

public sealed record PersistProviderInvocationRequest(
    string InvocationId,
    string RunId,
    string TaskId,
    string? AttemptId,
    string SnapshotJson,
    string? RecordJson,
    string InputDigestSetJson,
    string SnapshotGeneration);

public sealed record PersistProviderInvocationResponse(string CheckpointId, bool IdempotentReplay);

public sealed record AuthorizeToolProposalRequest(
    string RunId,
    string TaskId,
    string ToolName,
    string ArgumentsJson,
    string CapabilityName,
    RunSessionProof? Session);

public sealed record AuthorizeToolProposalResponse(
    bool Allowed,
    string Status,
    string? DenialCode,
    string CapabilityName);
