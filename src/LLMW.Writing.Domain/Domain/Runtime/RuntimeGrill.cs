using System.Text;
using System.Text.Json;

namespace LLMW.Writing.Domain.Runtime;

public enum RuntimeGrillPauseReason
{
    PlanAuthorityAmbiguous,
    NewCreativeDecisionRequired,
    TaskScopeExpansion,
    PlanAssumptionsInvalid
}

public enum RuntimeGrillResolutionKind
{
    Continue,
    Replan,
    RestartTask,
    RestartRun,
    BlockUnknown,
    PlanBlocked
}

public enum ApprovalKind
{
    ToolApproval,
    RuntimeGrill
}

public enum ApprovalStatus
{
    Pending,
    Resolved,
    Denied,
    StaleAlreadyResolved
}

public static class ApprovalKindCodec
{
    public const string ToolApproval = "tool_approval";
    public const string RuntimeGrill = "runtime_grill";

    public static string ToDurableValue(ApprovalKind kind) => kind switch
    {
        ApprovalKind.ToolApproval => ToolApproval,
        ApprovalKind.RuntimeGrill => RuntimeGrill,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParse(string? value, out ApprovalKind kind)
    {
        kind = value switch
        {
            ToolApproval => ApprovalKind.ToolApproval,
            RuntimeGrill => ApprovalKind.RuntimeGrill,
            _ => default
        };
        return value is ToolApproval or RuntimeGrill;
    }
}

public static class ApprovalStatusCodec
{
    public static string ToDurableValue(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Pending => "pending",
        ApprovalStatus.Resolved => "resolved",
        ApprovalStatus.Denied => "denied",
        ApprovalStatus.StaleAlreadyResolved => "stale_already_resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string? value, out ApprovalStatus status)
    {
        status = value switch
        {
            "pending" => ApprovalStatus.Pending,
            "resolved" => ApprovalStatus.Resolved,
            "denied" => ApprovalStatus.Denied,
            "stale_already_resolved" => ApprovalStatus.StaleAlreadyResolved,
            _ => default
        };
        return value is "pending" or "resolved" or "denied" or "stale_already_resolved";
    }
}

public sealed record RuntimeGrillQuestionV1(
    string DecisionKind,
    IReadOnlyList<string> Options,
    string Prompt);

public sealed record RuntimeGrillDecisionRequestV1(
    int SchemaVersion,
    string ApprovalId,
    string RunId,
    string? TaskId,
    RuntimeGrillPauseReason Reason,
    RuntimeGrillQuestionV1 Question,
    NarrativeDecisionAuthority RequiredAuthority,
    string BaselineDigest,
    string? CheckpointId)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record DurableApprovalRecord(
    string ApprovalId,
    string RunId,
    string? TaskId,
    string ApprovalKind,
    string Status,
    string? PayloadDigest,
    string? DecidedBy,
    long? DecidedAtMs,
    long CreatedAtMs);

public static class RuntimeGrillPolicy
{
    public static bool RequiresUserDecision(EffectiveOversightPolicy oversight) =>
        !oversight.NarrativeDelegated;

    public static bool AgentMayResolve(
        EffectiveOversightPolicy oversight,
        RuntimeGrillPauseReason reason,
        bool insideApprovedPlan,
        bool taskScopeUnchanged,
        bool capabilityAllowed,
        bool inputsFresh)
    {
        if (RequiresUserDecision(oversight))
        {
            return false;
        }

        if (reason is RuntimeGrillPauseReason.TaskScopeExpansion or RuntimeGrillPauseReason.PlanAssumptionsInvalid)
        {
            return false;
        }

        return insideApprovedPlan && taskScopeUnchanged && capabilityAllowed && inputsFresh;
    }

    public static RuntimeGrillResolutionKind MapResume(RuntimeGrillPauseReason reason, ResumeDecisionKind freshness) =>
        reason is RuntimeGrillPauseReason.TaskScopeExpansion or RuntimeGrillPauseReason.PlanAssumptionsInvalid
            ? RuntimeGrillResolutionKind.PlanBlocked
            : freshness switch
            {
                ResumeDecisionKind.Continue => RuntimeGrillResolutionKind.Continue,
                ResumeDecisionKind.Replan => RuntimeGrillResolutionKind.Replan,
                ResumeDecisionKind.RestartTask => RuntimeGrillResolutionKind.RestartTask,
                ResumeDecisionKind.RestartRun => RuntimeGrillResolutionKind.RestartRun,
                ResumeDecisionKind.BlockUnknown => RuntimeGrillResolutionKind.BlockUnknown,
                _ => RuntimeGrillResolutionKind.PlanBlocked
            };

    public static string WriteCanonical(RuntimeGrillDecisionRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", RuntimeGrillDecisionRequestV1.CurrentSchemaVersion);
            writer.WriteString("approvalId", request.ApprovalId);
            writer.WriteString("runId", request.RunId);
            if (!string.IsNullOrWhiteSpace(request.TaskId))
            {
                writer.WriteString("taskId", request.TaskId);
            }

            writer.WriteString("reason", ToReason(request.Reason));
            writer.WritePropertyName("question");
            writer.WriteStartObject();
            writer.WriteString("decisionKind", request.Question.DecisionKind);
            writer.WritePropertyName("options");
            writer.WriteStartArray();
            foreach (var option in request.Question.Options.OrderBy(item => item, StringComparer.Ordinal))
            {
                writer.WriteStringValue(option);
            }

            writer.WriteEndArray();
            writer.WriteString("prompt", request.Question.Prompt);
            writer.WriteEndObject();
            writer.WriteString("requiredAuthority", NarrativeDecisionAuthorityCodec.ToDurableValue(request.RequiredAuthority));
            writer.WriteString("baselineDigest", request.BaselineDigest);
            if (!string.IsNullOrWhiteSpace(request.CheckpointId))
            {
                writer.WriteString("checkpointId", request.CheckpointId);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Digest(RuntimeGrillDecisionRequestV1 request) =>
        CanonicalJson.Sha256Hex(WriteCanonical(request));

    public static string StableApprovalId(
        string runId,
        string? taskId,
        string baselineDigest,
        RuntimeGrillPauseReason reason,
        RuntimeGrillQuestionV1 question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var questionDigest = CanonicalJson.Sha256Hex(
            question.DecisionKind + "\n" + question.Prompt + "\n" + string.Join('\n', question.Options.OrderBy(item => item, StringComparer.Ordinal)));
        return "grill:" + CanonicalJson.Sha256Hex(
            runId + "\n" + (taskId ?? "") + "\n" + baselineDigest + "\n" + ToReason(reason) + "\n" + questionDigest);
    }

    public static RuntimeGrillDecisionRequestV1? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(root.GetString() ?? "");
                root = inner.RootElement.Clone();
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !TryParseReason(root.GetProperty("reason").GetString(), out var reason) ||
                !NarrativeDecisionAuthorityCodec.TryParse(root.GetProperty("requiredAuthority").GetString(), out var authority))
            {
                return null;
            }

            var question = root.GetProperty("question");
            var options = new List<string>();
            foreach (var option in question.GetProperty("options").EnumerateArray())
            {
                var value = option.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    options.Add(value);
                }
            }

            return new RuntimeGrillDecisionRequestV1(
                root.TryGetProperty("schemaVersion", out var version) ? version.GetInt32() : 1,
                root.GetProperty("approvalId").GetString() ?? "",
                root.GetProperty("runId").GetString() ?? "",
                root.TryGetProperty("taskId", out var task) ? task.GetString() : null,
                reason,
                new RuntimeGrillQuestionV1(
                    question.GetProperty("decisionKind").GetString() ?? "",
                    options,
                    question.GetProperty("prompt").GetString() ?? ""),
                authority,
                root.GetProperty("baselineDigest").GetString() ?? "",
                root.TryGetProperty("checkpointId", out var checkpoint) ? checkpoint.GetString() : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static bool TryParseReason(string? value, out RuntimeGrillPauseReason reason)
    {
        reason = value switch
        {
            "plan_authority_ambiguous" => RuntimeGrillPauseReason.PlanAuthorityAmbiguous,
            "new_creative_decision_required" => RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            "task_scope_expansion" => RuntimeGrillPauseReason.TaskScopeExpansion,
            "plan_assumptions_invalid" => RuntimeGrillPauseReason.PlanAssumptionsInvalid,
            _ => default
        };
        return value is "plan_authority_ambiguous" or "new_creative_decision_required"
            or "task_scope_expansion" or "plan_assumptions_invalid";
    }

    public static ApprovalStatus Compete(ApprovalStatus current, ApprovalStatus incoming)
    {
        if (current is ApprovalStatus.Resolved or ApprovalStatus.Denied)
        {
            return ApprovalStatus.StaleAlreadyResolved;
        }

        return incoming;
    }

    private static string ToReason(RuntimeGrillPauseReason reason) => reason switch
    {
        RuntimeGrillPauseReason.PlanAuthorityAmbiguous => "plan_authority_ambiguous",
        RuntimeGrillPauseReason.NewCreativeDecisionRequired => "new_creative_decision_required",
        RuntimeGrillPauseReason.TaskScopeExpansion => "task_scope_expansion",
        RuntimeGrillPauseReason.PlanAssumptionsInvalid => "plan_assumptions_invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}
