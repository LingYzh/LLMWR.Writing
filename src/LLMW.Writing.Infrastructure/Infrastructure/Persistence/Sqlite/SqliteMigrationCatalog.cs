using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace LLMW.Writing.Infrastructure.Persistence.Sqlite;

internal static class SqliteMigrationCatalog
{
    private const string ResourceName =
        "LLMW.Writing.Infrastructure.Persistence.Sqlite.Migrations.0001_initial.sql";

    public static SqliteMigration Version1 { get; } = LoadVersion1();

    private static SqliteMigration LoadVersion1()
    {
        var assembly = typeof(SqliteMigrationCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sql = Normalize(reader.ReadToEnd());
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
        const string ddlMarker = "CREATE TABLE schema_migrations";
        var ddlStart = sql.IndexOf(ddlMarker, StringComparison.Ordinal);
        if (ddlStart < 0)
        {
            throw new InvalidOperationException("Migration v1 does not contain its schema_migrations DDL marker.");
        }

        return new SqliteMigration(1, "0001_initial", checksum, sql, sql[ddlStart..]);
    }

    private static string Normalize(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd() + "\n";
    }
}

internal sealed record SqliteMigration(
    int Version,
    string MigrationId,
    string Checksum,
    string Sql,
    string TransactionalSql);
