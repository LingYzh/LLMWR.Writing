namespace LLMW.Writing.Domain.Runtime;

public enum ResultDependencyKind
{
    Required,
    Advisory,
    Optional
}

public enum ResultDependencyStatus
{
    Missing,
    Current,
    Stale,
    Invalid,
    Warning
}

public static class ResultDependencyKindCodec
{
    public const string Required = StructuralReadiness.RequiredKind;
    public const string Advisory = "advisory";
    public const string Optional = "optional";

    public static string ToDurableValue(ResultDependencyKind kind) => kind switch
    {
        ResultDependencyKind.Required => Required,
        ResultDependencyKind.Advisory => Advisory,
        ResultDependencyKind.Optional => Optional,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParse(string? value, out ResultDependencyKind kind)
    {
        kind = value switch
        {
            Required => ResultDependencyKind.Required,
            Advisory => ResultDependencyKind.Advisory,
            Optional => ResultDependencyKind.Optional,
            _ => default
        };
        return value is Required or Advisory or Optional;
    }

    public static bool IsRequired(string? kind) =>
        StringComparer.Ordinal.Equals(kind, Required);
}

public static class ResultDependencyStatusCodec
{
    public const string Missing = "missing";
    public const string Current = "current";
    public const string Satisfied = StructuralReadiness.SatisfiedStatus;
    public const string Stale = "stale";
    public const string Invalid = "invalid";
    public const string Warning = "warning";
    public const string Unsatisfied = "unsatisfied";

    public static string ToDurableValue(ResultDependencyStatus status) => status switch
    {
        ResultDependencyStatus.Missing => Missing,
        ResultDependencyStatus.Current => Current,
        ResultDependencyStatus.Stale => Stale,
        ResultDependencyStatus.Invalid => Invalid,
        ResultDependencyStatus.Warning => Warning,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string? value, out ResultDependencyStatus status)
    {
        status = value switch
        {
            Missing or Unsatisfied => ResultDependencyStatus.Missing,
            Current or Satisfied => ResultDependencyStatus.Current,
            Stale => ResultDependencyStatus.Stale,
            Invalid => ResultDependencyStatus.Invalid,
            Warning => ResultDependencyStatus.Warning,
            _ => default
        };
        return value is Missing or Unsatisfied or Current or Satisfied or Stale or Invalid or Warning;
    }

    public static bool IsCurrent(string? status) =>
        StringComparer.Ordinal.Equals(status, Current) ||
        StringComparer.Ordinal.Equals(status, Satisfied);
}

public sealed record ResultDependencyEvaluation(
    bool BlocksDispatch,
    bool BlocksCompletion,
    bool HasWarning,
    ResultDependencyStatus EffectiveStatus);

public static class ResultDependencyPolicy
{
    public static bool HardBlocks(DurableDependencyRecord dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (!ResultDependencyKindCodec.IsRequired(dependency.DependencyKind))
        {
            return false;
        }

        return !ResultDependencyStatusCodec.IsCurrent(dependency.Status);
    }

    public static ResultDependencyEvaluation Evaluate(DurableDependencyRecord dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (!ResultDependencyKindCodec.TryParse(dependency.DependencyKind, out var kind))
        {
            return new ResultDependencyEvaluation(true, true, false, ResultDependencyStatus.Invalid);
        }

        if (!ResultDependencyStatusCodec.TryParse(dependency.Status, out var status))
        {
            status = ResultDependencyStatus.Invalid;
        }

        var required = kind == ResultDependencyKind.Required;
        var notCurrent = status is not ResultDependencyStatus.Current;
        var warning = kind is ResultDependencyKind.Advisory &&
                      status is ResultDependencyStatus.Stale or ResultDependencyStatus.Missing or ResultDependencyStatus.Warning;
        return new ResultDependencyEvaluation(
            required && notCurrent,
            required && notCurrent,
            warning || (kind == ResultDependencyKind.Advisory && notCurrent),
            status);
    }

    public static ResultDependencyStatus Recompute(
        ResultDependencyKind kind,
        string? resultArtifactId,
        ResultFreshnessState? freshness,
        bool artifactValid,
        bool producerFormallyCompleted = true)
    {
        if (!producerFormallyCompleted)
        {
            if (kind == ResultDependencyKind.Required)
            {
                return ResultDependencyStatus.Missing;
            }

            if (kind == ResultDependencyKind.Advisory)
            {
                return ResultDependencyStatus.Warning;
            }

            return string.IsNullOrWhiteSpace(resultArtifactId)
                ? ResultDependencyStatus.Missing
                : ResultDependencyStatus.Stale;
        }

        if (string.IsNullOrWhiteSpace(resultArtifactId))
        {
            return kind == ResultDependencyKind.Advisory ? ResultDependencyStatus.Warning : ResultDependencyStatus.Missing;
        }

        if (!artifactValid)
        {
            return ResultDependencyStatus.Invalid;
        }

        return freshness switch
        {
            ResultFreshnessState.Current => ResultDependencyStatus.Current,
            ResultFreshnessState.Stale when kind == ResultDependencyKind.Required => ResultDependencyStatus.Stale,
            ResultFreshnessState.NeedsRevalidation when kind == ResultDependencyKind.Required => ResultDependencyStatus.Stale,
            ResultFreshnessState.Stale or ResultFreshnessState.NeedsRevalidation when kind == ResultDependencyKind.Advisory =>
                ResultDependencyStatus.Warning,
            ResultFreshnessState.Stale or ResultFreshnessState.NeedsRevalidation => ResultDependencyStatus.Stale,
            _ => ResultDependencyStatus.Missing
        };
    }

    public static bool ProposalMutatesEffectiveEdge => false;
}

public static class ResultDependencyGraph
{
    public static int Bound(IReadOnlyList<DurableDependencyRecord> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            ids.Add(dependency.ProducerTaskId);
            ids.Add(dependency.ConsumerTaskId);
        }

        return Math.Max(1, ids.Count + dependencies.Count);
    }
}
