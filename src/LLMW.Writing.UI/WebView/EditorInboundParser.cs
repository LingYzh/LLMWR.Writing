using System.Text.Json;

namespace LLMW.Writing.UI.WebView;

internal sealed class EditorInboundMessage
{
    public required string SemanticType { get; init; }
    public string? EditorSessionId { get; init; }
    public string? TransferId { get; init; }
    public string? Text { get; init; }
    public string? Data { get; init; }
    public string? Sha256 { get; init; }
    public string? Action { get; init; }
    public int? Sequence { get; init; }
    public int? ExpectedSequence { get; init; }
    public int? From { get; init; }
    public int? To { get; init; }
    public int? Head { get; init; }
    public int? Index { get; init; }
    public int? Count { get; init; }
    public int? TotalBytes { get; init; }
    public bool? ExplicitSave { get; init; }
}

internal readonly struct EditorInboundParseResult
{
    public EditorInboundParseResult(EditorInboundMessage message)
    {
        Message = message;
        Error = null;
    }

    public EditorInboundParseResult(BridgeError error)
    {
        Message = null;
        Error = error;
    }

    public EditorInboundMessage? Message { get; }
    public BridgeError? Error { get; }
    public bool Success => Message is not null && Error is null;
}

internal static class EditorInboundParser
{
    public static EditorInboundParseResult Parse(string semanticType, JsonElement payload)
    {
        return semanticType switch
        {
            BridgeSemanticTypes.EditorBindAck => ParseBindAck(payload),
            BridgeSemanticTypes.EditorChange => ParseChange(payload),
            BridgeSemanticTypes.EditorShadowResyncBegin => ParseResyncBegin(payload),
            BridgeSemanticTypes.EditorShadowResyncChunk => ParseResyncChunk(payload),
            BridgeSemanticTypes.EditorShadowResyncCommit => ParseIds(payload, semanticType, requireTransfer: true),
            BridgeSemanticTypes.EditorSaveRequest => ParseSave(payload),
            BridgeSemanticTypes.EditorRecoveryResponse => ParseRecovery(payload),
            BridgeSemanticTypes.EditorSelectionChanged => ParseSelection(payload),
            BridgeSemanticTypes.EditorCloseRequest => ParseIds(payload, semanticType, requireTransfer: false),
            _ => Fail(BridgeErrorCodes.UnknownMessageType, "Unknown editor message type.")
        };
    }

    private static EditorInboundParseResult ParseBindAck(JsonElement payload)
    {
        if (!KeysAllowed(payload, ["editorSessionId", "transferId"], out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, required: true, allowEmpty: false, out var session, out error)
            || !TryString(payload, "transferId", BridgeProtocol.MaximumIdChars, required: true, allowEmpty: false, out var transfer, out error))
        {
            return error!.Value;
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = BridgeSemanticTypes.EditorBindAck,
            EditorSessionId = session,
            TransferId = transfer
        });
    }

    private static EditorInboundParseResult ParseChange(JsonElement payload)
    {
        if (!KeysAllowed(payload, ["editorSessionId", "sequence", "expectedSequence", "from", "to", "text"], out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, true, false, out var session, out error)
            || !TryInt(payload, "sequence", out var sequence, out error)
            || !TryInt(payload, "expectedSequence", out var expected, out error)
            || !TryInt(payload, "from", out var from, out error)
            || !TryInt(payload, "to", out var to, out error)
            || !TryString(payload, "text", BridgeProtocol.MaximumEditorInsertChars, true, true, out var text, out error))
        {
            return error!.Value;
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = BridgeSemanticTypes.EditorChange,
            EditorSessionId = session,
            Sequence = sequence,
            ExpectedSequence = expected,
            From = from,
            To = to,
            Text = text
        });
    }

    private static EditorInboundParseResult ParseResyncBegin(JsonElement payload)
    {
        if (!KeysAllowed(payload, ["editorSessionId", "transferId", "totalBytes", "sha256"], out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, true, false, out var session, out error)
            || !TryString(payload, "transferId", BridgeProtocol.MaximumIdChars, true, false, out var transfer, out error)
            || !TryInt(payload, "totalBytes", out var total, out error)
            || !TryString(payload, "sha256", 64, true, false, out var sha, out error))
        {
            return error!.Value;
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = BridgeSemanticTypes.EditorShadowResyncBegin,
            EditorSessionId = session,
            TransferId = transfer,
            TotalBytes = total,
            Sha256 = sha
        });
    }

    private static EditorInboundParseResult ParseResyncChunk(JsonElement payload)
    {
        if (!KeysAllowed(payload, ["editorSessionId", "transferId", "index", "count", "data"], out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, true, false, out var session, out error)
            || !TryString(payload, "transferId", BridgeProtocol.MaximumIdChars, true, false, out var transfer, out error)
            || !TryInt(payload, "index", out var index, out error)
            || !TryInt(payload, "count", out var count, out error)
            || !TryString(payload, "data", BridgeProtocol.MaximumEnvelopeBytes, true, false, out var data, out error))
        {
            return error!.Value;
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = BridgeSemanticTypes.EditorShadowResyncChunk,
            EditorSessionId = session,
            TransferId = transfer,
            Index = index,
            Count = count,
            Data = data
        });
    }

    private static EditorInboundParseResult ParseSave(JsonElement payload)
    {
        if (!KeysAllowed(payload, ["editorSessionId", "explicit"], out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, true, false, out var session, out error)
            || !TryBool(payload, "explicit", out var explicitSave, out error))
        {
            return error!.Value;
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = BridgeSemanticTypes.EditorSaveRequest,
            EditorSessionId = session,
            ExplicitSave = explicitSave
        });
    }

    private static EditorInboundParseResult ParseRecovery(JsonElement payload)
    {
        if (!KeysAllowed(payload, ["editorSessionId", "action"], out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, true, false, out var session, out error)
            || !TryString(payload, "action", 16, true, false, out var action, out error))
        {
            return error!.Value;
        }

        if (action is not "restore" and not "discard")
        {
            return Fail(BridgeErrorCodes.InvalidSchema, "Recovery action is invalid.");
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = BridgeSemanticTypes.EditorRecoveryResponse,
            EditorSessionId = session,
            Action = action
        });
    }

    private static EditorInboundParseResult ParseSelection(JsonElement payload)
    {
        if (!KeysAllowed(payload, ["editorSessionId", "from", "to", "head"], out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, true, false, out var session, out error)
            || !TryInt(payload, "from", out var from, out error)
            || !TryInt(payload, "to", out var to, out error)
            || !TryInt(payload, "head", out var head, out error))
        {
            return error!.Value;
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = BridgeSemanticTypes.EditorSelectionChanged,
            EditorSessionId = session,
            From = from,
            To = to,
            Head = head
        });
    }

    private static EditorInboundParseResult ParseIds(JsonElement payload, string semanticType, bool requireTransfer)
    {
        var allowed = requireTransfer ? new[] { "editorSessionId", "transferId" } : new[] { "editorSessionId" };
        if (!KeysAllowed(payload, allowed, out var error))
        {
            return error!.Value;
        }

        if (!TryString(payload, "editorSessionId", BridgeProtocol.MaximumIdChars, true, false, out var session, out error))
        {
            return error!.Value;
        }

        string? transfer = null;
        if (requireTransfer && !TryString(payload, "transferId", BridgeProtocol.MaximumIdChars, true, false, out transfer, out error))
        {
            return error!.Value;
        }

        return Ok(new EditorInboundMessage
        {
            SemanticType = semanticType,
            EditorSessionId = session,
            TransferId = transfer
        });
    }

    private static bool KeysAllowed(JsonElement payload, string[] allowed, out EditorInboundParseResult? error)
    {
        error = null;
        foreach (var property in payload.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                error = Fail(BridgeErrorCodes.InvalidSchema, "Payload contains unknown fields.");
                return false;
            }
        }

        return true;
    }

    private static bool TryString(
        JsonElement payload,
        string name,
        int maxChars,
        bool required,
        bool allowEmpty,
        out string? value,
        out EditorInboundParseResult? error)
    {
        value = null;
        error = null;
        if (!payload.TryGetProperty(name, out var element))
        {
            if (!required)
            {
                return true;
            }

            error = Fail(BridgeErrorCodes.InvalidSchema, "Required payload field is missing.");
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = Fail(BridgeErrorCodes.InvalidSchema, "Payload field has the wrong type.");
            return false;
        }

        value = element.GetString();
        if (value is null || value.Length > maxChars || (!allowEmpty && value.Length == 0))
        {
            error = Fail(BridgeErrorCodes.InvalidSchema, "Payload field is empty or too large.");
            return false;
        }

        return true;
    }

    private static bool TryInt(JsonElement payload, string name, out int value, out EditorInboundParseResult? error)
    {
        value = 0;
        error = null;
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            error = Fail(BridgeErrorCodes.InvalidSchema, "Payload integer is invalid.");
            return false;
        }

        return true;
    }

    private static bool TryBool(JsonElement payload, string name, out bool value, out EditorInboundParseResult? error)
    {
        value = false;
        error = null;
        if (!payload.TryGetProperty(name, out var element) || (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
        {
            error = Fail(BridgeErrorCodes.InvalidSchema, "Payload boolean is invalid.");
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static EditorInboundParseResult Ok(EditorInboundMessage message) => new(message);

    private static EditorInboundParseResult Fail(string code, string message) => new(new BridgeError(code, message));
}
