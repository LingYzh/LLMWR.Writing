namespace LLMW.Writing.Infrastructure.Persistence.Sqlite;

public class SqliteDatabaseException : Exception
{
    public SqliteDatabaseException(string message)
        : base(message)
    {
    }

    public SqliteDatabaseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FutureDatabaseVersionException : SqliteDatabaseException
{
    public FutureDatabaseVersionException(int foundVersion, int supportedVersion)
        : base($"Database user_version {foundVersion} is newer than supported version {supportedVersion}; the database was not modified.")
    {
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    public int FoundVersion { get; }

    public int SupportedVersion { get; }
}

public sealed class MigrationIntegrityException : SqliteDatabaseException
{
    public MigrationIntegrityException(string message)
        : base(message)
    {
    }
}
