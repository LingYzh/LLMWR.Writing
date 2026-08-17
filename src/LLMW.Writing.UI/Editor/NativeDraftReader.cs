using LLMW.Writing.Application.Editor;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.UI.Editor;

internal sealed record NativeDraftRead(
    string LogicalText,
    string Digest,
    bool EncodingSupported,
    byte[] OriginalBytes);

internal static class NativeDraftReader
{
    public static EditorResult<NativeDraftRead> Read(
        string projectRoot,
        string relativePath,
        string expectedDigest)
    {
        if (!DraftDocumentResolver.IsDraftWorkspacePath(relativePath)
            || DraftDocumentResolver.IsManuscriptPath(relativePath))
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        var resolved = DraftDocumentResolver.Resolve(
            relativePath.Split('/')[1],
            relativePath.Split('/')[2]);
        if (!resolved.Succeeded)
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        string rootFull;
        string physical;
        try
        {
            rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            physical = Path.GetFullPath(Path.Combine(
                rootFull,
                resolved.Value!.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception)
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        var relativeBack = Path.GetRelativePath(rootFull, physical);
        if (string.IsNullOrWhiteSpace(relativeBack)
            || relativeBack.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeBack)
            || !string.Equals(
                relativeBack.Replace('\\', '/'),
                resolved.Value.RelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        if (!TryRejectReparse(rootFull, physical))
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        byte[] bytes;
        try
        {
            if (!File.Exists(physical))
            {
                return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
            }

            bytes = File.ReadAllBytes(physical);
        }
        catch (Exception)
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        if (bytes.LongLength > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorDocumentTooLarge);
        }

        var digest = ContentDigest.Sha256Hex(bytes);
        if (!string.Equals(digest, ContentDigest.Normalize(expectedDigest), StringComparison.Ordinal))
        {
            return EditorResult<NativeDraftRead>.Fail(IpcErrorCodes.EditorStaleBase);
        }

        var decode = TextDocumentCodec.TryDecode(bytes);
        if (!decode.Succeeded)
        {
            return EditorResult<NativeDraftRead>.Ok(new NativeDraftRead("", digest, false, bytes));
        }

        return EditorResult<NativeDraftRead>.Ok(new NativeDraftRead(decode.Value!.LogicalText, digest, true, bytes));
    }

    private static bool TryRejectReparse(string projectRoot, string physical)
    {
        try
        {
            var relative = Path.GetRelativePath(projectRoot, physical);
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
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
