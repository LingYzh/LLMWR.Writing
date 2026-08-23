using LLMW.Writing.Application.Editor;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public sealed class EditorRuntimeHolder
{
    private EditorRuntime? current;

    public EditorRuntime? Current => Volatile.Read(ref current);

    public void PublishOnce(EditorRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (Interlocked.CompareExchange(ref current, runtime, null) is not null)
        {
            throw new InvalidOperationException("Editor runtime is already published.");
        }
    }

    public bool TryAbandon(EditorRuntime expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ReferenceEquals(Interlocked.CompareExchange(ref current, null, expected), expected);
    }

    public void ReleaseByConnection(string connectionId)
    {
        Current?.ReleaseByConnection(connectionId);
    }
}

public sealed class Wp16IpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly EditorRuntimeHolder editors;
    private readonly string workspaceInstanceId;

    public Wp16IpcCommandHandler(EditorRuntimeHolder editors, string workspaceInstanceId)
    {
        this.editors = editors ?? throw new ArgumentNullException(nameof(editors));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        this.workspaceInstanceId = workspaceInstanceId;
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsEditorCommand(context.SemanticType))
        {
            return Task.FromResult<IpcApplicationCommandResult?>(null);
        }

        try
        {
            return Task.FromResult<IpcApplicationCommandResult?>(Handle(context));
        }
        catch (EditorSaveFaultInjectedException)
        {
            throw;
        }
        catch (System.Text.Json.JsonException)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(
                Error(context, IpcErrorCodes.MalformedFrame, "The editor command payload is malformed."));
        }
    }

    private IpcApplicationCommandResult Handle(IpcApplicationCommandContext context)
    {
        if (context.ClientKind != IpcClientKind.Ui)
        {
            return Error(context, IpcErrorCodes.EditorSessionInvalid, "Editor commands require USER_INTERACTIVE.");
        }

        if (context.Principal is null)
        {
            return Error(context, IpcErrorCodes.InvalidSession, "UI principal is unavailable.");
        }

        var runtime = editors.Current;
        if (runtime is null)
        {
            return Error(context, IpcErrorCodes.CommandUnavailable, "Editor runtime is unavailable until a project is open.");
        }

        return context.SemanticType switch
        {
            IpcSemanticTypes.OpenDraftEditorSession => Open(runtime, context),
            IpcSemanticTypes.GetDraftEditorSessionState => GetState(runtime, context),
            IpcSemanticTypes.ReleaseDraftEditorSession => Release(runtime, context),
            IpcSemanticTypes.BeginEditorContentDownload => BeginDownload(runtime, context),
            IpcSemanticTypes.EditorContentDownloadChunk => DownloadChunk(runtime, context),
            IpcSemanticTypes.BeginEditorContentUpload => BeginUpload(runtime, context),
            IpcSemanticTypes.EditorContentUploadChunk => Chunk(runtime, context),
            IpcSemanticTypes.CommitEditorContentUpload => Commit(runtime, context),
            IpcSemanticTypes.SaveDraftEditorSession => Save(runtime, context),
            IpcSemanticTypes.RestoreHistoryEntry => RestoreHistory(runtime, context),
            _ => Error(context, IpcErrorCodes.CommandUnavailable, "Unknown editor command.")
        };
    }

    private IpcApplicationCommandResult Open(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.OpenDraftEditorSessionRequest);
        var result = runtime.Open(
            context.Principal!,
            context.ConnectionId,
            context.EnvelopeProjectId?.ToString("D"),
            request.ChapterId,
            request.DraftFileName,
            request.RequestWritable);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult GetState(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetDraftEditorSessionStateRequest);
        var result = runtime.GetState(context.Principal!, context.ConnectionId, request.EditorSessionId);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.GetDraftEditorSessionStateResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult Release(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ReleaseDraftEditorSessionRequest);
        var result = runtime.Release(context.Principal!, context.ConnectionId, request.EditorSessionId);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.ReleaseDraftEditorSessionResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult BeginUpload(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.BeginEditorContentUploadRequest);
        var result = runtime.BeginUpload(context.Principal!, context.ConnectionId, request);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.BeginEditorContentUploadResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult BeginDownload(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.BeginEditorContentDownloadRequest);
        var result = runtime.BeginDownload(context.Principal!, context.ConnectionId, request);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.BeginEditorContentDownloadResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult DownloadChunk(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.EditorContentDownloadChunkRequest);
        var result = runtime.DownloadChunk(context.Principal!, context.ConnectionId, request);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.EditorContentDownloadChunkResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult Chunk(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.EditorContentUploadChunkRequest);
        var result = runtime.UploadChunk(context.Principal!, context.ConnectionId, request);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.EditorContentUploadChunkResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult Commit(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CommitEditorContentUploadRequest);
        var result = runtime.CommitUpload(context.Principal!, context.ConnectionId, request, context.CancellationToken);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.CommitEditorContentUploadResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult Save(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.SaveDraftEditorSessionRequest);
        try
        {
            var result = runtime.Save(context.Principal!, context.ConnectionId, request, context.CancellationToken);
            return result.Succeeded
                ? Ok(context, result.Value!, IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope)
                : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
        }
        catch (EditorSaveFaultInjectedException exception) when (exception.Point == EditorSaveFaultPoint.BeforeIpcResponse)
        {
            return Error(context, IpcErrorCodes.EditorSaveOutcomeUnknown, "Save outcome is unknown because the response was lost.");
        }
    }

    private IpcApplicationCommandResult RestoreHistory(EditorRuntime runtime, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.RestoreHistoryEntryRequest);
        var result = runtime.RestoreHistory(
            context.Principal!,
            context.ConnectionId,
            context.EnvelopeProjectId?.ToString("D"),
            request,
            context.CancellationToken);
        return result.Succeeded
            ? Ok(context, result.Value!, IpcJsonContext.Default.RestoreHistoryEntryResponseEnvelope)
            : Error(context, result.ErrorCode!, Safe(result.ErrorCode!));
    }

    private IpcApplicationCommandResult Ok<T>(
        IpcApplicationCommandContext context,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<T>> typeInfo) =>
        new(IpcJson.Serialize(
            IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                context.SemanticType,
                workspaceInstanceId,
                payload,
                context.EnvelopeProjectId,
                context.EnvelopeRunId,
                context.CorrelationId,
                context.RequestId),
            typeInfo));

    private IpcApplicationCommandResult Error(IpcApplicationCommandContext context, string code, string message) =>
        new(IpcJson.Serialize(
            IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                context.SemanticType,
                workspaceInstanceId,
                new IpcError(code, message, null, false),
                context.EnvelopeProjectId,
                context.EnvelopeRunId,
                context.CorrelationId,
                context.RequestId),
            IpcJsonContext.Default.ErrorEnvelope));

    private static bool IsEditorCommand(string semanticType) => semanticType is
        IpcSemanticTypes.OpenDraftEditorSession or
        IpcSemanticTypes.GetDraftEditorSessionState or
        IpcSemanticTypes.ReleaseDraftEditorSession or
        IpcSemanticTypes.BeginEditorContentDownload or
        IpcSemanticTypes.EditorContentDownloadChunk or
        IpcSemanticTypes.BeginEditorContentUpload or
        IpcSemanticTypes.EditorContentUploadChunk or
        IpcSemanticTypes.CommitEditorContentUpload or
        IpcSemanticTypes.SaveDraftEditorSession or
        IpcSemanticTypes.RestoreHistoryEntry;

    private static string Safe(string code) => code switch
    {
        IpcErrorCodes.EditorLeaseConflict => "Another writer holds the Draft lease.",
        IpcErrorCodes.EditorLeaseLost => "The editor lease is no longer held.",
        IpcErrorCodes.EditorStaleBase => "The Draft changed since this editor baseline.",
        IpcErrorCodes.EditorDocumentNotWritable => "The document is not a writable Draft.",
        IpcErrorCodes.EditorDocumentTooLarge => "The editor document exceeds the resource bound.",
        IpcErrorCodes.EditorEncodingUnsupported => "The Draft encoding is not supported UTF-8.",
        IpcErrorCodes.EditorUploadHashMismatch => "Uploaded editor content failed hash verification.",
        IpcErrorCodes.EditorSaveIdentityConflict => "The save operation identity does not match prior content.",
        IpcErrorCodes.EditorSessionInvalid => "The editor session is not valid.",
        IpcErrorCodes.EditorUploadInvalid => "The editor upload is invalid.",
        IpcErrorCodes.HistoryRestoreConflict => "The Draft changed since the selected history base.",
        IpcErrorCodes.HistoryEntryNotFound => "The selected local-history entry does not exist.",
        IpcErrorCodes.HistoryEntryInvalid => "The selected local-history entry is not valid for this Draft.",
        IpcErrorCodes.HistoryStorageFailure => "The local-history entry could not be verified.",
        _ => "The editor command was denied."
    };
}
