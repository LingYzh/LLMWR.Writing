using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Authority;

public sealed class AuthorityTransactionCoordinator : IAuthorityTransactionCoordinator
{
    private const string MaterializationPlanEvent = "wp03.materialization_plan";
    private const string PendingRecoveryState = "pending";
    private const string DirtyRecoveryState = "committed_but_dirty";
    private const string RecoveryRequiredState = "recovery_required";
    private const int RepairLimit = 3;

    private readonly string databasePath;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly ImmutableBlobStore blobStore;
    private readonly IAuthorityMaterializer materializer;
    private readonly ITransactionFaultInjector faultInjector;
    private readonly Func<long> clock;

    public AuthorityTransactionCoordinator(
        string databasePath,
        ImmutableBlobStore blobStore,
        IAuthorityMaterializer materializer,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        ITransactionFaultInjector? faultInjector = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.faultInjector = faultInjector ?? NoOpTransactionFaultInjector.Instance;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public AuthorityTransactionHandle Begin(string transactionKind, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        using var connection = connectionFactory.OpenConfigured(databasePath);
        var existing = ReadByIdempotencyKey(connection, idempotencyKey);
        if (existing is not null)
        {
            if (existing.State == AuthorityTransactionState.Failed && existing.CommittedAt is null)
            {
                EnsureAuthorityIsWritable(connection, existing.TransactionId);
                using var reactivate = connection.CreateCommand();
                reactivate.CommandText =
                    """
                    UPDATE authority_transactions
                    SET status='submitting', recovery_state='pending', failure_code=NULL,
                        committed_at_ms=NULL, completed_at_ms=NULL
                    WHERE transaction_id=$transaction_id;
                    """;
                AddParameter(reactivate, "$transaction_id", existing.TransactionId);
                reactivate.ExecuteNonQuery();
                return new AuthorityTransactionHandle(
                    existing.TransactionId,
                    idempotencyKey,
                    AuthorityTransactionState.Pending,
                    Existing: true);
            }

            return new AuthorityTransactionHandle(
                existing.TransactionId,
                idempotencyKey,
                existing.State,
                Existing: true);
        }

        EnsureAuthorityIsWritable(connection, exceptTransactionId: null);
        var transactionId = Guid.NewGuid().ToString();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO authority_transactions(
                    transaction_id,transaction_kind,idempotency_key,project_submission_state,
                    status,recovery_state,started_at_ms)
                VALUES($transaction_id,$transaction_kind,$idempotency_key,'idle','submitting','pending',$now);
                """;
            AddParameter(command, "$transaction_id", transactionId);
            AddParameter(command, "$transaction_kind", transactionKind);
            AddParameter(command, "$idempotency_key", idempotencyKey);
            AddParameter(command, "$now", clock());
            command.ExecuteNonQuery();
        }

        faultInjector.Inject(AuthorityTransactionFaultPoint.AfterPendingTransactionCreated);
        return new AuthorityTransactionHandle(
            transactionId,
            idempotencyKey,
            AuthorityTransactionState.Pending,
            Existing: false);
    }

    public BlobStageResult StageBlob(
        AuthorityTransactionHandle handle,
        Stream source,
        string? expectedDigest = null,
        CancellationToken cancellationToken = default)
    {
        RequirePending(handle);
        faultInjector.Inject(AuthorityTransactionFaultPoint.BeforeBlobStage);
        var result = blobStore.Stage(source, expectedDigest, cancellationToken);
        faultInjector.Inject(AuthorityTransactionFaultPoint.AfterBlobStage);
        return result;
    }

    public AuthorityTransactionHandle Commit(
        AuthorityTransactionHandle handle,
        AuthorityCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        return Commit(handle, request, authorityMutation: null, cancellationToken);
    }

    public AuthorityTransactionHandle Commit(
        AuthorityTransactionHandle handle,
        AuthorityCommitRequest request,
        Action<AuthoritySqliteTransactionContext>? authorityMutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var current = GetByIdempotencyKey(handle.IdempotencyKey)
            ?? throw new AuthorityTransactionException("The Authority transaction does not exist.");
        if (!StringComparer.Ordinal.Equals(current.TransactionId, handle.TransactionId))
        {
            throw new AuthorityTransactionException("The idempotency key belongs to another transaction identity.");
        }

        if (current.State == AuthorityTransactionState.Complete)
        {
            return ToHandle(current, Existing: true);
        }

        if (current.State is AuthorityTransactionState.CommittedButDirty or AuthorityTransactionState.RecoveryRequired)
        {
            return Recover(handle.TransactionId, cancellationToken) with { Existing = true };
        }

        if (current.State != AuthorityTransactionState.Pending)
        {
            throw new AuthorityTransactionException(
                $"Transaction '{handle.TransactionId}' cannot commit from state '{current.State}'.");
        }

        try
        {
            CommitDatabase(handle.TransactionId, request, authorityMutation, cancellationToken);
        }
        catch (PostCommitFaultException exception)
        {
            MarkCommittedButDirty(handle.TransactionId, exception.InnerException ?? exception);
            throw new AuthorityTransactionException(
                $"Authority transaction '{handle.TransactionId}' committed but post-commit processing was interrupted.",
                exception.InnerException ?? exception);
        }
        catch
        {
            throw;
        }

        try
        {
            CompleteMaterialization(handle.TransactionId, request.Materializations, cancellationToken);
            return new AuthorityTransactionHandle(
                handle.TransactionId,
                handle.IdempotencyKey,
                AuthorityTransactionState.Complete,
                Existing: handle.Existing);
        }
        catch (Exception exception)
        {
            MarkCommittedButDirty(handle.TransactionId, exception);
            throw new AuthorityTransactionException(
                $"Authority transaction '{handle.TransactionId}' committed but materialization is dirty.",
                exception);
        }
    }

    public AuthorityTransactionHandle Recover(string transactionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        var record = ReadByTransactionId(connection, transactionId)
            ?? throw new AuthorityTransactionException($"Transaction '{transactionId}' does not exist.");

        if (record.State == AuthorityTransactionState.Complete)
        {
            return ToHandle(record, Existing: true);
        }

        if (record.State == AuthorityTransactionState.RecoveryRequired)
        {
            throw new AuthorityRecoveryRequiredException(transactionId);
        }

        if (record.CommittedAt is null)
        {
            using var cleanup = connection.CreateCommand();
            cleanup.CommandText =
                """
                UPDATE authority_transactions
                SET status='failed', recovery_state='rolled_back', failure_code='uncommitted_pending_cleaned'
                WHERE transaction_id=$transaction_id;
                """;
            AddParameter(cleanup, "$transaction_id", transactionId);
            cleanup.ExecuteNonQuery();
            blobStore.CleanupTemporaryFiles(TimeSpan.Zero);
            return new AuthorityTransactionHandle(
                transactionId,
                record.IdempotencyKey,
                AuthorityTransactionState.Failed,
                Existing: true);
        }

        var plans = ReadMaterializationPlans(connection, transactionId);
        try
        {
            CompleteMaterialization(transactionId, plans, cancellationToken);
            return new AuthorityTransactionHandle(
                transactionId,
                record.IdempotencyKey,
                AuthorityTransactionState.Complete,
                Existing: true);
        }
        catch (Exception exception)
        {
            var attempts = ParseRepairAttempts(record.FailureCode) + 1;
            MarkRecoveryFailure(connection, transactionId, attempts, exception);
            if (attempts >= RepairLimit)
            {
                throw new AuthorityRecoveryRequiredException(transactionId);
            }

            throw new AuthorityTransactionException(
                $"Repair attempt {attempts} failed for Authority transaction '{transactionId}'.",
                exception);
        }
    }

    public IReadOnlyList<AuthorityRecoveryResult> RecoverIncomplete(
        CancellationToken cancellationToken = default)
    {
        List<string> transactionIds = [];
        using (var connection = connectionFactory.OpenConfigured(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT transaction_id
                FROM authority_transactions
                WHERE status IN('submitting','reviewing','resolving','accepting','committing','revalidating')
                ORDER BY started_at_ms, transaction_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                transactionIds.Add(reader.GetString(0));
            }
        }

        List<AuthorityRecoveryResult> results = [];
        foreach (var transactionId in transactionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Recover(transactionId, cancellationToken);
            }
            catch (AuthorityTransactionException)
            {
            }

            using var connection = connectionFactory.OpenConfigured(databasePath);
            var record = ReadByTransactionId(connection, transactionId)
                ?? throw new AuthorityTransactionException($"Transaction '{transactionId}' disappeared during recovery.");
            results.Add(new AuthorityRecoveryResult(
                transactionId,
                record.State,
                ParseRepairAttempts(record.FailureCode),
                record.FailureCode));
        }

        return results;
    }

    public AuthorityRecoveryResult Inspect(string transactionId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        var record = ReadByTransactionId(connection, transactionId)
            ?? throw new AuthorityTransactionException($"Transaction '{transactionId}' does not exist.");
        return new AuthorityRecoveryResult(
            transactionId,
            record.State,
            ParseRepairAttempts(record.FailureCode),
            record.FailureCode);
    }

    private void CommitDatabase(
        string transactionId,
        AuthorityCommitRequest request,
        Action<AuthoritySqliteTransactionContext>? authorityMutation,
        CancellationToken cancellationToken)
    {
        faultInjector.Inject(AuthorityTransactionFaultPoint.BeforeSqliteTransaction);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var context = new AuthoritySqliteTransactionContext(connection, transaction);
        var committed = false;
        try
        {
            faultInjector.Inject(AuthorityTransactionFaultPoint.AfterSqliteTransactionBegin);
            cancellationToken.ThrowIfCancellationRequested();
            authorityMutation?.Invoke(context);
            faultInjector.Inject(AuthorityTransactionFaultPoint.AfterAuthorityMutation);

            var nextSequence = ReadNextEventSequence(context);
            foreach (var authorityEvent in request.Events)
            {
                InsertEvent(context, transactionId, nextSequence++, authorityEvent);
            }

            if (request.Materializations.Count > 0)
            {
                var planEvent = new AuthorityEventData(
                    Guid.NewGuid().ToString(),
                    "authority_transaction",
                    transactionId,
                    MaterializationPlanEvent,
                    JsonSerializer.Serialize(request.Materializations));
                InsertEvent(context, transactionId, nextSequence, planEvent);
            }

            faultInjector.Inject(AuthorityTransactionFaultPoint.AfterAuthorityEventAppend);

            foreach (var pointer in request.PointerUpdates)
            {
                UpdateCurrentPointer(context, pointer);
            }

            faultInjector.Inject(AuthorityTransactionFaultPoint.AfterCurrentPointerUpdate);

            using (var command = context.CreateCommand(
                """
                UPDATE authority_transactions
                SET status='revalidating', recovery_state='committed_but_dirty',
                    committed_at_ms=$now, failure_code=NULL
                WHERE transaction_id=$transaction_id AND committed_at_ms IS NULL;
                """))
            {
                AddParameter(command, "$now", clock());
                AddParameter(command, "$transaction_id", transactionId);
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new AuthorityTransactionException("The pending transaction could not be marked committed.");
                }
            }

            faultInjector.Inject(AuthorityTransactionFaultPoint.BeforeSqliteCommit);
            transaction.Commit();
            committed = true;
            try
            {
                faultInjector.Inject(AuthorityTransactionFaultPoint.AfterSqliteCommit);
            }
            catch (Exception exception)
            {
                throw new PostCommitFaultException(exception);
            }
        }
        catch
        {
            if (!committed && transaction.Connection is not null)
            {
                transaction.Rollback();
            }

            throw;
        }
    }

    private void CompleteMaterialization(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken)
    {
        faultInjector.Inject(AuthorityTransactionFaultPoint.BeforeMaterialization);
        materializer.Materialize(transactionId, plans, cancellationToken);
        faultInjector.Inject(AuthorityTransactionFaultPoint.AfterMaterialization);
        faultInjector.Inject(AuthorityTransactionFaultPoint.BeforeMaterializationVerify);
        materializer.Verify(transactionId, plans, cancellationToken);
        faultInjector.Inject(AuthorityTransactionFaultPoint.AfterMaterializationVerify);
        faultInjector.Inject(AuthorityTransactionFaultPoint.BeforeMarkComplete);

        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE authority_transactions
            SET status='complete', recovery_state='none', completed_at_ms=$now, failure_code=NULL
            WHERE transaction_id=$transaction_id AND committed_at_ms IS NOT NULL;
            """;
        AddParameter(command, "$now", clock());
        AddParameter(command, "$transaction_id", transactionId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new AuthorityTransactionException("The committed transaction could not be marked complete.");
        }

        faultInjector.Inject(AuthorityTransactionFaultPoint.AfterMarkComplete);
    }

    private void MarkCommittedButDirty(string transactionId, Exception exception)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE authority_transactions
            SET status='revalidating', recovery_state='committed_but_dirty', failure_code=$failure_code
            WHERE transaction_id=$transaction_id AND committed_at_ms IS NOT NULL AND status <> 'complete';
            """;
        AddParameter(command, "$failure_code", FormatFailureCode(0, exception));
        AddParameter(command, "$transaction_id", transactionId);
        command.ExecuteNonQuery();
    }

    private static void MarkRecoveryFailure(
        DbConnection connection,
        string transactionId,
        int attempts,
        Exception exception)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE authority_transactions
            SET status='revalidating', recovery_state=$recovery_state, failure_code=$failure_code
            WHERE transaction_id=$transaction_id AND committed_at_ms IS NOT NULL;
            """;
        AddParameter(command, "$recovery_state", attempts >= RepairLimit ? RecoveryRequiredState : DirtyRecoveryState);
        AddParameter(command, "$failure_code", FormatFailureCode(attempts, exception));
        AddParameter(command, "$transaction_id", transactionId);
        command.ExecuteNonQuery();
    }

    private static long ReadNextEventSequence(AuthoritySqliteTransactionContext context)
    {
        using var command = context.CreateCommand("SELECT COALESCE(MAX(event_seq),0)+1 FROM authority_events;");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertEvent(
        AuthoritySqliteTransactionContext context,
        string transactionId,
        long eventSequence,
        AuthorityEventData authorityEvent)
    {
        using var command = context.CreateCommand(
            """
            INSERT INTO authority_events(
                event_id,event_seq,transaction_id,aggregate_type,aggregate_id,
                event_type,event_payload_json,created_at_ms)
            VALUES($event_id,$event_seq,$transaction_id,$aggregate_type,$aggregate_id,
                   $event_type,$event_payload_json,$created_at_ms);
            """);
        AddParameter(command, "$event_id", authorityEvent.EventId);
        AddParameter(command, "$event_seq", eventSequence);
        AddParameter(command, "$transaction_id", transactionId);
        AddParameter(command, "$aggregate_type", authorityEvent.AggregateType);
        AddParameter(command, "$aggregate_id", authorityEvent.AggregateId);
        AddParameter(command, "$event_type", authorityEvent.EventType);
        AddParameter(command, "$event_payload_json", authorityEvent.EventPayloadJson);
        AddParameter(command, "$created_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void UpdateCurrentPointer(
        AuthoritySqliteTransactionContext context,
        AuthorityCurrentPointerUpdate pointer)
    {
        var (sql, idParameter) = pointer.Kind switch
        {
            AuthorityCurrentPointerKind.ChapterManuscriptRevision =>
                ("UPDATE chapters SET current_manuscript_revision_id=$value, updated_at_ms=$now WHERE chapter_id=$id;", "$id"),
            AuthorityCurrentPointerKind.StorylineAcceptedSnapshot =>
                ("UPDATE storylines SET accepted_snapshot_id=$value, updated_at_ms=$now WHERE storyline_id=$id;", "$id"),
            _ => throw new ArgumentOutOfRangeException(nameof(pointer))
        };

        using var command = context.CreateCommand(sql);
        AddParameter(command, "$value", pointer.PointerValue);
        AddParameter(command, "$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        AddParameter(command, idParameter, pointer.AggregateId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new AuthorityTransactionException(
                $"Current pointer target '{pointer.AggregateId}' does not exist.");
        }
    }

    private static void EnsureAuthorityIsWritable(DbConnection connection, string? exceptTransactionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT transaction_id, recovery_state
            FROM authority_transactions
            WHERE status IN('submitting','reviewing','resolving','accepting','committing','revalidating')
              AND ($except_id IS NULL OR transaction_id <> $except_id)
            LIMIT 1;
            """;
        AddParameter(command, "$except_id", (object?)exceptTransactionId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new AuthorityTransactionException(
                $"Authority operations are blocked by transaction '{reader.GetString(0)}' " +
                $"in recovery state '{reader.GetString(1)}'.");
        }
    }

    private TransactionRecord? GetByIdempotencyKey(string idempotencyKey)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        return ReadByIdempotencyKey(connection, idempotencyKey);
    }

    private static TransactionRecord? ReadByIdempotencyKey(DbConnection connection, string idempotencyKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TransactionSelect + " WHERE idempotency_key=$idempotency_key;";
        AddParameter(command, "$idempotency_key", idempotencyKey);
        return ReadTransaction(command);
    }

    private static TransactionRecord? ReadByTransactionId(DbConnection connection, string transactionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TransactionSelect + " WHERE transaction_id=$transaction_id;";
        AddParameter(command, "$transaction_id", transactionId);
        return ReadTransaction(command);
    }

    private static TransactionRecord? ReadTransaction(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var status = reader.GetString(2);
        var recoveryState = reader.GetString(3);
        long? committedAt = reader.IsDBNull(4) ? null : reader.GetInt64(4);
        var state = MapState(status, recoveryState, committedAt);
        return new TransactionRecord(
            reader.GetString(0),
            reader.GetString(1),
            state,
            committedAt,
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static AuthorityTransactionState MapState(string status, string recoveryState, long? committedAt)
    {
        if (StringComparer.Ordinal.Equals(status, "complete"))
        {
            return AuthorityTransactionState.Complete;
        }

        if (StringComparer.Ordinal.Equals(recoveryState, RecoveryRequiredState))
        {
            return AuthorityTransactionState.RecoveryRequired;
        }

        if (committedAt is not null)
        {
            return AuthorityTransactionState.CommittedButDirty;
        }

        return StringComparer.Ordinal.Equals(status, "failed")
            ? AuthorityTransactionState.Failed
            : AuthorityTransactionState.Pending;
    }

    private static List<AuthorityMaterializationPlan> ReadMaterializationPlans(
        DbConnection connection,
        string transactionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_payload_json
            FROM authority_events
            WHERE transaction_id=$transaction_id AND event_type=$event_type
            ORDER BY event_seq DESC
            LIMIT 1;
            """;
        AddParameter(command, "$transaction_id", transactionId);
        AddParameter(command, "$event_type", MaterializationPlanEvent);
        var json = command.ExecuteScalar() as string;
        return json is null
            ? []
            : JsonSerializer.Deserialize<List<AuthorityMaterializationPlan>>(json)
                ?? throw new AuthorityTransactionException("The durable materialization plan is invalid.");
    }

    private static int ParseRepairAttempts(string? failureCode)
    {
        if (failureCode is null || !failureCode.StartsWith("repair_attempts=", StringComparison.Ordinal))
        {
            return 0;
        }

        var separator = failureCode.IndexOf(';');
        var value = separator < 0
            ? failureCode["repair_attempts=".Length..]
            : failureCode["repair_attempts=".Length..separator];
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var attempts)
            ? attempts
            : 0;
    }

    private static string FormatFailureCode(int repairAttempts, Exception exception)
    {
        var code = exception.GetType().Name;
        return $"repair_attempts={repairAttempts.ToString(CultureInfo.InvariantCulture)};last={code}";
    }

    private static AuthorityTransactionHandle ToHandle(TransactionRecord record, bool Existing)
    {
        return new AuthorityTransactionHandle(
            record.TransactionId,
            record.IdempotencyKey,
            record.State,
            Existing);
    }

    private static void RequirePending(AuthorityTransactionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.State != AuthorityTransactionState.Pending)
        {
            throw new AuthorityTransactionException(
                $"Blob staging requires a pending transaction, not '{handle.State}'.");
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private const string TransactionSelect =
        "SELECT transaction_id,idempotency_key,status,recovery_state,committed_at_ms,failure_code " +
        "FROM authority_transactions";

    private sealed record TransactionRecord(
        string TransactionId,
        string IdempotencyKey,
        AuthorityTransactionState State,
        long? CommittedAt,
        string? FailureCode);

    private sealed class PostCommitFaultException : Exception
    {
        public PostCommitFaultException(Exception innerException)
            : base("A fault was injected after the SQLite commit point.", innerException)
        {
        }
    }
}
