namespace LLMW.Writing.UI.WebView;

internal enum NavigationCompletionAction
{
    None = 0,
    BeginNewSession = 1,
    ShowNativeFailure = 2,
    IgnoreUnknown = 3
}

internal enum NavigationTrackResult
{
    Tracked = 0,
    UpdatedRedirect = 1,
    Overflow = 2
}

internal readonly struct NavigationTrackSnapshot
{
    public NavigationTrackSnapshot(ulong navigationId, bool hostCancelled, bool isAllowedApplicationNavigation)
    {
        NavigationId = navigationId;
        HostCancelled = hostCancelled;
        IsAllowedApplicationNavigation = isAllowedApplicationNavigation;
    }

    public ulong NavigationId { get; }
    public bool HostCancelled { get; }
    public bool IsAllowedApplicationNavigation { get; }
}

internal sealed class NavigationSessionLifecycle
{
    public const int MaximumActiveNavigations = 8;

    private readonly NavigationRecord?[] _active = new NavigationRecord?[MaximumActiveNavigations];
    private bool _applicationHandshakeIssued;

    public int ActiveCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _active.Length; i++)
            {
                if (_active[i] is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public NavigationTrackResult NoteStarting(
        ulong navigationId,
        bool hostCancelled,
        bool isAllowedApplicationNavigation)
    {
        var existingIndex = IndexOf(navigationId);
        if (existingIndex >= 0)
        {
            var existing = _active[existingIndex]!;
            if (hostCancelled)
            {
                existing.HostCancelled = true;
            }

            if (!isAllowedApplicationNavigation)
            {
                existing.IsAllowedApplicationNavigation = false;
            }

            return NavigationTrackResult.UpdatedRedirect;
        }

        var slot = FirstEmptySlot();
        if (slot < 0)
        {
            return NavigationTrackResult.Overflow;
        }

        _active[slot] = new NavigationRecord(navigationId, hostCancelled, isAllowedApplicationNavigation);
        return NavigationTrackResult.Tracked;
    }

    public NavigationCompletionAction NoteCompleted(
        ulong navigationId,
        bool isSuccess,
        bool isOperationCanceled,
        bool currentSourceIsApplicationDocument)
    {
        var index = IndexOf(navigationId);
        if (index < 0)
        {
            return NavigationCompletionAction.IgnoreUnknown;
        }

        var record = _active[index]!;
        _active[index] = null;
        var othersRemain = ActiveCount > 0;

        if (isSuccess
            && currentSourceIsApplicationDocument
            && record.IsAllowedApplicationNavigation
            && !record.HostCancelled)
        {
            _applicationHandshakeIssued = true;
            if (!othersRemain)
            {
                _applicationHandshakeIssued = false;
            }

            return NavigationCompletionAction.BeginNewSession;
        }

        if (record.HostCancelled
            && !isSuccess
            && isOperationCanceled
            && currentSourceIsApplicationDocument)
        {
            if (othersRemain || _applicationHandshakeIssued)
            {
                if (!othersRemain)
                {
                    _applicationHandshakeIssued = false;
                }

                return NavigationCompletionAction.None;
            }

            return NavigationCompletionAction.BeginNewSession;
        }

        if (!isSuccess && !record.HostCancelled)
        {
            if (othersRemain)
            {
                return NavigationCompletionAction.None;
            }

            _applicationHandshakeIssued = false;
            return NavigationCompletionAction.ShowNativeFailure;
        }

        if (!othersRemain)
        {
            _applicationHandshakeIssued = false;
        }

        return NavigationCompletionAction.None;
    }

    public bool TryGet(ulong navigationId, out NavigationTrackSnapshot snapshot)
    {
        var index = IndexOf(navigationId);
        if (index < 0)
        {
            snapshot = default;
            return false;
        }

        var record = _active[index]!;
        snapshot = new NavigationTrackSnapshot(
            record.NavigationId,
            record.HostCancelled,
            record.IsAllowedApplicationNavigation);
        return true;
    }

    public void Reset()
    {
        for (var i = 0; i < _active.Length; i++)
        {
            _active[i] = null;
        }

        _applicationHandshakeIssued = false;
    }

    private int IndexOf(ulong navigationId)
    {
        for (var i = 0; i < _active.Length; i++)
        {
            if (_active[i] is NavigationRecord record && record.NavigationId == navigationId)
            {
                return i;
            }
        }

        return -1;
    }

    private int FirstEmptySlot()
    {
        for (var i = 0; i < _active.Length; i++)
        {
            if (_active[i] is null)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class NavigationRecord
    {
        public NavigationRecord(ulong navigationId, bool hostCancelled, bool isAllowedApplicationNavigation)
        {
            NavigationId = navigationId;
            HostCancelled = hostCancelled;
            IsAllowedApplicationNavigation = isAllowedApplicationNavigation;
        }

        public ulong NavigationId { get; }
        public bool HostCancelled { get; set; }
        public bool IsAllowedApplicationNavigation { get; set; }
    }
}
