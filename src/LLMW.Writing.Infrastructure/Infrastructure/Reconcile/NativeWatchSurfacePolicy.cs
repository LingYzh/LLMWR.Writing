using LLMW.Writing.Infrastructure.FileSystem;

namespace LLMW.Writing.Infrastructure.Reconcile;

public sealed class NativeWatchSurfacePolicy
{
    private static readonly string[] RelevantRoots = ["Narrative", "Manuscript/current"];
    private readonly ProjectPathResolver paths;

    public NativeWatchSurfacePolicy(ProjectPathResolver paths)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public IReadOnlyList<string> ExistingWatchRoots()
    {
        List<string> roots = [];
        foreach (var relativeRoot in RelevantRoots)
        {
            try
            {
                var fullPath = paths.Resolve(relativeRoot);
                if (Directory.Exists(fullPath))
                {
                    roots.Add(fullPath);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                              System.Security.SecurityException)
            {
            }
        }

        return roots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool IsRelevantRelativePath(string relativePath)
    {
        var normalized = paths.NormalizeRelativePath(relativePath);
        return RelevantRoots.Any(root =>
            StringComparer.OrdinalIgnoreCase.Equals(normalized, root) ||
            normalized.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase));
    }
}
