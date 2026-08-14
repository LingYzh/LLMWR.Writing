using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Infrastructure.Authority;
using LLMW.Writing.Infrastructure.ChapterAuthority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static readonly List<string> Wp05PassedTests = [];

    private static void RunWp05Tests()
    {
        RunWp05(nameof(HappyPathPersistsAuthorityAndMaterializesCurrentManuscript), HappyPathPersistsAuthorityAndMaterializesCurrentManuscript);
        RunWp05(nameof(CandidateRemainsImmutableWhenDraftChanges), CandidateRemainsImmutableWhenDraftChanges);
        RunWp05(nameof(ReviewFailRetainsHistoryAndRetryCreatesNewCandidate), ReviewFailRetainsHistoryAndRetryCreatesNewCandidate);
        RunWp05(nameof(DuplicateSubmitAndUnauthorizedAcceptAreRejected), DuplicateSubmitAndUnauthorizedAcceptAreRejected);
        RunWp05(nameof(PreCommitFaultLeavesNoAuthorityAndCanRetry), PreCommitFaultLeavesNoAuthorityAndCanRetry);
        RunWp05(nameof(PostCommitFaultRollsForwardWithoutDuplicateAuthority), PostCommitFaultRollsForwardWithoutDuplicateAuthority);

        Console.WriteLine($"WP05 integration tests passed ({Wp05PassedTests.Count}).");
        foreach (var test in Wp05PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void HappyPathPersistsAuthorityAndMaterializesCurrentManuscript()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        var draftBytes = Encoding.UTF8.GetBytes("happy path manuscript\n");
        File.WriteAllBytes(fixture.DraftPath, draftBytes);

        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "happy-accept", Principal: Wp09UserPrincipal)));
        var reviewed = Success(fixture.Service.ReviewChapterCandidate(
            new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        var accepted = Success(fixture.Service.AcceptChapterCandidate(
            AuthorAccept(submitted.CandidateId, "happy-accept")));

        AssertWp05Equal(ChapterReviewOutcome.Pass, reviewed.Outcome, "Fake reviewer did not PASS.");
        AssertWp05Equal(AuthorityTransactionState.Complete, accepted.TransactionState, "Acceptance did not complete.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM candidates WHERE status='accepted';");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM review_attempts WHERE status='passed';");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records WHERE accepted_by_kind='AUTHOR_CONFIRMED';");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM manuscript_revisions WHERE materialization_status='materialized';");
        fixture.AssertScalar(4L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar(accepted.ManuscriptRevisionId,
            $"SELECT current_manuscript_revision_id FROM chapters WHERE chapter_id='{fixture.ChapterId}';");
        fixture.AssertScalar("complete", "SELECT status FROM authority_transactions WHERE idempotency_key='happy-accept';");
        AssertWp05Bytes(draftBytes, File.ReadAllBytes(fixture.CurrentManuscriptPath), "Current Manuscript bytes differ.");
        fixture.AssertScalar(Hash(draftBytes),
            $"SELECT artifact_digest FROM manuscript_revisions WHERE revision_id='{accepted.ManuscriptRevisionId}';");

        var duplicate = Success(fixture.Service.AcceptChapterCandidate(
            AuthorAccept(submitted.CandidateId, "happy-accept")));
        AssertWp05Equal(accepted.ManuscriptRevisionId, duplicate.ManuscriptRevisionId, "Idempotent Accept changed revision identity.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM manuscript_revisions;");
    }

    private static void CandidateRemainsImmutableWhenDraftChanges()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        var versionA = Encoding.UTF8.GetBytes("version A");
        var versionB = Encoding.UTF8.GetBytes("version B");
        File.WriteAllBytes(fixture.DraftPath, versionA);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "immutable-accept", Principal: Wp09UserPrincipal)));
        File.WriteAllBytes(fixture.DraftPath, versionB);

        Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        Success(fixture.Service.AcceptChapterCandidate(AuthorAccept(submitted.CandidateId, "immutable-accept")));

        AssertWp05Bytes(versionA, fixture.ReadBlob(submitted.ArtifactDigest), "Candidate blob changed with Draft.");
        AssertWp05Bytes(versionA, File.ReadAllBytes(fixture.CurrentManuscriptPath), "Accepted Manuscript did not use Candidate A.");
        AssertWp05Bytes(versionB, File.ReadAllBytes(fixture.DraftPath), "Draft B was unexpectedly overwritten.");
        AssertWp05Equal(Hash(versionA), submitted.ArtifactDigest, "Candidate digest is not the submitted bytes digest.");
    }

    private static void ReviewFailRetainsHistoryAndRetryCreatesNewCandidate()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Fail);
        File.WriteAllText(fixture.DraftPath, "first candidate", Encoding.UTF8);
        var first = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "fail-first", Principal: Wp09UserPrincipal)));
        var failed = Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(first.CandidateId, Wp09UserPrincipal)));
        AssertWp05Equal(ChapterReviewOutcome.Fail, failed.Outcome, "Fake reviewer did not FAIL.");
        fixture.AssertScalar("failed", $"SELECT status FROM candidates WHERE candidate_id='{first.CandidateId}';");
        fixture.AssertScalar(1L, $"SELECT COUNT(*) FROM review_attempts WHERE candidate_id='{first.CandidateId}';");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM manuscript_revisions;");
        AssertWp05False(File.Exists(fixture.CurrentManuscriptPath), "Review FAIL materialized a Manuscript.");

        fixture.Reviewer.Outcome = ChapterReviewOutcome.Pass;
        File.WriteAllText(fixture.DraftPath, "second candidate", Encoding.UTF8);
        var second = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "retry-second", Principal: Wp09UserPrincipal)));
        AssertWp05False(StringComparer.Ordinal.Equals(first.CandidateId, second.CandidateId), "Retry reused Candidate identity.");
        fixture.AssertScalar(first.CandidateId,
            $"SELECT parent_candidate_id FROM candidates WHERE candidate_id='{second.CandidateId}';");
        fixture.AssertScalar("failed", $"SELECT status FROM candidates WHERE candidate_id='{first.CandidateId}';");
        Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(second.CandidateId, Wp09UserPrincipal)));
        Success(fixture.Service.AcceptChapterCandidate(AuthorAccept(second.CandidateId, "retry-second")));
    }

    private static void DuplicateSubmitAndUnauthorizedAcceptAreRejected()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        File.WriteAllText(fixture.DraftPath, "duplicate guard", Encoding.UTF8);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "guard-accept", Principal: Wp09UserPrincipal)));
        var duplicate = fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "guard-second", Principal: Wp09UserPrincipal));
        AssertWp05Failure(ChapterAuthorityError.ActiveSubmissionExists, duplicate.Failure);

        Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        var unauthorized = fixture.Service.AcceptChapterCandidate(
            new AcceptChapterCandidateCommand(
                submitted.CandidateId,
                "guard-accept",
                "unauthorized",
                Principal: null));
        AssertWp05Failure(ChapterAuthorityError.InvalidPrincipal, unauthorized.Failure);
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM manuscript_revisions;");
        AssertWp05False(File.Exists(fixture.CurrentManuscriptPath), "Unauthorized Accept changed materialization.");
    }

    private static void PreCommitFaultLeavesNoAuthorityAndCanRetry()
    {
        using var fixture = Wp05Fixture.Create(
            ChapterReviewOutcome.Pass,
            AuthorityTransactionFaultPoint.BeforeSqliteCommit);
        File.WriteAllText(fixture.DraftPath, "pre-commit retry", Encoding.UTF8);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "precommit-accept", Principal: Wp09UserPrincipal)));
        Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));

        var interrupted = fixture.Service.AcceptChapterCandidate(AuthorAccept(submitted.CandidateId, "precommit-accept"));
        AssertWp05Failure(ChapterAuthorityError.InfrastructureFailure, interrupted.Failure);
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM manuscript_revisions;");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar(0L,
            $"SELECT COUNT(*) FROM chapters WHERE chapter_id='{fixture.ChapterId}' AND current_manuscript_revision_id IS NOT NULL;");
        AssertWp05False(File.Exists(fixture.CurrentManuscriptPath), "Pre-commit fault created Current Manuscript.");

        var retried = Success(fixture.Service.AcceptChapterCandidate(AuthorAccept(submitted.CandidateId, "precommit-accept")));
        AssertWp05Equal(AuthorityTransactionState.Complete, retried.TransactionState, "Pre-commit retry did not complete.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM manuscript_revisions;");
    }

    private static void PostCommitFaultRollsForwardWithoutDuplicateAuthority()
    {
        using var fixture = Wp05Fixture.Create(
            ChapterReviewOutcome.Pass,
            AuthorityTransactionFaultPoint.AfterSqliteCommit);
        var expected = Encoding.UTF8.GetBytes("post-commit recovery");
        File.WriteAllBytes(fixture.DraftPath, expected);
        var submitted = Success(fixture.Service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "postcommit-accept", Principal: Wp09UserPrincipal)));
        Success(fixture.Service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));

        var interrupted = fixture.Service.AcceptChapterCandidate(AuthorAccept(submitted.CandidateId, "postcommit-accept"));
        AssertWp05Failure(ChapterAuthorityError.AuthorityDirty, interrupted.Failure);
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM manuscript_revisions;");
        fixture.AssertScalar(4L, "SELECT COUNT(*) FROM authority_events;");
        fixture.AssertScalar("committed_but_dirty",
            "SELECT recovery_state FROM authority_transactions WHERE idempotency_key='postcommit-accept';");
        fixture.AssertScalar("revalidating",
            "SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='postcommit-accept';");
        AssertWp05False(File.Exists(fixture.CurrentManuscriptPath), "Post-commit interruption unexpectedly materialized file.");

        var startupRecovery = fixture.Coordinator.RecoverIncomplete();
        AssertWp05Equal(1, startupRecovery.Count, "RecoverIncomplete did not find the dirty transaction.");
        AssertWp05Equal(AuthorityTransactionState.Complete, startupRecovery[0].State, "RecoverIncomplete did not complete recovery.");
        var recovered = Success(fixture.Service.AcceptChapterCandidate(AuthorAccept(submitted.CandidateId, "postcommit-accept")));
        AssertWp05Equal(AuthorityTransactionState.Complete, recovered.TransactionState, "Recovery did not complete.");
        AssertWp05Bytes(expected, File.ReadAllBytes(fixture.CurrentManuscriptPath), "Recovery materialized wrong bytes.");
        fixture.AssertScalar("none", "SELECT recovery_state FROM authority_transactions WHERE idempotency_key='postcommit-accept';");
        fixture.AssertScalar("idle", "SELECT project_submission_state FROM authority_transactions WHERE idempotency_key='postcommit-accept';");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM manuscript_revisions;");
        fixture.AssertScalar(4L, "SELECT COUNT(*) FROM authority_events;");
    }

    private static AcceptChapterCandidateCommand AuthorAccept(string candidateId, string idempotencyKey) =>
        new(
            candidateId,
            idempotencyKey,
            "test/user-interactive",
            Principal: Wp09UserPrincipal);

    private static T Success<T>(ChapterAuthorityResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success; failure={result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static void RunWp05(string name, Action test)
    {
        test();
        Wp05PassedTests.Add(name);
    }

    private static void AssertWp05Failure(ChapterAuthorityError expected, ChapterAuthorityFailure? failure)
    {
        if (failure?.Code != expected)
        {
            throw new InvalidOperationException($"Expected failure {expected}; actual={failure?.Code}: {failure?.Detail}");
        }
    }

    private static void AssertWp05Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void AssertWp05False(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertWp05Bytes(byte[] expected, byte[] actual, string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Wp05Fixture : IDisposable
    {
        private readonly string root;
        private readonly ImmutableBlobStore blobStore;

        private Wp05Fixture(
            string root,
            string databasePath,
            string storylineId,
            string chapterId,
            string draftPath,
            ImmutableBlobStore blobStore,
            DeterministicReviewer reviewer,
            AuthorityTransactionCoordinator coordinator,
            ChapterAuthorityService service)
        {
            this.root = root;
            DatabasePath = databasePath;
            StorylineId = storylineId;
            ChapterId = chapterId;
            DraftPath = draftPath;
            this.blobStore = blobStore;
            Reviewer = reviewer;
            Coordinator = coordinator;
            Service = service;
        }

        public string DatabasePath { get; }
        public string Root => root;
        public ImmutableBlobStore BlobStore => blobStore;
        public string StorylineId { get; }
        public string ChapterId { get; }
        public string DraftPath { get; }
        public DeterministicReviewer Reviewer { get; }
        public AuthorityTransactionCoordinator Coordinator { get; }
        public ChapterAuthorityService Service { get; }
        public string CurrentManuscriptPath => Path.Combine(root, "Manuscript", "current", ChapterId + ".md");

        public static Wp05Fixture Create(
            ChapterReviewOutcome outcome,
            AuthorityTransactionFaultPoint? faultPoint = null,
            IAuthorizationService? authorizationService = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP05", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, ".llmw", "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp05-tests", 1735689600000);
            const string storylineId = "0198a8a0-0000-7000-8000-000000000001";
            const string chapterId = "0198a8a0-0000-7000-8000-000000000002";
            using (var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(databasePath))
            {
                ExecuteFixture(connection,
                    """
                    INSERT INTO objects(object_id,object_type,schema_version,status,created_at_ms,updated_at_ms)
                    VALUES($storyline_id,'storyline',1,'current',1,1),
                          ($chapter_id,'chapter',1,'current',1,1);
                    INSERT INTO storylines(storyline_id,workflow_state,updated_at_ms)
                    VALUES($storyline_id,'active',1);
                    INSERT INTO chapters(chapter_id,storyline_id,ordinal,workflow_state,current_draft_path,updated_at_ms)
                    VALUES($chapter_id,$storyline_id,1,'draft',$draft_path,1);
                    """,
                    ("$storyline_id", storylineId),
                    ("$chapter_id", chapterId),
                    ("$draft_path", $"Draft/{chapterId}/chapter.md"));
            }

            var draftDirectory = Path.Combine(root, "Draft", chapterId);
            Directory.CreateDirectory(draftDirectory);
            var draftPath = Path.Combine(draftDirectory, "chapter.md");
            var blobStore = new ImmutableBlobStore(root);
            var fileMaterializer = new AtomicAuthorityMaterializer(root, blobStore);
            var materializer = new ChapterAuthorityMaterializer(databasePath, fileMaterializer);
            var faultInjector = new OneShotFaultInjector(faultPoint);
            var coordinator = new AuthorityTransactionCoordinator(
                databasePath,
                blobStore,
                materializer,
                faultInjector: faultInjector);
            var store = new SqliteChapterAuthorityStore(databasePath, coordinator);
            var reviewer = new DeterministicReviewer(outcome);
            var service = new ChapterAuthorityService(
                blobStore,
                coordinator,
                store,
                reviewer,
                LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
                authorizationService ?? Wp09Authorization);
            return new Wp05Fixture(root, databasePath, storylineId, chapterId, draftPath, blobStore, reviewer, coordinator, service);
        }

        public byte[] ReadBlob(string digest)
        {
            using var stream = blobStore.OpenRead(digest);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }

        public void AssertScalar<T>(T expected, string sql)
        {
            using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            var actual = (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            AssertWp05Equal(expected, actual, sql);
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static void ExecuteFixture(
            DbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var item in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = item.Name;
                parameter.Value = item.Value;
                command.Parameters.Add(parameter);
            }

            command.ExecuteNonQuery();
        }
    }

    private sealed class DeterministicReviewer(ChapterReviewOutcome outcome) : IChapterReviewer
    {
        public ChapterReviewOutcome Outcome { get; set; } = outcome;

        public ChapterReviewDecision Review(
            CandidateReviewInput candidate,
            Stream candidateContent,
            CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream();
            candidateContent.CopyTo(output);
            cancellationToken.ThrowIfCancellationRequested();
            return new ChapterReviewDecision(
                Outcome,
                $"{{\"outcome\":\"{Outcome.ToString().ToUpperInvariant()}\",\"digest\":\"{Hash(output.ToArray())}\"}}",
                DiagnosticsReference: $"fixture:{candidate.CandidateId}");
        }
    }

    private sealed class OneShotFaultInjector(AuthorityTransactionFaultPoint? point) : ITransactionFaultInjector
    {
        private bool injected;

        public void Inject(AuthorityTransactionFaultPoint faultPoint)
        {
            if (!injected && point == faultPoint)
            {
                injected = true;
                throw new InjectedWp05FaultException(faultPoint);
            }
        }
    }

    private sealed class InjectedWp05FaultException(AuthorityTransactionFaultPoint point) : Exception(point.ToString());
}
