namespace LLMW.Writing.Application.Authority;

public interface IImmutableBlobStore
{
    BlobStageResult Stage(Stream source, string? expectedDigest = null, CancellationToken cancellationToken = default);

    Stream OpenRead(string digest);

    bool Verify(string digest, CancellationToken cancellationToken = default);
}

public sealed record BlobStageResult(string Digest, string FullPath, long Length, bool Deduplicated);

public interface IAuthorityTransactionCoordinator
{
    AuthorityTransactionHandle Begin(string transactionKind, string idempotencyKey);

    BlobStageResult StageBlob(
        AuthorityTransactionHandle handle,
        Stream source,
        string? expectedDigest = null,
        CancellationToken cancellationToken = default);

    AuthorityTransactionHandle Commit(
        AuthorityTransactionHandle handle,
        AuthorityCommitRequest request,
        CancellationToken cancellationToken = default);

    AuthorityTransactionHandle Recover(string transactionId, CancellationToken cancellationToken = default);

    IReadOnlyList<AuthorityRecoveryResult> RecoverIncomplete(CancellationToken cancellationToken = default);

    AuthorityRecoveryResult Inspect(string transactionId);
}

public sealed record AuthorityTransactionHandle(
    string TransactionId,
    string IdempotencyKey,
    AuthorityTransactionState State,
    bool Existing);

public enum AuthorityTransactionState
{
    Pending,
    CommittedButDirty,
    Complete,
    RecoveryRequired,
    Failed
}

public sealed record AuthorityEventData(
    string EventId,
    string AggregateType,
    string AggregateId,
    string EventType,
    string EventPayloadJson);

public sealed record AuthorityCurrentPointerUpdate(
    AuthorityCurrentPointerKind Kind,
    string AggregateId,
    string PointerValue);

public enum AuthorityCurrentPointerKind
{
    ChapterManuscriptRevision,
    StorylineAcceptedSnapshot
}

public sealed record AuthorityCommitRequest(
    IReadOnlyList<AuthorityEventData> Events,
    IReadOnlyList<AuthorityCurrentPointerUpdate> PointerUpdates,
    IReadOnlyList<AuthorityMaterializationPlan> Materializations)
{
    public static AuthorityCommitRequest Empty { get; } = new([], [], []);
}

public sealed record AuthorityRecoveryResult(
    string TransactionId,
    AuthorityTransactionState State,
    int RepairAttempts,
    string? FailureCode);

public interface IAuthorityMaterializer
{
    void Materialize(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default);

    void Verify(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default);
}

public sealed record AuthorityMaterializationPlan(string TargetRelativePath, string BlobDigest);

public interface ITransactionFaultInjector
{
    void Inject(AuthorityTransactionFaultPoint point);
}

public sealed class NoOpTransactionFaultInjector : ITransactionFaultInjector
{
    public static NoOpTransactionFaultInjector Instance { get; } = new();

    private NoOpTransactionFaultInjector()
    {
    }

    public void Inject(AuthorityTransactionFaultPoint point)
    {
    }
}

public enum AuthorityTransactionFaultPoint
{
    BeforeBlobStage,
    AfterBlobStage,
    AfterPendingTransactionCreated,
    BeforeSqliteTransaction,
    AfterSqliteTransactionBegin,
    AfterAuthorityMutation,
    AfterAuthorityEventAppend,
    AfterCurrentPointerUpdate,
    BeforeSqliteCommit,
    AfterSqliteCommit,
    BeforeMaterialization,
    AfterMaterialization,
    BeforeMaterializationVerify,
    AfterMaterializationVerify,
    BeforeMarkComplete,
    AfterMarkComplete
}
