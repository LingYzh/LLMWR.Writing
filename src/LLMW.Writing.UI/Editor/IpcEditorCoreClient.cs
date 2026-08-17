using LLMW.Writing.Application.Ipc;
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
        CancellationToken cancellationToken)
    {
        var response = await _session.RequestAsync(
                IpcSemanticTypes.SaveDraftEditorSession,
                new SaveDraftEditorSessionRequest(editorSessionId, saveOperationId, expectedPersistedDigest, content),
                IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
                cancellationToken,
                _projectId)
            .ConfigureAwait(false);
        return response.Payload;
    }
}
