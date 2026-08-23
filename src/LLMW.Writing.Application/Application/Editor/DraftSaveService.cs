using LLMW.Writing.Application.Authority;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Editor;

internal sealed record CompletedSave(
    string SaveOperationId,
    string ContentDigest,
    string ExpectedBaseDigest,
    SaveDraftEditorSessionResponse Response);

public sealed class DraftSaveService
{
    private readonly IDraftFileStore files;
    private readonly IImmutableBlobStore blobs;
    private readonly EditorLeaseCoordinator leases;
    private readonly IEditorSaveFaultInjector faults;
    private readonly Dictionary<string, CompletedSave> completed = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public DraftSaveService(
        IDraftFileStore files,
        IImmutableBlobStore blobs,
        EditorLeaseCoordinator leases,
        IEditorSaveFaultInjector? faults = null)
    {
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        this.leases = leases ?? throw new ArgumentNullException(nameof(leases));
        this.faults = faults ?? NoEditorSaveFaultInjector.Instance;
    }

    public EditorResult<SaveDraftEditorSessionResponse> Save(
        EditorSession session,
        string connectionId,
        string saveOperationId,
        string expectedPersistedDigest,
        IpcBlobRef content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(saveOperationId) || !ContentDigest.IsSha256Hex(expectedPersistedDigest))
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        if (!session.Writable
            || !leases.Owns(session.LeaseKey, EditorLeaseOwnerKind.UserEditor, connectionId, session.EditorSessionId))
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorLeaseLost);
        }

        if (!DraftDocumentResolver.IsDraftWorkspacePath(session.Document.RelativePath)
            || DraftDocumentResolver.IsManuscriptPath(session.Document.RelativePath))
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
        }

        var expectedLocator = "blob:" + ContentDigest.Normalize(content.Digest);
        if (!StringComparer.Ordinal.Equals(content.Locator, expectedLocator)
            || !ContentDigest.IsSha256Hex(content.Digest)
            || content.Size < 0
            || content.Size > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        var contentDigest = ContentDigest.Normalize(content.Digest);
        var identityKey = session.EditorSessionId + ":" + saveOperationId;
        lock (gate)
        {
            if (completed.TryGetValue(identityKey, out var previous))
            {
                if (StringComparer.Ordinal.Equals(previous.ContentDigest, contentDigest)
                    && StringComparer.Ordinal.Equals(previous.ExpectedBaseDigest, expectedPersistedDigest))
                {
                    return EditorResult<SaveDraftEditorSessionResponse>.Ok(
                        previous.Response with { IdempotentReplay = true });
                }

                return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorSaveIdentityConflict);
            }
        }

        byte[] payload;
        try
        {
            using var stream = blobs.OpenRead(contentDigest);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            payload = memory.ToArray();
        }
        catch (Exception)
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        if (payload.LongLength != content.Size || payload.Length > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        if (!StringComparer.Ordinal.Equals(ContentDigest.Sha256Hex(payload), contentDigest))
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorUploadHashMismatch);
        }

        var decode = TextDocumentCodec.TryDecode(payload);
        if (!decode.Succeeded)
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(decode.ErrorCode!);
        }

        var normalized = TextDocumentCodec.EncodeUtf8NoBomLf(decode.Value!.LogicalText);
        var normalizedDigest = ContentDigest.Sha256Hex(normalized);

        return leases.WithDocumentLock(session.LeaseKey, () =>
        {
            if (!leases.Owns(session.LeaseKey, EditorLeaseOwnerKind.UserEditor, connectionId, session.EditorSessionId))
            {
                return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorLeaseLost);
            }

            faults.ThrowIf(EditorSaveFaultPoint.BeforeFinalFreshnessCheck);
            var written = files.AtomicReplace(
                session.Document.RelativePath,
                expectedPersistedDigest,
                normalized,
                faults);
            if (!written.Succeeded)
            {
                return EditorResult<SaveDraftEditorSessionResponse>.Fail(written.ErrorCode!);
            }

            var response = new SaveDraftEditorSessionResponse(
                saveOperationId,
                written.Value!.Digest,
                session.LastPersistedRevision + 1,
                false);
            lock (gate)
            {
                completed[identityKey] = new CompletedSave(saveOperationId, contentDigest, expectedPersistedDigest, response);
            }

            _ = normalizedDigest;
            faults.ThrowIf(EditorSaveFaultPoint.BeforeIpcResponse);
            return EditorResult<SaveDraftEditorSessionResponse>.Ok(response);
        });
    }

    internal bool TryGetCompleted(
        EditorSession session,
        string saveOperationId,
        out SaveDraftEditorSessionResponse response)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveOperationId);
        lock (gate)
        {
            if (completed.TryGetValue(session.EditorSessionId + ":" + saveOperationId, out var previous))
            {
                response = previous.Response;
                return true;
            }
        }

        response = default!;
        return false;
    }
}
