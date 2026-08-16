using LLMW.Writing.Domain.Provider;

namespace LLMW.Writing.Application.Provider;

public sealed record TaskCertificationEvaluationProfile(
    string TaskClass,
    string EvaluationSuiteVersion,
    IReadOnlyList<TaskEvalThreshold> Thresholds,
    ReasoningCeiling MaxAllowedCeiling)
{
    public bool ScoresPass(IReadOnlyList<TaskEvalScore> scores)
    {
        foreach (var threshold in Thresholds.Where(item => item.MustPass))
        {
            var score = scores.FirstOrDefault(item =>
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

    public bool CandidatePolicyMatches(IReadOnlyList<TaskEvalThreshold> candidateThresholds)
    {
        if (candidateThresholds.Count == 0)
        {
            return true;
        }

        foreach (var required in Thresholds.Where(item => item.MustPass))
        {
            var supplied = candidateThresholds.FirstOrDefault(item =>
                string.Equals(item.MetricName, required.MetricName, StringComparison.Ordinal));
            if (supplied is null)
            {
                return false;
            }

            if (supplied.Comparator != required.Comparator ||
                supplied.Bound != required.Bound ||
                supplied.MustPass != required.MustPass)
            {
                return false;
            }
        }

        return true;
    }

    public bool HasRequiredScores(IReadOnlyList<TaskEvalScore> scores)
    {
        foreach (var threshold in Thresholds.Where(item => item.MustPass))
        {
            if (!scores.Any(item => string.Equals(item.MetricName, threshold.MetricName, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }
}

public static class TaskCertificationPolicies
{
    public static TaskCertificationEvaluationProfile RootConflictV1 { get; } = new(
        TaskCapabilityCertification.RootConflictTaskClass,
        TaskCapabilityCertification.CurrentEvaluationSuiteVersion,
        [
            TaskEvalThreshold.AtLeast(RootConflictMetrics.RootRecall, 0.9m),
            TaskEvalThreshold.AtMost(RootConflictMetrics.FalseMergeRate, 0.05m),
            TaskEvalThreshold.AtLeast(RootConflictMetrics.EvidenceFidelity, 0.9m),
            TaskEvalThreshold.AtLeast(RootConflictMetrics.PropagationAccuracy, 0.9m),
            TaskEvalThreshold.AtLeast(RootConflictMetrics.RecomputeAccuracy, 0.9m),
            TaskEvalThreshold.AtLeast(RootConflictMetrics.AbstentionQuality, 0.8m)
        ],
        ReasoningCeiling.Adaptive);

    public static TaskCertificationEvaluationProfile? For(IReadOnlyList<string> taskClasses, string evaluationSuiteVersion)
    {
        if (taskClasses.Any(item => string.Equals(item, RootConflictV1.TaskClass, StringComparison.Ordinal)) &&
            string.Equals(evaluationSuiteVersion, RootConflictV1.EvaluationSuiteVersion, StringComparison.Ordinal))
        {
            return RootConflictV1;
        }

        return null;
    }
}
