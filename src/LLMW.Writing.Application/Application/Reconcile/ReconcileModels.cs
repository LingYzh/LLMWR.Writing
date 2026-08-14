using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Domain.Registry;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Reconcile;

public enum FileEventKind
{
    Created,
    Modified,
    Deleted,
    Renamed,
    RescanRequired
}

public enum FileEventSource
{
    NativeWatcher,
    Polling,
    StartupScan,
    FullRescan,
    Batch
}

public sealed record FileEventRecord(
    long Sequence,
    string RelativePath,
    string? OldRelativePath,
    FileEventKind Kind,
    string? ObservedDigest,
    FileEventSource Source,
    DateTimeOffset ObservedAt,
    string? SelfWriteOperationToken = null);

public sealed record SelfWriteExpectation(string RelativePath, string ExpectedPhysicalDigest);

public interface ISelfWriteOperation : IDisposable
{
    string Token { get; }
}

public interface ISelfWriteTracker
{
    ISelfWriteOperation BeginOperation(IReadOnlyList<SelfWriteExpectation> expectations);

    string? TryGetActiveToken(string relativePath);

    bool ShouldSuppress(
        string? operationToken,
        string relativePath,
        string? observedPhysicalDigest);
}

public enum ReconcileSurfaceKind
{
    NarrativeProjection,
    MachineProjection,
    ManuscriptMaterialization,
    Draft,
    Unregistered
}

public enum ReconcileClassification
{
    Unchanged,
    RegisteredModified,
    RegisteredMissing,
    UnregisteredNew,
    SuspectedRename,
    ProjectionModified,
    ProjectionMissing,
    ManuscriptMaterializationModified,
    ManuscriptMaterializationMissing,
    FileTemporarilyUnavailable,
    Ignored
}

public enum RenameConfidence
{
    None,
    Low,
    Medium,
    High
}

public sealed record RenameEvidence(
    string OldRelativePath,
    string NewRelativePath,
    string ObjectId,
    bool ObjectIdMatches,
    bool PhysicalDigestMatches,
    double PathSimilarity,
    RenameConfidence Confidence);

public sealed record ReconcileObservation(
    string RelativePath,
    ReconcileSurfaceKind SurfaceKind,
    ReconcileClassification Classification,
    string? ObjectId,
    string? TrustedPhysicalDigest,
    string? ObservedPhysicalDigest,
    string? TrustedSemanticDigest,
    string? ObservedSemanticDigest,
    RegistryRegistrationState RegistrationState,
    RegistryRetrievalAvailability RetrievalAvailability,
    RegistryReconcileState ReconcileState,
    bool NormalRetrievable,
    bool SelfWriteSuppressed,
    RenameEvidence? RenameEvidence,
    IReadOnlyList<string> Warnings);

public sealed record ReconcileScanReport(
    FileEventSource Source,
    IReadOnlyList<ReconcileObservation> Observations,
    int BatchCount,
    bool FullRescan,
    bool Cancelled = false);

public enum ReconcileError
{
    ReconcileEntryNotFound,
    ExternalMutationDetected,
    RegisteredPathMissing,
    RegistryDirty,
    RegistryMissing,
    UnregisteredUnavailable,
    ReconcileConfirmationRequired,
    RenameConfirmationRequired,
    ProjectionParseFailed,
    ProjectionIdentityMismatch,
    ProjectionSchemaMismatch,
    MaterializationDirty,
    AuthoritySurfaceDirty,
    PreconditionChanged,
    FileTemporarilyUnavailable,
    FullRescanRequired,
    BatchAlreadyActive,
    NoActiveBatch,
    ReconcileNotSupported,
    PathOutsideProject,
    InvalidPrincipal,
    CapabilityDenied,
    ApprovalRequired,
    InfrastructureFailure
}

public sealed record ReconcileFailure(ReconcileError Code, string? Detail = null);

public sealed record ReconcileResult<T>(T? Value, ReconcileFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class ReconcileResults
{
    public static ReconcileResult<T> Success<T>(T value) => new(value, null);

    public static ReconcileResult<T> Fail<T>(ReconcileError code, string? detail = null) =>
        new(default, new ReconcileFailure(code, detail));
}

public sealed record ReconcileInspection(
    ReconcileObservation Observation,
    IReadOnlyDictionary<string, string?> ParsedProjectionMetadata,
    string? CurrentAuthorityDigest,
    string? CurrentAuthorityBody,
    string? ObservedBody,
    IReadOnlyList<string> SuggestedResolutions,
    IReadOnlyList<string> Warnings);

public sealed record ConfirmNarrativeReconcileResult(
    string ObjectId,
    bool AuthorityChanged,
    ApplyNarrativeChangeSetResult? AppliedChange,
    ReconcileScanReport FinalScan);

public enum AuthoritySurfaceIssueKind
{
    RegistryDirty,
    RegistryMissing,
    PendingReconcile,
    MaterializationDirty,
    MachineProjectionDirty,
    FileTemporarilyUnavailable
}

public sealed record AuthoritySurfaceIssue(
    AuthoritySurfaceIssueKind Kind,
    string RelativePath,
    string? ObjectId,
    string Detail);

public sealed record AuthoritySurfaceHealth(
    bool IsHealthy,
    IReadOnlyList<AuthoritySurfaceIssue> Issues)
{
    public static AuthoritySurfaceHealth Healthy { get; } = new(true, []);
}

public sealed record AuthoritySurfaceHealthRequest(
    IReadOnlySet<string> ResolvingNarrativeObjectIds,
    IReadOnlyDictionary<string, string>? ExpectedObservedPhysicalDigests = null)
{
    public static AuthoritySurfaceHealthRequest Standard { get; } =
        new(new HashSet<string>(StringComparer.Ordinal));
}

public interface IAuthoritySurfaceHealthGate
{
    AuthoritySurfaceHealth Check(
        AuthoritySurfaceHealthRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpAuthoritySurfaceHealthGate : IAuthoritySurfaceHealthGate
{
    public static NoOpAuthoritySurfaceHealthGate Instance { get; } = new();

    private NoOpAuthoritySurfaceHealthGate()
    {
    }

    public AuthoritySurfaceHealth Check(
        AuthoritySurfaceHealthRequest request,
        CancellationToken cancellationToken = default) => AuthoritySurfaceHealth.Healthy;
}
