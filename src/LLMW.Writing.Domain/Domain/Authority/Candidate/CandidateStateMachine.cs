using static LLMW.Writing.Domain.Authority.TransitionResults;

namespace LLMW.Writing.Domain.Authority.Candidate;

public enum CandidateState
{
    Created,
    UnderReview,
    Failed,
    Cancelled,
    Accepted,
    Superseded
}

public enum CandidateEvent
{
    BeginReview,
    FailReview,
    CancelReview,
    Accept,
    Supersede
}

public sealed record CandidateContext(
    AcceptanceDecisionContext AcceptanceDecision,
    bool HasValidLaterAcceptedLineage)
{
    public static CandidateContext Empty { get; } =
        new(AcceptanceDecisionContext.Unauthorized, false);
}

public static class CandidateStateMachine
{
    public static StateMachine<CandidateState, CandidateEvent, CandidateContext> Instance { get; } =
        new(Transition);

    private static TransitionResult<CandidateState> Transition(
        CandidateState state,
        CandidateEvent @event,
        CandidateContext context)
        => state switch
        {
            CandidateState.Created => FromCreated(state, @event),
            CandidateState.UnderReview => FromUnderReview(state, @event, context.AcceptanceDecision),
            CandidateState.Failed => AllIllegal(state, @event),
            CandidateState.Cancelled => AllIllegal(state, @event),
            CandidateState.Accepted => FromAccepted(state, @event, context),
            CandidateState.Superseded => AllIllegal(state, @event),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

    private static TransitionResult<CandidateState> FromCreated(CandidateState state, CandidateEvent @event)
        => @event switch
        {
            CandidateEvent.BeginReview => Legal(state, CandidateState.UnderReview),
            CandidateEvent.FailReview => Illegal(state),
            CandidateEvent.CancelReview => Illegal(state),
            CandidateEvent.Accept => Illegal(state),
            CandidateEvent.Supersede => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<CandidateState> FromUnderReview(
        CandidateState state,
        CandidateEvent @event,
        AcceptanceDecisionContext decision)
        => @event switch
        {
            CandidateEvent.BeginReview => Illegal(state),
            CandidateEvent.FailReview => Legal(state, CandidateState.Failed),
            CandidateEvent.CancelReview => Legal(state, CandidateState.Cancelled),
            CandidateEvent.Accept => Accept(state, decision),
            CandidateEvent.Supersede => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<CandidateState> FromAccepted(
        CandidateState state,
        CandidateEvent @event,
        CandidateContext context)
        => @event switch
        {
            CandidateEvent.BeginReview => Illegal(state),
            CandidateEvent.FailReview => Illegal(state),
            CandidateEvent.CancelReview => Illegal(state),
            CandidateEvent.Accept => Illegal(state),
            CandidateEvent.Supersede => context.HasValidLaterAcceptedLineage
                ? ConditionalAllowed(state, CandidateState.Superseded)
                : GuardRejected(state, AuthorityRejectionCode.LineageIneligible),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<CandidateState> AllIllegal(CandidateState state, CandidateEvent @event)
        => @event switch
        {
            CandidateEvent.BeginReview => Illegal(state),
            CandidateEvent.FailReview => Illegal(state),
            CandidateEvent.CancelReview => Illegal(state),
            CandidateEvent.Accept => Illegal(state),
            CandidateEvent.Supersede => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<CandidateState> Accept(
        CandidateState state,
        AcceptanceDecisionContext decision)
        => !decision.AcceptanceAuthorized || decision.AuthorityKind is null
            ? GuardRejected(state, AuthorityRejectionCode.AcceptanceNotAuthorized)
            : ConditionalAllowed(
                state,
                CandidateState.Accepted,
                decision.ToMetadata());
}
