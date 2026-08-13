using static LLMW.Writing.Domain.Authority.TransitionResults;

namespace LLMW.Writing.Domain.Authority.ProjectSubmission;

public enum ProjectSubmissionState
{
    Idle,
    Submitting,
    Reviewing,
    Resolving,
    Accepting,
    Committing,
    Revalidating
}

public enum ProjectSubmissionEvent
{
    Submit,
    CandidatePersisted,
    ReviewPassed,
    ReviewFailed,
    Cancel,
    BeginAcceptance,
    BeginCommit,
    CommitCompleted,
    RevalidationCompleted
}

public enum SubmissionEligibility
{
    None,
    Normal,
    HistoricalRevision
}

public sealed record ProjectSubmissionContext(
    SubmissionEligibility Eligibility,
    bool ActiveSubmissionExists,
    AcceptanceDecisionContext AcceptanceDecision)
{
    public static ProjectSubmissionContext Empty { get; } =
        new(SubmissionEligibility.None, false, AcceptanceDecisionContext.Unauthorized);
}

public static class ProjectSubmissionStateMachine
{
    public static StateMachine<ProjectSubmissionState, ProjectSubmissionEvent, ProjectSubmissionContext> Instance { get; } =
        new(Transition);

    private static TransitionResult<ProjectSubmissionState> Transition(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event,
        ProjectSubmissionContext context)
        => state switch
        {
            ProjectSubmissionState.Idle => FromIdle(state, @event, context),
            ProjectSubmissionState.Submitting => FromSubmitting(state, @event),
            ProjectSubmissionState.Reviewing => FromReviewing(state, @event),
            ProjectSubmissionState.Resolving => FromResolving(state, @event, context),
            ProjectSubmissionState.Accepting => FromAccepting(state, @event),
            ProjectSubmissionState.Committing => FromCommitting(state, @event),
            ProjectSubmissionState.Revalidating => FromRevalidating(state, @event),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

    private static TransitionResult<ProjectSubmissionState> FromIdle(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event,
        ProjectSubmissionContext context)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => Submit(state, context),
            ProjectSubmissionEvent.CandidatePersisted => Illegal(state),
            ProjectSubmissionEvent.ReviewPassed => Illegal(state),
            ProjectSubmissionEvent.ReviewFailed => Illegal(state),
            ProjectSubmissionEvent.Cancel => Illegal(state),
            ProjectSubmissionEvent.BeginAcceptance => Illegal(state),
            ProjectSubmissionEvent.BeginCommit => Illegal(state),
            ProjectSubmissionEvent.CommitCompleted => Illegal(state),
            ProjectSubmissionEvent.RevalidationCompleted => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ProjectSubmissionState> FromSubmitting(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => Illegal(state),
            ProjectSubmissionEvent.CandidatePersisted => Legal(state, ProjectSubmissionState.Reviewing),
            ProjectSubmissionEvent.ReviewPassed => Illegal(state),
            ProjectSubmissionEvent.ReviewFailed => Illegal(state),
            ProjectSubmissionEvent.Cancel => Illegal(state),
            ProjectSubmissionEvent.BeginAcceptance => Illegal(state),
            ProjectSubmissionEvent.BeginCommit => Illegal(state),
            ProjectSubmissionEvent.CommitCompleted => Illegal(state),
            ProjectSubmissionEvent.RevalidationCompleted => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ProjectSubmissionState> FromReviewing(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => Illegal(state),
            ProjectSubmissionEvent.CandidatePersisted => Illegal(state),
            ProjectSubmissionEvent.ReviewPassed => Legal(state, ProjectSubmissionState.Resolving),
            ProjectSubmissionEvent.ReviewFailed => Legal(state, ProjectSubmissionState.Idle),
            ProjectSubmissionEvent.Cancel => Legal(state, ProjectSubmissionState.Idle),
            ProjectSubmissionEvent.BeginAcceptance => Illegal(state),
            ProjectSubmissionEvent.BeginCommit => Illegal(state),
            ProjectSubmissionEvent.CommitCompleted => Illegal(state),
            ProjectSubmissionEvent.RevalidationCompleted => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ProjectSubmissionState> FromResolving(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event,
        ProjectSubmissionContext context)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => Illegal(state),
            ProjectSubmissionEvent.CandidatePersisted => Illegal(state),
            ProjectSubmissionEvent.ReviewPassed => Illegal(state),
            ProjectSubmissionEvent.ReviewFailed => Illegal(state),
            ProjectSubmissionEvent.Cancel => Legal(state, ProjectSubmissionState.Idle),
            ProjectSubmissionEvent.BeginAcceptance => BeginAcceptance(state, context.AcceptanceDecision),
            ProjectSubmissionEvent.BeginCommit => Illegal(state),
            ProjectSubmissionEvent.CommitCompleted => Illegal(state),
            ProjectSubmissionEvent.RevalidationCompleted => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ProjectSubmissionState> FromAccepting(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => Illegal(state),
            ProjectSubmissionEvent.CandidatePersisted => Illegal(state),
            ProjectSubmissionEvent.ReviewPassed => Illegal(state),
            ProjectSubmissionEvent.ReviewFailed => Illegal(state),
            ProjectSubmissionEvent.Cancel => Rejected(
                state,
                TransitionClassification.Illegal,
                AuthorityRejectionCode.CancelNotAllowed),
            ProjectSubmissionEvent.BeginAcceptance => Illegal(state),
            ProjectSubmissionEvent.BeginCommit => Legal(state, ProjectSubmissionState.Committing),
            ProjectSubmissionEvent.CommitCompleted => Illegal(state),
            ProjectSubmissionEvent.RevalidationCompleted => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ProjectSubmissionState> FromCommitting(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => Illegal(state),
            ProjectSubmissionEvent.CandidatePersisted => Illegal(state),
            ProjectSubmissionEvent.ReviewPassed => Illegal(state),
            ProjectSubmissionEvent.ReviewFailed => Illegal(state),
            ProjectSubmissionEvent.Cancel => Rejected(
                state,
                TransitionClassification.Illegal,
                AuthorityRejectionCode.CancelNotAllowed),
            ProjectSubmissionEvent.BeginAcceptance => Illegal(state),
            ProjectSubmissionEvent.BeginCommit => Illegal(state),
            ProjectSubmissionEvent.CommitCompleted => Legal(state, ProjectSubmissionState.Revalidating),
            ProjectSubmissionEvent.RevalidationCompleted => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ProjectSubmissionState> FromRevalidating(
        ProjectSubmissionState state,
        ProjectSubmissionEvent @event)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => Illegal(state),
            ProjectSubmissionEvent.CandidatePersisted => Illegal(state),
            ProjectSubmissionEvent.ReviewPassed => Illegal(state),
            ProjectSubmissionEvent.ReviewFailed => Illegal(state),
            ProjectSubmissionEvent.Cancel => Rejected(
                state,
                TransitionClassification.Illegal,
                AuthorityRejectionCode.CancelNotAllowed),
            ProjectSubmissionEvent.BeginAcceptance => Illegal(state),
            ProjectSubmissionEvent.BeginCommit => Illegal(state),
            ProjectSubmissionEvent.CommitCompleted => Illegal(state),
            ProjectSubmissionEvent.RevalidationCompleted => Legal(state, ProjectSubmissionState.Idle),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ProjectSubmissionState> Submit(
        ProjectSubmissionState state,
        ProjectSubmissionContext context)
    {
        if (context.ActiveSubmissionExists)
        {
            return GuardRejected(state, AuthorityRejectionCode.ActiveSubmissionExists);
        }

        return context.Eligibility == SubmissionEligibility.None
            ? GuardRejected(state, AuthorityRejectionCode.EligibilityDenied)
            : ConditionalAllowed(state, ProjectSubmissionState.Submitting);
    }

    private static TransitionResult<ProjectSubmissionState> BeginAcceptance(
        ProjectSubmissionState state,
        AcceptanceDecisionContext decision)
        => !decision.AcceptanceAuthorized || decision.AuthorityKind is null
            ? GuardRejected(state, AuthorityRejectionCode.AcceptanceNotAuthorized)
            : ConditionalAllowed(
                state,
                ProjectSubmissionState.Accepting,
                decision.ToMetadata());
}
