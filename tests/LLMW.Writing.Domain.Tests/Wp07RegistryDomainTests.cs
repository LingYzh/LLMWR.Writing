using LLMW.Writing.Domain.Registry;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private static void RunWp07RegistryDomainTests()
    {
        Run(nameof(RegistryEligibilityAllowsOnlyTrustedRegisteredAvailableClean),
            RegistryEligibilityAllowsOnlyTrustedRegisteredAvailableClean);
        Run(nameof(RegistryEligibilityDeniesEveryFrozenIneligibleState),
            RegistryEligibilityDeniesEveryFrozenIneligibleState);
    }

    private static void RegistryEligibilityAllowsOnlyTrustedRegisteredAvailableClean()
    {
        var result = RegistryRetrievalEligibility.Evaluate(new RegistryEligibilityInput(
            RegistryRegistrationState.Registered,
            RegistryRetrievalAvailability.Available,
            RegistryReconcileState.Clean,
            TrustedPhysicalBaselinePresent: true,
            TrustedSemanticBaselinePresent: true));

        AssertEqual(true, result.Eligible, "Trusted REGISTERED + AVAILABLE + CLEAN was denied.");
        AssertEqual(RegistryEligibilityDenial.None, result.Denial, "Eligible Registry entry has a denial reason.");
    }

    private static void RegistryEligibilityDeniesEveryFrozenIneligibleState()
    {
        RegistryEligibilityInput[] denied =
        [
            Eligible() with { RegistrationState = RegistryRegistrationState.Unregistered },
            Eligible() with { RegistrationState = RegistryRegistrationState.Missing },
            Eligible() with { RegistrationState = RegistryRegistrationState.Ignored },
            Eligible() with { RetrievalAvailability = RegistryRetrievalAvailability.Unavailable },
            Eligible() with { RetrievalAvailability = RegistryRetrievalAvailability.Stale },
            Eligible() with { ReconcileState = RegistryReconcileState.Dirty },
            Eligible() with { ReconcileState = RegistryReconcileState.PendingConfirm },
            Eligible() with { ReconcileState = RegistryReconcileState.Reconciling },
            Eligible() with { ReconcileState = RegistryReconcileState.NeedsAttention },
            Eligible() with { TrustedPhysicalBaselinePresent = false },
            Eligible() with { TrustedSemanticBaselinePresent = false }
        ];

        foreach (var input in denied)
        {
            var result = RegistryRetrievalEligibility.Evaluate(input);
            AssertEqual(false, result.Eligible, $"Ineligible Registry state was allowed: {input}");
        }
    }

    private static RegistryEligibilityInput Eligible() => new(
        RegistryRegistrationState.Registered,
        RegistryRetrievalAvailability.Available,
        RegistryReconcileState.Clean,
        TrustedPhysicalBaselinePresent: true,
        TrustedSemanticBaselinePresent: true);
}
