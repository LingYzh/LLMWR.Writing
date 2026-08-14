using System.Data.Common;
using System.Security.Cryptography;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Projection;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Domain.Registry;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Projection;

namespace LLMW.Writing.Infrastructure.Reconcile;

public sealed class ProjectReconcileEngine
{
    public const int DefaultScanBatchSize = 128;

    private readonly string databasePath;
    private readonly ImmutableBlobStore blobStore;
    private readonly SqliteDatabaseConnectionFactory connectionFactory;
    private readonly ProjectPathResolver paths;
    private readonly NarrativeProjectionPlanner projectionPlanner;
    private readonly ProjectionFrontmatterParser parser;
    private readonly ISelfWriteTracker selfWriteTracker;
    private readonly Func<long> clock;
    private readonly object observationSync = new();
    private readonly Dictionary<string, ReconcileObservation> observations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> ignoredUnregisteredPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectReconcileEngine(
        string projectRoot,
        string databasePath,
        ImmutableBlobStore blobStore,
        ISelfWriteTracker selfWriteTracker,
        SqliteDatabaseConnectionFactory? connectionFactory = null,
        ProjectionFrontmatterParser? parser = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.selfWriteTracker = selfWriteTracker ?? throw new ArgumentNullException(nameof(selfWriteTracker));
        this.connectionFactory = connectionFactory ?? new SqliteDatabaseConnectionFactory();
        paths = new ProjectPathResolver(projectRoot);
        projectionPlanner = new NarrativeProjectionPlanner(this.databasePath, blobStore, this.connectionFactory);
        this.parser = parser ?? new ProjectionFrontmatterParser();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public ProjectPathResolver PathResolver => paths;

    public ReconcileScanReport Scan(
        FileEventSource source,
        int batchSize = DefaultScanBatchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        cancellationToken.ThrowIfCancellationRequested();
        var registryRows = ReadRegistryRows();
        var manuscriptRows = ReadManuscriptRows();
        var currentProjection = projectionPlanner.BuildCurrent(cancellationToken).Build;
        var expectedProjection = currentProjection.Artifacts.ToDictionary(
            item => paths.NormalizeRelativePath(item.TargetRelativePath, rejectReparsePoints: false),
            StringComparer.OrdinalIgnoreCase);
        var projectionBaselineExists = registryRows.Count > 0 || expectedProjection.Keys.Any(ProjectionFileExists);

        List<ReconcileObservation> scanned = [];
        foreach (var row in registryRows.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned.Add(ClassifyRegistry(row));
        }

        if (projectionBaselineExists)
        {
            foreach (var artifact in expectedProjection.Values
                         .Where(item => item.Kind != ProjectionArtifactKind.NarrativeMarkdown)
                         .OrderBy(item => item.TargetRelativePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned.Add(ClassifyMachineProjection(artifact));
            }
        }

        foreach (var row in manuscriptRows.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned.Add(ClassifyManuscript(row));
        }

        var knownPaths = registryRows.Select(item => item.RelativePath)
            .Concat(expectedProjection.Keys)
            .Concat(manuscriptRows.Select(item => item.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in EnumerateUnregisteredNarrativeFiles(knownPaths, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned.Add(ClassifyUnregistered(relativePath));
        }

        ApplyRenameHeuristic(scanned);
        scanned = scanned.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToList();

        var batchCount = 0;
        foreach (var batch in scanned.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistBatch(batch, cancellationToken);
            batchCount++;
        }

        lock (observationSync)
        {
            observations.Clear();
            foreach (var observation in scanned)
            {
                observations[observation.RelativePath] = observation;
            }
        }

        return new ReconcileScanReport(source, scanned, batchCount, FullRescan: true);
    }

    public ReconcileObservation? Inspect(string relativePath)
    {
        var normalized = paths.NormalizeRelativePath(relativePath);
        lock (observationSync)
        {
            return observations.GetValueOrDefault(normalized);
        }
    }

    public void IgnoreUnregistered(string relativePath)
    {
        var normalized = paths.NormalizeRelativePath(relativePath);
        lock (observationSync)
        {
            ignoredUnregisteredPaths.Add(normalized);
        }
    }

    public AuthoritySurfaceHealth InspectAuthorityHealthFresh(
        AuthoritySurfaceHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken);
        var issues = report.Observations
            .Where(observation => !IsAllowedResolutionIssue(observation, request))
            .Select(ToAuthorityIssue)
            .Where(issue => issue is not null)
            .Cast<AuthoritySurfaceIssue>()
            .OrderBy(issue => issue.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return issues.Length == 0 ? AuthoritySurfaceHealth.Healthy : new AuthoritySurfaceHealth(false, issues);
    }

    public ReconcileResult<ReconcileScanReport> RestoreManuscript(
        string relativePath,
        AtomicAuthorityMaterializer materializer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        var normalized = paths.NormalizeRelativePath(relativePath);
        var row = ReadManuscriptRows().SingleOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.RelativePath, normalized));
        if (row is null)
        {
            return ReconcileResults.Fail<ReconcileScanReport>(ReconcileError.ReconcileEntryNotFound, normalized);
        }

        try
        {
            var plan = new AuthorityMaterializationPlan(row.RelativePath, row.ArtifactDigest);
            materializer.Materialize(row.TransactionId, [plan], cancellationToken);
            materializer.Verify(row.TransactionId, [plan], cancellationToken);
            using var connection = connectionFactory.OpenConfigured(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE manuscript_revisions SET materialization_status='materialized' WHERE revision_id=$revision_id;";
            Add(command, "$revision_id", row.RevisionId);
            command.ExecuteNonQuery();
            return ReconcileResults.Success(Scan(FileEventSource.FullRescan, cancellationToken: cancellationToken));
        }
        catch (Exception exception)
        {
            return ReconcileResults.Fail<ReconcileScanReport>(ReconcileError.InfrastructureFailure, exception.Message);
        }
    }

    private bool ProjectionFileExists(string relativePath)
    {
        try
        {
            return File.Exists(paths.Resolve(relativePath));
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private ReconcileObservation ClassifyRegistry(RegistryRow row)
    {
        var read = ReadPhysical(row.RelativePath, includeBytes: true);
        if (read.Status == PhysicalReadStatus.Missing)
        {
            return Observation(
                row,
                ReconcileClassification.RegisteredMissing,
                null,
                null,
                RegistryRegistrationState.Missing,
                RegistryRetrievalAvailability.Unavailable,
                RegistryReconcileState.Dirty,
                ["Registered canonical projection is missing; no Narrative REMOVE was inferred."]);
        }

        if (read.Status == PhysicalReadStatus.Unavailable)
        {
            return Observation(
                row,
                ReconcileClassification.FileTemporarilyUnavailable,
                null,
                null,
                RegistryRegistrationState.Registered,
                RegistryRetrievalAvailability.Stale,
                RegistryReconcileState.NeedsAttention,
                [read.Error ?? "The file is temporarily unavailable."]);
        }

        var suppressed = selfWriteTracker.ShouldSuppress(null, row.RelativePath, read.Digest);
        if (suppressed || StringComparer.Ordinal.Equals(row.TrustedPhysicalDigest, read.Digest))
        {
            return Observation(
                row,
                ReconcileClassification.Unchanged,
                read.Digest,
                row.TrustedSemanticDigest,
                RegistryRegistrationState.Registered,
                IsRaw(row.ObjectType) ? RegistryRetrievalAvailability.Unavailable : RegistryRetrievalAvailability.Available,
                RegistryReconcileState.Clean,
                [],
                suppressed);
        }

        var semantic = TryObservedSemantic(read.Bytes);
        return Observation(
            row,
            ReconcileClassification.RegisteredModified,
            read.Digest,
            semantic,
            RegistryRegistrationState.Registered,
            RegistryRetrievalAvailability.Stale,
            RegistryReconcileState.Dirty,
            ["External bytes differ from the trusted physical baseline; the trusted digest was preserved."]);
    }

    private ReconcileObservation ClassifyMachineProjection(ProjectionArtifact artifact)
    {
        var relativePath = paths.NormalizeRelativePath(artifact.TargetRelativePath, rejectReparsePoints: false);
        var read = ReadPhysical(relativePath, includeBytes: false);
        var missing = read.Status == PhysicalReadStatus.Missing;
        var unavailable = read.Status == PhysicalReadStatus.Unavailable;
        var suppressed = read.Status == PhysicalReadStatus.Available &&
                         selfWriteTracker.ShouldSuppress(null, relativePath, read.Digest);
        var unchanged = suppressed || StringComparer.Ordinal.Equals(artifact.PhysicalDigest, read.Digest);
        var classification = missing
            ? ReconcileClassification.ProjectionMissing
            : unavailable
                ? ReconcileClassification.FileTemporarilyUnavailable
                : unchanged
                    ? ReconcileClassification.Unchanged
                    : ReconcileClassification.ProjectionModified;
        var warnings = classification == ReconcileClassification.Unchanged
            ? Array.Empty<string>()
            : ["Machine projection cannot directly mutate Authority; Core rebuild is required."];
        return new ReconcileObservation(
            relativePath,
            ReconcileSurfaceKind.MachineProjection,
            classification,
            null,
            artifact.PhysicalDigest,
            read.Digest,
            artifact.SemanticDigest,
            null,
            RegistryRegistrationState.Registered,
            classification == ReconcileClassification.Unchanged
                ? RegistryRetrievalAvailability.Available
                : RegistryRetrievalAvailability.Stale,
            classification == ReconcileClassification.Unchanged
                ? RegistryReconcileState.Clean
                : RegistryReconcileState.Dirty,
            false,
            suppressed,
            null,
            warnings);
    }

    private ReconcileObservation ClassifyManuscript(ManuscriptRow row)
    {
        var read = ReadPhysical(row.RelativePath, includeBytes: false);
        var suppressed = read.Status == PhysicalReadStatus.Available &&
                         selfWriteTracker.ShouldSuppress(null, row.RelativePath, read.Digest);
        var classification = read.Status switch
        {
            PhysicalReadStatus.Missing => ReconcileClassification.ManuscriptMaterializationMissing,
            PhysicalReadStatus.Unavailable => ReconcileClassification.FileTemporarilyUnavailable,
            _ when suppressed || StringComparer.Ordinal.Equals(row.ArtifactDigest, read.Digest) =>
                ReconcileClassification.Unchanged,
            _ => ReconcileClassification.ManuscriptMaterializationModified
        };
        return new ReconcileObservation(
            row.RelativePath,
            ReconcileSurfaceKind.ManuscriptMaterialization,
            classification,
            row.ChapterId,
            row.ArtifactDigest,
            read.Digest,
            null,
            null,
            RegistryRegistrationState.Registered,
            classification == ReconcileClassification.Unchanged
                ? RegistryRetrievalAvailability.Available
                : RegistryRetrievalAvailability.Stale,
            classification == ReconcileClassification.Unchanged
                ? RegistryReconcileState.Clean
                : RegistryReconcileState.Dirty,
            false,
            suppressed,
            null,
            classification == ReconcileClassification.Unchanged
                ? []
                : ["Current Manuscript is Authority-derived; external bytes were not imported into Authority."]);
    }

    private ReconcileObservation ClassifyUnregistered(string relativePath)
    {
        bool ignored;
        lock (observationSync)
        {
            ignored = ignoredUnregisteredPaths.Contains(relativePath);
        }

        var read = ReadPhysical(relativePath, includeBytes: true);
        var objectId = TryParsedObjectId(read.Bytes);
        return new ReconcileObservation(
            relativePath,
            ReconcileSurfaceKind.Unregistered,
            ignored ? ReconcileClassification.Ignored : ReconcileClassification.UnregisteredNew,
            objectId,
            null,
            read.Digest,
            null,
            TryObservedSemantic(read.Bytes),
            ignored ? RegistryRegistrationState.Ignored : RegistryRegistrationState.Unregistered,
            RegistryRetrievalAvailability.Unavailable,
            RegistryReconcileState.PendingConfirm,
            false,
            false,
            null,
            ["Arbitrary unregistered paths are rebuildable runtime observations in schema v1 and are not normally retrievable."]);
    }

    private static void ApplyRenameHeuristic(List<ReconcileObservation> scanned)
    {
        var newFiles = scanned.Where(item => item.Classification == ReconcileClassification.UnregisteredNew &&
                                             item.ObjectId is not null).ToArray();
        for (var index = 0; index < scanned.Count; index++)
        {
            var missing = scanned[index];
            if (missing.Classification != ReconcileClassification.RegisteredMissing || missing.ObjectId is null)
            {
                continue;
            }

            var candidates = newFiles
                .Where(item => StringComparer.Ordinal.Equals(item.ObjectId, missing.ObjectId))
                .Select(item =>
                {
                    var digestMatches = StringComparer.Ordinal.Equals(
                        item.ObservedPhysicalDigest,
                        missing.TrustedPhysicalDigest);
                    var similarity = PathSimilarity(missing.RelativePath, item.RelativePath);
                    var confidence = digestMatches
                        ? RenameConfidence.High
                        : similarity >= 0.5
                            ? RenameConfidence.Medium
                            : RenameConfidence.Low;
                    return new RenameEvidence(
                        missing.RelativePath,
                        item.RelativePath,
                        missing.ObjectId,
                        true,
                        digestMatches,
                        similarity,
                        confidence);
                })
                .OrderByDescending(item => item.Confidence)
                .ThenByDescending(item => item.PathSimilarity)
                .ThenBy(item => item.NewRelativePath, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            var evidence = candidates[0];
            scanned[index] = missing with
            {
                Classification = ReconcileClassification.SuspectedRename,
                ReconcileState = RegistryReconcileState.PendingConfirm,
                RenameEvidence = evidence,
                Warnings = missing.Warnings.Concat(
                    ["A possible move/rename was detected; canonical path adoption was not performed."]).ToArray()
            };
        }
    }

    private static double PathSimilarity(string left, string right)
    {
        var leftName = Path.GetFileNameWithoutExtension(left);
        var rightName = Path.GetFileNameWithoutExtension(right);
        var length = Math.Max(leftName.Length, rightName.Length);
        if (length == 0)
        {
            return 1;
        }

        var prefix = 0;
        while (prefix < Math.Min(leftName.Length, rightName.Length) &&
               char.ToUpperInvariant(leftName[prefix]) == char.ToUpperInvariant(rightName[prefix]))
        {
            prefix++;
        }

        return (double)prefix / length;
    }

    private static ReconcileObservation Observation(
        RegistryRow row,
        ReconcileClassification classification,
        string? observedPhysical,
        string? observedSemantic,
        RegistryRegistrationState registration,
        RegistryRetrievalAvailability availability,
        RegistryReconcileState reconcile,
        IReadOnlyList<string> warnings,
        bool selfWriteSuppressed = false) =>
        new(
            row.RelativePath,
            ReconcileSurfaceKind.NarrativeProjection,
            classification,
            row.ObjectId,
            row.TrustedPhysicalDigest,
            observedPhysical,
            row.TrustedSemanticDigest,
            observedSemantic,
            registration,
            availability,
            reconcile,
            registration == RegistryRegistrationState.Registered &&
            availability == RegistryRetrievalAvailability.Available &&
            reconcile == RegistryReconcileState.Clean &&
            !IsRaw(row.ObjectType),
            selfWriteSuppressed,
            null,
            warnings);

    private void PersistBatch(ReconcileObservation[] batch, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        foreach (var observation in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation.SurfaceKind == ReconcileSurfaceKind.NarrativeProjection &&
                observation.ObjectId is not null)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE registry_entries
                    SET registration_state=$registration,retrieval_availability=$availability,
                        reconcile_state=$reconcile,last_verified_at_ms=$now,updated_at_ms=$now
                    WHERE object_id=$object_id;
                    """;
                Add(command, "$registration", RegistrationValue(observation.RegistrationState));
                Add(command, "$availability", AvailabilityValue(observation.RetrievalAvailability));
                Add(command, "$reconcile", ReconcileValue(observation.ReconcileState));
                Add(command, "$now", clock());
                Add(command, "$object_id", observation.ObjectId);
                command.ExecuteNonQuery();
            }
            else if (observation.SurfaceKind == ReconcileSurfaceKind.ManuscriptMaterialization &&
                     observation.ObjectId is not null)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE manuscript_revisions
                    SET materialization_status=$status
                    WHERE revision_id=(SELECT current_manuscript_revision_id FROM chapters WHERE chapter_id=$chapter_id);
                    """;
                Add(command, "$status", observation.Classification == ReconcileClassification.Unchanged
                    ? "materialized"
                    : observation.Classification == ReconcileClassification.ManuscriptMaterializationMissing
                        ? "missing"
                        : "dirty");
                Add(command, "$chapter_id", observation.ObjectId);
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private string[] EnumerateUnregisteredNarrativeFiles(
        HashSet<string> knownPaths,
        CancellationToken cancellationToken)
    {
        var narrativeRoot = Path.Combine(paths.ProjectRoot, "Narrative");
        if (!Directory.Exists(narrativeRoot))
        {
            return [];
        }

        List<string> result = [];
        foreach (var file in Directory.EnumerateFiles(narrativeRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = paths.FromFullPath(file);
            if (!knownPaths.Contains(relative) && !Path.GetFileName(relative).StartsWith(".tmp-", StringComparison.Ordinal))
            {
                result.Add(relative);
            }
        }

        return result.OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private List<RegistryRow> ReadRegistryRows()
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entries.object_id,entries.object_type,paths.relative_path,
                   entries.trusted_physical_digest,entries.trusted_semantic_digest
            FROM registry_entries entries
            JOIN object_paths paths ON paths.path_id=entries.path_id
            WHERE entries.object_id IS NOT NULL AND paths.is_canonical=1
            ORDER BY paths.relative_path;
            """;
        List<RegistryRow> rows = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RegistryRow(
                reader.GetString(0),
                reader.GetString(1),
                paths.NormalizeRelativePath(reader.GetString(2), rejectReparsePoints: false),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private List<ManuscriptRow> ReadManuscriptRows()
    {
        using var connection = connectionFactory.OpenConfigured(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT chapters.chapter_id,revisions.revision_id,revisions.artifact_digest,
                   revisions.transaction_id,candidates.source_draft_path
            FROM chapters
            JOIN manuscript_revisions revisions ON revisions.revision_id=chapters.current_manuscript_revision_id
            JOIN candidates ON candidates.candidate_id=revisions.candidate_id
            ORDER BY chapters.chapter_id;
            """;
        List<ManuscriptRow> rows = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var extension = Path.GetExtension(reader.GetString(4)).ToLowerInvariant();
            var relative = paths.NormalizeRelativePath(
                $"Manuscript/current/{reader.GetString(0)}{extension}",
                rejectReparsePoints: false);
            rows.Add(new ManuscriptRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                relative));
        }

        return rows;
    }

    private PhysicalRead ReadPhysical(string relativePath, bool includeBytes)
    {
        string fullPath;
        try
        {
            fullPath = paths.Resolve(relativePath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
        {
            return new PhysicalRead(PhysicalReadStatus.Unavailable, null, null, exception.Message);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!File.Exists(fullPath))
            {
                return new PhysicalRead(PhysicalReadStatus.Missing, null, null, null);
            }

            try
            {
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024,
                    FileOptions.SequentialScan);
                using var memory = includeBytes ? new MemoryStream() : null;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    memory?.Write(buffer, 0, read);
                }

                return new PhysicalRead(
                    PhysicalReadStatus.Available,
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    memory?.ToArray(),
                    null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(fullPath))
                {
                    return new PhysicalRead(PhysicalReadStatus.Missing, null, null, null);
                }

                if (attempt == 2)
                {
                    return new PhysicalRead(PhysicalReadStatus.Unavailable, null, null, exception.Message);
                }
            }
        }

        return new PhysicalRead(PhysicalReadStatus.Unavailable, null, null, "Physical read retry was exhausted.");
    }

    private string? TryObservedSemantic(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        var parsed = parser.Parse(bytes);
        if (!parsed.Succeeded)
        {
            return null;
        }

        var normalized = parsed.Value!.Body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Normalize().TrimEnd('\n');
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private string? TryParsedObjectId(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        var parsed = parser.Parse(bytes);
        return parsed.Succeeded && parsed.Value!.KnownFields.TryGetValue("objectId", out var value)
            ? value
            : null;
    }

    private static AuthoritySurfaceIssue? ToAuthorityIssue(ReconcileObservation observation) =>
        observation.Classification switch
        {
            ReconcileClassification.RegisteredModified when observation.ObjectId is not null =>
                new AuthoritySurfaceIssue(AuthoritySurfaceIssueKind.RegistryDirty, observation.RelativePath,
                    observation.ObjectId, "Registered projection differs from its trusted baseline."),
            ReconcileClassification.RegisteredMissing when observation.ObjectId is not null =>
                new AuthoritySurfaceIssue(AuthoritySurfaceIssueKind.RegistryMissing, observation.RelativePath,
                    observation.ObjectId, "Registered projection is missing."),
            ReconcileClassification.SuspectedRename when observation.ObjectId is not null =>
                new AuthoritySurfaceIssue(AuthoritySurfaceIssueKind.PendingReconcile, observation.RelativePath,
                    observation.ObjectId, "Suspected rename requires confirmation."),
            ReconcileClassification.ManuscriptMaterializationModified or
                ReconcileClassification.ManuscriptMaterializationMissing =>
                new AuthoritySurfaceIssue(AuthoritySurfaceIssueKind.MaterializationDirty, observation.RelativePath,
                    observation.ObjectId, "Current Manuscript materialization is dirty or missing."),
            ReconcileClassification.ProjectionModified or ReconcileClassification.ProjectionMissing =>
                new AuthoritySurfaceIssue(AuthoritySurfaceIssueKind.MachineProjectionDirty, observation.RelativePath,
                    observation.ObjectId, "Machine projection requires Core rebuild."),
            ReconcileClassification.FileTemporarilyUnavailable =>
                new AuthoritySurfaceIssue(AuthoritySurfaceIssueKind.FileTemporarilyUnavailable,
                    observation.RelativePath, observation.ObjectId, "Required surface is temporarily unavailable."),
            _ => null
        };

    private static bool IsAllowedResolutionIssue(
        ReconcileObservation observation,
        AuthoritySurfaceHealthRequest request) =>
        observation.ObjectId is not null &&
        request.ResolvingNarrativeObjectIds.Contains(observation.ObjectId) &&
        request.ExpectedObservedPhysicalDigests is not null &&
        request.ExpectedObservedPhysicalDigests.TryGetValue(observation.ObjectId, out var expectedDigest) &&
        StringComparer.Ordinal.Equals(expectedDigest, observation.ObservedPhysicalDigest) &&
        observation.Classification is ReconcileClassification.RegisteredModified or
            ReconcileClassification.SuspectedRename;

    private static bool IsRaw(string objectType) =>
        StringComparer.OrdinalIgnoreCase.Equals(objectType, "raw");

    private static string RegistrationValue(RegistryRegistrationState state) => state switch
    {
        RegistryRegistrationState.Registered => "registered",
        RegistryRegistrationState.Unregistered => "unregistered",
        RegistryRegistrationState.Ignored => "ignored",
        RegistryRegistrationState.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static string AvailabilityValue(RegistryRetrievalAvailability value) => value switch
    {
        RegistryRetrievalAvailability.Available => "available",
        RegistryRetrievalAvailability.Unavailable => "unavailable",
        RegistryRetrievalAvailability.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string ReconcileValue(RegistryReconcileState value) => value switch
    {
        RegistryReconcileState.Clean => "clean",
        RegistryReconcileState.Dirty => "dirty",
        RegistryReconcileState.PendingConfirm => "pending_confirm",
        RegistryReconcileState.Reconciling => "reconciling",
        RegistryReconcileState.NeedsAttention => "needs_attention",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record RegistryRow(
        string ObjectId,
        string ObjectType,
        string RelativePath,
        string? TrustedPhysicalDigest,
        string? TrustedSemanticDigest);

    private sealed record ManuscriptRow(
        string ChapterId,
        string RevisionId,
        string ArtifactDigest,
        string TransactionId,
        string RelativePath);

    private enum PhysicalReadStatus
    {
        Available,
        Missing,
        Unavailable
    }

    private sealed record PhysicalRead(
        PhysicalReadStatus Status,
        string? Digest,
        byte[]? Bytes,
        string? Error);
}
