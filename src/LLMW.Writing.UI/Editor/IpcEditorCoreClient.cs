using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Editor;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.UI.Editor;

internal interface IEditorCoreClient
{
    Guid ProjectId { get; }

    string ProjectRoot { get; }

    Task<OpenDraftEditorSessionResponse> OpenAsync(
        string chapterId,
        string draftFileName,
        bool requestWritable,
        CancellationToken cancellationToken);

    Task<GetDraftEditorSessionStateResponse> GetStateAsync(string editorSessionId, CancellationToken cancellationToken);

    Task ReleaseAsync(string editorSessionId, CancellationToken cancellationToken);

    Task<string> DownloadLogicalTextAsync(string editorSessionId, string expectedPersistedDigest, CancellationToken cancellationToken);

    Task<IpcBlobRef> UploadAsync(
        string editorSessionId,
        string saveOperationId,
        byte[] utf8NoBomLf,
        CancellationToken cancellationToken);

    Task<SaveDraftEditorSessionResponse> SaveAsync(
        string editorSessionId,
        string saveOperationId,
        string expectedPersistedDigest,
        IpcBlobRef content,
        HistoryCheckpointTriggerKind checkpointTrigger,
        CancellationToken cancellationToken);

    Task<RestoreHistoryEntryResponse> RestoreHistoryAsync(
        string historyId,
        string editorSessionId,
        string expectedPersistedDigest,
        CancellationToken cancellationToken);
}

internal sealed class IpcEditorCoreClient : IEditorCoreClient
{
    private readonly IpcClientSession _session;
    private readonly Guid _projectId;
    private readonly string _projectRoot;

    public IpcEditorCoreClient(IpcClientSession session, Guid projectId, string projectRoot)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _projectId = projectId;
        _projectRoot = projectRoot;
    }

    public Guid ProjectId => _projectId;

    public string ProjectRoot => _projectRoot;

    public async Task<OpenDraftEditorSessionResponse> OpenAsync(
        string chapterId,
        string draftFileName,
        bool requestWritable,
        CancellationToken cancellationToken)
    {
        var response = await _session.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(chapterId, draftFileName, requestWritable),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
        return response.Payload;
    }

    public async Task<GetDraftEditorSessionStateResponse> GetStateAsync(
        string editorSessionId,
        CancellationToken cancellationToken)
    {
        var response = await _session.RequestAsync(
                IpcSemanticTypes.GetDraftEditorSessionState,
                new GetDraftEditorSessionStateRequest(editorSessionId),
                IpcJsonContext.Default.GetDraftEditorSessionStateRequestEnvelope,
                IpcJsonContext.Default.GetDraftEditorSessionStateResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
        return response.Payload;
    }

    public async Task ReleaseAsync(string editorSessionId, CancellationToken cancellationToken)
    {
        await _session.RequestAsync(
                IpcSemanticTypes.ReleaseDraftEditorSession,
                new ReleaseDraftEditorSessionRequest(editorSessionId),
                IpcJsonContext.Default.ReleaseDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.ReleaseDraftEditorSessionResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
    }

    public async Task<string> DownloadLogicalTextAsync(string editorSessionId, string expectedPersistedDigest, CancellationToken cancellationToken)
    {
        var begin = await _session.RequestAsync(
                IpcSemanticTypes.BeginEditorContentDownload,
                new BeginEditorContentDownloadRequest(editorSessionId, expectedPersistedDigest),
                IpcJsonContext.Default.BeginEditorContentDownloadRequestEnvelope,
                IpcJsonContext.Default.BeginEditorContentDownloadResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
        var bytes = new byte[begin.Payload.DeclaredUtf8Length];
        for (var index = 0; index < begin.Payload.ChunkCount; index++)
        {
            var chunk = await _session.RequestAsync(
                    IpcSemanticTypes.EditorContentDownloadChunk,
                    new EditorContentDownloadChunkRequest(begin.Payload.DownloadId, index),
                    IpcJsonContext.Default.EditorContentDownloadChunkRequestEnvelope,
                    IpcJsonContext.Default.EditorContentDownloadChunkResponseEnvelope,
                    cancellationToken,
                    _projectId)
                .ConfigureAwait(false);
            var decoded = Convert.FromBase64String(chunk.Payload.DataBase64);
            var offset = index * begin.Payload.MaxChunkBytes;
            if (chunk.Payload.ChunkIndex != index || decoded.Length > begin.Payload.MaxChunkBytes || offset + decoded.Length > bytes.Length)
            {
                throw new InvalidOperationException("Core returned an invalid document-content chunk.");
            }

            decoded.CopyTo(bytes, offset);
        }

        if (!StringComparer.Ordinal.Equals(ContentDigest.Sha256Hex(bytes), begin.Payload.DeclaredSha256))
        {
            throw new InvalidOperationException("Core document-content digest verification failed.");
        }

        var text = TextDocumentCodec.TryDecode(bytes);
        if (!text.Succeeded)
        {
            throw new InvalidOperationException(text.ErrorCode);
        }

        return text.Value!.LogicalText;
    }

    public async Task<IpcBlobRef> UploadAsync(
        string editorSessionId,
        string saveOperationId,
        byte[] utf8NoBomLf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(utf8NoBomLf);
        var digest = ContentDigest.Sha256Hex(utf8NoBomLf);
        var begin = await _session.RequestAsync(
                IpcSemanticTypes.BeginEditorContentUpload,
                new BeginEditorContentUploadRequest(editorSessionId, saveOperationId, utf8NoBomLf.Length, digest),
                IpcJsonContext.Default.BeginEditorContentUploadRequestEnvelope,
                IpcJsonContext.Default.BeginEditorContentUploadResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);

        var chunkSize = Math.Min(begin.Payload.MaxChunkBytes, EditorTransportLimits.MaximumChunkUtf8Bytes);
        var count = utf8NoBomLf.Length == 0
            ? 0
            : (utf8NoBomLf.Length + chunkSize - 1) / chunkSize;
        for (var index = 0; index < count; index++)
        {
            var offset = index * chunkSize;
            var length = Math.Min(chunkSize, utf8NoBomLf.Length - offset);
            await _session.RequestAsync(
                    IpcSemanticTypes.EditorContentUploadChunk,
                    new EditorContentUploadChunkRequest(
                        begin.Payload.UploadId,
                        index,
                        count,
                        Convert.ToBase64String(utf8NoBomLf, offset, length)),
                    IpcJsonContext.Default.EditorContentUploadChunkRequestEnvelope,
                    IpcJsonContext.Default.EditorContentUploadChunkResponseEnvelope,
                    cancellationToken,
                    _projectId)
                .ConfigureAwait(false);
        }

        var committed = await _session.RequestAsync(
                IpcSemanticTypes.CommitEditorContentUpload,
                new CommitEditorContentUploadRequest(begin.Payload.UploadId),
                IpcJsonContext.Default.CommitEditorContentUploadRequestEnvelope,
                IpcJsonContext.Default.CommitEditorContentUploadResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
        return committed.Payload.BlobRef;
    }

    public async Task<SaveDraftEditorSessionResponse> SaveAsync(
        string editorSessionId,
        string saveOperationId,
        string expectedPersistedDigest,
        IpcBlobRef content,
        HistoryCheckpointTriggerKind checkpointTrigger,
        CancellationToken cancellationToken)
    {
        var response = await _session.RequestAsync(
                IpcSemanticTypes.SaveDraftEditorSession,
                new SaveDraftEditorSessionRequest(editorSessionId, saveOperationId, expectedPersistedDigest, content, checkpointTrigger),
                IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
        return response.Payload;
    }

    public async Task<RestoreHistoryEntryResponse> RestoreHistoryAsync(
        string historyId,
        string editorSessionId,
        string expectedPersistedDigest,
        CancellationToken cancellationToken)
    {
        var response = await _session.RequestAsync(
                IpcSemanticTypes.RestoreHistoryEntry,
                new RestoreHistoryEntryRequest(historyId, editorSessionId, expectedPersistedDigest),
                IpcJsonContext.Default.RestoreHistoryEntryRequestEnvelope,
                IpcJsonContext.Default.RestoreHistoryEntryResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
        return response.Payload;
    }
}
