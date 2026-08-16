using System.Text.Json;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public sealed class Wp14IpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly ProviderInvocationStateHandler handler;
    private readonly string workspaceInstanceId;

    public Wp14IpcCommandHandler(ProviderInvocationStateHandler handler, string workspaceInstanceId)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        this.workspaceInstanceId = workspaceInstanceId;
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return Task.FromResult(Handle(context));
        }
        catch (ProviderInvocationDeniedException exception)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(
                Error(context, exception.Code, exception.Detail));
        }
        catch (InvalidOperationException exception) when (exception.Message == IpcErrorCodes.ProviderSecretForbidden)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(
                Error(context, IpcErrorCodes.ProviderSecretForbidden, "Provider secrets must not cross Core IPC."));
        }
        catch (JsonException)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(
                Error(context, IpcErrorCodes.MalformedFrame, "The WP14 command payload is malformed."));
        }
    }

    private IpcApplicationCommandResult? Handle(IpcApplicationCommandContext context) =>
        context.SemanticType switch
        {
            IpcSemanticTypes.GetTaskExecutionSnapshot => Snapshot(context),
            IpcSemanticTypes.PersistProviderInvocation => Persist(context),
            IpcSemanticTypes.AuthorizeToolProposal => Authorize(context),
            _ => null
        };

    private IpcApplicationCommandResult Snapshot(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetTaskExecutionSnapshotRequest);
        var response = handler.GetSnapshot(request, context.Principal, context.Channel);
        return Respond(context, response, IpcJsonContext.Default.GetTaskExecutionSnapshotResponseEnvelope);
    }

    private IpcApplicationCommandResult Persist(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.PersistProviderInvocationRequest);
        var response = handler.Persist(request, context.Principal, context.Channel);
        return Respond(context, response, IpcJsonContext.Default.PersistProviderInvocationResponseEnvelope);
    }

    private IpcApplicationCommandResult Authorize(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.AuthorizeToolProposalRequest);
        var response = handler.Authorize(request, context.Principal, context.Channel);
        return Respond(context, response, IpcJsonContext.Default.AuthorizeToolProposalResponseEnvelope);
    }

    private IpcApplicationCommandResult Respond<T>(
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
}
