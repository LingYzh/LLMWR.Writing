using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Infrastructure.Authority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static readonly byte[] BlobPayload = Encoding.UTF8.GetBytes("immutable-authority-artifact\n");

    private static void RunWp03Tests()
    {
        Run(nameof(SameBytesUseSameDigestAndPath), SameBytesUseSameDigestAndPath);
        Run(nameof(DifferentBytesUseDifferentDigests), DifferentBytesUseDifferentDigests);
        Run(nameof(LargeBlobIsStreamedWithoutWholeInputBuffer), LargeBlobIsStreamedWithoutWholeInputBuffer);
        Run(nameof(ExistingValidBlobDeduplicates), ExistingValidBlobDeduplicates);
        Run(nameof(CorruptExistingBlobIsRejected), CorruptExistingBlobIsRejected);
        Run(nameof(InterruptedTemporaryFileNeverBecomesFinalBlob), InterruptedTemporaryFileNeverBecomesFinalBlob);
        Run(nameof(ConcurrentDuplicateStagesAreSafe), ConcurrentDuplicateStagesAreSafe);
        Run(nameof(BlobPublishReadVerifyRoundTrip), BlobPublishReadVerifyRoundTrip);
        Run(nameof(PartialFinalBlobIsNeverVisible), PartialFinalBlobIsNeverVisible);
        Run(nameof(PendingTransactionIsDurableAndIdempotent), PendingTransactionIsDurableAndIdempotent);
        Run(nameof(SuccessfulTransactionCommitsMutationEventPointerAndMaterialization), SuccessfulTransactionCommitsMutationEventPointerAndMaterialization);
        Run(nameof(EveryPreCommitFaultRollsBackAuthorityMutation), EveryPreCommitFaultRollsBackAuthorityMutation);
        Run(nameof(EventAndPointerRollbackWithAuthorityMutation), EventAndPointerRollbackWithAuthorityMutation);
        Run(nameof(PostCommitFailureBecomesDirtyAndRollsForward), PostCommitFailureBecomesDirtyAndRollsForward);
        Run(nameof(RecoveryCanBeInterruptedAndRerunSafely), RecoveryCanBeInterruptedAndRerunSafely);
        Run(nameof(ThirdFailedRepairRequiresRecovery), ThirdFailedRepairRequiresRecovery);
        Run(nameof(PreCommitRetryReusesLogicalTransaction), PreCommitRetryReusesLogicalTransaction);
        Run(nameof(PostCommitRetryRecoversExistingTransaction), PostCommitRetryRecoversExistingTransaction);
        Run(nameof(RepeatedMaterializationIsIdentical), RepeatedMaterializationIsIdentical);
        Run(nameof(LockedMaterializationTargetLeavesCommittedAuthorityDirty), LockedMaterializationTargetLeavesCommittedAuthorityDirty);
        Run(nameof(MissingBlobDuringRecoveryRemainsRollForwardOnly), MissingBlobDuringRecoveryRemainsRollForwardOnly);
        Run(nameof(PendingRecoveryCleansTemporaryStateWithoutAuthorityChange), PendingRecoveryCleansTemporaryStateWithoutAuthorityChange);
        Run(nameof(AllNamedFaultPointsAreReachableAndClassified), AllNamedFaultPointsAreReachableAndClassified);
        Run(nameof(StartupRecoveryScansDirtyTransactionsAndIgnoresTerminalCleanup), StartupRecoveryScansDirtyTransactionsAndIgnoresTerminalCleanup);
    }

    private static void SameBytesUseSameDigestAndPath()
    {
        using var project = Wp03Project.Create();
        var first = Stage(project.BlobStore, BlobPayload);
        var second = Stage(project.BlobStore, BlobPayload);

        AssertEqual(first.Digest, second.Digest, "Equal bytes must have equal SHA-256 identities.");
        AssertEqual(first.FullPath, second.FullPath, "Equal bytes must resolve to one object path.");
        AssertTrue(second.Deduplicated, "The second identical blob must deduplicate.");
        AssertTrue(first.FullPath.EndsWith(Path.Combine(first.Digest[..2], first.Digest[2..]), StringComparison.Ordinal),
            "The object path does not use the required two-character fanout.");
    }

    private static void DifferentBytesUseDifferentDigests()
    {
        using var project = Wp03Project.Create();
        var first = Stage(project.BlobStore, BlobPayload);
        var second = Stage(project.BlobStore, Encoding.UTF8.GetBytes("different\n"));
        AssertFalse(first.Digest == second.Digest, "Different bytes must not share a digest.");
        AssertFalse(first.FullPath == second.FullPath, "Different bytes must not share an object path.");
    }

    private static void LargeBlobIsStreamedWithoutWholeInputBuffer()
    {
        using var project = Wp03Project.Create();
        using var stream = new PatternStream(24L * 1024 * 1024);
        var result = project.BlobStore.Stage(stream);
        AssertEqual(24L * 1024 * 1024, result.Length, "The streaming blob length changed.");
        AssertTrue(project.BlobStore.Verify(result.Digest), "The streamed large blob failed verification.");
        AssertTrue(stream.LargestReadRequest <= 128 * 1024, "Blob staging requested an unbounded input buffer.");
    }

    private static void ExistingValidBlobDeduplicates()
    {
        using var project = Wp03Project.Create();
        var first = Stage(project.BlobStore, BlobPayload);
        var writeTime = File.GetLastWriteTimeUtc(first.FullPath);
        var second = Stage(project.BlobStore, BlobPayload);
        AssertTrue(second.Deduplicated, "An existing valid target must be reused.");
        AssertEqual(writeTime, File.GetLastWriteTimeUtc(first.FullPath), "Deduplication rewrote the immutable target.");
    }

    private static void CorruptExistingBlobIsRejected()
    {
        using var project = Wp03Project.Create();
        var first = Stage(project.BlobStore, BlobPayload);
        File.WriteAllText(first.FullPath, "corrupt", Encoding.UTF8);
        AssertThrows<BlobCorruptionException>(
            () => Stage(project.BlobStore, BlobPayload),
            "A corrupt existing digest target must be rejected rather than overwritten.");
    }

    private static void InterruptedTemporaryFileNeverBecomesFinalBlob()
    {
        using var project = Wp03Project.Create();
        var shard = Path.Combine(project.BlobStore.ObjectsRoot, "aa");
        Directory.CreateDirectory(shard);
        var temporary = Path.Combine(shard, ".tmp-interrupted");
        File.WriteAllBytes(temporary, BlobPayload[..5]);
        AssertEqual(0L, CountFinalBlobs(project.BlobStore.ObjectsRoot), "A temporary artifact was treated as a final blob.");
        AssertEqual(1, project.BlobStore.CleanupTemporaryFiles(TimeSpan.Zero), "Interrupted temp cleanup did not remove the artifact.");
    }

    private static void ConcurrentDuplicateStagesAreSafe()
    {
        using var project = Wp03Project.Create();
        var results = new ConcurrentBag<BlobStageResult>();
        Parallel.For(0, 12, _ => results.Add(Stage(project.BlobStore, BlobPayload)));
        AssertEqual(1, results.Select(result => result.Digest).Distinct(StringComparer.Ordinal).Count(),
            "Concurrent duplicate staging produced divergent identities.");
        AssertEqual(1L, CountFinalBlobs(project.BlobStore.ObjectsRoot),
            "Concurrent duplicate staging published more than one final blob.");
        AssertTrue(project.BlobStore.Verify(results.First().Digest), "The concurrent winner is invalid.");
    }

    private static void BlobPublishReadVerifyRoundTrip()
    {
        using var project = Wp03Project.Create();
        var result = Stage(project.BlobStore, BlobPayload);
        using var read = project.BlobStore.OpenRead(result.Digest);
        using var memory = new MemoryStream();
        read.CopyTo(memory);
        AssertTrue(BlobPayload.AsSpan().SequenceEqual(memory.ToArray()), "Blob read did not round-trip exact bytes.");
        AssertTrue(project.BlobStore.Verify(result.Digest), "Published blob did not verify.");
    }

    private static void PartialFinalBlobIsNeverVisible()
    {
        using var project = Wp03Project.Create();
        using var source = new ThrowingReadStream(BlobPayload, throwAfterBytes: 8);
        AssertThrows<InjectedIoException>(
            () => project.BlobStore.Stage(source),
            "The injected streaming interruption was not observed.");
        AssertEqual(0L, CountFinalBlobs(project.BlobStore.ObjectsRoot), "A partial final blob became visible.");
    }

    private static void PendingTransactionIsDurableAndIdempotent()
    {
        using var project = Wp03Project.Create();
        var first = project.Coordinator.Begin("test", "pending-key");
        var second = project.Coordinator.Begin("test", "pending-key");
        AssertEqual(first.TransactionId, second.TransactionId, "Same idempotency key created two transactions.");
        AssertTrue(second.Existing, "Idempotent Begin did not report the durable operation.");
        AssertEqual("submitting", project.Scalar<string>(
            $"SELECT status FROM authority_transactions WHERE transaction_id='{first.TransactionId}';"),
            "PENDING was not durably represented.");
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM authority_transactions;"),
            "Idempotent Begin persisted a duplicate logical transaction.");
    }

    private static void SuccessfulTransactionCommitsMutationEventPointerAndMaterialization()
    {
        using var project = Wp03Project.Create();
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "success-key");
        var request = CreateCommitRequest(project, staged.Digest);

        var completed = project.Coordinator.Commit(handle, request, InsertFixtureMutation);
        AssertTrue(completed.State == AuthorityTransactionState.Complete, "Successful commit did not reach COMPLETE.");
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
            "Authority mutation did not commit.");
        AssertEqual(2L, project.Scalar<long>("SELECT COUNT(*) FROM authority_events;"),
            "Business event plus durable materialization plan were not committed exactly once.");
        AssertEqual("revision-next", project.Scalar<string>(
            $"SELECT current_manuscript_revision_id FROM chapters WHERE chapter_id='{Id2}';"),
            "Current pointer did not commit.");
        AssertTrue(File.Exists(project.MaterializedPath), "Post-commit materialization was not published.");
        AssertTrue(BlobPayload.AsSpan().SequenceEqual(File.ReadAllBytes(project.MaterializedPath)),
            "Materialized bytes differ from the committed blob.");
    }

    private static void EveryPreCommitFaultRollsBackAuthorityMutation()
    {
        AuthorityTransactionFaultPoint[] points =
        [
            AuthorityTransactionFaultPoint.AfterSqliteTransactionBegin,
            AuthorityTransactionFaultPoint.AfterAuthorityMutation,
            AuthorityTransactionFaultPoint.AfterAuthorityEventAppend,
            AuthorityTransactionFaultPoint.AfterCurrentPointerUpdate,
            AuthorityTransactionFaultPoint.BeforeSqliteCommit
        ];

        foreach (var point in points)
        {
            using var project = Wp03Project.Create(new ThrowAtFaultPoint(point));
            project.CreateAuthorityFixture();
            var staged = Stage(project.BlobStore, BlobPayload);
            var handle = project.Coordinator.Begin("test", "pre-" + point);
            AssertThrows<InjectedTransactionFailureException>(
                () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
                $"Fault point {point} was not observed.");
            AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
                $"Pre-commit fault {point} left an Authority mutation.");
            AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM authority_events;"),
                $"Pre-commit fault {point} left a committed event.");
            AssertEqual("revision-old", project.Scalar<string>(
                $"SELECT current_manuscript_revision_id FROM chapters WHERE chapter_id='{Id2}';"),
                $"Pre-commit fault {point} left a pointer transition.");
            AssertTrue(project.Scalar<object?>(
                $"SELECT committed_at_ms FROM authority_transactions WHERE transaction_id='{handle.TransactionId}';") is null,
                $"Pre-commit fault {point} produced a commit marker.");
        }
    }

    private static void EventAndPointerRollbackWithAuthorityMutation()
    {
        using var project = Wp03Project.Create(new ThrowAtFaultPoint(AuthorityTransactionFaultPoint.BeforeSqliteCommit));
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "atomic-rollback");
        AssertThrows<InjectedTransactionFailureException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Pre-commit failure was not injected.");
        AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"), "Mutation survived rollback.");
        AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM authority_events;"), "Event survived rollback.");
        AssertEqual("revision-old", project.Scalar<string>(
            $"SELECT current_manuscript_revision_id FROM chapters WHERE chapter_id='{Id2}';"),
            "Pointer survived rollback.");
    }

    private static void PostCommitFailureBecomesDirtyAndRollsForward()
    {
        var injector = new ThrowAtFaultPoint(AuthorityTransactionFaultPoint.AfterSqliteCommit);
        using var project = Wp03Project.Create(injector);
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "post-commit");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Post-commit interruption was not reported.");
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
            "Post-commit failure incorrectly rolled back Authority.");
        AssertTrue(project.Coordinator.Inspect(handle.TransactionId).State == AuthorityTransactionState.CommittedButDirty,
            "Post-commit failure did not become COMMITTED_BUT_DIRTY.");

        injector.Enabled = false;
        var recovered = project.Coordinator.Recover(handle.TransactionId);
        AssertTrue(recovered.State == AuthorityTransactionState.Complete, "Roll-forward recovery did not complete.");
        AssertTrue(File.Exists(project.MaterializedPath), "Roll-forward did not materialize the committed artifact.");
    }

    private static void RecoveryCanBeInterruptedAndRerunSafely()
    {
        var injector = new ThrowAtFaultPoint(AuthorityTransactionFaultPoint.BeforeMaterialization);
        using var project = Wp03Project.Create(injector);
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "recovery-rerun");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Initial materialization failure was not observed.");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Recover(handle.TransactionId),
            "Interrupted recovery was not observed.");
        AssertEqual(1, project.Coordinator.Inspect(handle.TransactionId).RepairAttempts,
            "Interrupted recovery did not persist its attempt.");

        injector.Enabled = false;
        project.Coordinator.Recover(handle.TransactionId);
        project.Coordinator.Recover(handle.TransactionId);
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
            "Repeated recovery duplicated the Authority mutation.");
        AssertEqual(2L, project.Scalar<long>("SELECT COUNT(*) FROM authority_events;"),
            "Repeated recovery duplicated Authority events.");
    }

    private static void ThirdFailedRepairRequiresRecovery()
    {
        var materializer = new AlwaysFailMaterializer();
        using var project = Wp03Project.Create(materializer: materializer);
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "repair-limit");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Initial materialization failure was not observed.");
        AssertThrows<AuthorityTransactionException>(() => project.Coordinator.Recover(handle.TransactionId), "Repair 1 did not fail.");
        AssertThrows<AuthorityTransactionException>(() => project.Coordinator.Recover(handle.TransactionId), "Repair 2 did not fail.");
        AssertThrows<AuthorityRecoveryRequiredException>(() => project.Coordinator.Recover(handle.TransactionId), "Repair 3 did not stop.");
        var inspection = project.Coordinator.Inspect(handle.TransactionId);
        AssertTrue(inspection.State == AuthorityTransactionState.RecoveryRequired, "Third failure did not enter RECOVERY_REQUIRED.");
        AssertEqual(3, inspection.RepairAttempts, "Repair attempt count is not durable.");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Begin("test", "blocked-by-recovery"),
            "RECOVERY_REQUIRED did not block a new Authority operation.");
    }

    private static void PreCommitRetryReusesLogicalTransaction()
    {
        var injector = new ThrowAtFaultPoint(AuthorityTransactionFaultPoint.BeforeSqliteCommit);
        using var project = Wp03Project.Create(injector);
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "pre-retry");
        AssertThrows<InjectedTransactionFailureException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Pre-commit retry fixture did not fail.");

        injector.Enabled = false;
        var same = project.Coordinator.Begin("test", "pre-retry");
        AssertEqual(handle.TransactionId, same.TransactionId, "Pre-commit retry created a second transaction.");
        project.Coordinator.Commit(same, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation);
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM authority_transactions;"),
            "Pre-commit retry duplicated the transaction row.");
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
            "Pre-commit retry did not produce exactly one mutation.");
    }

    private static void PostCommitRetryRecoversExistingTransaction()
    {
        var injector = new ThrowAtFaultPoint(AuthorityTransactionFaultPoint.BeforeMaterialization);
        using var project = Wp03Project.Create(injector);
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "post-retry");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Post-commit retry fixture did not fail.");

        injector.Enabled = false;
        var same = project.Coordinator.Begin("test", "post-retry");
        AssertEqual(handle.TransactionId, same.TransactionId, "Post-commit retry created a second transaction.");
        var completed = project.Coordinator.Commit(same, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation);
        AssertTrue(completed.State == AuthorityTransactionState.Complete, "Post-commit retry did not recover the existing operation.");
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
            "Post-commit retry repeated the Authority mutation.");
    }

    private static void RepeatedMaterializationIsIdentical()
    {
        using var project = Wp03Project.Create();
        var staged = Stage(project.BlobStore, BlobPayload);
        var plans = new[] { new AuthorityMaterializationPlan(Wp03Project.MaterializedRelativePath, staged.Digest) };
        project.Materializer.Materialize("materialize-repeat", plans);
        var first = File.ReadAllBytes(project.MaterializedPath);
        project.Materializer.Materialize("materialize-repeat", plans);
        project.Materializer.Verify("materialize-repeat", plans);
        AssertTrue(first.AsSpan().SequenceEqual(File.ReadAllBytes(project.MaterializedPath)),
            "Repeated materialization changed deterministic output.");
    }

    private static void LockedMaterializationTargetLeavesCommittedAuthorityDirty()
    {
        using var project = Wp03Project.Create();
        project.CreateAuthorityFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(project.MaterializedPath)!);
        File.WriteAllText(project.MaterializedPath, "old", Encoding.UTF8);
        using var locked = new FileStream(project.MaterializedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "locked-target");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "A locked destination did not fail materialization.");
        AssertTrue(project.Coordinator.Inspect(handle.TransactionId).State == AuthorityTransactionState.CommittedButDirty,
            "Locked destination did not preserve committed-but-dirty state.");
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
            "Locked destination rolled back committed Authority.");
    }

    private static void MissingBlobDuringRecoveryRemainsRollForwardOnly()
    {
        var injector = new ThrowAtFaultPoint(AuthorityTransactionFaultPoint.BeforeMaterialization);
        using var project = Wp03Project.Create(injector);
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "missing-blob");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Missing-blob fixture did not reach dirty state.");
        File.Delete(staged.FullPath);
        injector.Enabled = false;
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Recover(handle.TransactionId),
            "Missing committed blob was not detected during recovery.");
        AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
            "Missing blob recovery incorrectly rolled back Authority.");
        AssertTrue(project.Coordinator.Inspect(handle.TransactionId).State == AuthorityTransactionState.CommittedButDirty,
            "Missing blob recovery left roll-forward state.");
    }

    private static void PendingRecoveryCleansTemporaryStateWithoutAuthorityChange()
    {
        using var project = Wp03Project.Create();
        var handle = project.Coordinator.Begin("test", "pending-cleanup");
        var shard = Path.Combine(project.BlobStore.ObjectsRoot, "ff");
        Directory.CreateDirectory(shard);
        File.WriteAllText(Path.Combine(shard, ".tmp-pending"), "partial", Encoding.UTF8);
        var result = project.Coordinator.Recover(handle.TransactionId);
        AssertTrue(result.State == AuthorityTransactionState.Failed, "Uncommitted PENDING was not classified as rolled back.");
        AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM authority_events;"),
            "Pending cleanup created Authority events.");
        AssertEqual(0L, CountFinalBlobs(project.BlobStore.ObjectsRoot), "Pending cleanup published a staged artifact.");
    }

    private static void AllNamedFaultPointsAreReachableAndClassified()
    {
        var preCommitPoints = new HashSet<AuthorityTransactionFaultPoint>
        {
            AuthorityTransactionFaultPoint.BeforeBlobStage,
            AuthorityTransactionFaultPoint.AfterBlobStage,
            AuthorityTransactionFaultPoint.AfterPendingTransactionCreated,
            AuthorityTransactionFaultPoint.BeforeSqliteTransaction,
            AuthorityTransactionFaultPoint.AfterSqliteTransactionBegin,
            AuthorityTransactionFaultPoint.AfterAuthorityMutation,
            AuthorityTransactionFaultPoint.AfterAuthorityEventAppend,
            AuthorityTransactionFaultPoint.AfterCurrentPointerUpdate,
            AuthorityTransactionFaultPoint.BeforeSqliteCommit
        };

        foreach (var point in Enum.GetValues<AuthorityTransactionFaultPoint>())
        {
            var injector = new RecordingFaultInjector(point);
            using var project = Wp03Project.Create(injector);
            project.CreateAuthorityFixture();
            var handle = BeginForFaultPoint(project, point);
            var staged = StageForFaultPoint(project, handle, point);

            if (point is AuthorityTransactionFaultPoint.BeforeBlobStage or AuthorityTransactionFaultPoint.AfterBlobStage)
            {
                AssertTrue(injector.Observed.Contains(point), $"Fault point {point} was not reachable.");
                AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM authority_events;"),
                    $"Blob fault {point} changed Authority.");
                continue;
            }

            if (point == AuthorityTransactionFaultPoint.AfterPendingTransactionCreated)
            {
                AssertTrue(injector.Observed.Contains(point), $"Fault point {point} was not reachable.");
                AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM authority_events;"),
                    "PENDING creation fault changed Authority.");
                continue;
            }

            AssertThrows<Exception>(
                () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
                $"Fault point {point} was not injected.");
            AssertTrue(injector.Observed.Contains(point), $"Fault point {point} was not reachable.");

            if (preCommitPoints.Contains(point))
            {
                AssertEqual(0L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
                    $"Pre-commit fault {point} changed Authority.");
            }
            else
            {
                AssertEqual(1L, project.Scalar<long>("SELECT COUNT(*) FROM wp03_authority_fixture;"),
                    $"Post-commit fault {point} did not preserve committed Authority.");
                var state = project.Coordinator.Inspect(handle.TransactionId).State;
                if (point == AuthorityTransactionFaultPoint.AfterMarkComplete)
                {
                    AssertTrue(state == AuthorityTransactionState.Complete,
                        "AfterMarkComplete fault must observe a durable COMPLETE state.");
                }
                else
                {
                    AssertTrue(state == AuthorityTransactionState.CommittedButDirty,
                        $"Post-commit fault {point} did not preserve a recoverable dirty state.");
                }
            }
        }
    }

    private static void StartupRecoveryScansDirtyTransactionsAndIgnoresTerminalCleanup()
    {
        var injector = new ThrowAtFaultPoint(AuthorityTransactionFaultPoint.BeforeMaterialization);
        using var project = Wp03Project.Create(injector);
        project.CreateAuthorityFixture();
        var staged = Stage(project.BlobStore, BlobPayload);
        var handle = project.Coordinator.Begin("test", "startup-dirty");
        AssertThrows<AuthorityTransactionException>(
            () => project.Coordinator.Commit(handle, CreateCommitRequest(project, staged.Digest), InsertFixtureMutation),
            "Startup recovery fixture did not become dirty.");

        injector.Enabled = false;
        var firstPass = project.Coordinator.RecoverIncomplete();
        AssertEqual(1, firstPass.Count, "Startup recovery did not scan the dirty transaction.");
        AssertTrue(firstPass[0].State == AuthorityTransactionState.Complete,
            "Startup recovery did not roll the committed transaction forward.");
        AssertEqual(0, project.Coordinator.RecoverIncomplete().Count,
            "Startup recovery rescanned a terminal transaction.");

        var pending = project.Coordinator.Begin("test", "startup-pending");
        var pendingPass = project.Coordinator.RecoverIncomplete();
        AssertEqual(1, pendingPass.Count, "Startup recovery did not scan PENDING state.");
        AssertTrue(pendingPass[0].TransactionId == pending.TransactionId &&
            pendingPass[0].State == AuthorityTransactionState.Failed,
            "Startup recovery did not clean uncommitted PENDING state.");
        AssertEqual(0, project.Coordinator.RecoverIncomplete().Count,
            "Startup recovery repeatedly scanned terminal PENDING cleanup.");
    }

    private static AuthorityTransactionHandle BeginForFaultPoint(
        Wp03Project project,
        AuthorityTransactionFaultPoint point)
    {
        try
        {
            return project.Coordinator.Begin("test", "all-points-" + point);
        }
        catch (InjectedTransactionFailureException) when (point == AuthorityTransactionFaultPoint.AfterPendingTransactionCreated)
        {
            using var connection = OpenConfigured(project.DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT transaction_id,idempotency_key FROM authority_transactions WHERE idempotency_key=$key;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$key";
            parameter.Value = "all-points-" + point;
            command.Parameters.Add(parameter);
            using var reader = command.ExecuteReader();
            AssertTrue(reader.Read(), "PENDING record was not durable before its fault point.");
            return new AuthorityTransactionHandle(
                reader.GetString(0), reader.GetString(1), AuthorityTransactionState.Pending, Existing: true);
        }
    }

    private static BlobStageResult StageForFaultPoint(
        Wp03Project project,
        AuthorityTransactionHandle handle,
        AuthorityTransactionFaultPoint point)
    {
        if (point is AuthorityTransactionFaultPoint.BeforeBlobStage or AuthorityTransactionFaultPoint.AfterBlobStage)
        {
            using var source = new MemoryStream(BlobPayload, writable: false);
            AssertThrows<InjectedTransactionFailureException>(
                () => project.Coordinator.StageBlob(handle, source),
                $"Blob fault point {point} was not injected.");
            return Stage(project.BlobStore, BlobPayload);
        }

        return Stage(project.BlobStore, BlobPayload);
    }

    private static AuthorityCommitRequest CreateCommitRequest(Wp03Project project, string digest)
    {
        return new AuthorityCommitRequest(
            [new AuthorityEventData(Guid.NewGuid().ToString(), "test", Id2, "test.committed", "{}")],
            [new AuthorityCurrentPointerUpdate(AuthorityCurrentPointerKind.ChapterManuscriptRevision, Id2, "revision-next")],
            [new AuthorityMaterializationPlan(Wp03Project.MaterializedRelativePath, digest)]);
    }

    private static void InsertFixtureMutation(AuthoritySqliteTransactionContext context)
    {
        using var command = context.CreateCommand(
            "INSERT INTO wp03_authority_fixture(fixture_id,value) VALUES('fixture-1','committed');");
        command.ExecuteNonQuery();
    }

    private static BlobStageResult Stage(ImmutableBlobStore store, byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        return store.Stage(stream);
    }

    private static long CountFinalBlobs(string objectsRoot)
    {
        return Directory.EnumerateFiles(objectsRoot, "*", SearchOption.AllDirectories)
            .LongCount(path => !Path.GetFileName(path).StartsWith(".tmp-", StringComparison.Ordinal));
    }

    private sealed class Wp03Project : IDisposable
    {
        private Wp03Project(
            string root,
            ImmutableBlobStore blobStore,
            IAuthorityMaterializer materializer,
            AuthorityTransactionCoordinator coordinator)
        {
            Root = root;
            BlobStore = blobStore;
            Materializer = materializer;
            Coordinator = coordinator;
            DatabasePath = Path.Combine(root, ".llmw", "project.db");
        }

        public string Root { get; }
        public string DatabasePath { get; }
        public ImmutableBlobStore BlobStore { get; }
        public IAuthorityMaterializer Materializer { get; }
        public AuthorityTransactionCoordinator Coordinator { get; }
        public static string MaterializedRelativePath => Path.Combine("Manuscript", "current", "chapter-test.md");
        public string MaterializedPath => Path.Combine(Root, MaterializedRelativePath);

        public static Wp03Project Create(
            ITransactionFaultInjector? faultInjector = null,
            IAuthorityMaterializer? materializer = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP03", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, ".llmw", "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp03-tests", 1735689600000);
            var blobStore = new ImmutableBlobStore(root);
            var actualMaterializer = materializer ?? new AtomicAuthorityMaterializer(root, blobStore);
            var coordinator = new AuthorityTransactionCoordinator(
                databasePath,
                blobStore,
                actualMaterializer,
                faultInjector: faultInjector,
                clock: () => 1735689600000);
            return new Wp03Project(root, blobStore, actualMaterializer, coordinator);
        }

        public void CreateAuthorityFixture()
        {
            using var connection = OpenConfigured(DatabasePath);
            Execute(
                connection,
                $"""
                CREATE TABLE wp03_authority_fixture(
                  fixture_id TEXT PRIMARY KEY,
                  value TEXT NOT NULL
                ) STRICT;
                INSERT INTO objects(object_id,object_type,schema_version,status,created_at_ms,updated_at_ms)
                VALUES ('{Id1}','storyline',1,'current',1,1),('{Id2}','chapter',1,'current',1,1);
                INSERT INTO storylines(storyline_id,workflow_state,updated_at_ms)
                VALUES ('{Id1}','draft',1);
                INSERT INTO chapters(
                  chapter_id,storyline_id,ordinal,workflow_state,current_manuscript_revision_id,updated_at_ms)
                VALUES ('{Id2}','{Id1}',1,'draft','revision-old',1);
                """);
        }

        public T Scalar<T>(string sql)
        {
            using var connection = OpenConfigured(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            if (value is null || value is DBNull)
            {
                return default!;
            }

            return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class ThrowAtFaultPoint : ITransactionFaultInjector
    {
        private readonly AuthorityTransactionFaultPoint point;

        public ThrowAtFaultPoint(AuthorityTransactionFaultPoint point)
        {
            this.point = point;
        }

        public bool Enabled { get; set; } = true;

        public void Inject(AuthorityTransactionFaultPoint observed)
        {
            if (Enabled && observed == point)
            {
                throw new InjectedTransactionFailureException(point);
            }
        }
    }

    private sealed class RecordingFaultInjector : ITransactionFaultInjector
    {
        private readonly AuthorityTransactionFaultPoint target;

        public RecordingFaultInjector(AuthorityTransactionFaultPoint target)
        {
            this.target = target;
        }

        public HashSet<AuthorityTransactionFaultPoint> Observed { get; } = [];

        public void Inject(AuthorityTransactionFaultPoint point)
        {
            Observed.Add(point);
            if (point == target)
            {
                throw new InjectedTransactionFailureException(point);
            }
        }
    }

    private sealed class AlwaysFailMaterializer : IAuthorityMaterializer
    {
        public void Materialize(
            string transactionId,
            IReadOnlyList<AuthorityMaterializationPlan> plans,
            CancellationToken cancellationToken = default)
        {
            throw new InjectedIoException();
        }

        public void Verify(
            string transactionId,
            IReadOnlyList<AuthorityMaterializationPlan> plans,
            CancellationToken cancellationToken = default)
        {
            throw new InjectedIoException();
        }
    }

    private sealed class PatternStream : Stream
    {
        private long remaining;

        public PatternStream(long length)
        {
            remaining = length;
        }

        public int LargestReadRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestReadRequest = Math.Max(LargestReadRequest, count);
            var read = (int)Math.Min(count, remaining);
            for (var index = 0; index < read; index++)
            {
                buffer[offset + index] = (byte)((remaining + index) % 251);
            }

            remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingReadStream : MemoryStream
    {
        private readonly int throwAfterBytes;

        public ThrowingReadStream(byte[] buffer, int throwAfterBytes)
            : base(buffer, writable: false)
        {
            this.throwAfterBytes = throwAfterBytes;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= throwAfterBytes)
            {
                throw new InjectedIoException();
            }

            return base.Read(buffer, offset, Math.Min(count, throwAfterBytes - (int)Position));
        }
    }

    private sealed class InjectedTransactionFailureException : Exception
    {
        public InjectedTransactionFailureException(AuthorityTransactionFaultPoint point)
            : base($"Injected transaction failure at {point}.")
        {
        }
    }

    private sealed class InjectedIoException : IOException
    {
    }
}
