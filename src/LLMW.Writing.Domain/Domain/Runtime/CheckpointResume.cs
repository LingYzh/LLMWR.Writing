using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Runtime;

public sealed record CheckpointCriticalMessage(int Sequence, string Role, string Text);

public sealed record CheckpointToolReference(string ToolCallId, string ToolName, string Payload);

public sealed record CheckpointV1(
    int SchemaVersion,
    string ApprovedPlanReference,
    string? ApprovedPlanDigest,
    string DagTaskStateJson,
    string AgentStateJson,
    string CompactedSummary,
    IReadOnlyList<CheckpointCriticalMessage> CriticalMessages,
    IReadOnlyList<CheckpointToolReference> ToolReferences,
    IReadOnlyList<string> ApprovalReferences,
    IReadOnlyList<string> ContextPointers,
    IReadOnlyList<string> ArtifactEvidenceReferences,
    IReadOnlyList<string> InputDigestSet,
    string? PromptConfigId,
    string? ProviderId,
    string? ModelId,
    string? EffectivePromptDigest)
{
    public const int CurrentSchemaVersion = 1;
    public const int RetainedCriticalMessageLimit = 20;
    public const int ToolReferenceHeadTailBytes = 256 * 1024;

    public static CheckpointV1 Create(
        string approvedPlanReference,
        string? approvedPlanDigest,
        string dagTaskStateJson,
        string agentStateJson,
        string compactedSummary,
        IReadOnlyList<CheckpointCriticalMessage> criticalMessages,
        IReadOnlyList<CheckpointToolReference> toolReferences,
        IReadOnlyList<string> approvalReferences,
        IReadOnlyList<string> contextPointers,
        IReadOnlyList<string> artifactEvidenceReferences,
        IReadOnlyList<string> inputDigestSet,
        string? promptConfigId,
        string? providerId,
        string? modelId,
        string? effectivePromptDigest) =>
        new(
            CurrentSchemaVersion,
            approvedPlanReference,
            approvedPlanDigest,
            dagTaskStateJson,
            SecretRedaction.RedactObjectJson(agentStateJson),
            compactedSummary,
            RetainLatestMessages(criticalMessages),
            TruncateToolReferences(toolReferences),
            approvalReferences,
            contextPointers,
            artifactEvidenceReferences,
            inputDigestSet.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            promptConfigId,
            providerId,
            modelId,
            effectivePromptDigest);

    public static IReadOnlyList<CheckpointCriticalMessage> RetainLatestMessages(
        IReadOnlyList<CheckpointCriticalMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count <= RetainedCriticalMessageLimit)
        {
            return messages.ToArray();
        }

        return messages
            .OrderBy(message => message.Sequence)
            .TakeLast(RetainedCriticalMessageLimit)
            .ToArray();
    }

    public static IReadOnlyList<CheckpointToolReference> TruncateToolReferences(
        IReadOnlyList<CheckpointToolReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return references.Select(reference =>
            reference with { Payload = TruncateHeadTail(reference.Payload, ToolReferenceHeadTailBytes) }).ToArray();
    }

    public static string TruncateHeadTail(string payload, int headTailBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var utf8 = Encoding.UTF8.GetBytes(payload);
        if (utf8.Length <= headTailBytes * 2)
        {
            return payload;
        }

        var head = Encoding.UTF8.GetString(utf8, 0, headTailBytes);
        var tail = Encoding.UTF8.GetString(utf8, utf8.Length - headTailBytes, headTailBytes);
        return head + "\n…\n" + tail;
    }
}

public static class SecretRedaction
{
    private static readonly string[] ForbiddenSubstrings =
    [
        "bootstrap",
        "runsession",
        "opaqueToken",
        "providerSecret",
        "apiKey",
        "credential",
        "password",
        "LLMW_UI_BOOTSTRAP_TOKEN",
        "LLMW_RUNTIME_BOOTSTRAP_TOKEN",
        "LLMW_CORE_BOOTSTRAP_TOKEN",
        "LLMW_WORKER_BOOTSTRAP_TOKEN"
    ];

    public static string RedactObjectJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return CanonicalJson.Write(document.RootElement, redactSecrets: true);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    public static bool ContainsSecretMaterial(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var needle in ForbiddenSubstrings)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSecretPropertyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("opaque", StringComparison.OrdinalIgnoreCase);
    }
}

public static class CanonicalJson
{
    public static string Sha256Hex(string canonicalUtf8)
    {
        ArgumentNullException.ThrowIfNull(canonicalUtf8);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalUtf8))).ToLowerInvariant();
    }

    public static string WriteCheckpoint(CheckpointV1 checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", checkpoint.SchemaVersion);
            writer.WriteString("approvedPlanReference", checkpoint.ApprovedPlanReference);
            WriteOptionalString(writer, "approvedPlanDigest", checkpoint.ApprovedPlanDigest);
            writer.WritePropertyName("dagTaskState");
            WriteRawOrEmptyObject(writer, checkpoint.DagTaskStateJson);
            writer.WritePropertyName("agentState");
            WriteRawOrEmptyObject(writer, checkpoint.AgentStateJson);
            writer.WriteString("compactedSummary", checkpoint.CompactedSummary);
            writer.WritePropertyName("criticalMessages");
            writer.WriteStartArray();
            foreach (var message in checkpoint.CriticalMessages.OrderBy(item => item.Sequence))
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", message.Sequence);
                writer.WriteString("role", message.Role);
                writer.WriteString("text", message.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("toolReferences");
            writer.WriteStartArray();
            foreach (var tool in checkpoint.ToolReferences.OrderBy(item => item.ToolCallId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("toolCallId", tool.ToolCallId);
                writer.WriteString("toolName", tool.ToolName);
                writer.WriteString("payload", tool.Payload);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteStringArray(writer, "approvalReferences", checkpoint.ApprovalReferences);
            WriteStringArray(writer, "contextPointers", checkpoint.ContextPointers);
            WriteStringArray(writer, "artifactEvidenceReferences", checkpoint.ArtifactEvidenceReferences);
            WriteStringArray(writer, "inputDigestSet", checkpoint.InputDigestSet);
            WriteOptionalString(writer, "promptConfigId", checkpoint.PromptConfigId);
            WriteOptionalString(writer, "providerId", checkpoint.ProviderId);
            WriteOptionalString(writer, "modelId", checkpoint.ModelId);
            WriteOptionalString(writer, "effectivePromptDigest", checkpoint.EffectivePromptDigest);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static CheckpointV1 Parse(string json, int schemaVersion)
    {
        if (schemaVersion != CheckpointV1.CurrentSchemaVersion)
        {
            throw new CheckpointSchemaException(RuntimeRejectionCode.CheckpointUnsupported, schemaVersion);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CheckpointSchemaException(RuntimeRejectionCode.CheckpointCorrupt, schemaVersion);
            }

            var version = root.GetProperty("schemaVersion").GetInt32();
            if (version != CheckpointV1.CurrentSchemaVersion)
            {
                throw new CheckpointSchemaException(RuntimeRejectionCode.CheckpointUnsupported, version);
            }

            return CheckpointV1.Create(
                root.GetProperty("approvedPlanReference").GetString() ?? "",
                OptionalString(root, "approvedPlanDigest"),
                root.GetProperty("dagTaskState").GetRawText(),
                root.GetProperty("agentState").GetRawText(),
                root.GetProperty("compactedSummary").GetString() ?? "",
                ReadMessages(root.GetProperty("criticalMessages")),
                ReadTools(root.GetProperty("toolReferences")),
                ReadStringArray(root.GetProperty("approvalReferences")),
                ReadStringArray(root.GetProperty("contextPointers")),
                ReadStringArray(root.GetProperty("artifactEvidenceReferences")),
                ReadStringArray(root.GetProperty("inputDigestSet")),
                OptionalString(root, "promptConfigId"),
                OptionalString(root, "providerId"),
                OptionalString(root, "modelId"),
                OptionalString(root, "effectivePromptDigest"));
        }
        catch (JsonException exception)
        {
            throw new CheckpointSchemaException(RuntimeRejectionCode.CheckpointCorrupt, schemaVersion, exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new CheckpointSchemaException(RuntimeRejectionCode.CheckpointCorrupt, schemaVersion, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new CheckpointSchemaException(RuntimeRejectionCode.CheckpointCorrupt, schemaVersion, exception);
        }
    }

    public static string Write(JsonElement element, bool redactSecrets)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(writer, element, redactSecrets);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element, bool redactSecrets)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (redactSecrets && SecretRedaction.IsSecretPropertyName(property.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value, redactSecrets);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, redactSecrets);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var int64))
                {
                    writer.WriteNumberValue(int64);
                }
                else
                {
                    writer.WriteNumberValue(element.GetDouble());
                }

                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(item => item, StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteRawOrEmptyObject(Utf8JsonWriter writer, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        using var document = JsonDocument.Parse(json);
        document.RootElement.WriteTo(writer);
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) ? property.GetString() : null;

    private static List<CheckpointCriticalMessage> ReadMessages(JsonElement array)
    {
        var list = new List<CheckpointCriticalMessage>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(new CheckpointCriticalMessage(
                item.GetProperty("sequence").GetInt32(),
                item.GetProperty("role").GetString() ?? "",
                item.GetProperty("text").GetString() ?? ""));
        }

        return list;
    }

    private static List<CheckpointToolReference> ReadTools(JsonElement array)
    {
        var list = new List<CheckpointToolReference>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(new CheckpointToolReference(
                item.GetProperty("toolCallId").GetString() ?? "",
                item.GetProperty("toolName").GetString() ?? "",
                item.GetProperty("payload").GetString() ?? ""));
        }

        return list;
    }

    private static List<string> ReadStringArray(JsonElement array)
    {
        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(item.GetString() ?? "");
        }

        return list;
    }
}

public sealed class CheckpointSchemaException : Exception
{
    public CheckpointSchemaException(RuntimeRejectionCode code, int schemaVersion, Exception? inner = null)
        : base($"Checkpoint schema error {code} version={schemaVersion}.", inner)
    {
        Code = code;
        SchemaVersion = schemaVersion;
    }

    public RuntimeRejectionCode Code { get; }

    public int SchemaVersion { get; }
}

public sealed record FreshnessInputs(
    string? AuthorityRevision,
    IReadOnlyDictionary<string, string> ObjectDigests,
    string? PromptConfigId,
    string? EffectivePromptDigest,
    string? AgentsDigest,
    IReadOnlyDictionary<string, string> SkillDigests,
    string? ProviderId,
    string? ModelId,
    IReadOnlyDictionary<string, string> RequiredArtifactDigests,
    bool StructuralInvalidation,
    bool PlanInvalid,
    bool UnrelatedDraftOnly,
    bool UnknownSideEffect);

public sealed record ResumeDecision(
    ResumeDecisionKind Kind,
    string Reason,
    string? CheckpointId);

public static class ResumeClassifier
{
    public static ResumeDecision Classify(
        DurableRunRecord run,
        DurableCheckpointRecord? latestCheckpoint,
        FreshnessInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.UnknownSideEffect)
        {
            return new ResumeDecision(ResumeDecisionKind.BlockUnknown, "unknown-side-effect", latestCheckpoint?.CheckpointId);
        }

        if (inputs.StructuralInvalidation)
        {
            return new ResumeDecision(ResumeDecisionKind.RestartRun, "structural-invalidation", latestCheckpoint?.CheckpointId);
        }

        if (inputs.PlanInvalid)
        {
            return new ResumeDecision(ResumeDecisionKind.RestartTask, "plan-invalid", latestCheckpoint?.CheckpointId);
        }

        if (inputs.UnrelatedDraftOnly)
        {
            return new ResumeDecision(ResumeDecisionKind.Continue, "unrelated-draft", latestCheckpoint?.CheckpointId);
        }

        if (latestCheckpoint is null)
        {
            return new ResumeDecision(ResumeDecisionKind.RestartTask, "missing-checkpoint", null);
        }

        if (latestCheckpoint.SchemaVersion != CheckpointV1.CurrentSchemaVersion)
        {
            throw new CheckpointSchemaException(RuntimeRejectionCode.CheckpointUnsupported, latestCheckpoint.SchemaVersion);
        }

        var changed = InputsChanged(latestCheckpoint, run, inputs);
        return changed
            ? new ResumeDecision(ResumeDecisionKind.Replan, "inputs-changed-plan-valid", latestCheckpoint.CheckpointId)
            : new ResumeDecision(ResumeDecisionKind.Continue, "unchanged", latestCheckpoint.CheckpointId);
    }

    public static ResumeDecisionKind ClassifyForRebuild(
        DurableRunRecord run,
        DurableCheckpointRecord? latestCheckpoint,
        bool unknownSideEffect)
    {
        if (unknownSideEffect)
        {
            return ResumeDecisionKind.BlockUnknown;
        }

        if (latestCheckpoint is null)
        {
            return RunStatusCodec.TryParse(run.Status, out var status) && status == RunStatus.Interrupted
                ? ResumeDecisionKind.RestartTask
                : ResumeDecisionKind.Continue;
        }

        if (latestCheckpoint.SchemaVersion != CheckpointV1.CurrentSchemaVersion)
        {
            return ResumeDecisionKind.RestartRun;
        }

        return ResumeDecisionKind.Continue;
    }

    private static bool InputsChanged(DurableCheckpointRecord checkpoint, DurableRunRecord run, FreshnessInputs inputs)
    {
        var retained = ParseDigestSet(checkpoint.InputDigestSetJson);
        if (!string.IsNullOrWhiteSpace(inputs.AuthorityRevision) &&
            retained.TryGetValue("authorityRevision", out var previousRevision) &&
            !StringComparer.Ordinal.Equals(previousRevision, inputs.AuthorityRevision))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(inputs.PromptConfigId) &&
            !string.Equals(inputs.PromptConfigId, run.PromptConfigId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(inputs.EffectivePromptDigest) &&
            !string.Equals(inputs.EffectivePromptDigest, run.EffectivePromptDigest, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(inputs.ProviderId) &&
            !string.Equals(inputs.ProviderId, run.ProviderId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(inputs.ModelId) &&
            !string.Equals(inputs.ModelId, run.ModelId, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var pair in inputs.ObjectDigests)
        {
            if (retained.TryGetValue("object:" + pair.Key, out var previous) &&
                !StringComparer.Ordinal.Equals(previous, pair.Value))
            {
                return true;
            }
        }

        foreach (var pair in inputs.RequiredArtifactDigests)
        {
            if (retained.TryGetValue("artifact:" + pair.Key, out var previous) &&
                !StringComparer.Ordinal.Equals(previous, pair.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> ParseDigestSet(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.GetString() ?? "";
            }
        }

        return result;
    }
}
