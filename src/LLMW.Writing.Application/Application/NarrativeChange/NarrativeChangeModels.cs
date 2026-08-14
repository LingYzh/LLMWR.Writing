using LLMW.Writing.Application.Authority;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Application.Security;

namespace LLMW.Writing.Application.NarrativeChange;

public enum NarrativeChangeError
{
    ChangeSetNotFound,
    ChangeSetNotApplicable,
    InvalidChangeOperation,
    ObjectNotFound,
    ObjectAlreadyCurrent,
    ObjectNotCurrent,
    BeforeRevisionMismatch,
    BeforeDigestMismatch,
    PreconditionChanged,
    PayloadMissing,
    PayloadVerificationFailed,
    DependencyAssessmentFailed,
    ImpactAnalysisFailed,
    DecisionAuthorityNotAvailable,
    AuthorityDirty,
    RecoveryRequired,
    PartialApplyForbidden,
    InvalidPrincipal,
    CapabilityDenied,
    ApprovalRequired,
    InfrastructureFailure
}

public sealed record NarrativeChangeFailure(NarrativeChangeError Code, string? Detail = null);

public sealed record NarrativeChangeResult<T>(T? Value, NarrativeChangeFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class NarrativeChangeResults
{
    public static NarrativeChangeResult<T> Success<T>(T value) => new(value, null);

    public static NarrativeChangeResult<T> Fail<T>(NarrativeChangeError error, string? detail = null) =>
        new(default, new NarrativeChangeFailure(error, detail));
}

public sealed record WorkingNarrativeChangeInput(
    string ObjectId,
    string ObjectType,
    NarrativeChangeKind ChangeKind,
    string? BeforeRevisionRef = null,
    string? BeforeDigest = null,
    Stream? AfterPayload = null,
    string? ExistingAfterPayloadDigest = null);

public sealed record CreateWorkingNarrativeChangeSetCommand(
    string ScopeKind,
    string ScopeId,
    string ProposerKind,
    string? ProposerId,
    IReadOnlyList<WorkingNarrativeChangeInput> Changes,
    CallerPrincipal? Principal = null);

public sealed record CreateWorkingNarrativeChangeSetResult(
    string ChangeSetId,
    IReadOnlyList<NarrativeChangeRecord> Changes);

public enum NarrativeDecisionKind
{
    AuthorConfirmed,
    AgentDelegated
}

public sealed record ApplyNarrativeChangeSetCommand(
    string ChangeSetId,
    string IdempotencyKey,
    NarrativeDecisionKind DeciderKind,
    string? DeciderId,
    IReadOnlyList<string>? ResolvingReconcileObjectIds = null,
    IReadOnlyDictionary<string, string>? ResolvingReconcilePhysicalDigests = null,
    CallerPrincipal? Principal = null);

public sealed record ApplyNarrativeChangeSetResult(
    string ChangeSetId,
    string TransactionId,
    string ImpactAnalysisId,
    AuthorityTransactionState TransactionState,
    bool Existing,
    IReadOnlyList<string> Warnings);

public sealed record NarrativeChangeSetSnapshot(
    string ChangeSetId,
    string ScopeKind,
    string ScopeId,
    string Status,
    string ProposerKind,
    string? ProposerId,
    string? DeciderKind,
    string? DeciderId,
    string? TransactionId,
    string? ImpactAnalysisId,
    IReadOnlyList<NarrativeChangeRecord> Changes);

public sealed record NarrativeChangeRecord(
    string NarrativeChangeId,
    string ObjectId,
    NarrativeChangeKind ChangeKind,
    string? BeforeRevisionRef,
    string? BeforeDigest,
    string? AfterPayloadDigest,
    int Ordinal);

public sealed record DependencyEdgeReference(
    string EdgeId,
    string FromObjectId,
    string ToObjectId,
    string EdgeType);

public sealed record StructuralDependencyAssessment(IReadOnlyList<DependencyEdgeReference> Edges)
{
    public bool HasRelevantDependency => Edges.Count > 0;
}

public sealed record SemanticDependencyAssessment(
    SemanticDependencyFinding Finding,
    string EvidenceJson,
    string? UncertaintyReason = null,
    string? CoverageMetadata = null);

public enum NarrativeImpactAnalysisStatus
{
    NoRelevantDependency,
    Affected,
    Uncertain,
    Failed
}

public sealed record NarrativeImpactAnalysisResult(
    NarrativeImpactAnalysisStatus Status,
    IReadOnlyList<string> AffectedObjectIds,
    IReadOnlyList<string> AffectedDependencyEdgeIds,
    string EvidenceJson,
    IReadOnlyList<string> Warnings);

public sealed record NarrativeImpactAnalysisRecord(
    string ImpactAnalysisId,
    NarrativeImpactAnalysisStatus Status,
    string AffectedSetJson,
    string EvidenceJson,
    string WarningsJson,
    IReadOnlyList<string> Warnings);

public sealed record PersistWorkingChangeSetRequest(
    string ScopeKind,
    string ScopeId,
    string ProposerKind,
    string? ProposerId,
    IReadOnlyList<NarrativeChangeDraft> Changes);

public sealed record PersistImpactAnalysisRequest(
    string ChangeSetId,
    NarrativeImpactAnalysisStatus Status,
    string AffectedSetJson,
    string EvidenceJson,
    string WarningsJson,
    IReadOnlyList<string> Warnings);

public sealed record NarrativeApplyStoreRequest(
    string ChangeSetId,
    string IdempotencyKey,
    NarrativeDecisionKind DeciderKind,
    string? DeciderId,
    string? ImpactAnalysisId);

public sealed record NarrativeApplyStoreResult(
    string ChangeSetId,
    string TransactionId,
    string ImpactAnalysisId,
    AuthorityTransactionState TransactionState,
    bool Existing);

public sealed record NarrativeStoreResult<T>(T? Value, NarrativeChangeFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class NarrativeStoreResults
{
    public static NarrativeStoreResult<T> Success<T>(T value) => new(value, null);

    public static NarrativeStoreResult<T> Fail<T>(NarrativeChangeError code, string? detail = null) =>
        new(default, new NarrativeChangeFailure(code, detail));
}
