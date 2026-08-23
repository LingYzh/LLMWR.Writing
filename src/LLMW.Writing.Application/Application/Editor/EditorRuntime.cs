using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Editor;

public sealed class EditorRuntime
{
    private readonly string projectId;
    private readonly IDraftFileStore files;
    private readonly EditorLeaseCoordinator leases = new();
    private readonly EditorContentUploadService uploads;
    private readonly DraftSaveService saves;
    private readonly Dictionary<string, EditorSession> sessions = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public EditorRuntime(
        string projectId,
        IDraftFileStore files,
        IImmutableBlobStore blobs,
        IEditorSaveFaultInjector? faults = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        this.projectId = projectId;
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        var injector = faults ?? NoEditorSaveFaultInjector.Instance;
        uploads = new EditorContentUploadService(blobs, injector);
        saves = new DraftSaveService(files, blobs, leases, injector);
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

        var decode = TextDocumentCodec.TryDecode(snapshot.Value!.Bytes);
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
        }

        return EditorResult<ReleaseDraftEditorSessionResponse>.Ok(new ReleaseDraftEditorSessionResponse(true));
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
