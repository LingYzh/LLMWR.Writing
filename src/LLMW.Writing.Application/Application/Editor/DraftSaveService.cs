using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.History;
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
    private readonly IDocxDocumentAdapter docx;
    private readonly IEditorSaveFaultInjector faults;
    private readonly LocalHistoryService? history;
    private readonly Dictionary<string, CompletedSave> completed = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public DraftSaveService(
        IDraftFileStore files,
        IImmutableBlobStore blobs,
        EditorLeaseCoordinator leases,
        IDocxDocumentAdapter? docx = null,
        IEditorSaveFaultInjector? faults = null,
        LocalHistoryService? history = null)
    {
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        this.leases = leases ?? throw new ArgumentNullException(nameof(leases));
        this.docx = docx ?? UnavailableDocxDocumentAdapter.Instance;
        this.faults = faults ?? NoEditorSaveFaultInjector.Instance;
        this.history = history;
    }

    public EditorResult<SaveDraftEditorSessionResponse> Save(
        EditorSession session,
        string connectionId,
        string saveOperationId,
        string expectedPersistedDigest,
        IpcBlobRef content,
        HistoryCheckpointTriggerKind checkpointTrigger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(saveOperationId) || !ContentDigest.IsSha256Hex(expectedPersistedDigest))
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        if (!Enum.IsDefined(checkpointTrigger))
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

        byte[] serialized;
        if (session.Document.FormatKind == EditorFormatKind.Docx)
        {
            var created = docx.Create(DocxEditorDocument.FromLogicalText(decode.Value!.LogicalText));
            if (!created.Succeeded)
            {
                return EditorResult<SaveDraftEditorSessionResponse>.Fail(ToError(created.Failure!.Value));
            }

            serialized = created.Value!;
        }
        else
        {
            serialized = TextDocumentCodec.EncodeUtf8NoBomLf(decode.Value!.LogicalText);
        }

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
                serialized,
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
            CaptureHistory(session, expectedPersistedDigest, written.Value.Bytes, checkpointTrigger, cancellationToken);
            lock (gate)
            {
                completed[identityKey] = new CompletedSave(saveOperationId, contentDigest, expectedPersistedDigest, response);
            }

            faults.ThrowIf(EditorSaveFaultPoint.BeforeIpcResponse);
            return EditorResult<SaveDraftEditorSessionResponse>.Ok(response);
        });
    }

    private void CaptureHistory(
        EditorSession session,
        string baseDigest,
        byte[] content,
        HistoryCheckpointTriggerKind trigger,
        CancellationToken cancellationToken)
    {
        if (history is null)
        {
            return;
        }

        // Draft durability already succeeded. Local-history metadata is recovery support and
        // must never turn that completed Draft save into an ambiguous overwrite outcome.
        _ = history.Capture(
            new LocalHistoryCheckpoint(
                session.ProjectId,
                HistoryDocumentIdentity.From(session.Document),
                session.EditorSessionId,
                baseDigest,
                content,
                trigger),
            cancellationToken);
    }

    private static string ToError(DocxDocumentFailure failure) => failure switch
    {
        DocxDocumentFailure.MalformedXml => IpcErrorCodes.DocxMalformedXml,
        DocxDocumentFailure.ExternalRelationship => IpcErrorCodes.DocxExternalRelationship,
        DocxDocumentFailure.Encrypted => IpcErrorCodes.DocxEncrypted,
        DocxDocumentFailure.AdapterUnavailable => IpcErrorCodes.DocxAdapterUnavailable,
        _ => failure == DocxDocumentFailure.Oversized
            ? IpcErrorCodes.EditorDocumentTooLarge
            : failure == DocxDocumentFailure.UnsupportedFeature
                ? IpcErrorCodes.DocxUnsupportedFeature
                : IpcErrorCodes.DocxMalformedPackage
    };

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
