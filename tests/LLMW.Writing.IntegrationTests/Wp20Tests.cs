using System.IO.Compression;
using LLMW.Writing.Application.ProjectPackages;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Infrastructure.ProjectPackages;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private const string Wp20ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";

    private static void RunWp20Tests()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP20.Integration", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP20.IntegrationPackages", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".llmw"));
            Directory.CreateDirectory(Path.Combine(root, "Manuscript", "current"));
            File.WriteAllText(Path.Combine(root, "project.llmw.json"),
                "{\"projectId\":\"" + Wp20ProjectId + "\",\"formatVersion\":1,\"schemaVersion\":1}");
            File.WriteAllText(Path.Combine(root, "Manuscript", "current", "sample.md"), "snapshot content");
            var databasePath = Path.Combine(root, ".llmw", "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp20-integration", 1735689600000);

            var service = new ProjectPackageService(
                new ProjectPackageStore(Wp20ProjectId, root, databasePath, packageRoot),
                Wp20ProjectId);
            var principal = new TrustedNativePrincipalSource("wp20-integration").ResolveUserInteractive();
            var operationId = Guid.NewGuid().ToString("D");
            var result = service.Create(
                principal,
                explicitlyUserInitiated: true,
                new ProjectPackageRequest(ProjectPackageKind.Backup, Wp20ProjectId, operationId));
            AssertTrue(result.Succeeded, "WP20 authenticated Application-to-Infrastructure backup failed.");
            var package = Path.Combine(packageRoot, result.Value!.FileName);
            AssertTrue(File.Exists(package), "WP20 backup was not atomically published to the external package root.");
            using var archive = ZipFile.OpenRead(package);
            AssertTrue(archive.GetEntry("authority/project.db") is not null &&
                       archive.GetEntry("project/project.llmw.json") is not null,
                "WP20 backup did not contain the required consistent snapshot and descriptor.");
            Console.WriteLine("WP20 integration package test passed.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, recursive: true);
        }
    }
}
