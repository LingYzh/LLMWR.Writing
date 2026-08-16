using System.Data.Common;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Persistence;

namespace LLMW.Writing.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteRuntimeStore
{
    public void UpdateTaskCompletionContract(string taskId, string? completionContractJson) =>
        Execute(
            "UPDATE tasks SET completion_contract_json=$json WHERE task_id=$id;",
            ("$json", (object?)completionContractJson ?? DBNull.Value),
            ("$id", taskId));

    public DurableResultArtifactRecord InsertResultArtifact(DurableResultArtifactRecord artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Execute(
            """
            INSERT INTO result_artifacts(
                result_artifact_id, task_id, status, conclusion_json, findings_json, evidence_json,
                uncertainty_json, diagnostics_json, freshness_json, produced_at_ms)
            VALUES (
                $id, $task_id, $status, $conclusion, $findings, $evidence,
                $uncertainty, $diagnostics, $freshness, $produced_at_ms);
            """,
            ("$id", artifact.ResultArtifactId),
            ("$task_id", artifact.TaskId),
            ("$status", artifact.Status),
            ("$conclusion", (object?)artifact.ConclusionJson ?? DBNull.Value),
            ("$findings", (object?)artifact.FindingsJson ?? DBNull.Value),
            ("$evidence", (object?)artifact.EvidenceJson ?? DBNull.Value),
            ("$uncertainty", (object?)artifact.UncertaintyJson ?? DBNull.Value),
            ("$diagnostics", (object?)artifact.DiagnosticsJson ?? DBNull.Value),
            ("$freshness", artifact.FreshnessJson),
            ("$produced_at_ms", artifact.ProducedAtMs));
        return artifact;
    }

    public void ReplaceResultArtifact(DurableResultArtifactRecord artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Execute(
            """
            UPDATE result_artifacts
            SET task_id=$task_id, status=$status, conclusion_json=$conclusion, findings_json=$findings,
                evidence_json=$evidence, uncertainty_json=$uncertainty, diagnostics_json=$diagnostics,
                freshness_json=$freshness, produced_at_ms=$produced_at_ms
            WHERE result_artifact_id=$id;
            """,
            ("$id", artifact.ResultArtifactId),
            ("$task_id", artifact.TaskId),
            ("$status", artifact.Status),
            ("$conclusion", (object?)artifact.ConclusionJson ?? DBNull.Value),
            ("$findings", (object?)artifact.FindingsJson ?? DBNull.Value),
            ("$evidence", (object?)artifact.EvidenceJson ?? DBNull.Value),
            ("$uncertainty", (object?)artifact.UncertaintyJson ?? DBNull.Value),
            ("$diagnostics", (object?)artifact.DiagnosticsJson ?? DBNull.Value),
            ("$freshness", artifact.FreshnessJson),
            ("$produced_at_ms", artifact.ProducedAtMs));
    }

    public DurableResultArtifactRecord? GetLatestResultArtifact(string taskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT result_artifact_id, task_id, status, conclusion_json, findings_json, evidence_json,
                       uncertainty_json, diagnostics_json, freshness_json, produced_at_ms
                FROM result_artifacts
                WHERE task_id=$task_id
                ORDER BY produced_at_ms DESC, result_artifact_id DESC
                LIMIT 1;
                """);
            Add(command, "$task_id", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadArtifact(reader) : null;
        }
    }

    public DurableResultArtifactRecord? GetResultArtifact(string resultArtifactId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT result_artifact_id, task_id, status, conclusion_json, findings_json, evidence_json,
                       uncertainty_json, diagnostics_json, freshness_json, produced_at_ms
                FROM result_artifacts WHERE result_artifact_id=$id;
                """);
            Add(command, "$id", resultArtifactId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadArtifact(reader) : null;
        }
    }

    public void InsertEvidence(EvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Execute(
            """
            INSERT INTO evidence(
                evidence_id, run_id, task_id, source_kind, source_id, source_digest, locator_json, stale, created_at_ms)
            VALUES ($id, $run_id, $task_id, $source_kind, $source_id, $source_digest, $locator_json, $stale, $created_at_ms);
            """,
            ("$id", evidence.EvidenceId),
            ("$run_id", (object?)evidence.RunId ?? DBNull.Value),
            ("$task_id", (object?)evidence.TaskId ?? DBNull.Value),
            ("$source_kind", evidence.SourceKind),
            ("$source_id", evidence.SourceId),
            ("$source_digest", evidence.SourceDigest),
            ("$locator_json", evidence.LocatorJson),
            ("$stale", evidence.Stale ? 1 : 0),
            ("$created_at_ms", evidence.CreatedAtMs));
    }

    public IReadOnlyList<EvidenceRecord> EvidenceForTask(string taskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT evidence_id, run_id, task_id, source_kind, source_id, source_digest, locator_json, stale, created_at_ms
                FROM evidence WHERE task_id=$task_id ORDER BY created_at_ms, evidence_id;
                """);
            Add(command, "$task_id", taskId);
            using var reader = command.ExecuteReader();
            var list = new List<EvidenceRecord>();
            while (reader.Read())
            {
                list.Add(new EvidenceRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt32(7) == 1,
                    reader.GetInt64(8)));
            }

            return list;
        }
    }

    public EvidenceRecord? GetEvidence(string evidenceId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT evidence_id, run_id, task_id, source_kind, source_id, source_digest, locator_json, stale, created_at_ms
                FROM evidence WHERE evidence_id=$id;
                """);
            Add(command, "$id", evidenceId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new EvidenceRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7) == 1,
                reader.GetInt64(8));
        }
    }

    public void MarkEvidenceStale(string evidenceId, bool stale) =>
        Execute("UPDATE evidence SET stale=$stale WHERE evidence_id=$id;", ("$stale", stale ? 1 : 0), ("$id", evidenceId));

    public DurableDependencyRecord? GetDependency(string dependencyId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT dependency_id, consumer_task_id, producer_task_id, dependency_kind, status, result_artifact_id
                FROM result_dependencies WHERE dependency_id=$id;
                """);
            Add(command, "$id", dependencyId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new DurableDependencyRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5))
                : null;
        }
    }

    public IReadOnlyList<DurableDependencyRecord> DependenciesForConsumer(string consumerTaskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT dependency_id, consumer_task_id, producer_task_id, dependency_kind, status, result_artifact_id
                FROM result_dependencies WHERE consumer_task_id=$id ORDER BY dependency_id;
                """);
            Add(command, "$id", consumerTaskId);
            using var reader = command.ExecuteReader();
            var list = new List<DurableDependencyRecord>();
            while (reader.Read())
            {
                list.Add(new DurableDependencyRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return list;
        }
    }

    public void UpdateDependencyRecord(string dependencyId, string kind, string status, string? resultArtifactId) =>
        Execute(
            """
            UPDATE result_dependencies
            SET dependency_kind=$kind, status=$status, result_artifact_id=$result_artifact_id
            WHERE dependency_id=$id;
            """,
            ("$kind", kind),
            ("$status", status),
            ("$result_artifact_id", (object?)resultArtifactId ?? DBNull.Value),
            ("$id", dependencyId));

    public void InsertOversightOverride(OversightOverrideRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Execute(
            """
            INSERT INTO oversight_overrides(
                override_id, scope_kind, scope_id, narrative_authority, runtime_permission_mode,
                effective_after_checkpoint_id, created_by, created_at_ms)
            VALUES ($id, $scope_kind, $scope_id, $narrative, $permission, $checkpoint, $created_by, $created_at_ms);
            """,
            ("$id", record.OverrideId),
            ("$scope_kind", OversightScopeKindCodec.ToDurableValue(record.ScopeKind)),
            ("$scope_id", (object?)record.ScopeId ?? DBNull.Value),
            ("$narrative", NarrativeDecisionAuthorityCodec.ToDurableValue(record.NarrativeAuthority)),
            ("$permission", RuntimePermissionModeDurableCodec.ToDurableValue(record.RuntimePermission)),
            ("$checkpoint", (object?)record.EffectiveAfterCheckpointId ?? DBNull.Value),
            ("$created_by", record.CreatedBy),
            ("$created_at_ms", record.CreatedAtMs));
    }

    public IReadOnlyList<OversightOverrideRecord> ListOversightOverrides()
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT override_id, scope_kind, scope_id, narrative_authority, runtime_permission_mode,
                       effective_after_checkpoint_id, created_by, created_at_ms
                FROM oversight_overrides ORDER BY created_at_ms, override_id;
                """);
            using var reader = command.ExecuteReader();
            var list = new List<OversightOverrideRecord>();
            while (reader.Read())
            {
                if (!OversightScopeKindCodec.TryParse(reader.GetString(1), out var scope) ||
                    !NarrativeDecisionAuthorityCodec.TryParse(reader.GetString(3), out var narrative) ||
                    !RuntimePermissionModeDurableCodec.TryParse(reader.GetString(4), out var permission))
                {
                    throw new InvalidOperationException("Corrupt oversight_overrides row.");
                }
                list.Add(new OversightOverrideRecord(
                    reader.GetString(0),
                    scope,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    narrative,
                    permission,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt64(7)));
            }

            return list;
        }
    }

    public void BindPendingOversightOverrides(string checkpointId, string runId, string? taskId, long checkpointCreatedAtMs)
    {
        _ = checkpointCreatedAtMs;
        _ = runId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        Execute(
            """
            UPDATE oversight_overrides
            SET effective_after_checkpoint_id=$checkpoint
            WHERE effective_after_checkpoint_id LIKE 'pending:%'
              AND scope_kind='task'
              AND scope_id=$task_id;
            """,
            ("$checkpoint", checkpointId),
            ("$task_id", taskId));
    }

    public void InsertDelegatedDecision(DelegatedDecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var existing = GetDelegatedDecision(record.DelegatedDecisionId);
        if (existing is not null)
        {
            if (!DelegatedDecisionEquality.Equivalent(existing, record))
            {
            throw new DelegatedDecisionConflictException(record.DelegatedDecisionId);
            }

            return;
        }

        Execute(
            """
            INSERT INTO delegated_decisions(
                delegated_decision_id, transaction_id, scope_kind, scope_id, proposed_by, confirmed_by,
                decided_by, authority_kind, oversight_mode, payload_digest, decided_at_ms)
            VALUES (
                $id, $transaction_id, $scope_kind, $scope_id, $proposed_by, $confirmed_by,
                $decided_by, $authority_kind, $oversight_mode, $payload_digest, $decided_at_ms);
            """,
            ("$id", record.DelegatedDecisionId),
            ("$transaction_id", (object?)record.TransactionId ?? DBNull.Value),
            ("$scope_kind", OversightScopeKindCodec.ToDurableValue(record.ScopeKind)),
            ("$scope_id", record.ScopeId),
            ("$proposed_by", (object?)record.ProposedBy ?? DBNull.Value),
            ("$confirmed_by", (object?)record.ConfirmedBy ?? DBNull.Value),
            ("$decided_by", record.DecidedBy),
            ("$authority_kind", NarrativeDecisionProvenance.AuthorityKindDurable(record.AuthorityKind)),
            ("$oversight_mode", record.OversightMode),
            ("$payload_digest", (object?)record.PayloadDigest ?? DBNull.Value),
            ("$decided_at_ms", record.DecidedAtMs));
    }

    public DelegatedDecisionRecord? GetDelegatedDecision(string delegatedDecisionId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT delegated_decision_id, transaction_id, scope_kind, scope_id, proposed_by, confirmed_by,
                       decided_by, authority_kind, oversight_mode, payload_digest, decided_at_ms
                FROM delegated_decisions WHERE delegated_decision_id=$id;
                """);
            Add(command, "$id", delegatedDecisionId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDelegated(reader) : null;
        }
    }

    public IReadOnlyList<DelegatedDecisionRecord> ListDelegatedDecisions()
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT delegated_decision_id, transaction_id, scope_kind, scope_id, proposed_by, confirmed_by,
                       decided_by, authority_kind, oversight_mode, payload_digest, decided_at_ms
                FROM delegated_decisions ORDER BY decided_at_ms, delegated_decision_id;
                """);
            using var reader = command.ExecuteReader();
            var list = new List<DelegatedDecisionRecord>();
            while (reader.Read())
            {
                list.Add(ReadDelegated(reader));
            }

            return list;
        }
    }

    public void InsertApproval(DurableApprovalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Execute(
            """
            INSERT INTO approvals(
                approval_id, run_id, task_id, approval_kind, status, payload_digest, decided_by, decided_at_ms, created_at_ms)
            VALUES ($id, $run_id, $task_id, $kind, $status, $digest, $decided_by, $decided_at_ms, $created_at_ms);
            """,
            ("$id", record.ApprovalId),
            ("$run_id", record.RunId),
            ("$task_id", (object?)record.TaskId ?? DBNull.Value),
            ("$kind", record.ApprovalKind),
            ("$status", record.Status),
            ("$digest", (object?)record.PayloadDigest ?? DBNull.Value),
            ("$decided_by", (object?)record.DecidedBy ?? DBNull.Value),
            ("$decided_at_ms", (object?)record.DecidedAtMs ?? DBNull.Value),
            ("$created_at_ms", record.CreatedAtMs));
    }

    public DurableApprovalRecord? GetApproval(string approvalId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT approval_id, run_id, task_id, approval_kind, status, payload_digest, decided_by, decided_at_ms, created_at_ms
                FROM approvals WHERE approval_id=$id;
                """);
            Add(command, "$id", approvalId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadApproval(reader) : null;
        }
    }

    public void UpdateApproval(DurableApprovalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Execute(
            """
            UPDATE approvals
            SET status=$status, decided_by=$decided_by, decided_at_ms=$decided_at_ms, payload_digest=$digest
            WHERE approval_id=$id;
            """,
            ("$status", record.Status),
            ("$decided_by", (object?)record.DecidedBy ?? DBNull.Value),
            ("$decided_at_ms", (object?)record.DecidedAtMs ?? DBNull.Value),
            ("$digest", (object?)record.PayloadDigest ?? DBNull.Value),
            ("$id", record.ApprovalId));
    }

    public bool TryCompareAndSetApproval(string approvalId, string expectedStatus, DurableApprovalRecord replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                UPDATE approvals
                SET status=$status, decided_by=$decided_by, decided_at_ms=$decided_at_ms, payload_digest=$digest
                WHERE approval_id=$id AND status=$expected;
                """);
            Add(command, "$status", replacement.Status);
            Add(command, "$decided_by", (object?)replacement.DecidedBy ?? DBNull.Value);
            Add(command, "$decided_at_ms", (object?)replacement.DecidedAtMs ?? DBNull.Value);
            Add(command, "$digest", (object?)replacement.PayloadDigest ?? DBNull.Value);
            Add(command, "$id", approvalId);
            Add(command, "$expected", expectedStatus);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<DurableApprovalRecord> ListApprovals(string? runId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT approval_id, run_id, task_id, approval_kind, status, payload_digest, decided_by, decided_at_ms, created_at_ms
                FROM approvals
                WHERE ($run_id IS NULL OR run_id=$run_id)
                ORDER BY created_at_ms, approval_id;
                """);
            Add(command, "$run_id", (object?)runId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var list = new List<DurableApprovalRecord>();
            while (reader.Read())
            {
                list.Add(ReadApproval(reader));
            }

            return list;
        }
    }

    public void InsertBackgroundTask(DurableBackgroundTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Execute(
            """
            INSERT INTO background_tasks(
                background_task_id, owner_run_id, owner_task_id, kind, status, checkpoint_id, started_at_ms, completed_at_ms)
            VALUES ($id, $owner_run_id, $owner_task_id, $kind, $status, $checkpoint_id, $started_at_ms, $completed_at_ms);
            """,
            ("$id", record.BackgroundTaskId),
            ("$owner_run_id", record.OwnerRunId),
            ("$owner_task_id", (object?)record.OwnerTaskId ?? DBNull.Value),
            ("$kind", record.KindJson),
            ("$status", record.Status),
            ("$checkpoint_id", (object?)record.CheckpointId ?? DBNull.Value),
            ("$started_at_ms", record.StartedAtMs),
            ("$completed_at_ms", (object?)record.CompletedAtMs ?? DBNull.Value));
    }

    public void UpdateBackgroundTask(DurableBackgroundTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Execute(
            """
            UPDATE background_tasks
            SET status=$status, checkpoint_id=$checkpoint_id, completed_at_ms=$completed_at_ms, kind=$kind
            WHERE background_task_id=$id;
            """,
            ("$status", record.Status),
            ("$checkpoint_id", (object?)record.CheckpointId ?? DBNull.Value),
            ("$completed_at_ms", (object?)record.CompletedAtMs ?? DBNull.Value),
            ("$kind", record.KindJson),
            ("$id", record.BackgroundTaskId));
    }

    public DurableBackgroundTaskRecord? GetBackgroundTask(string backgroundTaskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT background_task_id, owner_run_id, owner_task_id, kind, status, checkpoint_id, started_at_ms, completed_at_ms
                FROM background_tasks WHERE background_task_id=$id;
                """);
            Add(command, "$id", backgroundTaskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadBackground(reader) : null;
        }
    }

    public IReadOnlyList<DurableBackgroundTaskRecord> ListBackgroundTasks(string? ownerRunId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT background_task_id, owner_run_id, owner_task_id, kind, status, checkpoint_id, started_at_ms, completed_at_ms
                FROM background_tasks
                WHERE ($owner_run_id IS NULL OR owner_run_id=$owner_run_id)
                ORDER BY started_at_ms, background_task_id;
                """);
            Add(command, "$owner_run_id", (object?)ownerRunId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var list = new List<DurableBackgroundTaskRecord>();
            while (reader.Read())
            {
                list.Add(ReadBackground(reader));
            }

            return list;
        }
    }

    public void UpsertProjectSpecialist(DurableProjectSpecialistRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Execute(
            """
            INSERT INTO specialist_profiles(
                specialist_profile_id, scope_kind, project_id, name, version, definition_json,
                base_definition_digest, enabled, created_at_ms, updated_at_ms)
            VALUES (
                $id, $scope_kind, $project_id, $name, $version, $definition_json,
                $base_digest, $enabled, $created_at_ms, $updated_at_ms)
            ON CONFLICT(specialist_profile_id) DO UPDATE SET
                name=$name,
                version=$version,
                definition_json=$definition_json,
                base_definition_digest=$base_digest,
                enabled=$enabled,
                updated_at_ms=$updated_at_ms;
            """,
            ("$id", record.SpecialistProfileId),
            ("$scope_kind", record.ScopeKind),
            ("$project_id", (object?)record.ProjectId ?? DBNull.Value),
            ("$name", record.Name),
            ("$version", record.Version),
            ("$definition_json", record.DefinitionJson),
            ("$base_digest", (object?)record.BaseDefinitionDigest ?? DBNull.Value),
            ("$enabled", record.Enabled ? 1 : 0),
            ("$created_at_ms", record.CreatedAtMs),
            ("$updated_at_ms", record.UpdatedAtMs));
    }

    public DurableProjectSpecialistRecord? GetProjectSpecialist(string profileId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT specialist_profile_id, scope_kind, project_id, name, version, definition_json,
                       base_definition_digest, enabled, created_at_ms, updated_at_ms
                FROM specialist_profiles WHERE specialist_profile_id=$id;
                """);
            Add(command, "$id", profileId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSpecialist(reader) : null;
        }
    }

    public IReadOnlyList<DurableProjectSpecialistRecord> ListProjectSpecialists()
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT specialist_profile_id, scope_kind, project_id, name, version, definition_json,
                       base_definition_digest, enabled, created_at_ms, updated_at_ms
                FROM specialist_profiles ORDER BY name, specialist_profile_id;
                """);
            using var reader = command.ExecuteReader();
            var list = new List<DurableProjectSpecialistRecord>();
            while (reader.Read())
            {
                list.Add(ReadSpecialist(reader));
            }

            return list;
        }
    }

    public DurableAttemptRecord? FindActiveAttempt(string taskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT attempt_id, task_id, attempt_no, status, started_at_ms, completed_at_ms
                FROM attempts
                WHERE task_id=$id AND status IN ('starting','running')
                ORDER BY attempt_no DESC, attempt_id DESC
                LIMIT 1;
                """);
            Add(command, "$id", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAttempt(reader) : null;
        }
    }

    public bool StorylineExists(string storylineId)
    {
        if (string.IsNullOrWhiteSpace(storylineId))
        {
            return false;
        }

        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, "SELECT 1 FROM storylines WHERE storyline_id=$id LIMIT 1;");
            Add(command, "$id", storylineId);
            using var reader = command.ExecuteReader();
            return reader.Read();
        }
    }

    public DurableToolCallRecord? GetToolCall(string toolCallId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT tool_call_id, run_id, task_id, tool_name, status, side_effect_state
                FROM tool_calls WHERE tool_call_id=$id;
                """);
            Add(command, "$id", toolCallId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new DurableToolCallRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5));
        }
    }

    public bool TryCancelToolCall(string toolCallId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                UPDATE tool_calls SET status='cancelled'
                WHERE tool_call_id=$id;
                """);
            Add(command, "$id", toolCallId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    private static DelegatedDecisionRecord ReadDelegated(DbDataReader reader)
    {
        if (!OversightScopeKindCodec.TryParse(reader.GetString(2), out var scope))
        {
            throw new InvalidOperationException("Corrupt delegated_decisions row.");
        }

        var authority = StringComparer.Ordinal.Equals(reader.GetString(7), "AGENT_DELEGATED")
            ? LLMW.Writing.Domain.Authority.DecisionAuthorityKind.AgentDelegated
            : LLMW.Writing.Domain.Authority.DecisionAuthorityKind.AuthorConfirmed;
        return new DelegatedDecisionRecord(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            scope,
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            authority,
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt64(10));
    }

    private static DurableResultArtifactRecord ReadArtifact(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9));

    private static DurableApprovalRecord ReadApproval(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.GetInt64(8));

    private static DurableBackgroundTaskRecord ReadBackground(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7));

    private static DurableProjectSpecialistRecord ReadSpecialist(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7) == 1,
            reader.GetInt64(8),
            reader.GetInt64(9));
}
