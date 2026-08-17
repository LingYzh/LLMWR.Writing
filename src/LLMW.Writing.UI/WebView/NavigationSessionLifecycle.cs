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
    public NavigationTrackSnapshot(
        ulong navigationId,
        long startSequence,
        bool hostCancelled,
        bool isAllowedApplicationNavigation)
    {
        NavigationId = navigationId;
        StartSequence = startSequence;
        HostCancelled = hostCancelled;
        IsAllowedApplicationNavigation = isAllowedApplicationNavigation;
    }

    public ulong NavigationId { get; }
    public long StartSequence { get; }
    public bool HostCancelled { get; }
    public bool IsAllowedApplicationNavigation { get; }
    public bool CanReplaceTopLevelDocument => IsAllowedApplicationNavigation && !HostCancelled;
}

internal sealed class NavigationSessionLifecycle
{
    public const int MaximumActiveNavigations = 8;

    private readonly NavigationRecord?[] _active = new NavigationRecord?[MaximumActiveNavigations];
    private long _nextStartSequence;
    private long _latestStartSequence;

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

    public long LatestStartSequence => _latestStartSequence;

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

        var startSequence = ++_nextStartSequence;
        _latestStartSequence = startSequence;
        _active[slot] = new NavigationRecord(
            navigationId,
            startSequence,
            hostCancelled,
            isAllowedApplicationNavigation);
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

        if (record.StartSequence != _latestStartSequence)
        {
            return NavigationCompletionAction.None;
        }

        if (isSuccess
            && currentSourceIsApplicationDocument
            && record.IsAllowedApplicationNavigation
            && !record.HostCancelled)
        {
            return NavigationCompletionAction.BeginNewSession;
        }

        if (record.HostCancelled
            && !isSuccess
            && isOperationCanceled
            && currentSourceIsApplicationDocument)
        {
            return NavigationCompletionAction.BeginNewSession;
        }

        if (!isSuccess && !record.HostCancelled)
        {
            return NavigationCompletionAction.ShowNativeFailure;
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
            record.StartSequence,
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

        _latestStartSequence = 0;
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
        public NavigationRecord(
            ulong navigationId,
            long startSequence,
            bool hostCancelled,
            bool isAllowedApplicationNavigation)
        {
            NavigationId = navigationId;
            StartSequence = startSequence;
            HostCancelled = hostCancelled;
            IsAllowedApplicationNavigation = isAllowedApplicationNavigation;
        }

        public ulong NavigationId { get; }
        public long StartSequence { get; }
        public bool HostCancelled { get; set; }
        public bool IsAllowedApplicationNavigation { get; set; }
    }
}
