using System.Data;
using System.Data.Common;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.Infrastructure.Persistence.Sqlite;

public sealed class SqliteMigrationRunner
{
    public const int CurrentSchemaVersion = 1;

    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly Action<SqliteMigrationCheckpoint>? faultInjector;

    public SqliteMigrationRunner(
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        Action<SqliteMigrationCheckpoint>? faultInjector = null)
    {
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.faultInjector = faultInjector;
    }

    public DatabaseMigrationResult Migrate(
        string databasePath,
        string appVersion,
        long? appliedAtMilliseconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);

        var fullPath = Path.GetFullPath(databasePath);
        var initialVersion = InspectExistingDatabaseWithoutMutation(fullPath);
        if (initialVersion > CurrentSchemaVersion)
        {
            throw new FutureDatabaseVersionException(initialVersion, CurrentSchemaVersion);
        }

        using var connection = connectionFactory.OpenConfigured(fullPath);
        var version = ExecuteScalarInt64(connection, "PRAGMA user_version;");
        var applied = false;

        if (version == 0)
        {
            ApplyVersion1(
                connection,
                appVersion,
                appliedAtMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            applied = true;
        }
        else if (version != CurrentSchemaVersion)
        {
            throw new MigrationIntegrityException($"Unsupported database user_version {version}.");
        }

        return Verify(connection, applied);
    }

    public static DatabaseMigrationResult ValidateExistingV1WithoutMutation(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new MigrationIntegrityException("Existing project.db is missing.");
        }

        var version = InspectExistingDatabaseWithoutMutation(fullPath);
        if (version != CurrentSchemaVersion)
        {
            throw new MigrationIntegrityException(
                $"Existing project.db user_version {version} is not schema v1; refused without mutation.");
        }

        using var connection = SqliteDatabaseConnectionFactory.OpenReadOnly(fullPath);
        return Verify(connection, applied: false);
    }

    private static int InspectExistingDatabaseWithoutMutation(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        var headerVersion = ReadHeaderUserVersion(databasePath);
        if (headerVersion > CurrentSchemaVersion)
        {
            throw new FutureDatabaseVersionException(headerVersion, CurrentSchemaVersion);
        }

        using var connection = SqliteDatabaseConnectionFactory.OpenReadOnly(databasePath);
        var version = checked((int)ExecuteScalarInt64(connection, "PRAGMA user_version;"));
        if (version == CurrentSchemaVersion)
        {
            VerifyMigrationRecord(connection);
        }

        return version;
    }

    private static int ReadHeaderUserVersion(string databasePath)
    {
        const int headerLength = 100;
        const int userVersionOffset = 60;
        ReadOnlySpan<byte> expectedMagic = "SQLite format 3\0"u8;
        Span<byte> header = stackalloc byte[headerLength];

        using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: headerLength,
            FileOptions.SequentialScan);
        if (stream.Length == 0)
        {
            return 0;
        }

        if (stream.Length < headerLength)
        {
            throw new MigrationIntegrityException("Existing database is shorter than the SQLite header.");
        }

        stream.ReadExactly(header);
        if (!header[..expectedMagic.Length].SequenceEqual(expectedMagic))
        {
            var observed = Encoding.ASCII.GetString(header[..expectedMagic.Length]);
            throw new MigrationIntegrityException($"Existing database has an invalid SQLite header: '{observed}'.");
        }

        return BinaryPrimitives.ReadInt32BigEndian(header.Slice(userVersionOffset, sizeof(int)));
    }

    private void ApplyVersion1(DbConnection connection, string appVersion, long appliedAtMilliseconds)
    {
        var migration = SqliteMigrationCatalog.Version1;
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            faultInjector?.Invoke(SqliteMigrationCheckpoint.BeforeSchema);
            using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.Transaction = transaction;
                schemaCommand.CommandText = migration.TransactionalSql;
                schemaCommand.ExecuteNonQuery();
            }

            faultInjector?.Invoke(SqliteMigrationCheckpoint.AfterSchema);

            using (var recordCommand = connection.CreateCommand())
            {
                recordCommand.Transaction = transaction;
                recordCommand.CommandText =
                    """
                    INSERT INTO schema_migrations(migration_id, applied_at_ms, app_version, checksum)
                    VALUES ($migration_id, $applied_at_ms, $app_version, $checksum);
                    """;
                AddParameter(recordCommand, "$migration_id", migration.MigrationId);
                AddParameter(recordCommand, "$applied_at_ms", appliedAtMilliseconds);
                AddParameter(recordCommand, "$app_version", appVersion);
                AddParameter(recordCommand, "$checksum", migration.Checksum);
                recordCommand.ExecuteNonQuery();
            }

            faultInjector?.Invoke(SqliteMigrationCheckpoint.AfterMigrationRecord);

            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = $"PRAGMA user_version = {migration.Version};";
                versionCommand.ExecuteNonQuery();
            }

            faultInjector?.Invoke(SqliteMigrationCheckpoint.AfterUserVersion);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static DatabaseMigrationResult Verify(DbConnection connection, bool applied)
    {
        var userVersion = checked((int)ExecuteScalarInt64(connection, "PRAGMA user_version;"));
        if (userVersion != CurrentSchemaVersion)
        {
            throw new MigrationIntegrityException(
                $"Migration verification expected user_version {CurrentSchemaVersion}, found {userVersion}.");
        }

        var migrationCount = VerifyMigrationRecord(connection);
        var integrity = ExecuteScalarString(connection, "PRAGMA integrity_check;");
        if (!StringComparer.Ordinal.Equals(integrity, "ok"))
        {
            throw new MigrationIntegrityException($"SQLite integrity_check failed: {integrity}.");
        }

        using (var foreignKeyCheck = connection.CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeyCheck.ExecuteReader();
            if (reader.Read())
            {
                throw new MigrationIntegrityException("SQLite foreign_key_check found an invalid durable row.");
            }
        }

        var tables = ReadSchemaNames(connection, "table");
        var indexes = ReadSchemaNames(connection, "index");
        VerifyRequiredSchema(tables, indexes);

        return new DatabaseMigrationResult(
            applied,
            ExecuteScalarString(connection, "SELECT sqlite_version();"),
            ExecuteScalarString(connection, "PRAGMA journal_mode;"),
            checked((int)ExecuteScalarInt64(connection, "PRAGMA synchronous;")),
            ExecuteScalarInt64(connection, "PRAGMA foreign_keys;") == 1,
            userVersion,
            migrationCount,
            tables,
            indexes);
    }

    private static int VerifyMigrationRecord(DbConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT migration_id, checksum FROM schema_migrations ORDER BY migration_id;";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new MigrationIntegrityException("schema_migrations does not contain migration v1.");
            }

            var migrationId = reader.GetString(0);
            var checksum = reader.GetString(1);
            var expected = SqliteMigrationCatalog.Version1;
            if (!StringComparer.Ordinal.Equals(migrationId, expected.MigrationId) ||
                !StringComparer.Ordinal.Equals(checksum, expected.Checksum))
            {
                throw new MigrationIntegrityException(
                    $"Migration v1 checksum mismatch for '{migrationId}'.");
            }

            if (reader.Read())
            {
                throw new MigrationIntegrityException("Unexpected additional migration records exist.");
            }

            return 1;
        }
        catch (SqliteException exception)
        {
            throw new MigrationIntegrityException(
                $"Unable to verify schema_migrations: {exception.Message}");
        }
    }

    private static List<string> ReadSchemaNames(DbConnection connection, string type)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_schema WHERE type = $type ORDER BY name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$type";
        parameter.Value = type;
        command.Parameters.Add(parameter);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void VerifyRequiredSchema(
        IReadOnlyCollection<string> tables,
        IReadOnlyCollection<string> indexes)
    {
        string[] requiredTables =
        [
            "schema_migrations", "objects", "object_paths", "registry_entries", "storylines", "arcs",
            "chapters", "workflow_runs", "runs", "tasks", "attempts", "result_artifacts",
            "result_dependencies", "checkpoints", "approvals", "tool_calls", "evidence",
            "background_tasks", "specialist_profiles", "run_session_handles", "oversight_overrides",
            "delegated_decisions", "candidates", "review_attempts", "manuscript_revisions",
            "accepted_snapshots", "acceptance_records", "revision_barriers", "authority_transactions",
            "authority_events", "narrative_change_sets", "narrative_changes", "impact_analyses",
            "dependency_edges", "narrative_state_revisions", "history_entries", "snapshot_blob_leases",
            "authority_provenance_stubs", "search_documents", "search_fts"
        ];
        string[] requiredIndexes =
        [
            "ux_object_one_canonical_path", "ux_specialist_profile_name",
            "ux_single_active_authority_transaction", "ix_dependency_from", "ix_dependency_to",
            "ix_history_path_time"
        ];

        var missingTables = requiredTables.Except(tables, StringComparer.Ordinal).ToArray();
        var missingIndexes = requiredIndexes.Except(indexes, StringComparer.Ordinal).ToArray();
        if (missingTables.Length != 0 || missingIndexes.Length != 0)
        {
            throw new MigrationIntegrityException(
                $"Schema verification failed. Missing tables: [{string.Join(", ", missingTables)}]; " +
                $"missing indexes: [{string.Join(", ", missingIndexes)}].");
        }
    }

    private static long ExecuteScalarInt64(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ExecuteScalarString(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed record DatabaseMigrationResult(
    bool AppliedMigration,
    string SqliteVersion,
    string JournalMode,
    int Synchronous,
    bool ForeignKeysEnabled,
    int UserVersion,
    int MigrationCount,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Indexes);

public enum SqliteMigrationCheckpoint
{
    BeforeSchema,
    AfterSchema,
    AfterMigrationRecord,
    AfterUserVersion
}
