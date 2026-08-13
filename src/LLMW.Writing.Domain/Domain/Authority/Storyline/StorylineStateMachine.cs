using static LLMW.Writing.Domain.Authority.TransitionResults;

namespace LLMW.Writing.Domain.Authority.Storyline;

public enum StorylineState { RevisionComplete, UnderFinalReview, FinalAccepted, PostAcceptanceRevision }
public enum StorylineEvent { BeginFinalReview, AcceptFinal, BeginPostAcceptanceRevision }

public sealed record StorylineContext(
    AcceptanceDecisionContext AcceptanceDecision,
    string? AcceptedSnapshotId)
{
    public static StorylineContext Empty { get; } = new(AcceptanceDecisionContext.Unauthorized, null);
}

public static class StorylineStateMachine
{
    public static StateMachine<StorylineState, StorylineEvent, StorylineContext> Instance { get; } = new(Transition);

    private static TransitionResult<StorylineState> Transition(StorylineState state, StorylineEvent @event, StorylineContext context)
        => state switch
        {
            StorylineState.RevisionComplete => FromRevisionComplete(state, @event),
            StorylineState.UnderFinalReview => FromUnderFinalReview(state, @event, context),
            StorylineState.FinalAccepted => FromFinalAccepted(state, @event, context),
            StorylineState.PostAcceptanceRevision => AllIllegal(state, @event),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

    private static TransitionResult<StorylineState> FromRevisionComplete(StorylineState state, StorylineEvent @event)
        => @event switch
        {
            StorylineEvent.BeginFinalReview => Legal(state, StorylineState.UnderFinalReview),
            StorylineEvent.AcceptFinal => Illegal(state),
            StorylineEvent.BeginPostAcceptanceRevision => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<StorylineState> FromUnderFinalReview(
        StorylineState state,
        StorylineEvent @event,
        StorylineContext context)
        => @event switch
        {
            StorylineEvent.BeginFinalReview => Illegal(state),
            StorylineEvent.AcceptFinal when
                context.AcceptanceDecision.AcceptanceAuthorized && context.AcceptanceDecision.AuthorityKind is not null =>
                ConditionalAllowed(
                    state,
                    StorylineState.FinalAccepted,
                    context.AcceptanceDecision.ToMetadata(AuthorityEffectRequirement.AcceptedSnapshotRequired)),
            StorylineEvent.AcceptFinal => GuardRejected(state, AuthorityRejectionCode.AcceptanceNotAuthorized),
            StorylineEvent.BeginPostAcceptanceRevision => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<StorylineState> FromFinalAccepted(
        StorylineState state,
        StorylineEvent @event,
        StorylineContext context)
        => @event switch
        {
            StorylineEvent.BeginFinalReview => Illegal(state),
            StorylineEvent.AcceptFinal => Illegal(state),
            StorylineEvent.BeginPostAcceptanceRevision when context.AcceptedSnapshotId is not null =>
                ConditionalAllowed(
                    state,
                    StorylineState.PostAcceptanceRevision,
                    new AuthorityTransitionMetadata(AuthorityEffectRequirement.PreserveHistoricalSnapshotIdentity, null)),
            StorylineEvent.BeginPostAcceptanceRevision => GuardRejected(state, AuthorityRejectionCode.AcceptedSnapshotIdentityMissing),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<StorylineState> AllIllegal(StorylineState state, StorylineEvent @event)
        => @event switch
        {
            StorylineEvent.BeginFinalReview => Illegal(state),
            StorylineEvent.AcceptFinal => Illegal(state),
            StorylineEvent.BeginPostAcceptanceRevision => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };
}
