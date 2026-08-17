namespace LLMW.Writing.UI.WebView;

internal readonly struct ProcessRecoveryRequest
{
    public ProcessRecoveryRequest(int rendererGeneration, WebViewProcessFailedKind kind, WebViewProcessRecoveryAction action)
    {
        RendererGeneration = rendererGeneration;
        Kind = kind;
        Action = action;
    }

    public int RendererGeneration { get; }
    public WebViewProcessFailedKind Kind { get; }
    public WebViewProcessRecoveryAction Action { get; }
}

internal static class ProcessRecoveryGate
{
    public static bool ShouldApply(int requestGeneration, int currentGeneration)
        => requestGeneration == currentGeneration && requestGeneration > 0;
}
