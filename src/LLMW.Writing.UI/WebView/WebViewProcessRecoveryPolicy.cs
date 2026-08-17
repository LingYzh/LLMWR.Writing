namespace LLMW.Writing.UI.WebView;

internal enum WebViewProcessFailedKind
{
    BrowserProcessExited = 0,
    RenderProcessExited = 1,
    RenderProcessUnresponsive = 2,
    FrameRenderProcessExited = 3,
    UtilityProcessExited = 4,
    SandboxHelperProcessExited = 5,
    GpuProcessExited = 6,
    PpapiPluginProcessExited = 7,
    PpapiBrokerProcessExited = 8,
    UnknownProcessExited = 9
}

internal enum WebViewProcessRecoveryAction
{
    RecreateControl = 0,
    ReloadApplicationDocument = 1,
    FailClosedNoNavigate = 2,
    ObserveKeepSession = 3
}

internal static class WebViewProcessRecoveryPolicy
{
    public static WebViewProcessRecoveryAction Evaluate(WebViewProcessFailedKind kind, int unresponsiveRecoveryCount)
    {
        return kind switch
        {
            WebViewProcessFailedKind.BrowserProcessExited => WebViewProcessRecoveryAction.RecreateControl,
            WebViewProcessFailedKind.RenderProcessExited => WebViewProcessRecoveryAction.ReloadApplicationDocument,
            WebViewProcessFailedKind.RenderProcessUnresponsive => unresponsiveRecoveryCount >= 1
                ? WebViewProcessRecoveryAction.FailClosedNoNavigate
                : WebViewProcessRecoveryAction.ReloadApplicationDocument,
            WebViewProcessFailedKind.FrameRenderProcessExited => WebViewProcessRecoveryAction.FailClosedNoNavigate,
            WebViewProcessFailedKind.UtilityProcessExited => WebViewProcessRecoveryAction.ObserveKeepSession,
            WebViewProcessFailedKind.SandboxHelperProcessExited => WebViewProcessRecoveryAction.ObserveKeepSession,
            WebViewProcessFailedKind.GpuProcessExited => WebViewProcessRecoveryAction.ObserveKeepSession,
            WebViewProcessFailedKind.PpapiPluginProcessExited => WebViewProcessRecoveryAction.ObserveKeepSession,
            WebViewProcessFailedKind.PpapiBrokerProcessExited => WebViewProcessRecoveryAction.ObserveKeepSession,
            WebViewProcessFailedKind.UnknownProcessExited => WebViewProcessRecoveryAction.ObserveKeepSession,
            _ => WebViewProcessRecoveryAction.ObserveKeepSession
        };
    }

    public static bool LosesRendererDocument(WebViewProcessFailedKind kind)
        => kind is WebViewProcessFailedKind.BrowserProcessExited
            or WebViewProcessFailedKind.RenderProcessExited
            or WebViewProcessFailedKind.RenderProcessUnresponsive;
}
