using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public sealed class IpcClientSession : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly string workspaceInstanceId;
    private readonly TimeSpan heartbeatInterval;
    private readonly Channel<byte[]> writes = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(IpcProtocol.CriticalOutboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Dictionary<Guid, TaskCompletionSource<IpcWireEnvelope>> pending = [];
    private readonly Channel<IpcWireEnvelope> events = Channel.CreateBounded<IpcWireEnvelope>(
        new BoundedChannelOptions(IpcProtocol.ClientEventBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly object gate = new();
    private readonly object discontinuityGate = new();
    private bool hasEventDiscontinuity;
    private readonly CancellationTokenSource lifetime = new();
    private Task? reader;
    private Task? writer;
    private Task? heartbeat;
    private long heartbeatSequence;
    private int disposed;

    private IpcClientSession(Stream stream, string workspaceInstanceId, TimeSpan heartbeatInterval)
    {
        this.stream = stream;
        this.workspaceInstanceId = workspaceInstanceId;
        this.heartbeatInterval = heartbeatInterval;
    }

    public string EventStreamId { get; private set; } = "";

    public string ConnectionId { get; private set; } = "";

    public string? RotatedBootstrapToken { get; private set; }

    public ChannelReader<IpcWireEnvelope> Events => events.Reader;

    public bool HasEventDiscontinuity
    {
        get
        {
            lock (discontinuityGate)
            {
                return hasEventDiscontinuity;
            }
        }
    }

    public event Action? LocalEventOverflow;

    public static async Task<IpcClientSession> HandshakeAsync(
        Stream stream,
        string workspaceInstanceId,
        string bootstrapToken,
        IpcClientKind clientKind,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var session = new IpcClientSession(stream, workspaceInstanceId, heartbeatInterval);
        var hello = new HelloRequest(
            IpcProtocol.MinimumSupportedVersion,
            IpcProtocol.MaximumSupportedVersion,
            bootstrapToken,
            clientKind,
            Guid.NewGuid());
        await IpcFrameIO.WriteAsync(
                stream,
                IpcJson.Serialize(
                    IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Hello, workspaceInstanceId, hello),
                    IpcJsonContext.Default.HelloRequestEnvelope),
                cancellationToken)
            .ConfigureAwait(false);

        var ackBytes = await IpcFrameIO.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        var wire = IpcJson.DeserializeWire(ackBytes);
        if (wire.SemanticType == IpcSemanticTypes.Hello && wire.MessageType == IpcMessageType.Response)
        {
            var error = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.IpcError);
            throw new IpcProtocolException(error.Code, error.Message);
        }

        var ack = IpcJson.Deserialize(ackBytes, IpcJsonContext.Default.HelloAckEnvelope);
        if (ack.Payload.NegotiatedProtocol != IpcProtocol.Version1)
        {
            throw new IpcProtocolException(IpcErrorCodes.ProtocolNoCommonVersion, "Core selected an unsupported IPC protocol.");
        }

        session.EventStreamId = ack.Payload.EventStreamId;
        session.ConnectionId = ack.Payload.ConnectionId;
        session.RotatedBootstrapToken = ack.Payload.RotatedBootstrapToken;
        session.Start(cancellationToken);
        return session;
    }

    public async Task<IpcEnvelope<TResponse>> RequestAsync<TRequest, TResponse>(
        string semanticType,
        TRequest payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TRequest>> requestInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<TResponse>> responseInfo,
        CancellationToken cancellationToken,
        Guid? projectId = null,
        Guid? runId = null)
    {
        var wire = await RequestWireAsync(
                IpcMessageType.Request,
                semanticType,
                IpcJson.Serialize(
                    IpcEnvelopeFactory.Create(
                        IpcMessageType.Request,
                        semanticType,
                        workspaceInstanceId,
                        payload,
                        projectId,
                        runId),
                    requestInfo),
                cancellationToken)
            .ConfigureAwait(false);

        if (wire.Payload.TryGetProperty("code", out _))
        {
            var error = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.IpcError);
            throw new IpcProtocolException(error.Code, error.Message);
        }

        return IpcJson.Deserialize(IpcJson.SerializeWire(wire), responseInfo);
    }

    public async Task<IpcWireEnvelope> RequestWireAsync(
        IpcMessageType messageType,
        string semanticType,
        byte[] utf8,
        CancellationToken cancellationToken)
    {
        var envelope = IpcJson.DeserializeWire(utf8);
        _ = messageType;
        _ = semanticType;
        var tcs = RegisterPending(envelope.RequestId);
        if (!writes.Writer.TryWrite(utf8))
        {
            CompletePending(envelope.RequestId);
            throw new IpcProtocolException(IpcErrorCodes.QueueOverload, "The IPC write queue is saturated.");
        }

        return await AwaitPendingAsync(envelope.RequestId, tcs, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CancelResponse> CancelAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var envelope = IpcEnvelopeFactory.Create(
            IpcMessageType.Control,
            IpcSemanticTypes.Cancel,
            workspaceInstanceId,
            new CancelRequest(correlationId));
        var tcs = RegisterPending(envelope.RequestId);
        if (!writes.Writer.TryWrite(IpcJson.Serialize(envelope, IpcJsonContext.Default.CancelRequestEnvelope)))
        {
            CompletePending(envelope.RequestId);
            throw new IpcProtocolException(IpcErrorCodes.QueueOverload, "The IPC write queue is saturated.");
        }

        var wire = await AwaitPendingAsync(envelope.RequestId, tcs, cancellationToken).ConfigureAwait(false);
        return IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.CancelResponse);
    }

    public void BeginTrustedEventWindow()
    {
        lock (discontinuityGate)
        {
            hasEventDiscontinuity = false;
        }

        while (events.Reader.TryRead(out _))
        {
        }
    }

    public void FailPendingAsDisconnected()
    {
        TaskCompletionSource<IpcWireEnvelope>[] waiters;
        lock (gate)
        {
            waiters = pending.Values.ToArray();
            pending.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(new IOException("The IPC connection closed before the response arrived."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        writes.Writer.TryComplete();
        events.Writer.TryComplete();
        FailPendingAsDisconnected();
        try
        {
            if (reader is not null)
            {
                await reader.ConfigureAwait(false);
            }

            if (writer is not null)
            {
                await writer.ConfigureAwait(false);
            }

            if (heartbeat is not null)
            {
                await heartbeat.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        lifetime.Dispose();
    }

    private void Start(CancellationToken cancellationToken)
    {
        var token = lifetime.Token;
        writer = WriteLoopAsync(token);
        reader = ReadLoopAsync(token);
        heartbeat = HeartbeatLoopAsync(token);
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in writes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await IpcFrameIO.WriteAsync(stream, frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            events.Writer.TryComplete();
            FailPendingAsDisconnected();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await IpcFrameIO.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                var wire = IpcJson.DeserializeWire(frame);
                if (wire.MessageType == IpcMessageType.Event)
                {
                    DeliverEvent(wire);
                    continue;
                }

                TaskCompletionSource<IpcWireEnvelope>? waiter;
                lock (gate)
                {
                    pending.TryGetValue(wire.RequestId, out waiter);
                    if (waiter is null)
                    {
                        pending.TryGetValue(wire.CorrelationId, out waiter);
                    }
                }

                if (waiter is not null)
                {
                    waiter.TrySetResult(wire);
                    continue;
                }

                if (wire.SemanticType == IpcSemanticTypes.HeartbeatAck)
                {
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
            events.Writer.TryComplete();
            FailPendingAsDisconnected();
        }
        catch (IOException)
        {
            events.Writer.TryComplete();
            FailPendingAsDisconnected();
        }
        catch (IpcFrameException)
        {
            events.Writer.TryComplete();
            FailPendingAsDisconnected();
        }
        catch (JsonException)
        {
            events.Writer.TryComplete();
            FailPendingAsDisconnected();
        }
        catch (DecoderFallbackException)
        {
            events.Writer.TryComplete();
            FailPendingAsDisconnected();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
                var sequence = Interlocked.Increment(ref heartbeatSequence);
                var envelope = IpcEnvelopeFactory.Create(
                    IpcMessageType.Control,
                    IpcSemanticTypes.Heartbeat,
                    workspaceInstanceId,
                    new Heartbeat(sequence));
                if (!writes.Writer.TryWrite(IpcJson.Serialize(envelope, IpcJsonContext.Default.HeartbeatEnvelope)))
                {
                    throw new IpcProtocolException(IpcErrorCodes.QueueOverload, "Heartbeat could not be queued.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IpcProtocolException)
        {
            lifetime.Cancel();
        }
    }

    private void DeliverEvent(IpcWireEnvelope wire)
    {
        var ordinary = wire.SemanticType == IpcSemanticTypes.CoreNotice;
        if (ordinary && HasEventDiscontinuity)
        {
            return;
        }

        if (events.Writer.TryWrite(wire))
        {
            return;
        }

        RecordLocalEventOverflow();
    }

    private void RecordLocalEventOverflow()
    {
        lock (discontinuityGate)
        {
            if (hasEventDiscontinuity)
            {
                return;
            }

            hasEventDiscontinuity = true;
        }

        LocalEventOverflow?.Invoke();
    }

    private TaskCompletionSource<IpcWireEnvelope> RegisterPending(Guid requestId)
    {
        var tcs = new TaskCompletionSource<IpcWireEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (pending.Count >= IpcProtocol.MaximumInFlightRequests)
            {
                throw new IpcProtocolException(IpcErrorCodes.QueueOverload, "Too many in-flight IPC requests.");
            }

            if (!pending.TryAdd(requestId, tcs))
            {
                throw new IpcProtocolException(IpcErrorCodes.DuplicateRequest, "Duplicate IPC request id.");
            }
        }

        return tcs;
    }

    private async Task<IpcWireEnvelope> AwaitPendingAsync(
        Guid requestId,
        TaskCompletionSource<IpcWireEnvelope> waiter,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        using var timeout = new CancellationTokenSource(IpcProtocol.DefaultRequestTimeoutMs);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
        using var registration = combined.Token.Register(() => waiter.TrySetCanceled(combined.Token));
        try
        {
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            CompletePending(requestId);
        }
    }

    private void CompletePending(Guid requestId)
    {
        lock (gate)
        {
            pending.Remove(requestId);
        }
    }
}

public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
