using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Editor;

public sealed class EditorRuntime
{
    private sealed record PendingDownload(string EditorSessionId, string ConnectionId, byte[] Bytes);

    private readonly string projectId;
    private readonly IDraftFileStore files;
    private readonly EditorLeaseCoordinator leases = new();
    private readonly EditorContentUploadService uploads;
    private readonly DraftSaveService saves;
    private readonly IDocxDocumentAdapter docx;
    private readonly Dictionary<string, EditorSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingDownload> downloads = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public EditorRuntime(
        string projectId,
        IDraftFileStore files,
        IImmutableBlobStore blobs,
        IEditorSaveFaultInjector? faults = null,
        IDocxDocumentAdapter? docx = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        this.projectId = projectId;
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.docx = docx ?? UnavailableDocxDocumentAdapter.Instance;
        var injector = faults ?? NoEditorSaveFaultInjector.Instance;
        uploads = new EditorContentUploadService(blobs, injector);
        saves = new DraftSaveService(files, blobs, leases, this.docx, injector);
    }

    public string ProjectId => projectId;

    public EditorLeaseCoordinator Leases => leases;

    public EditorResult<OpenDraftEditorSessionResponse> Open(
        CallerPrincipal principal,
        string connectionId,
        string? envelopeProjectId,
        string chapterId,
        string draftFileName,
        bool requestWritable)
    {
        if (!IsUser(principal) || !ProjectMatches(envelopeProjectId))
        {
            return EditorResult<OpenDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorSessionInvalid);
        }

        var document = DraftDocumentResolver.Resolve(chapterId, draftFileName);
        if (!document.Succeeded)
        {
            return EditorResult<OpenDraftEditorSessionResponse>.Fail(document.ErrorCode!);
        }

        var snapshot = files.Read(document.Value!.RelativePath);
        if (!snapshot.Succeeded)
        {
            return EditorResult<OpenDraftEditorSessionResponse>.Fail(snapshot.ErrorCode!);
        }

        var decode = document.Value!.FormatKind == EditorFormatKind.Docx
            ? ReadDocx(snapshot.Value!.Bytes)
            : TextDocumentCodec.TryDecode(snapshot.Value!.Bytes);
        if (document.Value.FormatKind == EditorFormatKind.Docx && !decode.Succeeded)
        {
            return EditorResult<OpenDraftEditorSessionResponse>.Fail(decode.ErrorCode!);
        }

        var writable = false;
        EditorLeaseOwnerKind? owner = null;
        var sessionId = IpcMessageIds.Create().ToString("D");
        if (requestWritable && decode.Succeeded)
        {
            var acquired = leases.TryAcquire(
                snapshot.Value.LeaseKey,
                EditorLeaseOwnerKind.UserEditor,
                connectionId,
                sessionId);
            if (acquired.Succeeded)
            {
                writable = true;
                owner = EditorLeaseOwnerKind.UserEditor;
            }
            else
            {
                owner = leases.Get(snapshot.Value.LeaseKey)?.OwnerKind;
            }
        }
        else
        {
            owner = leases.Get(snapshot.Value.LeaseKey)?.OwnerKind;
        }

        var session = new EditorSession(
            sessionId,
            projectId,
            connectionId,
            document.Value,
            snapshot.Value.LeaseKey,
            snapshot.Value.Digest,
            snapshot.Value.Digest,
            1,
            owner,
            writable && decode.Succeeded);
        lock (gate)
        {
            sessions[sessionId] = session;
        }

        return EditorResult<OpenDraftEditorSessionResponse>.Ok(ToOpenResponse(session, snapshot.Value.Length));
    }

    public EditorResult<GetDraftEditorSessionStateResponse> GetState(
        CallerPrincipal principal,
        string connectionId,
        string editorSessionId)
    {
        var session = RequireSession(principal, connectionId, editorSessionId);
        if (!session.Succeeded)
        {
            return EditorResult<GetDraftEditorSessionStateResponse>.Fail(session.ErrorCode!);
        }

        var read = files.Read(session.Value!.Document.RelativePath);
        var digest = read.Succeeded ? read.Value!.Digest : session.Value.LastPersistedDigest;
        return EditorResult<GetDraftEditorSessionStateResponse>.Ok(new GetDraftEditorSessionStateResponse(
            session.Value.EditorSessionId,
            digest,
            session.Value.LastPersistedRevision,
            session.Value.LeaseOwnerKind,
            session.Value.Writable && leases.Owns(
                session.Value.LeaseKey,
                EditorLeaseOwnerKind.UserEditor,
                connectionId,
                session.Value.EditorSessionId),
            true));
    }

    public EditorResult<ReleaseDraftEditorSessionResponse> Release(
        CallerPrincipal principal,
        string connectionId,
        string editorSessionId)
    {
        EditorSession? session;
        lock (gate)
        {
            sessions.TryGetValue(editorSessionId, out session);
        }

        if (session is null)
        {
            return EditorResult<ReleaseDraftEditorSessionResponse>.Ok(new ReleaseDraftEditorSessionResponse(true));
        }

        if (!IsUser(principal) || !StringComparer.Ordinal.Equals(session.ConnectionId, connectionId))
        {
            return EditorResult<ReleaseDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorSessionInvalid);
        }

        leases.Release(session.LeaseKey, EditorLeaseOwnerKind.UserEditor, connectionId, editorSessionId);
        uploads.AbandonBySession(editorSessionId);
        lock (gate)
        {
            sessions.Remove(editorSessionId);
            foreach (var downloadId in downloads.Where(pair => StringComparer.Ordinal.Equals(pair.Value.EditorSessionId, editorSessionId)).Select(pair => pair.Key).ToArray())
            {
                downloads.Remove(downloadId);
            }
        }

        return EditorResult<ReleaseDraftEditorSessionResponse>.Ok(new ReleaseDraftEditorSessionResponse(true));
    }

    public EditorResult<BeginEditorContentDownloadResponse> BeginDownload(
        CallerPrincipal principal,
        string connectionId,
        BeginEditorContentDownloadRequest request)
    {
        var session = RequireSession(principal, connectionId, request.EditorSessionId);
        if (!session.Succeeded || !ContentDigest.IsSha256Hex(request.ExpectedPersistedDigest)
            || !StringComparer.Ordinal.Equals(request.ExpectedPersistedDigest, session.Value?.LastPersistedDigest))
        {
            return EditorResult<BeginEditorContentDownloadResponse>.Fail(session.Succeeded ? IpcErrorCodes.EditorStaleBase : session.ErrorCode!);
        }

        var snapshot = files.Read(session.Value!.Document.RelativePath);
        if (!snapshot.Succeeded || !StringComparer.Ordinal.Equals(snapshot.Value!.Digest, request.ExpectedPersistedDigest))
        {
            return EditorResult<BeginEditorContentDownloadResponse>.Fail(snapshot.ErrorCode ?? IpcErrorCodes.EditorStaleBase);
        }

        var decoded = session.Value.Document.FormatKind == EditorFormatKind.Docx
            ? ReadDocx(snapshot.Value.Bytes)
            : TextDocumentCodec.TryDecode(snapshot.Value.Bytes);
        if (!decoded.Succeeded)
        {
            return EditorResult<BeginEditorContentDownloadResponse>.Fail(decoded.ErrorCode!);
        }

        var bytes = TextDocumentCodec.EncodeUtf8NoBomLf(decoded.Value!.LogicalText);
        if (bytes.Length > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            return EditorResult<BeginEditorContentDownloadResponse>.Fail(IpcErrorCodes.EditorDocumentTooLarge);
        }

        var downloadId = IpcMessageIds.Create().ToString("D");
        lock (gate)
        {
            downloads[downloadId] = new PendingDownload(session.Value.EditorSessionId, connectionId, bytes);
        }

        var count = bytes.Length == 0 ? 0 : (bytes.Length + EditorTransportLimits.MaximumChunkUtf8Bytes - 1) / EditorTransportLimits.MaximumChunkUtf8Bytes;
        return EditorResult<BeginEditorContentDownloadResponse>.Ok(new BeginEditorContentDownloadResponse(
            downloadId, bytes.Length, ContentDigest.Sha256Hex(bytes), count, EditorTransportLimits.MaximumChunkUtf8Bytes));
    }

    public EditorResult<EditorContentDownloadChunkResponse> DownloadChunk(
        CallerPrincipal principal,
        string connectionId,
        EditorContentDownloadChunkRequest request)
    {
        if (!IsUser(principal))
        {
            return EditorResult<EditorContentDownloadChunkResponse>.Fail(IpcErrorCodes.EditorSessionInvalid);
        }

        PendingDownload? download;
        lock (gate)
        {
            downloads.TryGetValue(request.DownloadId, out download);
        }

        var count = download is null ? 0 : download.Bytes.Length == 0 ? 0 : (download.Bytes.Length + EditorTransportLimits.MaximumChunkUtf8Bytes - 1) / EditorTransportLimits.MaximumChunkUtf8Bytes;
        if (download is null || !StringComparer.Ordinal.Equals(download.ConnectionId, connectionId) || request.ChunkIndex < 0 || request.ChunkIndex >= count)
        {
            return EditorResult<EditorContentDownloadChunkResponse>.Fail(IpcErrorCodes.EditorSessionInvalid);
        }

        var offset = request.ChunkIndex * EditorTransportLimits.MaximumChunkUtf8Bytes;
        var length = Math.Min(EditorTransportLimits.MaximumChunkUtf8Bytes, download.Bytes.Length - offset);
        return EditorResult<EditorContentDownloadChunkResponse>.Ok(new EditorContentDownloadChunkResponse(request.ChunkIndex, Convert.ToBase64String(download.Bytes, offset, length)));
    }

    public void ReleaseByConnection(string connectionId)
    {
        string[] ids;
        lock (gate)
        {
            ids = sessions.Where(pair => StringComparer.Ordinal.Equals(pair.Value.ConnectionId, connectionId))
                .Select(pair => pair.Key)
                .ToArray();
        }

        foreach (var id in ids)
        {
            EditorSession session;
            lock (gate)
            {
                if (!sessions.TryGetValue(id, out session!))
                {
                    continue;
                }

                sessions.Remove(id);
                foreach (var downloadId in downloads.Where(pair => StringComparer.Ordinal.Equals(pair.Value.EditorSessionId, id)).Select(pair => pair.Key).ToArray())
                {
                    downloads.Remove(downloadId);
                }
            }

            leases.Release(session.LeaseKey, EditorLeaseOwnerKind.UserEditor, connectionId, session.EditorSessionId);
            uploads.AbandonBySession(id);
        }

        leases.ReleaseByOwner(EditorLeaseOwnerKind.UserEditor, connectionId);
    }

    public EditorResult<BeginEditorContentUploadResponse> BeginUpload(
        CallerPrincipal principal,
        string connectionId,
        BeginEditorContentUploadRequest request)
    {
        var session = RequireWritable(principal, connectionId, request.EditorSessionId);
        if (!session.Succeeded)
        {
            return EditorResult<BeginEditorContentUploadResponse>.Fail(session.ErrorCode!);
        }

        return uploads.Begin(
            session.Value!,
            connectionId,
            request.SaveOperationId,
            request.DeclaredUtf8Length,
            request.DeclaredSha256);
    }

    public EditorResult<EditorContentUploadChunkResponse> UploadChunk(
        CallerPrincipal principal,
        string connectionId,
        EditorContentUploadChunkRequest request)
    {
        var lookup = uploads.TryGet(request.UploadId);
        if (lookup is null)
        {
            return EditorResult<EditorContentUploadChunkResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        var session = RequireWritable(principal, connectionId, lookup.EditorSessionId);
        if (!session.Succeeded)
        {
            return EditorResult<EditorContentUploadChunkResponse>.Fail(session.ErrorCode!);
        }

        return uploads.AcceptChunk(
            request.UploadId,
            lookup.EditorSessionId,
            connectionId,
            request.ChunkIndex,
            request.ChunkCount,
            request.DataBase64);
    }

    public EditorResult<CommitEditorContentUploadResponse> CommitUpload(
        CallerPrincipal principal,
        string connectionId,
        CommitEditorContentUploadRequest request,
        CancellationToken cancellationToken)
    {
        var lookup = uploads.TryGet(request.UploadId);
        if (lookup is null)
        {
            return EditorResult<CommitEditorContentUploadResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        var session = RequireWritable(principal, connectionId, lookup.EditorSessionId);
        if (!session.Succeeded)
        {
            return EditorResult<CommitEditorContentUploadResponse>.Fail(session.ErrorCode!);
        }

        return uploads.Commit(request.UploadId, lookup.EditorSessionId, connectionId, cancellationToken);
    }

    public EditorResult<SaveDraftEditorSessionResponse> Save(
        CallerPrincipal principal,
        string connectionId,
        SaveDraftEditorSessionRequest request,
        CancellationToken cancellationToken)
    {
        var session = RequireWritable(principal, connectionId, request.EditorSessionId);
        if (!session.Succeeded)
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(session.ErrorCode!);
        }

        if (!uploads.IsCommittedForSave(
                session.Value!.EditorSessionId,
                connectionId,
                request.SaveOperationId,
                request.Content))
        {
            return EditorResult<SaveDraftEditorSessionResponse>.Fail(IpcErrorCodes.EditorUploadInvalid);
        }

        EditorResult<SaveDraftEditorSessionResponse> saved;
        try
        {
            saved = saves.Save(
                session.Value,
                connectionId,
                request.SaveOperationId,
                request.ExpectedPersistedDigest,
                request.Content,
                cancellationToken);
        }
        catch (EditorSaveFaultInjectedException exception) when (exception.Point == EditorSaveFaultPoint.BeforeIpcResponse)
        {
            if (saves.TryGetCompleted(session.Value, request.SaveOperationId, out var completed))
            {
                session.Value.LastPersistedDigest = completed.PersistedDigest;
                session.Value.LastPersistedRevision = completed.PersistedRevision;
            }

            throw;
        }

        if (!saved.Succeeded)
        {
            return saved;
        }

        session.Value!.LastPersistedDigest = saved.Value!.PersistedDigest;
        session.Value.LastPersistedRevision = saved.Value.PersistedRevision;
        return saved;
    }

    public EditorResult<EditorLease> AcquireAgentWrite(
        string leaseKey,
        string runId,
        string editorSessionId,
        string expectedDigest)
    {
        var current = files.ReadFromLeaseKey(leaseKey);
        if (!current.Succeeded)
        {
            return EditorResult<EditorLease>.Fail(current.ErrorCode!);
        }

        if (!StringComparer.Ordinal.Equals(current.Value!.Digest, expectedDigest))
        {
            return EditorResult<EditorLease>.Fail(IpcErrorCodes.EditorStaleBase);
        }

        return leases.TryAcquire(leaseKey, EditorLeaseOwnerKind.AgentWrite, runId, editorSessionId);
    }

    public EditorResult<EditorLease> TransferLease(
        string leaseKey,
        EditorLeaseOwnerKind fromKind,
        string fromOwnerId,
        EditorLeaseOwnerKind toKind,
        string toOwnerId,
        string toEditorSessionId,
        string expectedDigest)
    {
        var current = files.ReadFromLeaseKey(leaseKey);
        if (!current.Succeeded)
        {
            return EditorResult<EditorLease>.Fail(current.ErrorCode!);
        }

        return leases.Transfer(
            leaseKey,
            fromKind,
            fromOwnerId,
            toKind,
            toOwnerId,
            toEditorSessionId,
            expectedDigest,
            current.Value!.Digest);
    }

    private EditorResult<EditorSession> RequireWritable(CallerPrincipal principal, string connectionId, string editorSessionId)
    {
        var session = RequireSession(principal, connectionId, editorSessionId);
        if (!session.Succeeded)
        {
            return session;
        }

        if (!session.Value!.Writable
            || !leases.Owns(session.Value.LeaseKey, EditorLeaseOwnerKind.UserEditor, connectionId, editorSessionId))
        {
            return EditorResult<EditorSession>.Fail(IpcErrorCodes.EditorLeaseLost);
        }

        return session;
    }

    private EditorResult<EditorSession> RequireSession(CallerPrincipal principal, string connectionId, string editorSessionId)
    {
        if (!IsUser(principal))
        {
            return EditorResult<EditorSession>.Fail(IpcErrorCodes.EditorSessionInvalid);
        }

        lock (gate)
        {
            if (!sessions.TryGetValue(editorSessionId, out var session)
                || !StringComparer.Ordinal.Equals(session.ConnectionId, connectionId)
                || !StringComparer.Ordinal.Equals(session.ProjectId, projectId))
            {
                return EditorResult<EditorSession>.Fail(IpcErrorCodes.EditorSessionInvalid);
            }

            return EditorResult<EditorSession>.Ok(session);
        }
    }

    private bool ProjectMatches(string? envelopeProjectId) =>
        string.IsNullOrWhiteSpace(envelopeProjectId)
        || StringComparer.OrdinalIgnoreCase.Equals(envelopeProjectId, projectId);

    private static bool IsUser(CallerPrincipal principal) =>
        principal.Kind == PrincipalKind.UserInteractive;

    private EditorResult<TextDecodeResult> ReadDocx(byte[] bytes)
    {
        var read = docx.Read(bytes);
        if (!read.Succeeded)
        {
            return EditorResult<TextDecodeResult>.Fail(ToError(read.Failure!.Value));
        }

        return EditorResult<TextDecodeResult>.Ok(new TextDecodeResult(read.Value!.LogicalText, false, false));
    }

    private static string ToError(DocxDocumentFailure failure) => failure switch
    {
        DocxDocumentFailure.MalformedXml => IpcErrorCodes.DocxMalformedXml,
        DocxDocumentFailure.ExternalRelationship => IpcErrorCodes.DocxExternalRelationship,
        DocxDocumentFailure.Encrypted => IpcErrorCodes.DocxEncrypted,
        DocxDocumentFailure.AdapterUnavailable => IpcErrorCodes.DocxAdapterUnavailable,
        DocxDocumentFailure.Oversized => IpcErrorCodes.EditorDocumentTooLarge,
        DocxDocumentFailure.UnsupportedFeature => IpcErrorCodes.DocxUnsupportedFeature,
        _ => IpcErrorCodes.DocxMalformedPackage
    };

    private static OpenDraftEditorSessionResponse ToOpenResponse(EditorSession session, long length) =>
        new(
            session.EditorSessionId,
            session.ProjectId,
            session.Document.ChapterId,
            session.Document.DraftFileName,
            session.Document.RelativePath,
            session.Document.FormatKind,
            session.BaseDiskDigest,
            session.LastPersistedDigest,
            session.LastPersistedRevision,
            session.LeaseOwnerKind,
            session.Writable,
            session.Document.LogicalTitle,
            length);
}
