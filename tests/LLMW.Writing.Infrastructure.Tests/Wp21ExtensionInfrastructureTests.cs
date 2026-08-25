using LLMW.Writing.Application.Extensions;
using LLMW.Writing.Domain.Extensions;
using LLMW.Writing.Infrastructure.Extensions;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp21ExtensionInfrastructureTests()
    {
        Run(nameof(ExtensionDiscoveryIsScopedDeterministicAndHashesExecutableContent), ExtensionDiscoveryIsScopedDeterministicAndHashesExecutableContent);
        Run(nameof(AgentsInheritanceStaysInsideProjectAndPrefersAgentsOverClaude), AgentsInheritanceStaysInsideProjectAndPrefersAgentsOverClaude);
        Run(nameof(TrustStateIsAtomicPerUserAndBoundToProjectLocation), TrustStateIsAtomicPerUserAndBoundToProjectLocation);
        Run(nameof(TrustRecoveryIgnoresPartialPublishAndFailsClosedOnCorruption), TrustRecoveryIgnoresPartialPublishAndFailsClosedOnCorruption);
    }

    private static void ExtensionDiscoveryIsScopedDeterministicAndHashesExecutableContent()
    {
        using var fixture = ExtensionFixture.Create();
        ExtensionFixture.WriteManifest(fixture.ApplicationExtensions, "writer", "app instruction");
        ExtensionFixture.WriteManifest(fixture.UserExtensions, "writer", "user instruction");
        var projectExtension = ExtensionFixture.WriteManifest(fixture.ProjectExtensions, "writer", "project instruction", scripts: ["scripts/run.ps1"]);
        Directory.CreateDirectory(Path.Combine(projectExtension, "scripts"));
        File.WriteAllText(Path.Combine(projectExtension, "scripts", "run.ps1"), "Write-Output 'first'");
        ExtensionFixture.WriteManifest(fixture.ProjectExtensions, "research", "research instruction");
        ExtensionFixture.WriteRawManifest(fixture.ProjectExtensions, "unsafe", """
            {"kind":"mcp","name":"unsafe","version":"1.0.0","description":"unsafe","instructions":"x","scripts":["../escape.ps1"],"requestedPermissions":[],"dependencies":[]}
            """);

        var catalog = fixture.CreateCatalog();
        var first = catalog.Discover();
        AssertEqual("project instruction", first.Catalog.Extensions.Single(item => item.Id == "skill:writer").Manifest.Instructions!,
            "Project Skill did not override User/Application descriptors.");
        AssertEqual(2, first.Catalog.Extensions.Count, "Invalid script path was accepted as an extension descriptor.");
        AssertTrue(first.Catalog.Diagnostics.Any(item => item.Code == "EXTENSION_MANIFEST_INVALID"),
            "Unsafe manifest did not produce a safe diagnostic.");
        var digest = first.Catalog.Extensions.Single(item => item.Id == "skill:writer").ContentDigest;

        File.WriteAllText(Path.Combine(projectExtension, "scripts", "run.ps1"), "Write-Output 'changed'");
        var changed = catalog.Discover().Catalog.Extensions.Single(item => item.Id == "skill:writer").ContentDigest;
        AssertTrue(!StringComparer.Ordinal.Equals(digest, changed),
            "Changing a declared script did not change the activation invalidation digest.");
    }

    private static void AgentsInheritanceStaysInsideProjectAndPrefersAgentsOverClaude()
    {
        using var fixture = ExtensionFixture.Create();
        File.WriteAllText(Path.Combine(fixture.ProjectRoot, FileExtensionCatalog.AgentsFileName), "root agents");
        File.WriteAllText(Path.Combine(fixture.ProjectRoot, FileExtensionCatalog.ClaudeFileName), "root claude");
        var child = Path.Combine(fixture.ProjectRoot, "Draft");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, FileExtensionCatalog.AgentsFileName), "child agents");
        var catalog = fixture.CreateCatalog();
        var result = catalog.DiscoverForProjectPath("Draft/chapter.md");

        AssertEqual("root agents,child agents", string.Join(',', result.Instructions.Instructions),
            "AGENTS inheritance must compose root → child and win over CLAUDE at the same scope.");
        AssertTrue(result.Instructions.Diagnostics.Contains("AGENTS_CLAUDE_CONFLICT", StringComparer.Ordinal),
            "AGENTS/CLAUDE same-scope precedence omitted its required diagnostic.");
        AssertTrue(result.Instructions.AgentsDigest.Length == 64,
            "AGENTS digest must be a safe SHA-256 provenance value.");
        var denied = catalog.DiscoverForProjectPath("../outside.md");
        AssertTrue(denied.Instructions.Diagnostics.Contains("AGENTS_PATH_DENIED", StringComparer.Ordinal),
            "Instruction reference escaped Project Root.");
    }

    private static void TrustStateIsAtomicPerUserAndBoundToProjectLocation()
    {
        using var fixture = ExtensionFixture.Create();
        var state = new ExtensionSecurityState(
            true,
            new Dictionary<string, ExtensionActivationRecord>
            {
                ["mcp:research"] = new(ExtensionActivationState.Active, new string('a', 64))
            },
            new Dictionary<string, ExtensionOperationReceipt>
            {
                ["018f3e78-1234-7abc-8def-0123456789ae"] = new("activate\u001fmcp:research", "mcp:research", true, true)
            });
        var store = new FileExtensionSecurityStateStore(fixture.StateRoot, ExtensionFixture.ProjectId, fixture.ProjectRoot);
        store.Save(state);
        var reloaded = new FileExtensionSecurityStateStore(fixture.StateRoot, ExtensionFixture.ProjectId, fixture.ProjectRoot).Load();
        AssertTrue(reloaded.ProjectTrusted, "Atomic local trust state lost Project Trust.");
        AssertTrue(reloaded.Activations.TryGetValue("mcp:research", out var activation),
            "Atomic local trust state lost the activation identity.");
        AssertTrue(activation!.State == ExtensionActivationState.Active,
            "Atomic local trust state did not persist the activation state.");

        var cloneRoot = Path.Combine(fixture.Root, "clone");
        Directory.CreateDirectory(cloneRoot);
        var clone = new FileExtensionSecurityStateStore(fixture.StateRoot, ExtensionFixture.ProjectId, cloneRoot).Load();
        AssertTrue(!clone.ProjectTrusted && clone.Activations.Count == 0,
            "Trust leaked from an opened Project to a different project location.");
        var contents = string.Join('\n', Directory.EnumerateFiles(fixture.StateRoot).Select(File.ReadAllText));
        AssertTrue(!contents.Contains(fixture.ProjectRoot, StringComparison.OrdinalIgnoreCase) &&
                   !contents.Contains("token", StringComparison.OrdinalIgnoreCase),
            "Trust persistence exposed a project path or credential material.");
    }

    private static void TrustRecoveryIgnoresPartialPublishAndFailsClosedOnCorruption()
    {
        using var fixture = ExtensionFixture.Create();
        var state = new ExtensionSecurityState(
            true,
            new Dictionary<string, ExtensionActivationRecord>(),
            new Dictionary<string, ExtensionOperationReceipt>());
        var store = new FileExtensionSecurityStateStore(fixture.StateRoot, ExtensionFixture.ProjectId, fixture.ProjectRoot);
        store.Save(state);
        var statePath = Directory.EnumerateFiles(fixture.StateRoot, "*.json", SearchOption.TopDirectoryOnly).Single();
        File.WriteAllText(statePath + ".interrupted.tmp", "partial-state");
        AssertTrue(new FileExtensionSecurityStateStore(fixture.StateRoot, ExtensionFixture.ProjectId, fixture.ProjectRoot).Load().ProjectTrusted,
            "A crash-stage temporary file changed the last atomically published trust decision.");

        File.WriteAllText(statePath, "{not-json");
        var failedClosed = new FileExtensionSecurityStateStore(fixture.StateRoot, ExtensionFixture.ProjectId, fixture.ProjectRoot).Load();
        AssertTrue(!failedClosed.ProjectTrusted && failedClosed.Activations.Count == 0,
            "Corrupt activation state did not fail closed to untrusted/inactive.");
    }

    private sealed class ExtensionFixture : IDisposable
    {
        private ExtensionFixture(string root)
        {
            Root = root;
            ApplicationExtensions = Path.Combine(root, "application");
            UserExtensions = Path.Combine(root, "user");
            ProjectRoot = Path.Combine(root, "project");
            ProjectExtensions = Path.Combine(ProjectRoot, "Extensions");
            StateRoot = Path.Combine(root, "state");
            Directory.CreateDirectory(ApplicationExtensions);
            Directory.CreateDirectory(UserExtensions);
            Directory.CreateDirectory(ProjectExtensions);
            Directory.CreateDirectory(StateRoot);
        }

        public string Root { get; }

        public string ApplicationExtensions { get; }

        public string UserExtensions { get; }

        public string ProjectRoot { get; }

        public string ProjectExtensions { get; }

        public string StateRoot { get; }

        public const string ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";

        public static ExtensionFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP21.Infrastructure", Guid.NewGuid().ToString("N"));
            return new ExtensionFixture(root);
        }

        public FileExtensionCatalog CreateCatalog() => new(new ExtensionCatalogRoots(
            ApplicationExtensions,
            UserExtensions,
            ProjectExtensions,
            ProjectRoot));

        public static string WriteManifest(string scopeRoot, string name, string instructions, IReadOnlyList<string>? scripts = null)
        {
            var directory = Path.Combine(scopeRoot, name);
            Directory.CreateDirectory(directory);
            var scriptJson = scripts is null ? "[]" : "[" + string.Join(',', scripts.Select(script => "\"" + script + "\"")) + "]";
            File.WriteAllText(Path.Combine(directory, FileExtensionCatalog.ManifestFileName),
                "{\"kind\":\"skill\",\"name\":\"" + name + "\",\"version\":\"1.0.0\",\"description\":\"safe\",\"instructions\":\"" + instructions + "\",\"scripts\":" + scriptJson + ",\"requestedPermissions\":[\"ProjectFile.Read\"],\"dependencies\":[]}");
            return directory;
        }

        public static void WriteRawManifest(string scopeRoot, string name, string json)
        {
            var directory = Path.Combine(scopeRoot, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, FileExtensionCatalog.ManifestFileName), json);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
