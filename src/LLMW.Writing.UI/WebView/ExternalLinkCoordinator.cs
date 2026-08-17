namespace LLMW.Writing.UI.WebView;

internal enum ExternalLinkSource
{
    BridgeRequest = 0,
    UserInitiatedNavigation = 1
}

internal sealed class PendingExternalLink
{
    public required ExternalLinkSource Source { get; init; }
    public required string DocumentSessionId { get; init; }
    public required string RequestMessageId { get; init; }
    public required ValidatedExternalUri Uri { get; init; }
}

internal enum ExternalLinkAdmitResult
{
    Admitted = 0,
    Busy = 1
}

internal enum ExternalLinkLaunchResult
{
    Launched = 0,
    Cancelled = 1,
    StaleSession = 2,
    SourceChanged = 3
}

internal sealed class ExternalLinkCoordinator
{
    private readonly object _gate = new();
    private PendingExternalLink? _pending;

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    public ExternalLinkAdmitResult TryAdmit(PendingExternalLink request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_pending is not null)
            {
                return ExternalLinkAdmitResult.Busy;
            }

            _pending = request;
            return ExternalLinkAdmitResult.Admitted;
        }
    }

    public ExternalLinkLaunchResult Complete(
        PendingExternalLink request,
        bool userAccepted,
        string? currentSessionId,
        bool sessionReady,
        bool currentSourceIsApplicationDocument,
        IExternalBrowserLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(launcher);

        lock (_gate)
        {
            if (_pending is null || _pending.RequestMessageId != request.RequestMessageId)
            {
                return ExternalLinkLaunchResult.Cancelled;
            }

            _pending = null;
        }

        if (!userAccepted)
        {
            return ExternalLinkLaunchResult.Cancelled;
        }

        if (!currentSourceIsApplicationDocument)
        {
            return ExternalLinkLaunchResult.SourceChanged;
        }

        if (request.Source == ExternalLinkSource.BridgeRequest)
        {
            if (!sessionReady
                || !string.Equals(currentSessionId, request.DocumentSessionId, StringComparison.Ordinal))
            {
                return ExternalLinkLaunchResult.StaleSession;
            }
        }

        launcher.Open(request.Uri);
        return ExternalLinkLaunchResult.Launched;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending = null;
        }
    }
}
