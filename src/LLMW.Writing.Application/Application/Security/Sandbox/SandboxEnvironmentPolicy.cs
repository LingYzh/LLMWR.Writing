namespace LLMW.Writing.Application.Security.Sandbox;

public static class SandboxEnvironmentPolicy
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
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
        !string.IsNullOrWhiteSpace(name) && AllowedNames.Contains(name) && !IsSecretBearingName(name);

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
