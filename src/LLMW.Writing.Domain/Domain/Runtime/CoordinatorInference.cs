namespace LLMW.Writing.Domain.Runtime;

public static class CoordinatorInference
{
    public const string ToolLoopLimit = "ToolLoopLimit";
    public const string DigestPrefix = "coordinatorInference:";

    public static string DigestTag(string kind) => DigestPrefix + kind;

    public static bool Has(CheckpointV1 checkpoint, string kind)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var tag = DigestTag(kind);
        if (checkpoint.InputDigestSet.Any(item => string.Equals(item, tag, StringComparison.Ordinal)))
        {
            return true;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(checkpoint.AgentStateJson) ? "{}" : checkpoint.AgentStateJson);
            return document.RootElement.TryGetProperty("coordinatorInference", out var inference) &&
                   inference.TryGetProperty("kind", out var raw) &&
                   string.Equals(raw.GetString(), kind, StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
