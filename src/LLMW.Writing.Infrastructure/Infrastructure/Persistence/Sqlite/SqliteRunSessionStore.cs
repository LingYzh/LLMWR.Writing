using System.Data.Common;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Infrastructure.Persistence;

namespace LLMW.Writing.Infrastructure.Persistence.Sqlite;

public sealed class SqliteRunSessionStore : IRunSessionStore
{
    private readonly string databasePath;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;

    public SqliteRunSessionStore(
        string databasePath,
        SqliteDatabaseConnectionFactory? connectionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
    }

    public DurableRunIdentity? LoadRun(string runId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id, role FROM runs WHERE run_id=$run_id;";
        Add(command, "$run_id", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new DurableRunIdentity(reader.GetString(0), reader.GetString(1)) : null;
    }

    public StoredRunSession IssueReplacingActive(PersistRunSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handleId = DurableUuidV7.Create().ToString();
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();

        using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText =
                """
                UPDATE run_session_handles
                SET revoked_at_ms=$revoked_at_ms
                WHERE run_id=$run_id
                  AND worker_instance_id=$worker_instance_id
                  AND channel_instance_id=$channel_instance_id
                  AND project_scope=$project_scope
                  AND revoked_at_ms IS NULL;
                """;
            Add(revoke, "$revoked_at_ms", request.CreatedAtMs);
            Add(revoke, "$run_id", request.RunId);
            Add(revoke, "$worker_instance_id", request.WorkerInstanceId);
            Add(revoke, "$channel_instance_id", request.ChannelInstanceId);
            Add(revoke, "$project_scope", request.ProjectScope);
            revoke.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO run_session_handles(
                    handle_id, run_id, worker_instance_id, channel_instance_id, project_scope,
                    token_hash, expires_at_ms, revoked_at_ms, created_at_ms)
                VALUES(
                    $handle_id, $run_id, $worker_instance_id, $channel_instance_id, $project_scope,
                    $token_hash, $expires_at_ms, NULL, $created_at_ms);
                """;
            Add(insert, "$handle_id", handleId);
            Add(insert, "$run_id", request.RunId);
            Add(insert, "$worker_instance_id", request.WorkerInstanceId);
            Add(insert, "$channel_instance_id", request.ChannelInstanceId);
            Add(insert, "$project_scope", request.ProjectScope);
            Add(insert, "$token_hash", request.TokenHash);
            Add(insert, "$expires_at_ms", request.ExpiresAtMs);
            Add(insert, "$created_at_ms", request.CreatedAtMs);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return new StoredRunSession(
            handleId,
            request.RunId,
            request.WorkerInstanceId,
            request.ChannelInstanceId,
            request.ProjectScope,
            request.TokenHash,
            request.ExpiresAtMs,
            null,
            request.CreatedAtMs);
    }

    public StoredRunSession? FindByTokenHash(string tokenHash)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT handle_id, run_id, worker_instance_id, channel_instance_id, project_scope,
                   token_hash, expires_at_ms, revoked_at_ms, created_at_ms
            FROM run_session_handles
            WHERE token_hash=$token_hash;
            """;
        Add(command, "$token_hash", tokenHash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    public int RevokeHandle(string handleId, long revokedAtMs) =>
        Revoke(
            "UPDATE run_session_handles SET revoked_at_ms=$revoked_at_ms WHERE handle_id=$value AND revoked_at_ms IS NULL;",
            handleId,
            revokedAtMs);

    public int RevokeByRun(string runId, long revokedAtMs) =>
        Revoke(
            "UPDATE run_session_handles SET revoked_at_ms=$revoked_at_ms WHERE run_id=$value AND revoked_at_ms IS NULL;",
            runId,
            revokedAtMs);

    public int RevokeByChannelWorker(string channelInstanceId, string workerInstanceId, long revokedAtMs)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_session_handles
            SET revoked_at_ms=$revoked_at_ms
            WHERE channel_instance_id=$channel_instance_id
              AND worker_instance_id=$worker_instance_id
              AND revoked_at_ms IS NULL;
            """;
        Add(command, "$revoked_at_ms", revokedAtMs);
        Add(command, "$channel_instance_id", channelInstanceId);
        Add(command, "$worker_instance_id", workerInstanceId);
        return command.ExecuteNonQuery();
    }

    private int Revoke(string sql, string value, long revokedAtMs)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$revoked_at_ms", revokedAtMs);
        Add(command, "$value", value);
        return command.ExecuteNonQuery();
    }

    private static StoredRunSession ReadSession(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.GetInt64(8));

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
