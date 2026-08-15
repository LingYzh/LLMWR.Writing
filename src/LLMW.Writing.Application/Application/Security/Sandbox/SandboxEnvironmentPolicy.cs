namespace LLMW.Writing.Application.Security.Sandbox;

public static class SandboxEnvironmentPolicy
{
    private static readonly HashSet<string> ParentAllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot",
        "windir",
        "SystemDrive",
        "PATH",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER",
        "NUMBER_OF_PROCESSORS",
        "OS",
        "TEMP",
        "TMP",
        "ComSpec"
    };

    private static readonly HashSet<string> ExtraAllowedNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TrustedLaunchOverlayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LLMW_WORKER_BOOTSTRAP_TOKEN",
        "LLMW_WORKSPACE_INSTANCE_ID",
        "LLMW_WORKER_INSTANCE_ID",
        "LLMW_RUN_ID",
        "LLMW_LAUNCH_BINDING_ID",
        "LLMW_WORKER_PIPE_NAME"
    };

    private static readonly HashSet<string> ForbiddenWorkerInheritedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LLMW_UI_BOOTSTRAP_TOKEN",
        "LLMW_RUNTIME_BOOTSTRAP_TOKEN",
        "LLMW_CORE_BOOTSTRAP_TOKEN"
    };

    private static readonly HashSet<string> ForbiddenOverrideNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot",
        "windir",
        "SystemDrive",
        "PATH",
        "PATHEXT",
        "ComSpec",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA"
    };

    private static readonly string[] LoaderSensitiveNames =
    [
        "CORECLR_ENABLE_PROFILING",
        "CORECLR_PROFILER",
        "CORECLR_PROFILER_PATH",
        "CORECLR_PROFILER_PATH_32",
        "CORECLR_PROFILER_PATH_64",
        "COR_ENABLE_PROFILING",
        "COR_PROFILER",
        "COR_PROFILER_PATH",
        "COR_PROFILER_PATH_32",
        "COR_PROFILER_PATH_64",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",
        "DOTNET_MULTILEVEL_LOOKUP",
        "DOTNET_MODIFIABLE_ASSEMBLIES",
        "DOTNET_EnableDiagnostics",
        "COMPlus_EnableDiagnostics",
        "COMPlus_Profiler",
        "COMPlus_ProfAPI_ProfilerCompatibilitySetting",
        "SSLKEYLOGFILE",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "FTP_PROXY"
    ];

    public static bool IsSecretBearingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return ContainsToken(name, "SECRET") ||
               ContainsToken(name, "PASSWORD") ||
               ContainsToken(name, "TOKEN") ||
               ContainsToken(name, "API_KEY") ||
               ContainsToken(name, "APIKEY") ||
               ContainsToken(name, "CONNECTIONSTRING") ||
               ContainsToken(name, "CREDENTIAL") ||
               name.StartsWith("LLMW_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedName(string name) =>
        !string.IsNullOrWhiteSpace(name) && ParentAllowedNames.Contains(name) && !IsSecretBearingName(name);

    public static bool IsForbiddenExtraName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (ForbiddenOverrideNames.Contains(name) || IsSecretBearingName(name))
        {
            return true;
        }

        if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var sensitive in LoaderSensitiveNames)
        {
            if (name.Equals(sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAllowedExtraName(string name) =>
        !IsForbiddenExtraName(name) && ExtraAllowedNames.Contains(name);

    public static SandboxError? ValidateExtraEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        if (extra is null || extra.Count == 0)
        {
            return null;
        }

        foreach (var pair in extra)
        {
            if (!IsAllowedExtraName(pair.Key))
            {
                return SandboxError.EnvironmentRejected;
            }
        }

        return null;
    }

    public static SandboxError? ValidateTrustedLaunchEnvironment(IReadOnlyDictionary<string, string>? overlay)
    {
        if (overlay is null || overlay.Count == 0)
        {
            return SandboxError.EnvironmentRejected;
        }

        foreach (var pair in overlay)
        {
            if (ForbiddenWorkerInheritedNames.Contains(pair.Key) ||
                pair.Key.Equals("LLMW_UI_BOOTSTRAP_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("LLMW_RUNTIME_BOOTSTRAP_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("LLMW_CORE_BOOTSTRAP_TOKEN", StringComparison.OrdinalIgnoreCase))
            {
                return SandboxError.EnvironmentRejected;
            }

            if (!TrustedLaunchOverlayNames.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                return SandboxError.EnvironmentRejected;
            }
        }

        return overlay.ContainsKey("LLMW_WORKER_BOOTSTRAP_TOKEN") ? null : SandboxError.EnvironmentRejected;
    }

    public static IReadOnlyDictionary<string, string> Sanitize(
        IReadOnlyDictionary<string, string?> parent,
        string sandboxTempDirectory,
        string systemRoot,
        string system32Path,
        string? appContainerProfileDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxTempDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(system32Path);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["windir"] = systemRoot,
            ["SystemDrive"] = Path.GetPathRoot(systemRoot)?.TrimEnd('\\') ?? "C:",
            ["PATH"] = system32Path,
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.JS",
            ["TEMP"] = sandboxTempDirectory,
            ["TMP"] = sandboxTempDirectory,
            ["DOTNET_EnableDiagnostics"] = "0",
            ["COMPlus_EnableDiagnostics"] = "0"
        };
        if (!string.IsNullOrWhiteSpace(appContainerProfileDirectory))
        {
            result["LOCALAPPDATA"] = appContainerProfileDirectory;
            result["APPDATA"] = appContainerProfileDirectory;
            result["USERPROFILE"] = appContainerProfileDirectory;
        }

        foreach (var pair in parent)
        {
            if (!IsAllowedName(pair.Key) || string.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            if (pair.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("TMP", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("windir", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result[pair.Key] = pair.Value!;
        }

        return result;
    }

    private static bool ContainsToken(string name, string token) =>
        name.Contains(token, StringComparison.OrdinalIgnoreCase);
}
