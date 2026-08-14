namespace LLMW.Writing.Infrastructure.FileSystem;

public sealed class ProjectPathResolver
{
    private readonly string projectRoot;
    private readonly string rootWithSeparator;

    public ProjectPathResolver(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        this.projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        rootWithSeparator = this.projectRoot + Path.DirectorySeparatorChar;
    }

    public string ProjectRoot => projectRoot;

    public string NormalizeRelativePath(string relativePath, bool rejectReparsePoints = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("A project path must be relative.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new UnauthorizedAccessException("A project path contains an escape segment.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, Path.Combine(segments)));
        EnsureInside(fullPath);
        if (rejectReparsePoints)
        {
            RejectExistingReparsePoints(fullPath);
        }

        return string.Join('/', segments);
    }

    public string FromFullPath(string fullPath, bool rejectReparsePoints = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var normalized = Path.GetFullPath(fullPath);
        EnsureInside(normalized);
        return NormalizeRelativePath(Path.GetRelativePath(projectRoot, normalized), rejectReparsePoints);
    }

    public string Resolve(string relativePath, bool rejectReparsePoints = true)
    {
        var normalized = NormalizeRelativePath(relativePath, rejectReparsePoints);
        return Path.GetFullPath(Path.Combine(projectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    private void EnsureInside(string fullPath)
    {
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("A project path escapes the project root.");
        }
    }

    private void RejectExistingReparsePoints(string fullPath)
    {
        var relative = Path.GetRelativePath(projectRoot, fullPath);
        var current = projectRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Project paths may not traverse a reparse point.");
            }
        }
    }
}
