using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using AppCreateRunSessionRequest = LLMW.Writing.Application.Security.CreateRunSessionRequest;

namespace LLMW.Writing.Application.Ipc;

public sealed class IpcServerOptions
{
    public required string WorkspaceInstanceId { get; init; }

    public required IpcClientKind ExpectedClientKind { get; init; }

    public required IpcBootstrapAuthenticator Bootstrap { get; init; }

    public required IpcEventRing EventRing { get; init; }

    public ITrustedIpcBindingRegistry? Bindings { get; init; }

    public string? LaunchBindingId { get; init; }

    public RunSessionService? RunSessions { get; init; }

    public IRunSessionServiceAccessor? RunSessionAccessor { get; init; }

    public RunSessionService? ResolveRunSessions() => RunSessions ?? RunSessionAccessor?.Current;

    public IIpcStateSnapshotProvider Snapshots { get; init; } = TransportOnlySnapshotProvider.Instance;

    public IIpcApplicationCommandHandler Commands { get; init; } = UnavailableIpcCommandHandler.Instance;

    public TrustedNativePrincipalSource? NativeUi { get; init; }

    public ISecurityClock Clock { get; init; } = SystemSecurityClock.Instance;

    public TimeSpan HeartbeatTimeout { get; init; } =
        TimeSpan.FromMilliseconds(IpcProtocol.DefaultHeartbeatIntervalMs * IpcProtocol.MissedHeartbeatsBeforeEvict);

    public TimeSpan WriteTimeout { get; init; } =
        TimeSpan.FromMilliseconds(IpcProtocol.WriteTimeoutMs);

    public TimeSpan DrainTimeout { get; init; } =
        TimeSpan.FromMilliseconds(IpcProtocol.DrainTimeoutMs);

    public Func<string, Guid, CancellationToken, Task>? DelayAsync { get; init; }
}

public static class IpcServerSession
{
    public static async Task ServeAsync(Stream stream, IpcServerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        var connection = new Connection(stream, options);
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            options.Bootstrap.Release();
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly Stream stream;
        private readonly IpcServerOptions options;
        private readonly IpcOutboundScheduler scheduler;
        private readonly IpcInFlightRegistry inFlight = new();
        private readonly object subscriberGate = new();
        private readonly CancellationTokenSource connectionLifetime = new();
        private string? connectionId;
        private AuthenticatedChannelContext? channel;
        private DateTimeOffset lastHeartbeatUtc;
        private bool subscribed;
        private bool needsResync;
        private bool gapSent;
        private long lastDeliveredSeq;
        private GapEvent? outstandingGap;
        private int releasedSessions;

        public Connection(Stream stream, IpcServerOptions options)
        {
            this.stream = stream;
            this.options = options;
            scheduler = new IpcOutboundScheduler(options.WriteTimeout, options.DrainTimeout);
            scheduler.Failed += () =>
            {
                try
                {
                    connectionLifetime.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            };
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionLifetime.Token);
            var token = linked.Token;
            IpcWireEnvelope helloWire;
            try
            {
                var first = await IpcFrameIO.ReadAsync(stream, token).ConfigureAwait(false);
                helloWire = IpcJson.DeserializeWire(first);
            }
            catch (Exception exception) when (exception is IpcFrameException or JsonException or DecoderFallbackException or EndOfStreamException)
            {
                return;
            }

            if (!await TryHandshakeAsync(helloWire, token).ConfigureAwait(false))
            {
                return;
            }

            lastHeartbeatUtc = options.Clock.UtcNow;
            options.EventRing.Published += OnPublished;
            scheduler.Start(stream, TryPullEvent, connectionLifetime.Token);
            _ = WatchHeartbeatAsync(token);
            try
            {
                await ReadLoopAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
            catch (IpcFrameException)
            {
            }
            catch (JsonException)
            {
            }
            catch (DecoderFallbackException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            options.EventRing.Published -= OnPublished;
            connectionLifetime.Cancel();
            RevokeBoundSessions();
            foreach (var item in inFlight.SnapshotAndClear())
            {
                item.Cancellation.Cancel();
                item.Cancellation.Dispose();
            }

            await scheduler.DisposeAsync().ConfigureAwait(false);
            inFlight.Dispose();
            connectionLifetime.Dispose();
        }

        private void OnPublished() => scheduler.PulseEvents();

        private async Task WatchHeartbeatAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(IpcProtocol.DefaultHeartbeatIntervalMs), cancellationToken)
                        .ConfigureAwait(false);
                    if (options.Clock.UtcNow - lastHeartbeatUtc > options.HeartbeatTimeout)
                    {
                        connectionLifetime.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<bool> TryHandshakeAsync(IpcWireEnvelope helloWire, CancellationToken cancellationToken)
        {
            if (helloWire.ProtocolVersion != IpcProtocol.Version1 ||
                helloWire.MessageType != IpcMessageType.Control ||
                helloWire.SemanticType != IpcSemanticTypes.Hello ||
                !StringComparer.Ordinal.Equals(helloWire.WorkspaceInstanceId, options.WorkspaceInstanceId))
            {
                await WriteDirectErrorAsync(helloWire, IpcErrorCodes.UnexpectedMessage, "The first IPC message must be Hello.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            HelloRequest hello;
            try
            {
                hello = IpcJson.DeserializePayload(helloWire.Payload, IpcJsonContext.Default.HelloRequest);
            }
            catch (JsonException)
            {
                await WriteDirectErrorAsync(helloWire, IpcErrorCodes.MalformedFrame, "Hello payload is malformed.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (!IpcProtocol.TryNegotiate(hello.ProtocolMin, hello.ProtocolMax, out var negotiated))
            {
                await WriteDirectErrorAsync(helloWire, IpcErrorCodes.ProtocolNoCommonVersion, "No common IPC protocol version is available.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            var auth = options.Bootstrap.Authenticate(hello.BootstrapToken, hello.ClientKind, options.ExpectedClientKind);
            if (auth.Replay)
            {
                await WriteDirectErrorAsync(helloWire, IpcErrorCodes.AuthBootstrapReplay, "Bootstrap authentication replay was rejected.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (!auth.Accepted)
            {
                await WriteDirectErrorAsync(helloWire, IpcErrorCodes.AuthBootstrapRejected, "Bootstrap authentication was rejected.", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            connectionId = Guid.NewGuid().ToString("D");
            if (options.ExpectedClientKind is IpcClientKind.AgentRuntime or IpcClientKind.Worker)
            {
                var kind = options.ExpectedClientKind == IpcClientKind.Worker
                    ? AuthenticatedClientKind.Worker
                    : AuthenticatedClientKind.AgentRuntime;
                if (!string.IsNullOrWhiteSpace(options.LaunchBindingId))
                {
                    options.Bindings?.TryBind(options.LaunchBindingId, kind, out channel);
                }
                else
                {
                    options.Bindings?.TryBind(kind, out channel);
                }
            }

            var ack = new HelloAck(
                negotiated,
                IpcServerCapabilities.V1,
                options.EventRing.EventStreamId,
                connectionId,
                auth.RotatedToken);
            var envelope = IpcEnvelopeFactory.Create(
                IpcMessageType.Control,
                IpcSemanticTypes.HelloAck,
                options.WorkspaceInstanceId,
                ack,
                correlationId: helloWire.CorrelationId,
                requestId: helloWire.RequestId);
            await IpcFrameIO.WriteAsync(
                    stream,
                    IpcJson.Serialize(envelope, IpcJsonContext.Default.HelloAckEnvelope),
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (options.Clock.UtcNow - lastHeartbeatUtc > options.HeartbeatTimeout)
                {
                    return;
                }

                byte[] frame;
                try
                {
                    frame = await IpcFrameIO.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch (IpcFrameException exception)
                {
                    TryWriteError(Guid.Empty, Guid.Empty, exception.ErrorCode, "The IPC frame is invalid.", IpcSemanticTypes.Heartbeat);
                    return;
                }

                IpcWireEnvelope wire;
                try
                {
                    wire = IpcJson.DeserializeWire(frame);
                }
                catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
                {
                    TryWriteError(Guid.Empty, Guid.Empty, IpcErrorCodes.MalformedFrame, "The IPC JSON is invalid.", IpcSemanticTypes.Heartbeat);
                    return;
                }

                if (wire.ProtocolVersion != IpcProtocol.Version1 ||
                    !StringComparer.Ordinal.Equals(wire.WorkspaceInstanceId, options.WorkspaceInstanceId))
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.ProtocolViolation, "The IPC protocol or workspace is invalid.", wire.SemanticType);
                    return;
                }

                if (!IpcSemanticTypes.IsKnown(wire.SemanticType))
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.UnsupportedSemanticType, "The semantic type is not supported.", wire.SemanticType);
                    return;
                }

                if (!IpcSemanticTypes.IsWellFormed(wire.SemanticType, wire.MessageType))
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.UnexpectedMessage, "The semantic type does not match messageType.", wire.SemanticType);
                    return;
                }

                if (wire.SemanticType is IpcSemanticTypes.Heartbeat or IpcSemanticTypes.Cancel ||
                    wire.MessageType == IpcMessageType.Request)
                {
                    options.Bootstrap.Confirm();
                }

                if (wire.SemanticType == IpcSemanticTypes.Heartbeat)
                {
                    lastHeartbeatUtc = options.Clock.UtcNow;
                    HandleHeartbeat(wire);
                    continue;
                }

                if (wire.SemanticType == IpcSemanticTypes.Cancel)
                {
                    HandleCancel(wire);
                    continue;
                }

                if (wire.MessageType != IpcMessageType.Request)
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.UnexpectedMessage, "Only requests, heartbeat, and cancel are accepted after Hello.", wire.SemanticType);
                    return;
                }

                if (!inFlight.TryRegister(
                        wire.RequestId,
                        wire.CorrelationId,
                        wire.SemanticType,
                        IpcProtocol.MaximumInFlightRequests,
                        out var inflight,
                        out var errorCode))
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, errorCode!, "The request could not be accepted.", wire.SemanticType);
                    if (errorCode == IpcErrorCodes.QueueOverload)
                    {
                        return;
                    }

                    continue;
                }

                _ = Task.Run(() => DispatchAsync(wire, inflight), CancellationToken.None);
            }
        }

        private void HandleHeartbeat(IpcWireEnvelope wire)
        {
            Heartbeat heartbeat;
            try
            {
                heartbeat = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.Heartbeat);
            }
            catch (JsonException)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.MalformedFrame, "Heartbeat payload is malformed.", IpcSemanticTypes.Heartbeat);
                connectionLifetime.Cancel();
                return;
            }

            var ack = IpcEnvelopeFactory.Create(
                IpcMessageType.Control,
                IpcSemanticTypes.HeartbeatAck,
                options.WorkspaceInstanceId,
                new HeartbeatAck(heartbeat.Sequence),
                correlationId: wire.CorrelationId,
                requestId: wire.RequestId);
            if (!scheduler.TryEnqueueCritical(IpcJson.Serialize(ack, IpcJsonContext.Default.HeartbeatAckEnvelope), IpcSemanticTypes.HeartbeatAck))
            {
                FailClosedOverload();
            }
        }

        private void HandleCancel(IpcWireEnvelope wire)
        {
            CancelRequest cancel;
            try
            {
                cancel = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.CancelRequest);
            }
            catch (JsonException)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.MalformedFrame, "Cancel payload is malformed.", IpcSemanticTypes.Cancel);
                return;
            }

            string state;
            var accepted = true;
            if (!inFlight.TryGetByCorrelation(cancel.CorrelationId, out var inflight))
            {
                if (inFlight.WasCompleted(cancel.CorrelationId))
                {
                    state = CancelResponse.StateAlreadyCompleted;
                }
                else
                {
                    accepted = false;
                    state = CancelResponse.StateUnknown;
                }
            }
            else if (inflight.IsCompleted)
            {
                state = CancelResponse.StateAlreadyCompleted;
            }
            else
            {
                inflight.Cancellation.Cancel();
                state = CancelResponse.StateCancelling;
            }

            var response = IpcEnvelopeFactory.Create(
                IpcMessageType.Control,
                IpcSemanticTypes.Cancel,
                options.WorkspaceInstanceId,
                new CancelResponse(cancel.CorrelationId, accepted, state),
                correlationId: wire.CorrelationId,
                requestId: wire.RequestId);
            if (!scheduler.TryEnqueueCritical(IpcJson.Serialize(response, IpcJsonContext.Default.CancelResponseEnvelope), IpcSemanticTypes.Cancel))
            {
                FailClosedOverload();
            }
        }

        private async Task DispatchAsync(IpcWireEnvelope wire, IpcInFlightRequest inflight)
        {
            try
            {
                if (options.DelayAsync is not null)
                {
                    await options.DelayAsync(wire.SemanticType, wire.RequestId, inflight.Cancellation.Token).ConfigureAwait(false);
                }

                inflight.Cancellation.Token.ThrowIfCancellationRequested();
                switch (wire.SemanticType)
                {
                    case IpcSemanticTypes.GetStateSnapshot:
                        HandleGetStateSnapshot(wire);
                        break;
                    case IpcSemanticTypes.SubscribeEvents:
                        HandleSubscribe(wire);
                        break;
                    case IpcSemanticTypes.CreateRunSession:
                        HandleCreateRunSession(wire);
                        break;
                    case IpcSemanticTypes.RevokeRunSession:
                        HandleRevokeRunSession(wire);
                        break;
                    default:
                        await HandleApplicationCommandAsync(wire, inflight.Cancellation.Token).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.Cancelled, "The request was cancelled.", wire.SemanticType);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.MalformedFrame, "The request payload is malformed.", wire.SemanticType);
            }
            finally
            {
                if (inFlight.TryComplete(wire.RequestId, out var completed))
                {
                    completed.Cancellation.Dispose();
                }
            }
        }

        private void HandleGetStateSnapshot(IpcWireEnvelope wire)
        {
            var request = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.GetStateSnapshotRequest);
            var resyncRequired = !string.IsNullOrWhiteSpace(request.LastEventStreamId) &&
                                 !StringComparer.Ordinal.Equals(request.LastEventStreamId, options.EventRing.EventStreamId);
            var response = new GetStateSnapshotResponse(
                options.EventRing.EventStreamId,
                options.EventRing.HeadSeq,
                resyncRequired,
                options.Snapshots.Create());
            WriteSnapshot(wire, IpcJson.Serialize(
                IpcEnvelopeFactory.Create(
                    IpcMessageType.Response,
                    IpcSemanticTypes.GetStateSnapshot,
                    options.WorkspaceInstanceId,
                    response,
                    projectId: wire.ProjectId,
                    runId: wire.RunId,
                    correlationId: wire.CorrelationId,
                    requestId: wire.RequestId),
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope));
        }

        private void HandleSubscribe(IpcWireEnvelope wire)
        {
            var request = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.SubscribeEventsRequest);
            if (!StringComparer.Ordinal.Equals(request.EventStreamId, options.EventRing.EventStreamId))
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.ResyncRequired, "The event-stream epoch does not match.", IpcSemanticTypes.SubscribeEvents);
                return;
            }

            lock (subscriberGate)
            {
                subscribed = true;
                needsResync = false;
                gapSent = false;
                outstandingGap = null;
                lastDeliveredSeq = request.AfterSeq;
            }

            var response = new SubscribeEventsResponse(options.EventRing.EventStreamId, request.AfterSeq, options.EventRing.HeadSeq);
            WriteCritical(wire, IpcJson.Serialize(
                IpcEnvelopeFactory.Create(
                    IpcMessageType.Response,
                    IpcSemanticTypes.SubscribeEvents,
                    options.WorkspaceInstanceId,
                    response,
                    correlationId: wire.CorrelationId,
                    requestId: wire.RequestId),
                IpcJsonContext.Default.SubscribeEventsResponseEnvelope),
                IpcSemanticTypes.SubscribeEvents);
            scheduler.PulseEvents();
        }

        private void HandleCreateRunSession(IpcWireEnvelope wire)
        {
            var request = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.CreateRunSessionRequest);
            TryBindTrustedChannel();
            var sessions = options.ResolveRunSessions();
            if (channel is null || sessions is null)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.TrustedBindingUnavailable, "Trusted channel binding is unavailable.", IpcSemanticTypes.CreateRunSession);
                return;
            }

            if (!TryCrossCheckEnvelope(wire, request.RunId, out var envelopeError))
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, envelopeError!, "Envelope identity does not match the trusted binding.", IpcSemanticTypes.CreateRunSession);
                return;
            }

            DateTimeOffset? requested = request.ExpiresAtMs is long ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            var created = sessions.Create(new AppCreateRunSessionRequest(request.RunId, channel, requested));
            if (!created.Succeeded || created.Value is null)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, MapSessionError(created.Failure?.Code), "RunSession issuance failed.", IpcSemanticTypes.CreateRunSession);
                return;
            }

            var token = created.Value.Token.ExportOnceForAuthenticatedTransport();
            var response = new CreateRunSessionResponse(
                created.Value.HandleId,
                created.Value.RunId,
                token,
                created.Value.ExpiresAt.ToUnixTimeMilliseconds());
            WriteCritical(wire, IpcJson.Serialize(
                IpcEnvelopeFactory.Create(
                    IpcMessageType.Response,
                    IpcSemanticTypes.CreateRunSession,
                    options.WorkspaceInstanceId,
                    response,
                    runId: wire.RunId,
                    projectId: wire.ProjectId,
                    correlationId: wire.CorrelationId,
                    requestId: wire.RequestId),
                IpcJsonContext.Default.CreateRunSessionResponseEnvelope),
                IpcSemanticTypes.CreateRunSession);
        }

        private void HandleRevokeRunSession(IpcWireEnvelope wire)
        {
            var request = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.RevokeRunSessionRequest);
            TryBindTrustedChannel();
            var sessions = options.ResolveRunSessions();
            if (sessions is null)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.TrustedBindingUnavailable, "RunSession store is unavailable.", IpcSemanticTypes.RevokeRunSession);
                return;
            }

            if (options.ExpectedClientKind is IpcClientKind.AgentRuntime or IpcClientKind.Worker)
            {
                if (request.Session is null || channel is null)
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.InvalidSession, "RunSession proof is required.", IpcSemanticTypes.RevokeRunSession);
                    return;
                }

                var resolved = sessions.Resolve(new ResolveRunSessionRequest(request.Session.RunId, request.Session.OpaqueToken, channel));
                if (!resolved.Succeeded || resolved.Value is null ||
                    !StringComparer.Ordinal.Equals(resolved.Value.SessionHandleId, request.HandleId))
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, MapSessionError(resolved.Failure?.Code), "RunSession revoke was rejected.", IpcSemanticTypes.RevokeRunSession);
                    return;
                }
            }
            else if (options.NativeUi is null)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.InvalidSession, "UI principal is unavailable.", IpcSemanticTypes.RevokeRunSession);
                return;
            }

            var revoked = sessions.Revoke(request.HandleId) > 0;
            WriteCritical(wire, IpcJson.Serialize(
                IpcEnvelopeFactory.Create(
                    IpcMessageType.Response,
                    IpcSemanticTypes.RevokeRunSession,
                    options.WorkspaceInstanceId,
                    new RevokeRunSessionResponse(revoked),
                    correlationId: wire.CorrelationId,
                    requestId: wire.RequestId),
                IpcJsonContext.Default.RevokeRunSessionResponseEnvelope),
                IpcSemanticTypes.RevokeRunSession);
        }

        private async Task HandleApplicationCommandAsync(IpcWireEnvelope wire, CancellationToken cancellationToken)
        {
            CallerPrincipal? principal = null;
            if (RuntimeManagementCatalog.IsChannelScoped(wire.SemanticType))
            {
                if (options.ExpectedClientKind != IpcClientKind.AgentRuntime)
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.RuntimeManagementDenied, "Runtime management commands require an authenticated Agent Runtime channel.", wire.SemanticType);
                    return;
                }

                TryBindTrustedChannel();
                if (channel is null)
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.TrustedBindingUnavailable, "Trusted Runtime launch binding is unavailable.", wire.SemanticType);
                    return;
                }
            }
            else if (options.ExpectedClientKind == IpcClientKind.Ui)
            {
                principal = options.NativeUi?.ResolveUserInteractive();
                if (principal is null)
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.InvalidSession, "UI principal is unavailable.", wire.SemanticType);
                    return;
                }
            }
            else
            {
                TryBindTrustedChannel();
                var sessions = options.ResolveRunSessions();
                RunSessionProof? proof = TryReadProof(wire);
                if (proof is null || channel is null || sessions is null)
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.InvalidSession, "Agent commands require a Core-issued RunSession.", wire.SemanticType);
                    return;
                }

                var resolved = sessions.Resolve(new ResolveRunSessionRequest(proof.RunId, proof.OpaqueToken, channel));
                if (!resolved.Succeeded || resolved.Value is null)
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, MapSessionError(resolved.Failure?.Code), "RunSession resolution failed.", wire.SemanticType);
                    return;
                }

                if (!TryCrossCheckEnvelope(wire, proof.RunId, out var envelopeError))
                {
                    TryWriteError(wire.RequestId, wire.CorrelationId, envelopeError!, "Envelope identity does not match the resolved session.", wire.SemanticType);
                    return;
                }

                principal = resolved.Value;
            }

            var result = await options.Commands.HandleAsync(
                    new IpcApplicationCommandContext(
                        options.ExpectedClientKind,
                        connectionId ?? string.Empty,
                        channel,
                        principal,
                        wire.RequestId,
                        wire.CorrelationId,
                        wire.ProjectId,
                        wire.RunId,
                        wire.SemanticType,
                        wire.Payload,
                        cancellationToken))
                .ConfigureAwait(false);
            if (result is null)
            {
                TryWriteError(wire.RequestId, wire.CorrelationId, IpcErrorCodes.CommandUnavailable, "The command is not available on this Core.", wire.SemanticType);
                return;
            }

            WriteCritical(wire, result.ResponseUtf8, wire.SemanticType);
        }

        private byte[]? TryPullEvent()
        {
            lock (subscriberGate)
            {
                if (!subscribed)
                {
                    return null;
                }

                if (needsResync)
                {
                    if (!gapSent && outstandingGap is not null)
                    {
                        gapSent = true;
                        return SerializeGap(outstandingGap);
                    }

                    return null;
                }

                if (options.EventRing.TryDescribeGap(lastDeliveredSeq, out var fromSeq, out var toSeq))
                {
                    needsResync = true;
                    outstandingGap = new GapEvent(options.EventRing.EventStreamId, fromSeq, toSeq);
                    gapSent = true;
                    return SerializeGap(outstandingGap);
                }

                var next = lastDeliveredSeq + 1;
                if (!options.EventRing.TryGet(next, out var retained))
                {
                    return null;
                }

                lastDeliveredSeq = next;
                var envelope = IpcEnvelopeFactory.Create(
                    IpcMessageType.Event,
                    IpcSemanticTypes.CoreNotice,
                    options.WorkspaceInstanceId,
                    retained.Notice);
                return IpcJson.Serialize(envelope, IpcJsonContext.Default.CoreNoticeEventEnvelope);
            }
        }

        private byte[] SerializeGap(GapEvent gap)
        {
            var envelope = IpcEnvelopeFactory.Create(
                IpcMessageType.Event,
                IpcSemanticTypes.Gap,
                options.WorkspaceInstanceId,
                gap);
            return IpcJson.Serialize(envelope, IpcJsonContext.Default.GapEventEnvelope);
        }

        private void WriteCritical(IpcWireEnvelope wire, byte[] utf8, string semanticType)
        {
            if (!scheduler.TryEnqueueCritical(utf8, semanticType))
            {
                FailClosedOverload();
            }
        }

        private void WriteSnapshot(IpcWireEnvelope wire, byte[] utf8)
        {
            if (!scheduler.TryEnqueueSnapshot(utf8, IpcSemanticTypes.GetStateSnapshot) &&
                !scheduler.TryEnqueueCritical(utf8, IpcSemanticTypes.GetStateSnapshot))
            {
                FailClosedOverload();
            }
        }

        private void TryWriteError(Guid requestId, Guid correlationId, string code, string message, string semanticType)
        {
            var envelope = IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                semanticType,
                options.WorkspaceInstanceId,
                new IpcError(code, message, null, code is IpcErrorCodes.Cancelled or IpcErrorCodes.QueueOverload),
                correlationId: correlationId == Guid.Empty ? null : correlationId,
                requestId: requestId == Guid.Empty ? null : requestId);
            if (!scheduler.TryEnqueueCritical(IpcJson.Serialize(envelope, IpcJsonContext.Default.ErrorEnvelope), semanticType))
            {
                connectionLifetime.Cancel();
            }
        }

        private async Task WriteDirectErrorAsync(IpcWireEnvelope wire, string code, string message, CancellationToken cancellationToken)
        {
            var envelope = IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                wire.SemanticType,
                options.WorkspaceInstanceId,
                new IpcError(code, message, null, false),
                correlationId: wire.CorrelationId,
                requestId: wire.RequestId);
            try
            {
                await IpcFrameIO.WriteAsync(stream, IpcJson.Serialize(envelope, IpcJsonContext.Default.ErrorEnvelope), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        private void FailClosedOverload()
        {
            TryWriteError(Guid.Empty, Guid.Empty, IpcErrorCodes.QueueOverload, "The IPC critical queue is saturated.", IpcSemanticTypes.Heartbeat);
            connectionLifetime.Cancel();
        }

        private void TryBindTrustedChannel()
        {
            if (channel is not null)
            {
                return;
            }

            if (options.ExpectedClientKind == IpcClientKind.AgentRuntime)
            {
                options.Bindings?.TryBind(AuthenticatedClientKind.AgentRuntime, out channel);
                return;
            }

            if (options.ExpectedClientKind == IpcClientKind.Worker &&
                !string.IsNullOrWhiteSpace(options.LaunchBindingId))
            {
                options.Bindings?.TryBind(options.LaunchBindingId, AuthenticatedClientKind.Worker, out channel);
            }
        }

        private void RevokeBoundSessions()
        {
            TryBindTrustedChannel();
            var sessions = options.ResolveRunSessions();
            if (Interlocked.Exchange(ref releasedSessions, 1) != 0 || channel is null || sessions is null)
            {
                return;
            }

            if (options.ExpectedClientKind is IpcClientKind.AgentRuntime or IpcClientKind.Worker)
            {
                sessions.RevokeByChannelWorker(channel);
            }

            if (options.ExpectedClientKind == IpcClientKind.Worker &&
                !string.IsNullOrWhiteSpace(options.LaunchBindingId))
            {
                options.Bindings?.Unregister(options.LaunchBindingId);
            }
        }

        private bool TryCrossCheckEnvelope(IpcWireEnvelope wire, string runId, out string? errorCode)
        {
            errorCode = null;
            if (channel is null)
            {
                errorCode = IpcErrorCodes.TrustedBindingUnavailable;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(channel.BoundRunId) &&
                !StringComparer.Ordinal.Equals(channel.BoundRunId, runId))
            {
                errorCode = IpcErrorCodes.BindingMismatch;
                return false;
            }

            if (wire.ProjectId is Guid projectId && projectId != channel.ProjectScope.ProjectId)
            {
                errorCode = IpcErrorCodes.BindingMismatch;
                return false;
            }

            if (wire.RunId is Guid envelopeRunId)
            {
                if (!Guid.TryParse(runId, out var bound) || bound != envelopeRunId)
                {
                    errorCode = IpcErrorCodes.BindingMismatch;
                    return false;
                }
            }

            return true;
        }

        private static RunSessionProof? TryReadProof(IpcWireEnvelope wire)
        {
            if (!wire.Payload.TryGetProperty("session", out var session) || session.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            try
            {
                return IpcJson.DeserializePayload(session, IpcJsonContext.Default.RunSessionProof);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string MapSessionError(RunSessionError? error) => error switch
        {
            RunSessionError.SessionExpired => IpcErrorCodes.SessionExpired,
            RunSessionError.SessionRevoked => IpcErrorCodes.SessionRevoked,
            RunSessionError.SessionBindingMismatch => IpcErrorCodes.BindingMismatch,
            RunSessionError.InvalidTtlPolicy => IpcErrorCodes.TrustedBindingUnavailable,
            RunSessionError.RunNotFound or RunSessionError.UnknownAgentRole or RunSessionError.InvalidRunSession => IpcErrorCodes.InvalidSession,
            _ => IpcErrorCodes.InvalidSession
        };
    }
}
