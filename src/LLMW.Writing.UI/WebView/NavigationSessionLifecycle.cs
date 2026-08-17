namespace LLMW.Writing.UI.WebView;

internal enum NavigationCompletionAction
{
    None = 0,
    BeginNewSession = 1,
    ShowNativeFailure = 2
}

internal sealed class NavigationSessionLifecycle
{
    private ulong? _hostCancelledNavigationId;

    public ulong? HostCancelledNavigationId => _hostCancelledNavigationId;

    public void NoteStarting(ulong navigationId, bool hostCancelled)
    {
        _hostCancelledNavigationId = hostCancelled ? navigationId : null;
    }

    public NavigationCompletionAction NoteCompleted(
        ulong navigationId,
        bool isSuccess,
        bool isOperationCanceled,
        bool currentSourceIsApplicationDocument)
    {
        var wasHostCancelled = _hostCancelledNavigationId is ulong cancelled && cancelled == navigationId;
        _hostCancelledNavigationId = null;

        if (isSuccess && currentSourceIsApplicationDocument)
        {
            return NavigationCompletionAction.BeginNewSession;
        }

        if (wasHostCancelled
            && !isSuccess
            && isOperationCanceled
            && currentSourceIsApplicationDocument)
        {
            return NavigationCompletionAction.BeginNewSession;
        }

        return NavigationCompletionAction.ShowNativeFailure;
    }
}
