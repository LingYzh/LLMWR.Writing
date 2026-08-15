using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public sealed record IpcRetainedEvent(long Seq, CoreNoticeEvent Notice);

/// <summary>
/// In-memory Core event ring. Capacity is exactly 256. This is not an Authority log.
/// </summary>
public sealed class IpcEventRing
{
    private readonly object gate = new();
    private readonly IpcRetainedEvent?[] slots = new IpcRetainedEvent?[IpcProtocol.SubscriberRingCapacity];
    private long headSeq;
    private long tailSeq = IpcProtocol.FirstEventSequence;

    public IpcEventRing(string eventStreamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventStreamId);
        EventStreamId = eventStreamId;
    }

    public string EventStreamId { get; }

    public event Action? Published;

    public long HeadSeq
    {
        get
        {
            lock (gate)
            {
                return headSeq;
            }
        }
    }

    public CoreNoticeEvent PublishNotice(string name, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (gate)
        {
            var seq = ++headSeq;
            if (seq - tailSeq + 1 > IpcProtocol.SubscriberRingCapacity)
            {
                tailSeq = seq - IpcProtocol.SubscriberRingCapacity + 1;
            }

            var notice = new CoreNoticeEvent(EventStreamId, seq, name, detail);
            slots[IndexOf(seq)] = new IpcRetainedEvent(seq, notice);
            Published?.Invoke();
            return notice;
        }
    }

    public bool TryGet(long seq, out IpcRetainedEvent retained)
    {
        lock (gate)
        {
            if (seq < tailSeq || seq > headSeq)
            {
                retained = null!;
                return false;
            }

            retained = slots[IndexOf(seq)] ?? throw new InvalidOperationException("Event ring slot was empty for a retained sequence.");
            return true;
        }
    }

    public bool TryDescribeGap(long lastDeliveredSeq, out long fromSeq, out long toSeq)
    {
        lock (gate)
        {
            var next = lastDeliveredSeq + 1;
            if (headSeq == 0 || next > headSeq)
            {
                fromSeq = 0;
                toSeq = 0;
                return false;
            }

            if (next >= tailSeq)
            {
                fromSeq = 0;
                toSeq = 0;
                return false;
            }

            fromSeq = next;
            toSeq = tailSeq - 1;
            return fromSeq <= toSeq;
        }
    }

    private static int IndexOf(long seq) => (int)((seq - 1) % IpcProtocol.SubscriberRingCapacity);
}
