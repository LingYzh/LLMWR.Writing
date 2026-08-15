using System.Threading.Channels;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public enum IpcOutboundClass
{
    Critical,
    Snapshot
}

public sealed record IpcOutboundFrame(IpcOutboundClass Class, byte[] Utf8Json, string SemanticType);

/// <summary>
/// Bounded traffic-class queues feeding a single serialized writer.
/// Critical/snapshot saturation fails closed rather than silently dropping.
/// </summary>
public sealed class IpcOutboundScheduler : IAsyncDisposable
{
    private readonly Channel<IpcOutboundFrame> critical = Channel.CreateBounded<IpcOutboundFrame>(
        new BoundedChannelOptions(IpcProtocol.CriticalOutboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly Channel<IpcOutboundFrame> snapshot = Channel.CreateBounded<IpcOutboundFrame>(
        new BoundedChannelOptions(IpcProtocol.SnapshotOutboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly SemaphoreSlim eventPulse = new(0, 1);
    private readonly CancellationTokenSource writerLifetime = new();
    private Task? writer;
    private int disposed;

    public bool TryEnqueueCritical(byte[] utf8Json, string semanticType) =>
        critical.Writer.TryWrite(new IpcOutboundFrame(IpcOutboundClass.Critical, utf8Json, semanticType));

    public bool TryEnqueueSnapshot(byte[] utf8Json, string semanticType) =>
        snapshot.Writer.TryWrite(new IpcOutboundFrame(IpcOutboundClass.Snapshot, utf8Json, semanticType));

    public void PulseEvents()
    {
        if (eventPulse.CurrentCount == 0)
        {
            try
            {
                eventPulse.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public void Start(Stream stream, Func<byte[]?> tryPullEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tryPullEvent);
        writer = RunAsync(stream, tryPullEvent, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        writerLifetime.Cancel();
        critical.Writer.TryComplete();
        snapshot.Writer.TryComplete();
        PulseEvents();
        if (writer is not null)
        {
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }

        writerLifetime.Dispose();
        eventPulse.Dispose();
    }

    private async Task RunAsync(Stream stream, Func<byte[]?> tryPullEvent, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writerLifetime.Token);
        var token = linked.Token;
        try
        {
            while (true)
            {
                var wrote = false;
                while (critical.Reader.TryRead(out var criticalFrame))
                {
                    await IpcFrameIO.WriteAsync(stream, criticalFrame.Utf8Json, CancellationToken.None).ConfigureAwait(false);
                    wrote = true;
                }

                while (snapshot.Reader.TryRead(out var snapshotFrame))
                {
                    await IpcFrameIO.WriteAsync(stream, snapshotFrame.Utf8Json, CancellationToken.None).ConfigureAwait(false);
                    wrote = true;
                }

                if (wrote)
                {
                    continue;
                }

                if (!token.IsCancellationRequested)
                {
                    var eventPayload = tryPullEvent();
                    if (eventPayload is not null)
                    {
                        await IpcFrameIO.WriteAsync(stream, eventPayload, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                var waitCritical = critical.Reader.WaitToReadAsync(token).AsTask();
                var waitSnapshot = snapshot.Reader.WaitToReadAsync(token).AsTask();
                var waitEvent = eventPulse.WaitAsync(token);
                await Task.WhenAny(waitCritical, waitSnapshot, waitEvent).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            while (critical.Reader.TryRead(out var criticalFrame))
            {
                await IpcFrameIO.WriteAsync(stream, criticalFrame.Utf8Json, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
