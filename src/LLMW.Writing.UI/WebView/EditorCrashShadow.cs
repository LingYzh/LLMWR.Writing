using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.UI.WebView;

internal readonly struct EditorShadowResult
{
    public EditorShadowResult(bool succeeded, string? errorCode, string? safeMessage)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        SafeMessage = safeMessage;
    }

    public bool Succeeded { get; }
    public string? ErrorCode { get; }
    public string? SafeMessage { get; }

    public static EditorShadowResult Ok() => new(true, null, null);

    public static EditorShadowResult Fail(string code, string message) => new(false, code, message);
}

internal sealed class EditorCrashShadow
{
    private readonly object _gate = new();
    private string _logicalText;
    private PendingResync? _resync;

    public EditorCrashShadow(
        string editorSessionId,
        string basePersistedDigest,
        string logicalText,
        bool dirty)
    {
        EditorSessionId = editorSessionId;
        BasePersistedDigest = basePersistedDigest;
        LastPersistedDigest = basePersistedDigest;
        _logicalText = logicalText ?? "";
        Dirty = dirty;
        LastSequence = 0;
    }

    public string EditorSessionId { get; }
    public string BasePersistedDigest { get; private set; }
    public string LastPersistedDigest { get; private set; }
    public long LastPersistedRevision { get; private set; } = 1;
    public int LastSequence { get; private set; }
    public bool Dirty { get; private set; }
    public int SelectionFrom { get; private set; }
    public int SelectionTo { get; private set; }
    public int SelectionHead { get; private set; }
    public string? BoundDocumentSessionId { get; private set; }
    public int BoundRendererGeneration { get; private set; }
    public string? LastSaveOperationId { get; private set; }
    public string? LastSaveContentDigest { get; private set; }

    public string LogicalText
    {
        get
        {
            lock (_gate)
            {
                return _logicalText;
            }
        }
    }

    public void BindRenderer(string documentSessionId, int rendererGeneration)
    {
        BoundDocumentSessionId = documentSessionId;
        BoundRendererGeneration = rendererGeneration;
    }

    public bool MatchesBinding(string editorSessionId, string? documentSessionId)
        => string.Equals(EditorSessionId, editorSessionId, StringComparison.Ordinal)
           && (documentSessionId is null
               || BoundDocumentSessionId is null
               || string.Equals(BoundDocumentSessionId, documentSessionId, StringComparison.Ordinal));

    public EditorShadowResult ApplyChange(int sequence, int expectedSequence, int from, int to, string insert)
    {
        ArgumentNullException.ThrowIfNull(insert);
        lock (_gate)
        {
            if (sequence != LastSequence + 1 || expectedSequence != LastSequence)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchSequence, "Editor change sequence is invalid.");
            }

            if (from < 0 || to < 0 || from > to || to > _logicalText.Length)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Editor change range is invalid.");
            }

            if (insert.Length > BridgeProtocol.MaximumEditorInsertChars)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Editor insert exceeds the patch bound.");
            }

            int nextLength;
            try
            {
                checked
                {
                    nextLength = _logicalText.Length - (to - from) + insert.Length;
                }
            }
            catch (OverflowException)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Editor change overflowed.");
            }

            var next = _logicalText.Remove(from, to - from).Insert(from, insert);
            var utf8 = Encoding.UTF8.GetByteCount(next);
            if (utf8 > EditorTransportLimits.MaximumDocumentUtf8Bytes)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorDocumentTooLarge, "Editor document exceeds the resource bound.");
            }

            var changed = !string.Equals(_logicalText, next, StringComparison.Ordinal);
            _logicalText = next;
            LastSequence = sequence;
            if (changed)
            {
                Dirty = true;
            }

            return EditorShadowResult.Ok();
        }
    }

    public void ApplySelection(int from, int to, int head)
    {
        lock (_gate)
        {
            SelectionFrom = from;
            SelectionTo = to;
            SelectionHead = head;
        }
    }

    public EditorShadowResult BeginResync(string transferId, int totalBytes, string sha256)
    {
        lock (_gate)
        {
            if (totalBytes < 0 || totalBytes > EditorTransportLimits.MaximumDocumentUtf8Bytes)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorDocumentTooLarge, "Editor document exceeds the resource bound.");
            }

            if (!ContentDigest.IsSha256Hex(sha256) || string.IsNullOrWhiteSpace(transferId))
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync identity is invalid.");
            }

            _resync = new PendingResync(
                transferId,
                totalBytes,
                ContentDigest.Normalize(sha256),
                totalBytes == 0 ? [] : new byte[totalBytes],
                totalBytes == 0 ? 0 : ExpectedChunkCount(totalBytes));
            return EditorShadowResult.Ok();
        }
    }

    public EditorShadowResult AcceptResyncChunk(string transferId, int index, int count, string dataBase64)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(dataBase64);
        }
        catch (FormatException)
        {
            return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync chunk encoding is invalid.");
        }

        if (raw.Length > EditorTransportLimits.MaximumChunkUtf8Bytes)
        {
            return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync chunk exceeds the bound.");
        }

        lock (_gate)
        {
            if (_resync is null || !string.Equals(_resync.TransferId, transferId, StringComparison.Ordinal))
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync transfer is not current.");
            }

            if (count != _resync.ChunkCount || index < 0 || index >= _resync.ChunkCount)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync chunk index is invalid.");
            }

            if (_resync.Received.Length == 0)
            {
                _resync.Received = new bool[_resync.ChunkCount];
            }

            var offset = index * EditorTransportLimits.MaximumChunkUtf8Bytes;
            var expectedLength = index == _resync.ChunkCount - 1
                ? _resync.TotalBytes - offset
                : EditorTransportLimits.MaximumChunkUtf8Bytes;
            if (raw.Length != expectedLength)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync chunk length is invalid.");
            }

            if (_resync.Received[index] && !_resync.Buffer.AsSpan(offset, raw.Length).SequenceEqual(raw))
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Duplicate resync chunk conflicted.");
            }

            raw.CopyTo(_resync.Buffer.AsSpan(offset));
            _resync.Received[index] = true;
            return EditorShadowResult.Ok();
        }
    }

    public EditorShadowResult CommitResync(string transferId)
    {
        lock (_gate)
        {
            if (_resync is null || !string.Equals(_resync.TransferId, transferId, StringComparison.Ordinal))
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync transfer is not current.");
            }

            if (_resync.TotalBytes == 0)
            {
                _resync.Received = [];
            }

            if (_resync.Received.Length != _resync.ChunkCount || _resync.Received.Any(received => !received))
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorPatchInvalid, "Resync chunks are incomplete.");
            }

            var actual = Convert.ToHexString(SHA256.HashData(_resync.Buffer)).ToLowerInvariant();
            if (!string.Equals(actual, _resync.Sha256, StringComparison.Ordinal))
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorUploadHashMismatch, "Resync hash did not match.");
            }

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(_resync.Buffer);
            }
            catch (DecoderFallbackException)
            {
                return EditorShadowResult.Fail(IpcErrorCodes.EditorEncodingUnsupported, "Resync text is not valid UTF-8.");
            }

            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            var changed = !string.Equals(_logicalText, text, StringComparison.Ordinal);
            _logicalText = text;
            if (changed)
            {
                Dirty = true;
            }

            LastSequence++;
            _resync = null;
            return EditorShadowResult.Ok();
        }
    }

    public void MarkSaved(string saveOperationId, string persistedDigest, long revision, string savedLogicalText)
    {
        lock (_gate)
        {
            LastSaveOperationId = saveOperationId;
            LastSaveContentDigest = ContentDigest.Sha256Hex(Encoding.UTF8.GetBytes(savedLogicalText));
            LastPersistedDigest = persistedDigest;
            LastPersistedRevision = revision;
            BasePersistedDigest = persistedDigest;
            if (string.Equals(_logicalText, savedLogicalText, StringComparison.Ordinal))
            {
                Dirty = false;
            }
        }
    }

    public bool CoversLatestShadow(string savedLogicalText)
    {
        lock (_gate)
        {
            return string.Equals(_logicalText, savedLogicalText, StringComparison.Ordinal);
        }
    }

    public void LoadRestoredBuffer(string text)
    {
        lock (_gate)
        {
            _logicalText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            Dirty = true;
            LastSequence = 0;
            _resync = null;
        }
    }

    public void LoadCleanDisk(string text, string digest, long revision)
    {
        lock (_gate)
        {
            _logicalText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            Dirty = false;
            BasePersistedDigest = digest;
            LastPersistedDigest = digest;
            LastPersistedRevision = revision;
            LastSequence = 0;
            _resync = null;
        }
    }

    public byte[] EncodeUtf8NoBomLf()
    {
        lock (_gate)
        {
            var lf = _logicalText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetBytes(lf);
        }
    }

    private static int ExpectedChunkCount(int totalBytes)
        => (totalBytes + EditorTransportLimits.MaximumChunkUtf8Bytes - 1) / EditorTransportLimits.MaximumChunkUtf8Bytes;

    private sealed class PendingResync
    {
        public PendingResync(string transferId, int totalBytes, string sha256, byte[] buffer, int chunkCount)
        {
            TransferId = transferId;
            TotalBytes = totalBytes;
            Sha256 = sha256;
            Buffer = buffer;
            ChunkCount = chunkCount;
            Received = chunkCount == 0 ? [] : new bool[chunkCount];
        }

        public string TransferId { get; }
        public int TotalBytes { get; }
        public string Sha256 { get; }
        public byte[] Buffer { get; }
        public int ChunkCount { get; }
        public bool[] Received { get; set; }
    }
}
