using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static async Task RunWp11TestsAsync()
    {
        await FullIpcSnapshotMultiplexAndUnknownTypeAsync();
        await OversizeFrameIsRejectedWithoutAllocatingBodyAsync();
        await WrongBootstrapAndProtocolMismatchAsync();
        await MalformedJsonAndMidFrameDisconnectRecoverAsync();
        await CoreRestartForcesEventStreamResyncAsync();
        await ProductionCoreRefusesSessionWithoutTrustedBindingAsync();
        await ProductionReconnectClientRestoresSnapshotAndNewEpochAsync();
        Console.WriteLine("WP11 IPC integration tests passed (7).");
    }

    private static async Task FullIpcSnapshotMultiplexAndUnknownTypeAsync()
    {
        var workspaceInstanceId = $"wp11-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var core = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        try
        {
            using var client = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token);
            var helloAck = await HelloAsync(client, workspaceInstanceId, bootstrapToken, testTimeout.Token);
            AssertEqual(1, helloAck.Payload.NegotiatedProtocol, "WP11 Core did not negotiate IPC v1.");
            AssertTrue(!string.IsNullOrWhiteSpace(helloAck.Payload.EventStreamId), "HelloAck must identify the event-stream epoch.");
            AssertTrue(!string.IsNullOrWhiteSpace(helloAck.Payload.RotatedBootstrapToken), "HelloAck must rotate the bootstrap secret.");

            var first = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.GetStateSnapshot,
                workspaceInstanceId,
                new GetStateSnapshotRequest(0, null));
            var second = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.GetStateSnapshot,
                workspaceInstanceId,
                new GetStateSnapshotRequest(0, "stale-epoch"));
            await WriteAsync(client, first, IpcJsonContext.Default.GetStateSnapshotRequestEnvelope, testTimeout.Token);
            await WriteAsync(client, second, IpcJsonContext.Default.GetStateSnapshotRequestEnvelope, testTimeout.Token);

            var responses = new List<IpcEnvelope<GetStateSnapshotResponse>>
            {
                await ReadAsync(client, IpcJsonContext.Default.GetStateSnapshotResponseEnvelope, testTimeout.Token),
                await ReadAsync(client, IpcJsonContext.Default.GetStateSnapshotResponseEnvelope, testTimeout.Token)
            };
            AssertTrue(responses.Any(item => item.RequestId == first.RequestId), "First snapshot correlation was lost.");
            AssertTrue(responses.Any(item => item.RequestId == second.RequestId), "Second snapshot correlation was lost.");
            var stale = responses.Single(item => item.RequestId == second.RequestId);
            AssertEqual(true, stale.Payload.ResyncRequired, "Stale event-stream id must force resync.");

            await WriteAsync(
                client,
                IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Heartbeat, workspaceInstanceId, new Heartbeat(9)),
                IpcJsonContext.Default.HeartbeatEnvelope,
                testTimeout.Token);
            var heartbeatAck = await ReadAsync(client, IpcJsonContext.Default.HeartbeatAckEnvelope, testTimeout.Token);
            AssertEqual(9L, heartbeatAck.Payload.Sequence, "Heartbeat must coexist with multiplexed requests.");

            await WriteAsync(
                client,
                IpcEnvelopeFactory.Create(IpcMessageType.Request, "notARealOperation", workspaceInstanceId, new Heartbeat(1)),
                IpcJsonContext.Default.HeartbeatEnvelope,
                testTimeout.Token);
            var error = await ReadAsync(client, IpcJsonContext.Default.ErrorEnvelope, testTimeout.Token);
            AssertEqual(IpcErrorCodes.UnsupportedSemanticType, error.Payload.Code, "Unknown semantic type must fail closed.");
        }
        finally
        {
            StopCore(core);
        }
    }

    private static async Task OversizeFrameIsRejectedWithoutAllocatingBodyAsync()
    {
        var workspaceInstanceId = $"wp11-oversize-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var core = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        try
        {
            using var client = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token);
            _ = await HelloAsync(client, workspaceInstanceId, bootstrapToken, testTimeout.Token);

            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)IpcProtocol.MaximumFrameBytes + 1);
            await client.WriteAsync(header, testTimeout.Token);
            await client.FlushAsync(testTimeout.Token);
            try
            {
                var error = await ReadAsync(client, IpcJsonContext.Default.ErrorEnvelope, testTimeout.Token);
                AssertEqual(IpcErrorCodes.InvalidFrame, error.Payload.Code, "Oversize frames must return IPC_INVALID_FRAME.");
            }
            catch (EndOfStreamException)
            {
            }
        }
        finally
        {
            StopCore(core);
        }
    }

    private static async Task WrongBootstrapAndProtocolMismatchAsync()
    {
        var workspaceInstanceId = $"wp11-auth-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var core = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        try
        {
            using (var wrong = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token))
            {
                var hello = new HelloRequest(1, 1, IpcBootstrapToken.Create(), IpcClientKind.AgentRuntime, Guid.NewGuid());
                await WriteAsync(
                    wrong,
                    IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Hello, workspaceInstanceId, hello),
                    IpcJsonContext.Default.HelloRequestEnvelope,
                    testTimeout.Token);
                var rejected = await ReadAsync(wrong, IpcJsonContext.Default.ErrorEnvelope, testTimeout.Token);
                AssertEqual(IpcErrorCodes.AuthBootstrapRejected, rejected.Payload.Code, "Wrong bootstrap token must fail closed.");
            }

            using (var mismatch = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token))
            {
                var hello = new HelloRequest(2, 2, bootstrapToken, IpcClientKind.AgentRuntime, Guid.NewGuid());
                await WriteAsync(
                    mismatch,
                    IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Hello, workspaceInstanceId, hello),
                    IpcJsonContext.Default.HelloRequestEnvelope,
                    testTimeout.Token);
                var error = await ReadAsync(mismatch, IpcJsonContext.Default.ErrorEnvelope, testTimeout.Token);
                AssertEqual(IpcErrorCodes.ProtocolNoCommonVersion, error.Payload.Code, "Protocol mismatch must fail closed.");
            }

            using var client = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token);
            var ack = await HelloAsync(client, workspaceInstanceId, bootstrapToken, testTimeout.Token);
            AssertEqual(1, ack.Payload.NegotiatedProtocol, "Rejected Hello must not consume the bootstrap secret.");
        }
        finally
        {
            StopCore(core);
        }
    }

    private static async Task MalformedJsonAndMidFrameDisconnectRecoverAsync()
    {
        var workspaceInstanceId = $"wp11-fault-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var core = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        try
        {
            string? rotated;
            using (var malformed = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token))
            {
                var ack = await HelloAsync(malformed, workspaceInstanceId, bootstrapToken, testTimeout.Token);
                rotated = ack.Payload.RotatedBootstrapToken;
                var junk = Encoding.UTF8.GetBytes("{not-json");
                await malformed.WriteAsync(IpcFrameHeader.Create(junk.Length), testTimeout.Token);
                await malformed.WriteAsync(junk, testTimeout.Token);
                await malformed.FlushAsync(testTimeout.Token);
                try
                {
                    var error = await ReadAsync(malformed, IpcJsonContext.Default.ErrorEnvelope, testTimeout.Token);
                    AssertEqual(IpcErrorCodes.MalformedFrame, error.Payload.Code, "Malformed JSON must return IPC_MALFORMED_FRAME.");
                }
                catch (EndOfStreamException)
                {
                }
            }

            using (var partial = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token))
            {
                var secondAck = await HelloAsync(partial, workspaceInstanceId, rotated!, testTimeout.Token);
                rotated = secondAck.Payload.RotatedBootstrapToken;
                await partial.WriteAsync(new byte[] { 0x05, 0x00 }, testTimeout.Token);
                await partial.FlushAsync(testTimeout.Token);
            }

            using var recovered = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token);
            var recoveredAck = await HelloAsync(recovered, workspaceInstanceId, rotated!, testTimeout.Token);
            var snapshot = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.GetStateSnapshot,
                workspaceInstanceId,
                new GetStateSnapshotRequest(0, recoveredAck.Payload.EventStreamId));
            await WriteAsync(recovered, snapshot, IpcJsonContext.Default.GetStateSnapshotRequestEnvelope, testTimeout.Token);
            var response = await ReadAsync(recovered, IpcJsonContext.Default.GetStateSnapshotResponseEnvelope, testTimeout.Token);
            AssertEqual(false, response.Payload.ResyncRequired, "Reconnect after a mid-frame disconnect must still snapshot.");
        }
        finally
        {
            StopCore(core);
        }
    }

    private static async Task CoreRestartForcesEventStreamResyncAsync()
    {
        var workspaceInstanceId = $"wp11-epoch-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var firstCore = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        Process? restarted = null;
        try
        {
            string firstEpoch;
            using (var client = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token))
            {
                var ack = await HelloAsync(client, workspaceInstanceId, bootstrapToken, testTimeout.Token);
                firstEpoch = ack.Payload.EventStreamId;
            }

            StopCore(firstCore);
            restarted = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
            using var second = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token);
            var secondAck = await HelloAsync(second, workspaceInstanceId, bootstrapToken, testTimeout.Token);
            AssertTrue(!StringComparer.Ordinal.Equals(firstEpoch, secondAck.Payload.EventStreamId), "Core restart must mint a new event-stream epoch.");
            var snapshot = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.GetStateSnapshot,
                workspaceInstanceId,
                new GetStateSnapshotRequest(12, firstEpoch));
            await WriteAsync(second, snapshot, IpcJsonContext.Default.GetStateSnapshotRequestEnvelope, testTimeout.Token);
            var response = await ReadAsync(second, IpcJsonContext.Default.GetStateSnapshotResponseEnvelope, testTimeout.Token);
            AssertEqual(true, response.Payload.ResyncRequired, "A previous Core epoch must not be compared as a continuous stream.");
        }
        finally
        {
            StopCore(firstCore);
            if (restarted is not null)
            {
                StopCore(restarted);
                restarted.Dispose();
            }
        }
    }

    private static async Task ProductionCoreRefusesSessionWithoutTrustedBindingAsync()
    {
        var workspaceInstanceId = $"wp11-bind-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var core = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        try
        {
            using var client = await ConnectRuntimeAsync(workspaceInstanceId, testTimeout.Token);
            _ = await HelloAsync(client, workspaceInstanceId, bootstrapToken, testTimeout.Token);
            var create = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.CreateRunSession,
                workspaceInstanceId,
                new CreateRunSessionRequest("run-1", DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeMilliseconds()));
            await WriteAsync(client, create, IpcJsonContext.Default.CreateRunSessionRequestEnvelope, testTimeout.Token);
            var error = await ReadAsync(client, IpcJsonContext.Default.ErrorEnvelope, testTimeout.Token);
            AssertEqual(IpcErrorCodes.TrustedBindingUnavailable, error.Payload.Code, "Production Core must fail closed without a trusted launch record.");

            var search = IpcEnvelopeFactory.Create(
                IpcMessageType.Request,
                IpcSemanticTypes.SearchNarrative,
                workspaceInstanceId,
                new SearchNarrativeRequest("q", 1, new RunSessionProof("run-1", "stolen")));
            await WriteAsync(client, search, IpcJsonContext.Default.SearchNarrativeRequestEnvelope, testTimeout.Token);
            var invalid = await ReadAsync(client, IpcJsonContext.Default.ErrorEnvelope, testTimeout.Token);
            AssertEqual(IpcErrorCodes.InvalidSession, invalid.Payload.Code, "Agent commands require a Core-issued RunSession.");
        }
        finally
        {
            StopCore(core);
        }
    }

    private static async Task ProductionReconnectClientRestoresSnapshotAndNewEpochAsync()
    {
        var workspaceInstanceId = $"wp11-reconnect-{Guid.NewGuid():N}";
        var bootstrapToken = IpcBootstrapToken.Create();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var firstCore = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
        Process? restarted = null;
        var recovery = new LLMW.Writing.Application.Ipc.IpcTransportRecovery();
        var reconnect = new LLMW.Writing.Application.Ipc.IpcReconnectClient(
            async cancellationToken =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = new NamedPipeClientStream(
                        ".",
                        IpcPipeNames.Runtime(workspaceInstanceId),
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try
                    {
                        await client.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                        return client;
                    }
                    catch (Exception exception) when (
                        exception is IOException or TimeoutException ||
                        (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        await client.DisposeAsync();
                        await Task.Delay(50, cancellationToken);
                    }
                }

                throw new OperationCanceledException(cancellationToken);
            },
            workspaceInstanceId,
            bootstrapToken,
            IpcClientKind.AgentRuntime,
            TimeSpan.FromMilliseconds(200),
            recovery);
        var run = Task.Run(() => reconnect.RunAsync(testTimeout.Token), testTimeout.Token);
        try
        {
            await WaitUntilAsync(
                () => recovery.RestoreCount >= 1 && !string.IsNullOrWhiteSpace(recovery.LastEventStreamId),
                "Production IpcReconnectClient must GetStateSnapshot and SubscribeEvents after Hello.",
                testTimeout.Token);
            AssertEqual(0L, recovery.LastKnownSeq, "First Core snapshot watermark must be 0.");
            var firstEpoch = recovery.LastEventStreamId!;
            var restores = recovery.RestoreCount;
            AssertTrue(
                !IpcSemanticTypes.IsSafeToReplayAfterReconnect(IpcSemanticTypes.SearchNarrative),
                "Reconnect must not treat business mutations as safe automatic recovery.");

            StopCore(firstCore);
            restarted = StartCore(workspaceInstanceId, IpcBootstrapToken.Create(), bootstrapToken);
            await WaitUntilAsync(
                () => recovery.RestoreCount > restores &&
                      !string.IsNullOrWhiteSpace(recovery.LastEventStreamId) &&
                      !StringComparer.Ordinal.Equals(recovery.LastEventStreamId, firstEpoch),
                "Core restart must force IpcReconnectClient to restore against a new event-stream epoch.",
                testTimeout.Token);
            AssertEqual(0L, recovery.LastKnownSeq, "A new Core epoch must not compare previous seq values as continuity.");
        }
        finally
        {
            testTimeout.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            StopCore(firstCore);
            if (restarted is not null)
            {
                StopCore(restarted);
                restarted.Dispose();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new InvalidOperationException(message);
    }

    private static async Task<NamedPipeClientStream> ConnectRuntimeAsync(string workspaceInstanceId, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".",
            IpcPipeNames.Runtime(workspaceInstanceId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(cancellationToken).WaitAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<IpcEnvelope<HelloAck>> HelloAsync(
        Stream client,
        string workspaceInstanceId,
        string bootstrapToken,
        CancellationToken cancellationToken)
    {
        var hello = new HelloRequest(1, 1, bootstrapToken, IpcClientKind.AgentRuntime, Guid.NewGuid());
        await WriteAsync(
            client,
            IpcEnvelopeFactory.Create(IpcMessageType.Control, IpcSemanticTypes.Hello, workspaceInstanceId, hello),
            IpcJsonContext.Default.HelloRequestEnvelope,
            cancellationToken);
        return await ReadAsync(client, IpcJsonContext.Default.HelloAckEnvelope, cancellationToken);
    }
}
