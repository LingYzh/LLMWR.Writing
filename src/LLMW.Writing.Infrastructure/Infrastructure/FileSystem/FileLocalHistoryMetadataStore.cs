using System.Security.Cryptography;
using System.Text.Json;
using LLMW.Writing.Application.Editor;
using LLMW.Writing.Application.History;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Infrastructure.FileSystem;

/// <summary>
/// File-backed, project-local metadata index for the shared-blob local-history service.
/// The only path it writes is Core-resolved <c>.llmw/history/index.json</c>.
/// </summary>
public sealed class FileLocalHistoryMetadataStore : ILocalHistoryMetadataStore
{
    private const int FormatVersion = 1;
    private const int BufferSize = 128 * 1024;
    private const string RelativeIndexPath = ".llmw/history/index.json";
    private readonly ProjectPathResolver resolver;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public FileLocalHistoryMetadataStore(ProjectPathResolver resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public EditorResult<IReadOnlyList<HistoryEntry>> Load(CancellationToken cancellationToken = default)
    {
        try
        {
            var path = ResolveIndexPath();
            if (!File.Exists(path))
            {
                return EditorResult<IReadOnlyList<HistoryEntry>>.Ok([]);
            }

            RejectReparsePoint(path);
            var bytes = ReadAllBytes(path, cancellationToken);
            var index = JsonSerializer.Deserialize<HistoryIndex>(bytes, jsonOptions);
            if (index is null || index.FormatVersion != FormatVersion || index.Entries is null)
            {
                return EditorResult<IReadOnlyList<HistoryEntry>>.Fail(IpcErrorCodes.HistoryStorageFailure);
            }

            var entriesBytes = JsonSerializer.SerializeToUtf8Bytes(index.Entries, jsonOptions);
            if (!StringComparer.Ordinal.Equals(index.EntriesDigest, Sha256(entriesBytes))
                || index.Entries.Any(entry => !IsValid(entry))
                || HasDuplicateHistoryIds(index.Entries))
            {
                return EditorResult<IReadOnlyList<HistoryEntry>>.Fail(IpcErrorCodes.HistoryStorageFailure);
            }

            return EditorResult<IReadOnlyList<HistoryEntry>>.Ok(index.Entries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return EditorResult<IReadOnlyList<HistoryEntry>>.Fail(IpcErrorCodes.HistoryStorageFailure);
        }
    }

    public EditorResult<bool> Replace(IReadOnlyList<HistoryEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Any(entry => !IsValid(entry)) || HasDuplicateHistoryIds(entries))
        {
            return EditorResult<bool>.Fail(IpcErrorCodes.HistoryEntryInvalid);
        }

        try
        {
            var entriesArray = entries.ToArray();
            var entriesBytes = JsonSerializer.SerializeToUtf8Bytes(entriesArray, jsonOptions);
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(
                new HistoryIndex(FormatVersion, Sha256(entriesBytes), entriesArray),
                jsonOptions);
            var targetPath = ResolveIndexPath();
            var directory = Path.GetDirectoryName(targetPath)
                ?? throw new IOException("Local history directory could not be resolved.");
            Directory.CreateDirectory(directory);
            RejectReparsePoint(directory);
            var temporaryPath = Path.Combine(directory, ".tmp-history-" + Guid.NewGuid().ToString("N"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.WriteThrough))
                {
                    stream.Write(indexBytes);
                    stream.Flush(flushToDisk: true);
                }

                if (!StringComparer.Ordinal.Equals(Sha256(ReadAllBytes(temporaryPath, cancellationToken)), Sha256(indexBytes)))
                {
                    return EditorResult<bool>.Fail(IpcErrorCodes.HistoryStorageFailure);
                }

                if (File.Exists(targetPath))
                {
                    RejectReparsePoint(targetPath);
                    File.Replace(temporaryPath, targetPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, targetPath, overwrite: false);
                }

                return EditorResult<bool>.Ok(true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return EditorResult<bool>.Fail(IpcErrorCodes.HistoryStorageFailure);
        }
    }

    private string ResolveIndexPath() => resolver.Resolve(RelativeIndexPath);

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        cancellationToken.ThrowIfCancellationRequested();
        return memory.ToArray();
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsValid(HistoryEntry? entry)
    {
        if (entry is null || entry.DocumentIdentity is null)
        {
            return false;
        }

        return Guid.TryParse(entry.HistoryId, out _)
               && Guid.TryParse(entry.ProjectId, out _)
               && Guid.TryParse(entry.EditorSessionId, out _)
               && ContentDigest.IsSha256Hex(entry.BaseDigest)
               && ContentDigest.IsSha256Hex(entry.ContentDigest)
               && entry.ContentLength >= 0
               && entry.ContentLength <= EditorTransportLimits.MaximumDocumentUtf8Bytes
               && Enum.IsDefined(entry.TriggerKind);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Local history paths may not be reparse points.");
        }
    }

    private static bool HasDuplicateHistoryIds(IEnumerable<HistoryEntry> entries) =>
        entries.GroupBy(entry => entry.HistoryId, StringComparer.Ordinal).Any(group => group.Skip(1).Any());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record HistoryIndex(int FormatVersion, string EntriesDigest, HistoryEntry[] Entries);
}
