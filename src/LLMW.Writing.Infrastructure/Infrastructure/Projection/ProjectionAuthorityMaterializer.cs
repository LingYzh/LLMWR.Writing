using System.Data.Common;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Projection;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Projection;

public sealed class ProjectionAuthorityMaterializer : IAuthorityMaterializer
{
    private readonly string databasePath;
    private readonly IAuthorityMaterializer inner;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly NarrativeProjectionPlanner planner;
    private readonly SqliteNarrativeSearchIndex searchIndex;
    private readonly Func<long> clock;

    public ProjectionAuthorityMaterializer(
        string databasePath,
        ImmutableBlobStore blobStore,
        IAuthorityMaterializer inner,
        SqliteNarrativeSearchIndex? searchIndex = null,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        planner = new NarrativeProjectionPlanner(this.databasePath, blobStore, this.connectionFactory);
        this.searchIndex = searchIndex ?? new SqliteNarrativeSearchIndex(this.databasePath, blobStore, this.connectionFactory);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public Exception? LastSearchIndexFailure { get; private set; }

    public void Materialize(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default) =>
        inner.Materialize(transactionId, plans, cancellationToken);

    public void Verify(
        string transactionId,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken = default)
    {
        inner.Verify(transactionId, plans, cancellationToken);
        var metadata = ReadRecoveryMetadata(transactionId);
        FinalizeVerifiedProjection(metadata, cancellationToken);
    }

    internal void VerifyRebuild(
        string transactionId,
        ProjectionBuild build,
        ProjectionRecoveryMetadata metadata,
        IReadOnlyList<AuthorityMaterializationPlan> plans,
        CancellationToken cancellationToken)
    {
        inner.Verify(transactionId, plans, cancellationToken);
        ValidateSemanticDigests(build, metadata);
        UpdateTrustedRegistry(metadata, cancellationToken);
        TryRebuildSearch(cancellationToken);
    }

    private void FinalizeVerifiedProjection(
        ProjectionRecoveryMetadata metadata,
        CancellationToken cancellationToken)
    {
        var current = planner.BuildCurrent(cancellationToken);
        ValidateSemanticDigests(current.Build, metadata);
        UpdateTrustedRegistry(metadata, cancellationToken);
        TryRebuildSearch(cancellationToken);
    }

    private static void ValidateSemanticDigests(ProjectionBuild build, ProjectionRecoveryMetadata metadata)
    {
        var artifacts = build.Artifacts
            .Where(value => value.Kind == ProjectionArtifactKind.NarrativeMarkdown)
            .ToDictionary(value => value.ObjectId!, StringComparer.Ordinal);
        foreach (var registration in metadata.Registrations)
        {
            if (!artifacts.TryGetValue(registration.ObjectId, out var artifact) ||
                !StringComparer.Ordinal.Equals(artifact.TargetRelativePath, registration.RelativePath) ||
                !StringComparer.Ordinal.Equals(artifact.PhysicalDigest, registration.PhysicalDigest) ||
                !StringComparer.Ordinal.Equals(artifact.SemanticDigest, registration.SemanticDigest))
            {
                throw new InvalidOperationException(
                    $"Projection verification failed for Narrative Object '{registration.ObjectId}'.");
            }
        }
    }

    private void UpdateTrustedRegistry(
        ProjectionRecoveryMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        try
        {
            var now = clock();
            foreach (var registration in metadata.Registrations.OrderBy(value => value.ObjectId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO object_paths(
                        path_id,object_id,relative_path,path_kind,is_canonical,
                        physical_digest,semantic_digest,updated_at_ms)
                    VALUES($path_id,$object_id,$relative_path,'narrative_projection',1,
                           $physical_digest,$semantic_digest,$now)
                    ON CONFLICT(path_id) DO UPDATE SET
                        object_id=excluded.object_id,
                        relative_path=excluded.relative_path,
                        path_kind=excluded.path_kind,
                        is_canonical=excluded.is_canonical,
                        physical_digest=excluded.physical_digest,
                        semantic_digest=excluded.semantic_digest,
                        updated_at_ms=excluded.updated_at_ms;
                    """,
                    ("$path_id", registration.PathId),
                    ("$object_id", registration.ObjectId),
                    ("$relative_path", registration.RelativePath),
                    ("$physical_digest", registration.PhysicalDigest),
                    ("$semantic_digest", registration.SemanticDigest),
                    ("$now", now));
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO registry_entries(
                        registry_entry_id,object_id,path_id,object_type,schema_version,
                        registration_state,retrieval_availability,trusted_physical_digest,
                        trusted_semantic_digest,reconcile_state,registered_at_ms,last_verified_at_ms,updated_at_ms)
                    VALUES($registry_entry_id,$object_id,$path_id,$object_type,$schema_version,
                           'registered','available',$physical_digest,$semantic_digest,'clean',$now,$now,$now)
                    ON CONFLICT(registry_entry_id) DO UPDATE SET
                        object_id=excluded.object_id,
                        path_id=excluded.path_id,
                        object_type=excluded.object_type,
                        schema_version=excluded.schema_version,
                        registration_state='registered',
                        retrieval_availability='available',
                        trusted_physical_digest=excluded.trusted_physical_digest,
                        trusted_semantic_digest=excluded.trusted_semantic_digest,
                        reconcile_state='clean',
                        registered_at_ms=COALESCE(registry_entries.registered_at_ms,excluded.registered_at_ms),
                        last_verified_at_ms=excluded.last_verified_at_ms,
                        updated_at_ms=excluded.updated_at_ms;
                    """,
                    ("$registry_entry_id", registration.RegistryEntryId),
                    ("$object_id", registration.ObjectId),
                    ("$path_id", registration.PathId),
                    ("$object_type", registration.ObjectType),
                    ("$schema_version", registration.SchemaVersion),
                    ("$physical_digest", registration.PhysicalDigest),
                    ("$semantic_digest", registration.SemanticDigest),
                    ("$now", now));
            }

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

    private ProjectionRecoveryMetadata ReadRecoveryMetadata(string transactionId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_payload_json
            FROM authority_events
            WHERE transaction_id=$transaction_id AND event_type=$event_type
            ORDER BY event_seq DESC
            LIMIT 1;
            """;
        Add(command, "$transaction_id", transactionId);
        Add(command, "$event_type", ProjectionMetadataCodec.EventType);
        var json = command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("Committed projection recovery metadata is missing.");
        return ProjectionMetadataCodec.Deserialize(json);
    }

    private void TryRebuildSearch(CancellationToken cancellationToken)
    {
        try
        {
            searchIndex.Rebuild(cancellationToken);
            LastSearchIndexFailure = null;
        }
        catch (Exception exception)
        {
            LastSearchIndexFailure = exception;
        }
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
            Add(command, name, value);
        }

        return command.ExecuteNonQuery();
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
