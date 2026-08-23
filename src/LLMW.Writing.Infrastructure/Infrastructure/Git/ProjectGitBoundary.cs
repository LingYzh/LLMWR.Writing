using LLMW.Writing.Application.Git;

namespace LLMW.Writing.Infrastructure.Git;

internal static class ProjectGitBoundary
{
    public static GitResult<string> ValidateProjectRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return GitResults.Fail<string>(GitFailureCode.ProjectBindingInvalid);
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return GitResults.Fail<string>(GitFailureCode.ProjectBindingInvalid, exception.GetType().Name);
        }

        if (IsUnc(fullPath) || ContainsAlternateDataStream(fullPath) || !Directory.Exists(fullPath))
        {
            return GitResults.Fail<string>(GitFailureCode.PathRejected);
        }

        return HasReparsePoint(fullPath)
            ? GitResults.Fail<string>(GitFailureCode.PathRejected)
            : GitResults.Success(fullPath);
    }

    public static GitResult<bool> ValidateRepositoryRoot(string projectRoot, string repositoryRoot, string gitDirectory)
    {
        var root = ValidateProjectRoot(projectRoot);
        if (!root.Succeeded)
        {
            return GitResults.Fail<bool>(root.Failure!.Code, root.Failure.Detail);
        }

        string canonicalRepositoryRoot;
        string canonicalGitDirectory;
        try
        {
            canonicalRepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            canonicalGitDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return GitResults.Fail<bool>(GitFailureCode.PathRejected, exception.GetType().Name);
        }

        if (IsUnc(canonicalRepositoryRoot) || IsUnc(canonicalGitDirectory)
            || ContainsAlternateDataStream(canonicalRepositoryRoot) || ContainsAlternateDataStream(canonicalGitDirectory)
            || HasReparsePoint(canonicalRepositoryRoot) || HasReparsePoint(canonicalGitDirectory))
        {
            return GitResults.Fail<bool>(GitFailureCode.PathRejected);
        }

        // A Project can be a monorepo subdirectory, but a repository nested inside a Project is
        // intentionally unsupported in v1. The Git directory must remain inside its repository.
        if (!IsSameOrAncestor(canonicalRepositoryRoot, root.Value!)
            || !IsSameOrAncestor(canonicalRepositoryRoot, canonicalGitDirectory))
        {
            return GitResults.Fail<bool>(GitFailureCode.RepositoryOutsideProject);
        }

        return GitResults.Success(true);
    }

    private static bool IsSameOrAncestor(string ancestor, string path)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(ancestor, path))
        {
            return true;
        }

        var prefix = Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnc(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static bool ContainsAlternateDataStream(string path)
    {
        var first = path.IndexOf(':');
        return first >= 0 && (first != 1 || path.IndexOf(':', first + 1) >= 0);
    }

    private static bool HasReparsePoint(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        var current = Path.TrimEndingDirectorySeparator(root);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative is "." or "")
        {
            return HasReparsePointAt(fullPath);
        }

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or "." or "..")
            {
                return true;
            }

            current = Path.Combine(current, segment);
            if (HasReparsePointAt(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReparsePointAt(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
