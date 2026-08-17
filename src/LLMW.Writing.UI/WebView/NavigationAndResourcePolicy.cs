namespace LLMW.Writing.UI.WebView;

internal enum NavigationDecision
{
    AllowApplication = 0,
    Block = 1,
    CancelAndOfferExternal = 2
}

internal static class NavigationPolicy
{
    public static NavigationDecision EvaluateTopLevel(string? uri)
    {
        if (!AppOriginPolicy.TryParseAbsolute(uri, out var parsed))
        {
            return NavigationDecision.Block;
        }

        if (AppOriginPolicy.IsApplicationDocument(parsed))
        {
            return NavigationDecision.AllowApplication;
        }

        if (ExternalUriPolicy.TryValidate(uri, out _, out _))
        {
            return NavigationDecision.CancelAndOfferExternal;
        }

        return NavigationDecision.Block;
    }

    public static NavigationDecision EvaluateFrame(string? uri)
    {
        _ = uri;
        return NavigationDecision.Block;
    }

    public static NavigationDecision EvaluateNewWindow(string? uri)
    {
        if (ExternalUriPolicy.TryValidate(uri, out _, out _))
        {
            return NavigationDecision.CancelAndOfferExternal;
        }

        return NavigationDecision.Block;
    }
}

internal static class WebResourcePolicy
{
    public static bool IsAllowed(string? uri) => AppOriginPolicy.IsApplicationResource(uri);
}
