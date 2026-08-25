using System.Data.Common;
using LLMW.Writing.Application.Recovery;
using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Domain.Authority.Recovery;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Recovery;

public sealed class SqliteChapterSubmissionRecoveryStore : IChapterSubmissionRecoveryStore
{
    private readonly string databasePath;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly Func<long> clock;

    public SqliteChapterSubmissionRecoveryStore(
        string databasePath,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public IReadOnlyList<DurableChapterSubmissionState> LoadIncomplete()
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = SelectState +
            " WHERE t.status IN('submitting','reviewing','resolving','accepting','committing','revalidating')" +
            " OR t.project_submission_state <> 'idle'" +
            " OR t.recovery_state = 'recovery_required'" +
            " ORDER BY t.started_at_ms,t.transaction_id;";
        return ReadAll(command);
    }

    public DurableChapterSubmissionState? Load(string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = SelectState + " WHERE t.transaction_id=$transaction_id;";
        Add(command, "$transaction_id", transactionId);
        return ReadAll(command).SingleOrDefault();
    }

    public void RehydratePreCommit(
        DurableChapterSubmissionState state,
        ChapterSubmissionRecoveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.HoldsSubmissionLock ||
            plan.RehydratedProjectState is not (ProjectSubmissionState.Reviewing or ProjectSubmissionState.Resolving))
        {
            throw new InvalidOperationException("Only a legal active pre-commit workflow can be rehydrated.");
        }

        var durableState = ToText(plan.RehydratedProjectState);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE authority_transactions
            SET status=$status,project_submission_state=$project_state,
                recovery_state='workflow_interrupted',failure_code='precommit_workflow_rehydrated'
            WHERE transaction_id=$transaction_id
              AND committed_at_ms IS NULL
              AND NOT EXISTS(SELECT 1 FROM acceptance_records WHERE transaction_id=$transaction_id)
              AND NOT EXISTS(SELECT 1 FROM manuscript_revisions WHERE transaction_id=$transaction_id);
            """;
        Add(command, "$status", durableState);
        Add(command, "$project_state", durableState);
        Add(command, "$transaction_id", state.TransactionId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The pre-commit workflow could not be rehydrated without crossing the Authority commit boundary.");
        }

        transaction.Commit();
    }

    public void ReleaseOrphanedPreCommit(DurableChapterSubmissionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE authority_transactions
            SET status='failed',project_submission_state='idle',recovery_state='rolled_back',
                failure_code=COALESCE(failure_code,'uncommitted_pending_cleaned')
            WHERE transaction_id=$transaction_id AND committed_at_ms IS NULL
              AND NOT EXISTS(SELECT 1 FROM acceptance_records WHERE transaction_id=$transaction_id)
              AND NOT EXISTS(SELECT 1 FROM manuscript_revisions WHERE transaction_id=$transaction_id);
            """;
        Add(command, "$transaction_id", state.TransactionId);
        command.ExecuteNonQuery();
    }

    public void FinalizeCommittedRollForward(DurableChapterSubmissionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE authority_transactions
            SET project_submission_state='idle'
            WHERE transaction_id=$transaction_id
              AND status='complete' AND committed_at_ms IS NOT NULL
              AND EXISTS(SELECT 1 FROM acceptance_records WHERE transaction_id=$transaction_id)
              AND EXISTS(SELECT 1 FROM manuscript_revisions WHERE transaction_id=$transaction_id)
              AND NOT EXISTS(SELECT 1 FROM manuscript_revisions
                             WHERE transaction_id=$transaction_id
                               AND materialization_status <> 'materialized');
            """;
        Add(command, "$transaction_id", state.TransactionId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Committed Authority could not be normalized after roll-forward verification.");
        }
    }

    public void CancelPreCommit(DurableChapterSubmissionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.CandidateId is null || state.ChapterId is null)
        {
            throw new InvalidOperationException("A durable Candidate and Chapter are required to cancel a submission.");
        }

        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        using (var transactionGuard = connection.CreateCommand())
        {
            transactionGuard.Transaction = transaction;
            transactionGuard.CommandText =
                """
                UPDATE authority_transactions
                SET status='failed',project_submission_state='idle',recovery_state='cancelled',failure_code='recovery_cancelled'
                WHERE transaction_id=$transaction_id AND committed_at_ms IS NULL
                  AND NOT EXISTS(SELECT 1 FROM acceptance_records WHERE transaction_id=$transaction_id)
                  AND NOT EXISTS(SELECT 1 FROM manuscript_revisions WHERE transaction_id=$transaction_id);
                """;
            Add(transactionGuard, "$transaction_id", state.TransactionId);
            if (transactionGuard.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Recovery Cancel cannot cross the SQLite Authority commit point.");
            }
        }

        Execute(
            connection,
            transaction,
            "UPDATE candidates SET status='cancelled',updated_at_ms=$now WHERE candidate_id=$candidate_id AND status='under_review';",
            ("$now", clock()),
            ("$candidate_id", state.CandidateId));
        Execute(
            connection,
            transaction,
            "UPDATE chapters SET workflow_state='draft',updated_at_ms=$now WHERE chapter_id=$chapter_id AND workflow_state='under_review';",
            ("$now", clock()),
            ("$chapter_id", state.ChapterId));
        transaction.Commit();
    }

    public void MarkRecoveryRequired(DurableChapterSubmissionState state, string reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE authority_transactions
            SET status='revalidating',project_submission_state='revalidating',
                recovery_state='recovery_required',failure_code='workflow_rehydrate_invariant_failed'
            WHERE transaction_id=$transaction_id;
            """;
        Add(command, "$transaction_id", state.TransactionId);
        command.ExecuteNonQuery();
    }

    private static List<DurableChapterSubmissionState> ReadAll(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        List<DurableChapterSubmissionState> results = [];
        while (reader.Read())
        {
            results.Add(new DurableChapterSubmissionState(
                reader.GetString(0),
                MapTransactionState(reader.GetString(1), reader.GetString(2), !reader.IsDBNull(3)),
                ParseProjectState(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : ParseCandidateState(reader.GetString(7)),
                reader.IsDBNull(8) ? null : ParseChapterState(reader.GetString(8)),
                reader.IsDBNull(9) ? null : StringComparer.Ordinal.Equals(reader.GetString(9), "passed"),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetBoolean(13)));
        }

        return results;
    }

    private static RecoveryTransactionState MapTransactionState(string status, string recoveryState, bool committed) =>
        StringComparer.Ordinal.Equals(status, "complete") ? RecoveryTransactionState.Complete :
        StringComparer.Ordinal.Equals(recoveryState, "recovery_required") ? RecoveryTransactionState.RecoveryRequired :
        committed ? RecoveryTransactionState.CommittedButDirty :
        StringComparer.Ordinal.Equals(status, "failed") ? RecoveryTransactionState.Failed :
        RecoveryTransactionState.Pending;

    private static CandidateState ParseCandidateState(string value) => value switch
    {
        "under_review" => CandidateState.UnderReview,
        "failed" => CandidateState.Failed,
        "cancelled" => CandidateState.Cancelled,
        "accepted" => CandidateState.Accepted,
        "superseded" => CandidateState.Superseded,
        _ => CandidateState.Created
    };

    private static ChapterState ParseChapterState(string value) => value switch
    {
        "draft" => ChapterState.Draft,
        "under_review" => ChapterState.UnderReview,
        "failed" => ChapterState.Failed,
        "accepted" => ChapterState.Accepted,
        "materialized" => ChapterState.Materialized,
        "submitted" => ChapterState.Submitted,
        "ready" => ChapterState.Ready,
        _ => ChapterState.OutlineContract
    };

    private static ProjectSubmissionState ParseProjectState(string value) => value switch
    {
        "submitting" => ProjectSubmissionState.Submitting,
        "reviewing" => ProjectSubmissionState.Reviewing,
        "resolving" => ProjectSubmissionState.Resolving,
        "accepting" => ProjectSubmissionState.Accepting,
        "committing" => ProjectSubmissionState.Committing,
        "revalidating" => ProjectSubmissionState.Revalidating,
        _ => ProjectSubmissionState.Idle
    };

    private static string ToText(ProjectSubmissionState state) => state switch
    {
        ProjectSubmissionState.Reviewing => "reviewing",
        ProjectSubmissionState.Resolving => "resolving",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static void Execute(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private const string SelectState =
        """
        SELECT t.transaction_id,t.status,t.recovery_state,t.committed_at_ms,t.project_submission_state,
               c.candidate_id,c.chapter_id,c.status,ch.workflow_state,
               (SELECT r.status FROM review_attempts r WHERE r.candidate_id=c.candidate_id
                ORDER BY r.attempt_no DESC LIMIT 1),
               EXISTS(SELECT 1 FROM acceptance_records a WHERE a.transaction_id=t.transaction_id),
               EXISTS(SELECT 1 FROM manuscript_revisions m WHERE m.transaction_id=t.transaction_id),
               EXISTS(SELECT 1 FROM manuscript_revisions m JOIN chapters cp
                      ON cp.current_manuscript_revision_id=m.revision_id
                      WHERE m.transaction_id=t.transaction_id),
               CASE WHEN EXISTS(SELECT 1 FROM manuscript_revisions m WHERE m.transaction_id=t.transaction_id)
                    THEN NOT EXISTS(SELECT 1 FROM manuscript_revisions m
                                    WHERE m.transaction_id=t.transaction_id
                                      AND m.materialization_status <> 'materialized')
                    ELSE 0 END
        FROM authority_transactions t
        LEFT JOIN candidates c ON c.candidate_id=COALESCE(
            (SELECT a.candidate_id FROM acceptance_records a
             WHERE a.transaction_id=t.transaction_id LIMIT 1),
            CASE WHEN t.project_submission_state <> 'idle' THEN
                (SELECT active.candidate_id FROM candidates active
                 WHERE active.status IN('under_review','failed','cancelled')
                 ORDER BY active.updated_at_ms DESC,active.candidate_id DESC LIMIT 1)
            END)
        LEFT JOIN chapters ch ON ch.chapter_id=c.chapter_id
        """;
}
