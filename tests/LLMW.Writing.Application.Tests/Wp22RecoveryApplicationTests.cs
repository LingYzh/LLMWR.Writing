using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Recovery;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Domain.Authority.Recovery;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Tests;

internal static class Wp22RecoveryApplicationTests
{
    public static int Run()
    {
        RestartRecoveryRehydratesPassedReview();
        DuplicateRecoveryRequestIsIdempotent();
        CancelAndResumeKeepLockStateConsistent();
        RecoveryDecisionRequiresTrustedAuthorization();
        ProjectHealthExposesEveryRequiredClassification();
        CommittedRollForwardSettlesWorkflow();
        return 6;
    }

    private static void RestartRecoveryRehydratesPassedReview()
    {
        var store = new FakeRecoveryStore(ReviewPassed());
        var transactions = new FakeTransactions(store);
        var report = new ProjectRecoveryCoordinator(transactions, store).RecoverStartup();

        AssertEqual(RecoveryClassification.UserActionRequired, report.OverallClassification,
            "Restart did not classify a PASSed pre-commit workflow as requiring a decision.");
        AssertEqual("USER_ACTION_REQUIRED", report.StableClassification, "Health name is not stable.");
        AssertTrue(report.Items.Single().HoldsSubmissionLock, "Rehydrated workflow released its lock.");
        AssertEqual(ProjectSubmissionState.Resolving, store.State.ProjectSubmissionState,
            "Application coordinator did not route rehydration to RESOLVING.");
        AssertEqual(1, store.CandidateCount, "Recovery created or removed a Candidate.");
        AssertEqual(1, store.TransactionCount, "Recovery created or removed a transaction.");
    }

    private static void DuplicateRecoveryRequestIsIdempotent()
    {
        var store = new FakeRecoveryStore(ReviewPassed());
        var coordinator = CreateCoordinator(store);

        var first = coordinator.RecoverStartup();
        var second = coordinator.RecoverStartup();

        AssertEqual(first.StableClassification, second.StableClassification,
            "Repeated startup recovery changed its classification.");
        AssertEqual(1, store.CandidateCount, "Repeated recovery duplicated the Candidate.");
        AssertEqual(1, store.TransactionCount, "Repeated recovery duplicated the transaction.");
        AssertEqual(ProjectSubmissionState.Resolving, store.State.ProjectSubmissionState,
            "Repeated recovery drifted the workflow state.");
    }

    private static void CancelAndResumeKeepLockStateConsistent()
    {
        var store = new FakeRecoveryStore(ReviewPassed());
        var coordinator = CreateCoordinator(store);
        coordinator.RecoverStartup();
        var principal = new TrustedNativePrincipalSource("wp22-application").ResolveUserInteractive();

        var resume = coordinator.ApplyDecision(
            store.State.TransactionId,
            ChapterSubmissionRecoveryAction.ResumeAcceptance,
            principal);
        AssertTrue(resume.Succeeded && resume.Item!.HoldsSubmissionLock,
            "Resume did not retain the active submission lock.");

        var cancel = coordinator.ApplyDecision(
            store.State.TransactionId,
            ChapterSubmissionRecoveryAction.Cancel,
            principal);
        AssertTrue(cancel.Succeeded && !cancel.Item!.HoldsSubmissionLock,
            "Cancel did not release the active submission lock.");
        AssertEqual(CandidateState.Cancelled, store.State.CandidateState!.Value,
            "Cancel did not persist terminal Candidate state.");
        AssertEqual(ProjectSubmissionState.Idle, store.State.ProjectSubmissionState,
            "Cancel left a live workflow after releasing the lock.");
    }

    private static void ProjectHealthExposesEveryRequiredClassification()
    {
        var auto = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassed() with
        {
            CandidateId = null,
            ChapterId = null,
            CandidateState = null,
            ChapterState = null,
            ReviewPassed = null
        });
        var user = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassed());
        var required = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassed() with
        {
            TransactionState = RecoveryTransactionState.RecoveryRequired
        });
        var rollForward = ChapterSubmissionRecoveryPolicy.Derive(ReviewPassed() with
        {
            TransactionState = RecoveryTransactionState.CommittedButDirty,
            AcceptanceExists = true,
            ManuscriptRevisionExists = true,
            CurrentPointerChanged = true
        });

        AssertEqual("AUTO_RECOVERABLE", auto.Classification.ToStableName(), "Missing AUTO_RECOVERABLE.");
        AssertEqual("USER_ACTION_REQUIRED", user.Classification.ToStableName(), "Missing USER_ACTION_REQUIRED.");
        AssertEqual("RECOVERY_REQUIRED", required.Classification.ToStableName(), "Missing RECOVERY_REQUIRED.");
        AssertEqual("AUTHORITY_COMMITTED_ROLL_FORWARD", rollForward.Classification.ToStableName(),
            "Missing AUTHORITY_COMMITTED_ROLL_FORWARD.");
        AssertTrue(user.Classification != rollForward.Classification,
            "Pre-commit interruption was mislabeled as committed materialization recovery.");
    }

    private static void RecoveryDecisionRequiresTrustedAuthorization()
    {
        var store = new FakeRecoveryStore(ReviewPassed());
        var coordinator = CreateCoordinator(store);
        coordinator.RecoverStartup();

        var denied = coordinator.ApplyDecision(
            store.State.TransactionId,
            ChapterSubmissionRecoveryAction.Cancel,
            principal: null);

        AssertTrue(!denied.Succeeded && denied.ErrorCode == "RECOVERY_DECISION_DENIED",
            "Recovery decision routing accepted a missing principal.");
        AssertEqual(CandidateState.UnderReview, store.State.CandidateState!.Value,
            "Denied recovery decision mutated the Candidate.");
        AssertEqual(ProjectSubmissionState.Resolving, store.State.ProjectSubmissionState,
            "Denied recovery decision released the workflow lock.");
    }

    private static void CommittedRollForwardSettlesWorkflow()
    {
        var store = new FakeRecoveryStore(ReviewPassed() with
        {
            TransactionState = RecoveryTransactionState.CommittedButDirty,
            ProjectSubmissionState = ProjectSubmissionState.Revalidating,
            CandidateState = CandidateState.Accepted,
            ChapterState = ChapterState.Accepted,
            AcceptanceExists = true,
            ManuscriptRevisionExists = true,
            CurrentPointerChanged = true
        });
        var coordinator = CreateCoordinator(store);

        var report = coordinator.RecoverStartup();

        AssertEqual("AUTHORITY_COMMITTED_ROLL_FORWARD", report.StableClassification,
            "Committed Authority was not classified as roll-forward recovery.");
        AssertEqual(ProjectSubmissionState.Idle, store.State.ProjectSubmissionState,
            "Verified roll-forward did not settle the durable submission workflow.");
        AssertEqual(0, coordinator.RecoverStartup().Items.Count,
            "Settled roll-forward was rediscovered on the next restart.");
    }

    private static DurableChapterSubmissionState ReviewPassed() =>
        new(
            "tx-1",
            RecoveryTransactionState.Pending,
            ProjectSubmissionState.Resolving,
            "candidate-1",
            "chapter-1",
            CandidateState.UnderReview,
            ChapterState.UnderReview,
            true,
            false,
            false,
            false,
            false);

    private static ProjectRecoveryCoordinator CreateCoordinator(FakeRecoveryStore store) =>
        new(
            new FakeTransactions(store),
            store,
            authorizationService: AllowRecoveryAuthorization.Instance);

    private sealed class FakeRecoveryStore : IChapterSubmissionRecoveryStore
    {
        public FakeRecoveryStore(DurableChapterSubmissionState state)
        {
            State = state;
        }

        public DurableChapterSubmissionState State { get; private set; }
        public int CandidateCount => State.CandidateId is null ? 0 : 1;
        public int TransactionCount => string.IsNullOrEmpty(State.TransactionId) ? 0 : 1;

        public IReadOnlyList<DurableChapterSubmissionState> LoadIncomplete() =>
            State.ProjectSubmissionState == ProjectSubmissionState.Idle ? [] : [State];

        public DurableChapterSubmissionState? Load(string transactionId) =>
            StringComparer.Ordinal.Equals(transactionId, State.TransactionId) ? State : null;

        public void RehydratePreCommit(
            DurableChapterSubmissionState state,
            ChapterSubmissionRecoveryPlan plan) =>
            State = State with
            {
                TransactionState = RecoveryTransactionState.Pending,
                ProjectSubmissionState = plan.RehydratedProjectState
            };

        public void ReleaseOrphanedPreCommit(DurableChapterSubmissionState state) =>
            State = State with
            {
                TransactionState = RecoveryTransactionState.Failed,
                ProjectSubmissionState = ProjectSubmissionState.Idle
            };

        public void FinalizeCommittedRollForward(DurableChapterSubmissionState state) =>
            State = State with { ProjectSubmissionState = ProjectSubmissionState.Idle };

        public void CancelPreCommit(DurableChapterSubmissionState state) =>
            State = State with
            {
                TransactionState = RecoveryTransactionState.Failed,
                ProjectSubmissionState = ProjectSubmissionState.Idle,
                CandidateState = CandidateState.Cancelled,
                ChapterState = ChapterState.Draft
            };

        public void MarkRecoveryRequired(DurableChapterSubmissionState state, string reason) =>
            State = State with
            {
                TransactionState = RecoveryTransactionState.RecoveryRequired,
                ProjectSubmissionState = ProjectSubmissionState.Revalidating
            };

        public void CompleteRollForward() =>
            State = State with
            {
                TransactionState = RecoveryTransactionState.Complete,
                MaterializationComplete = true
            };
    }

    private sealed class FakeTransactions(FakeRecoveryStore store) : IAuthorityTransactionCoordinator
    {
        public AuthorityTransactionHandle Begin(string transactionKind, string idempotencyKey) => throw new NotSupportedException();

        public BlobStageResult StageBlob(
            AuthorityTransactionHandle handle,
            Stream source,
            string? expectedDigest = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public AuthorityTransactionHandle Commit(
            AuthorityTransactionHandle handle,
            AuthorityCommitRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public AuthorityTransactionHandle Recover(string transactionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<AuthorityRecoveryResult> RecoverIncomplete(CancellationToken cancellationToken = default)
        {
            if (store.State.TransactionState == RecoveryTransactionState.CommittedButDirty)
            {
                store.CompleteRollForward();
            }

            var state = store.State.TransactionState switch
            {
                RecoveryTransactionState.CommittedButDirty => AuthorityTransactionState.Complete,
                RecoveryTransactionState.RecoveryRequired => AuthorityTransactionState.RecoveryRequired,
                _ => AuthorityTransactionState.Failed
            };
            return [new AuthorityRecoveryResult(store.State.TransactionId, state, 0, null)];
        }

        public AuthorityRecoveryResult Inspect(string transactionId) => throw new NotSupportedException();
    }

    private sealed class AllowRecoveryAuthorization : IAuthorizationService
    {
        public static AllowRecoveryAuthorization Instance { get; } = new();

        public CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request) =>
            new(
                request.Capability,
                principal?.Kind ?? PrincipalKind.UserInteractive,
                principal is null ? CapabilityDecisionKind.Denied : CapabilityDecisionKind.Allowed,
                [],
                null,
                null,
                SecurityScopeClassification.InScope,
                HardDenied: false);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }
}
