using LLMW.Writing.Domain.Authority.Recovery;

namespace LLMW.Writing.Application.Recovery;

public sealed record ProjectRecoveryItem(
    string TransactionId,
    string? CandidateId,
    RecoveryClassification Classification,
    string StableClassification,
    bool HoldsSubmissionLock,
    IReadOnlySet<ChapterSubmissionRecoveryAction> AllowedActions,
    string Reason);

public sealed record ProjectRecoveryReport(
    RecoveryClassification OverallClassification,
    IReadOnlyList<ProjectRecoveryItem> Items)
{
    public string StableClassification => OverallClassification.ToStableName();

    public bool AuthorityReadOnly => OverallClassification == RecoveryClassification.RecoveryRequired;
}

public sealed record RecoveryDecisionResult(
    bool Succeeded,
    ProjectRecoveryItem? Item,
    string? ErrorCode = null);

public interface IChapterSubmissionRecoveryStore
{
    IReadOnlyList<DurableChapterSubmissionState> LoadIncomplete();

    DurableChapterSubmissionState? Load(string transactionId);

    void RehydratePreCommit(
        DurableChapterSubmissionState state,
        ChapterSubmissionRecoveryPlan plan);

    void ReleaseOrphanedPreCommit(DurableChapterSubmissionState state);

    void FinalizeCommittedRollForward(DurableChapterSubmissionState state);

    void CancelPreCommit(DurableChapterSubmissionState state);

    void MarkRecoveryRequired(DurableChapterSubmissionState state, string reason);
}

public interface IRecoveryFaultInjector
{
    void Inject(ProjectRecoveryFaultPoint point);
}

public enum ProjectRecoveryFaultPoint
{
    BeforeTransactionRecovery,
    AfterTransactionRecovery,
    BeforeWorkflowRehydrate,
    AfterWorkflowRehydrate
}

public sealed class NoOpRecoveryFaultInjector : IRecoveryFaultInjector
{
    public static NoOpRecoveryFaultInjector Instance { get; } = new();

    private NoOpRecoveryFaultInjector()
    {
    }

    public void Inject(ProjectRecoveryFaultPoint point)
    {
    }
}
