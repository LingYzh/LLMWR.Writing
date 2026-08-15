using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// Production Runtime reconnect loop. Safe automatic recovery is Hello, heartbeat,
/// GetStateSnapshot, and SubscribeEvents only.
/// </summary>
public sealed class IpcReconnectClient
{
    private readonly Func<CancellationToken, Task<Stream>> connectAsync;
    private readonly string workspaceInstanceId;
    private readonly string launcherBootstrapToken;
    private readonly IpcClientKind clientKind;
    private readonly TimeSpan heartbeatInterval;

    public IpcReconnectClient(
        Func<CancellationToken, Task<Stream>> connectAsync,
        string workspaceInstanceId,
        string launcherBootstrapToken,
        IpcClientKind clientKind,
        TimeSpan heartbeatInterval,
        IpcTransportRecovery? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(connectAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherBootstrapToken);
        this.connectAsync = connectAsync;
        this.workspaceInstanceId = workspaceInstanceId;
        this.launcherBootstrapToken = launcherBootstrapToken;
        this.clientKind = clientKind;
        this.heartbeatInterval = heartbeatInterval;
        Recovery = recovery ?? new IpcTransportRecovery();
    }

    public IpcTransportRecovery Recovery { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(IpcProtocol.ReconnectInitialBackoffMs);
        var currentToken = launcherBootstrapToken;
        var retriedLauncherSecret = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            Stream? stream = null;
            try
            {
                stream = await connectAsync(cancellationToken).ConfigureAwait(false);
                var session = await IpcClientSession.HandshakeAsync(
                        stream,
                        workspaceInstanceId,
                        currentToken,
                        clientKind,
                        heartbeatInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                await using (session.ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(session.RotatedBootstrapToken))
                    {
                        currentToken = session.RotatedBootstrapToken;
                        retriedLauncherSecret = false;
                    }

                    retryDelay = TimeSpan.FromMilliseconds(IpcProtocol.ReconnectInitialBackoffMs);
                    await Recovery.RunSessionAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (IpcProtocolException exception) when (
                exception.ErrorCode is IpcErrorCodes.AuthBootstrapRejected or IpcErrorCodes.AuthBootstrapReplay &&
                !retriedLauncherSecret &&
                !StringComparer.Ordinal.Equals(currentToken, launcherBootstrapToken))
            {
                currentToken = launcherBootstrapToken;
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
            catch (System.Threading.Channels.ChannelClosedException)
            {
                await DelayBeforeReconnectAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = NextRetryDelay(retryDelay);
            }
            catch (System.Text.Json.JsonException)
            {
                await DelayBeforeReconnectAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = NextRetryDelay(retryDelay);
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
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
