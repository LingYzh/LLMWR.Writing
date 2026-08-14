using System.Data.Common;
using LLMW.Writing.Application.Registry;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.Infrastructure.Projection;

public sealed class SqliteNarrativeSearchStore : INarrativeSearchStore
{
    private readonly string databasePath;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;

    public SqliteNarrativeSearchStore(
        string databasePath,
        SqliteDatabaseConnectionFactory? connectionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
    }

    public RegistryQueryResult<IReadOnlyList<NarrativeSearchHit>> Search(
        SearchNarrativeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            using var connection = connectionFactory.OpenConfigured(databasePath);
            if (HasDirtyEligibleDocument(connection))
            {
                return RegistryQueryResults.Fail<IReadOnlyList<NarrativeSearchHit>>(
                    RegistryQueryError.SearchIndexDirty,
                    "Authority-derived search documents do not match the current trusted Registry baseline.");
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT documents.object_id,documents.artifact_digest,documents.section_key,
                       documents.title,documents.body,documents.current_status,bm25(search_fts) AS rank
                FROM search_fts
                JOIN search_documents documents ON documents.search_rowid=search_fts.rowid
                JOIN objects
                  ON objects.object_id=documents.object_id AND objects.status='current'
                JOIN narrative_state_revisions current_state
                  ON current_state.scope_object_id=objects.object_id
                 AND current_state.snapshot_digest=documents.artifact_digest
                 AND NOT EXISTS(
                     SELECT 1 FROM narrative_state_revisions successor
                     WHERE successor.supersedes_state_revision_id=current_state.state_revision_id)
                JOIN object_paths paths
                  ON paths.object_id=documents.object_id AND paths.is_canonical=1
                JOIN registry_entries registry
                  ON registry.object_id=documents.object_id AND registry.path_id=paths.path_id
                WHERE search_fts MATCH $query
                  AND registry.registration_state='registered'
                  AND registry.retrieval_availability='available'
                  AND registry.reconcile_state='clean'
                  AND registry.trusted_physical_digest IS NOT NULL
                  AND registry.trusted_semantic_digest IS NOT NULL
                  AND paths.physical_digest=registry.trusted_physical_digest
                  AND paths.semantic_digest=registry.trusted_semantic_digest
                  AND documents.current_status='current'
                  AND lower(objects.object_type)<>'raw'
                  AND lower(registry.object_type)<>'raw'
                ORDER BY rank,documents.object_id,documents.section_key
                LIMIT $limit;
                """;
            Add(command, "$query", query.Text);
            Add(command, "$limit", query.Limit);
            List<NarrativeSearchHit> hits = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                hits.Add(new NarrativeSearchHit(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetDouble(6)));
            }

            return RegistryQueryResults.Success<IReadOnlyList<NarrativeSearchHit>>(hits);
        }
        catch (SqliteException exception) when (
            exception.Message.Contains("no such table: search_fts", StringComparison.OrdinalIgnoreCase))
        {
            return RegistryQueryResults.Fail<IReadOnlyList<NarrativeSearchHit>>(
                RegistryQueryError.SearchIndexUnavailable,
                exception.Message);
        }
        catch (SqliteException exception)
        {
            return RegistryQueryResults.Fail<IReadOnlyList<NarrativeSearchHit>>(
                RegistryQueryError.SearchQueryInvalid,
                exception.Message);
        }
        catch (DbException exception)
        {
            return RegistryQueryResults.Fail<IReadOnlyList<NarrativeSearchHit>>(
                RegistryQueryError.SearchIndexUnavailable,
                exception.Message);
        }
    }

    private static bool HasDirtyEligibleDocument(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM objects
                JOIN narrative_state_revisions state
                  ON state.scope_object_id=objects.object_id
                 AND NOT EXISTS(
                     SELECT 1 FROM narrative_state_revisions successor
                     WHERE successor.supersedes_state_revision_id=state.state_revision_id)
                JOIN object_paths paths
                  ON paths.object_id=objects.object_id AND paths.is_canonical=1
                JOIN registry_entries registry
                  ON registry.object_id=objects.object_id AND registry.path_id=paths.path_id
                WHERE objects.status='current'
                  AND lower(objects.object_type)<>'raw'
                  AND registry.registration_state='registered'
                  AND registry.retrieval_availability='available'
                  AND registry.reconcile_state='clean'
                  AND registry.trusted_physical_digest IS NOT NULL
                  AND registry.trusted_semantic_digest IS NOT NULL
                  AND paths.physical_digest=registry.trusted_physical_digest
                  AND paths.semantic_digest=registry.trusted_semantic_digest
                  AND (
                      NOT EXISTS(
                          SELECT 1 FROM search_documents documents
                          WHERE documents.object_id=objects.object_id
                            AND documents.artifact_digest=state.snapshot_digest
                            AND documents.current_status='current')
                      OR EXISTS(
                          SELECT 1 FROM search_documents documents
                          WHERE documents.object_id=objects.object_id
                            AND documents.current_status='current'
                            AND documents.artifact_digest<>state.snapshot_digest)));
            """;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
