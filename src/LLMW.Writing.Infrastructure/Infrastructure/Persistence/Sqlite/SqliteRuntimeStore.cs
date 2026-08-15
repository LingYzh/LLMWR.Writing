using System.Data.Common;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Persistence;

namespace LLMW.Writing.Infrastructure.Persistence.Sqlite;

public sealed partial class SqliteRuntimeStore : IRuntimePersistence
{
    private readonly string databasePath;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly object gate = new();
    private DbConnection? ambientConnection;
    private DbTransaction? ambientTransaction;

    public SqliteRuntimeStore(string databasePath, SqliteDatabaseConnectionFactory? connectionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
    }

    public SchedulerSnapshot LoadSnapshot()
    {
        lock (gate)
        {
            using var lease = Open();
            return new SchedulerSnapshot(
                ReadWorkflows(lease.Connection, lease.Transaction),
                ReadRuns(lease.Connection, lease.Transaction),
                ReadTasks(lease.Connection, lease.Transaction),
                ReadAttempts(lease.Connection, lease.Transaction),
                ReadDependencies(lease.Connection, lease.Transaction),
                ReadToolCalls(lease.Connection, lease.Transaction),
                ReadCheckpoints(lease.Connection, lease.Transaction));
        }
    }

    public DurableWorkflowRunRecord InsertWorkflowRun(string workflowRunId, string status, long nowMs, string? storylineId = null)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                INSERT INTO workflow_runs(workflow_run_id, storyline_id, status, oversight_scope_json, created_at_ms, updated_at_ms)
                VALUES ($id, $storyline, $status, NULL, $now, $now);
                """);
            Add(command, "$id", workflowRunId);
            Add(command, "$storyline", (object?)storylineId ?? DBNull.Value);
            Add(command, "$status", status);
            Add(command, "$now", nowMs);
            command.ExecuteNonQuery();
            return new DurableWorkflowRunRecord(workflowRunId, status, nowMs, nowMs, storylineId);
        }
    }

    public DurableRunRecord InsertRun(DurableRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                INSERT INTO runs(
                    run_id, workflow_run_id, parent_run_id, role, status, depth,
                    provider_id, model_id, prompt_config_id, effective_prompt_digest, created_at_ms, updated_at_ms)
                VALUES (
                    $run_id, $workflow_run_id, $parent_run_id, $role, $status, $depth,
                    $provider_id, $model_id, $prompt_config_id, $effective_prompt_digest, $created_at_ms, $updated_at_ms);
                """);
            Add(command, "$run_id", run.RunId);
            Add(command, "$workflow_run_id", run.WorkflowRunId);
            Add(command, "$parent_run_id", (object?)run.ParentRunId ?? DBNull.Value);
            Add(command, "$role", run.Role);
            Add(command, "$status", run.Status);
            Add(command, "$depth", run.Depth);
            Add(command, "$provider_id", (object?)run.ProviderId ?? DBNull.Value);
            Add(command, "$model_id", (object?)run.ModelId ?? DBNull.Value);
            Add(command, "$prompt_config_id", (object?)run.PromptConfigId ?? DBNull.Value);
            Add(command, "$effective_prompt_digest", (object?)run.EffectivePromptDigest ?? DBNull.Value);
            Add(command, "$created_at_ms", run.CreatedAtMs);
            Add(command, "$updated_at_ms", run.UpdatedAtMs);
            command.ExecuteNonQuery();
            return run;
        }
    }

    public DurableTaskRecord InsertTask(DurableTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                INSERT INTO tasks(
                    task_id, run_id, parent_task_id, task_kind, status, priority, completion_contract_json, created_at_ms, updated_at_ms)
                VALUES (
                    $task_id, $run_id, $parent_task_id, $task_kind, $status, $priority, $completion_contract_json, $created_at_ms, $updated_at_ms);
                """);
            Add(command, "$task_id", task.TaskId);
            Add(command, "$run_id", task.RunId);
            Add(command, "$parent_task_id", (object?)task.ParentTaskId ?? DBNull.Value);
            Add(command, "$task_kind", task.TaskKind);
            Add(command, "$status", task.Status);
            Add(command, "$priority", task.Priority);
            Add(command, "$completion_contract_json", (object?)task.CompletionContractJson ?? DBNull.Value);
            Add(command, "$created_at_ms", task.CreatedAtMs);
            Add(command, "$updated_at_ms", task.UpdatedAtMs);
            command.ExecuteNonQuery();
            return task;
        }
    }

    public DurableAttemptRecord InsertAttempt(DurableAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                INSERT INTO attempts(attempt_id, task_id, attempt_no, status, started_at_ms, completed_at_ms)
                VALUES ($attempt_id, $task_id, $attempt_no, $status, $started_at_ms, $completed_at_ms);
                """);
            Add(command, "$attempt_id", attempt.AttemptId);
            Add(command, "$task_id", attempt.TaskId);
            Add(command, "$attempt_no", attempt.AttemptNo);
            Add(command, "$status", attempt.Status);
            Add(command, "$started_at_ms", attempt.StartedAtMs);
            Add(command, "$completed_at_ms", (object?)attempt.CompletedAtMs ?? DBNull.Value);
            command.ExecuteNonQuery();
            return attempt;
        }
    }

    public void InsertDependency(DurableDependencyRecord dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                INSERT INTO result_dependencies(
                    dependency_id, consumer_task_id, producer_task_id, result_artifact_id, dependency_kind, status)
                VALUES ($dependency_id, $consumer_task_id, $producer_task_id, $result_artifact_id, $dependency_kind, $status);
                """);
            Add(command, "$dependency_id", dependency.DependencyId);
            Add(command, "$consumer_task_id", dependency.ConsumerTaskId);
            Add(command, "$producer_task_id", dependency.ProducerTaskId);
            Add(command, "$result_artifact_id", (object?)dependency.ResultArtifactId ?? DBNull.Value);
            Add(command, "$dependency_kind", dependency.DependencyKind);
            Add(command, "$status", dependency.Status);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateWorkflowRunStatus(string workflowRunId, string status, long nowMs) =>
        Execute(
            "UPDATE workflow_runs SET status=$status, updated_at_ms=$now WHERE workflow_run_id=$id;",
            ("$status", status),
            ("$now", nowMs),
            ("$id", workflowRunId));

    public void UpdateRunStatus(string runId, string status, long nowMs) =>
        Execute(
            "UPDATE runs SET status=$status, updated_at_ms=$now WHERE run_id=$id;",
            ("$status", status),
            ("$now", nowMs),
            ("$id", runId));

    public void UpdateTaskStatus(string taskId, string status, long nowMs) =>
        Execute(
            "UPDATE tasks SET status=$status, updated_at_ms=$now WHERE task_id=$id;",
            ("$status", status),
            ("$now", nowMs),
            ("$id", taskId));

    public void UpdateAttemptStatus(string attemptId, string status, long? completedAtMs)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(
                lease,
                "UPDATE attempts SET status=$status, completed_at_ms=$completed_at_ms WHERE attempt_id=$id;");
            Add(command, "$status", status);
            Add(command, "$completed_at_ms", (object?)completedAtMs ?? DBNull.Value);
            Add(command, "$id", attemptId);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateDependencyStatus(string producerTaskId, string status) =>
        Execute(
            "UPDATE result_dependencies SET status=$status WHERE producer_task_id=$id;",
            ("$status", status),
            ("$id", producerTaskId));

    public string InsertCheckpoint(DurableCheckpointRecord checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                INSERT INTO checkpoints(
                    checkpoint_id, run_id, task_id, schema_version, payload_json, input_digest_set_json, created_at_ms)
                VALUES (
                    $checkpoint_id, $run_id, $task_id, $schema_version, $payload_json, $input_digest_set_json, $created_at_ms);
                """);
            Add(command, "$checkpoint_id", checkpoint.CheckpointId);
            Add(command, "$run_id", checkpoint.RunId);
            Add(command, "$task_id", (object?)checkpoint.TaskId ?? DBNull.Value);
            Add(command, "$schema_version", checkpoint.SchemaVersion);
            Add(command, "$payload_json", checkpoint.PayloadJson);
            Add(command, "$input_digest_set_json", checkpoint.InputDigestSetJson);
            Add(command, "$created_at_ms", checkpoint.CreatedAtMs);
            command.ExecuteNonQuery();
            return checkpoint.CheckpointId;
        }
    }

    public DurableWorkflowRunRecord? GetWorkflowRun(string workflowRunId) =>
        LoadSnapshot().WorkflowRuns.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.WorkflowRunId, workflowRunId));

    public DurableRunRecord? GetRun(string runId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT run_id, workflow_run_id, parent_run_id, role, status, depth,
                       provider_id, model_id, prompt_config_id, effective_prompt_digest, created_at_ms, updated_at_ms
                FROM runs WHERE run_id=$id;
                """);
            Add(command, "$id", runId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRun(reader) : null;
        }
    }

    public DurableTaskRecord? GetTask(string taskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT task_id, run_id, parent_task_id, task_kind, status, priority, created_at_ms, updated_at_ms, completion_contract_json
                FROM tasks WHERE task_id=$id;
                """);
            Add(command, "$id", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadTask(reader) : null;
        }
    }

    public DurableAttemptRecord? GetAttempt(string attemptId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT attempt_id, task_id, attempt_no, status, started_at_ms, completed_at_ms
                FROM attempts WHERE attempt_id=$id;
                """);
            Add(command, "$id", attemptId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAttempt(reader) : null;
        }
    }

    public DurableAttemptRecord? FindStartingAttempt(string taskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT attempt_id, task_id, attempt_no, status, started_at_ms, completed_at_ms
                FROM attempts
                WHERE task_id=$id AND status='starting'
                ORDER BY attempt_no DESC, attempt_id DESC
                LIMIT 1;
                """);
            Add(command, "$id", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAttempt(reader) : null;
        }
    }

    public int MaxAttemptNo(string taskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, "SELECT COALESCE(MAX(attempt_no), 0) FROM attempts WHERE task_id=$id;");
            Add(command, "$id", taskId);
            var value = command.ExecuteScalar();
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<DurableCheckpointRecord> CheckpointsForRun(string runId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT checkpoint_id, run_id, task_id, schema_version, payload_json, input_digest_set_json, created_at_ms
                FROM checkpoints
                WHERE run_id=$id
                ORDER BY created_at_ms DESC, checkpoint_id DESC;
                """);
            Add(command, "$id", runId);
            using var reader = command.ExecuteReader();
            var list = new List<DurableCheckpointRecord>();
            while (reader.Read())
            {
                list.Add(ReadCheckpoint(reader));
            }

            return list;
        }
    }

    public IReadOnlyList<DurableToolCallRecord> ToolCallsFor(string? runId, string? taskId)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                SELECT tool_call_id, run_id, task_id, tool_name, status, side_effect_state
                FROM tool_calls
                WHERE ($run_id IS NULL OR run_id=$run_id)
                  AND ($task_id IS NULL OR task_id=$task_id);
                """);
            Add(command, "$run_id", (object?)runId ?? DBNull.Value);
            Add(command, "$task_id", (object?)taskId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var list = new List<DurableToolCallRecord>();
            while (reader.Read())
            {
                list.Add(new DurableToolCallRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }

            return list;
        }
    }

    public void InsertToolCall(DurableToolCallRecord toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, """
                INSERT INTO tool_calls(
                    tool_call_id, run_id, task_id, tool_name, arguments_digest, status, side_effect_state,
                    idempotency_key, started_at_ms, completed_at_ms)
                VALUES (
                    $tool_call_id, $run_id, $task_id, $tool_name, NULL, $status, $side_effect_state,
                    NULL, $started_at_ms, NULL);
                """);
            Add(command, "$tool_call_id", string.IsNullOrWhiteSpace(toolCall.ToolCallId) ? DurableUuidV7.Create().ToString() : toolCall.ToolCallId);
            Add(command, "$run_id", toolCall.RunId);
            Add(command, "$task_id", (object?)toolCall.TaskId ?? DBNull.Value);
            Add(command, "$tool_name", toolCall.ToolName);
            Add(command, "$status", toolCall.Status);
            Add(command, "$side_effect_state", toolCall.SideEffectState);
            Add(command, "$started_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
    }

    public void MarkRunningToolCallsUnknown(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        Execute(
            """
            UPDATE tool_calls
            SET side_effect_state=$unknown
            WHERE run_id=$run_id AND status=$running AND side_effect_state<>$unknown;
            """,
            ("$unknown", SideEffectStateCodec.ToDurableValue(SideEffectState.Unknown)),
            ("$run_id", runId),
            ("$running", "running"));
    }

    public void InTransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            if (ambientConnection is not null)
            {
                action();
                return;
            }

            using var connection = connectionFactory.OpenConfigured(databasePath);
            using var transaction = connection.BeginTransaction();
            ambientConnection = connection;
            ambientTransaction = transaction;
            try
            {
                action();
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch (Exception rollback) when (rollback is not OperationCanceledException)
                {
                    _ = rollback;
                }

                throw;
            }
            finally
            {
                ambientTransaction = null;
                ambientConnection = null;
            }
        }
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        lock (gate)
        {
            using var lease = Open();
            using var command = Create(lease, sql);
            foreach (var parameter in parameters)
            {
                Add(command, parameter.Name, parameter.Value);
            }

            command.ExecuteNonQuery();
        }
    }

    private ConnectionLease Open()
    {
        if (ambientConnection is not null)
        {
            return new ConnectionLease(ambientConnection, ambientTransaction, owns: false);
        }

        return new ConnectionLease(connectionFactory.OpenConfigured(databasePath), null, owns: true);
    }

    private static DbCommand Create(ConnectionLease lease, string sql)
    {
        var command = lease.Connection.CreateCommand();
        command.Transaction = lease.Transaction;
        command.CommandText = sql;
        return command;
    }

    private static List<DurableWorkflowRunRecord> ReadWorkflows(DbConnection connection, DbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT workflow_run_id, status, created_at_ms, updated_at_ms, storyline_id FROM workflow_runs ORDER BY created_at_ms, workflow_run_id;";
        using var reader = command.ExecuteReader();
        var list = new List<DurableWorkflowRunRecord>();
        while (reader.Read())
        {
            list.Add(new DurableWorkflowRunRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return list;
    }

    private static List<DurableRunRecord> ReadRuns(DbConnection connection, DbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, workflow_run_id, parent_run_id, role, status, depth,
                   provider_id, model_id, prompt_config_id, effective_prompt_digest, created_at_ms, updated_at_ms
            FROM runs ORDER BY created_at_ms, run_id;
            """;
        using var reader = command.ExecuteReader();
        var list = new List<DurableRunRecord>();
        while (reader.Read())
        {
            list.Add(ReadRun(reader));
        }

        return list;
    }

    private static List<DurableTaskRecord> ReadTasks(DbConnection connection, DbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT task_id, run_id, parent_task_id, task_kind, status, priority, created_at_ms, updated_at_ms, completion_contract_json
            FROM tasks ORDER BY created_at_ms, task_id;
            """;
        using var reader = command.ExecuteReader();
        var list = new List<DurableTaskRecord>();
        while (reader.Read())
        {
            list.Add(ReadTask(reader));
        }

        return list;
    }

    private static List<DurableAttemptRecord> ReadAttempts(DbConnection connection, DbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT attempt_id, task_id, attempt_no, status, started_at_ms, completed_at_ms
            FROM attempts ORDER BY started_at_ms, attempt_id;
            """;
        using var reader = command.ExecuteReader();
        var list = new List<DurableAttemptRecord>();
        while (reader.Read())
        {
            list.Add(ReadAttempt(reader));
        }

        return list;
    }

    private static List<DurableDependencyRecord> ReadDependencies(DbConnection connection, DbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT dependency_id, consumer_task_id, producer_task_id, dependency_kind, status, result_artifact_id
            FROM result_dependencies ORDER BY dependency_id;
            """;
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

    private static List<DurableToolCallRecord> ReadToolCalls(DbConnection connection, DbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT tool_call_id, run_id, task_id, tool_name, status, side_effect_state
            FROM tool_calls ORDER BY tool_call_id;
            """;
        using var reader = command.ExecuteReader();
        var list = new List<DurableToolCallRecord>();
        while (reader.Read())
        {
            list.Add(new DurableToolCallRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return list;
    }

    private static List<DurableCheckpointRecord> ReadCheckpoints(DbConnection connection, DbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT checkpoint_id, run_id, task_id, schema_version, payload_json, input_digest_set_json, created_at_ms
            FROM checkpoints ORDER BY created_at_ms, checkpoint_id;
            """;
        using var reader = command.ExecuteReader();
        var list = new List<DurableCheckpointRecord>();
        while (reader.Read())
        {
            list.Add(ReadCheckpoint(reader));
        }

        return list;
    }

    private static DurableRunRecord ReadRun(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static DurableTaskRecord ReadTask(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));

    private static DurableAttemptRecord ReadAttempt(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));

    private static DurableCheckpointRecord ReadCheckpoint(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class ConnectionLease : IDisposable
    {
        public ConnectionLease(DbConnection connection, DbTransaction? transaction, bool owns)
        {
            Connection = connection;
            Transaction = transaction;
            this.owns = owns;
        }

        public DbConnection Connection { get; }

        public DbTransaction? Transaction { get; }

        private readonly bool owns;

        public void Dispose()
        {
            if (owns)
            {
                Connection.Dispose();
            }
        }
    }
}
