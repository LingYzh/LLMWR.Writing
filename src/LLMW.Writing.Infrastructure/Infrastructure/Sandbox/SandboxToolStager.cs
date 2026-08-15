using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal static class SandboxToolStager
{
    public static string ResolveLaunchExecutable(
        string trustedProjectRoot,
        string sourceExecutable,
        string workDirectory,
        string appContainerSid,
        ISandboxFaultInjector faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutable);
        var source = Path.GetFullPath(sourceExecutable);
        if (!File.Exists(source))
        {
            throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, "Executable does not exist.");
        }

        if (SandboxPathPolicy.IsWindowsSystemLocation(source))
        {
            return source;
        }

        if (SandboxPathPolicy.IsInside(workDirectory, source))
        {
            var relativeWork = Path.GetRelativePath(trustedProjectRoot, workDirectory)
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (relativeWork.Length == 0 || relativeWork.Any(segment => segment is "." or ".."))
            {
                throw new SandboxLayerException(SandboxError.PathOutOfScope, "Work-directory executable is not under the trusted project root.");
            }

            SafeSandboxHierarchy.VerifyExistingChain(trustedProjectRoot, relativeWork);
            return source;
        }

        var sourceRoot = Path.GetDirectoryName(source)
            ?? throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, "Executable directory is missing.");
        var closure = ResolveClosure(source, sourceRoot);
        if (closure.Count == 0)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Executable dependency closure could not be determined.");
        }

        var files = new List<StagedFile>();
        foreach (var relative in closure.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var segments = SplitRelative(relative);
            var bytes = SafeSandboxHierarchy.ReadFileRelative(sourceRoot, segments);
            files.Add(new StagedFile(relative, bytes));
        }

        var stagingIdentity = ManifestIdentity(files);
        var staging = SafeSandboxHierarchy.EnsureToolStagingDirectory(trustedProjectRoot, stagingIdentity);
        SafeSandboxHierarchy.VerifyExistingChain(
            trustedProjectRoot,
            SandboxPathPolicy.SandboxRootDirectoryName,
            "tools",
            stagingIdentity);

        string? stagedExe = null;
        foreach (var file in files)
        {
            var directorySegments = DirectorySegments(file.Relative);
            var fileName = Path.GetFileName(file.Relative.Replace('/', Path.DirectorySeparatorChar));
            var destSegments = new[] { SandboxPathPolicy.SandboxRootDirectoryName, "tools", stagingIdentity }
                .Concat(directorySegments)
                .ToArray();
            if (directorySegments.Length > 0)
            {
                SafeSandboxHierarchy.EnsureDirectory(trustedProjectRoot, destSegments);
            }

            SafeSandboxHierarchy.WriteFileRelative(
                trustedProjectRoot,
                destSegments,
                fileName,
                file.Bytes);

            var destination = Path.GetFullPath(Path.Combine(staging, file.Relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!SandboxPathPolicy.IsInside(staging, destination))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Staged dependency escaped the Core tool staging directory.");
            }

            SafeSandboxHierarchy.VerifyExistingFile(
                trustedProjectRoot,
                destSegments.Concat([fileName]).ToArray());
            AppContainerAclManager.GrantMinimum(
                destination,
                appContainerSid,
                NativeConstants.SandboxExecuteAccess,
                inherit: false,
                faultInjector);
            if (file.Relative.Equals(Path.GetFileName(source), StringComparison.OrdinalIgnoreCase))
            {
                stagedExe = destination;
            }
        }

        SafeSandboxHierarchy.VerifyExistingChain(
            trustedProjectRoot,
            SandboxPathPolicy.SandboxRootDirectoryName,
            "tools",
            stagingIdentity);
        AppContainerAclManager.GrantMinimum(
            staging,
            appContainerSid,
            NativeConstants.FILE_GENERIC_EXECUTE,
            inherit: false,
            faultInjector);
        return stagedExe ?? throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, "Staged executable path was not produced.");
    }

    internal static IReadOnlyList<string> ResolveClosureForTests(string source) =>
        ResolveClosure(Path.GetFullPath(source), Path.GetDirectoryName(Path.GetFullPath(source)) ?? "");

    internal static string ComputeStagingIdentity(string sourceExecutable)
    {
        var source = Path.GetFullPath(sourceExecutable);
        var sourceRoot = Path.GetDirectoryName(source)
            ?? throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, "Executable directory is missing.");
        var files = new List<StagedFile>();
        foreach (var relative in ResolveClosure(source, sourceRoot).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(new StagedFile(relative, SafeSandboxHierarchy.ReadFileRelative(sourceRoot, SplitRelative(relative))));
        }

        return ManifestIdentity(files);
    }

    private static List<string> ResolveClosure(string source, string sourceRoot)
    {
        var relativeExe = Path.GetFileName(source);
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase) { relativeExe };
        var stem = Path.GetFileNameWithoutExtension(source);
        var runtimeConfig = stem + ".runtimeconfig.json";
        var deps = stem + ".deps.json";
        var companionDll = stem + ".dll";
        var runtimeConfigBytes = TryReadRelative(sourceRoot, runtimeConfig);
        var depsBytes = TryReadRelative(sourceRoot, deps);
        var companionBytes = TryReadRelative(sourceRoot, companionDll);
        var isDotNet = runtimeConfigBytes is not null || depsBytes is not null || companionBytes is not null;
        if (!isDotNet)
        {
            return [relativeExe];
        }

        if (runtimeConfigBytes is not null)
        {
            files.Add(runtimeConfig);
        }

        if (companionBytes is not null)
        {
            files.Add(companionDll);
        }

        if (depsBytes is null)
        {
            throw new SandboxLayerException(
                SandboxError.AppContainerAclFailed,
                "A .NET executable is missing a matching .deps.json; dependency closure cannot be determined safely.");
        }

        files.Add(deps);
        AddDepsJsonAssets(depsBytes, sourceRoot, files);

        foreach (var hostFile in new[] { "hostfxr.dll", "hostpolicy.dll" })
        {
            if (TryReadRelative(sourceRoot, hostFile) is not null)
            {
                files.Add(hostFile);
            }
        }

        foreach (var relative in files)
        {
            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Dependency closure contained an unsafe relative path.");
            }
        }

        return files.ToList();
    }

    private static void AddDepsJsonAssets(byte[] depsBytes, string directory, HashSet<string> files)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(depsBytes);
        }
        catch (JsonException)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Could not parse .deps.json for a safe dependency closure.");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("targets", out var targets))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, ".deps.json does not declare targets; failing closed.");
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    AddNamedAssets(library.Value, "runtime", directory, files);
                    AddNamedAssets(library.Value, "native", directory, files);
                    AddNamedAssets(library.Value, "resources", directory, files);
                    AddNamedAssets(library.Value, "runtimeTargets", directory, files);
                }
            }
        }
    }

    private static void AddNamedAssets(JsonElement library, string property, string directory, HashSet<string> files)
    {
        if (!library.TryGetProperty(property, out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var asset in assets.EnumerateObject())
        {
            var relative = asset.Name.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative) ||
                relative.StartsWith('/') ||
                relative.Split('/', StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "A .deps.json asset path is not a safe relative path.");
            }

            var candidate = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!SandboxPathPolicy.IsInside(directory, candidate))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "A .deps.json asset escaped the executable directory.");
            }

            if (TryReadRelative(directory, relative) is not null)
            {
                files.Add(relative);
            }
        }
    }

    private static byte[]? TryReadRelative(string root, string relative)
    {
        try
        {
            return SafeSandboxHierarchy.ReadFileRelative(root, SplitRelative(relative));
        }
        catch (SandboxLayerException exception) when (exception.Error == SandboxError.PathOutOfScope)
        {
            return null;
        }
    }

    private static string ManifestIdentity(IReadOnlyList<StagedFile> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(value => value.Relative, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(file.Relative);
            builder.Append('\n');
            builder.Append(Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant());
            builder.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant()[..16];
    }

    private static string[] SplitRelative(string relative) =>
        relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string[] DirectorySegments(string relative)
    {
        var segments = SplitRelative(relative);
        return segments.Length <= 1 ? [] : segments[..^1];
    }

    private sealed record StagedFile(string Relative, byte[] Bytes);
}
