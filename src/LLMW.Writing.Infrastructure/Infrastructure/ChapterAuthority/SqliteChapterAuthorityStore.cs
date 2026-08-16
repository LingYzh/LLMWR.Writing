using System.Data.Common;
using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Authority;
using LLMW.Writing.Infrastructure.Persistence;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.ChapterAuthority;

public sealed class SqliteChapterAuthorityStore : IChapterAuthorityStore
{
    private readonly string databasePath;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly AuthorityTransactionCoordinator coordinator;
    private readonly Func<long> clock;

    public SqliteChapterAuthorityStore(
        string databasePath,
        AuthorityTransactionCoordinator coordinator,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public SubmissionContext? LoadSubmissionContext(string chapterId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.workflow_state,
                   EXISTS(SELECT 1 FROM authority_transactions
                          WHERE status IN('submitting','reviewing','resolving','accepting','committing','revalidating')),
                   (SELECT candidate_id FROM candidates
                    WHERE chapter_id=$chapter_id
                    ORDER BY created_at_ms DESC, candidate_id DESC LIMIT 1)
            FROM chapters c
            WHERE c.chapter_id=$chapter_id;
            """;
        Add(command, "$chapter_id", chapterId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SubmissionContext(
                chapterId,
                ParseChapterState(reader.GetString(0)),
                reader.GetBoolean(1) ? ProjectSubmissionState.Reviewing : ProjectSubmissionState.Idle,
                reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    public SubmitChapterDraftResult PersistCandidate(
        AuthorityTransactionHandle transaction,
        PersistCandidateRequest request)
    {
        var candidateId = DurableUuidV7.Create().ToString();
        var now = clock();
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var databaseTransaction = connection.BeginTransaction();
        Execute(
            connection,
            databaseTransaction,
            """
            INSERT INTO candidates(
                candidate_id,chapter_id,submission_kind,source_draft_path,artifact_digest,
                status,parent_candidate_id,created_at_ms,updated_at_ms)
            VALUES($candidate_id,$chapter_id,$submission_kind,$source_draft_path,$artifact_digest,
                   'under_review',$parent_candidate_id,$now,$now);
            """,
            ("$candidate_id", candidateId),
            ("$chapter_id", request.ChapterId),
            ("$submission_kind", SubmissionKind(request.Eligibility)),
            ("$source_draft_path", request.SourceDraftPath),
            ("$artifact_digest", request.ArtifactDigest),
            ("$parent_candidate_id", request.ParentCandidateId),
            ("$now", now));
        Execute(
            connection,
            databaseTransaction,
            "UPDATE chapters SET workflow_state='under_review',updated_at_ms=$now WHERE chapter_id=$chapter_id;",
            ("$now", now), ("$chapter_id", request.ChapterId));
        Execute(
            connection,
            databaseTransaction,
            "UPDATE authority_transactions SET status='reviewing',project_submission_state='reviewing' WHERE transaction_id=$transaction_id;",
            ("$transaction_id", transaction.TransactionId));
        databaseTransaction.Commit();
        return new SubmitChapterDraftResult(
            candidateId,
            transaction.TransactionId,
            request.ArtifactDigest,
            request.SourceDraftPath);
    }

    public CandidateReviewContext? LoadReviewContext(string candidateId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.candidate_id,c.chapter_id,t.transaction_id,t.idempotency_key,c.artifact_digest,c.source_draft_path,
                   c.status,ch.workflow_state,t.project_submission_state,
                   COALESCE((SELECT MAX(attempt_no)+1 FROM review_attempts WHERE candidate_id=c.candidate_id),1)
            FROM candidates c
            JOIN chapters ch ON ch.chapter_id=c.chapter_id
            JOIN authority_transactions t ON t.status IN('submitting','reviewing','resolving','accepting','committing','revalidating')
            WHERE c.candidate_id=$candidate_id
              AND c.candidate_id=(SELECT candidate_id FROM candidates
                                  WHERE status='under_review'
                                  ORDER BY created_at_ms DESC,candidate_id DESC LIMIT 1)
            ORDER BY t.started_at_ms DESC LIMIT 1;
            """;
        Add(command, "$candidate_id", candidateId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadReviewContext(reader) : null;
    }

    public ReviewChapterCandidateResult PersistReview(PersistReviewRequest request)
    {
        var attemptId = DurableUuidV7.Create().ToString();
        var now = clock();
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            """
            INSERT INTO review_attempts(
                review_attempt_id,scope_kind,scope_id,review_kind,candidate_id,attempt_no,status,
                result_json,diagnostics_ref,requested_changes_ref,started_at_ms,completed_at_ms)
            VALUES($attempt_id,'candidate',$candidate_id,'independent_chapter_review',$candidate_id,$attempt_no,
                   $status,$result_json,$diagnostics_ref,$requested_changes_ref,$now,$now);
            """,
            ("$attempt_id", attemptId),
            ("$candidate_id", request.Context.CandidateId),
            ("$attempt_no", request.Context.NextAttemptNumber),
            ("$status", request.Decision.Outcome == ChapterReviewOutcome.Pass ? "passed" : "failed"),
            ("$result_json", request.Decision.ResultJson),
            ("$diagnostics_ref", request.Decision.DiagnosticsReference),
            ("$requested_changes_ref", request.Decision.RequestedChangesReference),
            ("$now", now));

        if (request.Decision.Outcome == ChapterReviewOutcome.Pass)
        {
            Execute(
                connection,
                transaction,
                "UPDATE authority_transactions SET status='resolving',project_submission_state='resolving' WHERE transaction_id=$transaction_id;",
                ("$transaction_id", request.Context.TransactionId));
        }
        else
        {
            Execute(
                connection,
                transaction,
                "UPDATE candidates SET status='failed',updated_at_ms=$now WHERE candidate_id=$candidate_id;",
                ("$now", now), ("$candidate_id", request.Context.CandidateId));
            Execute(
                connection,
                transaction,
                "UPDATE chapters SET workflow_state='draft',updated_at_ms=$now WHERE chapter_id=$chapter_id;",
                ("$now", now), ("$chapter_id", request.Context.ChapterId));
            Execute(
                connection,
                transaction,
                "UPDATE authority_transactions SET status='failed',project_submission_state='idle',recovery_state='review_failed',failure_code='review_failed' WHERE transaction_id=$transaction_id;",
                ("$transaction_id", request.Context.TransactionId));
        }

        transaction.Commit();
        return new ReviewChapterCandidateResult(
            request.Context.CandidateId,
            attemptId,
            request.Decision.Outcome);
    }

    public CandidateAcceptanceContext? LoadAcceptanceContext(string candidateId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.candidate_id,c.chapter_id,t.transaction_id,t.idempotency_key,c.artifact_digest,c.source_draft_path,
                   c.status,ch.workflow_state,t.project_submission_state,r.review_attempt_id,r.status,
                   t.status,t.recovery_state,t.committed_at_ms,a.acceptance_id,m.revision_id,
                   (SELECT json_extract(event_payload_json,'$[0].TargetRelativePath')
                    FROM authority_events WHERE transaction_id=t.transaction_id AND event_type='wp03.materialization_plan'
                    ORDER BY event_seq DESC LIMIT 1),
                   COALESCE(
                       a.warnings_ack_digest,
                       (SELECT event_payload_json FROM authority_events
                        WHERE transaction_id=t.transaction_id AND event_type='wp13.delegated_authorization'
                        ORDER BY event_seq DESC LIMIT 1))
            FROM candidates c
            JOIN chapters ch ON ch.chapter_id=c.chapter_id
            JOIN authority_transactions t ON t.transaction_id=COALESCE(
                (SELECT transaction_id FROM acceptance_records WHERE candidate_id=c.candidate_id LIMIT 1),
                (SELECT transaction_id FROM authority_transactions
                 WHERE status IN('resolving','accepting','committing','revalidating')
                 ORDER BY started_at_ms DESC LIMIT 1))
            JOIN review_attempts r ON r.review_attempt_id=(
                SELECT review_attempt_id FROM review_attempts
                WHERE candidate_id=c.candidate_id ORDER BY attempt_no DESC LIMIT 1)
            LEFT JOIN acceptance_records a ON a.candidate_id=c.candidate_id
            LEFT JOIN manuscript_revisions m ON m.candidate_id=c.candidate_id
            WHERE c.candidate_id=$candidate_id
              AND (a.acceptance_id IS NOT NULL OR c.candidate_id=(
                  SELECT candidate_id FROM candidates
                  WHERE status='under_review'
                  ORDER BY created_at_ms DESC,candidate_id DESC LIMIT 1))
            LIMIT 1;
            """;
        Add(command, "$candidate_id", candidateId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAcceptanceContext(reader) : null;
    }

    public CandidateAcceptanceContext PrepareAcceptance(PrepareAcceptanceRequest request)
    {
        var acceptanceId = request.Context.AcceptanceId ?? DurableUuidV7.Create().ToString();
        var revisionId = request.Context.ManuscriptRevisionId ?? DurableUuidV7.Create().ToString();
        using var connection = connectionFactory.OpenConfigured(databasePath);
        Execute(
            connection,
            transaction: null,
            "UPDATE authority_transactions SET status='committing',project_submission_state='committing' WHERE transaction_id=$transaction_id;",
            ("$transaction_id", request.Context.TransactionId));
        return request.Context with
        {
            ProjectSubmissionState = ProjectSubmissionState.Committing,
            AcceptanceId = acceptanceId,
            ManuscriptRevisionId = revisionId,
            MaterializedRelativePath = request.MaterializedRelativePath,
            AcceptedById = request.AcceptedById,
            AcceptedByKind = request.Decision.AuthorityKind == DecisionAuthorityKind.AgentDelegated
                ? "AGENT_DELEGATED"
                : "AUTHOR_CONFIRMED",
            AuthorizationSnapshotJson = request.AuthorizationSnapshotJson ?? request.Context.AuthorizationSnapshotJson
        };
    }

    public AcceptChapterCandidateResult CommitAcceptance(
        CandidateAcceptanceContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.AcceptanceId is null || context.ManuscriptRevisionId is null || context.MaterializedRelativePath is null)
        {
            throw new InvalidOperationException("Acceptance identities and materialization path must be prepared before commit.");
        }

        var handle = new AuthorityTransactionHandle(
            context.TransactionId,
            context.IdempotencyKey,
            context.TransactionState,
            Existing: true);
        var request = new AuthorityCommitRequest(
            Events:
            [
                Event("candidate", context.CandidateId, "candidate.accepted", context.TransactionId),
                Event("chapter", context.ChapterId, "chapter.accepted", context.TransactionId),
                Event("manuscript_revision", context.ManuscriptRevisionId, "manuscript_revision.created", context.TransactionId),
                ..AuthorizationSnapshotEvents(context)
            ],
            PointerUpdates:
            [
                new AuthorityCurrentPointerUpdate(
                    AuthorityCurrentPointerKind.ChapterManuscriptRevision,
                    context.ChapterId,
                    context.ManuscriptRevisionId)
            ],
            Materializations:
            [
                new AuthorityMaterializationPlan(context.MaterializedRelativePath, context.ArtifactDigest)
            ]);

        var committed = coordinator.Commit(
            handle,
            request,
            transaction => MutateAcceptance(transaction, context),
            cancellationToken);
        FinalizeProjectSubmissionState(context);
        return Result(context, committed);
    }

    public AcceptChapterCandidateResult RecoverAcceptance(
        CandidateAcceptanceContext context,
        CancellationToken cancellationToken = default)
    {
        var recovered = context.TransactionState == AuthorityTransactionState.Complete
            ? new AuthorityTransactionHandle(context.TransactionId, context.IdempotencyKey, AuthorityTransactionState.Complete, true)
            : coordinator.Recover(context.TransactionId, cancellationToken);
        if (recovered.State == AuthorityTransactionState.Complete)
        {
            FinalizeProjectSubmissionState(context);
        }

        return Result(context, recovered);
    }

    private void MutateAcceptance(AuthoritySqliteTransactionContext transaction, CandidateAcceptanceContext context)
    {
        var now = clock();
        string? supersedes;
        using (var read = transaction.CreateCommand(
            "SELECT current_manuscript_revision_id FROM chapters WHERE chapter_id=$chapter_id;"))
        {
            Add(read, "$chapter_id", context.ChapterId);
            supersedes = read.ExecuteScalar() as string;
        }

        Execute(
            transaction.Connection,
            transaction.Transaction,
            "UPDATE candidates SET status='accepted',updated_at_ms=$now WHERE candidate_id=$candidate_id AND status='under_review';",
            ("$now", now), ("$candidate_id", context.CandidateId));
        Execute(
            transaction.Connection,
            transaction.Transaction,
            """
            INSERT INTO manuscript_revisions(
                revision_id,chapter_id,candidate_id,artifact_digest,transaction_id,supersedes_revision_id,
                materialization_status,accepted_at_ms,created_at_ms)
            VALUES($revision_id,$chapter_id,$candidate_id,$artifact_digest,$transaction_id,$supersedes,
                   'pending',$now,$now);
            """,
            ("$revision_id", context.ManuscriptRevisionId),
            ("$chapter_id", context.ChapterId),
            ("$candidate_id", context.CandidateId),
            ("$artifact_digest", context.ArtifactDigest),
            ("$transaction_id", context.TransactionId),
            ("$supersedes", supersedes),
            ("$now", now));
        Execute(
            transaction.Connection,
            transaction.Transaction,
            """
            INSERT INTO acceptance_records(
                acceptance_id,scope_kind,scope_id,candidate_id,manuscript_revision_id,review_attempt_id,
                accepted_by_kind,accepted_by_id,warnings_ack_digest,transaction_id,accepted_at_ms)
            VALUES($acceptance_id,'chapter',$chapter_id,$candidate_id,$revision_id,$review_id,
                   $accepted_by_kind,$accepted_by_id,$snapshot,$transaction_id,$now);
            """,
            ("$acceptance_id", context.AcceptanceId),
            ("$chapter_id", context.ChapterId),
            ("$candidate_id", context.CandidateId),
            ("$revision_id", context.ManuscriptRevisionId),
            ("$review_id", context.ReviewAttemptId),
            ("$accepted_by_kind", context.AcceptedByKind),
            ("$accepted_by_id", context.AcceptedById ?? "user-interactive"),
            ("$snapshot", (object?)context.AuthorizationSnapshotJson ?? DBNull.Value),
            ("$transaction_id", context.TransactionId),
            ("$now", now));
        Execute(
            transaction.Connection,
            transaction.Transaction,
            "UPDATE chapters SET workflow_state='accepted',updated_at_ms=$now WHERE chapter_id=$chapter_id;",
            ("$now", now), ("$chapter_id", context.ChapterId));
        Execute(
            transaction.Connection,
            transaction.Transaction,
            "UPDATE authority_transactions SET project_submission_state='revalidating' WHERE transaction_id=$transaction_id;",
            ("$transaction_id", context.TransactionId));
    }

    private void FinalizeProjectSubmissionState(CandidateAcceptanceContext context)
    {
        if (context.ManuscriptRevisionId is null)
        {
            return;
        }

        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            "UPDATE authority_transactions SET project_submission_state='idle' WHERE transaction_id=$transaction_id;",
            ("$transaction_id", context.TransactionId));
        transaction.Commit();
    }

    private static CandidateReviewContext ReadReviewContext(DbDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), ParseCandidateState(reader.GetString(6)),
            ParseChapterState(reader.GetString(7)), ParseProjectState(reader.GetString(8)), reader.GetInt32(9));

    private static CandidateAcceptanceContext ReadAcceptanceContext(DbDataReader reader)
    {
        var transactionState = MapTransactionState(reader.GetString(11), reader.GetString(12), !reader.IsDBNull(13));
        return new CandidateAcceptanceContext(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), ParseCandidateState(reader.GetString(6)),
            ParseChapterState(reader.GetString(7)), ParseProjectState(reader.GetString(8)),
            reader.GetString(9), ParseReviewOutcome(reader.GetString(10)), transactionState,
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(14) ? null : "user-interactive",
            "AUTHOR_CONFIRMED",
            reader.IsDBNull(17) ? null : reader.GetString(17));
    }

    private static AcceptChapterCandidateResult Result(
        CandidateAcceptanceContext context,
        AuthorityTransactionHandle transaction) =>
        new(
            context.CandidateId,
            context.AcceptanceId ?? throw new InvalidOperationException("Acceptance identity is missing."),
            context.ManuscriptRevisionId ?? throw new InvalidOperationException("Revision identity is missing."),
            context.TransactionId,
            context.MaterializedRelativePath ?? throw new InvalidOperationException("Materialization path is missing."),
            transaction.State,
            transaction.Existing);

    private static AuthorityEventData[] AuthorizationSnapshotEvents(CandidateAcceptanceContext context)
    {
        if (string.IsNullOrWhiteSpace(context.AuthorizationSnapshotJson) || context.AcceptanceId is null)
        {
            return [];
        }

        return
        [
            new AuthorityEventData(
                DurableUuidV7.Create().ToString(),
                "acceptance",
                context.AcceptanceId,
                FormalAuthorizationSnapshot.EventType,
                context.AuthorizationSnapshotJson)
        ];
    }

    private static AuthorityEventData Event(string aggregateType, string aggregateId, string eventType, string transactionId) =>
        new(
            DurableUuidV7.Create().ToString(),
            aggregateType,
            aggregateId,
            eventType,
            JsonSerializer.Serialize(new { transactionId, aggregateId }));

    private static CandidateState ParseCandidateState(string value) => value switch
    {
        "under_review" => CandidateState.UnderReview,
        "failed" => CandidateState.Failed,
        "accepted" => CandidateState.Accepted,
        "cancelled" => CandidateState.Cancelled,
        "superseded" => CandidateState.Superseded,
        _ => CandidateState.Created
    };

    private static ChapterState ParseChapterState(string value) => value switch
    {
        "ready" => ChapterState.Ready,
        "draft" => ChapterState.Draft,
        "submitted" => ChapterState.Submitted,
        "under_review" => ChapterState.UnderReview,
        "failed" => ChapterState.Failed,
        "accepted" => ChapterState.Accepted,
        "materialized" => ChapterState.Materialized,
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

    private static ChapterReviewOutcome ParseReviewOutcome(string value) =>
        StringComparer.Ordinal.Equals(value, "passed") ? ChapterReviewOutcome.Pass : ChapterReviewOutcome.Fail;

    private static AuthorityTransactionState MapTransactionState(string status, string recovery, bool committed) =>
        StringComparer.Ordinal.Equals(status, "complete") ? AuthorityTransactionState.Complete :
        StringComparer.Ordinal.Equals(recovery, "recovery_required") ? AuthorityTransactionState.RecoveryRequired :
        committed ? AuthorityTransactionState.CommittedButDirty :
        StringComparer.Ordinal.Equals(status, "failed") ? AuthorityTransactionState.Failed :
        AuthorityTransactionState.Pending;

    private static string SubmissionKind(SubmissionEligibility eligibility) => eligibility switch
    {
        SubmissionEligibility.Normal => "normal",
        SubmissionEligibility.HistoricalRevision => "historical_revision",
        _ => throw new ArgumentOutOfRangeException(nameof(eligibility))
    };

    private static void Execute(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
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

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
