using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LLMW.Writing.Application.ProjectPackages;
using LLMW.Writing.Domain.ProjectPackages;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Reconcile;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.Infrastructure.ProjectPackages;

/// <summary>
/// Core-composed implementation of the frozen snapshot protocol. It receives no caller supplied
/// path: both the project root and external package root are trusted composition values.
/// </summary>
public sealed class ProjectPackageStore : IProjectPackageStore
{
    private const int BackupRetentionCount = 5;
    private const string StagingDirectoryName = ".staging";
    private readonly string projectId;
    private readonly string projectRoot;
    private readonly string databasePath;
    private readonly string packageRoot;
    private readonly ProjectPathResolver projectPaths;
    private readonly SqliteDatabaseConnectionFactory connections;
    private readonly SafeProjectFileEnumerator files = new();
    private readonly TimeProvider clock;
    private readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ProjectPackageStore(
        string projectId,
        string projectRoot,
        string databasePath,
        string packageRoot,
        SqliteDatabaseConnectionFactory? connections = null,
        TimeProvider? clock = null)
    {
        if (!Guid.TryParseExact(projectId, "D", out _))
        {
            throw new ArgumentException("Project identity must be a canonical UUID.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        this.projectId = projectId;
        this.projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        this.databasePath = Path.GetFullPath(databasePath);
        this.packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        projectPaths = new ProjectPathResolver(this.projectRoot);
        this.connections = connections ?? new SqliteDatabaseConnectionFactory();
        this.clock = clock ?? TimeProvider.System;
    }

    public ProjectPackageStoreResult Build(ProjectPackageRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request))
        {
            return Fail(ProjectPackageFailureCode.InvalidRequest);
        }

        var stage = string.Empty;
        var leasesInstalled = false;
        var leaseKind = ToLeaseKind(request.Kind);
        try
        {
            EnsurePackageRoot();
            stage = CreateStage(request.OperationId);
            var snapshotDatabase = Path.Combine(stage, "snapshot.db");
            CreateOnlineDatabaseSnapshot(snapshotDatabase, cancellationToken);
            VerifyDatabase(snapshotDatabase);

            if (request.Kind == ProjectPackageKind.Archive)
            {
                ApplyArchiveProjection(snapshotDatabase, request.IncludeHistory);
            }

            var closure = request.Kind == ProjectPackageKind.FinalPackage
                ? ReadFinalPackageInput(snapshotDatabase, request.AcceptedSnapshotId!)
                : new SnapshotInput(ReadAuthorityClosure(snapshotDatabase, request.IncludeHistory), null, null);
            if (closure.Failure is not null)
            {
                return Fail(closure.Failure.Value);
            }

            var digests = closure.BlobDigests;
            InstallLeases(leaseKind, request.OperationId, digests, cancellationToken);
            leasesInstalled = true;

            var payload = Path.Combine(stage, "payload");
            Directory.CreateDirectory(payload);
            if (request.Kind == ProjectPackageKind.FinalPackage)
            {
                BuildFinalPayload(payload, closure.FinalPackage!, cancellationToken);
            }
            else
            {
                BuildProjectPayload(payload, snapshotDatabase, digests, cancellationToken);
            }

            var fileName = BuildFileName(request.Kind, request.OperationId);
            var temporaryPackage = Path.Combine(stage, fileName + ".tmp");
            BuildZip(payload, temporaryPackage, cancellationToken);
            var finalPath = Path.Combine(packageRoot, fileName);
            Publish(temporaryPackage, finalPath);
            if (request.Kind == ProjectPackageKind.Backup)
            {
                ApplyBackupRetention(finalPath);
            }

            return new ProjectPackageStoreResult(
                new ProjectPackageResult(request.Kind, request.OperationId, fileName, clock.GetUtcNow(), digests.Count),
                null);
        }
        catch (OperationCanceledException)
        {
            return Fail(ProjectPackageFailureCode.StorageFailure);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or
                                          InvalidDataException or CryptographicException or ArgumentException)
        {
            return Fail(ProjectPackageFailureCode.StorageFailure);
        }
        finally
        {
            if (leasesInstalled)
            {
                RemoveLeases(leaseKind, request.OperationId);
            }

            if (!string.IsNullOrEmpty(stage))
            {
                DeleteStage(stage);
            }
        }
    }

    public ProjectPackageStoreVerification VerifyFinalPackage(string packageId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(packageId, "D", out _))
        {
            return VerifyFail(ProjectPackageFailureCode.PackageNotFound);
        }

        try
        {
            var packagePath = Path.Combine(packageRoot, BuildFileName(ProjectPackageKind.FinalPackage, packageId));
            if (!File.Exists(packagePath))
            {
                return VerifyFail(ProjectPackageFailureCode.PackageNotFound);
            }

            using var archive = ZipFile.OpenRead(packagePath);
            var manifestEntry = archive.GetEntry("final-package-manifest.json");
            if (manifestEntry is null)
            {
                return new ProjectPackageStoreVerification(
                    new ProjectPackageVerification(packageId, FinalPackageVerificationStatus.ModifiedAfterFinalAcceptance, "manifest_missing"),
                    null);
            }

            FinalPackageManifest? manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<FinalPackageManifest>(stream, json);
            }

            if (manifest is null || manifest.Validate() != FinalPackageManifestValidation.Valid)
            {
                return new ProjectPackageStoreVerification(
                    new ProjectPackageVerification(packageId, FinalPackageVerificationStatus.ModifiedAfterFinalAcceptance, "manifest_invalid"),
                    null);
            }

            var expectedEntries = manifest.LogicalFiles
                .Select(file => file.LogicalPath)
                .Append("final-package-manifest.json")
                .ToHashSet(StringComparer.Ordinal);
            if (archive.Entries.Count != expectedEntries.Count ||
                archive.Entries.Any(entry => !expectedEntries.Contains(entry.FullName)) ||
                expectedEntries.Any(expected => archive.Entries.Count(entry => StringComparer.Ordinal.Equals(entry.FullName, expected)) != 1))
            {
                return new ProjectPackageStoreVerification(
                    new ProjectPackageVerification(packageId, FinalPackageVerificationStatus.ModifiedAfterFinalAcceptance, "logical_file_list_mismatch"),
                    null);
            }

            foreach (var file in manifest.LogicalFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(file.LogicalPath);
                if (entry is null || !StringComparer.Ordinal.Equals(Hash(entry.Open(), cancellationToken), file.ContentDigest))
                {
                    return new ProjectPackageStoreVerification(
                        new ProjectPackageVerification(packageId, FinalPackageVerificationStatus.ModifiedAfterFinalAcceptance, "content_digest_mismatch"),
                        null);
                }
            }

            return new ProjectPackageStoreVerification(
                new ProjectPackageVerification(packageId, FinalPackageVerificationStatus.Verified, null),
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException)
        {
            return new ProjectPackageStoreVerification(
                new ProjectPackageVerification(packageId, FinalPackageVerificationStatus.Unavailable, "package_unavailable"),
                null);
        }
    }

    private bool IsValidRequest(ProjectPackageRequest request) =>
        request is not null &&
        StringComparer.Ordinal.Equals(request.ProjectId, projectId) &&
        Guid.TryParseExact(request.OperationId, "D", out _) &&
        Enum.IsDefined(request.Kind) &&
        (!(request.Kind == ProjectPackageKind.FinalPackage) || Guid.TryParseExact(request.AcceptedSnapshotId, "D", out _));

    private void EnsurePackageRoot()
    {
        Directory.CreateDirectory(packageRoot);
        if ((File.GetAttributes(packageRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Package root may not be a reparse point.");
        }

        Directory.CreateDirectory(Path.Combine(packageRoot, StagingDirectoryName));
    }

    private string CreateStage(string packageId)
    {
        var stageRoot = Path.GetFullPath(Path.Combine(packageRoot, StagingDirectoryName));
        var stage = Path.GetFullPath(Path.Combine(stageRoot, packageId));
        if (!stage.StartsWith(stageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Package staging path escaped the trusted root.");
        }

        Directory.CreateDirectory(stage);
        return stage;
    }

    private void CreateOnlineDatabaseSnapshot(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var targetBuilder = new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(targetBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void VerifyDatabase(string snapshotDatabase)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotDatabase,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!StringComparer.Ordinal.Equals(Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "ok"))
        {
            throw new InvalidDataException("SQLite snapshot integrity check failed.");
        }
    }

    private static void ApplyArchiveProjection(string snapshotDatabase, bool includeHistory)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotDatabase,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM workflow_runs;" +
            (includeHistory ? string.Empty : "DELETE FROM history_entries;") +
            "DELETE FROM authority_provenance_stubs " +
            "WHERE provenance_stub_id NOT IN (SELECT reviewer_provenance_stub_id FROM review_attempts WHERE reviewer_provenance_stub_id IS NOT NULL);";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static SnapshotInput ReadFinalPackageInput(string snapshotDatabase, string acceptedSnapshotId)
    {
        using var connection = OpenReadOnly(snapshotDatabase);
        using var accepted = connection.CreateCommand();
        accepted.CommandText =
            "SELECT accepted_snapshot_id,storyline_id,accepted_version,accepted_at_ms,final_review_id,warnings_digest " +
            "FROM accepted_snapshots WHERE accepted_snapshot_id=$snapshot_id;";
        accepted.Parameters.AddWithValue("$snapshot_id", acceptedSnapshotId);
        using var reader = accepted.ExecuteReader();
        if (!reader.Read())
        {
            return new SnapshotInput([], null, ProjectPackageFailureCode.AcceptedSnapshotNotFound);
        }

        var snapshot = new AcceptedSnapshotInput(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
        var logicalFiles = new List<FinalPackageSourceFile>();
        using var manuscripts = connection.CreateCommand();
        manuscripts.CommandText =
            "SELECT c.ordinal,c.chapter_id,m.artifact_digest " +
            "FROM manuscript_revisions m " +
            "JOIN chapters c ON c.chapter_id=m.chapter_id " +
            "WHERE c.storyline_id=$storyline_id AND m.accepted_at_ms <= $accepted_at_ms " +
            "AND NOT EXISTS (SELECT 1 FROM manuscript_revisions newer " +
            "WHERE newer.chapter_id=m.chapter_id AND newer.accepted_at_ms <= $accepted_at_ms " +
            "AND (newer.accepted_at_ms > m.accepted_at_ms OR " +
            "(newer.accepted_at_ms=m.accepted_at_ms AND newer.revision_id > m.revision_id))) " +
            "ORDER BY c.ordinal,c.chapter_id;";
        manuscripts.Parameters.AddWithValue("$storyline_id", snapshot.StorylineId);
        manuscripts.Parameters.AddWithValue("$accepted_at_ms", snapshot.AcceptedAt.ToUnixTimeMilliseconds());
        using var manuscriptReader = manuscripts.ExecuteReader();
        while (manuscriptReader.Read())
        {
            var digest = manuscriptReader.GetString(2);
            if (!FinalPackageManifest.IsSha256(digest))
            {
                return new SnapshotInput([], null, ProjectPackageFailureCode.StorageFailure);
            }

            logicalFiles.Add(new FinalPackageSourceFile(
                $"manuscript/{manuscriptReader.GetInt64(0):D4}-{manuscriptReader.GetString(1)}.md",
                digest));
        }

        if (logicalFiles.Count == 0)
        {
            return new SnapshotInput([], null, ProjectPackageFailureCode.AcceptedSnapshotInvalid);
        }

        if (snapshot.WarningsDigest is not null && !FinalPackageManifest.IsSha256(snapshot.WarningsDigest))
        {
            return new SnapshotInput([], null, ProjectPackageFailureCode.StorageFailure);
        }

        return new SnapshotInput(
            logicalFiles.Select(file => file.Digest).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            new FinalPackageInput(snapshot, logicalFiles),
            null);
    }

    private static string[] ReadAuthorityClosure(string snapshotDatabase, bool includeHistory)
    {
        using var connection = OpenReadOnly(snapshotDatabase);
        const string sql =
            "SELECT artifact_digest FROM candidates UNION " +
            "SELECT artifact_digest FROM manuscript_revisions UNION " +
            "SELECT manifest_digest FROM accepted_snapshots UNION " +
            "SELECT warnings_digest FROM accepted_snapshots WHERE warnings_digest IS NOT NULL UNION " +
            "SELECT before_digest FROM narrative_changes WHERE before_digest IS NOT NULL UNION " +
            "SELECT after_payload_digest FROM narrative_changes WHERE after_payload_digest IS NOT NULL UNION " +
            "SELECT snapshot_digest FROM narrative_state_revisions";
        var digests = ReadDigests(connection, sql).ToList();
        if (includeHistory)
        {
            digests.AddRange(ReadDigests(connection, "SELECT artifact_digest FROM history_entries"));
        }

        return digests.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> ReadDigests(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var digest = reader.GetString(0);
            if (!FinalPackageManifest.IsSha256(digest))
            {
                throw new InvalidDataException("Snapshot closure contains an invalid blob digest.");
            }

            yield return digest.ToLowerInvariant();
        }
    }

    private void InstallLeases(string kind, string snapshotId, IReadOnlyList<string> digests, CancellationToken cancellationToken)
    {
        if (digests.Count == 0)
        {
            return;
        }

        using var connection = connections.OpenConfigured(databasePath);
        using var transaction = connection.BeginTransaction();
        foreach (var digest in digests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO snapshot_blob_leases(lease_id,snapshot_kind,snapshot_id,blob_digest,expires_at_ms,created_at_ms) " +
                "VALUES($lease_id,$kind,$snapshot_id,$digest,$expires,$created);";
            AddParameter(command, "$lease_id", Guid.NewGuid().ToString("D"));
            AddParameter(command, "$kind", kind);
            AddParameter(command, "$snapshot_id", snapshotId);
            AddParameter(command, "$digest", digest);
            AddParameter(command, "$expires", clock.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds());
            AddParameter(command, "$created", clock.GetUtcNow().ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void RemoveLeases(string kind, string snapshotId)
    {
        try
        {
            using var connection = connections.OpenConfigured(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM snapshot_blob_leases WHERE snapshot_kind=$kind AND snapshot_id=$snapshot_id;";
            AddParameter(command, "$kind", kind);
            AddParameter(command, "$snapshot_id", snapshotId);
            command.ExecuteNonQuery();
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            // Expiry remains the crash-hygiene fallback; a package must never be unpublished because
            // best-effort cleanup encounters a transient database failure after publication.
        }
    }

    private void BuildProjectPayload(string payload, string snapshotDatabase, IReadOnlyList<string> digests, CancellationToken cancellationToken)
    {
        CopyProjectFiles(payload, cancellationToken);
        CopyAndVerify(snapshotDatabase, Path.Combine(payload, "authority", "project.db"), cancellationToken);
        foreach (var digest in digests)
        {
            var source = projectPaths.Resolve($".llmw/objects/{digest[..2]}/{digest[2..]}");
            if (!File.Exists(source))
            {
                throw new InvalidDataException("A snapshot-reachable blob is missing.");
            }

            CopyAndVerify(source, Path.Combine(payload, "objects", digest[..2], digest[2..]), cancellationToken, digest);
        }
    }

    private void BuildFinalPayload(string payload, FinalPackageInput input, CancellationToken cancellationToken)
    {
        var manifestFiles = new List<FinalPackageManifestFile>();
        foreach (var sourceFile in input.LogicalFiles.OrderBy(file => file.LogicalPath, StringComparer.Ordinal))
        {
            var source = projectPaths.Resolve($".llmw/objects/{sourceFile.Digest[..2]}/{sourceFile.Digest[2..]}");
            if (!File.Exists(source))
            {
                throw new InvalidDataException("A final-package artifact is missing.");
            }

            CopyAndVerify(source, Path.Combine(payload, sourceFile.LogicalPath.Replace('/', Path.DirectorySeparatorChar)), cancellationToken, sourceFile.Digest);
            manifestFiles.Add(new FinalPackageManifestFile(sourceFile.LogicalPath, sourceFile.Digest));
        }

        var manifest = new FinalPackageManifest(
            FinalPackageManifest.CurrentManifestVersion,
            input.Snapshot.SnapshotId,
            input.Snapshot.StorylineId,
            input.Snapshot.AcceptedVersion,
            input.Snapshot.AcceptedAt,
            input.Snapshot.FinalReviewId,
            input.Snapshot.WarningsDigest,
            manifestFiles).Canonicalize();
        if (manifest.Validate() != FinalPackageManifestValidation.Valid)
        {
            throw new InvalidDataException("Final package manifest is invalid.");
        }

        var manifestPath = Path.Combine(payload, "final-package-manifest.json");
        Directory.CreateDirectory(payload);
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, json));
    }

    private void CopyProjectFiles(string payload, CancellationToken cancellationToken)
    {
        foreach (var source in files.EnumerateFiles(projectRoot, cancellationToken))
        {
            var relative = projectPaths.FromFullPath(source);
            if (relative.StartsWith(".llmw/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyAndVerify(source, Path.Combine(payload, "project", relative.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
        }
    }

    private static void CopyAndVerify(string source, string destination, CancellationToken cancellationToken, string? expectedDigest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Snapshot sources may not be reparse points.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new IOException("Package destination is invalid."));
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        var digest = HashFile(destination, cancellationToken);
        if (expectedDigest is not null && !StringComparer.Ordinal.Equals(digest, expectedDigest))
        {
            throw new InvalidDataException("Snapshot file digest mismatch.");
        }
    }

    private static void BuildZip(string payload, string target, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var source in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(payload, source).Replace('\\', '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static void Publish(string temporaryPackage, string finalPath)
    {
        if (File.Exists(finalPath))
        {
            throw new IOException("A package already exists for this operation identity.");
        }

        File.Move(temporaryPackage, finalPath, overwrite: false);
    }

    private void ApplyBackupRetention(string publishedBackup)
    {
        var protectedPath = Path.GetFullPath(publishedBackup);
        var backups = Directory.EnumerateFiles(packageRoot, "backup-*.zip", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ThenByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (var path in backups.Skip(BackupRetentionCount))
        {
            if (!path.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                StringComparer.Ordinal.Equals(path, protectedPath))
            {
                continue;
            }

            File.Delete(path);
        }
    }

    private void DeleteStage(string stage)
    {
        try
        {
            var stageRoot = Path.GetFullPath(Path.Combine(packageRoot, StagingDirectoryName));
            var fullStage = Path.GetFullPath(stage);
            if (fullStage.StartsWith(stageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullStage))
            {
                Directory.Delete(fullStage, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string ToLeaseKind(ProjectPackageKind kind) => kind switch
    {
        ProjectPackageKind.Backup => "backup",
        ProjectPackageKind.Archive => "archive",
        _ => "final_package"
    };

    private static string BuildFileName(ProjectPackageKind kind, string packageId) => kind switch
    {
        ProjectPackageKind.Backup => "backup-" + packageId + ".zip",
        ProjectPackageKind.Archive => "archive-" + packageId + ".zip",
        _ => "final-" + packageId + ".zip"
    };

    private static string Hash(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            sha.AppendData(buffer, 0, read);
        }
    }

    private static string HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        return Hash(stream, cancellationToken);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static ProjectPackageStoreResult Fail(ProjectPackageFailureCode code) =>
        new(null, new ProjectPackageFailure(code));

    private static ProjectPackageStoreVerification VerifyFail(ProjectPackageFailureCode code) =>
        new(null, new ProjectPackageFailure(code));

    private sealed record SnapshotInput(
        IReadOnlyList<string> BlobDigests,
        FinalPackageInput? FinalPackage,
        ProjectPackageFailureCode? Failure);

    private sealed record AcceptedSnapshotInput(
        string SnapshotId,
        string StorylineId,
        string AcceptedVersion,
        DateTimeOffset AcceptedAt,
        string? FinalReviewId,
        string? WarningsDigest);

    private sealed record FinalPackageInput(
        AcceptedSnapshotInput Snapshot,
        IReadOnlyList<FinalPackageSourceFile> LogicalFiles);

    private sealed record FinalPackageSourceFile(string LogicalPath, string Digest);
}
