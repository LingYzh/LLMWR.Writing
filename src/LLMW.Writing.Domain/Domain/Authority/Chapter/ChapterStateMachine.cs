using static LLMW.Writing.Domain.Authority.TransitionResults;

namespace LLMW.Writing.Domain.Authority.Chapter;

public enum ChapterState
{
    OutlineContract,
    Ready,
    Draft,
    Submitted,
    UnderReview,
    Failed,
    Accepted,
    Materialized
}

public enum ChapterEvent
{
    MarkReady,
    BeginDraft,
    Submit,
    BeginReview,
    FailReview,
    ReturnToDraft,
    Accept,
    Materialize
}

public sealed record ChapterContext(AcceptanceDecisionContext AcceptanceDecision)
{
    public static ChapterContext Empty { get; } = new(AcceptanceDecisionContext.Unauthorized);
}

public static class ChapterStateMachine
{
    public static StateMachine<ChapterState, ChapterEvent, ChapterContext> Instance { get; } =
        new(Transition);

    private static TransitionResult<ChapterState> Transition(
        ChapterState state,
        ChapterEvent @event,
        ChapterContext context)
        => state switch
        {
            ChapterState.OutlineContract => One(state, @event, ChapterEvent.MarkReady, ChapterState.Ready),
            ChapterState.Ready => One(state, @event, ChapterEvent.BeginDraft, ChapterState.Draft),
            ChapterState.Draft => One(state, @event, ChapterEvent.Submit, ChapterState.Submitted),
            ChapterState.Submitted => One(state, @event, ChapterEvent.BeginReview, ChapterState.UnderReview),
            ChapterState.UnderReview => FromUnderReview(state, @event, context.AcceptanceDecision),
            ChapterState.Failed => One(state, @event, ChapterEvent.ReturnToDraft, ChapterState.Draft),
            ChapterState.Accepted => One(state, @event, ChapterEvent.Materialize, ChapterState.Materialized),
            ChapterState.Materialized => AllIllegal(state, @event),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

    private static TransitionResult<ChapterState> FromUnderReview(
        ChapterState state,
        ChapterEvent @event,
        AcceptanceDecisionContext decision)
        => @event switch
        {
            ChapterEvent.MarkReady => Illegal(state),
            ChapterEvent.BeginDraft => Illegal(state),
            ChapterEvent.Submit => Illegal(state),
            ChapterEvent.BeginReview => Illegal(state),
            ChapterEvent.FailReview => Legal(state, ChapterState.Failed),
            ChapterEvent.ReturnToDraft => Illegal(state),
            ChapterEvent.Accept => Accept(state, decision),
            ChapterEvent.Materialize => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ChapterState> One(
        ChapterState state,
        ChapterEvent @event,
        ChapterEvent legalEvent,
        ChapterState nextState)
        => @event switch
        {
            ChapterEvent.MarkReady => legalEvent == ChapterEvent.MarkReady
                ? Legal(state, nextState)
                : Illegal(state),
            ChapterEvent.BeginDraft => legalEvent == ChapterEvent.BeginDraft
                ? Legal(state, nextState)
                : Illegal(state),
            ChapterEvent.Submit => legalEvent == ChapterEvent.Submit
                ? Legal(state, nextState)
                : Illegal(state),
            ChapterEvent.BeginReview => legalEvent == ChapterEvent.BeginReview
                ? Legal(state, nextState)
                : Illegal(state),
            ChapterEvent.FailReview => legalEvent == ChapterEvent.FailReview
                ? Legal(state, nextState)
                : Illegal(state),
            ChapterEvent.ReturnToDraft => legalEvent == ChapterEvent.ReturnToDraft
                ? Legal(state, nextState)
                : Illegal(state),
            ChapterEvent.Accept => legalEvent == ChapterEvent.Accept
                ? Legal(state, nextState)
                : Illegal(state),
            ChapterEvent.Materialize => legalEvent == ChapterEvent.Materialize
                ? Legal(state, nextState)
                : Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<ChapterState> AllIllegal(ChapterState state, ChapterEvent @event)
        => One(state, @event, (ChapterEvent)(-1), state);

    private static TransitionResult<ChapterState> Accept(
        ChapterState state,
        AcceptanceDecisionContext decision)
        => !decision.AcceptanceAuthorized || decision.AuthorityKind is null
            ? GuardRejected(state, AuthorityRejectionCode.AcceptanceNotAuthorized)
            : ConditionalAllowed(
                state,
                ChapterState.Accepted,
                decision.ToMetadata());
}
