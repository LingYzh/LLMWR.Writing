using System.Data.Common;
using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Infrastructure.Authority;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.NarrativeChange;

public sealed class SqliteNarrativeChangeStore : INarrativeChangeStore
{
    private readonly string databasePath;
    private readonly ImmutableBlobStore blobStore;
    private readonly AuthorityTransactionCoordinator coordinator;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly Func<long> clock;

    public SqliteNarrativeChangeStore(
        string databasePath,
        ImmutableBlobStore blobStore,
        AuthorityTransactionCoordinator coordinator,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public NarrativeStoreResult<NarrativeChangeSetSnapshot> CreateWorkingChangeSet(PersistWorkingChangeSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var changeSetId = DurableUuidV7.Create().ToString();
        var now = clock();
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var change in request.Changes)
            {
                var existing = ReadObject(connection, transaction, change.ObjectId);
                if (change.ChangeKind == NarrativeChangeKind.Add)
                {
                    if (existing is not null)
                    {
                        transaction.Rollback();
                        return NarrativeStoreResults.Fail<NarrativeChangeSetSnapshot>(
                            NarrativeChangeError.ObjectAlreadyCurrent,
                            change.ObjectId);
                    }

                    Execute(
                        connection,
                        transaction,
                        """
                        INSERT INTO objects(object_id,object_type,schema_version,revision_no,status,created_at_ms,updated_at_ms)
                        VALUES($object_id,$object_type,1,0,'proposed',$now,$now);
                        """,
                        ("$object_id", change.ObjectId),
                        ("$object_type", change.ObjectType),
                        ("$now", now));
                }
                else if (existing is null)
                {
                    transaction.Rollback();
                    return NarrativeStoreResults.Fail<NarrativeChangeSetSnapshot>(
                        NarrativeChangeError.ObjectNotFound,
                        change.ObjectId);
                }
            }

            Execute(
                connection,
                transaction,
                """
                INSERT INTO narrative_change_sets(
                    change_set_id,scope_kind,scope_id,status,proposer_kind,proposer_id,created_at_ms,updated_at_ms)
                VALUES($change_set_id,$scope_kind,$scope_id,'working',$proposer_kind,$proposer_id,$now,$now);
                """,
                ("$change_set_id", changeSetId),
                ("$scope_kind", request.ScopeKind),
                ("$scope_id", request.ScopeId),
                ("$proposer_kind", request.ProposerKind),
                ("$proposer_id", request.ProposerId),
                ("$now", now));

            foreach (var change in request.Changes)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO narrative_changes(
                        narrative_change_id,change_set_id,object_id,change_kind,before_revision_ref,before_digest,
                        after_payload_digest,ordinal)
                    VALUES($narrative_change_id,$change_set_id,$object_id,$change_kind,$before_revision_ref,
                           $before_digest,$after_payload_digest,$ordinal);
                    """,
                    ("$narrative_change_id", DurableUuidV7.Create().ToString()),
                    ("$change_set_id", changeSetId),
                    ("$object_id", change.ObjectId),
                    ("$change_kind", ChangeKindValue(change.ChangeKind)),
                    ("$before_revision_ref", change.BeforeRevisionRef),
                    ("$before_digest", change.BeforeDigest),
                    ("$after_payload_digest", change.AfterPayloadDigest),
                    ("$ordinal", change.Ordinal));
            }

            transaction.Commit();
            return NarrativeStoreResults.Success(
                LoadChangeSet(changeSetId) ?? throw new InvalidOperationException("Working change set was not readable after commit."));
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                transaction.Rollback();
            }

            throw;
        }
    }

    public NarrativeChangeSetSnapshot? LoadChangeSet(string changeSetId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT change_set_id,scope_kind,scope_id,status,proposer_kind,proposer_id,decider_kind,decider_id,
                   transaction_id,impact_analysis_id
            FROM narrative_change_sets
            WHERE change_set_id=$change_set_id;
            """;
        Add(command, "$change_set_id", changeSetId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var changes = ReadChanges(connection, reader.GetString(0));
        return new NarrativeChangeSetSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            changes);
    }

    public NarrativeChangeFailure? ValidateApplyPreconditions(
        NarrativeChangeSetSnapshot changeSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        return ValidateAllChanges(connection, transaction: null, changeSet.Changes, cancellationToken);
    }

    public StructuralDependencyAssessment AssessStructuralDependencies(NarrativeChangeSetSnapshot changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        if (changeSet.Changes.Count == 0)
        {
            return new StructuralDependencyAssessment([]);
        }

        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        var parameters = changeSet.Changes
            .Select((change, index) => (change.ObjectId, Name: $"$object_{index}"))
            .ToArray();
        var names = string.Join(",", parameters.Select(parameter => parameter.Name));
        command.CommandText =
            $"""
            SELECT edge_id,from_object_id,to_object_id,edge_type
            FROM dependency_edges
            WHERE from_object_id IN ({names}) OR to_object_id IN ({names})
            ORDER BY edge_id;
            """;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.ObjectId);
        }

        List<DependencyEdgeReference> edges = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new DependencyEdgeReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return new StructuralDependencyAssessment(edges);
    }

    public NarrativeStoreResult<NarrativeImpactAnalysisRecord> PersistImpactAnalysis(PersistImpactAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var analysisId = DurableUuidV7.Create().ToString();
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        try
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO impact_analyses(
                    impact_analysis_id,change_set_id,status,affected_set_json,evidence_json,warnings_json,created_at_ms)
                VALUES($impact_analysis_id,$change_set_id,$status,$affected_set_json,$evidence_json,$warnings_json,$now);
                """,
                ("$impact_analysis_id", analysisId),
                ("$change_set_id", request.ChangeSetId),
                ("$status", ImpactStatusValue(request.Status)),
                ("$affected_set_json", request.AffectedSetJson),
                ("$evidence_json", request.EvidenceJson),
                ("$warnings_json", request.WarningsJson),
                ("$now", clock()));
            var updated = Execute(
                connection,
                transaction,
                """
                UPDATE narrative_change_sets
                SET impact_analysis_id=$impact_analysis_id,updated_at_ms=$now
                WHERE change_set_id=$change_set_id AND status='working';
                """,
                ("$impact_analysis_id", analysisId),
                ("$now", clock()),
                ("$change_set_id", request.ChangeSetId));
            if (updated != 1)
            {
                transaction.Rollback();
                return NarrativeStoreResults.Fail<NarrativeImpactAnalysisRecord>(NarrativeChangeError.ChangeSetNotApplicable);
            }

            transaction.Commit();
            return NarrativeStoreResults.Success(new NarrativeImpactAnalysisRecord(
                analysisId,
                request.Status,
                request.AffectedSetJson,
                request.EvidenceJson,
                request.WarningsJson,
                request.Warnings));
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                transaction.Rollback();
            }

            throw;
        }
    }

    public NarrativeStoreResult<NarrativeApplyStoreResult> Apply(
        NarrativeApplyStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var changeSet = LoadChangeSet(request.ChangeSetId);
        if (changeSet is null)
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.ChangeSetNotFound);
        }

        if (StringComparer.Ordinal.Equals(changeSet.Status, "applied"))
        {
            return RecoverOrReturnApplied(changeSet, request, cancellationToken);
        }

        if (!StringComparer.Ordinal.Equals(changeSet.Status, "working") ||
            changeSet.ImpactAnalysisId is null ||
            !StringComparer.Ordinal.Equals(changeSet.ImpactAnalysisId, request.ImpactAnalysisId))
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.ChangeSetNotApplicable);
        }

        using (var validationConnection = connectionFactory.OpenConfigured(databasePath))
        {
            var validationFailure = ValidateAllChanges(validationConnection, transaction: null, changeSet.Changes, cancellationToken);
            if (validationFailure is not null)
            {
                return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(validationFailure.Code, validationFailure.Detail);
            }
        }

        AuthorityTransactionHandle handle;
        try
        {
            handle = coordinator.Begin("narrative_change_apply", request.IdempotencyKey);
        }
        catch (AuthorityRecoveryRequiredException exception)
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.RecoveryRequired, exception.Message);
        }
        catch (AuthorityTransactionException exception)
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.AuthorityDirty, exception.Message);
        }

        try
        {
            var events = CreateEvents(changeSet);
            var committed = coordinator.Commit(
                handle,
                new AuthorityCommitRequest(events, [], []),
                transaction => ApplyMutation(transaction, changeSet, request, handle.TransactionId, cancellationToken),
                cancellationToken);
            return NarrativeStoreResults.Success(new NarrativeApplyStoreResult(
                changeSet.ChangeSetId,
                handle.TransactionId,
                changeSet.ImpactAnalysisId,
                committed.State,
                committed.Existing));
        }
        catch (NarrativePreconditionException exception)
        {
            TryCleanUncommittedTransaction(handle.TransactionId);
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(exception.Failure.Code, exception.Failure.Detail);
        }
        catch (AuthorityRecoveryRequiredException exception)
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.RecoveryRequired, exception.Message);
        }
        catch (AuthorityTransactionException exception)
        {
            var state = TryInspect(handle.TransactionId);
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(
                state == AuthorityTransactionState.CommittedButDirty ? NarrativeChangeError.AuthorityDirty : NarrativeChangeError.InfrastructureFailure,
                exception.Message);
        }
        catch (Exception exception)
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.InfrastructureFailure, exception.Message);
        }
    }

    private NarrativeStoreResult<NarrativeApplyStoreResult> RecoverOrReturnApplied(
        NarrativeChangeSetSnapshot changeSet,
        NarrativeApplyStoreRequest request,
        CancellationToken cancellationToken)
    {
        if (changeSet.TransactionId is null || changeSet.ImpactAnalysisId is null ||
            !StringComparer.Ordinal.Equals(changeSet.ImpactAnalysisId, request.ImpactAnalysisId) ||
            !StringComparer.Ordinal.Equals(ReadIdempotencyKey(changeSet.TransactionId), request.IdempotencyKey))
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.ChangeSetNotApplicable);
        }

        try
        {
            var handle = coordinator.Begin("narrative_change_apply", request.IdempotencyKey);
            if (!StringComparer.Ordinal.Equals(handle.TransactionId, changeSet.TransactionId))
            {
                return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.ChangeSetNotApplicable);
            }

            var completed = coordinator.Commit(handle, AuthorityCommitRequest.Empty, cancellationToken);
            return NarrativeStoreResults.Success(new NarrativeApplyStoreResult(
                changeSet.ChangeSetId,
                completed.TransactionId,
                changeSet.ImpactAnalysisId,
                completed.State,
                Existing: true));
        }
        catch (AuthorityRecoveryRequiredException exception)
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.RecoveryRequired, exception.Message);
        }
        catch (AuthorityTransactionException exception)
        {
            return NarrativeStoreResults.Fail<NarrativeApplyStoreResult>(NarrativeChangeError.AuthorityDirty, exception.Message);
        }
    }

    private void ApplyMutation(
        AuthoritySqliteTransactionContext transaction,
        NarrativeChangeSetSnapshot changeSet,
        NarrativeApplyStoreRequest request,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var validationFailure = ValidateAllChanges(transaction.Connection, transaction.Transaction, changeSet.Changes, cancellationToken);
        if (validationFailure is not null)
        {
            throw new NarrativePreconditionException(validationFailure);
        }

        var now = clock();
        foreach (var change in changeSet.Changes.OrderBy(change => change.Ordinal))
        {
            var current = ReadObject(transaction.Connection, transaction.Transaction, change.ObjectId)
                ?? throw new NarrativePreconditionException(new NarrativeChangeFailure(NarrativeChangeError.ObjectNotFound, change.ObjectId));
            var state = ReadCurrentState(transaction.Connection, transaction.Transaction, change.ObjectId);
            switch (change.ChangeKind)
            {
                case NarrativeChangeKind.Add:
                    RequireSingleRow(Execute(
                        transaction.Connection,
                        transaction.Transaction,
                        """
                        UPDATE objects
                        SET status='current',revision_no=1,deleted_at_ms=NULL,updated_at_ms=$now
                        WHERE object_id=$object_id AND status='proposed' AND revision_no=0;
                        """,
                        ("$now", now), ("$object_id", change.ObjectId)), change.ObjectId);
                    InsertStateRevision(transaction, change.ObjectId, transactionId, change.AfterPayloadDigest!, null, now);
                    break;

                case NarrativeChangeKind.Modify:
                    RequireSingleRow(Execute(
                        transaction.Connection,
                        transaction.Transaction,
                        """
                        UPDATE objects
                        SET revision_no=revision_no+1,updated_at_ms=$now
                        WHERE object_id=$object_id AND status='current' AND deleted_at_ms IS NULL;
                        """,
                        ("$now", now), ("$object_id", change.ObjectId)), change.ObjectId);
                    InsertStateRevision(transaction, change.ObjectId, transactionId, change.AfterPayloadDigest!, state!.StateRevisionId, now);
                    break;

                case NarrativeChangeKind.Remove:
                    RequireSingleRow(Execute(
                        transaction.Connection,
                        transaction.Transaction,
                        """
                        UPDATE objects
                        SET status='removed',revision_no=revision_no+1,deleted_at_ms=$now,updated_at_ms=$now
                        WHERE object_id=$object_id AND status='current' AND deleted_at_ms IS NULL;
                        """,
                        ("$now", now), ("$object_id", change.ObjectId)), change.ObjectId);
                    break;

                case NarrativeChangeKind.Reintroduce:
                    RequireSingleRow(Execute(
                        transaction.Connection,
                        transaction.Transaction,
                        """
                        UPDATE objects
                        SET status='current',revision_no=revision_no+1,deleted_at_ms=NULL,updated_at_ms=$now
                        WHERE object_id=$object_id AND status='removed' AND deleted_at_ms IS NOT NULL;
                        """,
                        ("$now", now), ("$object_id", change.ObjectId)), change.ObjectId);
                    InsertStateRevision(transaction, change.ObjectId, transactionId, change.AfterPayloadDigest!, state!.StateRevisionId, now);
                    break;

                default:
                    throw new NarrativePreconditionException(new NarrativeChangeFailure(NarrativeChangeError.InvalidChangeOperation));
            }
        }

        var applied = Execute(
            transaction.Connection,
            transaction.Transaction,
            """
            UPDATE narrative_change_sets
            SET status='applied',decider_kind=$decider_kind,decider_id=$decider_id,
                transaction_id=$transaction_id,updated_at_ms=$now
            WHERE change_set_id=$change_set_id AND status='working' AND transaction_id IS NULL;
            """,
            ("$decider_kind", DecisionKindValue(request.DeciderKind)),
            ("$decider_id", request.DeciderId),
            ("$transaction_id", transactionId),
            ("$now", now),
            ("$change_set_id", changeSet.ChangeSetId));
        RequireSingleRow(applied, changeSet.ChangeSetId);

        foreach (var edgeId in ReadAffectedEdgeIds(transaction.Connection, transaction.Transaction, changeSet.ImpactAnalysisId!))
        {
            Execute(
                transaction.Connection,
                transaction.Transaction,
                """
                UPDATE dependency_edges
                SET validation_status='needs_revalidation',updated_at_ms=$now
                WHERE edge_id=$edge_id;
                """,
                ("$now", now), ("$edge_id", edgeId));
        }
    }

    private NarrativeChangeFailure? ValidateAllChanges(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<NarrativeChangeRecord> changes,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes.OrderBy(change => change.Ordinal))
        {
            var current = ReadObject(connection, transaction, change.ObjectId);
            if (current is null)
            {
                return new NarrativeChangeFailure(NarrativeChangeError.ObjectNotFound, change.ObjectId);
            }

            var state = ReadCurrentState(connection, transaction, change.ObjectId);
            var requiresPayload = change.ChangeKind is NarrativeChangeKind.Add or NarrativeChangeKind.Modify or NarrativeChangeKind.Reintroduce;
            if (requiresPayload)
            {
                if (string.IsNullOrWhiteSpace(change.AfterPayloadDigest))
                {
                    return new NarrativeChangeFailure(NarrativeChangeError.PayloadMissing, change.ObjectId);
                }

                if (!blobStore.Verify(change.AfterPayloadDigest, cancellationToken))
                {
                    return new NarrativeChangeFailure(NarrativeChangeError.PayloadVerificationFailed, change.ObjectId);
                }
            }

            switch (change.ChangeKind)
            {
                case NarrativeChangeKind.Add:
                    if (StringComparer.Ordinal.Equals(current.Status, "current") && current.DeletedAtMs is null)
                    {
                        return new NarrativeChangeFailure(NarrativeChangeError.ObjectAlreadyCurrent, change.ObjectId);
                    }

                    if (!StringComparer.Ordinal.Equals(current.Status, "proposed") || current.RevisionNo != 0 || state is not null)
                    {
                        return new NarrativeChangeFailure(NarrativeChangeError.ChangeSetNotApplicable, change.ObjectId);
                    }

                    break;

                case NarrativeChangeKind.Modify or NarrativeChangeKind.Remove:
                    if (!StringComparer.Ordinal.Equals(current.Status, "current") || current.DeletedAtMs is not null)
                    {
                        return new NarrativeChangeFailure(NarrativeChangeError.ObjectNotCurrent, change.ObjectId);
                    }

                    if (state is null || !StringComparer.Ordinal.Equals(state.StateRevisionId, change.BeforeRevisionRef))
                    {
                        return new NarrativeChangeFailure(
                            NarrativeChangeError.PreconditionChanged,
                            $"{NarrativeChangeError.BeforeRevisionMismatch}:{change.ObjectId}");
                    }

                    if (!StringComparer.Ordinal.Equals(state.SnapshotDigest, change.BeforeDigest))
                    {
                        return new NarrativeChangeFailure(
                            NarrativeChangeError.PreconditionChanged,
                            $"{NarrativeChangeError.BeforeDigestMismatch}:{change.ObjectId}");
                    }

                    break;

                case NarrativeChangeKind.Reintroduce:
                    if (StringComparer.Ordinal.Equals(current.Status, "current") && current.DeletedAtMs is null)
                    {
                        return new NarrativeChangeFailure(NarrativeChangeError.ObjectAlreadyCurrent, change.ObjectId);
                    }

                    if (!StringComparer.Ordinal.Equals(current.Status, "removed") || current.DeletedAtMs is null)
                    {
                        return new NarrativeChangeFailure(NarrativeChangeError.ObjectNotCurrent, change.ObjectId);
                    }

                    if (state is null || !StringComparer.Ordinal.Equals(state.StateRevisionId, change.BeforeRevisionRef))
                    {
                        return new NarrativeChangeFailure(
                            NarrativeChangeError.PreconditionChanged,
                            $"{NarrativeChangeError.BeforeRevisionMismatch}:{change.ObjectId}");
                    }

                    if (!StringComparer.Ordinal.Equals(state.SnapshotDigest, change.BeforeDigest))
                    {
                        return new NarrativeChangeFailure(
                            NarrativeChangeError.PreconditionChanged,
                            $"{NarrativeChangeError.BeforeDigestMismatch}:{change.ObjectId}");
                    }

                    break;

                default:
                    return new NarrativeChangeFailure(NarrativeChangeError.InvalidChangeOperation);
            }
        }

        return null;
    }

    private static List<AuthorityEventData> CreateEvents(NarrativeChangeSetSnapshot changeSet)
    {
        List<AuthorityEventData> events =
        [
            new AuthorityEventData(
                DurableUuidV7.Create().ToString(),
                "narrative_change_set",
                changeSet.ChangeSetId,
                "narrative_change_set.applied",
                JsonSerializer.Serialize(new
                {
                    changeSetId = changeSet.ChangeSetId,
                    changeCount = changeSet.Changes.Count,
                    impactAnalysisId = changeSet.ImpactAnalysisId
                }))
        ];
        foreach (var change in changeSet.Changes.OrderBy(change => change.Ordinal))
        {
            events.Add(new AuthorityEventData(
                DurableUuidV7.Create().ToString(),
                "narrative_object",
                change.ObjectId,
                EventType(change.ChangeKind),
                JsonSerializer.Serialize(new
                {
                    changeSetId = changeSet.ChangeSetId,
                    narrativeChangeId = change.NarrativeChangeId,
                    changeKind = ChangeKindValue(change.ChangeKind),
                    change.BeforeRevisionRef,
                    change.BeforeDigest,
                    change.AfterPayloadDigest,
                    change.Ordinal
                })));
        }

        return events;
    }

    private static string ChangeKindValue(NarrativeChangeKind kind) => kind switch
    {
        NarrativeChangeKind.Add => "add",
        NarrativeChangeKind.Modify => "modify",
        NarrativeChangeKind.Remove => "remove",
        NarrativeChangeKind.Reintroduce => "reintroduce",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string EventType(NarrativeChangeKind kind) => kind switch
    {
        NarrativeChangeKind.Add => "narrative_object.added",
        NarrativeChangeKind.Modify => "narrative_object.modified",
        NarrativeChangeKind.Remove => "narrative_object.removed",
        NarrativeChangeKind.Reintroduce => "narrative_object.reintroduced",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string DecisionKindValue(NarrativeDecisionKind kind) => kind switch
    {
        NarrativeDecisionKind.AuthorConfirmed => "author_confirmed",
        NarrativeDecisionKind.AgentDelegated => "agent_delegated",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ImpactStatusValue(NarrativeImpactAnalysisStatus status) => status switch
    {
        NarrativeImpactAnalysisStatus.NoRelevantDependency => "no_relevant_dependency",
        NarrativeImpactAnalysisStatus.Affected => "affected",
        NarrativeImpactAnalysisStatus.Uncertain => "uncertain",
        NarrativeImpactAnalysisStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static NarrativeChangeKind ParseChangeKind(string value) => value switch
    {
        "add" => NarrativeChangeKind.Add,
        "modify" => NarrativeChangeKind.Modify,
        "remove" => NarrativeChangeKind.Remove,
        "reintroduce" => NarrativeChangeKind.Reintroduce,
        _ => throw new InvalidOperationException($"Unsupported narrative change kind '{value}'.")
    };

    private static ObjectRow? ReadObject(DbConnection connection, DbTransaction? transaction, string objectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT object_id,object_type,revision_no,status,deleted_at_ms
            FROM objects
            WHERE object_id=$object_id;
            """;
        Add(command, "$object_id", objectId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ObjectRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4))
            : null;
    }

    private static NarrativeStateRow? ReadCurrentState(DbConnection connection, DbTransaction? transaction, string objectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT state_revision_id,snapshot_digest
            FROM narrative_state_revisions current_state
            WHERE current_state.scope_object_id=$object_id
              AND NOT EXISTS(
                  SELECT 1
                  FROM narrative_state_revisions successor
                  WHERE successor.supersedes_state_revision_id=current_state.state_revision_id)
            ORDER BY current_state.created_at_ms DESC,current_state.state_revision_id DESC
            LIMIT 1;
            """;
        Add(command, "$object_id", objectId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new NarrativeStateRow(reader.GetString(0), reader.GetString(1)) : null;
    }

    private static List<NarrativeChangeRecord> ReadChanges(DbConnection connection, string changeSetId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT narrative_change_id,object_id,change_kind,before_revision_ref,before_digest,after_payload_digest,ordinal
            FROM narrative_changes
            WHERE change_set_id=$change_set_id
            ORDER BY ordinal;
            """;
        Add(command, "$change_set_id", changeSetId);
        List<NarrativeChangeRecord> changes = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            changes.Add(new NarrativeChangeRecord(
                reader.GetString(0),
                reader.GetString(1),
                ParseChangeKind(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6)));
        }

        return changes;
    }

    private static void InsertStateRevision(
        AuthoritySqliteTransactionContext transaction,
        string objectId,
        string transactionId,
        string snapshotDigest,
        string? supersedesStateRevisionId,
        long now)
    {
        Execute(
            transaction.Connection,
            transaction.Transaction,
            """
            INSERT INTO narrative_state_revisions(
                state_revision_id,scope_object_id,transaction_id,snapshot_digest,supersedes_state_revision_id,created_at_ms)
            VALUES($state_revision_id,$scope_object_id,$transaction_id,$snapshot_digest,$supersedes_state_revision_id,$now);
            """,
            ("$state_revision_id", DurableUuidV7.Create().ToString()),
            ("$scope_object_id", objectId),
            ("$transaction_id", transactionId),
            ("$snapshot_digest", snapshotDigest),
            ("$supersedes_state_revision_id", supersedesStateRevisionId),
            ("$now", now));
    }

    private static List<string> ReadAffectedEdgeIds(
        DbConnection connection,
        DbTransaction transaction,
        string impactAnalysisId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT value
            FROM impact_analyses, json_each(impact_analyses.affected_set_json,'$.dependencyEdgeIds')
            WHERE impact_analysis_id=$impact_analysis_id
            ORDER BY value;
            """;
        Add(command, "$impact_analysis_id", impactAnalysisId);
        List<string> edgeIds = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            edgeIds.Add(reader.GetString(0));
        }

        return edgeIds;
    }

    private string? ReadIdempotencyKey(string transactionId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idempotency_key FROM authority_transactions WHERE transaction_id=$transaction_id;";
        Add(command, "$transaction_id", transactionId);
        return command.ExecuteScalar() as string;
    }

    private AuthorityTransactionState? TryInspect(string transactionId)
    {
        try
        {
            return coordinator.Inspect(transactionId).State;
        }
        catch (AuthorityTransactionException)
        {
            return null;
        }
    }

    private void TryCleanUncommittedTransaction(string transactionId)
    {
        try
        {
            coordinator.Recover(transactionId);
        }
        catch (AuthorityTransactionException)
        {
        }
    }

    private static void RequireSingleRow(int affectedRows, string target)
    {
        if (affectedRows != 1)
        {
            throw new NarrativePreconditionException(new NarrativeChangeFailure(
                NarrativeChangeError.PreconditionChanged,
                target));
        }
    }

    private static int Execute(
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

        return command.ExecuteNonQuery();
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record ObjectRow(
        string ObjectId,
        string ObjectType,
        int RevisionNo,
        string Status,
        long? DeletedAtMs);

    private sealed record NarrativeStateRow(string StateRevisionId, string SnapshotDigest);

    private sealed class NarrativePreconditionException : Exception
    {
        public NarrativePreconditionException(NarrativeChangeFailure failure)
            : base(failure.Code.ToString())
        {
            Failure = failure;
        }

        public NarrativeChangeFailure Failure { get; }
    }
}
