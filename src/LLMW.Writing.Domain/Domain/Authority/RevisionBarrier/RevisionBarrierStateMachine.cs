using static LLMW.Writing.Domain.Authority.TransitionResults;

namespace LLMW.Writing.Domain.Authority.RevisionBarrier;

public enum RevisionBarrierState { Inactive, ActiveInitial, Resolving }
public enum RevisionBarrierEvent { Activate, AuthorityCommitted, ReviewFailed, Cancel, OrdinaryFailure, CompleteResolution, SubmitRemediation }

public sealed record RevisionBarrierContext(
    bool AuthorityCommitted,
    bool AffectedSetClean,
    string? BarrierId,
    string? OriginatingTransactionId,
    string? RemediationBarrierId,
    string? RemediationOriginatingTransactionId)
{
    public static RevisionBarrierContext Empty { get; } = new(false, false, null, null, null, null);
}

public static class RevisionBarrierStateMachine
{
    public static StateMachine<RevisionBarrierState, RevisionBarrierEvent, RevisionBarrierContext> Instance { get; } = new(Transition);

    private static TransitionResult<RevisionBarrierState> Transition(RevisionBarrierState state, RevisionBarrierEvent @event, RevisionBarrierContext context)
        => state switch
        {
            RevisionBarrierState.Inactive => FromInactive(state, @event),
            RevisionBarrierState.ActiveInitial => FromActiveInitial(state, @event, context),
            RevisionBarrierState.Resolving => FromResolving(state, @event, context),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };

    private static TransitionResult<RevisionBarrierState> FromInactive(
        RevisionBarrierState state,
        RevisionBarrierEvent @event)
        => @event switch
        {
            RevisionBarrierEvent.Activate => Legal(state, RevisionBarrierState.ActiveInitial),
            RevisionBarrierEvent.AuthorityCommitted => Illegal(state),
            RevisionBarrierEvent.ReviewFailed => Illegal(state),
            RevisionBarrierEvent.Cancel => Illegal(state),
            RevisionBarrierEvent.OrdinaryFailure => Illegal(state),
            RevisionBarrierEvent.CompleteResolution => Illegal(state),
            RevisionBarrierEvent.SubmitRemediation => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<RevisionBarrierState> FromActiveInitial(
        RevisionBarrierState state,
        RevisionBarrierEvent @event,
        RevisionBarrierContext context)
        => @event switch
        {
            RevisionBarrierEvent.Activate => Illegal(state),
            RevisionBarrierEvent.AuthorityCommitted =>
                context.AuthorityCommitted
                    ? ConditionalAllowed(state, RevisionBarrierState.Resolving)
                    : GuardRejected(state, AuthorityRejectionCode.GuardFailed),
            RevisionBarrierEvent.ReviewFailed or RevisionBarrierEvent.Cancel =>
                !context.AuthorityCommitted
                    ? ConditionalAllowed(state, RevisionBarrierState.Inactive)
                    : GuardRejected(state, AuthorityRejectionCode.BarrierNotResolved),
            RevisionBarrierEvent.OrdinaryFailure => Illegal(state),
            RevisionBarrierEvent.CompleteResolution => Illegal(state),
            RevisionBarrierEvent.SubmitRemediation => Illegal(state),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<RevisionBarrierState> FromResolving(
        RevisionBarrierState state,
        RevisionBarrierEvent @event,
        RevisionBarrierContext context)
        => @event switch
        {
            RevisionBarrierEvent.Activate => Illegal(state),
            RevisionBarrierEvent.AuthorityCommitted => Illegal(state),
            RevisionBarrierEvent.ReviewFailed => Illegal(state),
            RevisionBarrierEvent.Cancel => Illegal(state),
            RevisionBarrierEvent.OrdinaryFailure => Illegal(state),
            RevisionBarrierEvent.CompleteResolution =>
                context.AffectedSetClean
                    ? ConditionalAllowed(state, RevisionBarrierState.Inactive)
                    : GuardRejected(state, AuthorityRejectionCode.BarrierNotResolved),
            RevisionBarrierEvent.SubmitRemediation => ValidateRemediation(state, context),
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, null)
        };

    private static TransitionResult<RevisionBarrierState> ValidateRemediation(RevisionBarrierState state, RevisionBarrierContext context)
    {
        if (context.BarrierId is null || context.RemediationBarrierId is null ||
            !StringComparer.Ordinal.Equals(context.BarrierId, context.RemediationBarrierId))
        {
            return GuardRejected(state, AuthorityRejectionCode.BarrierIdentityMismatch);
        }

        if (context.OriginatingTransactionId is null || context.RemediationOriginatingTransactionId is null ||
            !StringComparer.Ordinal.Equals(context.OriginatingTransactionId, context.RemediationOriginatingTransactionId))
        {
            return GuardRejected(state, AuthorityRejectionCode.OriginatingTransactionMismatch);
        }

        return ConditionalAllowed(state, RevisionBarrierState.Resolving);
    }
}
