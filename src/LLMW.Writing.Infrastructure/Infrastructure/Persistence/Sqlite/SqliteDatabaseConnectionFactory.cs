using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.Infrastructure.Persistence.Sqlite;

/// <summary>
/// The single initialization point for project-database connections.
/// </summary>
public sealed class SqliteDatabaseConnectionFactory
{
    public const int BusyTimeoutMilliseconds = 5000;

    private readonly int busyTimeoutMilliseconds;

    public SqliteDatabaseConnectionFactory(int busyTimeoutMilliseconds = BusyTimeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(busyTimeoutMilliseconds);
        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public DbConnection OpenConfigured(string databasePath)
    {
        var connection = Open(databasePath, SqliteOpenMode.ReadWriteCreate);
        try
        {
            ExecuteNonQuery(
                connection,
                $"""
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA busy_timeout = {busyTimeoutMilliseconds};
                """);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static SqliteConnection OpenReadOnly(string databasePath)
    {
        return Open(databasePath, SqliteOpenMode.ReadOnly);
    }

    private static SqliteConnection Open(string databasePath, SqliteOpenMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        if (mode == SqliteOpenMode.ReadWriteCreate)
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = mode,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}
