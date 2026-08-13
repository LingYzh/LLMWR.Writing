using static LLMW.Writing.Domain.Authority.TransitionResults;

namespace LLMW.Writing.Domain.Authority.Arc;

public enum ArcState { Open, UnderClosureReview, Accepted }
public enum ArcEvent { BeginClosureReview, Accept }
public sealed record ArcContext(AcceptanceDecisionContext AcceptanceDecision)
{
    public static ArcContext Empty { get; } = new(AcceptanceDecisionContext.Unauthorized);
}

public static class ArcStateMachine
{
    public static StateMachine<ArcState, ArcEvent, ArcContext> Instance { get; } = new(Transition);

    private static TransitionResult<ArcState> Transition(ArcState state, ArcEvent @event, ArcContext context)
        => state switch
        {
            ArcState.Open => @event switch
            {
                ArcEvent.BeginClosureReview => Legal(state, ArcState.UnderClosureReview),
                ArcEvent.Accept => Illegal(state),
                _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
            },
            ArcState.UnderClosureReview => @event switch
            {
                ArcEvent.BeginClosureReview => Illegal(state),
                ArcEvent.Accept when context.AcceptanceDecision.AcceptanceAuthorized && context.AcceptanceDecision.AuthorityKind is not null =>
                    ConditionalAllowed(state, ArcState.Accepted, context.AcceptanceDecision.ToMetadata()),
                ArcEvent.Accept => GuardRejected(state, AuthorityRejectionCode.AcceptanceNotAuthorized),
                _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
            },
            ArcState.Accepted => @event switch
            {
                ArcEvent.BeginClosureReview => Illegal(state),
                ArcEvent.Accept => Illegal(state),
                _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
}
