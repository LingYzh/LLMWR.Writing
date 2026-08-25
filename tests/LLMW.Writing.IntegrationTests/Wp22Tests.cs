using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Application.Recovery;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Domain.Authority.Recovery;
using LLMW.Writing.Infrastructure.Authority;
using LLMW.Writing.Infrastructure.ChapterAuthority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Recovery;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static readonly List<string> Wp22PassedTests = [];

    private static void RunWp22Tests()
    {
        RunWp22(nameof(CrashAfterReviewPassRehydratesWithoutAuthority), CrashAfterReviewPassRehydratesWithoutAuthority);
        RunWp22(nameof(PendingAcceptanceCrashCanResumeLegally), PendingAcceptanceCrashCanResumeLegally);
        RunWp22(nameof(RepeatedAndInterruptedRecoveryIsIdempotent), RepeatedAndInterruptedRecoveryIsIdempotent);
        RunWp22(nameof(RecoveredSubmissionLockMatchesWorkflowLifetime), RecoveredSubmissionLockMatchesWorkflowLifetime);
        RunWp22(nameof(ProjectHealthSeparatesPreCommitAndRollForward), ProjectHealthSeparatesPreCommitAndRollForward);
        RunWp22(nameof(TransactionCreationCrashAutoReleasesWithoutHistoryFabrication), TransactionCreationCrashAutoReleasesWithoutHistoryFabrication);

        Console.WriteLine($"WP22 integration tests passed ({Wp22PassedTests.Count}).");
        foreach (var test in Wp22PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void CrashAfterReviewPassRehydratesWithoutAuthority()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(fixture.DraftPath, "wp22 case A", Encoding.UTF8);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-case-a", Principal: Wp09UserPrincipal)));
        Success(fixture.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));

        var restarted = RestartWp22(fixture);
        var report = restarted.Recovery.RecoverStartup();

        AssertWp05Equal("USER_ACTION_REQUIRED", report.StableClassification,
            "Case A recovery classification was wrong.");
        AssertWp05Equal(1, report.Items.Count, "Case A did not reconstruct exactly one submission.");
        AssertWp05Equal(submitted.CandidateId, report.Items[0].CandidateId!, "Case A changed Candidate identity.");
        AssertWp05Equal(true, report.Items[0].HoldsSubmissionLock, "Case A released the live submission lock.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM candidates;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM review_attempts WHERE status='passed';");
        AssertNoPreCommitAuthority(fixture);
        fixture.AssertScalar("resolving", "SELECT status FROM authority_transactions WHERE idempotency_key='wp22-case-a';");
        fixture.AssertScalar("resolving", "SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='wp22-case-a';");
    }

    private static void PendingAcceptanceCrashCanResumeLegally()
    {
        using var fixture = Wp05Fixture.Create(
            ChapterReviewOutcome.Pass,
            AuthorityTransactionFaultPoint.BeforeSqliteCommit);
        File.WriteAllText(fixture.DraftPath, "wp22 case B", Encoding.UTF8);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-case-b", Principal: Wp09UserPrincipal)));
        Success(fixture.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        var interrupted = fixture.Service.AcceptChapterCandidate(AuthorAccept(submitted.CandidateId, "wp22-case-b"));
        AssertWp05Failure(ChapterAuthorityError.InfrastructureFailure, interrupted.Failure);
        AssertNoPreCommitAuthority(fixture);

        var restarted = RestartWp22(fixture);
        var report = restarted.Recovery.RecoverStartup();
        AssertWp05Equal("USER_ACTION_REQUIRED", report.StableClassification,
            "Pending Acceptance was confused with committed Authority recovery.");
        fixture.AssertScalar("resolving", "SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='wp22-case-b';");

        var resumed = Success(restarted.Service.AcceptChapterCandidate(
            AuthorAccept(submitted.CandidateId, "wp22-case-b")));
        AssertWp05Equal(AuthorityTransactionState.Complete, resumed.TransactionState,
            "Recovered Acceptance could not continue legally.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM manuscript_revisions;");
    }

    private static void RepeatedAndInterruptedRecoveryIsIdempotent()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(fixture.DraftPath, "wp22 case C", Encoding.UTF8);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-case-c", Principal: Wp09UserPrincipal)));
        Success(fixture.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));

        var interrupted = RestartWp22(fixture, new ThrowAtRecoveryPoint(ProjectRecoveryFaultPoint.AfterTransactionRecovery));
        AssertWp22Throws<InjectedWp22RecoveryException>(
            () => interrupted.Recovery.RecoverStartup(),
            "Interrupted recovery fault was not observed.");
        fixture.AssertScalar("failed", "SELECT status FROM authority_transactions WHERE idempotency_key='wp22-case-c';");
        fixture.AssertScalar("resolving", "SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='wp22-case-c';");

        var firstRestart = RestartWp22(fixture);
        var first = firstRestart.Recovery.RecoverStartup();
        var secondRestart = RestartWp22(fixture);
        var second = secondRestart.Recovery.RecoverStartup();

        AssertWp05Equal(first.StableClassification, second.StableClassification,
            "Repeated RecoverIncomplete changed classification.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM candidates;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM review_attempts;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM authority_transactions;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar(1L,
            "SELECT COUNT(*) FROM authority_transactions WHERE status='resolving' AND project_submission_state='resolving';");
    }

    private static void RecoveredSubmissionLockMatchesWorkflowLifetime()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(fixture.DraftPath, "wp22 case D", Encoding.UTF8);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-case-d", Principal: Wp09UserPrincipal)));
        Success(fixture.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        var restarted = RestartWp22(fixture);
        restarted.Recovery.RecoverStartup();

        var blocked = restarted.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-case-d-blocked", Principal: Wp09UserPrincipal));
        AssertWp05Failure(ChapterAuthorityError.ActiveSubmissionExists, blocked.Failure);

        var cancelled = restarted.Recovery.ApplyDecision(
            submitted.TransactionId,
            ChapterSubmissionRecoveryAction.Cancel,
            Wp09UserPrincipal);
        AssertWp05Equal(true, cancelled.Succeeded, "Legal recovery Cancel failed.");
        fixture.AssertScalar("cancelled", $"SELECT status FROM candidates WHERE candidate_id='{submitted.CandidateId}';");
        fixture.AssertScalar("idle", $"SELECT project_submission_state FROM authority_transactions WHERE transaction_id='{submitted.TransactionId}';");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM review_attempts;");

        var next = Success(restarted.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-case-d-next", Principal: Wp09UserPrincipal)));
        AssertWp05False(StringComparer.Ordinal.Equals(next.CandidateId, submitted.CandidateId),
            "A new submission reused the cancelled Candidate.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM authority_transactions WHERE status='reviewing';");
    }

    private static void ProjectHealthSeparatesPreCommitAndRollForward()
    {
        using var preCommit = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(preCommit.DraftPath, "precommit health", Encoding.UTF8);
        var preSubmitted = Success(preCommit.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(preCommit.ChapterId, preCommit.DraftPath, "wp22-health-pre", Principal: Wp09UserPrincipal)));
        Success(preCommit.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(preSubmitted.CandidateId, Wp09UserPrincipal)));
        var preReport = RestartWp22(preCommit).Recovery.RecoverStartup();
        AssertWp05Equal("USER_ACTION_REQUIRED", preReport.StableClassification,
            "Pre-commit workflow surfaced as materialization dirty.");

        using var postCommit = Wp05Fixture.Create(
            ChapterReviewOutcome.Pass,
            AuthorityTransactionFaultPoint.AfterSqliteCommit);
        File.WriteAllText(postCommit.DraftPath, "roll forward health", Encoding.UTF8);
        var expectedPostCommitBytes = File.ReadAllBytes(postCommit.DraftPath);
        var postSubmitted = Success(postCommit.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(postCommit.ChapterId, postCommit.DraftPath, "wp22-health-post", Principal: Wp09UserPrincipal)));
        Success(postCommit.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(postSubmitted.CandidateId, Wp09UserPrincipal)));
        AssertWp05Failure(
            ChapterAuthorityError.AuthorityDirty,
            postCommit.Service.AcceptChapterCandidate(AuthorAccept(postSubmitted.CandidateId, "wp22-health-post")).Failure);

        var postReport = RestartWp22(postCommit).Recovery.RecoverStartup();
        AssertWp05Equal("AUTHORITY_COMMITTED_ROLL_FORWARD", postReport.StableClassification,
            "Committed dirty Authority did not surface as roll-forward.");
        postCommit.AssertScalar("complete", "SELECT status FROM authority_transactions WHERE idempotency_key='wp22-health-post';");
        postCommit.AssertScalar("idle", "SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='wp22-health-post';");
        postCommit.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        AssertWp05Bytes(
            expectedPostCommitBytes,
            File.ReadAllBytes(postCommit.CurrentManuscriptPath),
            "Roll-forward did not materialize committed Authority.");
        var settledReport = RestartWp22(postCommit).Recovery.RecoverStartup();
        AssertWp05Equal(0, settledReport.Items.Count,
            "Completed roll-forward remained an incomplete workflow after another restart.");

        using var required = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(required.DraftPath, "required health", Encoding.UTF8);
        var requiredSubmitted = Success(required.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(required.ChapterId, required.DraftPath, "wp22-health-required", Principal: Wp09UserPrincipal)));
        Success(required.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(requiredSubmitted.CandidateId, Wp09UserPrincipal)));
        SetRecoveryRequired(required.DatabasePath, requiredSubmitted.TransactionId);
        var requiredReport = RestartWp22(required).Recovery.RecoverStartup();
        AssertWp05Equal("RECOVERY_REQUIRED", requiredReport.StableClassification,
            "RECOVERY_REQUIRED was not exposed by Project Health.");
        AssertWp05Equal(true, requiredReport.AuthorityReadOnly,
            "RECOVERY_REQUIRED did not require read-only Authority behavior.");

        postCommit.AssertScalar(1L, "PRAGMA user_version;");
        postCommit.AssertScalar(1L, "SELECT COUNT(*) FROM schema_migrations;");
    }

    private static void TransactionCreationCrashAutoReleasesWithoutHistoryFabrication()
    {
        using var fixture = Wp05Fixture.Create(
            ChapterReviewOutcome.Pass,
            AuthorityTransactionFaultPoint.AfterPendingTransactionCreated);
        File.WriteAllText(fixture.DraftPath, "transaction created", Encoding.UTF8);
        var interrupted = fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-created", Principal: Wp09UserPrincipal));
        AssertWp05Failure(ChapterAuthorityError.InfrastructureFailure, interrupted.Failure);
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM candidates;");

        var report = RestartWp22(fixture).Recovery.RecoverStartup();
        AssertWp05Equal("AUTO_RECOVERABLE", report.StableClassification,
            "A transaction with no Candidate was not auto-released.");
        fixture.AssertScalar("failed", "SELECT status FROM authority_transactions WHERE idempotency_key='wp22-created';");
        fixture.AssertScalar("idle", "SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='wp22-created';");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");

        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp22-created-next", Principal: Wp09UserPrincipal)));
        AssertWp05Equal(false, string.IsNullOrWhiteSpace(submitted.CandidateId),
            "New submission remained blocked after orphan transaction recovery.");
    }

    private static Wp22RestartedRuntime RestartWp22(
        Wp05Fixture fixture,
        IRecoveryFaultInjector? recoveryFaultInjector = null)
    {
        var blobStore = new ImmutableBlobStore(fixture.Root);
        var materializer = new ChapterAuthorityMaterializer(
            fixture.DatabasePath,
            new AtomicAuthorityMaterializer(fixture.Root, blobStore));
        var transactions = new AuthorityTransactionCoordinator(
            fixture.DatabasePath,
            blobStore,
            materializer);
        var recoveryStore = new SqliteChapterSubmissionRecoveryStore(fixture.DatabasePath);
        var recovery = new ProjectRecoveryCoordinator(
            transactions,
            recoveryStore,
            recoveryFaultInjector,
            Wp09Authorization);
        var chapterStore = new SqliteChapterAuthorityStore(fixture.DatabasePath, transactions);
        var service = new ChapterAuthorityService(
            blobStore,
            transactions,
            chapterStore,
            fixture.Reviewer,
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            Wp09Authorization);
        return new Wp22RestartedRuntime(recovery, service);
    }

    private static void AssertNoPreCommitAuthority(Wp05Fixture fixture)
    {
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM manuscript_revisions;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar(0L,
            $"SELECT COUNT(*) FROM chapters WHERE chapter_id='{fixture.ChapterId}' AND current_manuscript_revision_id IS NOT NULL;");
        AssertWp05False(File.Exists(fixture.CurrentManuscriptPath),
            "Pre-commit recovery created Current Manuscript materialization.");
    }

    private static void SetRecoveryRequired(string databasePath, string transactionId)
    {
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE authority_transactions SET recovery_state='recovery_required',status='revalidating' WHERE transaction_id=$id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = transactionId;
        command.Parameters.Add(parameter);
        command.ExecuteNonQuery();
    }

    private static void RunWp22(string name, Action test)
    {
        test();
        Wp22PassedTests.Add(name);
    }

    private static void AssertWp22Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed record Wp22RestartedRuntime(
        ProjectRecoveryCoordinator Recovery,
        ChapterAuthorityService Service);

    private sealed class ThrowAtRecoveryPoint(ProjectRecoveryFaultPoint target) : IRecoveryFaultInjector
    {
        private bool injected;

        public void Inject(ProjectRecoveryFaultPoint point)
        {
            if (!injected && point == target)
            {
                injected = true;
                throw new InjectedWp22RecoveryException(point);
            }
        }
    }

    private sealed class InjectedWp22RecoveryException(ProjectRecoveryFaultPoint point) : Exception(point.ToString());
}
