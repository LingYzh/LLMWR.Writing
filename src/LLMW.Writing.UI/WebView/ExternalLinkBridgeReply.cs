namespace LLMW.Writing.UI.WebView;

internal static class ExternalLinkBridgeReply
{
    public static string Busy(string documentSessionId, string requestMessageId)
        => BridgeOutboundJson.BridgeError(
            documentSessionId,
            Guid.NewGuid().ToString("D"),
            requestMessageId,
            new BridgeError(BridgeErrorCodes.ExternalLinkBusy, "An external link confirmation is already pending."));

    public static string FromLaunch(PendingExternalLink request, ExternalLinkLaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        return result switch
        {
            ExternalLinkLaunchResult.Launched => BridgeOutboundJson.BridgeAck(
                request.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                request.RequestMessageId,
                accepted: true),
            ExternalLinkLaunchResult.Cancelled => BridgeOutboundJson.BridgeAck(
                request.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                request.RequestMessageId,
                accepted: false),
            ExternalLinkLaunchResult.StaleSession => BridgeOutboundJson.BridgeError(
                request.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                request.RequestMessageId,
                new BridgeError(BridgeErrorCodes.StaleSession, "The document session is no longer current.")),
            ExternalLinkLaunchResult.SourceChanged => BridgeOutboundJson.BridgeError(
                request.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                request.RequestMessageId,
                new BridgeError(BridgeErrorCodes.StaleSession, "The application document is no longer current.")),
            _ => BridgeOutboundJson.BridgeError(
                request.DocumentSessionId,
                Guid.NewGuid().ToString("D"),
                request.RequestMessageId,
                new BridgeError(BridgeErrorCodes.ExternalUrlDenied, "The external link was not opened."))
        };
    }
}
