using LLMW.Writing.Application.Authority;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Editor;

internal sealed class PendingEditorUpload
{
    public required string UploadId { get; init; }
    public required string EditorSessionId { get; init; }
    public required string SaveOperationId { get; init; }
    public required string ConnectionId { get; init; }
    public required int DeclaredUtf8Length { get; init; }
    public required string DeclaredSha256 { get; init; }
    public required byte[] Buffer { get; init; }
    public int ChunkCount { get; set; } = -1;
    public bool[] Received { get; set; } = [];
    public bool Committed { get; set; }
}

public sealed class EditorContentUploadService
{
    private readonly IImmutableBlobStore blobs;
    private readonly IEditorSaveFaultInjector faults;
    private readonly Dictionary<string, PendingEditorUpload> uploads = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public EditorContentUploadService(IImmutableBlobStore blobs, IEditorSaveFaultInjector? faults = null)
    {
        this.blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        this.faults = faults ?? NoEditorSaveFaultInjector.Instance;
    }

    public EditorResult<BeginEditorContentUploadResponse> Begin(
        EditorSession session,
        string connectionId,
        string saveOperationId,
        int declaredUtf8Length,
        string declaredSha256)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Writable)
        {
            return EditorResult<BeginEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        if (declaredUtf8Length < 0)
        {
            return EditorResult<BeginEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        if (declaredUtf8Length > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            return EditorResult<BeginEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorDocumentTooLarge);
        }

        if (!ContentDigest.IsSha256Hex(declaredSha256) || string.IsNullOrWhiteSpace(saveOperationId))
        {
            return EditorResult<BeginEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        var uploadId = IpcMessageIds.Create().ToString("D");
        lock (gate)
        {
            uploads[uploadId] = new PendingEditorUpload
            {
                UploadId = uploadId,
                EditorSessionId = session.EditorSessionId,
                SaveOperationId = saveOperationId,
                ConnectionId = connectionId,
                DeclaredUtf8Length = declaredUtf8Length,
                DeclaredSha256 = ContentDigest.Normalize(declaredSha256),
                Buffer = declaredUtf8Length == 0 ? [] : new byte[declaredUtf8Length]
            };
        }

        return EditorResult<BeginEditorContentUploadResponse>.Ok(
            new BeginEditorContentUploadResponse(uploadId, EditorTransportLimits.MaximumChunkUtf8Bytes));
    }

    public EditorResult<EditorContentUploadChunkResponse> AcceptChunk(
        string uploadId,
        string editorSessionId,
        string connectionId,
        int chunkIndex,
        int chunkCount,
        string dataBase64)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(dataBase64);
        }
        catch (FormatException)
        {
            return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        if (raw.Length > EditorTransportLimits.MaximumChunkUtf8Bytes || chunkIndex < 0 || chunkCount < 1)
        {
            return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        lock (gate)
        {
            if (!uploads.TryGetValue(uploadId, out var pending) || pending.Committed)
            {
                return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
            }

            if (!StringComparer.Ordinal.Equals(pending.EditorSessionId, editorSessionId)
                || !StringComparer.Ordinal.Equals(pending.ConnectionId, connectionId))
            {
                return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorSessionInvalid);
            }

            var expectedCount = ExpectedChunkCount(pending.DeclaredUtf8Length);
            if (chunkCount != expectedCount)
            {
                return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
            }

            if (pending.ChunkCount < 0)
            {
                pending.ChunkCount = chunkCount;
                pending.Received = new bool[chunkCount];
            }
            else if (pending.ChunkCount != chunkCount)
            {
                return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
            }

            if (chunkIndex >= pending.ChunkCount)
            {
                return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
            }

            var offset = chunkIndex * EditorTransportLimits.MaximumChunkUtf8Bytes;
            var expectedLength = chunkIndex == pending.ChunkCount - 1
                ? pending.DeclaredUtf8Length - offset
                : EditorTransportLimits.MaximumChunkUtf8Bytes;
            if (raw.Length != expectedLength)
            {
                return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
            }

            if (pending.Received[chunkIndex])
            {
                if (!pending.Buffer.AsSpan(offset, raw.Length).SequenceEqual(raw))
                {
                    return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
                }

                return EditorResult<EditorContentUploadChunkResponse>.Ok(new EditorContentUploadChunkResponse(chunkIndex));
            }

            raw.CopyTo(pending.Buffer.AsSpan(offset));
            pending.Received[chunkIndex] = true;
            return EditorResult<EditorContentUploadChunkResponse>.Ok(new EditorContentUploadChunkResponse(chunkIndex));
        }
    }

    public EditorResult<CommitEditorContentUploadResponse> Commit(
        string uploadId,
        string editorSessionId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        PendingEditorUpload pending;
        lock (gate)
        {
            if (!uploads.TryGetValue(uploadId, out pending!) || pending.Committed)
            {
                return EditorResult<CommitEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
            }

            if (!StringComparer.Ordinal.Equals(pending.EditorSessionId, editorSessionId)
                || !StringComparer.Ordinal.Equals(pending.ConnectionId, connectionId))
            {
                return EditorResult<CommitEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorSessionInvalid);
            }

            if (pending.DeclaredUtf8Length == 0)
            {
                pending.ChunkCount = 0;
                pending.Received = [];
            }

            var expectedCount = ExpectedChunkCount(pending.DeclaredUtf8Length);
            if (pending.ChunkCount != expectedCount || pending.Received.Length != expectedCount || pending.Received.Any(received => !received))
            {
                return EditorResult<CommitEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
            }
        }

        var actualDigest = ContentDigest.Sha256Hex(pending.Buffer);
        if (!StringComparer.Ordinal.Equals(actualDigest, pending.DeclaredSha256))
        {
            return EditorResult<CommitEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorUploadHashMismatch);
        }

        faults.ThrowIf(EditorSaveFaultPoint.AfterUploadHashVerify);
        using var stream = new MemoryStream(pending.Buffer, writable: false);
        var staged = blobs.Stage(stream, pending.DeclaredSha256, cancellationToken);
        faults.ThrowIf(EditorSaveFaultPoint.AfterUploadStaging);
        lock (gate)
        {
            pending.Committed = true;
        }

        return EditorResult<CommitEditorContentUploadResponse>.Ok(
            new CommitEditorContentUploadResponse(new IpcBlobRef(staged.Digest, staged.Length, "blob:" + staged.Digest)));
    }

    public void AbandonBySession(string editorSessionId)
    {
        lock (gate)
        {
            foreach (var id in uploads
                         .Where(pair => StringComparer.Ordinal.Equals(pair.Value.EditorSessionId, editorSessionId))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                uploads.Remove(id);
            }
        }
    }

    public PendingUploadLookup? TryGet(string uploadId)
    {
        lock (gate)
        {
            return uploads.TryGetValue(uploadId, out var pending)
                ? new PendingUploadLookup(
                    pending.UploadId,
                    pending.EditorSessionId,
                    pending.SaveOperationId,
                    pending.DeclaredSha256,
                    pending.DeclaredUtf8Length,
                    pending.Committed)
                : null;
        }
    }

    internal bool IsCommittedForSave(
        string editorSessionId,
        string connectionId,
        string saveOperationId,
        IpcBlobRef content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editorSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveOperationId);
        ArgumentNullException.ThrowIfNull(content);
        if (!ContentDigest.IsSha256Hex(content.Digest)
            || content.Size < 0
            || !StringComparer.Ordinal.Equals(content.Locator, "blob:" + ContentDigest.Normalize(content.Digest)))
        {
            return false;
        }

        var digest = ContentDigest.Normalize(content.Digest);
        lock (gate)
        {
            return uploads.Values.Any(pending =>
                pending.Committed
                && StringComparer.Ordinal.Equals(pending.EditorSessionId, editorSessionId)
                && StringComparer.Ordinal.Equals(pending.ConnectionId, connectionId)
                && StringComparer.Ordinal.Equals(pending.SaveOperationId, saveOperationId)
                && StringComparer.Ordinal.Equals(pending.DeclaredSha256, digest)
                && pending.DeclaredUtf8Length == content.Size);
        }
    }

    private static int ExpectedChunkCount(int declaredUtf8Length)
    {
        if (declaredUtf8Length == 0)
        {
            return 0;
        }

        return (declaredUtf8Length + EditorTransportLimits.MaximumChunkUtf8Bytes - 1)
               / EditorTransportLimits.MaximumChunkUtf8Bytes;
    }
}

public sealed record PendingUploadLookup(
    string UploadId,
    string EditorSessionId,
    string SaveOperationId,
    string DeclaredSha256,
    int DeclaredUtf8Length,
    bool Committed);
