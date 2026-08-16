using System.Text;
using System.Text.Json;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Domain.Runtime;

public enum NarrativeDecisionAuthority
{
    AuthorConfirmedRequired,
    AgentDelegated
}

public enum OversightScopeKind
{
    Application,
    Project,
    Storyline,
    Task
}

public enum OversightProductPreset
{
    ManualAsk,
    AcceptEdits,
    Auto,
    BypassPermissions
}

public static class NarrativeDecisionAuthorityCodec
{
    public static string ToDurableValue(NarrativeDecisionAuthority authority) => authority switch
    {
        NarrativeDecisionAuthority.AuthorConfirmedRequired => "author_confirmed_required",
        NarrativeDecisionAuthority.AgentDelegated => "agent_delegated",
        _ => throw new ArgumentOutOfRangeException(nameof(authority), authority, null)
    };

    public static bool TryParse(string? value, out NarrativeDecisionAuthority authority)
    {
        authority = value switch
        {
            "author_confirmed_required" => NarrativeDecisionAuthority.AuthorConfirmedRequired,
            "agent_delegated" => NarrativeDecisionAuthority.AgentDelegated,
            _ => default
        };
        return value is "author_confirmed_required" or "agent_delegated";
    }
}

public static class RuntimePermissionModeDurableCodec
{
    public static string ToDurableValue(RuntimePermissionMode mode) => mode switch
    {
        RuntimePermissionMode.Ask => "ask",
        RuntimePermissionMode.AcceptEdits => "accept_edits",
        RuntimePermissionMode.AutoApproveScoped => "auto_approve_scoped",
        RuntimePermissionMode.BypassPermissions => "bypass_permissions",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static bool TryParse(string? value, out RuntimePermissionMode mode)
    {
        mode = value switch
        {
            "ask" => RuntimePermissionMode.Ask,
            "accept_edits" => RuntimePermissionMode.AcceptEdits,
            "auto_approve_scoped" => RuntimePermissionMode.AutoApproveScoped,
            "bypass_permissions" => RuntimePermissionMode.BypassPermissions,
            _ => default
        };
        return value is "ask" or "accept_edits" or "auto_approve_scoped" or "bypass_permissions";
    }
}

public static class OversightScopeKindCodec
{
    public static string ToDurableValue(OversightScopeKind scope) => scope switch
    {
        OversightScopeKind.Application => "application",
        OversightScopeKind.Project => "project",
        OversightScopeKind.Storyline => "storyline",
        OversightScopeKind.Task => "task",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    public static bool TryParse(string? value, out OversightScopeKind scope)
    {
        scope = value switch
        {
            "application" => OversightScopeKind.Application,
            "project" => OversightScopeKind.Project,
            "storyline" => OversightScopeKind.Storyline,
            "task" => OversightScopeKind.Task,
            _ => default
        };
        return value is "application" or "project" or "storyline" or "task";
    }
}

public sealed record OversightAxes(
    NarrativeDecisionAuthority NarrativeAuthority,
    RuntimePermissionMode RuntimePermission);

public sealed record OversightOverrideRecord(
    string OverrideId,
    OversightScopeKind ScopeKind,
    string? ScopeId,
    NarrativeDecisionAuthority NarrativeAuthority,
    RuntimePermissionMode RuntimePermission,
    string? EffectiveAfterCheckpointId,
    string CreatedBy,
    long CreatedAtMs);

public sealed record EffectiveOversightPolicy(
    NarrativeDecisionAuthority NarrativeAuthority,
    RuntimePermissionMode RuntimePermission,
    OversightScopeKind WinningScope,
    string? WinningScopeId,
    string? WinningOverrideId,
    string? EffectiveAfterCheckpointId,
    bool Active)
{
    public static EffectiveOversightPolicy ApplicationDefault { get; } = new(
        NarrativeDecisionAuthority.AuthorConfirmedRequired,
        RuntimePermissionMode.Ask,
        OversightScopeKind.Application,
        null,
        null,
        null,
        true);

    public bool NarrativeDelegated =>
        Active && NarrativeAuthority == NarrativeDecisionAuthority.AgentDelegated;

    public DecisionAuthorityKind AuthorityKind => NarrativeDelegated
        ? DecisionAuthorityKind.AgentDelegated
        : DecisionAuthorityKind.AuthorConfirmed;
}

public static class OversightPresetMap
{
    public static OversightAxes FromPreset(OversightProductPreset preset) => preset switch
    {
        OversightProductPreset.ManualAsk => new OversightAxes(
            NarrativeDecisionAuthority.AuthorConfirmedRequired,
            RuntimePermissionMode.Ask),
        OversightProductPreset.AcceptEdits => new OversightAxes(
            NarrativeDecisionAuthority.AuthorConfirmedRequired,
            RuntimePermissionMode.AcceptEdits),
        OversightProductPreset.Auto => new OversightAxes(
            NarrativeDecisionAuthority.AgentDelegated,
            RuntimePermissionMode.AutoApproveScoped),
        OversightProductPreset.BypassPermissions => new OversightAxes(
            NarrativeDecisionAuthority.AuthorConfirmedRequired,
            RuntimePermissionMode.BypassPermissions),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };
}

public static class OversightActivation
{
    public const string PendingBindPrefix = "pending:";

    public static string PendingBindToken(string overrideId) => PendingBindPrefix + overrideId;

    public static bool IsPendingBind(string? checkpointId) =>
        !string.IsNullOrWhiteSpace(checkpointId) &&
        checkpointId.StartsWith(PendingBindPrefix, StringComparison.Ordinal);

    public static bool IsActive(OversightOverrideRecord record, IReadOnlySet<string> existingCheckpointIds)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(existingCheckpointIds);
        if (string.IsNullOrWhiteSpace(record.EffectiveAfterCheckpointId))
        {
            return true;
        }

        if (IsPendingBind(record.EffectiveAfterCheckpointId))
        {
            return false;
        }

        return existingCheckpointIds.Contains(record.EffectiveAfterCheckpointId);
    }

    public static bool IsActiveForExecution(OversightOverrideRecord record, OversightActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(record.EffectiveAfterCheckpointId))
        {
            return true;
        }

        if (IsPendingBind(record.EffectiveAfterCheckpointId))
        {
            return BornAfterOverride(record, context) || HasMatchingSafeCheckpoint(record, context);
        }

        return context.ExecutionCheckpoints.Any(item =>
            StringComparer.Ordinal.Equals(item.CheckpointId, record.EffectiveAfterCheckpointId) &&
            CheckpointMatchesScope(record, item, context));
    }

    private static bool BornAfterOverride(OversightOverrideRecord record, OversightActivationContext context)
    {
        if (record.ScopeKind == OversightScopeKind.Task)
        {
            return StringComparer.Ordinal.Equals(record.ScopeId, context.TaskId) &&
                   context.TaskCreatedAtMs is { } taskCreated &&
                   taskCreated > record.CreatedAtMs;
        }

        if (!ScopeMatches(record, context))
        {
            return false;
        }

        if (context.TaskCreatedAtMs is { } createdTask && createdTask > record.CreatedAtMs)
        {
            return true;
        }

        return context.RunCreatedAtMs is { } createdRun && createdRun > record.CreatedAtMs;
    }

    private static bool ScopeMatches(OversightOverrideRecord record, OversightActivationContext context) =>
        record.ScopeKind switch
        {
            OversightScopeKind.Storyline => StringComparer.Ordinal.Equals(record.ScopeId, context.StorylineId),
            OversightScopeKind.Project => StringComparer.Ordinal.Equals(record.ScopeId, context.ProjectId),
            _ => false
        };

    private static bool HasMatchingSafeCheckpoint(OversightOverrideRecord record, OversightActivationContext context)
    {
        var after = context.ExecutionCheckpoints.Where(item => item.CreatedAtMs > record.CreatedAtMs);
        return record.ScopeKind switch
        {
            OversightScopeKind.Task => false,
            OversightScopeKind.Storyline =>
                StringComparer.Ordinal.Equals(record.ScopeId, context.StorylineId) &&
                after.Any(item => StringComparer.Ordinal.Equals(item.RunId, context.RunId)),
            OversightScopeKind.Project =>
                StringComparer.Ordinal.Equals(record.ScopeId, context.ProjectId) &&
                after.Any(item => StringComparer.Ordinal.Equals(item.RunId, context.RunId)),
            _ => false
        };
    }

    private static bool CheckpointMatchesScope(
        OversightOverrideRecord record,
        DurableCheckpointRecord checkpoint,
        OversightActivationContext context) =>
        record.ScopeKind switch
        {
            OversightScopeKind.Task => StringComparer.Ordinal.Equals(checkpoint.TaskId, record.ScopeId),
            OversightScopeKind.Storyline =>
                StringComparer.Ordinal.Equals(record.ScopeId, context.StorylineId) &&
                StringComparer.Ordinal.Equals(checkpoint.RunId, context.RunId),
            OversightScopeKind.Project =>
                StringComparer.Ordinal.Equals(record.ScopeId, context.ProjectId) &&
                StringComparer.Ordinal.Equals(checkpoint.RunId, context.RunId),
            _ => false
        };
}

public sealed record OversightExecutionContext(
    string? ProjectId,
    string? StorylineId,
    string? TaskId,
    string? RunId,
    string? WorkflowRunId);

public sealed record OversightActivationContext(
    string? ProjectId,
    string? StorylineId,
    string? TaskId,
    string? RunId,
    IReadOnlyList<DurableCheckpointRecord> ExecutionCheckpoints,
    long? RunCreatedAtMs = null,
    long? TaskCreatedAtMs = null);

public static class OversightResolver
{
    public static EffectiveOversightPolicy Resolve(
        EffectiveOversightPolicy applicationDefault,
        IReadOnlyList<OversightOverrideRecord> durableOverrides,
        IReadOnlySet<string> existingCheckpointIds,
        string? projectId,
        string? storylineId,
        string? taskId)
    {
        ArgumentNullException.ThrowIfNull(applicationDefault);
        ArgumentNullException.ThrowIfNull(durableOverrides);
        ArgumentNullException.ThrowIfNull(existingCheckpointIds);

        var active = durableOverrides
            .Where(item => item.ScopeKind != OversightScopeKind.Application)
            .Where(item => OversightActivation.IsActive(item, existingCheckpointIds))
            .OrderBy(item => item.CreatedAtMs)
            .ThenBy(item => item.OverrideId, StringComparer.Ordinal)
            .ToArray();

        var task = Match(active, OversightScopeKind.Task, taskId);
        if (task is not null)
        {
            return ToPolicy(task);
        }

        var storyline = Match(active, OversightScopeKind.Storyline, storylineId);
        if (storyline is not null)
        {
            return ToPolicy(storyline);
        }

        var project = Match(active, OversightScopeKind.Project, projectId);
        return project is null ? applicationDefault with { WinningScope = OversightScopeKind.Application } : ToPolicy(project);
    }

    public static EffectiveOversightPolicy Resolve(
        EffectiveOversightPolicy applicationDefault,
        IReadOnlyList<OversightOverrideRecord> durableOverrides,
        OversightActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(applicationDefault);
        ArgumentNullException.ThrowIfNull(durableOverrides);
        ArgumentNullException.ThrowIfNull(context);

        var active = durableOverrides
            .Where(item => item.ScopeKind != OversightScopeKind.Application)
            .Where(item => OversightActivation.IsActiveForExecution(item, context))
            .OrderBy(item => item.CreatedAtMs)
            .ThenBy(item => item.OverrideId, StringComparer.Ordinal)
            .ToArray();

        var task = Match(active, OversightScopeKind.Task, context.TaskId);
        if (task is not null)
        {
            return ToPolicy(task);
        }

        var storyline = Match(active, OversightScopeKind.Storyline, context.StorylineId);
        if (storyline is not null)
        {
            return ToPolicy(storyline);
        }

        var project = Match(active, OversightScopeKind.Project, context.ProjectId);
        return project is null ? applicationDefault with { WinningScope = OversightScopeKind.Application } : ToPolicy(project);
    }

    private static OversightOverrideRecord? Match(
        IReadOnlyList<OversightOverrideRecord> records,
        OversightScopeKind scope,
        string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return null;
        }

        return records.LastOrDefault(item =>
            item.ScopeKind == scope && StringComparer.Ordinal.Equals(item.ScopeId, scopeId));
    }

    private static EffectiveOversightPolicy ToPolicy(OversightOverrideRecord record) =>
        new(
            record.NarrativeAuthority,
            record.RuntimePermission,
            record.ScopeKind,
            record.ScopeId,
            record.OverrideId,
            record.EffectiveAfterCheckpointId,
            true);
}

public enum PendingApprovalReevaluation
{
    StillPending,
    ApprovedDelegated,
    Denied,
    ReplanRequired
}

public sealed record PendingApprovalSnapshot(
    string ApprovalId,
    string ApprovalKind,
    bool NarrativeDecision,
    bool CapabilityAllowed,
    bool GateValid,
    bool InputsFresh,
    bool PlanValid,
    bool ProjectTrusted,
    bool HardDenied,
    EffectiveOversightPolicy Oversight);

public static class PendingApprovalReevaluator
{
    public static PendingApprovalReevaluation Reevaluate(PendingApprovalSnapshot pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.HardDenied || !pending.ProjectTrusted || !pending.CapabilityAllowed)
        {
            return PendingApprovalReevaluation.Denied;
        }

        if (!pending.PlanValid || !pending.GateValid)
        {
            return PendingApprovalReevaluation.ReplanRequired;
        }

        if (!pending.InputsFresh)
        {
            return PendingApprovalReevaluation.StillPending;
        }

        if (pending.NarrativeDecision)
        {
            return pending.Oversight.NarrativeDelegated
                ? PendingApprovalReevaluation.ApprovedDelegated
                : PendingApprovalReevaluation.StillPending;
        }

        return pending.Oversight.RuntimePermission is RuntimePermissionMode.AutoApproveScoped
            or RuntimePermissionMode.BypassPermissions
            ? PendingApprovalReevaluation.ApprovedDelegated
            : PendingApprovalReevaluation.StillPending;
    }
}

public sealed record DelegatedDecisionRecord(
    string DelegatedDecisionId,
    string? TransactionId,
    OversightScopeKind ScopeKind,
    string ScopeId,
    string? ProposedBy,
    string? ConfirmedBy,
    string DecidedBy,
    DecisionAuthorityKind AuthorityKind,
    string OversightMode,
    string? PayloadDigest,
    long DecidedAtMs);

public static class DelegatedDecisionEquality
{
    public static bool Equivalent(DelegatedDecisionRecord left, DelegatedDecisionRecord right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return StringComparer.Ordinal.Equals(left.DelegatedDecisionId, right.DelegatedDecisionId) &&
               StringComparer.Ordinal.Equals(left.TransactionId, right.TransactionId) &&
               left.ScopeKind == right.ScopeKind &&
               StringComparer.Ordinal.Equals(left.ScopeId, right.ScopeId) &&
               left.AuthorityKind == right.AuthorityKind &&
               StringComparer.Ordinal.Equals(left.DecidedBy, right.DecidedBy) &&
               StringComparer.Ordinal.Equals(left.OversightMode, right.OversightMode);
    }
}

public sealed class DelegatedDecisionConflictException : InvalidOperationException
{
    public DelegatedDecisionConflictException(string decisionId)
        : base("delegated-decision-conflict:" + decisionId)
    {
        DecisionId = decisionId;
    }

    public string DecisionId { get; }
}

public enum DelegatedProvenanceWriteResult
{
    Written,
    Equivalent,
    Conflict,
    Unavailable
}

public sealed record FormalAuthorizationSnapshot(
    string DecisionId,
    string? TransactionId,
    DecisionAuthorityKind AuthorityKind,
    OversightScopeKind WinningScope,
    string WinningScopeId,
    NarrativeDecisionAuthority NarrativeAuthority,
    RuntimePermissionMode RuntimePermission,
    string DecidedBy,
    long DecidedAtMs)
{
    public const string EventType = "wp13.delegated_authorization";

    public static FormalAuthorizationSnapshot Capture(
        string decisionId,
        string? transactionId,
        EffectiveOversightPolicy policy,
        string decidedBy,
        long decidedAtMs) =>
        new(
            decisionId,
            transactionId,
            policy.AuthorityKind,
            policy.WinningScope,
            policy.WinningScopeId ?? decisionId,
            policy.NarrativeAuthority,
            policy.RuntimePermission,
            decidedBy,
            decidedAtMs);

    public DelegatedDecisionRecord ToDelegatedDecision() =>
        NarrativeDecisionProvenance.AgentDelegated(
            DecisionId,
            TransactionId,
            WinningScope,
            WinningScopeId,
            DecidedBy,
            new EffectiveOversightPolicy(
                NarrativeAuthority,
                RuntimePermission,
                WinningScope,
                WinningScopeId,
                null,
                null,
                true),
            null,
            DecidedAtMs);

    public string WriteCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("decisionId", DecisionId);
            if (!string.IsNullOrWhiteSpace(TransactionId))
            {
                writer.WriteString("transactionId", TransactionId);
            }

            writer.WriteString("authorityKind", NarrativeDecisionProvenance.AuthorityKindDurable(AuthorityKind));
            writer.WriteString("winningScope", OversightScopeKindCodec.ToDurableValue(WinningScope));
            writer.WriteString("winningScopeId", WinningScopeId);
            writer.WriteString("narrativeAuthority", NarrativeDecisionAuthorityCodec.ToDurableValue(NarrativeAuthority));
            writer.WriteString("runtimePermission", RuntimePermissionModeDurableCodec.ToDurableValue(RuntimePermission));
            writer.WriteString("decidedBy", DecidedBy);
            writer.WriteNumber("decidedAtMs", DecidedAtMs);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static FormalAuthorizationSnapshot? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !OversightScopeKindCodec.TryParse(root.GetProperty("winningScope").GetString(), out var scope) ||
                !NarrativeDecisionAuthorityCodec.TryParse(root.GetProperty("narrativeAuthority").GetString(), out var narrative) ||
                !RuntimePermissionModeDurableCodec.TryParse(root.GetProperty("runtimePermission").GetString(), out var permission))
            {
                return null;
            }

            var authority = StringComparer.Ordinal.Equals(root.GetProperty("authorityKind").GetString(), "AGENT_DELEGATED")
                ? DecisionAuthorityKind.AgentDelegated
                : DecisionAuthorityKind.AuthorConfirmed;
            return new FormalAuthorizationSnapshot(
                root.GetProperty("decisionId").GetString() ?? "",
                root.TryGetProperty("transactionId", out var tx) ? tx.GetString() : null,
                authority,
                scope,
                root.GetProperty("winningScopeId").GetString() ?? "",
                narrative,
                permission,
                root.GetProperty("decidedBy").GetString() ?? "",
                root.TryGetProperty("decidedAtMs", out var at) ? at.GetInt64() : 0);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

public static class NarrativeDecisionProvenance
{
    public static DelegatedDecisionRecord AuthorConfirmed(
        string id,
        string? transactionId,
        OversightScopeKind scopeKind,
        string scopeId,
        string proposedBy,
        string confirmedBy,
        EffectiveOversightPolicy oversight,
        string? payloadDigest,
        long decidedAtMs) =>
        new(
            id,
            transactionId,
            scopeKind,
            scopeId,
            proposedBy,
            confirmedBy,
            confirmedBy,
            DecisionAuthorityKind.AuthorConfirmed,
            FormatOversightMode(oversight),
            payloadDigest,
            decidedAtMs);

    public static DelegatedDecisionRecord AgentDelegated(
        string id,
        string? transactionId,
        OversightScopeKind scopeKind,
        string scopeId,
        string decidedBy,
        EffectiveOversightPolicy oversight,
        string? payloadDigest,
        long decidedAtMs) =>
        new(
            id,
            transactionId,
            scopeKind,
            scopeId,
            decidedBy,
            null,
            decidedBy,
            DecisionAuthorityKind.AgentDelegated,
            FormatOversightMode(oversight),
            payloadDigest,
            decidedAtMs);

    public static string FormatOversightMode(EffectiveOversightPolicy policy) =>
        NarrativeDecisionAuthorityCodec.ToDurableValue(policy.NarrativeAuthority) + "/" +
        RuntimePermissionModeDurableCodec.ToDurableValue(policy.RuntimePermission);

    public static string AuthorityKindDurable(DecisionAuthorityKind kind) => kind switch
    {
        DecisionAuthorityKind.AuthorConfirmed => "AUTHOR_CONFIRMED",
        DecisionAuthorityKind.AgentDelegated => "AGENT_DELEGATED",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

public sealed record ProvenanceTrace(
    string? NarrativeDecisionId,
    string? ChangeSetId,
    string? ResultArtifactId,
    string? TaskId,
    string? RunId,
    string? ApprovedPlanRef,
    string? OriginalUserRequestRef);

public static class ProvenanceTraceResolver
{
    public static ProvenanceTrace Resolve(
        DelegatedDecisionRecord? decision,
        ResultProvenanceV1? resultProvenance,
        string? resultArtifactId,
        string? taskId,
        string? runId) =>
        new(
            decision?.DelegatedDecisionId,
            resultProvenance?.ChangeSetId ?? decision?.TransactionId,
            resultArtifactId,
            resultProvenance?.ProducedByTaskId ?? taskId,
            resultProvenance?.ProducedByRunId ?? runId,
            resultProvenance?.ApprovedPlanRef,
            resultProvenance?.OriginalUserRequestRef);
}
