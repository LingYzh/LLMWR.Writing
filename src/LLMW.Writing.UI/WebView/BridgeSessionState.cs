namespace LLMW.Writing.UI.WebView;

internal enum BridgeSessionPhase
{
    None = 0,
    HelloSent = 1,
    Ready = 2
}

internal sealed class BridgeReplayGuard
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public int Count => _seen.Count;

    public ReplayRecordResult TryRecord(string messageId)
    {
        if (!_seen.Add(messageId))
        {
            return ReplayRecordResult.Duplicate;
        }

        if (_seen.Count > BridgeProtocol.MaximumReplayCache)
        {
            _seen.Remove(messageId);
            return ReplayRecordResult.Overflow;
        }

        return ReplayRecordResult.Accepted;
    }

    public void Reset() => _seen.Clear();
}

internal enum ReplayRecordResult
{
    Accepted = 0,
    Duplicate = 1,
    Overflow = 2
}

internal sealed class BridgeSessionState
{
    private readonly BridgeReplayGuard _replay = new();

    public string? DocumentSessionId { get; private set; }
    public BridgeSessionPhase Phase { get; private set; }
    public bool IsReady => Phase == BridgeSessionPhase.Ready;

    public void Invalidate()
    {
        DocumentSessionId = null;
        Phase = BridgeSessionPhase.None;
        _replay.Reset();
    }

    public string BeginHello()
    {
        Invalidate();
        DocumentSessionId = Guid.NewGuid().ToString("D");
        Phase = BridgeSessionPhase.HelloSent;
        return DocumentSessionId;
    }

    public bool Matches(string documentSessionId)
        => DocumentSessionId is not null
           && string.Equals(DocumentSessionId, documentSessionId, StringComparison.Ordinal);

    public ReplayRecordResult RecordMessageId(string messageId) => _replay.TryRecord(messageId);

    public void MarkReady()
    {
        if (Phase == BridgeSessionPhase.HelloSent)
        {
            Phase = BridgeSessionPhase.Ready;
        }
    }
}

internal sealed class IncomingWebMessage
{
    public required string? Source { get; init; }
    public required string? CurrentDocument { get; init; }
    public required string? Json { get; init; }
    public required int AdditionalObjectCount { get; init; }
}

internal sealed class BridgeProcessResult
{
    public bool Dispatched { get; init; }
    public BridgeError? Error { get; init; }
    public IReadOnlyList<string> OutboundJson { get; init; } = [];
    public ValidatedExternalUri? ExternalUri { get; init; }
    public bool NotifyRenderer => Error is not null && Error.Code != BridgeErrorCodes.WrongOrigin
        && Error.Code != BridgeErrorCodes.AdditionalObjectsDenied;
}
