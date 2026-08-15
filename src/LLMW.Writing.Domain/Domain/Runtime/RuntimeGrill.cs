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

    public static string StableApprovalId(string runId, string? taskId, string baselineDigest) =>
        "grill:" + CanonicalJson.Sha256Hex(runId + "\n" + (taskId ?? "") + "\n" + baselineDigest);

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
