namespace LLMW.Writing.Application.Ipc;

public sealed class IpcInFlightRequest
{
    public IpcInFlightRequest(Guid requestId, Guid correlationId, string semanticType)
    {
        RequestId = requestId;
        CorrelationId = correlationId;
        SemanticType = semanticType;
        Cancellation = new CancellationTokenSource();
    }

    public Guid RequestId { get; }

    public Guid CorrelationId { get; }

    public string SemanticType { get; }

    public CancellationTokenSource Cancellation { get; }

    public bool IsCompleted => Volatile.Read(ref completed) == 1;

    public void MarkCompleted() => Interlocked.Exchange(ref completed, 1);

    private int completed;
}

/// <summary>
/// Race-safe in-flight request table. Capacity is bounded; duplicates are rejected.
/// </summary>
public sealed class IpcInFlightRegistry : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, IpcInFlightRequest> byRequestId = [];
    private readonly Dictionary<Guid, IpcInFlightRequest> byCorrelationId = [];
    private readonly HashSet<Guid> completedCorrelations = [];
    private readonly Queue<Guid> completedOrder = [];
    private const int CompletedCorrelationCapacity = 64;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return byRequestId.Count;
            }
        }
    }

    public bool TryRegister(Guid requestId, Guid correlationId, string semanticType, int maximum, out IpcInFlightRequest request, out string? errorCode)
    {
        lock (gate)
        {
            if (byRequestId.ContainsKey(requestId) || byCorrelationId.ContainsKey(correlationId))
            {
                request = null!;
                errorCode = LLMW.Writing.Contracts.Ipc.IpcErrorCodes.DuplicateRequest;
                return false;
            }

            if (byRequestId.Count >= maximum)
            {
                request = null!;
                errorCode = LLMW.Writing.Contracts.Ipc.IpcErrorCodes.QueueOverload;
                return false;
            }

            request = new IpcInFlightRequest(requestId, correlationId, semanticType);
            byRequestId[requestId] = request;
            byCorrelationId[correlationId] = request;
            errorCode = null;
            return true;
        }
    }

    public bool TryComplete(Guid requestId, out IpcInFlightRequest request)
    {
        lock (gate)
        {
            if (!byRequestId.TryGetValue(requestId, out request!))
            {
                return false;
            }

            request.MarkCompleted();
            byRequestId.Remove(requestId);
            byCorrelationId.Remove(request.CorrelationId);
            RememberCompleted(request.CorrelationId);
            return true;
        }
    }

    public bool WasCompleted(Guid correlationId)
    {
        lock (gate)
        {
            return completedCorrelations.Contains(correlationId);
        }
    }

    public bool TryGetByCorrelation(Guid correlationId, out IpcInFlightRequest request)
    {
        lock (gate)
        {
            return byCorrelationId.TryGetValue(correlationId, out request!);
        }
    }

    public IpcInFlightRequest[] SnapshotAndClear()
    {
        lock (gate)
        {
            var items = byRequestId.Values.ToArray();
            byRequestId.Clear();
            byCorrelationId.Clear();
            completedCorrelations.Clear();
            completedOrder.Clear();
            return items;
        }
    }

    private void RememberCompleted(Guid correlationId)
    {
        if (!completedCorrelations.Add(correlationId))
        {
            return;
        }

        completedOrder.Enqueue(correlationId);
        while (completedOrder.Count > CompletedCorrelationCapacity)
        {
            completedCorrelations.Remove(completedOrder.Dequeue());
        }
    }

    public void Dispose()
    {
        foreach (var item in SnapshotAndClear())
        {
            item.Cancellation.Dispose();
        }
    }
}
