using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Domain.Authority.Recovery;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private static void RunWp22RecoveryDomainTests()
    {
        Run(nameof(Wp22IllegalRecoveryTransitionIsRejected), Wp22IllegalRecoveryTransitionIsRejected);
        Run(nameof(Wp22ValidResumePreservesSubmissionLock), Wp22ValidResumePreservesSubmissionLock);
        Run(nameof(Wp22CancelReleasesOnlyPreCommitWorkflow), Wp22CancelReleasesOnlyPreCommitWorkflow);
        Run(nameof(Wp22FailedSubmissionPreservesHistoryAndReleasesLock), Wp22FailedSubmissionPreservesHistoryAndReleasesLock);
        Run(nameof(Wp22PreCommitAuthorityMutationRequiresRecovery), Wp22PreCommitAuthorityMutationRequiresRecovery);
    }

    private static void Wp22IllegalRecoveryTransitionIsRejected()
    {
        var plan = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassedState());
        var illegal = ChapterSubmissionRecoveryPolicy.Transition(
            plan,
            ChapterSubmissionRecoveryAction.ResumeReview);

        AssertTrue(!illegal.Allowed, "Recovery allowed Review resume after a durable PASS.");
        AssertEqual("RECOVERY_ILLEGAL_TRANSITION", illegal.RejectionCode!, "Wrong recovery rejection code.");
    }

    private static void Wp22ValidResumePreservesSubmissionLock()
    {
        var plan = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassedState());
        var resumed = ChapterSubmissionRecoveryPolicy.Transition(
            plan,
            ChapterSubmissionRecoveryAction.ResumeAcceptance);

        AssertTrue(resumed.Allowed, "A PASSed pre-commit workflow was not resumable.");
        AssertEqual(RecoveryClassification.UserActionRequired, resumed.Plan.Classification,
            "PASSed pre-commit workflow had the wrong health classification.");
        AssertEqual(ProjectSubmissionState.Resolving, resumed.Plan.RehydratedProjectState,
            "PASSed workflow did not rehydrate to RESOLVING.");
        AssertTrue(resumed.Plan.HoldsSubmissionLock, "Resume released the active submission lock.");
    }

    private static void Wp22CancelReleasesOnlyPreCommitWorkflow()
    {
        var plan = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassedState());
        var cancelled = ChapterSubmissionRecoveryPolicy.Transition(
            plan,
            ChapterSubmissionRecoveryAction.Cancel);

        AssertTrue(cancelled.Allowed, "Legal pre-commit Cancel was rejected.");
        AssertEqual(ProjectSubmissionState.Idle, cancelled.Plan.RehydratedProjectState,
            "Cancel did not return the project submission aggregate to IDLE.");
        AssertTrue(!cancelled.Plan.HoldsSubmissionLock, "Cancel retained the submission lock.");

        var committed = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassedState() with
        {
            TransactionState = RecoveryTransactionState.CommittedButDirty,
            AcceptanceExists = true,
            ManuscriptRevisionExists = true,
            CurrentPointerChanged = true
        });
        var forbidden = ChapterSubmissionRecoveryPolicy.Transition(
            committed,
            ChapterSubmissionRecoveryAction.Cancel);
        AssertTrue(!forbidden.Allowed, "Cancel was allowed after the Authority commit point.");
    }

    private static void Wp22FailedSubmissionPreservesHistoryAndReleasesLock()
    {
        var failed = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassedState() with
        {
            ProjectSubmissionState = ProjectSubmissionState.Idle,
            CandidateState = CandidateState.Failed,
            ChapterState = ChapterState.Draft,
            ReviewPassed = false
        });

        AssertEqual(RecoveryClassification.AutoRecoverable, failed.Classification,
            "A terminal failed submission was not auto-recoverable.");
        AssertTrue(!failed.HoldsSubmissionLock, "A terminal failed submission retained the lock.");
        AssertTrue(failed.Reason.Contains("history", StringComparison.OrdinalIgnoreCase),
            "Failed-submission recovery did not state history preservation.");
    }

    private static void Wp22PreCommitAuthorityMutationRequiresRecovery()
    {
        var invalid = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassedState() with
        {
            AcceptanceExists = true
        });

        AssertEqual(RecoveryClassification.RecoveryRequired, invalid.Classification,
            "A pre-commit Acceptance record was not treated as an invariant violation.");
        AssertTrue(invalid.HoldsSubmissionLock, "Invariant failure released the submission lock.");
    }

    private static DurableChapterSubmissionState ReviewPassedState() =>
        new(
            "transaction-1",
            RecoveryTransactionState.Pending,
            ProjectSubmissionState.Resolving,
            "candidate-1",
            "chapter-1",
            CandidateState.UnderReview,
            ChapterState.UnderReview,
            ReviewPassed: true,
            AcceptanceExists: false,
            ManuscriptRevisionExists: false,
            CurrentPointerChanged: false,
            MaterializationComplete: false);
}
