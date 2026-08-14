using System.Data.Common;
using System.Text;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Application.Projection;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Projection;

namespace LLMW.Writing.Infrastructure.Reconcile;

public sealed class NarrativeReconcileService
{
    private readonly string databasePath;
    private readonly ProjectReconcileEngine engine;
    private readonly ImmutableBlobStore blobStore;
    private readonly ProjectionFrontmatterParser parser;
    private readonly ProjectionRebuilder projectionRebuilder;
    private readonly NarrativeChangeService narrativeChangeService;
    private readonly AtomicAuthorityMaterializer manuscriptMaterializer;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;

    public NarrativeReconcileService(
        string databasePath,
        ProjectReconcileEngine engine,
        ImmutableBlobStore blobStore,
        ProjectionRebuilder projectionRebuilder,
        NarrativeChangeService narrativeChangeService,
        AtomicAuthorityMaterializer manuscriptMaterializer,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        ProjectionFrontmatterParser? parser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.projectionRebuilder = projectionRebuilder ?? throw new ArgumentNullException(nameof(projectionRebuilder));
        this.narrativeChangeService = narrativeChangeService ?? throw new ArgumentNullException(nameof(narrativeChangeService));
        this.manuscriptMaterializer = manuscriptMaterializer ?? throw new ArgumentNullException(nameof(manuscriptMaterializer));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        this.parser = parser ?? new ProjectionFrontmatterParser();
    }

    public ReconcileResult<ReconcileInspection> Analyze(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        string normalized;
        try
        {
            normalized = engine.PathResolver.NormalizeRelativePath(relativePath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
        {
            return ReconcileResults.Fail<ReconcileInspection>(ReconcileError.PathOutsideProject, exception.Message);
        }

        try
        {
            var report = engine.Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken);
            var observation = report.Observations.SingleOrDefault(item =>
                StringComparer.OrdinalIgnoreCase.Equals(item.RelativePath, normalized));
            if (observation is null)
            {
                return ReconcileResults.Fail<ReconcileInspection>(ReconcileError.ReconcileEntryNotFound, normalized);
            }

            Dictionary<string, string?> metadata = new(StringComparer.Ordinal);
            string? observedBody = null;
            List<string> warnings = [.. observation.Warnings];
            if (observation.SurfaceKind is ReconcileSurfaceKind.NarrativeProjection or ReconcileSurfaceKind.Unregistered &&
                File.Exists(engine.PathResolver.Resolve(normalized)))
            {
                var bytes = File.ReadAllBytes(engine.PathResolver.Resolve(normalized));
                var parsed = parser.Parse(bytes);
                if (parsed.Succeeded)
                {
                    foreach (var item in parsed.Value!.KnownFields)
                    {
                        metadata[item.Key] = item.Value;
                    }

                    foreach (var item in parsed.Value.CompatibleUnknownFields)
                    {
                        metadata[item.Key] = item.Value;
                    }

                    observedBody = parsed.Value.Body;
                    warnings.AddRange(parsed.Value.Warnings);
                }
                else
                {
                    warnings.Add($"Projection parse failed: {parsed.Failure!.Detail}");
                }
            }

            var authority = observation.ObjectId is null ? null : ReadAuthority(observation.ObjectId);
            return ReconcileResults.Success(new ReconcileInspection(
                observation,
                metadata,
                authority?.SnapshotDigest,
                authority is null ? null : ReadAuthorityBody(authority.SnapshotDigest),
                observedBody,
                SuggestedResolutions(observation),
                warnings.OrderBy(item => item, StringComparer.Ordinal).ToArray()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DbException)
        {
            return ReconcileResults.Fail<ReconcileInspection>(ReconcileError.InfrastructureFailure, exception.Message);
        }
    }

    public ReconcileResult<ReconcileInspection> Inspect(
        string relativePath,
        CancellationToken cancellationToken = default) => Analyze(relativePath, cancellationToken);

    public ReconcileResult<ReconcileScanReport> Ignore(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var analyzed = Analyze(relativePath, cancellationToken);
        if (!analyzed.Succeeded)
        {
            return new ReconcileResult<ReconcileScanReport>(null, analyzed.Failure);
        }

        if (analyzed.Value!.Observation.Classification != ReconcileClassification.UnregisteredNew)
        {
            return ReconcileResults.Fail<ReconcileScanReport>(
                ReconcileError.ReconcileNotSupported,
                "Ignore cannot clear a registered digest mismatch or required materialization state.");
        }

        engine.IgnoreUnregistered(relativePath);
        return ReconcileResults.Success(
            engine.Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken));
    }

    public ReconcileResult<ReconcileScanReport> Delete(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var analyzed = Analyze(relativePath, cancellationToken);
        if (!analyzed.Succeeded)
        {
            return new ReconcileResult<ReconcileScanReport>(null, analyzed.Failure);
        }

        if (analyzed.Value!.Observation.Classification is not
            (ReconcileClassification.UnregisteredNew or ReconcileClassification.Ignored))
        {
            return ReconcileResults.Fail<ReconcileScanReport>(
                ReconcileError.ReconcileNotSupported,
                "Deleting a registered Narrative projection cannot stand in for formal Narrative REMOVE.");
        }

        try
        {
            var target = engine.PathResolver.Resolve(relativePath);
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            return ReconcileResults.Success(
                engine.Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ReconcileResults.Fail<ReconcileScanReport>(ReconcileError.InfrastructureFailure, exception.Message);
        }
    }

    public ReconcileResult<ReconcileScanReport> Restore(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var analyzed = Analyze(relativePath, cancellationToken);
        if (!analyzed.Succeeded)
        {
            return new ReconcileResult<ReconcileScanReport>(null, analyzed.Failure);
        }

        var observation = analyzed.Value!.Observation;
        if (observation.Classification == ReconcileClassification.FileTemporarilyUnavailable)
        {
            return ReconcileResults.Fail<ReconcileScanReport>(
                ReconcileError.FileTemporarilyUnavailable,
                "The required surface cannot be restored until its path is safely readable.");
        }

        if (observation.SurfaceKind == ReconcileSurfaceKind.ManuscriptMaterialization)
        {
            return engine.RestoreManuscript(relativePath, manuscriptMaterializer, cancellationToken);
        }

        if (observation.SurfaceKind is ReconcileSurfaceKind.NarrativeProjection or ReconcileSurfaceKind.MachineProjection)
        {
            var rebuilt = projectionRebuilder.Rebuild(cancellationToken);
            return rebuilt.Succeeded
                ? ReconcileResults.Success(engine.Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken))
                : ReconcileResults.Fail<ReconcileScanReport>(ReconcileError.InfrastructureFailure, rebuilt.Failure!.Detail);
        }

        return ReconcileResults.Fail<ReconcileScanReport>(ReconcileError.ReconcileNotSupported);
    }

    public ReconcileResult<ConfirmNarrativeReconcileResult> ConfirmNarrativeModify(
        string relativePath,
        string idempotencyKey,
        string acceptedById,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedById);
        var analyzed = Analyze(relativePath, cancellationToken);
        if (!analyzed.Succeeded)
        {
            return new ReconcileResult<ConfirmNarrativeReconcileResult>(null, analyzed.Failure);
        }

        var inspection = analyzed.Value!;
        var observation = inspection.Observation;
        if (observation.Classification != ReconcileClassification.RegisteredModified || observation.ObjectId is null)
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(
                ReconcileError.ReconcileConfirmationRequired,
                observation.Classification.ToString());
        }

        var fullPath = engine.PathResolver.Resolve(relativePath);
        var externalBytes = File.ReadAllBytes(fullPath);
        var observedDigest = PhysicalDigest(externalBytes);
        if (!StringComparer.Ordinal.Equals(observation.ObservedPhysicalDigest, observedDigest))
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(
                ReconcileError.PreconditionChanged,
                "The external projection changed after Analyze.");
        }

        var parsed = parser.Parse(externalBytes);
        if (!parsed.Succeeded)
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(
                ReconcileError.ProjectionParseFailed,
                parsed.Failure!.Detail);
        }

        var parsedProjection = parsed.Value!;
        var authority = ReadAuthority(observation.ObjectId);
        if (authority is null)
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(ReconcileError.ReconcileEntryNotFound);
        }

        if (!TryKnown(parsedProjection, "objectId", out var parsedObjectId) ||
            !StringComparer.Ordinal.Equals(parsedObjectId, authority.ObjectId))
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(ReconcileError.ProjectionIdentityMismatch);
        }

        if (!TryKnown(parsedProjection, "objectType", out var parsedObjectType) ||
            !StringComparer.Ordinal.Equals(parsedObjectType, authority.ObjectType))
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(ReconcileError.ProjectionIdentityMismatch);
        }

        if (!TryKnown(parsedProjection, "schemaVersion", out var parsedSchema) ||
            !int.TryParse(parsedSchema, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var schemaVersion) ||
            schemaVersion != authority.SchemaVersion)
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(ReconcileError.ProjectionSchemaMismatch);
        }

        if (parsedProjection.CompatibleUnknownFields.Count > 0)
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(
                ReconcileError.ReconcileNotSupported,
                "Compatible namespaced fields cannot yet be promoted into Narrative Authority without loss.");
        }

        var externalBody = NormalizeBody(parsedProjection.Body);
        var currentBody = NormalizeBody(ReadAuthorityBody(authority.SnapshotDigest));
        if (StringComparer.Ordinal.Equals(externalBody, currentBody))
        {
            if (!PhysicalDigestStillMatches(fullPath, observedDigest))
            {
                return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(ReconcileError.PreconditionChanged);
            }

            var rebuilt = projectionRebuilder.Rebuild(cancellationToken);
            if (!rebuilt.Succeeded)
            {
                return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(
                    ReconcileError.InfrastructureFailure,
                    rebuilt.Failure!.Detail);
            }

            var finalScan = engine.Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken);
            return ReconcileResults.Success(new ConfirmNarrativeReconcileResult(
                authority.ObjectId,
                AuthorityChanged: false,
                AppliedChange: null,
                finalScan));
        }

        using var payload = new MemoryStream(Encoding.UTF8.GetBytes(externalBody), writable: false);
        var working = narrativeChangeService.CreateWorkingChangeSet(
            new CreateWorkingNarrativeChangeSetCommand(
                "object",
                authority.ObjectId,
                "user_reconcile",
                acceptedById,
                [new WorkingNarrativeChangeInput(
                    authority.ObjectId,
                    authority.ObjectType,
                    NarrativeChangeKind.Modify,
                    authority.StateRevisionId,
                    authority.SnapshotDigest,
                    payload)]),
            cancellationToken);
        if (!working.Succeeded)
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(
                MapNarrativeFailure(working.Failure!),
                working.Failure!.Detail);
        }

        var applied = narrativeChangeService.Apply(
            new ApplyNarrativeChangeSetCommand(
                working.Value!.ChangeSetId,
                idempotencyKey,
                NarrativeDecisionKind.AuthorConfirmed,
                acceptedById,
                [authority.ObjectId],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [authority.ObjectId] = observedDigest
                }),
            cancellationToken);
        if (!applied.Succeeded)
        {
            return ReconcileResults.Fail<ConfirmNarrativeReconcileResult>(
                MapNarrativeFailure(applied.Failure!),
                applied.Failure!.Detail);
        }

        var scan = engine.Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken);
        return ReconcileResults.Success(new ConfirmNarrativeReconcileResult(
            authority.ObjectId,
            AuthorityChanged: true,
            applied.Value,
            scan));
    }

    private static bool PhysicalDigestStillMatches(string fullPath, string expectedDigest)
    {
        try
        {
            return StringComparer.Ordinal.Equals(PhysicalDigest(File.ReadAllBytes(fullPath)), expectedDigest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PhysicalDigest(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private AuthorityRow? ReadAuthority(string objectId)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT objects.object_id,objects.object_type,objects.schema_version,
                   state.state_revision_id,state.snapshot_digest
            FROM objects
            JOIN narrative_state_revisions state ON state.scope_object_id=objects.object_id
            WHERE objects.object_id=$object_id AND objects.status='current'
              AND NOT EXISTS(
                  SELECT 1 FROM narrative_state_revisions successor
                  WHERE successor.supersedes_state_revision_id=state.state_revision_id)
            LIMIT 1;
            """;
        Add(command, "$object_id", objectId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AuthorityRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4))
            : null;
    }

    private string ReadAuthorityBody(string digest)
    {
        using var stream = blobStore.OpenRead(digest);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<string> SuggestedResolutions(ReconcileObservation observation) =>
        observation.Classification switch
        {
            ReconcileClassification.RegisteredModified =>
                ["Inspect", "Confirm Narrative Modify through WP06", "Restore canonical projection from Authority"],
            ReconcileClassification.RegisteredMissing or ReconcileClassification.SuspectedRename =>
                ["Inspect dependency impact", "Restore canonical projection from Authority"],
            ReconcileClassification.ProjectionModified or ReconcileClassification.ProjectionMissing =>
                ["Inspect", "Rebuild machine projection from Authority"],
            ReconcileClassification.ManuscriptMaterializationModified or
                ReconcileClassification.ManuscriptMaterializationMissing =>
                ["Compare", "Restore Current Manuscript from accepted Authority artifact"],
            ReconcileClassification.UnregisteredNew => ["Analyze", "Ignore", "Delete"],
            _ => []
        };

    private static bool TryKnown(
        ParsedProjectionFrontmatter parsed,
        string key,
        out string value)
    {
        if (parsed.KnownFields.TryGetValue(key, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string NormalizeBody(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize()
            .TrimEnd('\n');

    private static ReconcileError MapNarrativeFailure(NarrativeChangeFailure failure) => failure.Code switch
    {
        NarrativeChangeError.AuthorityDirty => ReconcileError.AuthoritySurfaceDirty,
        NarrativeChangeError.PreconditionChanged or NarrativeChangeError.BeforeDigestMismatch or
            NarrativeChangeError.BeforeRevisionMismatch => ReconcileError.PreconditionChanged,
        NarrativeChangeError.ObjectNotFound => ReconcileError.ReconcileEntryNotFound,
        _ => ReconcileError.InfrastructureFailure
    };

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record AuthorityRow(
        string ObjectId,
        string ObjectType,
        int SchemaVersion,
        string StateRevisionId,
        string SnapshotDigest);
}
