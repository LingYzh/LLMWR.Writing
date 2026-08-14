using System.Data.Common;
using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Application.Projection;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Projection;

internal sealed class NarrativeProjectionPlanner
{
    private readonly string databasePath;
    private readonly ImmutableBlobStore blobStore;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly DeterministicProjectionSerializer serializer;

    public NarrativeProjectionPlanner(
        string databasePath,
        ImmutableBlobStore blobStore,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        DeterministicProjectionSerializer? serializer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.serializer = serializer ?? new DeterministicProjectionSerializer();
    }

    public PreparedNarrativeProjection Prepare(
        NarrativeChangeSetSnapshot changeSet,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        Dictionary<string, string> stateRevisionIds = new(StringComparer.Ordinal);
        foreach (var change in changeSet.Changes.Where(RequiresNewStateRevision))
        {
            stateRevisionIds.Add(change.ObjectId, DurableUuidV7.Create().ToString());
        }

        var build = Build(changeSet, stateRevisionIds, cancellationToken);
        var metadata = CreateMetadata(build);
        var plans = Stage(build, cancellationToken);
        return new PreparedNarrativeProjection(
            build,
            plans,
            ProjectionMetadataCodec.CreateEvent(transactionId, metadata),
            metadata,
            stateRevisionIds);
    }

    public (ProjectionBuild Build, ProjectionRecoveryMetadata Metadata) BuildCurrent(
        CancellationToken cancellationToken = default)
    {
        var build = Build(changeSet: null, new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken);
        return (build, CreateMetadata(build));
    }

    public IReadOnlyList<AuthorityMaterializationPlan> Stage(
        ProjectionBuild build,
        CancellationToken cancellationToken = default)
    {
        List<AuthorityMaterializationPlan> plans = [];
        foreach (var artifact in build.Artifacts.OrderBy(value => value.TargetRelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new MemoryStream(artifact.Bytes, writable: false);
            var staged = blobStore.Stage(source, artifact.PhysicalDigest, cancellationToken);
            plans.Add(new AuthorityMaterializationPlan(artifact.TargetRelativePath, staged.Digest));
        }

        return plans;
    }

    private ProjectionBuild Build(
        NarrativeChangeSetSnapshot? changeSet,
        IReadOnlyDictionary<string, string> stateRevisionIds,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        var objects = ReadObjects(connection);
        if (changeSet is not null)
        {
            OverlayChanges(objects, changeSet, stateRevisionIds);
        }

        List<ProjectionNarrativeObject> narrativeObjects = [];
        foreach (var row in objects.Values
                     .Where(value => (value.Status is "current" or "removed") && value.StateRevisionId is not null)
                     .OrderBy(value => value.ObjectId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = StringComparer.Ordinal.Equals(row.Status, "removed")
                ? string.Empty
                : ReadBody(row.ArtifactDigest, cancellationToken);
            narrativeObjects.Add(new ProjectionNarrativeObject(
                row.ObjectId,
                row.ObjectType,
                row.SchemaVersion,
                row.Revision,
                row.Status,
                row.StateRevisionId,
                row.ArtifactDigest,
                body,
                ProjectionPathPolicy.NarrativeObjectPath(row.ObjectType, row.ObjectId)));
        }

        var dependencies = ReadDependencies(connection, changeSet?.ImpactAnalysisId);
        var emptySnapshot = new ProjectionSnapshot(narrativeObjects, dependencies, []);
        var objectArtifacts = narrativeObjects
            .Select(serializer.SerializeNarrativeMarkdown)
            .OrderBy(value => value.TargetRelativePath, StringComparer.Ordinal)
            .ToArray();
        var registryEntries = BuildRegistryEntries(connection, narrativeObjects, objectArtifacts);
        var snapshot = emptySnapshot with { RegistryEntries = registryEntries };
        List<ProjectionArtifact> artifacts = [.. objectArtifacts];
        artifacts.Add(serializer.SerializeNarrativeState(snapshot));
        artifacts.Add(serializer.SerializeDependencies(snapshot));
        artifacts.Add(serializer.SerializeRegistry(snapshot));
        return new ProjectionBuild(artifacts.OrderBy(value => value.TargetRelativePath, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, ObjectProjectionRow> ReadObjects(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT o.object_id,o.object_type,o.schema_version,o.revision_no,o.status,
                   state.state_revision_id,state.snapshot_digest
            FROM objects o
            LEFT JOIN narrative_state_revisions state
              ON state.scope_object_id=o.object_id
             AND NOT EXISTS(
                 SELECT 1 FROM narrative_state_revisions successor
                 WHERE successor.supersedes_state_revision_id=state.state_revision_id)
            ORDER BY o.object_id;
            """;
        Dictionary<string, ObjectProjectionRow> rows = new(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0), new ObjectProjectionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows;
    }

    private static void OverlayChanges(
        IDictionary<string, ObjectProjectionRow> objects,
        NarrativeChangeSetSnapshot changeSet,
        IReadOnlyDictionary<string, string> stateRevisionIds)
    {
        foreach (var change in changeSet.Changes.OrderBy(value => value.Ordinal))
        {
            if (!objects.TryGetValue(change.ObjectId, out var current))
            {
                throw new InvalidOperationException($"Projection source object '{change.ObjectId}' is missing.");
            }

            objects[change.ObjectId] = change.ChangeKind switch
            {
                NarrativeChangeKind.Add => current with
                {
                    Revision = 1,
                    Status = "current",
                    StateRevisionId = stateRevisionIds[change.ObjectId],
                    ArtifactDigest = change.AfterPayloadDigest
                },
                NarrativeChangeKind.Modify => current with
                {
                    Revision = current.Revision + 1,
                    Status = "current",
                    StateRevisionId = stateRevisionIds[change.ObjectId],
                    ArtifactDigest = change.AfterPayloadDigest
                },
                NarrativeChangeKind.Remove => current with
                {
                    Revision = current.Revision + 1,
                    Status = "removed"
                },
                NarrativeChangeKind.Reintroduce => current with
                {
                    Revision = current.Revision + 1,
                    Status = "current",
                    StateRevisionId = stateRevisionIds[change.ObjectId],
                    ArtifactDigest = change.AfterPayloadDigest
                },
                _ => throw new ArgumentOutOfRangeException(nameof(changeSet))
            };
        }
    }

    private string ReadBody(string? digest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            throw new InvalidOperationException("A current Narrative Object has no Authority snapshot digest.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = blobStore.OpenRead(digest);
        return ProjectionCanonicalization.DecodeBody(stream);
    }

    private static List<ProjectionDependencyEdge> ReadDependencies(
        DbConnection connection,
        string? impactAnalysisId)
    {
        var affected = ReadAffectedEdges(connection, impactAnalysisId);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT edge_id,from_object_id,to_object_id,edge_type,validation_status,confidence,
                   provenance_ref,source_revision_id,last_validated_at_ms
            FROM dependency_edges
            ORDER BY from_object_id,to_object_id,edge_type,edge_id;
            """;
        List<ProjectionDependencyEdge> edges = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var edgeId = reader.GetString(0);
            edges.Add(new ProjectionDependencyEdge(
                edgeId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                affected.Contains(edgeId) ? "needs_revalidation" : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        }

        return edges;
    }

    private static HashSet<string> ReadAffectedEdges(DbConnection connection, string? impactAnalysisId)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (impactAnalysisId is null)
        {
            return result;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT affected_set_json FROM impact_analyses WHERE impact_analysis_id=$id;";
        Add(command, "$id", impactAnalysisId);
        var json = command.ExecuteScalar() as string;
        if (json is null)
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var value in document.RootElement.GetProperty("dependencyEdgeIds").EnumerateArray())
        {
            result.Add(value.GetString()!);
        }

        return result;
    }

    private static List<ProjectionRegistryEntry> BuildRegistryEntries(
        DbConnection connection,
        IReadOnlyList<ProjectionNarrativeObject> objects,
        IReadOnlyList<ProjectionArtifact> artifacts)
    {
        var existing = ReadExistingRegistry(connection);
        var artifactsByObject = artifacts.Where(value => value.ObjectId is not null)
            .ToDictionary(value => value.ObjectId!, StringComparer.Ordinal);
        List<ProjectionRegistryEntry> entries = [];
        foreach (var item in objects.OrderBy(value => value.ObjectId, StringComparer.Ordinal))
        {
            var artifact = artifactsByObject[item.ObjectId];
            existing.TryGetValue(item.ObjectId, out var current);
            entries.Add(new ProjectionRegistryEntry(
                current?.RegistryEntryId ?? StableProjectionUuidV7.Create(item.ObjectId, "registry-entry"),
                item.ObjectId,
                item.ObjectType,
                item.SchemaVersion,
                current?.PathId ?? StableProjectionUuidV7.Create(item.ObjectId, "canonical-path"),
                item.CanonicalRelativePath,
                "narrative_projection",
                true,
                "registered",
                StringComparer.OrdinalIgnoreCase.Equals(item.ObjectType, "raw") ? "unavailable" : "available",
                "clean",
                artifact.PhysicalDigest,
                artifact.SemanticDigest));
        }

        return entries;
    }

    private static Dictionary<string, ExistingRegistryRow> ReadExistingRegistry(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entries.object_id,entries.registry_entry_id,paths.path_id
            FROM registry_entries entries
            JOIN object_paths paths ON paths.path_id=entries.path_id
            WHERE entries.object_id IS NOT NULL AND paths.is_canonical=1
            ORDER BY entries.object_id;
            """;
        Dictionary<string, ExistingRegistryRow> rows = new(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows[reader.GetString(0)] = new ExistingRegistryRow(reader.GetString(1), reader.GetString(2));
        }

        return rows;
    }

    private static ProjectionRecoveryMetadata CreateMetadata(ProjectionBuild build)
    {
        var registryArtifact = build.Artifacts.Single(value => value.Kind == ProjectionArtifactKind.RegistryJson);
        using var registryDocument = JsonDocument.Parse(registryArtifact.Bytes);
        var registryByObject = registryDocument.RootElement.GetProperty("entries")
            .EnumerateArray()
            .ToDictionary(
                value => value.GetProperty("objectId").GetString()!,
                value => new RegistryRecoveryRow(
                    value.GetProperty("registryEntryId").GetString()!,
                    value.GetProperty("pathId").GetString()!,
                    value.GetProperty("registrationState").GetString()!,
                    value.GetProperty("retrievalAvailability").GetString()!,
                    value.GetProperty("reconcileState").GetString()!),
                StringComparer.Ordinal);
        var registrations = build.Artifacts
            .Where(value => value.Kind == ProjectionArtifactKind.NarrativeMarkdown)
            .Select(value =>
            {
                var registry = registryByObject[value.ObjectId!];
                return new ProjectionRegistrationMetadata(
                    registry.RegistryEntryId,
                    value.ObjectId!,
                    value.ObjectType!,
                    value.SchemaVersion!.Value,
                    registry.PathId,
                    value.TargetRelativePath,
                    value.PhysicalDigest,
                    value.SemanticDigest,
                    value.ArtifactDigest,
                    value.Status!,
                    registry.RegistrationState,
                    registry.RetrievalAvailability,
                    registry.ReconcileState);
            })
            .OrderBy(value => value.ObjectId, StringComparer.Ordinal)
            .ToArray();
        return new ProjectionRecoveryMetadata(registrations);
    }

    private static bool RequiresNewStateRevision(NarrativeChangeRecord change) =>
        change.ChangeKind is NarrativeChangeKind.Add or NarrativeChangeKind.Modify or NarrativeChangeKind.Reintroduce;

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record ObjectProjectionRow(
        string ObjectId,
        string ObjectType,
        int SchemaVersion,
        int Revision,
        string Status,
        string? StateRevisionId,
        string? ArtifactDigest);

    private sealed record ExistingRegistryRow(string RegistryEntryId, string PathId);

    private sealed record RegistryRecoveryRow(
        string RegistryEntryId,
        string PathId,
        string RegistrationState,
        string RetrievalAvailability,
        string ReconcileState);
}
