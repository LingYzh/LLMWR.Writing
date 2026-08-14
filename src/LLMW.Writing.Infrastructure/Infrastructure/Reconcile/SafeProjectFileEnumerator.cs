namespace LLMW.Writing.Infrastructure.Reconcile;

public sealed class SafeProjectFileEnumerator
{
    private readonly EnumerationOptions enumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    public IReadOnlyList<string> EnumerateFiles(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot) || !CanTraverse(TryGetAttributes(fullRoot)))
        {
            return [];
        }

        var pending = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { fullRoot };
        List<string> files = [];
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Min!;
            pending.Remove(directory);
            if (!CanTraverse(TryGetAttributes(directory)))
            {
                continue;
            }

            foreach (var entry in EnumerateDirectorySafely(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = TryGetAttributes(entry);
                if (attributes is null || (attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes.Value & FileAttributes.Directory) != 0)
                {
                    pending.Add(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return files.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    public static bool CanTraverse(FileAttributes? attributes) =>
        attributes is not null &&
        (attributes.Value & FileAttributes.Directory) != 0 &&
        (attributes.Value & FileAttributes.ReparsePoint) == 0;

    private string[] EnumerateDirectorySafely(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    enumerationOptions)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException)
        {
            return [];
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException)
        {
            return null;
        }
    }
}
