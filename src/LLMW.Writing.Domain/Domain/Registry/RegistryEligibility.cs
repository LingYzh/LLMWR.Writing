namespace LLMW.Writing.Domain.Registry;

public enum RegistryRegistrationState
{
    Registered,
    Unregistered,
    Ignored,
    Missing
}

public enum RegistryRetrievalAvailability
{
    Available,
    Unavailable,
    Stale
}

public enum RegistryReconcileState
{
    Clean,
    Dirty,
    PendingConfirm,
    Reconciling,
    NeedsAttention
}

public enum RegistryEligibilityDenial
{
    None,
    NotRegistered,
    Unavailable,
    Stale,
    ReconciliationRequired,
    TrustedBaselineMissing
}

public sealed record RegistryEligibilityInput(
    RegistryRegistrationState RegistrationState,
    RegistryRetrievalAvailability RetrievalAvailability,
    RegistryReconcileState ReconcileState,
    bool TrustedPhysicalBaselinePresent,
    bool TrustedSemanticBaselinePresent);

public sealed record RegistryEligibility(bool Eligible, RegistryEligibilityDenial Denial)
{
    public static RegistryEligibility Allow { get; } = new(true, RegistryEligibilityDenial.None);
}

public static class RegistryRetrievalEligibility
{
    public static RegistryEligibility Evaluate(RegistryEligibilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.RegistrationState != RegistryRegistrationState.Registered)
        {
            return new RegistryEligibility(false, RegistryEligibilityDenial.NotRegistered);
        }

        if (input.RetrievalAvailability == RegistryRetrievalAvailability.Stale)
        {
            return new RegistryEligibility(false, RegistryEligibilityDenial.Stale);
        }

        if (input.RetrievalAvailability != RegistryRetrievalAvailability.Available)
        {
            return new RegistryEligibility(false, RegistryEligibilityDenial.Unavailable);
        }

        if (input.ReconcileState != RegistryReconcileState.Clean)
        {
            return new RegistryEligibility(false, RegistryEligibilityDenial.ReconciliationRequired);
        }

        return input.TrustedPhysicalBaselinePresent && input.TrustedSemanticBaselinePresent
            ? RegistryEligibility.Allow
            : new RegistryEligibility(false, RegistryEligibilityDenial.TrustedBaselineMissing);
    }
}
