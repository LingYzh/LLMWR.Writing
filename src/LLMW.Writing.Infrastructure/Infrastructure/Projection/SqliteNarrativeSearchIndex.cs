using System.Data.Common;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Projection;

public enum SearchIndexFaultPoint
{
    BeforeRebuild,
    AfterDocumentsWritten,
    BeforeFtsRebuild
}

public interface ISearchIndexFaultInjector
{
    void Inject(SearchIndexFaultPoint point);
}

public sealed class NoOpSearchIndexFaultInjector : ISearchIndexFaultInjector
{
    public static NoOpSearchIndexFaultInjector Instance { get; } = new();

    private NoOpSearchIndexFaultInjector()
    {
    }

    public void Inject(SearchIndexFaultPoint point)
    {
    }
}

public sealed class SqliteNarrativeSearchIndex
{
    public const string BaselineTokenizerProfile = "unicode61";

    private readonly string databasePath;
    private readonly ImmutableBlobStore blobStore;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly ISearchIndexFaultInjector faultInjector;

    public SqliteNarrativeSearchIndex(
        string databasePath,
        ImmutableBlobStore blobStore,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        ISearchIndexFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.faultInjector = faultInjector ?? NoOpSearchIndexFaultInjector.Instance;
    }

    public void Rebuild(CancellationToken cancellationToken = default)
    {
        faultInjector.Inject(SearchIndexFaultPoint.BeforeRebuild);
        var documents = ReadAuthorityDocuments(cancellationToken);
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        try
        {
            Execute(connection, transaction, "DELETE FROM search_documents;");
            long rowId = 1;
            foreach (var document in documents
                         .OrderBy(value => value.ObjectId, StringComparer.Ordinal)
                         .ThenBy(value => value.ArtifactDigest, StringComparer.Ordinal)
                         .ThenBy(value => value.SectionKey, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO search_documents(
                        search_rowid,object_id,artifact_digest,section_key,title,body,current_status)
                    VALUES($rowid,$object_id,$artifact_digest,$section_key,$title,$body,$current_status);
                    """,
                    ("$rowid", rowId++),
                    ("$object_id", document.ObjectId),
                    ("$artifact_digest", document.ArtifactDigest),
                    ("$section_key", document.SectionKey),
                    ("$title", document.Title),
                    ("$body", document.Body),
                    ("$current_status", document.CurrentStatus));
            }

            faultInjector.Inject(SearchIndexFaultPoint.AfterDocumentsWritten);
            faultInjector.Inject(SearchIndexFaultPoint.BeforeFtsRebuild);
            Execute(connection, transaction, "INSERT INTO search_fts(search_fts) VALUES('rebuild');");
            transaction.Commit();
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

    private List<NarrativeSearchDocument> ReadAuthorityDocuments(CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT objects.object_id,objects.object_type,state.snapshot_digest,objects.status
            FROM objects
            JOIN narrative_state_revisions state
              ON state.scope_object_id=objects.object_id
             AND NOT EXISTS(
                 SELECT 1 FROM narrative_state_revisions successor
                 WHERE successor.supersedes_state_revision_id=state.state_revision_id)
            WHERE objects.status='current' AND lower(objects.object_type)<>'raw'
            ORDER BY objects.object_id;
            """;
        List<NarrativeSearchDocument> documents = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectId = reader.GetString(0);
            var objectType = reader.GetString(1);
            var digest = reader.GetString(2);
            using var source = blobStore.OpenRead(digest);
            var body = ProjectionCanonicalization.DecodeBody(source);
            documents.AddRange(DeterministicNarrativeChunker.Chunk(
                objectId,
                objectType,
                digest,
                body,
                reader.GetString(3)));
        }

        return documents;
    }

    private static int Execute(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command.ExecuteNonQuery();
    }
}
