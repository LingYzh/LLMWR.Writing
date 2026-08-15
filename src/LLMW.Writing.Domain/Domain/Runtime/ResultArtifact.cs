using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Runtime;

public sealed record DurableResultArtifactRecord(
    string ResultArtifactId,
    string TaskId,
    string Status,
    string? ConclusionJson,
    string? FindingsJson,
    string? EvidenceJson,
    string? UncertaintyJson,
    string? DiagnosticsJson,
    string FreshnessJson,
    long ProducedAtMs);

public sealed record DurableProjectSpecialistRecord(
    string SpecialistProfileId,
    string ScopeKind,
    string? ProjectId,
    string Name,
    int Version,
    string DefinitionJson,
    string? BaseDefinitionDigest,
    bool Enabled,
    long CreatedAtMs,
    long UpdatedAtMs);

public enum ResultArtifactStatus
{
    Complete,
    Incomplete,
    Failed
}

public enum ResultFreshnessState
{
    Current,
    Stale,
    NeedsRevalidation
}

public sealed record ResultFindingV1(string Id, string Text, string? ObjectRef);

public sealed record ResultDiagnosticV1(string Code, string Severity, string Message);

public sealed record ResultProvenanceV1(
    string? ProducedByRunId,
    string? ProducedByTaskId,
    string? AttemptId,
    string? SpecialistProfileId,
    string? ApprovedPlanRef,
    string? OriginalUserRequestRef,
    string? ChangeSetId,
    string? TransactionId);

public sealed record ResultProducedAgainstV1(
    string? AuthorityRevision,
    IReadOnlyList<string> NarrativeObjectDigests,
    string? EvidenceDigest,
    string? PromptConfigId,
    string? EffectivePromptDigest,
    string? AgentsDigest,
    IReadOnlyList<string> SkillDigests,
    string? ProviderId,
    string? ModelId,
    IReadOnlyList<string> UpstreamRequiredResultRefs);

public sealed record ResultFreshnessV1(
    int SchemaVersion,
    ResultFreshnessState State,
    ResultProducedAgainstV1 ProducedAgainst,
    ResultProvenanceV1 Provenance);

public sealed record TaskResultArtifactV1(
    string ResultArtifactId,
    string TaskId,
    ResultArtifactStatus Status,
    string Conclusion,
    IReadOnlyList<ResultFindingV1> Findings,
    IReadOnlyList<string> EvidenceIds,
    string? Uncertainty,
    IReadOnlyList<ResultDiagnosticV1> Diagnostics,
    IReadOnlyList<string> AffectedNarrativeObjectRefs,
    IReadOnlyList<string> RecommendedFollowUps,
    ResultFreshnessV1 Freshness,
    string? ProposedChangeSetRef,
    long ProducedAtMs)
{
    public const int CurrentSchemaVersion = 1;

    public int BlockingDiagnosticCount =>
        Diagnostics.Count(item => StringComparer.Ordinal.Equals(item.Severity, "blocking"));

    public IReadOnlySet<string> OutputKeys
    {
        get
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal) { "conclusion", "findings", "freshness" };
            if (EvidenceIds.Count > 0)
            {
                keys.Add("evidence");
            }

            return keys;
        }
    }
}

public static class ResultArtifactStatusCodec
{
    public static string ToDurableValue(ResultArtifactStatus status) => status switch
    {
        ResultArtifactStatus.Complete => "complete",
        ResultArtifactStatus.Incomplete => "incomplete",
        ResultArtifactStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string? value, out ResultArtifactStatus status)
    {
        status = value switch
        {
            "complete" => ResultArtifactStatus.Complete,
            "incomplete" => ResultArtifactStatus.Incomplete,
            "failed" => ResultArtifactStatus.Failed,
            _ => default
        };
        return value is "complete" or "incomplete" or "failed";
    }
}

public static class ResultFreshnessStateCodec
{
    public static string ToDurableValue(ResultFreshnessState state) => state switch
    {
        ResultFreshnessState.Current => "current",
        ResultFreshnessState.Stale => "stale",
        ResultFreshnessState.NeedsRevalidation => "needs_revalidation",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static bool TryParse(string? value, out ResultFreshnessState state)
    {
        state = value switch
        {
            "current" => ResultFreshnessState.Current,
            "stale" => ResultFreshnessState.Stale,
            "needs_revalidation" => ResultFreshnessState.NeedsRevalidation,
            _ => default
        };
        return value is "current" or "stale" or "needs_revalidation";
    }
}

public static class ResultFreshnessPolicy
{
    public static ResultFreshnessState Classify(
        ResultProducedAgainstV1 producedAgainst,
        ResultProducedAgainstV1 current,
        bool unrelatedDraftOnly)
    {
        ArgumentNullException.ThrowIfNull(producedAgainst);
        ArgumentNullException.ThrowIfNull(current);
        if (unrelatedDraftOnly)
        {
            return ResultFreshnessState.Current;
        }

        if (!SameOptional(producedAgainst.AuthorityRevision, current.AuthorityRevision) ||
            !SameSet(producedAgainst.NarrativeObjectDigests, current.NarrativeObjectDigests) ||
            !SameOptional(producedAgainst.EvidenceDigest, current.EvidenceDigest) ||
            !SameSet(producedAgainst.UpstreamRequiredResultRefs, current.UpstreamRequiredResultRefs))
        {
            return ResultFreshnessState.Stale;
        }

        if (!SameOptional(producedAgainst.PromptConfigId, current.PromptConfigId) ||
            !SameOptional(producedAgainst.EffectivePromptDigest, current.EffectivePromptDigest) ||
            !SameOptional(producedAgainst.AgentsDigest, current.AgentsDigest) ||
            !SameSet(producedAgainst.SkillDigests, current.SkillDigests))
        {
            return ResultFreshnessState.NeedsRevalidation;
        }

        return ResultFreshnessState.Current;
    }

    private static bool SameOptional(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right) ||
        StringComparer.Ordinal.Equals(left, right);

    private static bool SameSet(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return left.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);
    }
}

public sealed record TaskHandoffPacketV1(
    string ConsumerTaskId,
    IReadOnlyList<string> ResultArtifactIds,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<ResultFreshnessV1> Freshness,
    IReadOnlyList<string> Warnings,
    bool IncludeTranscript)
{
    public static TaskHandoffPacketV1 Default(
        string consumerTaskId,
        IReadOnlyList<string> resultArtifactIds,
        IReadOnlyList<ResultFreshnessV1> freshness,
        IReadOnlyList<string> warnings) =>
        new(consumerTaskId, resultArtifactIds, [], freshness, warnings, IncludeTranscript: false);
}

public static class ResultArtifactCanonicalJson
{
    public static string Write(TaskResultArtifactV1 artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (SecretRedaction.ContainsSecretMaterial(artifact.Conclusion) ||
            artifact.Findings.Any(item => SecretRedaction.ContainsSecretMaterial(item.Text)) ||
            (!string.IsNullOrWhiteSpace(artifact.Uncertainty) && SecretRedaction.ContainsSecretMaterial(artifact.Uncertainty)))
        {
            artifact = Redact(artifact);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", TaskResultArtifactV1.CurrentSchemaVersion);
            writer.WriteString("resultArtifactId", artifact.ResultArtifactId);
            writer.WriteString("taskId", artifact.TaskId);
            writer.WriteString("status", ResultArtifactStatusCodec.ToDurableValue(artifact.Status));
            writer.WriteString("conclusion", artifact.Conclusion);
            writer.WritePropertyName("findings");
            writer.WriteStartArray();
            foreach (var finding in artifact.Findings.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", finding.Id);
                writer.WriteString("text", finding.Text);
                if (!string.IsNullOrWhiteSpace(finding.ObjectRef))
                {
                    writer.WriteString("objectRef", finding.ObjectRef);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteStringArray(writer, "evidenceIds", artifact.EvidenceIds);
            if (!string.IsNullOrWhiteSpace(artifact.Uncertainty))
            {
                writer.WriteString("uncertainty", artifact.Uncertainty);
            }

            writer.WritePropertyName("diagnostics");
            writer.WriteStartArray();
            foreach (var diagnostic in artifact.Diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal)
                         .ThenBy(item => item.Message, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString("severity", diagnostic.Severity);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteStringArray(writer, "affectedNarrativeObjectRefs", artifact.AffectedNarrativeObjectRefs);
            WriteStringArray(writer, "recommendedFollowUps", artifact.RecommendedFollowUps);
            if (!string.IsNullOrWhiteSpace(artifact.ProposedChangeSetRef))
            {
                writer.WriteString("proposedChangeSetRef", artifact.ProposedChangeSetRef);
            }

            writer.WriteNumber("producedAtMs", artifact.ProducedAtMs);
            writer.WritePropertyName("freshness");
            WriteFreshness(writer, artifact.Freshness);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string WriteColumn(string name, TaskResultArtifactV1 artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", TaskResultArtifactV1.CurrentSchemaVersion);
            switch (name)
            {
                case "conclusion":
                    writer.WriteString("text", artifact.Conclusion);
                    WriteStringArray(writer, "recommendedFollowUps", artifact.RecommendedFollowUps);
                    if (!string.IsNullOrWhiteSpace(artifact.ProposedChangeSetRef))
                    {
                        writer.WriteString("proposedChangeSetRef", artifact.ProposedChangeSetRef);
                    }

                    break;
                case "findings":
                    writer.WritePropertyName("items");
                    writer.WriteStartArray();
                    foreach (var finding in artifact.Findings.OrderBy(item => item.Id, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", finding.Id);
                        writer.WriteString("text", finding.Text);
                        if (!string.IsNullOrWhiteSpace(finding.ObjectRef))
                        {
                            writer.WriteString("objectRef", finding.ObjectRef);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    WriteStringArray(writer, "affectedNarrativeObjectRefs", artifact.AffectedNarrativeObjectRefs);
                    break;
                case "evidence":
                    WriteStringArray(writer, "evidenceIds", artifact.EvidenceIds);
                    break;
                case "uncertainty":
                    writer.WriteString("notes", artifact.Uncertainty ?? "");
                    break;
                case "diagnostics":
                    writer.WritePropertyName("items");
                    writer.WriteStartArray();
                    foreach (var diagnostic in artifact.Diagnostics.OrderBy(item => item.Code, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("code", diagnostic.Code);
                        writer.WriteString("severity", diagnostic.Severity);
                        writer.WriteString("message", diagnostic.Message);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    break;
                case "freshness":
                    WriteFreshnessContent(writer, artifact.Freshness, writeSchemaVersion: false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(name), name, null);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static TaskResultArtifactV1 ParseColumns(
        string resultArtifactId,
        string taskId,
        string status,
        string? conclusionJson,
        string? findingsJson,
        string? evidenceJson,
        string? uncertaintyJson,
        string? diagnosticsJson,
        string freshnessJson,
        long producedAtMs)
    {
        if (!ResultArtifactStatusCodec.TryParse(status, out var parsedStatus))
        {
            throw new InvalidOperationException("Unsupported result artifact status.");
        }

        using var freshnessDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(freshnessJson) ? "{}" : freshnessJson);
        var freshnessRoot = freshnessDoc.RootElement.TryGetProperty("state", out _)
            ? freshnessDoc.RootElement
            : freshnessDoc.RootElement.TryGetProperty("freshness", out var nested)
                ? nested
                : freshnessDoc.RootElement;
        var conclusion = ReadObject(conclusionJson);
        var findings = ReadObject(findingsJson);
        var evidence = ReadObject(evidenceJson);
        var uncertainty = ReadObject(uncertaintyJson);
        var diagnostics = ReadObject(diagnosticsJson);
        return new TaskResultArtifactV1(
            resultArtifactId,
            taskId,
            parsedStatus,
            ReadString(conclusion, "text") ?? "",
            ReadFindings(findings),
            ReadStringList(evidence, "evidenceIds"),
            ReadString(uncertainty, "notes"),
            ReadDiagnostics(diagnostics),
            ReadStringList(findings, "affectedNarrativeObjectRefs"),
            ReadStringList(conclusion, "recommendedFollowUps"),
            ReadFreshness(freshnessRoot),
            ReadString(conclusion, "proposedChangeSetRef"),
            producedAtMs);
    }

    public static string Digest(TaskResultArtifactV1 artifact) => CanonicalJson.Sha256Hex(Write(artifact));

    public static DurableResultArtifactRecord ToDurable(TaskResultArtifactV1 artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new DurableResultArtifactRecord(
            artifact.ResultArtifactId,
            artifact.TaskId,
            ResultArtifactStatusCodec.ToDurableValue(artifact.Status),
            WriteColumn("conclusion", artifact),
            WriteColumn("findings", artifact),
            WriteColumn("evidence", artifact),
            WriteColumn("uncertainty", artifact),
            WriteColumn("diagnostics", artifact),
            WriteColumn("freshness", artifact),
            artifact.ProducedAtMs);
    }

    public static TaskResultArtifactV1 FromDurable(DurableResultArtifactRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ParseColumns(
            record.ResultArtifactId,
            record.TaskId,
            record.Status,
            record.ConclusionJson,
            record.FindingsJson,
            record.EvidenceJson,
            record.UncertaintyJson,
            record.DiagnosticsJson,
            record.FreshnessJson,
            record.ProducedAtMs);
    }

    public static bool ContainsTranscript(TaskResultArtifactV1 artifact) =>
        artifact.Findings.Any(item => item.Text.Contains("transcript", StringComparison.OrdinalIgnoreCase)) ||
        artifact.Conclusion.Contains("full transcript", StringComparison.OrdinalIgnoreCase);

    private static TaskResultArtifactV1 Redact(TaskResultArtifactV1 artifact) =>
        artifact with
        {
            Conclusion = "[redacted]",
            Findings = artifact.Findings.Select(item => item with
            {
                Text = SecretRedaction.ContainsSecretMaterial(item.Text) ? "[redacted]" : item.Text
            }).ToArray(),
            Uncertainty = artifact.Uncertainty is null
                ? null
                : SecretRedaction.ContainsSecretMaterial(artifact.Uncertainty) ? "[redacted]" : artifact.Uncertainty
        };

    private static void WriteFreshness(Utf8JsonWriter writer, ResultFreshnessV1 freshness)
    {
        writer.WriteStartObject();
        WriteFreshnessContent(writer, freshness, writeSchemaVersion: true);
        writer.WriteEndObject();
    }

    private static void WriteFreshnessContent(Utf8JsonWriter writer, ResultFreshnessV1 freshness, bool writeSchemaVersion)
    {
        if (writeSchemaVersion)
        {
            writer.WriteNumber("schemaVersion", freshness.SchemaVersion == 0 ? 1 : freshness.SchemaVersion);
        }

        writer.WriteString("state", ResultFreshnessStateCodec.ToDurableValue(freshness.State));
        writer.WritePropertyName("producedAgainst");
        writer.WriteStartObject();
        WriteOptional(writer, "authorityRevision", freshness.ProducedAgainst.AuthorityRevision);
        WriteStringArray(writer, "narrativeObjectDigests", freshness.ProducedAgainst.NarrativeObjectDigests);
        WriteOptional(writer, "evidenceDigest", freshness.ProducedAgainst.EvidenceDigest);
        WriteOptional(writer, "promptConfigId", freshness.ProducedAgainst.PromptConfigId);
        WriteOptional(writer, "effectivePromptDigest", freshness.ProducedAgainst.EffectivePromptDigest);
        WriteOptional(writer, "agentsDigest", freshness.ProducedAgainst.AgentsDigest);
        WriteStringArray(writer, "skillDigests", freshness.ProducedAgainst.SkillDigests);
        WriteOptional(writer, "providerId", freshness.ProducedAgainst.ProviderId);
        WriteOptional(writer, "modelId", freshness.ProducedAgainst.ModelId);
        WriteStringArray(writer, "upstreamRequiredResultRefs", freshness.ProducedAgainst.UpstreamRequiredResultRefs);
        writer.WriteEndObject();
        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        WriteOptional(writer, "producedByRunId", freshness.Provenance.ProducedByRunId);
        WriteOptional(writer, "producedByTaskId", freshness.Provenance.ProducedByTaskId);
        WriteOptional(writer, "attemptId", freshness.Provenance.AttemptId);
        WriteOptional(writer, "specialistProfileId", freshness.Provenance.SpecialistProfileId);
        WriteOptional(writer, "approvedPlanRef", freshness.Provenance.ApprovedPlanRef);
        WriteOptional(writer, "originalUserRequestRef", freshness.Provenance.OriginalUserRequestRef);
        WriteOptional(writer, "changeSetId", freshness.Provenance.ChangeSetId);
        WriteOptional(writer, "transactionId", freshness.Provenance.TransactionId);
        writer.WriteEndObject();
    }

    private static ResultFreshnessV1 ReadFreshness(JsonElement root)
    {
        var produced = root.TryGetProperty("producedAgainst", out var against) ? against : default;
        var provenance = root.TryGetProperty("provenance", out var proven) ? proven : default;
        if (!ResultFreshnessStateCodec.TryParse(root.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : "current", out var parsedState))
        {
            parsedState = ResultFreshnessState.Current;
        }
        return new ResultFreshnessV1(
            root.TryGetProperty("schemaVersion", out var version) ? version.GetInt32() : 1,
            parsedState,
            new ResultProducedAgainstV1(
                Optional(produced, "authorityRevision"),
                ReadStringList(produced, "narrativeObjectDigests"),
                Optional(produced, "evidenceDigest"),
                Optional(produced, "promptConfigId"),
                Optional(produced, "effectivePromptDigest"),
                Optional(produced, "agentsDigest"),
                ReadStringList(produced, "skillDigests"),
                Optional(produced, "providerId"),
                Optional(produced, "modelId"),
                ReadStringList(produced, "upstreamRequiredResultRefs")),
            new ResultProvenanceV1(
                Optional(provenance, "producedByRunId"),
                Optional(provenance, "producedByTaskId"),
                Optional(provenance, "attemptId"),
                Optional(provenance, "specialistProfileId"),
                Optional(provenance, "approvedPlanRef"),
                Optional(provenance, "originalUserRequestRef"),
                Optional(provenance, "changeSetId"),
                Optional(provenance, "transactionId")));
    }

    private static JsonElement ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static ResultFindingV1[] ReadFindings(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray().Select(item => new ResultFindingV1(
            item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            item.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            item.TryGetProperty("objectRef", out var obj) ? obj.GetString() : null)).ToArray();
    }

    private static ResultDiagnosticV1[] ReadDiagnostics(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray().Select(item => new ResultDiagnosticV1(
            item.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "",
            item.TryGetProperty("severity", out var severity) ? severity.GetString() ?? "" : "",
            item.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "")).ToArray();
    }

    private static string[] ReadStringList(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string? Optional(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
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
}
