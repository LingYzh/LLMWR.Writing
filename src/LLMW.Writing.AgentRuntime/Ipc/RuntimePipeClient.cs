using System.IO.Pipes;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.AgentRuntime.Ipc;

internal sealed class RuntimePipeClient
{
    private readonly IpcReconnectClient _reconnect;

    public RuntimePipeClient(string workspaceInstanceId, string bootstrapToken, TimeSpan heartbeatInterval)
    {
        var recovery = new IpcTransportRecovery
        {
            AfterRestoreAsync = async (session, cancellationToken) =>
            {
                try
                {
                    await session.RequestAsync(
                            IpcSemanticTypes.LoadSchedulerSnapshot,
                            new LoadSchedulerSnapshotRequest(null),
                            IpcJsonContext.Default.LoadSchedulerSnapshotRequestEnvelope,
                            IpcJsonContext.Default.LoadSchedulerSnapshotResponseEnvelope,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IpcProtocolException exception) when (
                    exception.ErrorCode is IpcErrorCodes.CommandUnavailable or IpcErrorCodes.TrustedBindingUnavailable)
                {
                }
            }
        };
        _reconnect = new IpcReconnectClient(
            token => ConnectAsync(workspaceInstanceId, token),
            workspaceInstanceId,
            bootstrapToken,
            IpcClientKind.AgentRuntime,
            heartbeatInterval,
            recovery);
    }

    public Task RunWithReconnectAsync(CancellationToken cancellationToken) =>
        _reconnect.RunAsync(cancellationToken);

    private static async Task<Stream> ConnectAsync(string workspaceInstanceId, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".",
            IpcPipeNames.Runtime(workspaceInstanceId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
