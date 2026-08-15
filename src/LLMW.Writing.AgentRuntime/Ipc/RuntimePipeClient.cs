using System.IO.Pipes;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.AgentRuntime.Ipc;

internal sealed class RuntimePipeClient
{
    private readonly string _workspaceInstanceId;
    private readonly string _launcherBootstrapToken;
    private readonly TimeSpan _heartbeatInterval;

    public RuntimePipeClient(string workspaceInstanceId, string bootstrapToken, TimeSpan heartbeatInterval)
    {
        _workspaceInstanceId = workspaceInstanceId;
        _launcherBootstrapToken = bootstrapToken;
        _heartbeatInterval = heartbeatInterval;
    }

    public async Task RunWithReconnectAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(IpcProtocol.ReconnectInitialBackoffMs);
        var currentToken = _launcherBootstrapToken;
        var retriedLauncherSecret = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    IpcPipeNames.Runtime(_workspaceInstanceId),
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await using var session = await IpcClientSession.HandshakeAsync(
                        client,
                        _workspaceInstanceId,
                        currentToken,
                        IpcClientKind.AgentRuntime,
                        _heartbeatInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(session.RotatedBootstrapToken))
                {
                    currentToken = session.RotatedBootstrapToken;
                    retriedLauncherSecret = false;
                }

                retryDelay = TimeSpan.FromMilliseconds(IpcProtocol.ReconnectInitialBackoffMs);
                await WaitUntilDisconnectedAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (IpcProtocolException exception) when (
                exception.ErrorCode is IpcErrorCodes.AuthBootstrapRejected or IpcErrorCodes.AuthBootstrapReplay &&
                !retriedLauncherSecret &&
                !StringComparer.Ordinal.Equals(currentToken, _launcherBootstrapToken))
            {
                currentToken = _launcherBootstrapToken;
                retriedLauncherSecret = true;
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                await DelayBeforeReconnectAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = NextRetryDelay(retryDelay);
            }
            catch (System.Text.Json.JsonException)
            {
                await DelayBeforeReconnectAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = NextRetryDelay(retryDelay);
            }
        }
    }

    private static async Task WaitUntilDisconnectedAsync(IpcClientSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.Events.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task DelayBeforeReconnectAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var jitterMilliseconds = Random.Shared.Next(0, Math.Max(1, (int)(delay.TotalMilliseconds / 4)));
        await Task.Delay(delay + TimeSpan.FromMilliseconds(jitterMilliseconds), cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan NextRetryDelay(TimeSpan current) =>
        TimeSpan.FromMilliseconds(Math.Min(IpcProtocol.ReconnectMaximumBackoffMs, current.TotalMilliseconds * 2));
}
