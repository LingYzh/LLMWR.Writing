using System.Text;
using System.Text.Json;
using LLMW.Writing.Application.Provider;

namespace LLMW.Writing.Infrastructure.Providers;

internal static class AdapterContinuation
{
    public static ProviderContinuationState Append(
        string adapterId,
        string adapterVersion,
        ProviderContinuationState? prior,
        Action<Utf8JsonWriter> writeTurn,
        IEnumerable<string> newIds)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("turns");
            writer.WriteStartArray();
            if (prior is not null && TryTurns(prior.OpaqueJson, out var existing))
            {
                foreach (var turn in existing)
                {
                    turn.WriteTo(writer);
                }
            }

            writer.WriteStartObject();
            writeTurn(writer);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var ids = (prior?.NormalizedToolCallIds ?? []).Concat(newIds).Distinct(StringComparer.Ordinal).ToArray();
        return new ProviderContinuationState(adapterId, adapterVersion, Encoding.UTF8.GetString(stream.ToArray()), ids);
    }

    public static void WriteResults(
        Utf8JsonWriter writer,
        IReadOnlyList<LocalToolExecutionResult> toolResults,
        string idName,
        string contentName)
    {
        writer.WritePropertyName("results");
        writer.WriteStartArray();
        foreach (var result in toolResults)
        {
            writer.WriteStartObject();
            writer.WriteString(idName, result.CallId);
            writer.WriteString(contentName, result.ResultJson);
            if (!string.IsNullOrEmpty(result.ErrorCode))
            {
                writer.WriteBoolean("is_error", true);
                writer.WriteString("errorCode", result.ErrorCode);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static bool HasTurns(string? opaqueJson)
    {
        return TryTurns(opaqueJson, out var turns) && turns.Count > 0;
    }

    public static bool HasItemType(string opaqueJson, string arrayName, string type)
    {
        if (!TryTurns(opaqueJson, out var turns))
        {
            return false;
        }

        foreach (var turn in turns)
        {
            if (!turn.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typed) &&
                    string.Equals(typed.GetString(), type, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasMessageProperty(string opaqueJson, string name)
    {
        if (!TryTurns(opaqueJson, out var turns))
        {
            return false;
        }

        foreach (var turn in turns)
        {
            if (turn.TryGetProperty("message", out var message) &&
                message.TryGetProperty(name, out var property) &&
                property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryTurns(string? opaqueJson, out List<JsonElement> turns)
    {
        turns = [];
        if (string.IsNullOrWhiteSpace(opaqueJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(opaqueJson);
            if (!document.RootElement.TryGetProperty("turns", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in array.EnumerateArray())
            {
                turns.Add(item.Clone());
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
