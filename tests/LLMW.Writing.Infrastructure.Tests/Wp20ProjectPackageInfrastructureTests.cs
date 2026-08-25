using System.IO.Compression;
using System.Security.Cryptography;
using LLMW.Writing.Application.ProjectPackages;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.ProjectPackages;
using Microsoft.Data.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private const string Wp20ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";
    private const string Wp20StorylineId = "018f3e78-1234-7abc-8def-0123456789ae";
    private const string Wp20ChapterId = "018f3e78-1234-7abc-8def-0123456789af";
    private const string Wp20CandidateId = "018f3e78-1234-7abc-8def-0123456789b0";
    private const string Wp20RevisionId = "018f3e78-1234-7abc-8def-0123456789b1";
    private const string Wp20AcceptedSnapshotId = "018f3e78-1234-7abc-8def-0123456789b2";
    private const string Wp20ReviewId = "018f3e78-1234-7abc-8def-0123456789b3";
    private const string Wp20StubId = "018f3e78-1234-7abc-8def-0123456789b4";

    private static void RunWp20ProjectPackageInfrastructureTests()
    {
        Run(nameof(BackupUsesConsistentSnapshotClosureAndCleansLeases), BackupUsesConsistentSnapshotClosureAndCleansLeases);
        Run(nameof(ArchiveExcludesHistoryButPreservesAuthorityProvenance), ArchiveExcludesHistoryButPreservesAuthorityProvenance);
        Run(nameof(FinalPackageVerifiesAndFlagsExternalModification), FinalPackageVerifiesAndFlagsExternalModification);
        Run(nameof(BackupRetentionKeepsFivePublishedBackups), BackupRetentionKeepsFivePublishedBackups);
    }

    private static void BackupUsesConsistentSnapshotClosureAndCleansLeases()
    {
        using var project = Wp20Project.Create();
        var store = project.CreateStore();
        var operation = Guid.NewGuid().ToString("D");
        var result = store.Build(new ProjectPackageRequest(ProjectPackageKind.Backup, Wp20ProjectId, operation));

        AssertTrue(result.Succeeded, "Backup package creation failed.");
        AssertEqual(2, result.Value!.ReachableBlobCount, "Backup closure should include candidate and accepted manifest blobs only.");
        using var archive = ZipFile.OpenRead(project.PackagePath(result.Value.FileName));
        AssertTrue(archive.GetEntry("authority/project.db") is not null, "Backup omitted the SQLite consistent snapshot.");
        AssertTrue(archive.GetEntry("project/project.llmw.json") is not null, "Backup omitted the project descriptor.");
        AssertTrue(archive.GetEntry("project/Manuscript/current/chapter.md") is not null, "Backup omitted durable project files.");
        AssertTrue(archive.GetEntry("objects/" + project.ArtifactDigest[..2] + "/" + project.ArtifactDigest[2..]) is not null,
            "Backup omitted an Authority-reachable blob.");
        AssertEqual(0L, project.Scalar("SELECT COUNT(*) FROM snapshot_blob_leases;"),
            "Snapshot leases were not cleaned after successful publication.");
    }

    private static void ArchiveExcludesHistoryButPreservesAuthorityProvenance()
    {
        using var project = Wp20Project.Create();
        var result = project.CreateStore().Build(new ProjectPackageRequest(
            ProjectPackageKind.Archive, Wp20ProjectId, Guid.NewGuid().ToString("D"), IncludeHistory: false));
        AssertTrue(result.Succeeded, "Archive package creation failed.");
        using var archive = ZipFile.OpenRead(project.PackagePath(result.Value!.FileName));
        AssertTrue(archive.GetEntry("objects/" + project.HistoryDigest[..2] + "/" + project.HistoryDigest[2..]) is null,
            "Default Archive incorrectly included history-only blobs.");
        var projectedDatabase = project.CopyEntryToTemporaryFile(archive, "authority/project.db");
        try
        {
            AssertEqual(0L, Wp20Project.Scalar(projectedDatabase, "SELECT COUNT(*) FROM history_entries;"),
                "Default Archive retained Local History records.");
            AssertEqual(1L, Wp20Project.Scalar(projectedDatabase, "SELECT COUNT(*) FROM authority_provenance_stubs;"),
                "Archive removed the provenance stub referenced by Authority review history.");
        }
        finally
        {
            File.Delete(projectedDatabase);
        }

        var withHistory = project.CreateStore().Build(new ProjectPackageRequest(
            ProjectPackageKind.Archive, Wp20ProjectId, Guid.NewGuid().ToString("D"), IncludeHistory: true));
        AssertTrue(withHistory.Succeeded, "Archive with explicit history inclusion failed.");
        using var historyArchive = ZipFile.OpenRead(project.PackagePath(withHistory.Value!.FileName));
        AssertTrue(historyArchive.GetEntry("objects/" + project.HistoryDigest[..2] + "/" + project.HistoryDigest[2..]) is not null,
            "Archive with explicit history inclusion omitted history-only blobs.");
    }

    private static void FinalPackageVerifiesAndFlagsExternalModification()
    {
        using var project = Wp20Project.Create();
        var operation = Guid.NewGuid().ToString("D");
        var store = project.CreateStore();
        var result = store.Build(new ProjectPackageRequest(
            ProjectPackageKind.FinalPackage,
            Wp20ProjectId,
            operation,
            AcceptedSnapshotId: Wp20AcceptedSnapshotId));
        AssertTrue(result.Succeeded, "Final Package creation failed.");
        var verified = store.VerifyFinalPackage(operation);
        AssertTrue(verified.Succeeded, "Final Package verification failed unexpectedly.");
        AssertTrue(verified.Value!.Status == FinalPackageVerificationStatus.Verified,
            "Unmodified Final Package was not verified.");

        var path = project.PackagePath(result.Value!.FileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manuscript/0001-" + Wp20ChapterId + ".md")
                ?? throw new InvalidOperationException("Final Package manuscript entry is missing.");
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, leaveOpen: false);
            writer.Write("tampered");
        }

        var modified = store.VerifyFinalPackage(operation);
        AssertTrue(modified.Succeeded, "Modified Final Package verification did not return a result.");
        AssertTrue(modified.Value!.Status == FinalPackageVerificationStatus.ModifiedAfterFinalAcceptance,
            "Modified Final Package was not marked as modified after final acceptance.");
        AssertTrue(File.Exists(path), "Verification removed a modified but still usable Final Package.");
    }

    private static void BackupRetentionKeepsFivePublishedBackups()
    {
        using var project = Wp20Project.Create();
        var store = project.CreateStore();
        for (var index = 0; index < 6; index++)
        {
            var result = store.Build(new ProjectPackageRequest(
                ProjectPackageKind.Backup, Wp20ProjectId, Guid.NewGuid().ToString("D")));
            AssertTrue(result.Succeeded, "Backup for retention test failed.");
        }

        AssertEqual(5, Directory.EnumerateFiles(project.PackageRoot, "backup-*.zip", SearchOption.TopDirectoryOnly).Count(),
            "Backup rotation did not retain exactly five published backups.");
    }

    private sealed class Wp20Project : IDisposable
    {
        private Wp20Project(string root, string packageRoot, string artifactDigest, string manifestDigest, string historyDigest)
        {
            Root = root;
            PackageRoot = packageRoot;
            ArtifactDigest = artifactDigest;
            ManifestDigest = manifestDigest;
            HistoryDigest = historyDigest;
            DatabasePath = Path.Combine(root, ".llmw", "project.db");
        }

        public string Root { get; }
        public string PackageRoot { get; }
        public string DatabasePath { get; }
        public string ArtifactDigest { get; }
        public string ManifestDigest { get; }
        public string HistoryDigest { get; }

        public static Wp20Project Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP20", Guid.NewGuid().ToString("N"));
            var packageRoot = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP20.Packages", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".llmw"));
            Directory.CreateDirectory(Path.Combine(root, "Manuscript", "current"));
            File.WriteAllText(Path.Combine(root, "Manuscript", "current", "chapter.md"), "current projection");
            File.WriteAllText(Path.Combine(root, "project.llmw.json"),
                "{\"projectId\":\"" + Wp20ProjectId + "\",\"formatVersion\":1,\"schemaVersion\":1}");
            var databasePath = Path.Combine(root, ".llmw", "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp20-tests", 1735689600000);
            var blobs = new ImmutableBlobStore(root);
            var artifact = Stage(blobs, "accepted manuscript");
            var manifest = Stage(blobs, "accepted manifest");
            var history = Stage(blobs, "local history");
            var project = new Wp20Project(root, packageRoot, artifact, manifest, history);
            project.Seed();
            return project;
        }

        public ProjectPackageStore CreateStore() =>
            new(Wp20ProjectId, Root, DatabasePath, PackageRoot);

        public string PackagePath(string fileName) => Path.Combine(PackageRoot, fileName);

        public long Scalar(string sql) => Scalar(DatabasePath, sql);

        public string CopyEntryToTemporaryFile(ZipArchive archive, string entryName)
        {
            var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException("Archive entry is missing: " + entryName);
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db");
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            if (Directory.Exists(PackageRoot))
            {
                Directory.Delete(PackageRoot, recursive: true);
            }
        }

        public static long Scalar(string databasePath, string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private void Seed()
        {
            using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(DatabasePath);
            Execute(connection, "INSERT INTO objects(object_id,object_type,schema_version,status,created_at_ms,updated_at_ms) VALUES ('" + Wp20StorylineId + "','storyline',1,'current',1,1);");
            Execute(connection, "INSERT INTO objects(object_id,object_type,schema_version,status,created_at_ms,updated_at_ms) VALUES ('" + Wp20ChapterId + "','chapter',1,'current',1,1);");
            Execute(connection, "INSERT INTO storylines(storyline_id,workflow_state,updated_at_ms) VALUES ('" + Wp20StorylineId + "','final_accepted',1);");
            Execute(connection, "INSERT INTO chapters(chapter_id,storyline_id,ordinal,workflow_state,updated_at_ms) VALUES ('" + Wp20ChapterId + "','" + Wp20StorylineId + "',1,'accepted',1);");
            Execute(connection, "INSERT INTO candidates(candidate_id,chapter_id,submission_kind,source_draft_path,artifact_digest,status,created_at_ms,updated_at_ms) VALUES ('" + Wp20CandidateId + "','" + Wp20ChapterId + "','manual','Draft/chapter.md','" + ArtifactDigest + "','accepted',1,1);");
            Execute(connection, "INSERT INTO manuscript_revisions(revision_id,chapter_id,candidate_id,artifact_digest,transaction_id,materialization_status,accepted_at_ms,created_at_ms) VALUES ('" + Wp20RevisionId + "','" + Wp20ChapterId + "','" + Wp20CandidateId + "','" + ArtifactDigest + "','transaction-20','complete',10,10);");
            Execute(connection, "INSERT INTO accepted_snapshots(accepted_snapshot_id,storyline_id,accepted_version,manifest_digest,transaction_id,accepted_at_ms) VALUES ('" + Wp20AcceptedSnapshotId + "','" + Wp20StorylineId + "','v1.0','" + ManifestDigest + "','transaction-20',20);");
            Execute(connection, "INSERT INTO authority_provenance_stubs(provenance_stub_id,run_id,role,created_at_ms) VALUES ('" + Wp20StubId + "','run-20','reviewer',1);");
            Execute(connection, "INSERT INTO review_attempts(review_attempt_id,scope_kind,scope_id,review_kind,candidate_id,attempt_no,reviewer_provenance_stub_id,status,started_at_ms) VALUES ('" + Wp20ReviewId + "','candidate','" + Wp20CandidateId + "','final','" + Wp20CandidateId + "',1,'" + Wp20StubId + "','passed',1);");
            Execute(connection, "INSERT INTO history_entries(history_entry_id,relative_path,artifact_digest,source_kind,created_at_ms) VALUES ('history-20','Draft/chapter.md','" + HistoryDigest + "','autosave',1);");
        }

        private static string Stage(ImmutableBlobStore blobs, string content)
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            return blobs.Stage(stream).Digest;
        }

        private static void Execute(System.Data.Common.DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
