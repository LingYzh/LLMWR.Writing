using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Authority.Recovery;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Recovery;

public sealed class ProjectRecoveryCoordinator
{
    private readonly IAuthorityTransactionCoordinator transactions;
    private readonly IChapterSubmissionRecoveryStore submissions;
    private readonly IRecoveryFaultInjector faultInjector;
    private readonly IAuthorizationService authorizationService;

    public ProjectRecoveryCoordinator(
        IAuthorityTransactionCoordinator transactions,
        IChapterSubmissionRecoveryStore submissions,
        IRecoveryFaultInjector? faultInjector = null,
        IAuthorizationService? authorizationService = null)
    {
        this.transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        this.submissions = submissions ?? throw new ArgumentNullException(nameof(submissions));
        this.faultInjector = faultInjector ?? NoOpRecoveryFaultInjector.Instance;
        this.authorizationService = authorizationService ?? DenyAllAuthorizationService.Instance;
    }

    public ProjectRecoveryReport RecoverStartup(CancellationToken cancellationToken = default)
    {
        var durableBeforeRecovery = submissions.LoadIncomplete();
        faultInjector.Inject(ProjectRecoveryFaultPoint.BeforeTransactionRecovery);
        transactions.RecoverIncomplete(cancellationToken);
        faultInjector.Inject(ProjectRecoveryFaultPoint.AfterTransactionRecovery);

        List<ProjectRecoveryItem> items = [];
        foreach (var original in durableBeforeRecovery)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var durable = submissions.Load(original.TransactionId) ?? original;
            var plan = ChapterSubmissionRecoveryPolicy.Derive(durable);

            faultInjector.Inject(ProjectRecoveryFaultPoint.BeforeWorkflowRehydrate);
            ApplyStartupPlan(durable, plan);
            faultInjector.Inject(ProjectRecoveryFaultPoint.AfterWorkflowRehydrate);
            items.Add(ToItem(durable, plan));
        }

        return new ProjectRecoveryReport(Overall(items), items);
    }

    public RecoveryDecisionResult ApplyDecision(
        string transactionId,
        ChapterSubmissionRecoveryAction action,
        CallerPrincipal? principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        var capability = action == ChapterSubmissionRecoveryAction.ResumeAcceptance
            ? Capability.AuthorityAccept
            : Capability.AuthoritySubmit;
        var authorization = authorizationService.Authorize(principal, new AuthorizationRequest(capability));
        if (!authorization.IsAllowed)
        {
            return new RecoveryDecisionResult(false, null, "RECOVERY_DECISION_DENIED");
        }

        var durable = submissions.Load(transactionId);
        if (durable is null)
        {
            return new RecoveryDecisionResult(false, null, "RECOVERY_SUBMISSION_NOT_FOUND");
        }

        var plan = ChapterSubmissionRecoveryPolicy.Derive(durable);
        var transition = ChapterSubmissionRecoveryPolicy.Transition(plan, action);
        if (!transition.Allowed)
        {
            return new RecoveryDecisionResult(false, ToItem(durable, plan), transition.RejectionCode);
        }

        if (action == ChapterSubmissionRecoveryAction.Cancel)
        {
            submissions.CancelPreCommit(durable);
            return new RecoveryDecisionResult(true, ToItem(durable, transition.Plan));
        }

        submissions.RehydratePreCommit(durable, plan);
        return new RecoveryDecisionResult(true, ToItem(durable, plan));
    }

    private void ApplyStartupPlan(
        DurableChapterSubmissionState durable,
        ChapterSubmissionRecoveryPlan plan)
    {
        switch (plan.Classification)
        {
            case RecoveryClassification.AuthorityCommittedRollForward:
                if (durable.TransactionState == RecoveryTransactionState.Complete &&
                    durable.MaterializationComplete)
                {
                    submissions.FinalizeCommittedRollForward(durable);
                }

                return;
            case RecoveryClassification.RecoveryRequired:
                submissions.MarkRecoveryRequired(durable, plan.Reason);
                return;
            case RecoveryClassification.AutoRecoverable when durable.CandidateId is null || !plan.HoldsSubmissionLock:
                submissions.ReleaseOrphanedPreCommit(durable);
                return;
            case RecoveryClassification.AutoRecoverable:
            case RecoveryClassification.UserActionRequired:
                submissions.RehydratePreCommit(durable, plan);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(plan), plan.Classification, null);
        }
    }

    private static ProjectRecoveryItem ToItem(
        DurableChapterSubmissionState durable,
        ChapterSubmissionRecoveryPlan plan) =>
        new(
            durable.TransactionId,
            durable.CandidateId,
            plan.Classification,
            plan.Classification.ToStableName(),
            plan.HoldsSubmissionLock,
            plan.AllowedActions,
            plan.Reason);

    private static RecoveryClassification Overall(IReadOnlyList<ProjectRecoveryItem> items)
    {
        if (items.Any(item => item.Classification == RecoveryClassification.RecoveryRequired))
        {
            return RecoveryClassification.RecoveryRequired;
        }

        if (items.Any(item => item.Classification == RecoveryClassification.UserActionRequired))
        {
            return RecoveryClassification.UserActionRequired;
        }

        if (items.Any(item => item.Classification == RecoveryClassification.AuthorityCommittedRollForward))
        {
            return RecoveryClassification.AuthorityCommittedRollForward;
        }

        return RecoveryClassification.AutoRecoverable;
    }
}
