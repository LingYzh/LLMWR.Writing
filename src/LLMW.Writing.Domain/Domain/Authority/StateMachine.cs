namespace LLMW.Writing.Domain.Authority;

public sealed class StateMachine<TState, TEvent, TContext>
    where TState : struct, Enum
    where TEvent : struct, Enum
{
    private readonly Func<TState, TEvent, TContext, TransitionResult<TState>> transition;

    public StateMachine(Func<TState, TEvent, TContext, TransitionResult<TState>> transition)
    {
        this.transition = transition ?? throw new ArgumentNullException(nameof(transition));
    }

    public bool CanTransition(TState currentState, TEvent @event, TContext context)
        => Transition(currentState, @event, context).Allowed;

    public TransitionResult<TState> Transition(TState currentState, TEvent @event, TContext context)
        => transition(currentState, @event, context);
}

public enum TransitionClassification
{
    Legal,
    Illegal,
    GuardConditional
}

public sealed record TransitionResult<TState>(
    bool Allowed,
    TransitionClassification Classification,
    TState CurrentState,
    TState? NextState,
    AuthorityRejection? Rejection,
    AuthorityTransitionMetadata Metadata)
    where TState : struct, Enum;

public enum AuthorityRejectionCode
{
    IllegalTransition,
    GuardFailed,
    CancelNotAllowed,
    EligibilityDenied,
    ActiveSubmissionExists,
    BarrierRequired,
    BarrierNotResolved,
    BarrierIdentityMismatch,
    OriginatingTransactionMismatch,
    AcceptanceNotAuthorized,
    LineageIneligible,
    AcceptedSnapshotIdentityMissing
}

public sealed record AuthorityRejection(AuthorityRejectionCode Code, string? Detail = null);

public enum AuthorityEffectRequirement
{
    None,
    AcceptedSnapshotRequired,
    PreserveHistoricalSnapshotIdentity
}

public enum DecisionAuthorityKind
{
    AuthorConfirmed,
    AgentDelegated
}

public enum NarrativeOversightMode
{
    Manual,
    AcceptEdits,
    Auto,
    BypassPermissions
}

public sealed record DecisionProvenanceRequirement(
    DecisionAuthorityKind AuthorityKind,
    NarrativeOversightMode OversightMode);

public sealed record AuthorityTransitionMetadata(
    AuthorityEffectRequirement EffectRequirement,
    DecisionProvenanceRequirement? DecisionProvenance)
{
    public static AuthorityTransitionMetadata Empty { get; } =
        new(AuthorityEffectRequirement.None, null);
}

public sealed record AcceptanceDecisionContext(
    bool AcceptanceAuthorized,
    DecisionAuthorityKind? AuthorityKind,
    NarrativeOversightMode OversightMode,
    bool BypassPermissions)
{
    public static AcceptanceDecisionContext Unauthorized { get; } =
        new(false, null, NarrativeOversightMode.Manual, false);

    public AuthorityTransitionMetadata ToMetadata(AuthorityEffectRequirement effectRequirement = AuthorityEffectRequirement.None)
    {
        if (!AcceptanceAuthorized || AuthorityKind is null)
        {
            throw new InvalidOperationException("An unauthorized decision cannot produce Authority provenance metadata.");
        }

        return new AuthorityTransitionMetadata(
            effectRequirement,
            new DecisionProvenanceRequirement(AuthorityKind.Value, OversightMode));
    }
}

internal static class TransitionResults
{
    public static TransitionResult<TState> Legal<TState>(
        TState currentState,
        TState nextState,
        AuthorityTransitionMetadata? metadata = null)
        where TState : struct, Enum
        => new(true, TransitionClassification.Legal, currentState, nextState, null,
            metadata ?? AuthorityTransitionMetadata.Empty);

    public static TransitionResult<TState> ConditionalAllowed<TState>(
        TState currentState,
        TState nextState,
        AuthorityTransitionMetadata? metadata = null)
        where TState : struct, Enum
        => new(true, TransitionClassification.GuardConditional, currentState, nextState, null,
            metadata ?? AuthorityTransitionMetadata.Empty);

    public static TransitionResult<TState> Illegal<TState>(TState currentState)
        where TState : struct, Enum
        => Rejected(
            currentState,
            TransitionClassification.Illegal,
            AuthorityRejectionCode.IllegalTransition);

    public static TransitionResult<TState> GuardRejected<TState>(
        TState currentState,
        AuthorityRejectionCode code,
        string? detail = null)
        where TState : struct, Enum
        => Rejected(
            currentState,
            TransitionClassification.GuardConditional,
            code,
            detail);

    public static TransitionResult<TState> Rejected<TState>(
        TState currentState,
        TransitionClassification classification,
        AuthorityRejectionCode code,
        string? detail = null)
        where TState : struct, Enum
        => new(false, classification, currentState, null, new AuthorityRejection(code, detail),
            AuthorityTransitionMetadata.Empty);
}
