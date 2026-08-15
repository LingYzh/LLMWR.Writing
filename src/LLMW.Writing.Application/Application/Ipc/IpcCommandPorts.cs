using System.Text.Json;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public interface IIpcStateSnapshotProvider
{
    IpcTransportSnapshot Create();
}

public sealed class TransportOnlySnapshotProvider : IIpcStateSnapshotProvider
{
    public static TransportOnlySnapshotProvider Instance { get; } = new();

    private TransportOnlySnapshotProvider()
    {
    }

    public IpcTransportSnapshot Create() => new(IpcProtocol.Version1, IpcServerCapabilities.V1);
}

public sealed record IpcApplicationCommandContext(
    IpcClientKind ClientKind,
    string ConnectionId,
    LLMW.Writing.Application.Security.AuthenticatedChannelContext? Channel,
    LLMW.Writing.Application.Security.CallerPrincipal? Principal,
    Guid RequestId,
    Guid CorrelationId,
    Guid? EnvelopeProjectId,
    Guid? EnvelopeRunId,
    string SemanticType,
    JsonElement Payload,
    CancellationToken CancellationToken);

public sealed record IpcApplicationCommandResult(byte[] ResponseUtf8);

public interface IIpcApplicationCommandHandler
{
    Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context);
}

public sealed class UnavailableIpcCommandHandler : IIpcApplicationCommandHandler
{
    public static UnavailableIpcCommandHandler Instance { get; } = new();

    private UnavailableIpcCommandHandler()
    {
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context) =>
        Task.FromResult<IpcApplicationCommandResult?>(null);
}
