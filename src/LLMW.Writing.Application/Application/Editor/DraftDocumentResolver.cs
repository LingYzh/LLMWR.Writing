using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Narrative;

namespace LLMW.Writing.Application.Editor;

public static class DraftDocumentResolver
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static EditorResult<ResolvedDraftDocument> Resolve(string? chapterId, string? draftFileName)
    {
        if (!NarrativeChangeDraft.IsCanonicalUuidV7(chapterId))
        {
            return EditorResult<ResolvedDraftDocument>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        if (!TryValidateFileName(draftFileName, out var fileName, out var format))
        {
            return EditorResult<ResolvedDraftDocument>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        var relative = "Draft/" + chapterId + "/" + fileName;
        if (!IsDraftWorkspacePath(relative))
        {
            return EditorResult<ResolvedDraftDocument>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        return EditorResult<ResolvedDraftDocument>.Ok(new ResolvedDraftDocument(
            chapterId!,
            fileName,
            relative,
            format,
            fileName));
    }

    public static bool IsDraftWorkspacePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Contains(':')
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.StartsWith('/')
            || normalized.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = normalized.Split('/');
        if (segments.Length != 3
            || !string.Equals(segments[0], "Draft", StringComparison.Ordinal)
            || !NarrativeChangeDraft.IsCanonicalUuidV7(segments[1]))
        {
            return false;
        }

        return TryValidateFileName(segments[2], out _, out _);
    }

    public static bool IsManuscriptPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("Manuscript/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "Manuscript", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateFileName(string? draftFileName, out string fileName, out EditorFormatKind format)
    {
        fileName = "";
        format = EditorFormatKind.Txt;
        if (string.IsNullOrWhiteSpace(draftFileName)
            || draftFileName.Length > EditorTransportLimits.MaximumFilenameChars)
        {
            return false;
        }

        if (draftFileName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0)
        {
            return false;
        }

        if (draftFileName.Contains("..", StringComparison.Ordinal)
            || draftFileName.EndsWith(' ')
            || draftFileName.EndsWith('.'))
        {
            return false;
        }

        var extension = Path.GetExtension(draftFileName);
        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
        {
            format = EditorFormatKind.Md;
        }
        else if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            format = EditorFormatKind.Txt;
        }
        else if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            format = EditorFormatKind.Docx;
        }
        else
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(draftFileName);
        if (string.IsNullOrWhiteSpace(stem) || ReservedDeviceNames.Contains(stem))
        {
            return false;
        }

        fileName = draftFileName;
        return true;
    }
}
