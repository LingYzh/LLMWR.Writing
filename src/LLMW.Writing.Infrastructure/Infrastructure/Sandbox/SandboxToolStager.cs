using System.Runtime.Versioning;
using System.Text.Json;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal static class SandboxToolStager
{
    private static readonly HashSet<string> ClosureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".dll",
        ".json",
        ".config"
    };

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
            return source;
        }

        var closure = ResolveClosure(source);
        if (closure.Count == 0)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Executable dependency closure could not be determined.");
        }

        var staging = SandboxPathPolicy.ToolStagingDirectory(trustedProjectRoot, source);
        Directory.CreateDirectory(staging);
        var sourceRoot = Path.GetDirectoryName(source) ?? throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, "Executable directory is missing.");
        string? stagedExe = null;
        foreach (var file in closure)
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Dependency closure escaped the executable directory.");
            }

            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!SandboxPathPolicy.IsInside(staging, destination))
            {
                throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Staged dependency escaped the Core tool staging directory.");
            }

            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destDir))
            {
                Directory.CreateDirectory(destDir);
                AppContainerAclManager.GrantMinimum(
                    destDir,
                    appContainerSid,
                    NativeConstants.FILE_GENERIC_EXECUTE,
                    inherit: false,
                    faultInjector);
            }

            File.Copy(file, destination, overwrite: true);
            AppContainerAclManager.GrantMinimum(
                destination,
                appContainerSid,
                NativeConstants.SandboxExecuteAccess,
                inherit: false,
                faultInjector);
            if (file.Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                stagedExe = destination;
            }
        }

        AppContainerAclManager.GrantMinimum(
            staging,
            appContainerSid,
            NativeConstants.FILE_GENERIC_EXECUTE,
            inherit: false,
            faultInjector);
        return stagedExe ?? throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, "Staged executable path was not produced.");
    }

    private static List<string> ResolveClosure(string source)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        files.Add(source);
        var directory = Path.GetDirectoryName(source);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [source];
        }

        var stem = Path.GetFileNameWithoutExtension(source);
        var runtimeConfig = Path.Combine(directory, stem + ".runtimeconfig.json");
        var deps = Path.Combine(directory, stem + ".deps.json");
        var companionDll = Path.Combine(directory, stem + ".dll");
        var isDotNet = File.Exists(runtimeConfig) || File.Exists(deps) || File.Exists(companionDll);
        if (!isDotNet)
        {
            return [source];
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (ClosureExtensions.Contains(Path.GetExtension(file)))
            {
                files.Add(file);
            }
        }

        var runtimes = Path.Combine(directory, "runtimes");
        if (Directory.Exists(runtimes))
        {
            foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
            {
                if (ClosureExtensions.Contains(Path.GetExtension(file)))
                {
                    files.Add(file);
                }
            }
        }

        if (File.Exists(deps))
        {
            TryAddDepsJsonAssets(deps, directory, files);
        }

        return files.ToList();
    }

    private static void TryAddDepsJsonAssets(string depsPath, string directory, HashSet<string> files)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
            if (!document.RootElement.TryGetProperty("targets", out var targets))
            {
                return;
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("runtime", out var runtime))
                    {
                        continue;
                    }

                    foreach (var asset in runtime.EnumerateObject())
                    {
                        var candidate = Path.GetFullPath(Path.Combine(directory, asset.Name.Replace('/', Path.DirectorySeparatorChar)));
                        if (File.Exists(candidate) && SandboxPathPolicy.IsInside(directory, candidate))
                        {
                            files.Add(candidate);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            throw new SandboxLayerException(SandboxError.AppContainerAclFailed, "Could not parse .deps.json for a safe dependency closure.");
        }
    }
}
