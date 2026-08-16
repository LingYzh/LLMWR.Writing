using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Provider;

public static class RootConflictMetrics
{
    public const string RootRecall = "RootRecall";
    public const string FalseMergeRate = "FalseMergeRate";
    public const string EvidenceFidelity = "EvidenceFidelity";
    public const string PropagationAccuracy = "PropagationAccuracy";
    public const string RecomputeAccuracy = "RecomputeAccuracy";
    public const string AbstentionQuality = "AbstentionQuality";
}

public sealed record TaskEvalScore(string MetricName, decimal Value);

public enum ThresholdComparator
{
    Minimum,
    Maximum
}

public sealed record TaskEvalThreshold(string MetricName, decimal Bound, ThresholdComparator Comparator, bool MustPass)
{
    public static TaskEvalThreshold AtLeast(string metricName, decimal bound, bool mustPass = true) =>
        new(metricName, bound, ThresholdComparator.Minimum, mustPass);

    public static TaskEvalThreshold AtMost(string metricName, decimal bound, bool mustPass = true) =>
        new(metricName, bound, ThresholdComparator.Maximum, mustPass);

    public static ThresholdComparator ComparatorFor(string metricName) =>
        string.Equals(metricName, RootConflictMetrics.FalseMergeRate, StringComparison.Ordinal)
            ? ThresholdComparator.Maximum
            : ThresholdComparator.Minimum;
}

public sealed record TaskCapabilityCertification(
    string CertificationId,
    int CertificationVersion,
    string DatasetId,
    string DatasetVersion,
    string EvaluationSuiteVersion,
    IReadOnlyList<TaskEvalScore> Scores,
    IReadOnlyList<TaskEvalThreshold> Thresholds,
    IReadOnlyList<string> CertifiedTaskClasses,
    ReasoningCeiling MaxReasoningCeiling,
    ProviderDefinitionId ProviderDefinitionId,
    ProviderRevision ProviderRevision,
    string EndpointIdentity,
    string ProtocolAdapterId,
    string ProtocolAdapterVersion,
    ModelId ModelId,
    string? PromptBaselineDigest,
    CertificationState State,
    long CertifiedAtMs)
{
    public const string CurrentEvaluationSuiteVersion = "wp14-task-eval-v1";
    public const string RootConflictTaskClass = "root_conflict";

    public static IReadOnlyList<string> RootConflictRequiredMetrics { get; } =
    [
        RootConflictMetrics.RootRecall,
        RootConflictMetrics.FalseMergeRate,
        RootConflictMetrics.EvidenceFidelity,
        RootConflictMetrics.PropagationAccuracy,
        RootConflictMetrics.RecomputeAccuracy,
        RootConflictMetrics.AbstentionQuality
    ];

    public ReasoningCeiling EffectiveCeiling =>
        State == CertificationState.Certified ? MaxReasoningCeiling : ReasoningCeiling.Conservative;

    public bool RequiresRootConflictMetrics =>
        CertifiedTaskClasses.Any(item => string.Equals(item, RootConflictTaskClass, StringComparison.Ordinal));

    public bool HasCompleteRequiredMetrics()
    {
        if (!RequiresRootConflictMetrics)
        {
            return true;
        }

        foreach (var metric in RootConflictRequiredMetrics)
        {
            if (!Scores.Any(item => string.Equals(item.MetricName, metric, StringComparison.Ordinal)))
            {
                return false;
            }

            if (!Thresholds.Any(item =>
                    item.MustPass && string.Equals(item.MetricName, metric, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    public bool PassesThresholds()
    {
        var required = Thresholds.Where(item => item.MustPass).ToArray();
        if (required.Length == 0)
        {
            return false;
        }

        foreach (var threshold in required)
        {
            var score = Scores.FirstOrDefault(item =>
                string.Equals(item.MetricName, threshold.MetricName, StringComparison.Ordinal));
            if (score is null)
            {
                return false;
            }

            if (threshold.Comparator == ThresholdComparator.Maximum)
            {
                if (score.Value > threshold.Bound)
                {
                    return false;
                }
            }
            else if (score.Value < threshold.Bound)
            {
                return false;
            }
        }

        return true;
    }

    public bool IsStaleFor(
        ProviderRevision currentRevision,
        string currentEndpointIdentity,
        string currentAdapterId,
        string currentAdapterVersion,
        string currentEvaluationSuiteVersion,
        string? currentPromptBaselineDigest)
    {
        if (State is CertificationState.Stale or CertificationState.Failed or CertificationState.Uncertified)
        {
            return State is CertificationState.Stale or CertificationState.Failed;
        }

        return currentRevision != ProviderRevision ||
               !ProviderEndpoint.IdentitiesMatch(currentEndpointIdentity, EndpointIdentity) ||
               !string.Equals(currentAdapterId, ProtocolAdapterId, StringComparison.Ordinal) ||
               !string.Equals(currentAdapterVersion, ProtocolAdapterVersion, StringComparison.Ordinal) ||
               !string.Equals(currentEvaluationSuiteVersion, EvaluationSuiteVersion, StringComparison.Ordinal) ||
               (currentPromptBaselineDigest is not null &&
                !string.Equals(currentPromptBaselineDigest, PromptBaselineDigest ?? "", StringComparison.Ordinal));
    }

    public static TaskCapabilityCertification Uncertified(
        ProviderDefinitionId provider,
        ProviderRevision revision,
        string endpointIdentity,
        string adapterId,
        string adapterVersion,
        ModelId model) =>
        new(
            "task-uncertified:" + provider.Value + ":" + model.Value,
            0,
            "",
            "",
            CurrentEvaluationSuiteVersion,
            [],
            [],
            [],
            ReasoningCeiling.Conservative,
            provider,
            revision,
            endpointIdentity,
            adapterId,
            adapterVersion,
            model,
            null,
            CertificationState.Uncertified,
            0);

    public string CanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("certificationId", CertificationId);
            writer.WriteNumber("certificationVersion", CertificationVersion);
            writer.WriteString("datasetId", DatasetId);
            writer.WriteString("datasetVersion", DatasetVersion);
            writer.WriteString("evaluationSuiteVersion", EvaluationSuiteVersion);
            writer.WritePropertyName("scores");
            writer.WriteStartArray();
            foreach (var score in Scores.OrderBy(item => item.MetricName, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("metric", score.MetricName);
                writer.WriteString("value", score.Value.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("thresholds");
            writer.WriteStartArray();
            foreach (var threshold in Thresholds.OrderBy(item => item.MetricName, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("metric", threshold.MetricName);
                writer.WriteString("bound", threshold.Bound.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("comparator", threshold.Comparator == ThresholdComparator.Maximum ? "maximum" : "minimum");
                writer.WriteBoolean("mustPass", threshold.MustPass);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("certifiedTaskClasses");
            writer.WriteStartArray();
            foreach (var item in CertifiedTaskClasses.OrderBy(value => value, StringComparer.Ordinal))
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
            writer.WriteString("maxReasoningCeiling", ReasoningCeilingCodec.ToDurableValue(MaxReasoningCeiling));
            writer.WriteString("providerDefinitionId", ProviderDefinitionId.Value);
            writer.WriteNumber("providerRevision", ProviderRevision.Value);
            writer.WriteString("endpointIdentity", EndpointIdentity);
            writer.WriteString("protocolAdapterId", ProtocolAdapterId);
            writer.WriteString("protocolAdapterVersion", ProtocolAdapterVersion);
            writer.WriteString("modelId", ModelId.Value);
            if (!string.IsNullOrEmpty(PromptBaselineDigest))
            {
                writer.WriteString("promptBaselineDigest", PromptBaselineDigest);
            }

            writer.WriteString("state", CertificationStateCodec.ToDurableValue(State));
            writer.WriteNumber("certifiedAtMs", CertifiedAtMs);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public static class ProtocolCapabilityNames
{
    public const string BasicText = ModelCapabilityNames.BasicText;
    public const string Streaming = ModelCapabilityNames.Streaming;
    public const string InstructionHierarchy = ModelCapabilityNames.InstructionHierarchy;
    public const string ToolCalling = ModelCapabilityNames.ToolCalling;
    public const string StructuredJson = ModelCapabilityNames.StructuredJson;
    public const string UsageReporting = ModelCapabilityNames.UsageReporting;
    public const string CacheUsageReporting = ModelCapabilityNames.CacheUsageReporting;
    public const string ReasoningControl = ModelCapabilityNames.ReasoningControl;
    public const string ContextMetadata = "context_metadata";
    public const string OutputMetadata = "output_metadata";
    public const string DataBehavior = "data_behavior";
    public const string ThinkingWithTools = "thinking_with_tools";
    public const string ThinkingManual = "thinking_manual";
    public const string ThinkingAdaptive = "thinking_adaptive";
    public const string ThinkingEffort = "thinking_effort";
}
