namespace LLMW.Writing.Application.Security.Sandbox;

public static class SandboxPathPolicy
{
    public const string SandboxRootDirectoryName = ".llmw.sandbox";
    public const string AuthorityDirectoryName = ".llmw";
    public const int MaxCapturedOutputBytes = 256 * 1024;
    public const int OutputHeadBytes = 128 * 1024;
    public const int OutputTailBytes = 128 * 1024;

    public static string AppContainerName(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("AppContainer identity requires a non-empty Project UUID.", nameof(projectId));
        }

        return "llmw.w." + projectId.ToString("N");
    }

    public static string SandboxRoot(string projectRoot) =>
        Path.GetFullPath(Path.Combine(RequireProjectRoot(projectRoot), SandboxRootDirectoryName));

    public static string RunWorkDirectory(string projectRoot, string runId) =>
        Path.GetFullPath(Path.Combine(SandboxRoot(projectRoot), "runs", RequireRunId(runId), "work"));

    public static bool IsAuthorityTree(string relativePath) =>
        StartsWithSegment(relativePath, AuthorityDirectoryName);

    public static bool IsDesignatedWorkRelative(string relativePath, string runId)
    {
        var expected = $"{SandboxRootDirectoryName}/runs/{RequireRunId(runId)}/work";
        var normalized = NormalizeRelative(relativePath);
        return StringComparer.OrdinalIgnoreCase.Equals(normalized, expected) ||
               normalized.StartsWith(expected + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProjectSensitiveRelative(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        return IsAuthorityTree(normalized) ||
               StartsWithSegment(normalized, "Draft") ||
               StartsWithSegment(normalized, "Narrative") ||
               StartsWithSegment(normalized, "Manuscript") ||
               StartsWithSegment(normalized, "Reviews") ||
               StringComparer.OrdinalIgnoreCase.Equals(normalized, "project.db") ||
               StringComparer.OrdinalIgnoreCase.Equals(normalized, "project.llmw.json");
    }

    public static string NormalizeRelative(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A sandbox logical path must be project-relative.", nameof(relativePath));
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("A sandbox logical path contains an escape segment.", nameof(relativePath));
        }

        return string.Join('/', segments);
    }

    public static bool IsWindowsSystemLocation(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return true;
        }

        var normalized = Path.GetFullPath(fullPath);
        foreach (var root in WindowsProtectedRoots())
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string CombineRelative(string projectRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(RequireProjectRoot(projectRoot), NormalizeRelative(relativePath).Replace('/', Path.DirectorySeparatorChar)));

    public static bool IsInside(string parent, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> WindowsProtectedRoots()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(windows));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles));
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFilesX86));
        }
    }

    private static bool StartsWithSegment(string relativePath, string segment)
    {
        var normalized = NormalizeRelative(relativePath);
        return StringComparer.OrdinalIgnoreCase.Equals(normalized, segment) ||
               normalized.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string RequireProjectRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
    }

    private static string RequireRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || runId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Run identity is not a safe directory name.", nameof(runId));
        }

        return runId;
    }
}
