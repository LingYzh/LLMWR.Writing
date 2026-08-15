namespace LLMW.Writing.Domain.Runtime;

public sealed record ResultFreshnessAuthorityInputs(
    string RunId,
    string TaskId,
    string? AttemptId,
    IReadOnlyDictionary<string, DurableResultArtifactRecord> UpstreamResults,
    IReadOnlyDictionary<string, EvidenceRecord> EvidenceById,
    string? KnownAuthorityRevision,
    IReadOnlySet<string> KnownNarrativeObjectDigests);

public static class ResultFreshnessAuthority
{
    public static ResultFreshnessV1 Stamp(
        ResultFreshnessV1 submitted,
        ResultFreshnessAuthorityInputs inputs,
        IReadOnlyList<string> evidenceIds)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        var provenance = new ResultProvenanceV1(
            inputs.RunId,
            inputs.TaskId,
            inputs.AttemptId,
            submitted.Provenance.SpecialistProfileId,
            submitted.Provenance.ApprovedPlanRef,
            submitted.Provenance.OriginalUserRequestRef,
            submitted.Provenance.ChangeSetId,
            submitted.Provenance.TransactionId);
        var state = Evaluate(submitted.ProducedAgainst, evidenceIds, inputs);
        return submitted with { State = state, Provenance = provenance };
    }

    public static ResultFreshnessState Evaluate(
        ResultProducedAgainstV1 producedAgainst,
        IReadOnlyList<string> evidenceIds,
        ResultFreshnessAuthorityInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(producedAgainst);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        ArgumentNullException.ThrowIfNull(inputs);

        var state = ResultFreshnessState.Current;
        if (HasUnvalidatableProviderClaims(producedAgainst))
        {
            state = Worse(state, ResultFreshnessState.NeedsRevalidation);
        }

        if (!string.IsNullOrWhiteSpace(producedAgainst.AuthorityRevision))
        {
            if (string.IsNullOrWhiteSpace(inputs.KnownAuthorityRevision))
            {
                state = Worse(state, ResultFreshnessState.NeedsRevalidation);
            }
            else if (!StringComparer.Ordinal.Equals(producedAgainst.AuthorityRevision, inputs.KnownAuthorityRevision))
            {
                state = Worse(state, ResultFreshnessState.Stale);
            }
        }

        if (producedAgainst.NarrativeObjectDigests.Count > 0)
        {
            if (inputs.KnownNarrativeObjectDigests.Count == 0)
            {
                state = Worse(state, ResultFreshnessState.NeedsRevalidation);
            }
            else if (!producedAgainst.NarrativeObjectDigests.All(inputs.KnownNarrativeObjectDigests.Contains))
            {
                state = Worse(state, ResultFreshnessState.Stale);
            }
        }

        foreach (var upstream in producedAgainst.UpstreamRequiredResultRefs)
        {
            if (!inputs.UpstreamResults.TryGetValue(upstream, out var artifact))
            {
                state = Worse(state, ResultFreshnessState.NeedsRevalidation);
                continue;
            }

            var freshness = ResultArtifactCanonicalJson.FromDurable(artifact).Freshness.State;
            if (freshness is ResultFreshnessState.Stale or ResultFreshnessState.NeedsRevalidation)
            {
                state = Worse(state, ResultFreshnessState.Stale);
            }
        }

        foreach (var evidenceId in evidenceIds)
        {
            if (!inputs.EvidenceById.TryGetValue(evidenceId, out var evidence))
            {
                state = Worse(state, ResultFreshnessState.NeedsRevalidation);
                continue;
            }

            var owned = StringComparer.Ordinal.Equals(evidence.TaskId, inputs.TaskId) ||
                        StringComparer.Ordinal.Equals(evidence.RunId, inputs.RunId);
            if (!owned)
            {
                state = Worse(state, ResultFreshnessState.NeedsRevalidation);
            }

            if (evidence.Stale)
            {
                state = Worse(state, ResultFreshnessState.Stale);
            }
        }

        if (!string.IsNullOrWhiteSpace(producedAgainst.EvidenceDigest) && evidenceIds.Count > 0)
        {
            var digest = CanonicalJson.Sha256Hex(string.Join('\n', evidenceIds.OrderBy(item => item, StringComparer.Ordinal)));
            if (!StringComparer.Ordinal.Equals(producedAgainst.EvidenceDigest, digest))
            {
                state = Worse(state, ResultFreshnessState.Stale);
            }
        }

        return state;
    }

    private static bool HasUnvalidatableProviderClaims(ResultProducedAgainstV1 produced) =>
        !string.IsNullOrWhiteSpace(produced.PromptConfigId) ||
        !string.IsNullOrWhiteSpace(produced.EffectivePromptDigest) ||
        !string.IsNullOrWhiteSpace(produced.AgentsDigest) ||
        produced.SkillDigests.Count > 0 ||
        !string.IsNullOrWhiteSpace(produced.ProviderId) ||
        !string.IsNullOrWhiteSpace(produced.ModelId);

    private static ResultFreshnessState Worse(ResultFreshnessState current, ResultFreshnessState candidate)
    {
        if (current == ResultFreshnessState.Stale || candidate == ResultFreshnessState.Stale)
        {
            return ResultFreshnessState.Stale;
        }

        if (current == ResultFreshnessState.NeedsRevalidation || candidate == ResultFreshnessState.NeedsRevalidation)
        {
            return ResultFreshnessState.NeedsRevalidation;
        }

        return ResultFreshnessState.Current;
    }
}
