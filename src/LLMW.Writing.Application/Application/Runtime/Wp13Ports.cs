using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Runtime;

namespace LLMW.Writing.Application.Runtime;

public interface ISemanticCompletionEvaluator
{
    SemanticCompletionOutcome? Evaluate(TaskCompletionContractV1 contract, TaskResultArtifactV1 artifact);
}

public sealed class UnavailableSemanticCompletionEvaluator : ISemanticCompletionEvaluator
{
    public static UnavailableSemanticCompletionEvaluator Instance { get; } = new();

    private UnavailableSemanticCompletionEvaluator()
    {
    }

    public SemanticCompletionOutcome? Evaluate(TaskCompletionContractV1 contract, TaskResultArtifactV1 artifact)
    {
        _ = contract;
        _ = artifact;
        return null;
    }
}

public interface IApplicationOversightDefaults
{
    EffectiveOversightPolicy Current { get; }

    void Replace(EffectiveOversightPolicy policy);
}

public sealed class MemoryApplicationOversightDefaults : IApplicationOversightDefaults
{
    private readonly object gate = new();
    private EffectiveOversightPolicy current = EffectiveOversightPolicy.ApplicationDefault;

    public EffectiveOversightPolicy Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Replace(EffectiveOversightPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (gate)
        {
            current = policy;
        }
    }
}

public interface IUserSpecialistProfileStore
{
    IReadOnlyList<DurableProjectSpecialistRecord> List();

    DurableProjectSpecialistRecord? Find(string profileId);

    void Upsert(DurableProjectSpecialistRecord record);
}

public sealed class MemoryUserSpecialistProfileStore : IUserSpecialistProfileStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, DurableProjectSpecialistRecord> items = new(StringComparer.Ordinal);

    public IReadOnlyList<DurableProjectSpecialistRecord> List()
    {
        lock (gate)
        {
            return items.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        }
    }

    public DurableProjectSpecialistRecord? Find(string profileId)
    {
        lock (gate)
        {
            return items.TryGetValue(profileId, out var record) ? record : null;
        }
    }

    public void Upsert(DurableProjectSpecialistRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            items[record.SpecialistProfileId] = record;
        }
    }
}

public interface IBuiltInSpecialistCatalog
{
    IReadOnlyList<SpecialistProfileDefinitionV1> List();

    SpecialistProfileDefinitionV1? Find(string profileId);
}

public sealed class SyntheticBuiltInSpecialistCatalog : IBuiltInSpecialistCatalog
{
    public static SyntheticBuiltInSpecialistCatalog Instance { get; } = new();

    private readonly SpecialistProfileDefinitionV1 reviewer = new(
        SpecialistProfileDefinitionV1.CurrentSchemaVersion,
        "builtin.reviewer",
        "builtin-reviewer",
        "Built-in Reviewer",
        "Synthetic WP13 built-in used for immutability tests.",
        1,
        SpecialistScopeKind.BuiltIn,
        ["review"],
        ["review"],
        ["draft"],
        ["review-chapter"],
        true,
        "Review the supplied Result Artifact. This prompt is data for a future compiler.",
        ["review findings"],
        ["canon mutation"],
        false,
        false,
        new SpecialistInputContractV1(["upstream-result"], [], []),
        ["conclusion", "findings"],
        TaskCompletionContractV1.Empty,
        ["Authority.Review"],
        null,
        true,
        null,
        null,
        null);

    public IReadOnlyList<SpecialistProfileDefinitionV1> List() => [reviewer];

    public SpecialistProfileDefinitionV1? Find(string profileId) =>
        StringComparer.Ordinal.Equals(profileId, reviewer.ProfileId) ? reviewer : null;
}

public interface IOversightCheckpointListener
{
    void OnSafeCheckpoint(string checkpointId, string runId, string? taskId, long createdAtMs);
}

public interface IEffectiveOversightSource
{
    EffectiveOversightPolicy Resolve(string? projectId, string? storylineId, string? taskId);

    EffectiveOversightPolicy ResolveForPrincipal(CallerPrincipal? principal, string? taskId = null, string? storylineId = null) =>
        Resolve(principal?.ProjectScope?.ProjectId.ToString("D"), storylineId, taskId);
}

public sealed class LateBoundOversightSource : IEffectiveOversightSource
{
    public IEffectiveOversightSource? Inner { get; set; }

    public EffectiveOversightPolicy Resolve(string? projectId, string? storylineId, string? taskId) =>
        (Inner ?? FailClosedOversightSource.Instance).Resolve(projectId, storylineId, taskId);

    public EffectiveOversightPolicy ResolveForPrincipal(CallerPrincipal? principal, string? taskId = null, string? storylineId = null) =>
        Inner is null
            ? ((IEffectiveOversightSource)FailClosedOversightSource.Instance).ResolveForPrincipal(principal, taskId, storylineId)
            : Inner.ResolveForPrincipal(principal, taskId, storylineId);
}

public sealed class FailClosedOversightSource : IEffectiveOversightSource
{
    public static FailClosedOversightSource Instance { get; } = new();

    private FailClosedOversightSource()
    {
    }

    public EffectiveOversightPolicy Resolve(string? projectId, string? storylineId, string? taskId)
    {
        _ = projectId;
        _ = storylineId;
        _ = taskId;
        return EffectiveOversightPolicy.ApplicationDefault;
    }
}

public interface IDelegatedDecisionSink
{
    DelegatedProvenanceWriteResult Record(DelegatedDecisionRecord record);
}

public sealed class NullDelegatedDecisionSink : IDelegatedDecisionSink
{
    public static NullDelegatedDecisionSink Instance { get; } = new();

    private NullDelegatedDecisionSink()
    {
    }

    public DelegatedProvenanceWriteResult Record(DelegatedDecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return DelegatedProvenanceWriteResult.Written;
    }
}

public sealed record ApprovalSafetyFacts(
    bool CapabilityAllowed,
    bool ProjectTrusted,
    bool HardDenied,
    bool GateValid,
    bool PlanValid,
    bool InputsFresh);

public interface IPendingApprovalSafetyEvaluator
{
    ApprovalSafetyFacts Evaluate(DurableApprovalRecord approval, EffectiveOversightPolicy oversight);
}

public sealed class ProductionPendingApprovalSafetyEvaluator : IPendingApprovalSafetyEvaluator
{
    private readonly Func<DurableApprovalRecord, EffectiveOversightPolicy, ApprovalSafetyFacts> evaluate;

    public ProductionPendingApprovalSafetyEvaluator(
        Func<DurableApprovalRecord, EffectiveOversightPolicy, ApprovalSafetyFacts>? evaluate = null)
    {
        this.evaluate = evaluate ?? ((_, _) => new ApprovalSafetyFacts(
            CapabilityAllowed: false,
            ProjectTrusted: false,
            HardDenied: false,
            GateValid: false,
            PlanValid: false,
            InputsFresh: false));
    }

    public static ProductionPendingApprovalSafetyEvaluator Unavailable { get; } = new();

    public ApprovalSafetyFacts Evaluate(DurableApprovalRecord approval, EffectiveOversightPolicy oversight) =>
        evaluate(approval, oversight);
}

public sealed record RuntimeGrillSafetyFacts(
    bool CapabilityAllowed,
    bool InsideApprovedPlan,
    bool TaskScopeUnchanged,
    bool InputsFresh);

public interface IRuntimeGrillSafetyEvaluator
{
    RuntimeGrillSafetyFacts Evaluate(
        RuntimeGrillDecisionRequestV1 request,
        CallerPrincipal? principal,
        EffectiveOversightPolicy oversight);
}

public sealed class FailClosedRuntimeGrillSafetyEvaluator : IRuntimeGrillSafetyEvaluator
{
    public static FailClosedRuntimeGrillSafetyEvaluator Instance { get; } = new();

    private FailClosedRuntimeGrillSafetyEvaluator()
    {
    }

    public RuntimeGrillSafetyFacts Evaluate(
        RuntimeGrillDecisionRequestV1 request,
        CallerPrincipal? principal,
        EffectiveOversightPolicy oversight)
    {
        _ = request;
        _ = principal;
        _ = oversight;
        return new RuntimeGrillSafetyFacts(false, false, false, false);
    }
}

public sealed record ToolCallCancellationOutcome(bool Available, bool Cancelled, string? Detail);

public interface IToolCallCancellationPort
{
    ToolCallCancellationOutcome Cancel(string toolCallId);
}

public sealed class UnavailableToolCallCancellationPort : IToolCallCancellationPort
{
    public static UnavailableToolCallCancellationPort Instance { get; } = new();

    private UnavailableToolCallCancellationPort()
    {
    }

    public ToolCallCancellationOutcome Cancel(string toolCallId)
    {
        _ = toolCallId;
        return new ToolCallCancellationOutcome(false, false, "unavailable");
    }
}

public sealed class ConfirmingToolCallCancellationPort : IToolCallCancellationPort
{
    private readonly Func<string, bool> cancel;

    public ConfirmingToolCallCancellationPort(Func<string, bool> cancel) =>
        this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));

    public ToolCallCancellationOutcome Cancel(string toolCallId)
    {
        var cancelled = cancel(toolCallId);
        return cancelled
            ? new ToolCallCancellationOutcome(true, true, null)
            : new ToolCallCancellationOutcome(true, false, "not-found");
    }
}

public sealed record TaskCompletionOutcome(
    string Outcome,
    string? ResultArtifactId,
    IReadOnlyList<string> Failures);

public sealed record SpecialistMutationOutcome(string ProfileId, IReadOnlyList<string> ValidationErrors);

public sealed record RuntimeGrillPauseOutcome(string ApprovalId, string Status);

public sealed record RuntimeGrillResolveOutcome(string Status, string Resolution, string? ResumeDecision);
