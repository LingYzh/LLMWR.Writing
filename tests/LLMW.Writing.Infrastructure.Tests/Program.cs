using System.Data.Common;
using System.Security.Cryptography;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private const string Id1 = "018f3e78-1234-7abc-8def-0123456789a1";
    private const string Id2 = "018f3e78-1234-7abc-8def-0123456789a2";
    private const string Id3 = "018f3e78-1234-7abc-8def-0123456789a3";
    private const string Id4 = "018f3e78-1234-7abc-8def-0123456789a4";

    private static readonly List<string> PassedTests = [];
    private static DatabaseMigrationResult? verificationResult;

    private static int Main()
    {
        try
        {
            Run(nameof(CleanFileBackedDatabaseMigratesAndReopens), CleanFileBackedDatabaseMigratesAndReopens);
            Run(nameof(ConnectionConfigurationIsCentralizedAndDurable), ConnectionConfigurationIsCentralizedAndDurable);
            Run(nameof(ForeignKeysAndDurableRestrictAreEnforced), ForeignKeysAndDurableRestrictAreEnforced);
            Run(nameof(RuntimeChildrenCascadeOnlyWithinRuntimeFamily), RuntimeChildrenCascadeOnlyWithinRuntimeFamily);
            Run(nameof(ActiveAuthorityTransactionIsPartiallyUnique), ActiveAuthorityTransactionIsPartiallyUnique);
            Run(nameof(IdempotencyAndDeclaredCheckConstraintsAreEnforced), IdempotencyAndDeclaredCheckConstraintsAreEnforced);
            Run(nameof(EventSequenceAndCanonicalPathsAreUnique), EventSequenceAndCanonicalPathsAreUnique);
            Run(nameof(TombstonedObjectsCanBeExcludedWithoutDeletingHistory), TombstonedObjectsCanBeExcludedWithoutDeletingHistory);
            Run(nameof(TransactionRollbackLeavesNoPartialAuthorityRows), TransactionRollbackLeavesNoPartialAuthorityRows);
            Run(nameof(MigrationInterruptionRollsBackSchemaAndVersion), MigrationInterruptionRollsBackSchemaAndVersion);
            Run(nameof(MigrationChecksumMismatchIsRejected), MigrationChecksumMismatchIsRejected);
            Run(nameof(FutureVersionIsRejectedWithoutMutation), FutureVersionIsRejectedWithoutMutation);
            Run(nameof(FtsCanBeDroppedAndRebuiltWithoutAuthorityLoss), FtsCanBeDroppedAndRebuiltWithoutAuthorityLoss);
            RunWp03Tests();
            RunWp07ProjectionInfrastructureTests();
            RunWp08InfrastructureTests();
            RunWp09InfrastructureTests();
            RunWp11InfrastructureTests();
            RunWp12InfrastructureTests();
            RunWp13InfrastructureTests();
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("WP10 Windows sandbox tests cannot be skipped on a non-Windows runner.");
            }

            RunWp10InfrastructureTests();

            Console.WriteLine($"Infrastructure tests passed ({PassedTests.Count}).");
            foreach (var test in PassedTests)
            {
                Console.WriteLine($"PASS {test}");
            }

            var result = verificationResult
                ?? throw new InvalidOperationException("Database verification result was not captured.");
            Console.WriteLine(
                $"DATABASE sqlite={result.SqliteVersion} journal_mode={result.JournalMode} " +
                $"synchronous={result.Synchronous} foreign_keys={(result.ForeignKeysEnabled ? 1 : 0)} " +
                $"user_version={result.UserVersion} migrations={result.MigrationCount} " +
                $"tables={result.Tables.Count} indexes={result.Indexes.Count}");

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CleanFileBackedDatabaseMigratesAndReopens()
    {
        using var database = TemporaryDatabase.Create();
        var runner = new SqliteMigrationRunner();
        var first = runner.Migrate(database.Path, "wp02-tests", 1735689600000);
        verificationResult = first;

        AssertTrue(first.AppliedMigration, "A clean database must apply migration v1.");
        AssertEqual(1, first.UserVersion, "Migration must set user_version to 1.");
        AssertEqual(1, first.MigrationCount, "Exactly one migration record must exist.");
        AssertEqual("wal", first.JournalMode, "A file-backed project database must use WAL.");
        AssertEqual(2, first.Synchronous, "SQLite synchronous numeric value 2 is FULL.");
        AssertTrue(first.ForeignKeysEnabled, "foreign_keys must be enabled.");
        AssertTrue(first.Tables.Contains("search_fts", StringComparer.Ordinal), "FTS5 virtual table was not created.");

        var second = runner.Migrate(database.Path, "wp02-tests", 1735689600001);
        AssertFalse(second.AppliedMigration, "Reopen must not reapply migration v1.");
        AssertEqual(1, second.MigrationCount, "Reopen must preserve exactly one migration record.");

        using var connection = OpenConfigured(database.Path);
        AssertEqual(
            1735689600000L,
            Scalar<long>(connection, "SELECT applied_at_ms FROM schema_migrations;"),
            "Reopen unexpectedly replaced the migration record.");
        AssertEqual(
            "wp02-tests",
            Scalar<string>(connection, "SELECT app_version FROM schema_migrations;"),
            "Migration app-version provenance was not recorded.");
        AssertEqual(
            64L,
            Scalar<long>(connection, "SELECT length(checksum) FROM schema_migrations;"),
            "Migration checksum must be a SHA-256 hex digest.");
    }

    private static void ConnectionConfigurationIsCentralizedAndDurable()
    {
        using var database = MigratedDatabase.Create();
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(database.Path);

        AssertEqual("wal", Scalar<string>(connection, "PRAGMA journal_mode;"), "journal_mode must be WAL.");
        AssertEqual(2L, Scalar<long>(connection, "PRAGMA synchronous;"), "synchronous must be FULL.");
        AssertEqual(1L, Scalar<long>(connection, "PRAGMA foreign_keys;"), "foreign_keys must be ON.");
        AssertEqual(5000L, Scalar<long>(connection, "PRAGMA busy_timeout;"), "busy_timeout must be 5000ms.");
        AssertEqual("ok", Scalar<string>(connection, "PRAGMA integrity_check;"), "Database integrity failed.");
        AssertTrue(File.Exists(database.Path), "Database file was not persisted.");
    }

    private static void ForeignKeysAndDurableRestrictAreEnforced()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);

        AssertSqliteConstraint(
            connection,
            "INSERT INTO object_paths(path_id, object_id, relative_path, path_kind, is_canonical, updated_at_ms) " +
            $"VALUES ('path-1', '{Id1}', 'Narrative/missing.md', 'narrative', 1, 1);",
            "A missing object FK must be rejected.");

        InsertObject(connection, Id1);
        Execute(
            connection,
            "INSERT INTO storylines(storyline_id, workflow_state, updated_at_ms) " +
            $"VALUES ('{Id1}', 'draft', 1);");

        AssertSqliteConstraint(
            connection,
            $"DELETE FROM objects WHERE object_id = '{Id1}';",
            "Authority/Narrative ownership must use RESTRICT rather than destructive cascade.");
        AssertEqual(1L, Scalar<long>(connection, $"SELECT COUNT(*) FROM objects WHERE object_id = '{Id1}';"),
            "Restricted parent was unexpectedly deleted.");
    }

    private static void RuntimeChildrenCascadeOnlyWithinRuntimeFamily()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);

        Execute(
            connection,
            """
            INSERT INTO workflow_runs(workflow_run_id,status,created_at_ms,updated_at_ms)
            VALUES ('workflow-1','running',1,1);
            INSERT INTO runs(run_id,workflow_run_id,role,status,depth,created_at_ms,updated_at_ms)
            VALUES ('run-1','workflow-1','writer','running',0,1,1);
            INSERT INTO tasks(task_id,run_id,task_kind,status,created_at_ms,updated_at_ms)
            VALUES ('task-1','run-1','test','running',1,1);
            INSERT INTO attempts(attempt_id,task_id,attempt_no,status,started_at_ms)
            VALUES ('attempt-1','task-1',1,'running',1);
            DELETE FROM workflow_runs WHERE workflow_run_id='workflow-1';
            """);

        AssertEqual(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM runs;"), "Runtime run did not cascade.");
        AssertEqual(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM tasks;"), "Runtime task did not cascade.");
        AssertEqual(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM attempts;"), "Runtime attempt did not cascade.");
    }

    private static void ActiveAuthorityTransactionIsPartiallyUnique()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);

        InsertAuthorityTransaction(connection, "tx-1", "key-1", "submitting");
        AssertSqliteConstraint(
            connection,
            AuthorityTransactionSql("tx-2", "key-2", "reviewing"),
            "Only one active Authority transaction may exist.");

        Execute(connection, "UPDATE authority_transactions SET status='complete' WHERE transaction_id='tx-1';");
        InsertAuthorityTransaction(connection, "tx-2", "key-2", "reviewing");
        InsertAuthorityTransaction(connection, "tx-3", "key-3", "complete");
        AssertEqual(3L, Scalar<long>(connection, "SELECT COUNT(*) FROM authority_transactions;"),
            "Inactive transactions should not violate the partial unique index.");
    }

    private static void EventSequenceAndCanonicalPathsAreUnique()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);

        InsertObject(connection, Id1);
        Execute(
            connection,
            "INSERT INTO object_paths(path_id,object_id,relative_path,path_kind,is_canonical,updated_at_ms) " +
            $"VALUES ('path-1','{Id1}','Narrative/a.md','narrative',1,1);");
        AssertSqliteConstraint(
            connection,
            "INSERT INTO object_paths(path_id,object_id,relative_path,path_kind,is_canonical,updated_at_ms) " +
            $"VALUES ('path-2','{Id1}','Narrative/b.md','narrative',1,1);",
            "An object may have only one canonical path.");
        Execute(
            connection,
            "INSERT INTO object_paths(path_id,object_id,relative_path,path_kind,is_canonical,updated_at_ms) " +
            $"VALUES ('path-3','{Id1}','Narrative/b.md','narrative',0,1);");
        AssertSqliteConstraint(
            connection,
            "INSERT INTO object_paths(path_id,object_id,relative_path,path_kind,is_canonical,updated_at_ms) " +
            $"VALUES ('path-4','{Id1}','Narrative/b.md','narrative',0,1);",
            "Relative project paths must be globally unique.");

        InsertAuthorityTransaction(connection, "tx-1", "key-1", "complete");
        Execute(connection, AuthorityEventSql("event-1", 1, "tx-1"));
        AssertSqliteConstraint(
            connection,
            AuthorityEventSql("event-2", 1, "tx-1"),
            "Authority event sequence must be unique.");
        AssertSqliteConstraint(
            connection,
            "DELETE FROM authority_transactions WHERE transaction_id='tx-1';",
            "Durable Authority events must RESTRICT deletion of their transaction provenance.");
    }

    private static void IdempotencyAndDeclaredCheckConstraintsAreEnforced()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);

        InsertAuthorityTransaction(connection, "tx-1", "duplicate-key", "complete");
        AssertSqliteConstraint(
            connection,
            AuthorityTransactionSql("tx-2", "duplicate-key", "complete"),
            "Authority transaction idempotency keys must be unique.");

        AssertSqliteConstraint(
            connection,
            "INSERT INTO objects(object_id,object_type,schema_version,status,created_at_ms,updated_at_ms) " +
            "VALUES ('too-short','test',1,'current',1,1);",
            "Object identities must remain canonical TEXT(36).");
        AssertSqliteConstraint(
            connection,
            "INSERT INTO oversight_overrides(" +
            "override_id,scope_kind,narrative_authority,runtime_permission_mode,created_by,created_at_ms) " +
            "VALUES ('override-1','invalid','author_confirmed_required','ask','user',1);",
            "Declared stable-enum CHECK constraints must be enforced.");
    }

    private static void TombstonedObjectsCanBeExcludedWithoutDeletingHistory()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);

        InsertObject(connection, Id1);
        InsertObject(connection, Id2);
        Execute(connection, $"UPDATE objects SET status='removed',deleted_at_ms=10 WHERE object_id='{Id2}';");

        AssertEqual(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM objects WHERE deleted_at_ms IS NULL;"),
            "Current-state query must exclude tombstones.");
        AssertEqual(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM objects;"),
            "Tombstoned durable history must remain present.");
    }

    private static void TransactionRollbackLeavesNoPartialAuthorityRows()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);
        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AuthorityTransactionSql("tx-rollback", "key-rollback", "submitting");
            command.ExecuteNonQuery();
            transaction.Rollback();
        }

        AssertEqual(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM authority_transactions;"),
            "Rolled-back Authority rows must not persist.");
    }

    private static void MigrationInterruptionRollsBackSchemaAndVersion()
    {
        using var database = TemporaryDatabase.Create();
        var interruptedRunner = new SqliteMigrationRunner(
            faultInjector: checkpoint =>
            {
                if (checkpoint == SqliteMigrationCheckpoint.AfterMigrationRecord)
                {
                    throw new InjectedMigrationFailureException();
                }
            });
        AssertThrows<InjectedMigrationFailureException>(
            () => interruptedRunner.Migrate(database.Path, "wp02-tests"),
            "Injected migration failure was not observed.");

        using (var connection = OpenConfigured(database.Path))
        {
            AssertEqual(0L, Scalar<long>(connection, "PRAGMA user_version;"),
                "Interrupted migration must not advance user_version.");
            AssertEqual(
                0L,
                Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='schema_migrations';"),
                "Interrupted migration DDL must roll back.");
        }

        var result = new SqliteMigrationRunner().Migrate(database.Path, "wp02-tests");
        AssertTrue(result.AppliedMigration, "A database left at version zero must remain migratable.");
    }

    private static void MigrationChecksumMismatchIsRejected()
    {
        using var database = MigratedDatabase.Create();
        using (var connection = OpenConfigured(database.Path))
        {
            Execute(connection, "UPDATE schema_migrations SET checksum='tampered';");
        }

        AssertThrows<MigrationIntegrityException>(
            () => new SqliteMigrationRunner().Migrate(database.Path, "wp02-tests"),
            "A migration checksum mismatch must be rejected.");
    }

    private static void FutureVersionIsRejectedWithoutMutation()
    {
        using var database = TemporaryDatabase.Create();
        using (var connection = new SqliteConnection($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            Execute(
                connection,
                "PRAGMA journal_mode=WAL; PRAGMA user_version=2; CREATE TABLE future_marker(value TEXT) STRICT;");
        }

        var before = Snapshot(database.Path);
        AssertThrows<FutureDatabaseVersionException>(
            () => new SqliteMigrationRunner().Migrate(database.Path, "wp02-tests"),
            "A future-version database must be refused.");
        var after = Snapshot(database.Path);

        AssertEqual(before.Length, after.Length, "Future-version refusal changed database length.");
        AssertTrue(before.AsSpan().SequenceEqual(after), "Future-version refusal mutated database bytes.");
        AssertFalse(File.Exists(database.Path + "-wal"), "Future-version preflight unexpectedly created a WAL file.");
        AssertFalse(File.Exists(database.Path + "-shm"), "Future-version preflight unexpectedly created an SHM file.");
    }

    private static void FtsCanBeDroppedAndRebuiltWithoutAuthorityLoss()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);

        InsertObject(connection, Id1);
        Execute(
            connection,
            "INSERT INTO search_documents(search_rowid,object_id,artifact_digest,section_key,title,body,current_status) " +
            $"VALUES (1,'{Id1}','digest-1','section-1','Title','Body','current');");
        Execute(connection, "INSERT INTO search_fts(rowid,title,body) SELECT search_rowid,title,body FROM search_documents;");
        AssertEqual(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM search_fts WHERE search_fts MATCH 'Body';"),
            "FTS content was not searchable.");

        Execute(connection, "DROP TABLE search_fts;");
        AssertEqual(1L, Scalar<long>(connection, $"SELECT COUNT(*) FROM objects WHERE object_id='{Id1}';"),
            "Dropping derived FTS state must not delete Authority/Narrative state.");
        AssertEqual(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM search_documents;"),
            "Dropping FTS must not delete its rebuildable external-content source.");

        Execute(
            connection,
            """
            CREATE VIRTUAL TABLE search_fts USING fts5(
              title, body,
              content='search_documents',
              content_rowid='search_rowid',
              tokenize='unicode61'
            );
            INSERT INTO search_fts(search_fts) VALUES('rebuild');
            """);
        AssertEqual(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM search_fts WHERE search_fts MATCH 'Body';"),
            "Rebuilt FTS did not restore searchable derived state.");
    }

    private static void InsertObject(DbConnection connection, string objectId)
    {
        Execute(
            connection,
            "INSERT INTO objects(object_id,object_type,schema_version,status,created_at_ms,updated_at_ms) " +
            $"VALUES ('{objectId}','test',1,'current',1,1);");
    }

    private static void InsertAuthorityTransaction(
        DbConnection connection,
        string transactionId,
        string idempotencyKey,
        string status)
    {
        Execute(connection, AuthorityTransactionSql(transactionId, idempotencyKey, status));
    }

    private static string AuthorityTransactionSql(string transactionId, string idempotencyKey, string status)
    {
        return
            "INSERT INTO authority_transactions(" +
            "transaction_id,transaction_kind,idempotency_key,project_submission_state,status,recovery_state,started_at_ms) " +
            $"VALUES ('{transactionId}','test','{idempotencyKey}','idle','{status}','none',1);";
    }

    private static string AuthorityEventSql(string eventId, int eventSequence, string transactionId)
    {
        return
            "INSERT INTO authority_events(" +
            "event_id,event_seq,transaction_id,aggregate_type,aggregate_id,event_type,event_payload_json,created_at_ms) " +
            $"VALUES ('{eventId}',{eventSequence},'{transactionId}','test','{Id1}','test','{{}}',1);";
    }

    private static DbConnection OpenConfigured(string path)
    {
        return new SqliteDatabaseConnectionFactory().OpenConfigured(path);
    }

    private static void Execute(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertSqliteConstraint(DbConnection connection, string sql, string message)
    {
        try
        {
            Execute(connection, sql);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static byte[] Snapshot(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream).Concat(BitConverter.GetBytes(stream.Length)).ToArray();
    }

    private static void Run(string name, Action test)
    {
        test();
        PassedTests.Add(name);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertFalse(bool condition, string message) => AssertTrue(!condition, message);

    private static void AssertEqual<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private class TemporaryDatabase : IDisposable
    {
        protected TemporaryDatabase(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }

        public string Path { get; }

        public static TemporaryDatabase Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "LLMW.Writing.WP02",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            return new TemporaryDatabase(directory, System.IO.Path.Combine(directory, "project.db"));
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }

    private sealed class MigratedDatabase : TemporaryDatabase
    {
        private MigratedDatabase(string directory, string path)
            : base(directory, path)
        {
        }

        public static new MigratedDatabase Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "LLMW.Writing.WP02",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var database = new MigratedDatabase(directory, System.IO.Path.Combine(directory, "project.db"));
            new SqliteMigrationRunner().Migrate(database.Path, "wp02-tests", 1735689600000);
            return database;
        }
    }

    private sealed class InjectedMigrationFailureException : Exception
    {
    }
}
