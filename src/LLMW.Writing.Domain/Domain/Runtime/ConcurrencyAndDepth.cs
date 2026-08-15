namespace LLMW.Writing.Domain.Runtime;

/// <summary>
/// Root Run depth = 0. Child depth = parent.depth + 1. Maximum allowed depth = 4.
/// Spawn from depth 4 is denied. Illegal requested depths are rejected, never clamped.
/// </summary>
public static class DelegationDepth
{
    public const int RootDepth = 0;
    public const int MaximumDepth = 4;

    public static int ChildDepth(int parentDepth)
    {
        if (parentDepth < RootDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(parentDepth), parentDepth, "Parent depth must be >= 0.");
        }

        return parentDepth + 1;
    }

    public static bool CanSpawnFrom(int parentDepth) =>
        parentDepth is >= RootDepth and < MaximumDepth;

    public static SpawnDecision EvaluateRequestedDepth(int parentDepth, int? requestedChildDepth)
    {
        if (!CanSpawnFrom(parentDepth))
        {
            return SpawnDecision.Denied(SpawnDenialReason.DepthLimit, ChildDepth(parentDepth));
        }

        var derived = ChildDepth(parentDepth);
        if (requestedChildDepth is int requested && requested != derived)
        {
            return SpawnDecision.Denied(SpawnDenialReason.DepthSpoof, requested);
        }

        return SpawnDecision.Allow(derived);
    }
}

/// <summary>
/// Default/hard ceiling is 4. Effective budget may adapt downward in 1..ConfiguredMax.
/// The entire Task Tree shares this budget; a child Run does not receive a fresh independent budget.
/// Higher <c>tasks.priority</c> is scheduled first.
/// </summary>
public sealed class ConcurrencyBudget
{
    public const int ConfiguredMax = 4;

    public ConcurrencyBudget(int effective)
    {
        if (effective < 1 || effective > ConfiguredMax)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effective),
                effective,
                $"Effective concurrency budget must be in 1..{ConfiguredMax}.");
        }

        Effective = effective;
    }

    public int Effective { get; }

    public static ConcurrencyBudget Default { get; } = new(ConfiguredMax);

    public static ConcurrencyBudget FromEffective(int requested) =>
        new(Math.Clamp(requested, 1, ConfiguredMax));
}

public interface IConcurrencyBudgetPolicy
{
    ConcurrencyBudget Current { get; }
}

public sealed class FixedConcurrencyBudgetPolicy : IConcurrencyBudgetPolicy
{
    public FixedConcurrencyBudgetPolicy(ConcurrencyBudget budget)
    {
        Current = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public ConcurrencyBudget Current { get; }
}

public sealed class MutableConcurrencyBudgetPolicy : IConcurrencyBudgetPolicy
{
    private ConcurrencyBudget current;

    public MutableConcurrencyBudgetPolicy(ConcurrencyBudget? budget = null)
    {
        current = budget ?? ConcurrencyBudget.Default;
    }

    public ConcurrencyBudget Current
    {
        get => current;
        set => current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void SetEffective(int effective) => current = ConcurrencyBudget.FromEffective(effective);
}

public sealed record SpawnDecision(
    SpawnOutcomeKind Outcome,
    SpawnDenialReason Denial,
    int? DerivedDepth)
{
    public static SpawnDecision Allow(int derivedDepth) =>
        new(SpawnOutcomeKind.Allowed, SpawnDenialReason.None, derivedDepth);

    public static SpawnDecision Queue(int derivedDepth) =>
        new(SpawnOutcomeKind.Queued, SpawnDenialReason.None, derivedDepth);

    public static SpawnDecision Denied(SpawnDenialReason reason, int? derivedDepth = null) =>
        new(SpawnOutcomeKind.Denied, reason, derivedDepth);
}

public static class SpawnPolicy
{
    public static SpawnDecision Evaluate(
        bool agentSpawnAllowed,
        bool parentCancelled,
        int parentDepth,
        int? requestedChildDepth,
        int activeInTree,
        ConcurrencyBudget budget,
        bool unknownSideEffect)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (unknownSideEffect)
        {
            return SpawnDecision.Denied(SpawnDenialReason.UnknownSideEffect);
        }

        if (parentCancelled)
        {
            return SpawnDecision.Denied(SpawnDenialReason.Cancelled);
        }

        if (!agentSpawnAllowed)
        {
            return SpawnDecision.Denied(SpawnDenialReason.AgentSpawnDenied);
        }

        var depth = DelegationDepth.EvaluateRequestedDepth(parentDepth, requestedChildDepth);
        if (depth.Outcome == SpawnOutcomeKind.Denied)
        {
            return depth;
        }

        if (activeInTree >= budget.Effective)
        {
            return SpawnDecision.Queue(depth.DerivedDepth!.Value);
        }

        return SpawnDecision.Allow(depth.DerivedDepth!.Value);
    }
}
