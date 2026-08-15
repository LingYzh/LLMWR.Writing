using LLMW.Writing.Application.Authority;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Application.Security;

namespace LLMW.Writing.Application.ChapterAuthority;

public enum ChapterAuthorityError
{
    SubmissionNotEligible,
    ActiveSubmissionExists,
    DraftMissing,
    UnsupportedDraftFormat,
    CandidateNotFound,
    CandidateNotCurrent,
    ReviewNotPassed,
    AcceptanceNotAuthorized,
    FsmTransitionRejected,
    ArtifactVerificationFailed,
    AuthorityDirty,
    RecoveryRequired,
    InvalidPrincipal,
    CapabilityDenied,
    ApprovalRequired,
    InfrastructureFailure
}

public sealed record ChapterAuthorityFailure(ChapterAuthorityError Code, string? Detail = null);

public sealed record ChapterAuthorityResult<T>(T? Value, ChapterAuthorityFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class ChapterAuthorityResults
{
    public static ChapterAuthorityResult<T> Success<T>(T value) => new(value, null);

    public static ChapterAuthorityResult<T> Fail<T>(ChapterAuthorityError code, string? detail = null) =>
        new(default, new ChapterAuthorityFailure(code, detail));
}

public sealed record SubmitChapterDraftCommand(
    string ChapterId,
    string DraftPath,
    string IdempotencyKey,
    SubmissionEligibility Eligibility = SubmissionEligibility.Normal,
    CallerPrincipal? Principal = null);

public sealed record SubmitChapterDraftResult(
    string CandidateId,
    string TransactionId,
    string ArtifactDigest,
    string SourceDraftPath);

public enum ChapterReviewOutcome
{
    Pass,
    Fail
}

public sealed record ChapterReviewDecision(
    ChapterReviewOutcome Outcome,
    string ResultJson,
    string? DiagnosticsReference = null,
    string? RequestedChangesReference = null);

public sealed record ReviewChapterCandidateCommand(
    string CandidateId,
    CallerPrincipal? Principal = null);

public sealed record ReviewChapterCandidateResult(
    string CandidateId,
    string ReviewAttemptId,
    ChapterReviewOutcome Outcome);

public sealed record AcceptChapterCandidateCommand(
    string CandidateId,
    string IdempotencyKey,
    string AcceptedById,
    NarrativeOversightMode OversightMode = NarrativeOversightMode.Manual,
    CallerPrincipal? Principal = null);

public sealed record AcceptChapterCandidateResult(
    string CandidateId,
    string AcceptanceId,
    string ManuscriptRevisionId,
    string TransactionId,
    string MaterializedRelativePath,
    AuthorityTransactionState TransactionState,
    bool Existing);

public sealed record CandidateReviewInput(
    string CandidateId,
    string ChapterId,
    string ArtifactDigest,
    string SourceDraftPath);

public sealed record SubmissionContext(
    string ChapterId,
    ChapterState ChapterState,
    ProjectSubmissionState ProjectSubmissionState,
    bool ActiveSubmissionExists,
    string? ParentCandidateId);

public sealed record CandidateReviewContext(
    string CandidateId,
    string ChapterId,
    string TransactionId,
    string IdempotencyKey,
    string ArtifactDigest,
    string SourceDraftPath,
    CandidateState CandidateState,
    ChapterState ChapterState,
    ProjectSubmissionState ProjectSubmissionState,
    int NextAttemptNumber);

public sealed record CandidateAcceptanceContext(
    string CandidateId,
    string ChapterId,
    string TransactionId,
    string IdempotencyKey,
    string ArtifactDigest,
    string SourceDraftPath,
    CandidateState CandidateState,
    ChapterState ChapterState,
    ProjectSubmissionState ProjectSubmissionState,
    string ReviewAttemptId,
    ChapterReviewOutcome ReviewOutcome,
    AuthorityTransactionState TransactionState,
    string? AcceptanceId,
    string? ManuscriptRevisionId,
    string? MaterializedRelativePath,
    string? AcceptedById,
    string AcceptedByKind = "AUTHOR_CONFIRMED");

public sealed record PersistCandidateRequest(
    string ChapterId,
    string SourceDraftPath,
    string ArtifactDigest,
    string? ParentCandidateId,
    SubmissionEligibility Eligibility);

public sealed record PersistReviewRequest(
    CandidateReviewContext Context,
    ChapterReviewDecision Decision);

public sealed record PrepareAcceptanceRequest(
    CandidateAcceptanceContext Context,
    AcceptanceDecisionContext Decision,
    string AcceptedById,
    string MaterializedRelativePath);
