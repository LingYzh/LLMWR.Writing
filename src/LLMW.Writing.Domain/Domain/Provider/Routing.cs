namespace LLMW.Writing.Domain.Provider;

public sealed record RouteRequirementProfile(
    bool RequiresStreaming,
    bool RequiresToolCalling,
    bool RequiresStructuredOutput,
    bool RequiresInstructionHierarchy,
    ReasoningCeiling RequestedReasoning,
    ProviderDataBehavior RequiredDataBehavior,
    int? MinimumContextTokens,
    string? PinnedProviderDefinitionId,
    string? PinnedModelId,
    string? SpecialistPreferredProviderId,
    string? SpecialistPreferredModelId,
    bool AllowFallback,
    string? RequiredTaskClass = null)
{
    public static RouteRequirementProfile TextOnly { get; } = new(
        false, false, false, true, ReasoningCeiling.Conservative,
        ProviderDataBehavior.Unknown, null, null, null, null, null, false);
}

public sealed record RouteCandidate(
    ProviderDefinitionId ProviderDefinitionId,
    ProviderRevision Revision,
    ModelId ModelId,
    ProtocolKind ProtocolKind,
    bool Enabled,
    bool CredentialAvailable,
    ModelCertificationRecord Certification,
    int? ContextLimit,
    int Priority,
    TaskCapabilityCertification? TaskCertification = null)
{
    public string StableId => ProviderIdentity.StableTieBreak(ProviderDefinitionId, ModelId);
}

public sealed record RouteDecision(
    RouteCandidate? Selected,
    IReadOnlyList<RouteCandidate> EligibleOrdered,
    string? FailureCode,
    bool PinHonored)
{
    public static RouteDecision Fail(string code) => new(null, [], code, true);
}

public static class ProviderRouter
{
    public static RouteDecision Route(
        IReadOnlyList<RouteCandidate> candidates,
        RouteRequirementProfile requirement)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(requirement);

        if (!string.IsNullOrWhiteSpace(requirement.PinnedProviderDefinitionId) ||
            !string.IsNullOrWhiteSpace(requirement.PinnedModelId))
        {
            var pinned = candidates.Where(candidate => MatchesPin(candidate, requirement)).ToArray();
            var eligiblePinned = pinned.Where(candidate => IsEligible(candidate, requirement)).ToArray();
            if (eligiblePinned.Length == 0)
            {
                return RouteDecision.Fail("ROUTE_PIN_UNAVAILABLE");
            }

            var orderedPinned = Order(eligiblePinned, requirement);
            return new RouteDecision(orderedPinned[0], orderedPinned, null, true);
        }

        var eligible = candidates.Where(candidate => IsEligible(candidate, requirement)).ToArray();
        if (eligible.Length == 0)
        {
            return RouteDecision.Fail("ROUTE_NO_ELIGIBLE_CANDIDATE");
        }

        var ordered = Order(eligible, requirement);
        return new RouteDecision(ordered[0], ordered, null, false);
    }

    public static bool IsEligible(RouteCandidate candidate, RouteRequirementProfile requirement)
    {
        if (!candidate.Enabled || !candidate.CredentialAvailable)
        {
            return false;
        }

        if (candidate.Certification.State is CertificationState.Failed or CertificationState.Stale)
        {
            return false;
        }

        if (requirement.RequiresToolCalling &&
            !CapabilitySupportCodec.IsUsableAsSupported(candidate.Certification.SupportFor(ModelCapabilityNames.ToolCalling)))
        {
            return false;
        }

        if (requirement.RequiresStructuredOutput &&
            !CapabilitySupportCodec.IsUsableAsSupported(candidate.Certification.SupportFor(ModelCapabilityNames.StructuredJson)))
        {
            return false;
        }

        if (requirement.RequiresStreaming &&
            !CapabilitySupportCodec.IsUsableAsSupported(candidate.Certification.SupportFor(ModelCapabilityNames.Streaming)))
        {
            return false;
        }

        if (requirement.RequiresInstructionHierarchy &&
            !CapabilitySupportCodec.IsUsableAsSupported(candidate.Certification.SupportFor(ModelCapabilityNames.InstructionHierarchy)))
        {
            return false;
        }

        var taskCert = candidate.TaskCertification ??
                       TaskCapabilityCertification.Uncertified(
                           candidate.ProviderDefinitionId,
                           candidate.Revision,
                           "",
                           "",
                           "",
                           candidate.ModelId);
        if (taskCert.State == CertificationState.Stale)
        {
            return false;
        }

        var ceiling = taskCert.EffectiveCeiling;
        if (!ReasoningCeilingCodec.CanDowngradeTo(ceiling, requirement.RequestedReasoning) &&
            RankCeiling(requirement.RequestedReasoning) > RankCeiling(ceiling))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requirement.RequiredTaskClass) &&
            (taskCert.State != CertificationState.Certified ||
             !taskCert.CertifiedTaskClasses.Contains(requirement.RequiredTaskClass, StringComparer.Ordinal)))
        {
            return false;
        }

        if (requirement.RequiredDataBehavior is not ProviderDataBehavior.Unknown &&
            candidate.Certification.DataBehavior != requirement.RequiredDataBehavior)
        {
            if (candidate.Certification.DataBehavior == ProviderDataBehavior.Unknown)
            {
                return false;
            }

            return false;
        }

        if (requirement.MinimumContextTokens is int needed)
        {
            if (candidate.ContextLimit is null)
            {
                return false;
            }

            if (candidate.ContextLimit.Value < needed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesPin(RouteCandidate candidate, RouteRequirementProfile requirement)
    {
        if (!string.IsNullOrWhiteSpace(requirement.PinnedProviderDefinitionId) &&
            !string.Equals(candidate.ProviderDefinitionId.Value, requirement.PinnedProviderDefinitionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requirement.PinnedModelId) &&
            !string.Equals(candidate.ModelId.Value, requirement.PinnedModelId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static RouteCandidate[] Order(
        IReadOnlyList<RouteCandidate> eligible,
        RouteRequirementProfile requirement)
    {
        return eligible
            .OrderByDescending(candidate => PreferenceScore(candidate, requirement))
            .ThenByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.ProviderDefinitionId.Value, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ModelId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static int PreferenceScore(RouteCandidate candidate, RouteRequirementProfile requirement)
    {
        var score = 0;
        if (string.Equals(candidate.ProviderDefinitionId.Value, requirement.SpecialistPreferredProviderId, StringComparison.Ordinal))
        {
            score += 2;
        }

        if (string.Equals(candidate.ModelId.Value, requirement.SpecialistPreferredModelId, StringComparison.Ordinal))
        {
            score += 1;
        }

        if (candidate.Certification.State == CertificationState.Certified)
        {
            score += 4;
        }

        return score;
    }

    private static int RankCeiling(ReasoningCeiling value) => value switch
    {
        ReasoningCeiling.Conservative => 0,
        ReasoningCeiling.Guarded => 1,
        ReasoningCeiling.Adaptive => 2,
        _ => 0
    };
}
