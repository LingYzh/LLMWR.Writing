using System.Threading.Channels;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// Production Runtime transport recovery: snapshot watermark, subscribe, Gap/overflow resync.
/// Does not auto-replay business mutations.
/// </summary>
public sealed class IpcTransportRecovery
{
    private readonly object gate = new();
    private long lastKnownSeq = IpcProtocol.EmptySnapshotSequence;
    private string? lastEventStreamId;
    private bool needsResync = true;
    private int restoreCount;
    private long trustedOrdinaryEventCount;
    private TaskCompletionSource overflowSignal = NewSignal();

    public long LastKnownSeq
    {
        get
        {
            lock (gate)
            {
                return lastKnownSeq;
            }
        }
    }

    public string? LastEventStreamId
    {
        get
        {
            lock (gate)
            {
                return lastEventStreamId;
            }
        }
    }

    public bool NeedsResync
    {
        get
        {
            lock (gate)
            {
                return needsResync;
            }
        }
    }

    public int RestoreCount
    {
        get
        {
            lock (gate)
            {
                return restoreCount;
            }
        }
    }

    public long TrustedOrdinaryEventCount
    {
        get
        {
            lock (gate)
            {
                return trustedOrdinaryEventCount;
            }
        }
    }

    public async Task RestoreAsync(IpcClientSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        long known;
        string? stream;
        lock (gate)
        {
            known = lastKnownSeq;
            stream = lastEventStreamId;
            needsResync = true;
        }

        var snapshot = await session.RequestAsync(
                IpcSemanticTypes.GetStateSnapshot,
                new GetStateSnapshotRequest(known, stream),
                IpcJsonContext.Default.GetStateSnapshotRequestEnvelope,
                IpcJsonContext.Default.GetStateSnapshotResponseEnvelope,
                cancellationToken)
            .ConfigureAwait(false);

        lock (gate)
        {
            lastEventStreamId = snapshot.Payload.EventStreamId;
            lastKnownSeq = snapshot.Payload.SnapshotSeq;
        }

        session.BeginTrustedEventWindow();
        overflowSignal = NewSignal();

        await session.RequestAsync(
                IpcSemanticTypes.SubscribeEvents,
                new SubscribeEventsRequest(snapshot.Payload.EventStreamId, snapshot.Payload.SnapshotSeq),
                IpcJsonContext.Default.SubscribeEventsRequestEnvelope,
                IpcJsonContext.Default.SubscribeEventsResponseEnvelope,
                cancellationToken)
            .ConfigureAwait(false);

        lock (gate)
        {
            needsResync = false;
            restoreCount++;
        }
    }

    public async Task RunSessionAsync(IpcClientSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.LocalEventOverflow += OnLocalOverflow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    overflowSignal = NewSignal();
                    await RestoreAsync(session, cancellationToken).ConfigureAwait(false);
                    var disconnected = await PumpAsync(session, cancellationToken).ConfigureAwait(false);
                    if (disconnected)
                    {
                        return;
                    }
                }
                catch (IpcProtocolException exception) when (exception.ErrorCode == IpcErrorCodes.ResyncRequired)
                {
                    MarkNeedsResync();
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
        finally
        {
            session.LocalEventOverflow -= OnLocalOverflow;
        }
    }

    private async Task<bool> PumpAsync(IpcClientSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (session.HasEventDiscontinuity)
            {
                MarkNeedsResync();
                return false;
            }

            Task<bool> waitRead;
            try
            {
                waitRead = session.Events.WaitToReadAsync(cancellationToken).AsTask();
            }
            catch (ChannelClosedException)
            {
                return true;
            }

            var overflow = overflowSignal.Task;
            var completed = await Task.WhenAny(waitRead, overflow).ConfigureAwait(false);
            if (completed == overflow || session.HasEventDiscontinuity)
            {
                MarkNeedsResync();
                return false;
            }

            bool readable;
            try
            {
                readable = await waitRead.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ChannelClosedException)
            {
                return true;
            }

            if (!readable)
            {
                return true;
            }

            while (session.Events.TryRead(out var wire))
            {
                if (Observe(wire))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool Observe(IpcWireEnvelope wire)
    {
        if (wire.SemanticType == IpcSemanticTypes.Gap)
        {
            MarkNeedsResync();
            return true;
        }

        if (wire.SemanticType != IpcSemanticTypes.CoreNotice)
        {
            return false;
        }

        var notice = IpcJson.DeserializePayload(wire.Payload, IpcJsonContext.Default.CoreNoticeEvent);
        lock (gate)
        {
            if (needsResync)
            {
                return false;
            }

            if (!StringComparer.Ordinal.Equals(notice.EventStreamId, lastEventStreamId))
            {
                needsResync = true;
                return true;
            }

            if (notice.Seq <= lastKnownSeq)
            {
                return false;
            }

            if (notice.Seq > lastKnownSeq + 1)
            {
                needsResync = true;
                return true;
            }

            lastKnownSeq = notice.Seq;
            trustedOrdinaryEventCount++;
            return false;
        }
    }

    private void OnLocalOverflow()
    {
        MarkNeedsResync();
        overflowSignal.TrySetResult();
    }

    private void MarkNeedsResync()
    {
        lock (gate)
        {
            needsResync = true;
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
