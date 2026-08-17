using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LLMW.Writing.UI.WebView;

internal sealed class ParsedBridgeMessage
{
    public required string Protocol { get; init; }
    public required int Version { get; init; }
    public required string DocumentSessionId { get; init; }
    public required string MessageId { get; init; }
    public required string SemanticType { get; init; }
    public string? ReplyTo { get; init; }
    public string? Shell { get; init; }
    public string? Nonce { get; init; }
    public string? ExternalUri { get; init; }
}

internal readonly struct BridgeParseResult
{
    public BridgeParseResult(ParsedBridgeMessage message)
    {
        Message = message;
        Error = null;
    }

    public BridgeParseResult(BridgeError error)
    {
        Message = null;
        Error = error;
    }

    public ParsedBridgeMessage? Message { get; }
    public BridgeError? Error { get; }
    public bool Success => Message is not null && Error is null;

    public static BridgeParseResult Fail(string code, string safeMessage)
        => new(new BridgeError(code, safeMessage));
}

internal static class BridgeEnvelopeParser
{
    private static readonly HashSet<string> EnvelopeKeys = new(StringComparer.Ordinal)
    {
        "protocol",
        "version",
        "documentSessionId",
        "messageId",
        "semanticType",
        "replyTo",
        "payload"
    };

    private static readonly HashSet<string> DangerousEnvelopeKeys = new(StringComparer.Ordinal)
    {
        "method",
        "args",
        "$type",
        "$id",
        "__type"
    };

    public static BridgeParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return BridgeParseResult.Fail(BridgeErrorCodes.MalformedJson, "Message is empty.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > BridgeProtocol.MaximumEnvelopeBytes)
        {
            return BridgeParseResult.Fail(BridgeErrorCodes.MessageTooLarge, "Message exceeds the bridge size limit.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = BridgeProtocol.MaximumJsonDepth
            });
        }
        catch (JsonException exception)
        {
            if (IsDepthExceeded(exception))
            {
                return BridgeParseResult.Fail(BridgeErrorCodes.JsonTooDeep, "JSON nesting exceeds the bridge limit.");
            }

            return BridgeParseResult.Fail(BridgeErrorCodes.MalformedJson, "Message is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return BridgeParseResult.Fail(BridgeErrorCodes.MalformedJson, "Bridge envelope must be a JSON object.");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (DangerousEnvelopeKeys.Contains(property.Name))
                {
                    return BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Generic proxy envelopes are not accepted.");
                }

                if (!EnvelopeKeys.Contains(property.Name))
                {
                    return BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Envelope contains unknown fields.");
                }
            }

            if (!TryGetExactString(root, "protocol", BridgeProtocol.MaximumMetadataChars, required: true, out var protocol, out var protocolError))
            {
                return protocolError!.Value;
            }

            if (!string.Equals(protocol, BridgeProtocol.Name, StringComparison.Ordinal))
            {
                return BridgeParseResult.Fail(
                    BridgeErrorCodes.ProtocolUnsupported,
                    "Bridge protocol or version is not supported.");
            }

            if (!TryGetExactInt32(root, "version", out var version, out var versionError))
            {
                return versionError!.Value;
            }

            if (version != BridgeProtocol.Version)
            {
                return BridgeParseResult.Fail(
                    BridgeErrorCodes.ProtocolUnsupported,
                    "Bridge protocol version is not supported.");
            }

            if (!TryGetExactString(root, "documentSessionId", BridgeProtocol.MaximumIdChars, required: true, out var sessionId, out var sessionError))
            {
                return sessionError!.Value;
            }

            if (!TryGetExactString(root, "messageId", BridgeProtocol.MaximumIdChars, required: true, out var messageId, out var messageIdError))
            {
                return messageIdError!.Value;
            }

            if (!TryGetExactString(root, "semanticType", BridgeProtocol.MaximumSemanticTypeChars, required: true, out var semanticType, out var typeError))
            {
                return typeError!.Value;
            }

            string? replyTo = null;
            if (root.TryGetProperty("replyTo", out _))
            {
                if (!TryGetExactString(root, "replyTo", BridgeProtocol.MaximumIdChars, required: true, out replyTo, out var replyError))
                {
                    return replyError!.Value;
                }
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Payload must be a JSON object.");
            }

            if (!BridgeSemanticTypes.IsInbound(semanticType!))
            {
                return BridgeParseResult.Fail(BridgeErrorCodes.UnknownMessageType, "Unknown bridge message type.");
            }

            string? shell = null;
            string? nonce = null;
            string? externalUri = null;
            switch (semanticType)
            {
                case BridgeSemanticTypes.RendererReady:
                    if (!TryReadOptionalString(payload, "shell", BridgeProtocol.MaximumMetadataChars, out shell, out var shellError, allowedKeys: ["shell"]))
                    {
                        return shellError!.Value;
                    }

                    break;
                case BridgeSemanticTypes.BridgePing:
                    if (!TryReadOptionalString(payload, "nonce", BridgeProtocol.MaximumMetadataChars, out nonce, out var nonceError, allowedKeys: ["nonce"]))
                    {
                        return nonceError!.Value;
                    }

                    break;
                case BridgeSemanticTypes.ExternalLinkRequest:
                    if (!TryReadRequiredString(payload, "uri", ExternalUriPolicy.MaximumUriChars, out externalUri, out var uriError, allowedKeys: ["uri"]))
                    {
                        return uriError!.Value;
                    }

                    break;
            }

            return new BridgeParseResult(new ParsedBridgeMessage
            {
                Protocol = protocol!,
                Version = version,
                DocumentSessionId = sessionId!,
                MessageId = messageId!,
                SemanticType = semanticType!,
                ReplyTo = replyTo,
                Shell = shell,
                Nonce = nonce,
                ExternalUri = externalUri
            });
        }
    }

    private static bool IsDepthExceeded(JsonException exception)
        => exception.Message.Contains("maximum configured depth", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("MaxDepth", StringComparison.Ordinal);

    private static bool TryGetExactInt32(JsonElement root, string name, out int value, out BridgeParseResult? error)
    {
        value = 0;
        error = null;
        if (!root.TryGetProperty(name, out var element))
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Required envelope field is missing.");
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.ProtocolUnsupported, "Bridge version is not supported.");
            return false;
        }

        var raw = element.GetRawText();
        if (!string.Equals(raw, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.ProtocolUnsupported, "Bridge version is not supported.");
            return false;
        }

        return true;
    }

    private static bool TryGetExactString(
        JsonElement root,
        string name,
        int maxChars,
        bool required,
        out string? value,
        out BridgeParseResult? error)
    {
        value = null;
        error = null;
        if (!root.TryGetProperty(name, out var element))
        {
            if (!required)
            {
                return true;
            }

            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Required envelope field is missing.");
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Envelope field has the wrong type.");
            return false;
        }

        value = element.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maxChars)
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Envelope field is empty or too large.");
            return false;
        }

        return true;
    }

    private static bool TryReadOptionalString(
        JsonElement payload,
        string name,
        int maxChars,
        out string? value,
        out BridgeParseResult? error,
        string[] allowedKeys)
    {
        value = null;
        if (!PayloadKeysAllowed(payload, allowedKeys, out error))
        {
            return false;
        }

        if (!payload.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Payload field has the wrong type.");
            return false;
        }

        value = element.GetString();
        if (value is null || value.Length > maxChars)
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Payload field is too large.");
            return false;
        }

        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement payload,
        string name,
        int maxChars,
        out string? value,
        out BridgeParseResult? error,
        string[] allowedKeys)
    {
        value = null;
        if (!PayloadKeysAllowed(payload, allowedKeys, out error))
        {
            return false;
        }

        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Required payload field is missing.");
            return false;
        }

        value = element.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maxChars)
        {
            error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Payload field is empty or too large.");
            return false;
        }

        return true;
    }

    private static bool PayloadKeysAllowed(JsonElement payload, string[] allowedKeys, out BridgeParseResult? error)
    {
        error = null;
        foreach (var property in payload.EnumerateObject())
        {
            if (Array.IndexOf(allowedKeys, property.Name) < 0)
            {
                error = BridgeParseResult.Fail(BridgeErrorCodes.InvalidSchema, "Payload contains unknown fields.");
                return false;
            }
        }

        return true;
    }
}
