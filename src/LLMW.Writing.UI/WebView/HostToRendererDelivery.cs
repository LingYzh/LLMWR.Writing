using System.Text.Json;

namespace LLMW.Writing.UI.WebView;

internal static class HostToRendererDelivery
{
    public static bool ShouldPost(string json, string? liveDocumentSessionId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("semanticType", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var semanticType = typeElement.GetString();
            if (string.Equals(semanticType, BridgeSemanticTypes.HostHello, StringComparison.Ordinal))
            {
                return true;
            }

            if (!document.RootElement.TryGetProperty("documentSessionId", out var sessionElement)
                || sessionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var messageSession = sessionElement.GetString();
            return liveDocumentSessionId is not null
                   && messageSession is not null
                   && string.Equals(messageSession, liveDocumentSessionId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
