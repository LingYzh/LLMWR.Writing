using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Application.Provider;

public sealed record ProviderContinuationState(
    string AdapterId,
    string AdapterVersion,
    string OpaqueJson,
    IReadOnlyList<string> NormalizedToolCallIds)
{
    public static ProviderContinuationState Empty(string adapterId, string adapterVersion) =>
        new(adapterId, adapterVersion, "{\"turns\":[]}", []);

    public static ProviderContinuationState AppendLegacyTurns(
        string adapterId,
        string adapterVersion,
        ProviderContinuationState? prior,
        IReadOnlyList<LocalToolExecutionResult> toolResults)
    {
        var turns = new List<JsonElement>();
        if (prior is not null && TryReadTurns(prior.OpaqueJson, out var existing))
        {
            turns.AddRange(existing);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("turns");
            writer.WriteStartArray();
            foreach (var turn in turns)
            {
                turn.WriteTo(writer);
            }

            if (toolResults.Count > 0)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("results");
                writer.WriteStartArray();
                foreach (var result in toolResults)
                {
                    writer.WriteStartObject();
                    writer.WriteString("callId", result.CallId);
                    writer.WriteString("toolName", result.ToolName);
                    writer.WriteString("output", result.ResultJson);
                    if (!string.IsNullOrEmpty(result.ErrorCode))
                    {
                        writer.WriteString("errorCode", result.ErrorCode);
                        writer.WriteBoolean("isError", true);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var ids = (prior?.NormalizedToolCallIds ?? []).Concat(toolResults.Select(item => item.CallId)).Distinct(StringComparer.Ordinal).ToArray();
        return new ProviderContinuationState(adapterId, adapterVersion, Encoding.UTF8.GetString(stream.ToArray()), ids);
    }

    public static bool TryReadTurns(string opaqueJson, out List<JsonElement> turns)
    {
        turns = [];
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(opaqueJson) ? "{\"turns\":[]}" : opaqueJson);
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
