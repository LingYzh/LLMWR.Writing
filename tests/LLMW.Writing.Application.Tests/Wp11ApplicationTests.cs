using System.Globalization;
using System.Threading.Channels;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Security;
using AppCreateRunSessionRequest = LLMW.Writing.Application.Security.CreateRunSessionRequest;

namespace LLMW.Writing.Application.Tests;

internal static class Wp11ApplicationTests
{
    private static readonly Guid ProjectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab");
    private static readonly ProjectScope Scope = new(ProjectId, "workspace-01");
    private const string Workspace = "workspace-01";
    private const string RunId = "run-wp11";

    public static int Run()
    {
        TtlPolicyClampsHugeExpiryAndHonorsShorterRequest();
        TtlPolicyRejectsPastAndInvalidPolicy();
        EventRingRetains256AndReportsExactGap();
        MultiplexOutOfOrderSnapshotAndUnknownSemanticType();
        CreateRunSessionRequiresTrustedBindingAndClampsExpiry();
        ForgedEnvelopeIdentityCannotEscapeBinding();
        CancellationIsBestEffortAndUnknownCorrelationIsSafe();
        SlowSubscriberDoesNotBlockPublishAndSnapshotStillProgresses();
        IpcSurfaceCannotReachSandboxHost();
        BootstrapRejectsWrongKindWithoutRotatingSecret();
        DuplicateInFlightRequestIsRejected();
        CriticalOutboundQueueFailsClosedWithoutSilentDrop();
        GapEventIsExactAndCoalescedForLiveSubscriber();
        AgentCommandsResolveSessionAndRejectStolenRevokedExpiredProofs();
        StaleSessionAfterDisconnectIsRevoked();
        RuntimeCannotBecomeUserInteractive();
        BootstrapBadTokenDoesNotRotate();
        BootstrapAckLossRecoversWithoutCoreRestart();
        BootstrapRejectsConcurrentAuthenticatedConnection();
        ClientEventBufferOverflowIsExplicitAndRecoverable();
        StalledNonReadingPeerUnblocksEndpoint();
        ProductionReconnectSnapshotSubscribeGapAndEpoch();
        WriterWakeDeliversEventAfterRepeatedCriticalIdle();
        CancelAsyncHonorsInFlightBoundAndDoesNotLeak();
        Console.WriteLine("Application WP11 IPC tests passed (24).");
        return 24;
    }

    private static void TtlPolicyClampsHugeExpiryAndHonorsShorterRequest()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var store = new MemoryRunStore();
        store.Seed(RunId, "writer");
        var service = new RunSessionService(store, clock);
        var channel = Channel();
        var huge = Success(service.Create(new AppCreateRunSessionRequest(RunId, channel, clock.UtcNow.AddYears(50))));
        AssertEqual(clock.UtcNow.AddHours(8), huge.ExpiresAt, "Huge caller expiry was not clamped to MaximumTtl.");
        AssertEqual(huge.ExpiresAt.ToUnixTimeMilliseconds(), store.LastPersistedExpiryMs, "Persisted expiry is not the clamped Core expiry.");

        var inside = Success(service.Create(new AppCreateRunSessionRequest(RunId, channel, clock.UtcNow.AddMinutes(5))));
        AssertEqual(clock.UtcNow.AddMinutes(5), inside.ExpiresAt, "A shorter legitimate expiry must be honored.");

        var boundary = Success(service.Create(new AppCreateRunSessionRequest(RunId, channel, clock.UtcNow.AddHours(8))));
        AssertEqual(clock.UtcNow.AddHours(8), boundary.ExpiresAt, "Exact MaximumTtl boundary must be accepted.");

        var unspecified = Success(service.Create(new AppCreateRunSessionRequest(RunId, channel, null)));
        AssertEqual(clock.UtcNow.AddHours(1), unspecified.ExpiresAt, "Omitted expiry must use DefaultTtl.");
    }

    private static void TtlPolicyRejectsPastAndInvalidPolicy()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeMilliseconds(2_000_000));
        var store = new MemoryRunStore();
        store.Seed(RunId, "writer");
        var past = new RunSessionService(store, clock).Create(new AppCreateRunSessionRequest(RunId, Channel(), clock.UtcNow));
        AssertEqual(RunSessionError.SessionExpired, past.Failure?.Code, "Expiry equal to now must fail.");

        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(2_000_000);
        var expiring = Success(new RunSessionService(store, new MutableClock(issuedAt)).Create(
            new AppCreateRunSessionRequest(RunId, Channel(), issuedAt.AddMinutes(1))));
        var later = new RunSessionService(store, new MutableClock(issuedAt.AddMinutes(10)));
        AssertEqual(RunSessionError.SessionExpired, later.Resolve(new ResolveRunSessionRequest(
            RunId, expiring.Token.ExportOnceForAuthenticatedTransport(), Channel())).Failure?.Code, "Clock advancement must expire the session.");

        var invalid = new RunSessionService(store, clock, ttlPolicy: new RunSessionTtlPolicy(TimeSpan.FromHours(2), TimeSpan.FromHours(1)));
        AssertEqual(RunSessionError.InvalidTtlPolicy, invalid.Create(new AppCreateRunSessionRequest(RunId, Channel(), clock.UtcNow.AddMinutes(1))).Failure?.Code,
            "Invalid TTL policy must fail closed.");
    }

    private static void EventRingRetains256AndReportsExactGap()
    {
        var ring = new IpcEventRing("stream-a");
        for (var i = 0; i < 256; i++)
        {
            ring.PublishNotice("n", i.ToString(CultureInfo.InvariantCulture));
        }

        AssertEqual(256L, ring.HeadSeq, "Ring head after 256 events.");
        AssertTrue(ring.TryGet(1, out _), "Seq 1 must still be retained.");
        AssertTrue(ring.TryGet(256, out _), "Seq 256 must be retained.");

        ring.PublishNotice("overflow", "257");
        AssertTrue(!ring.TryGet(1, out _), "The 257th event must drop seq 1.");
        AssertTrue(ring.TryGet(2, out _), "Seq 2 must remain after the first overflow.");
        AssertTrue(ring.TryDescribeGap(0, out var from, out var to), "Overflow must produce a gap.");
        AssertEqual(1L, from, "Gap fromSeq must be inclusive of the first missing seq.");
        AssertEqual(1L, to, "Gap toSeq must be inclusive of the last missing seq.");

        ring.PublishNotice("overflow", "258");
        AssertTrue(ring.TryDescribeGap(0, out from, out to), "Repeated overflow must remain describable.");
        AssertEqual(1L, from, "Coalesced gap fromSeq.");
        AssertEqual(2L, to, "Coalesced gap toSeq must widen exactly.");
    }

    private static void MultiplexOutOfOrderSnapshotAndUnknownSemanticType()
    {
        var token = IpcBootstrapToken.Create();
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D"))
        };

        RunHosted(token, options, async client =>
        {
            var firstTask = client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, null),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            var secondTask = client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, "other-epoch"),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            var completed = await Task.WhenAll(firstTask, secondTask);
            AssertEqual(options.EventRing.EventStreamId, completed[0].Payload.EventStreamId, "Snapshot epoch must be Core-owned.");
            AssertEqual(true, completed[1].Payload.ResyncRequired, "Epoch mismatch must force resync.");
            AssertEqual(0L, completed[0].Payload.SnapshotSeq, "Empty snapshot watermark must be 0.");
            AssertTrue(completed[0].RequestId != completed[1].RequestId, "Concurrent requests must keep distinct request ids.");

            try
            {
                await client.RequestAsync(
                    "notARealOperation",
                    new Heartbeat(1),
                    IpcJsonContext.Default.HeartbeatEnvelope,
                    IpcJsonContext.Default.HeartbeatAckEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Unknown semantic type must fail closed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.UnsupportedSemanticType, exception.ErrorCode, "Unknown semantic type must return IPC_UNSUPPORTED_SEMANTIC_TYPE.");
            }
            catch (IOException)
            {
            }
        });
    }

    private static void CreateRunSessionRequiresTrustedBindingAndClampsExpiry()
    {
        var token = IpcBootstrapToken.Create();
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D"))
        }, async client =>
        {
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.CreateRunSession,
                    new LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest(RunId, DateTimeOffset.UtcNow.AddYears(20).ToUnixTimeMilliseconds()),
                    IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                    IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("CreateRunSession without a store/binding must fail.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.TrustedBindingUnavailable, exception.ErrorCode, "Missing trusted binding must fail closed.");
            }
        });

        var clock = new MutableClock(DateTimeOffset.FromUnixTimeMilliseconds(3_000_000));
        var store = new MemoryRunStore();
        store.Seed(RunId, "writer");
        var bindings = new TrustedIpcBindingRegistry();
        bindings.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.AgentRuntime, "worker-1", "channel-1", Scope));
        var boundToken = IpcBootstrapToken.Create();
        RunHosted(boundToken, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(boundToken),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = bindings,
            RunSessions = new RunSessionService(store, clock),
            Clock = clock
        }, async client =>
        {
            var created = await client.RequestAsync(
                IpcSemanticTypes.CreateRunSession,
                new LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest(RunId, clock.UtcNow.AddYears(10).ToUnixTimeMilliseconds()),
                IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                CancellationToken.None);
            AssertEqual(clock.UtcNow.AddHours(8).ToUnixTimeMilliseconds(), created.Payload.ExpiresAtMs, "IPC CreateRunSession must return the clamped Core expiry.");
            AssertTrue(!string.IsNullOrWhiteSpace(created.Payload.OpaqueToken), "Opaque token must be issued.");
        });
    }

    private static void ForgedEnvelopeIdentityCannotEscapeBinding()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var store = new MemoryRunStore();
        store.Seed(RunId, "writer");
        var bindings = new TrustedIpcBindingRegistry();
        bindings.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.AgentRuntime, "worker-1", "channel-1", Scope));
        var token = IpcBootstrapToken.Create();
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = bindings,
            RunSessions = new RunSessionService(store, clock),
            Clock = clock
        }, async client =>
        {
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.CreateRunSession,
                    new LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest(RunId, null),
                    IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                    IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                    CancellationToken.None,
                    projectId: Guid.Parse("018f3e78-0000-7000-8000-000000000099"));
                throw new InvalidOperationException("Forged projectId must not issue a session.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.BindingMismatch, exception.ErrorCode, "Forged projectId must fail closed.");
            }
        });
    }

    private static void CancellationIsBestEffortAndUnknownCorrelationIsSafe()
    {
        var holdSnapshots = false;
        var token = IpcBootstrapToken.Create();
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            DelayAsync = async (semantic, _, cancellationToken) =>
            {
                if (!holdSnapshots || semantic != IpcSemanticTypes.GetStateSnapshot)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        };

        RunHosted(token, options, async client =>
        {
            var unknown = await client.CancelAsync(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), CancellationToken.None);
            AssertEqual(CancelResponse.StateUnknown, unknown.State, "Unknown correlation must return unknown.");
            AssertEqual(false, unknown.Accepted, "Unknown cancel must not claim success.");

            var completedSnapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, null),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            var afterComplete = await client.CancelAsync(completedSnapshot.CorrelationId, CancellationToken.None);
            AssertEqual(CancelResponse.StateAlreadyCompleted, afterComplete.State, "Cancel after completion must not invent a rollback.");
            AssertEqual(true, afterComplete.Accepted, "Cancel after completion is accepted as alreadyCompleted.");

            holdSnapshots = true;
            var envelope = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.GetStateSnapshot,
                Workspace,
                new GetStateSnapshotRequest(0, null));
            var pending = client.RequestWireAsync(
                IpcMessageType.Request,
                IpcSemanticTypes.GetStateSnapshot,
                IpcJson.Serialize(envelope, IpcJsonContext.Default.GetStateSnapshotRequestEnvelope),
                CancellationToken.None);
            await Task.Delay(80);
            var cancel = await client.CancelAsync(envelope.CorrelationId, CancellationToken.None);
            AssertTrue(
                cancel.State is CancelResponse.StateCancelling or CancelResponse.StateCancelled or CancelResponse.StateAlreadyCompleted,
                "In-flight cancel must be best-effort.");
            var completed = await pending;
            AssertEqual(IpcErrorCodes.Cancelled, completed.Payload.GetProperty("code").GetString()!, "Cancelled in-flight work must not claim Authority rollback.");
        });
    }

    private static void CancelAsyncHonorsInFlightBoundAndDoesNotLeak()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSnapshots = false;
        var entered = 0;
        var token = IpcBootstrapToken.Create();
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            DelayAsync = async (semantic, _, cancellationToken) =>
            {
                if (!holdSnapshots || semantic != IpcSemanticTypes.GetStateSnapshot)
                {
                    return;
                }

                Interlocked.Increment(ref entered);
                await hold.Task.WaitAsync(cancellationToken);
            }
        };

        RunHosted(token, options, async client =>
        {
            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            try
            {
                await client.CancelAsync(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), alreadyCancelled.Token);
                throw new InvalidOperationException("Caller cancellation of CancelAsync must be observed.");
            }
            catch (OperationCanceledException)
            {
            }

            for (var i = 0; i < 40; i++)
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await client.CancelAsync(Guid.NewGuid(), cancelled.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, null),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(false, snapshot.Payload.ResyncRequired, "Repeated cancelled CancelAsync calls must not poison later requests.");

            holdSnapshots = true;
            var inflight = new List<Task<IpcWireEnvelope>>();
            for (var i = 0; i < IpcProtocol.MaximumInFlightRequests; i++)
            {
                var envelope = IpcEnvelopeFactory.Create(
                    IpcMessageType.Request,
                    IpcSemanticTypes.GetStateSnapshot,
                    Workspace,
                    new GetStateSnapshotRequest(0, null));
                inflight.Add(client.RequestWireAsync(
                    IpcMessageType.Request,
                    IpcSemanticTypes.GetStateSnapshot,
                    IpcJson.Serialize(envelope, IpcJsonContext.Default.GetStateSnapshotRequestEnvelope),
                    CancellationToken.None));
            }

            WaitUntil(
                () => Volatile.Read(ref entered) >= IpcProtocol.MaximumInFlightRequests,
                "Client pending slots must fill to MaximumInFlightRequests.");

            try
            {
                await client.CancelAsync(Guid.NewGuid(), CancellationToken.None);
                throw new InvalidOperationException("CancelAsync must honor MaximumInFlightRequests.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.QueueOverload, exception.ErrorCode, "CancelAsync must not bypass the in-flight bound.");
            }

            hold.TrySetResult();
            await Task.WhenAll(inflight);
        });
    }

    private static void SlowSubscriberDoesNotBlockPublishAndSnapshotStillProgresses()
    {
        var ring = new IpcEventRing("stream-slow");
        var started = DateTime.UtcNow;
        for (var i = 0; i < 300; i++)
        {
            ring.PublishNotice("flood", i.ToString(CultureInfo.InvariantCulture));
        }

        AssertTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "Event publish must not block on a subscriber.");
        AssertEqual(300L, ring.HeadSeq, "Authority-side publish count.");
        AssertTrue(ring.TryDescribeGap(0, out var from, out var to), "Slow subscriber must see an exact gap.");
        AssertEqual(1L, from, "Gap start.");
        AssertEqual(44L, to, "300-256=44 dropped events, inclusive 1-44.");

        var token = IpcBootstrapToken.Create();
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = ring
        }, async client =>
        {
            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, ring.EventStreamId),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(300L, snapshot.Payload.SnapshotSeq, "Snapshot must progress while the event path is overflowing.");
            await client.RequestAsync(
                IpcSemanticTypes.SubscribeEvents,
                new SubscribeEventsRequest(ring.EventStreamId, snapshot.Payload.SnapshotSeq),
                IpcJsonContext.Default.SubscribeEventsRequestEnvelope,
                IpcJsonContext.Default.SubscribeEventsResponseEnvelope,
                CancellationToken.None);
        });
    }

    private static void IpcSurfaceCannotReachSandboxHost()
    {
        AssertEqual(false, typeof(IpcServerOptions).GetProperties().Any(property =>
                typeof(ISandboxHost).IsAssignableFrom(property.PropertyType) ||
                property.PropertyType.Name.Contains("SandboxHost", StringComparison.Ordinal)),
            "IpcServerOptions must not expose ISandboxHost.");
        AssertEqual(false, IpcSemanticTypes.All.Any(type =>
                type.Contains("sandbox", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("execute", StringComparison.OrdinalIgnoreCase)),
            "IPC semantic catalog must not expose sandbox execution.");
        AssertEqual(false, typeof(IpcClientSession).GetMethods().Any(method =>
                method.ReturnType == typeof(ISandboxHost) ||
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(ISandboxHost))),
            "Runtime IPC client must not reach ISandboxHost.");
    }

    private static void BootstrapRejectsWrongKindWithoutRotatingSecret()
    {
        var token = IpcBootstrapToken.Create();
        var authenticator = new IpcBootstrapAuthenticator(token);
        var rejected = authenticator.Authenticate(token, IpcClientKind.Ui, IpcClientKind.AgentRuntime);
        AssertEqual(false, rejected.Accepted, "Wrong clientKind must be rejected.");
        AssertEqual(false, rejected.Replay, "Wrong clientKind is not a replay.");
        var accepted = authenticator.Authenticate(token, IpcClientKind.AgentRuntime, IpcClientKind.AgentRuntime);
        AssertEqual(true, accepted.Accepted, "A failed Hello must not consume the bootstrap secret.");
        authenticator.Release();

        var protocolToken = IpcBootstrapToken.Create();
        var protocolAuth = new IpcBootstrapAuthenticator(protocolToken);
        var protocolOptions = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = protocolAuth,
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D"))
        };
        RunRaw(protocolOptions, stream =>
        {
            WriteHello(stream, protocolToken, 2, 2, IpcClientKind.AgentRuntime, CancellationToken.None);
            var error = ReadError(stream, CancellationToken.None);
            AssertEqual(IpcErrorCodes.ProtocolNoCommonVersion, error.Code, "Protocol mismatch must return IPC_PROTOCOL_NO_COMMON_VERSION.");
        });
        RunHosted(protocolToken, protocolOptions, async client =>
        {
            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, null),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(false, snapshot.Payload.ResyncRequired, "Original bootstrap must still work after a failed protocol Hello.");
        });
    }

    private static void DuplicateInFlightRequestIsRejected()
    {
        var registry = new IpcInFlightRegistry();
        AssertEqual(true, registry.TryRegister(Guid.NewGuid(), Guid.NewGuid(), IpcSemanticTypes.GetStateSnapshot, 32, out _, out _), "First request must register.");
        var requestId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        AssertEqual(true, registry.TryRegister(requestId, correlationId, IpcSemanticTypes.GetStateSnapshot, 32, out _, out _), "Distinct ids must register.");
        AssertEqual(false, registry.TryRegister(requestId, Guid.NewGuid(), IpcSemanticTypes.GetStateSnapshot, 32, out _, out var duplicate), "Duplicate requestId must be rejected.");
        AssertEqual(IpcErrorCodes.DuplicateRequest, duplicate!, "Duplicate requestId must use IPC_DUPLICATE_REQUEST.");
        AssertEqual(false, registry.TryRegister(Guid.NewGuid(), correlationId, IpcSemanticTypes.GetStateSnapshot, 32, out _, out var duplicateCorrelation), "Duplicate correlationId must be rejected.");
        AssertEqual(IpcErrorCodes.DuplicateRequest, duplicateCorrelation!, "Duplicate correlationId must use IPC_DUPLICATE_REQUEST.");
        registry.Dispose();

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = IpcBootstrapToken.Create();
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            DelayAsync = async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken);
            }
        };

        RunRaw(options, stream =>
        {
            WriteHello(stream, token, 1, 1, IpcClientKind.AgentRuntime, CancellationToken.None);
            _ = IpcJson.Deserialize(
                IpcFrameIO.ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult(),
                IpcJsonContext.Default.HelloAckEnvelope);
            var envelope = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.GetStateSnapshot,
                Workspace,
                new GetStateSnapshotRequest(0, null));
            var utf8 = IpcJson.Serialize(envelope, IpcJsonContext.Default.GetStateSnapshotRequestEnvelope);
            IpcFrameIO.WriteAsync(stream, utf8, CancellationToken.None).GetAwaiter().GetResult();
            IpcFrameIO.WriteAsync(stream, utf8, CancellationToken.None).GetAwaiter().GetResult();
            entered.Task.GetAwaiter().GetResult();
            var error = ReadError(stream, CancellationToken.None);
            AssertEqual(IpcErrorCodes.DuplicateRequest, error.Code, "Live duplicate request must fail closed.");
            hold.TrySetResult();
        });
    }

    private static void CriticalOutboundQueueFailsClosedWithoutSilentDrop()
    {
        var scheduler = new IpcOutboundScheduler();
        try
        {
            var payload = "{}"u8.ToArray();
            for (var i = 0; i < IpcProtocol.CriticalOutboundCapacity; i++)
            {
                AssertEqual(true, scheduler.TryEnqueueCritical(payload, IpcSemanticTypes.HeartbeatAck), "Critical queue accepted below capacity.");
            }

            AssertEqual(false, scheduler.TryEnqueueCritical(payload, IpcSemanticTypes.HeartbeatAck), "Critical saturation must not silently drop; enqueue must fail closed.");
            for (var i = 0; i < IpcProtocol.SnapshotOutboundCapacity; i++)
            {
                AssertEqual(true, scheduler.TryEnqueueSnapshot(payload, IpcSemanticTypes.GetStateSnapshot), "Snapshot queue accepted below capacity.");
            }

            AssertEqual(false, scheduler.TryEnqueueSnapshot(payload, IpcSemanticTypes.GetStateSnapshot), "Snapshot saturation must fail closed.");
        }
        finally
        {
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void WriterWakeDeliversEventAfterRepeatedCriticalIdle()
    {
        var (left, right) = IpcConnectedStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var scheduler = new IpcOutboundScheduler();
        byte[]? pendingEvent = null;
        var eventPayload = """{"kind":"event"}"""u8.ToArray();
        var criticalPayload = """{"kind":"critical"}"""u8.ToArray();
        try
        {
            scheduler.Start(
                left,
                () =>
                {
                    var payload = pendingEvent;
                    pendingEvent = null;
                    return payload;
                },
                timeout.Token);

            for (var i = 0; i < 32; i++)
            {
                AssertEqual(
                    true,
                    scheduler.TryEnqueueCritical(criticalPayload, IpcSemanticTypes.HeartbeatAck),
                    "Repeated critical wakes must enqueue.");
                var frame = IpcFrameIO.ReadAsync(right, timeout.Token).GetAwaiter().GetResult();
                AssertTrue(
                    frame.AsSpan().SequenceEqual(criticalPayload),
                    "Idle writer must drain each critical/heartbeat frame before the next wait.");
            }

            pendingEvent = eventPayload;
            scheduler.PulseEvents();
            using var eventWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            byte[] delivered;
            try
            {
                delivered = IpcFrameIO.ReadAsync(right, eventWait.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "A single event pulse must wake the current writer and deliver the event without another critical/heartbeat enqueue.");
            }

            AssertTrue(delivered.AsSpan().SequenceEqual(eventPayload), "The coalesced wake must deliver the event promptly.");
        }
        finally
        {
            timeout.Cancel();
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            left.Dispose();
            right.Dispose();
        }
    }

    private static void GapEventIsExactAndCoalescedForLiveSubscriber()
    {
        var ring = new IpcEventRing("stream-gap");
        for (var i = 0; i < 300; i++)
        {
            ring.PublishNotice("flood", i.ToString(CultureInfo.InvariantCulture));
        }

        var token = IpcBootstrapToken.Create();
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = ring
        }, async client =>
        {
            await client.RequestAsync(
                IpcSemanticTypes.SubscribeEvents,
                new SubscribeEventsRequest(ring.EventStreamId, 0),
                IpcJsonContext.Default.SubscribeEventsRequestEnvelope,
                IpcJsonContext.Default.SubscribeEventsResponseEnvelope,
                CancellationToken.None);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var delivered = await client.Events.ReadAsync(wait.Token);
            AssertEqual(IpcSemanticTypes.Gap, delivered.SemanticType, "Overflowed live subscribe must deliver GapEvent.");
            var gap = IpcJson.DeserializePayload(delivered.Payload, IpcJsonContext.Default.GapEvent);
            AssertEqual(1L, gap.FromSeq, "GapEvent fromSeq must be inclusive.");
            AssertEqual(44L, gap.ToSeq, "GapEvent toSeq must be the exact inclusive missing range.");

            ring.PublishNotice("more", "301");
            await Task.Delay(100);
            AssertEqual(false, client.Events.TryRead(out _), "Repeated overflow must not enqueue another GapEvent.");

            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, ring.EventStreamId),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(301L, snapshot.Payload.SnapshotSeq, "Snapshot must progress after NeedsResync.");
        });
    }

    private static void AgentCommandsResolveSessionAndRejectStolenRevokedExpiredProofs()
    {
        var clock = new MutableClock(DateTimeOffset.FromUnixTimeMilliseconds(4_000_000));
        var store = new MemoryRunStore();
        store.Seed(RunId, "writer");
        store.Seed("run-other", "writer");
        var bindings = new TrustedIpcBindingRegistry();
        bindings.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.AgentRuntime, "worker-1", "channel-1", Scope));
        var recorder = new RecordingCommandHandler();
        var token = IpcBootstrapToken.Create();
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = bindings,
            RunSessions = new RunSessionService(store, clock),
            Clock = clock,
            Commands = recorder
        }, async client =>
        {
            var created = await client.RequestAsync(
                IpcSemanticTypes.CreateRunSession,
                new LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest(RunId, null),
                IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                CancellationToken.None);
            var proof = new RunSessionProof(RunId, created.Payload.OpaqueToken);

            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.SearchNarrative,
                    new SearchNarrativeRequest("q", 1, proof),
                    IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
                    IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Unimplemented SearchNarrative must remain unavailable.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.CommandUnavailable, exception.ErrorCode, "Resolved Agent commands must not invent WP12/WP13 behavior.");
            }

            AssertEqual(PrincipalKind.AgentRun, recorder.Kind ?? default, "Resolved Agent command must be AGENT_RUN.");

            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.SearchNarrative,
                    new SearchNarrativeRequest("q", 1, new RunSessionProof(RunId, "not-a-valid-token")),
                    IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
                    IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Stolen/malformed token must fail closed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.InvalidSession, exception.ErrorCode, "Stolen or malformed RunSession must fail closed.");
            }

            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.SearchNarrative,
                    new SearchNarrativeRequest("q", 1, new RunSessionProof("run-other", proof.OpaqueToken)),
                    IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
                    IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Forged runId must fail closed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.BindingMismatch, exception.ErrorCode, "Forged runId must not select another Run.");
            }

            await client.RequestAsync(
                IpcSemanticTypes.RevokeRunSession,
                new RevokeRunSessionRequest(created.Payload.HandleId, proof),
                IpcJsonContext.Default.RevokeRunSessionRequestEnvelope,
                IpcJsonContext.Default.RevokeRunSessionResponseEnvelope,
                CancellationToken.None);
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.SearchNarrative,
                    new SearchNarrativeRequest("q", 1, proof),
                    IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
                    IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Revoked session must fail closed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.SessionRevoked, exception.ErrorCode, "Revoked RunSession must fail closed.");
            }
        });

        var expiredStore = new MemoryRunStore();
        expiredStore.Seed(RunId, "writer");
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(5_000_000);
        var issued = Success(new RunSessionService(expiredStore, new MutableClock(issuedAt)).Create(
            new AppCreateRunSessionRequest(RunId, Channel(), issuedAt.AddMinutes(1))));
        var expiredToken = IpcBootstrapToken.Create();
        RunHosted(expiredToken, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(expiredToken),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = bindings,
            RunSessions = new RunSessionService(expiredStore, new MutableClock(issuedAt.AddMinutes(10))),
            Clock = new MutableClock(issuedAt.AddMinutes(10))
        }, async client =>
        {
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.SearchNarrative,
                    new SearchNarrativeRequest("q", 1, new RunSessionProof(RunId, issued.Token.ExportOnceForAuthenticatedTransport())),
                    IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
                    IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Expired session must fail closed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.SessionExpired, exception.ErrorCode, "Expired RunSession must fail closed.");
            }
        });
    }

    private static void StaleSessionAfterDisconnectIsRevoked()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var store = new MemoryRunStore();
        store.Seed(RunId, "writer");
        var bindings = new TrustedIpcBindingRegistry();
        bindings.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.AgentRuntime, "worker-1", "channel-1", Scope));
        var initial = IpcBootstrapToken.Create();
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(initial),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = bindings,
            RunSessions = new RunSessionService(store, clock),
            Clock = clock
        };

        string? rotated = null;
        string? opaque = null;
        RunHosted(initial, options, async client =>
        {
            rotated = client.RotatedBootstrapToken;
            var created = await client.RequestAsync(
                IpcSemanticTypes.CreateRunSession,
                new LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest(RunId, null),
                IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                CancellationToken.None);
            opaque = created.Payload.OpaqueToken;
        });

        try
        {
            RunHosted(initial, options, _ => Task.CompletedTask);
            throw new InvalidOperationException("Original bootstrap must not work after rotation.");
        }
        catch (IpcProtocolException exception)
        {
            AssertEqual(IpcErrorCodes.AuthBootstrapRejected, exception.ErrorCode, "Rotated-away bootstrap must be rejected.");
        }

        RunHosted(rotated!, options, async client =>
        {
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.SearchNarrative,
                    new SearchNarrativeRequest("q", 1, new RunSessionProof(RunId, opaque!)),
                    IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
                    IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Stale session after disconnect must fail closed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.SessionRevoked, exception.ErrorCode, "Disconnect must revoke the previous Runtime session.");
            }
        });
    }

    private static void RuntimeCannotBecomeUserInteractive()
    {
        var token = IpcBootstrapToken.Create();
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            NativeUi = new TrustedNativePrincipalSource("wp11-must-not-be-used")
        }, async client =>
        {
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.SearchNarrative,
                    new SearchNarrativeRequest("q", 1, null),
                    IpcJsonContext.Default.SearchNarrativeRequestEnvelope,
                    IpcJsonContext.Default.SearchNarrativeResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Runtime without RunSession must fail closed.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.InvalidSession, exception.ErrorCode, "Runtime must not use Native UI principal.");
            }

            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.AcceptAuthority,
                    new AcceptAuthorityRequest("cand-1", "key-1", "author"),
                    IpcJsonContext.Default.AcceptAuthorityRequestEnvelope,
                    IpcJsonContext.Default.AcceptAuthorityResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Runtime must not execute UI-only mutations as USER_INTERACTIVE.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.InvalidSession, exception.ErrorCode, "Runtime AcceptAuthority without RunSession must fail closed.");
            }
        });
    }

    private static void BootstrapBadTokenDoesNotRotate()
    {
        var token = IpcBootstrapToken.Create();
        var authenticator = new IpcBootstrapAuthenticator(token);
        var rejected = authenticator.Authenticate(IpcBootstrapToken.Create(), IpcClientKind.AgentRuntime, IpcClientKind.AgentRuntime);
        AssertEqual(false, rejected.Accepted, "A bad bootstrap token must be rejected.");
        AssertEqual(false, authenticator.HasUnconfirmedRotation, "A bad token must not issue a pending rotation.");
        var accepted = authenticator.Authenticate(token, IpcClientKind.AgentRuntime, IpcClientKind.AgentRuntime);
        AssertEqual(true, accepted.Accepted, "The original secret must still authenticate after a rejected Hello.");
        AssertEqual(true, authenticator.HasUnconfirmedRotation, "A successful Hello must issue a pending rotation.");
        authenticator.Release();
        var recovered = authenticator.Authenticate(token, IpcClientKind.AgentRuntime, IpcClientKind.AgentRuntime);
        AssertEqual(true, recovered.Accepted, "An unconfirmed rotation must still accept the pre-Hello secret.");
        authenticator.Confirm();
        authenticator.Release();
        var stale = authenticator.Authenticate(token, IpcClientKind.AgentRuntime, IpcClientKind.AgentRuntime);
        AssertEqual(false, stale.Accepted, "The previous secret must be rejected after a committed rotation.");
        var rotated = authenticator.Authenticate(recovered.RotatedToken!, IpcClientKind.AgentRuntime, IpcClientKind.AgentRuntime);
        AssertEqual(true, rotated.Accepted, "The committed rotated secret must authenticate.");
    }

    private static void BootstrapAckLossRecoversWithoutCoreRestart()
    {
        var token = IpcBootstrapToken.Create();
        var authenticator = new IpcBootstrapAuthenticator(token);
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = authenticator,
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D"))
        };

        RunRaw(options, stream =>
        {
            WriteHello(stream, token, 1, 1, IpcClientKind.AgentRuntime, CancellationToken.None);
            WaitUntil(
                () => authenticator.HasUnconfirmedRotation && authenticator.HasActiveConnection,
                "Core must accept Hello before the client obtains HelloAck.");
        });
        AssertEqual(true, authenticator.HasUnconfirmedRotation, "ACK-loss must keep the one-generation pending credential.");
        AssertEqual(false, authenticator.HasActiveConnection, "ACK-loss must release the endpoint reservation.");

        RunHosted(token, options, async client =>
        {
            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, null),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(false, snapshot.Payload.ResyncRequired, "The original secret must recover after HelloAck loss without a Core restart.");
        });

        try
        {
            RunHosted(token, options, _ => Task.CompletedTask);
            throw new InvalidOperationException("The original secret must be invalid after a completed rotation.");
        }
        catch (IpcProtocolException exception)
        {
            AssertEqual(IpcErrorCodes.AuthBootstrapRejected, exception.ErrorCode, "Committed rotation must invalidate the previous secret.");
        }
    }

    private static void BootstrapRejectsConcurrentAuthenticatedConnection()
    {
        var token = IpcBootstrapToken.Create();
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D"))
        };

        RunHosted(token, options, async client =>
        {
            var (left, right) = IpcConnectedStreamPair.Create();
            using var secondTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Task.Run(() => IpcServerSession.ServeAsync(left, options, secondTimeout.Token), secondTimeout.Token);
            try
            {
                try
                {
                    await IpcClientSession.HandshakeAsync(
                        right,
                        Workspace,
                        token,
                        IpcClientKind.AgentRuntime,
                        TimeSpan.FromMilliseconds(200),
                        secondTimeout.Token);
                    throw new InvalidOperationException("A second concurrent Hello must not authenticate.");
                }
                catch (IpcProtocolException exception)
                {
                    AssertEqual(IpcErrorCodes.AuthBootstrapReplay, exception.ErrorCode, "A second concurrent Hello must be AUTH_BOOTSTRAP_REPLAY.");
                }
            }
            finally
            {
                secondTimeout.Cancel();
                try
                {
                    await server;
                }
                catch (OperationCanceledException)
                {
                }
                catch (AggregateException)
                {
                }

                left.Dispose();
                right.Dispose();
            }

            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, null),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(false, snapshot.Payload.ResyncRequired, "The first authenticated connection must remain usable.");
        });
    }

    private static void ClientEventBufferOverflowIsExplicitAndRecoverable()
    {
        var token = IpcBootstrapToken.Create();
        var ring = new IpcEventRing("stream-client-overflow");
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = ring
        }, async client =>
        {
            await client.RequestAsync(
                IpcSemanticTypes.SubscribeEvents,
                new SubscribeEventsRequest(ring.EventStreamId, 0),
                IpcJsonContext.Default.SubscribeEventsRequestEnvelope,
                IpcJsonContext.Default.SubscribeEventsResponseEnvelope,
                CancellationToken.None);
            for (var i = 0; i < IpcProtocol.ClientEventBufferCapacity + 8; i++)
            {
                ring.PublishNotice("fill", i.ToString(CultureInfo.InvariantCulture));
            }

            WaitUntil(() => client.HasEventDiscontinuity, "Saturating the client event buffer must record an explicit discontinuity.");

            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, ring.EventStreamId),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(ring.HeadSeq, snapshot.Payload.SnapshotSeq, "Snapshot responses must still progress after local overflow.");
            var cancel = await client.CancelAsync(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), CancellationToken.None);
            AssertEqual(CancelResponse.StateUnknown, cancel.State, "Cancel must still progress after local overflow.");

            var seen = new List<long>();
            while (client.Events.TryRead(out var wire))
            {
                if (wire.SemanticType != IpcSemanticTypes.CoreNotice)
                {
                    continue;
                }

                seen.Add(IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.CoreNoticeEvent).Seq);
            }

            AssertEqual(IpcProtocol.ClientEventBufferCapacity, seen.Count, "The client buffer must remain bounded.");
            for (var i = 0; i < seen.Count; i++)
            {
                AssertEqual(i + 1L, seen[i], "Local overflow must not present a silent seq skip.");
            }

            AssertTrue(
                !seen.Contains(IpcProtocol.ClientEventBufferCapacity + 1L),
                "Later ordinary events must not be exposed as a continuous stream after local loss.");

            var recovery = new IpcTransportRecovery();
            await recovery.RestoreAsync(client, CancellationToken.None);
            AssertEqual(false, client.HasEventDiscontinuity, "Snapshot/resubscribe must clear the local discontinuity.");
            ring.PublishNotice("restored", "tail");
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            IpcWireEnvelope delivered;
            do
            {
                delivered = await client.Events.ReadAsync(wait.Token);
            }
            while (delivered.SemanticType != IpcSemanticTypes.CoreNotice);

            var notice = IpcJson.DeserializePayload(delivered.Payload, IpcJsonContext.Default.CoreNoticeEvent);
            AssertEqual(ring.HeadSeq, notice.Seq, "Snapshot/resubscribe must restore ordinary-event continuity.");
        });
    }

    private static void StalledNonReadingPeerUnblocksEndpoint()
    {
        var token = IpcBootstrapToken.Create();
        var authenticator = new IpcBootstrapAuthenticator(token);
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = authenticator,
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            WriteTimeout = TimeSpan.FromMilliseconds(IpcProtocol.WriteTimeoutMs),
            DrainTimeout = TimeSpan.FromMilliseconds(IpcProtocol.DrainTimeoutMs)
        };

        var (left, right) = IpcConnectedStreamPair.Create(segmentCapacity: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var sessionCts = new CancellationTokenSource();
        var server = Task.Run(() => IpcServerSession.ServeAsync(left, options, sessionCts.Token), timeout.Token);
        string? rotated = null;
        try
        {
            WriteHello(right, token, 1, 1, IpcClientKind.AgentRuntime, timeout.Token);
            var ack = IpcJson.Deserialize(
                IpcFrameIO.ReadAsync(right, timeout.Token).GetAwaiter().GetResult(),
                IpcJsonContext.Default.HelloAckEnvelope);
            rotated = ack.Payload.RotatedBootstrapToken;
            WriteHeartbeat(right, 1, timeout.Token);
            WaitUntil(() => !authenticator.HasUnconfirmedRotation, "The stalled peer must first confirm rotation via a post-Hello frame.");
            var started = DateTime.UtcNow;
            sessionCts.Cancel();
            try
            {
                server.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("A non-reading peer must not hang writer shutdown beyond the bounded write/drain lifetime.");
            }

            AssertTrue(
                DateTime.UtcNow - started < TimeSpan.FromSeconds(8),
                "Connection shutdown must complete within the bounded write/drain window.");
        }
        finally
        {
            left.Dispose();
            right.Dispose();
        }

        RunHosted(rotated!, options, async client =>
        {
            var snapshot = await client.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(0, null),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                CancellationToken.None);
            AssertEqual(false, snapshot.Payload.ResyncRequired, "The endpoint must accept a subsequent connection after a stalled write is cancelled.");
        });
    }

    private static void ProductionReconnectSnapshotSubscribeGapAndEpoch()
    {
        var token = IpcBootstrapToken.Create();
        var ring = new IpcEventRing(Guid.NewGuid().ToString("D"));
        var recorder = new RecordingCommandHandler();
        var authenticator = new IpcBootstrapAuthenticator(token);
        IpcServerOptions serveOptions = new()
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = authenticator,
            EventRing = ring,
            Commands = recorder
        };
        var incoming = System.Threading.Channels.Channel.CreateUnbounded<Stream>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Stream? currentServer = null;
        var accept = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                var (left, right) = IpcConnectedStreamPair.Create();
                currentServer = left;
                incoming.Writer.TryWrite(right);
                try
                {
                    await IpcServerSession.ServeAsync(left, serveOptions, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                finally
                {
                    left.Dispose();
                }
            }
        }, timeout.Token);

        var recovery = new IpcTransportRecovery();
        var reconnect = new IpcReconnectClient(
            cancellationToken => incoming.Reader.ReadAsync(cancellationToken).AsTask(),
            Workspace,
            token,
            IpcClientKind.AgentRuntime,
            TimeSpan.FromMilliseconds(200),
            recovery);
        var run = Task.Run(() => reconnect.RunAsync(timeout.Token), timeout.Token);
        try
        {
            WaitUntil(() => recovery.RestoreCount >= 1, "Production reconnect must snapshot and subscribe on first connect.");
            AssertTrue(
                StringComparer.Ordinal.Equals(ring.EventStreamId, recovery.LastEventStreamId),
                "First restore must use the Core-owned epoch.");
            AssertEqual(0L, recovery.LastKnownSeq, "Empty snapshot watermark must be 0.");

            ring.PublishNotice("one", "1");
            WaitUntil(() => recovery.LastKnownSeq >= 1, "Ordinary events must advance lastKnownSeq.");

            var restores = recovery.RestoreCount;
            var epoch = recovery.LastEventStreamId;
            currentServer!.Dispose();
            WaitUntil(() => recovery.RestoreCount > restores, "Reconnect on the same epoch must restore.");
            AssertTrue(
                StringComparer.Ordinal.Equals(epoch, recovery.LastEventStreamId),
                "Same-process reconnect must keep the event-stream epoch.");
            AssertTrue(recovery.LastKnownSeq >= 1, "Reconnect snapshot must keep the trusted watermark.");

            restores = recovery.RestoreCount;
            for (var i = 0; i < 300; i++)
            {
                ring.PublishNotice("flood", i.ToString(CultureInfo.InvariantCulture));
            }

            WaitUntil(
                () => recovery.RestoreCount > restores && recovery.LastKnownSeq == ring.HeadSeq,
                "GapEvent must force snapshot/resubscribe.");
            ring.PublishNotice("after-gap", "tail");
            WaitUntil(() => recovery.LastKnownSeq == ring.HeadSeq, "Continuity must resume after Gap recovery.");

            var newRing = new IpcEventRing(Guid.NewGuid().ToString("D"));
            serveOptions = new IpcServerOptions
            {
                WorkspaceInstanceId = Workspace,
                ExpectedClientKind = IpcClientKind.AgentRuntime,
                Bootstrap = authenticator,
                EventRing = newRing,
                Commands = recorder
            };
            restores = recovery.RestoreCount;
            currentServer!.Dispose();
            WaitUntil(
                () => recovery.RestoreCount > restores &&
                      StringComparer.Ordinal.Equals(recovery.LastEventStreamId, newRing.EventStreamId),
                "A new epoch must discard previous continuity and resync.");
            AssertEqual(0L, recovery.LastKnownSeq, "A new epoch snapshot watermark must not compare previous seq values.");
            AssertTrue(recorder.Kind is null, "Transport recovery must not auto-replay business mutations.");
            AssertTrue(
                !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.SearchNarrative) &&
                !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.CreateRunSession),
                "Business mutations are not in the safe automatic recovery catalog.");
        }
        finally
        {
            timeout.Cancel();
            incoming.Writer.TryComplete();
            try
            {
                accept.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            try
            {
                run.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }
    }

    private static AuthenticatedChannelContext Channel() =>
        new("channel-1", AuthenticatedClientKind.AgentRuntime, "worker-1", Scope);

    private static void RunHosted(string token, IpcServerOptions options, Func<IpcClientSession, Task> test)
    {
        var (left, right) = IpcConnectedStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(() => IpcServerSession.ServeAsync(left, options, timeout.Token), timeout.Token);
        IpcClientSession? client = null;
        try
        {
            client = IpcClientSession.HandshakeAsync(
                    right,
                    Workspace,
                    token,
                    IpcClientKind.AgentRuntime,
                    TimeSpan.FromMilliseconds(200),
                    timeout.Token)
                .GetAwaiter()
                .GetResult();
            test(client).GetAwaiter().GetResult();
        }
        finally
        {
            client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            timeout.Cancel();
            try
            {
                server.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            left.Dispose();
            right.Dispose();
        }
    }

    private static void RunRaw(IpcServerOptions options, Action<Stream> test)
    {
        var (left, right) = IpcConnectedStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(() => IpcServerSession.ServeAsync(left, options, timeout.Token), timeout.Token);
        try
        {
            test(right);
        }
        finally
        {
            timeout.Cancel();
            try
            {
                server.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            left.Dispose();
            right.Dispose();
        }
    }

    private static void WriteHello(
        Stream stream,
        string token,
        int protocolMin,
        int protocolMax,
        IpcClientKind clientKind,
        CancellationToken cancellationToken)
    {
        var hello = new HelloRequest(protocolMin, protocolMax, token, clientKind, Guid.NewGuid());
        IpcFrameIO.WriteAsync(
                stream,
                IpcJson.Serialize(
                    IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Hello, Workspace, hello),
                    IpcJsonContext.Default.HelloRequestEnvelope),
                cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static void WriteHeartbeat(Stream stream, long sequence, CancellationToken cancellationToken)
    {
        var heartbeat = IpcEnvelopeFactory.Create(
            IpcMessageType.Control,
            IpcSemanticTypes.Heartbeat,
            Workspace,
            new Heartbeat(sequence));
        IpcFrameIO.WriteAsync(
                stream,
                IpcJson.Serialize(heartbeat, IpcJsonContext.Default.HeartbeatEnvelope),
                cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(20);
        }

        throw new InvalidOperationException(message);
    }

    private static IpcError ReadError(Stream stream, CancellationToken cancellationToken)
    {
        var frame = IpcFrameIO.ReadAsync(stream, cancellationToken).GetAwaiter().GetResult();
        return IpcJson.Deserialize(frame, IpcJsonContext.Default.ErrorEnvelope).Payload;
    }

    private static CreateRunSessionResult Success(RunSessionResult<CreateRunSessionResult> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success, got {result.Failure?.Code}.");
        }

        return result.Value;
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class RecordingCommandHandler : IIpcApplicationCommandHandler
    {
        public PrincipalKind? Kind { get; private set; }

        public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
        {
            Kind = context.Principal?.Kind;
            return Task.FromResult<IpcApplicationCommandResult?>(null);
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : ISecurityClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class MemoryRunStore : IRunSessionStore
    {
        private readonly Dictionary<string, DurableRunIdentity> runs = new(StringComparer.Ordinal);
        private readonly List<StoredRunSession> sessions = [];

        public long LastPersistedExpiryMs { get; private set; }

        public void Seed(string runId, string role) => runs[runId] = new DurableRunIdentity(runId, role);

        public DurableRunIdentity? LoadRun(string runId) => runs.TryGetValue(runId, out var run) ? run : null;

        public StoredRunSession IssueReplacingActive(PersistRunSessionRequest request)
        {
            for (var index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
                if (session.RevokedAtMs is null &&
                    StringComparer.Ordinal.Equals(session.RunId, request.RunId) &&
                    StringComparer.Ordinal.Equals(session.WorkerInstanceId, request.WorkerInstanceId) &&
                    StringComparer.Ordinal.Equals(session.ChannelInstanceId, request.ChannelInstanceId) &&
                    StringComparer.Ordinal.Equals(session.ProjectScope, request.ProjectScope))
                {
                    sessions[index] = session with { RevokedAtMs = request.CreatedAtMs };
                }
            }

            var stored = new StoredRunSession(
                Guid.NewGuid().ToString("D"),
                request.RunId,
                request.WorkerInstanceId,
                request.ChannelInstanceId,
                request.ProjectScope,
                request.TokenHash,
                request.ExpiresAtMs,
                null,
                request.CreatedAtMs);
            sessions.Add(stored);
            LastPersistedExpiryMs = request.ExpiresAtMs;
            return stored;
        }

        public StoredRunSession? FindByTokenHash(string tokenHash) =>
            sessions.LastOrDefault(session => StringComparer.Ordinal.Equals(session.TokenHash, tokenHash));

        public StoredRunSession? FindByHandleId(string handleId) =>
            sessions.LastOrDefault(session => StringComparer.Ordinal.Equals(session.HandleId, handleId));

        public int RevokeHandle(string handleId, long revokedAtMs)
        {
            var count = 0;
            for (var index = 0; index < sessions.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(sessions[index].HandleId, handleId) && sessions[index].RevokedAtMs is null)
                {
                    sessions[index] = sessions[index] with { RevokedAtMs = revokedAtMs };
                    count++;
                }
            }

            return count;
        }

        public int RevokeByRun(string runId, long revokedAtMs)
        {
            var count = 0;
            for (var index = 0; index < sessions.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(sessions[index].RunId, runId) && sessions[index].RevokedAtMs is null)
                {
                    sessions[index] = sessions[index] with { RevokedAtMs = revokedAtMs };
                    count++;
                }
            }

            return count;
        }

        public int RevokeByChannelWorker(string channelInstanceId, string workerInstanceId, long revokedAtMs)
        {
            var count = 0;
            for (var index = 0; index < sessions.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(sessions[index].ChannelInstanceId, channelInstanceId) &&
                    StringComparer.Ordinal.Equals(sessions[index].WorkerInstanceId, workerInstanceId) &&
                    sessions[index].RevokedAtMs is null)
                {
                    sessions[index] = sessions[index] with { RevokedAtMs = revokedAtMs };
                    count++;
                }
            }

            return count;
        }
    }
}
