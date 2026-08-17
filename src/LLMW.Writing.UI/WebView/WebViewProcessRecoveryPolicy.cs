namespace LLMW.Writing.UI.WebView;

internal enum WebViewProcessFailedKind
{
    BrowserProcessExited = 0,
    RenderProcessExited = 1,
    RenderProcessUnresponsive = 2,
    FrameRenderProcessExited = 3,
    GpuProcessExited = 4,
    UtilityProcessExited = 5,
    SandboxIdleProcessExited = 6,
    Other = 7
}

internal enum WebViewProcessRecoveryAction
{
    RecreateControl = 0,
    ReloadApplicationDocument = 1,
    FailClosedNoNavigate = 2,
    IgnoreAutoRecovered = 3
}

internal static class WebViewProcessRecoveryPolicy
{
    public static WebViewProcessRecoveryAction Evaluate(WebViewProcessFailedKind kind, int unresponsiveRecoveryCount)
    {
        return kind switch
        {
            WebViewProcessFailedKind.BrowserProcessExited => WebViewProcessRecoveryAction.RecreateControl,
            WebViewProcessFailedKind.RenderProcessExited => WebViewProcessRecoveryAction.ReloadApplicationDocument,
            WebViewProcessFailedKind.FrameRenderProcessExited => WebViewProcessRecoveryAction.FailClosedNoNavigate,
            WebViewProcessFailedKind.GpuProcessExited => WebViewProcessRecoveryAction.IgnoreAutoRecovered,
            WebViewProcessFailedKind.UtilityProcessExited => WebViewProcessRecoveryAction.IgnoreAutoRecovered,
            WebViewProcessFailedKind.SandboxIdleProcessExited => WebViewProcessRecoveryAction.IgnoreAutoRecovered,
            WebViewProcessFailedKind.RenderProcessUnresponsive => unresponsiveRecoveryCount >= 1
                ? WebViewProcessRecoveryAction.FailClosedNoNavigate
                : WebViewProcessRecoveryAction.ReloadApplicationDocument,
            _ => WebViewProcessRecoveryAction.FailClosedNoNavigate
        };
    }
}
