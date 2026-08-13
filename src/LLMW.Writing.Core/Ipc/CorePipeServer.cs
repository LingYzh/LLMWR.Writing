using System.IO.Pipes;
using System.Text.Json;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Core.Ipc;

internal sealed class CorePipeServer
{
    private static readonly string[] ServerCapabilities = ["heartbeat"];

    private readonly string _pipeName;
    private readonly IpcClientKind _expectedClientKind;
    private readonly string _bootstrapToken;

    public CorePipeServer(string pipeName, IpcClientKind expectedClientKind, string bootstrapToken)
    {
        _pipeName = pipeName;
        _expectedClientKind = expectedClientKind;
        _bootstrapToken = bootstrapToken;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeConnectionAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // The connection is intentionally isolated. A fresh pipe instance accepts a reconnect.
            }
            catch (JsonException)
            {
                // Invalid JSON is handled with an error envelope where framing permits, then disconnected.
            }
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        IpcEnvelope<HelloRequest>? hello;
        try
        {
            hello = await IpcPipeTransport.ReadAsync(stream, IpcJsonContext.Default.HelloRequestEnvelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (hello.MessageType != IpcMessageType.Control)
        {
            await WriteErrorAsync(stream, hello, IpcErrorCodes.UnexpectedMessage, "The first IPC message must be Hello.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!IpcBootstrapToken.FixedTimeEquals(_bootstrapToken, hello.Payload.BootstrapToken))
        {
            await WriteErrorAsync(stream, hello, IpcErrorCodes.AuthBootstrapRejected, "Bootstrap authentication was rejected.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (hello.Payload.ClientKind != _expectedClientKind)
        {
            await WriteErrorAsync(stream, hello, IpcErrorCodes.AuthBootstrapRejected, "The bootstrap token is not valid for this IPC client kind.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!IpcProtocol.TryNegotiate(hello.Payload.ProtocolMin, hello.Payload.ProtocolMax, out var negotiatedProtocol))
        {
            await WriteErrorAsync(stream, hello, IpcErrorCodes.ProtocolNoCommonVersion, "No common IPC protocol version is available.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var ack = new HelloAck(negotiatedProtocol, ServerCapabilities);
        await IpcPipeTransport.WriteAsync(
                stream,
                IpcEnvelopeFactory.Create(IpcMessageType.Control, hello.WorkspaceInstanceId, ack, correlationId: hello.CorrelationId),
                IpcJsonContext.Default.HelloAckEnvelope,
                cancellationToken)
            .ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            IpcEnvelope<Heartbeat>? heartbeat;
            try
            {
                heartbeat = await IpcPipeTransport.ReadAsync(stream, IpcJsonContext.Default.HeartbeatEnvelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            if (heartbeat.MessageType != IpcMessageType.Control)
            {
                await WriteErrorAsync(stream, heartbeat, IpcErrorCodes.UnexpectedMessage, "Only heartbeat is available from the WP01 process shell.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var heartbeatAck = new HeartbeatAck(heartbeat.Payload.Sequence);
            await IpcPipeTransport.WriteAsync(
                    stream,
                    IpcEnvelopeFactory.Create(
                        IpcMessageType.Control,
                        heartbeat.WorkspaceInstanceId,
                        heartbeatAck,
                        correlationId: heartbeat.CorrelationId),
                    IpcJsonContext.Default.HeartbeatAckEnvelope,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Task WriteErrorAsync(
        Stream stream,
        IpcEnvelope<HelloRequest> request,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        WriteErrorAsync(
            stream,
            request.WorkspaceInstanceId,
            request.CorrelationId,
            code,
            message,
            cancellationToken);

    private static Task WriteErrorAsync(
        Stream stream,
        IpcEnvelope<Heartbeat> request,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        WriteErrorAsync(
            stream,
            request.WorkspaceInstanceId,
            request.CorrelationId,
            code,
            message,
            cancellationToken);

    private static Task WriteErrorAsync(
        Stream stream,
        string workspaceInstanceId,
        Guid correlationId,
        string code,
        string message,
        CancellationToken cancellationToken) =>
        IpcPipeTransport.WriteAsync(
            stream,
            IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                workspaceInstanceId,
                new IpcError(code, message, null, false),
                correlationId: correlationId),
            IpcJsonContext.Default.ErrorEnvelope,
            cancellationToken);
}

internal static class IpcPipeTransport
{
    public static async Task WriteAsync<TPayload>(
        Stream stream,
        IpcEnvelope<TPayload> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = IpcJson.Serialize(envelope, typeInfo);
        var header = IpcFrameHeader.Create(payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
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
        var payloadLength = IpcFrameHeader.Parse(header);
        var payload = new byte[payloadLength];
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
                throw new EndOfStreamException("IPC stream closed before a complete frame was received.");
            }

            totalRead += read;
        }
    }
}
