using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;

namespace LLMW.Writing.Domain.Authority.Recovery;

public enum RecoveryClassification
{
    AutoRecoverable,
    UserActionRequired,
    RecoveryRequired,
    AuthorityCommittedRollForward
}

public enum RecoveryTransactionState
{
    Pending,
    CommittedButDirty,
    Complete,
    RecoveryRequired,
    Failed
}

public enum ChapterSubmissionRecoveryAction
{
    ResumeReview,
    ResumeAcceptance,
    Cancel
}

public sealed record DurableChapterSubmissionState(
    string TransactionId,
    RecoveryTransactionState TransactionState,
    ProjectSubmissionState ProjectSubmissionState,
    string? CandidateId,
    string? ChapterId,
    CandidateState? CandidateState,
    ChapterState? ChapterState,
    bool? ReviewPassed,
    bool AcceptanceExists,
    bool ManuscriptRevisionExists,
    bool CurrentPointerChanged,
    bool MaterializationComplete);

public sealed record ChapterSubmissionRecoveryPlan(
    RecoveryClassification Classification,
    ProjectSubmissionState RehydratedProjectState,
    bool HoldsSubmissionLock,
    IReadOnlySet<ChapterSubmissionRecoveryAction> AllowedActions,
    string Reason)
{
    public bool Allows(ChapterSubmissionRecoveryAction action) => AllowedActions.Contains(action);
}

public sealed record RecoveryTransitionResult(
    bool Allowed,
    ChapterSubmissionRecoveryPlan Plan,
    string? RejectionCode = null);

public static class ChapterSubmissionRecoveryPolicy
{
    private static readonly IReadOnlySet<ChapterSubmissionRecoveryAction> NoActions =
        new HashSet<ChapterSubmissionRecoveryAction>();

    public static ChapterSubmissionRecoveryPlan Derive(DurableChapterSubmissionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.TransactionState == RecoveryTransactionState.RecoveryRequired)
        {
            return Required("The durable Authority transaction exhausted roll-forward recovery.");
        }

        var hasAnyAuthorityMutation =
            state.AcceptanceExists || state.ManuscriptRevisionExists || state.CurrentPointerChanged;
        var authorityMutationIsComplete =
            state.AcceptanceExists && state.ManuscriptRevisionExists && state.CurrentPointerChanged;

        if (state.TransactionState is RecoveryTransactionState.CommittedButDirty or RecoveryTransactionState.Complete)
        {
            return authorityMutationIsComplete
                ? new ChapterSubmissionRecoveryPlan(
                    RecoveryClassification.AuthorityCommittedRollForward,
                    state.MaterializationComplete
                        ? ProjectSubmissionState.Idle
                        : ProjectSubmissionState.Revalidating,
                    HoldsSubmissionLock: !state.MaterializationComplete,
                    NoActions,
                    state.MaterializationComplete
                        ? "Committed Authority and its materialization are complete."
                        : "SQLite committed Authority; recovery must roll materialization forward.")
                : Required("A committed transaction is missing its Acceptance, Revision, or current pointer.");
        }

        if (hasAnyAuthorityMutation)
        {
            return Required("Pre-commit workflow contains an Authority mutation that may only exist after SQLite COMMIT.");
        }

        if (state.CandidateId is null)
        {
            return Auto(ProjectSubmissionState.Idle, holdsLock: false, NoActions,
                "The interrupted transaction has no durable submission workflow and can be released.");
        }

        if (state.ChapterId is null || state.CandidateState is null || state.ChapterState is null)
        {
            return Required("The durable Candidate cannot be associated with a complete Chapter workflow.");
        }

        if (state.CandidateState is CandidateState.Failed or CandidateState.Cancelled)
        {
            return state.ChapterState is ChapterState.Draft or ChapterState.Failed
                ? Auto(ProjectSubmissionState.Idle, holdsLock: false, NoActions,
                    "The submission is terminal and its history remains durable.")
                : Required("A terminal Candidate is paired with a non-terminal Chapter workflow.");
        }

        if (state.CandidateState != CandidateState.UnderReview || state.ChapterState != ChapterState.UnderReview)
        {
            return Required("The pre-commit Candidate and Chapter states do not form a legal active submission.");
        }

        if (state.ReviewPassed == true)
        {
            return new ChapterSubmissionRecoveryPlan(
                RecoveryClassification.UserActionRequired,
                ProjectSubmissionState.Resolving,
                HoldsSubmissionLock: true,
                new HashSet<ChapterSubmissionRecoveryAction>
                {
                    ChapterSubmissionRecoveryAction.ResumeAcceptance,
                    ChapterSubmissionRecoveryAction.Cancel
                },
                "Review PASS is durable, but Acceptance is absent and requires an explicit decision.");
        }

        if (state.ReviewPassed == false)
        {
            return Required("A failed Review did not transition the Candidate and Chapter to terminal workflow states.");
        }

        return Auto(
            ProjectSubmissionState.Reviewing,
            holdsLock: true,
            new HashSet<ChapterSubmissionRecoveryAction>
            {
                ChapterSubmissionRecoveryAction.ResumeReview,
                ChapterSubmissionRecoveryAction.Cancel
            },
            "The Candidate is durable and can resume Review without creating a new submission.");
    }

    public static RecoveryTransitionResult Transition(
        ChapterSubmissionRecoveryPlan plan,
        ChapterSubmissionRecoveryAction action)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Allows(action))
        {
            return new RecoveryTransitionResult(false, plan, "RECOVERY_ILLEGAL_TRANSITION");
        }

        if (action == ChapterSubmissionRecoveryAction.Cancel)
        {
            return new RecoveryTransitionResult(
                true,
                Auto(
                    ProjectSubmissionState.Idle,
                    holdsLock: false,
                    NoActions,
                    "The interrupted pre-commit submission was cancelled without deleting history."));
        }

        return new RecoveryTransitionResult(true, plan);
    }

    private static ChapterSubmissionRecoveryPlan Required(string reason) =>
        new(
            RecoveryClassification.RecoveryRequired,
            ProjectSubmissionState.Revalidating,
            HoldsSubmissionLock: true,
            NoActions,
            reason);

    private static ChapterSubmissionRecoveryPlan Auto(
        ProjectSubmissionState state,
        bool holdsLock,
        IReadOnlySet<ChapterSubmissionRecoveryAction> actions,
        string reason) =>
        new(RecoveryClassification.AutoRecoverable, state, holdsLock, actions, reason);
}

public static class RecoveryClassificationNames
{
    public static string ToStableName(this RecoveryClassification classification) => classification switch
    {
        RecoveryClassification.AutoRecoverable => "AUTO_RECOVERABLE",
        RecoveryClassification.UserActionRequired => "USER_ACTION_REQUIRED",
        RecoveryClassification.RecoveryRequired => "RECOVERY_REQUIRED",
        RecoveryClassification.AuthorityCommittedRollForward => "AUTHORITY_COMMITTED_ROLL_FORWARD",
        _ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null)
    };
}
