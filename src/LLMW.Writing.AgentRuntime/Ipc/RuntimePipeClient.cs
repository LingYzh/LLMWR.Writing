using System.IO.Pipes;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.AgentRuntime.Ipc;

internal sealed class RuntimePipeClient
{
    private readonly string _workspaceInstanceId;
    private readonly string _bootstrapToken;
    private readonly TimeSpan _heartbeatInterval;

    public RuntimePipeClient(string workspaceInstanceId, string bootstrapToken, TimeSpan heartbeatInterval)
    {
        _workspaceInstanceId = workspaceInstanceId;
        _bootstrapToken = bootstrapToken;
        _heartbeatInterval = heartbeatInterval;
    }

    public async Task RunWithReconnectAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(100);

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
                await AuthenticateAndHeartbeatAsync(client, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(100);
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

    private async Task AuthenticateAndHeartbeatAsync(Stream stream, CancellationToken cancellationToken)
    {
        var hello = new HelloRequest(
            IpcProtocol.MinimumSupportedVersion,
            IpcProtocol.MaximumSupportedVersion,
            _bootstrapToken,
            IpcClientKind.AgentRuntime,
            Guid.NewGuid());
        await RuntimePipeTransport.WriteAsync(
                stream,
                IpcEnvelopeFactory.Create(IpcMessageType.Control, _workspaceInstanceId, hello),
                IpcJsonContext.Default.HelloRequestEnvelope,
                cancellationToken)
            .ConfigureAwait(false);

        var acknowledgement = await RuntimePipeTransport.ReadAsync(
                stream,
                IpcJsonContext.Default.HelloAckEnvelope,
                cancellationToken)
            .ConfigureAwait(false);
        if (acknowledgement.Payload.NegotiatedProtocol != IpcProtocol.Version1)
        {
            throw new IOException("Core selected an unsupported IPC protocol.");
        }

        var sequence = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
            sequence++;
            await RuntimePipeTransport.WriteAsync(
                    stream,
                    IpcEnvelopeFactory.Create(IpcMessageType.Control, _workspaceInstanceId, new Heartbeat(sequence)),
                    IpcJsonContext.Default.HeartbeatEnvelope,
                    cancellationToken)
                .ConfigureAwait(false);
            var heartbeatAck = await RuntimePipeTransport.ReadAsync(
                    stream,
                    IpcJsonContext.Default.HeartbeatAckEnvelope,
                    cancellationToken)
                .ConfigureAwait(false);
            if (heartbeatAck.Payload.Sequence != sequence)
            {
                throw new IOException("Core acknowledged an unexpected heartbeat sequence.");
            }
        }
    }

    private static async Task DelayBeforeReconnectAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var jitterMilliseconds = Random.Shared.Next(0, Math.Max(1, (int)(delay.TotalMilliseconds / 4)));
        await Task.Delay(delay + TimeSpan.FromMilliseconds(jitterMilliseconds), cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan NextRetryDelay(TimeSpan current) =>
        TimeSpan.FromMilliseconds(Math.Min(5000, current.TotalMilliseconds * 2));
}

internal static class RuntimePipeTransport
{
    public static async Task WriteAsync<TPayload>(
        Stream stream,
        IpcEnvelope<TPayload> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = IpcJson.Serialize(envelope, typeInfo);
        await stream.WriteAsync(IpcFrameHeader.Create(payload.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IpcEnvelope<TPayload>> ReadAsync<TPayload>(
        Stream stream,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var payload = new byte[IpcFrameHeader.Parse(header)];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return IpcJson.Deserialize(payload, typeInfo);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Core closed the IPC channel.");
            }

            totalRead += read;
        }
    }
}
