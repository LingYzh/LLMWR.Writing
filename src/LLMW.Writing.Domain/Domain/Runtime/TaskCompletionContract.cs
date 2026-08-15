using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Runtime;

public enum SemanticCompletionOutcome
{
    Pass,
    ReviewRequired,
    Fail
}

public enum CompletionCheckOutcome
{
    Pass,
    DeterministicFail,
    SemanticReviewRequired,
    SemanticFail
}

public sealed record SemanticCompletionCriterion(string Id, string Description);

public sealed record TaskCompletionContractV1(
    int SchemaVersion,
    IReadOnlyList<string> RequiredOutputKeys,
    IReadOnlyList<string> RequiredProducerTaskIds,
    IReadOnlyList<string> RequiredInputRefs,
    IReadOnlyList<string> RequiredResultShapeKeys,
    bool RequireZeroBlockingDiagnostics,
    IReadOnlyList<SemanticCompletionCriterion> SemanticCriteria)
{
    public const int CurrentSchemaVersion = 1;

    public static TaskCompletionContractV1 Empty { get; } = new(
        CurrentSchemaVersion,
        [],
        [],
        [],
        [],
        true,
        []);

    public bool HasSemanticCriteria => SemanticCriteria.Count > 0;
}

public sealed record CompletionCheckInputs(
    IReadOnlySet<string> PresentOutputKeys,
    IReadOnlySet<string> CompletedTaskIds,
    IReadOnlySet<string> ReadCurrentInputRefs,
    IReadOnlySet<string> ResultShapeKeys,
    int BlockingDiagnosticCount,
    IReadOnlyList<DurableDependencyRecord> Dependencies,
    bool ResultArtifactPresent,
    SemanticCompletionOutcome? SemanticOutcome);

public sealed record CompletionCheckResult(
    CompletionCheckOutcome Outcome,
    IReadOnlyList<string> Failures);

public static class TaskCompletionContractChecker
{
    public static CompletionCheckResult Check(
        TaskCompletionContractV1 contract,
        CompletionCheckInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(inputs);
        if (contract.SchemaVersion != TaskCompletionContractV1.CurrentSchemaVersion)
        {
            return new CompletionCheckResult(CompletionCheckOutcome.DeterministicFail, ["unsupported-contract-version"]);
        }

        var failures = new List<string>();
        if (!inputs.ResultArtifactPresent)
        {
            failures.Add("required-result-artifact-missing");
        }

        foreach (var key in contract.RequiredOutputKeys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!inputs.PresentOutputKeys.Contains(key))
            {
                failures.Add("missing-required-output:" + key);
            }
        }

        foreach (var taskId in contract.RequiredProducerTaskIds.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!inputs.CompletedTaskIds.Contains(taskId))
            {
                failures.Add("required-task-incomplete:" + taskId);
            }
        }

        foreach (var inputRef in contract.RequiredInputRefs.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!inputs.ReadCurrentInputRefs.Contains(inputRef))
            {
                failures.Add("required-input-not-current:" + inputRef);
            }
        }

        foreach (var key in contract.RequiredResultShapeKeys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!inputs.ResultShapeKeys.Contains(key))
            {
                failures.Add("result-shape-mismatch:" + key);
            }
        }

        if (contract.RequireZeroBlockingDiagnostics && inputs.BlockingDiagnosticCount != 0)
        {
            failures.Add("blocking-diagnostics");
        }

        foreach (var dependency in inputs.Dependencies)
        {
            var evaluation = ResultDependencyPolicy.Evaluate(dependency);
            if (evaluation.BlocksCompletion)
            {
                failures.Add("required-dependency-" + ResultDependencyStatusCodec.ToDurableValue(evaluation.EffectiveStatus) +
                             ":" + dependency.ProducerTaskId);
            }
        }

        if (failures.Count > 0)
        {
            return new CompletionCheckResult(CompletionCheckOutcome.DeterministicFail, failures);
        }

        if (contract.HasSemanticCriteria)
        {
            return inputs.SemanticOutcome switch
            {
                SemanticCompletionOutcome.Pass => new CompletionCheckResult(CompletionCheckOutcome.Pass, []),
                SemanticCompletionOutcome.Fail => new CompletionCheckResult(
                    CompletionCheckOutcome.SemanticFail,
                    ["semantic-completion-failed"]),
                _ => new CompletionCheckResult(
                    CompletionCheckOutcome.SemanticReviewRequired,
                    ["semantic-review-required"])
            };
        }

        return new CompletionCheckResult(CompletionCheckOutcome.Pass, []);
    }
}

public static class TaskCompletionContractCanonicalJson
{
    public static string Write(TaskCompletionContractV1 contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", contract.SchemaVersion);
            WriteStrings(writer, "requiredOutputKeys", contract.RequiredOutputKeys);
            WriteStrings(writer, "requiredProducerTaskIds", contract.RequiredProducerTaskIds);
            WriteStrings(writer, "requiredInputRefs", contract.RequiredInputRefs);
            WriteStrings(writer, "requiredResultShapeKeys", contract.RequiredResultShapeKeys);
            writer.WriteBoolean("requireZeroBlockingDiagnostics", contract.RequireZeroBlockingDiagnostics);
            writer.WritePropertyName("semanticCriteria");
            writer.WriteStartArray();
            foreach (var item in contract.SemanticCriteria.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("description", item.Description);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static TaskCompletionContractV1 Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return TaskCompletionContractV1.Empty;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var criteria = new List<SemanticCompletionCriterion>();
        if (root.TryGetProperty("semanticCriteria", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                criteria.Add(new SemanticCompletionCriterion(
                    item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    item.TryGetProperty("description", out var description) ? description.GetString() ?? "" : ""));
            }
        }

        return new TaskCompletionContractV1(
            root.TryGetProperty("schemaVersion", out var version) ? version.GetInt32() : 1,
            ReadStrings(root, "requiredOutputKeys"),
            ReadStrings(root, "requiredProducerTaskIds"),
            ReadStrings(root, "requiredInputRefs"),
            ReadStrings(root, "requiredResultShapeKeys"),
            !root.TryGetProperty("requireZeroBlockingDiagnostics", out var zero) || zero.GetBoolean(),
            criteria);
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(item => item, StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string[] ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    }
}
