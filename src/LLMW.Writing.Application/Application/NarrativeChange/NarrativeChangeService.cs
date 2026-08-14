using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Domain.Narrative;

namespace LLMW.Writing.Application.NarrativeChange;

public sealed class NarrativeChangeService
{
    private readonly IImmutableBlobStore blobStore;
    private readonly INarrativeChangeStore store;
    private readonly ISemanticDependencyAssessor semanticDependencyAssessor;
    private readonly INarrativeImpactAnalyzer impactAnalyzer;

    public NarrativeChangeService(
        IImmutableBlobStore blobStore,
        INarrativeChangeStore store,
        ISemanticDependencyAssessor semanticDependencyAssessor,
        INarrativeImpactAnalyzer impactAnalyzer)
    {
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.semanticDependencyAssessor = semanticDependencyAssessor ?? throw new ArgumentNullException(nameof(semanticDependencyAssessor));
        this.impactAnalyzer = impactAnalyzer ?? throw new ArgumentNullException(nameof(impactAnalyzer));
    }

    public NarrativeChangeResult<CreateWorkingNarrativeChangeSetResult> CreateWorkingChangeSet(
        CreateWorkingNarrativeChangeSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ScopeKind) || string.IsNullOrWhiteSpace(command.ScopeId) ||
            string.IsNullOrWhiteSpace(command.ProposerKind) || command.Changes.Count == 0)
        {
            return NarrativeChangeResults.Fail<CreateWorkingNarrativeChangeSetResult>(NarrativeChangeError.InvalidChangeOperation);
        }

        if (command.Changes.Select(change => change.ObjectId).Distinct(StringComparer.Ordinal).Count() != command.Changes.Count)
        {
            return NarrativeChangeResults.Fail<CreateWorkingNarrativeChangeSetResult>(
                NarrativeChangeError.PartialApplyForbidden,
                "A working change set may contain each Narrative Object only once.");
        }

        List<NarrativeChangeDraft> drafts = [];
        for (var ordinal = 0; ordinal < command.Changes.Count; ordinal++)
        {
            var input = command.Changes[ordinal];
            var digestResult = ResolveAfterPayloadDigest(input, cancellationToken);
            if (digestResult.Failure is not null)
            {
                return new NarrativeChangeResult<CreateWorkingNarrativeChangeSetResult>(null, digestResult.Failure);
            }

            var draft = new NarrativeChangeDraft(
                input.ObjectId,
                input.ObjectType,
                input.ChangeKind,
                input.BeforeRevisionRef,
                input.BeforeDigest,
                digestResult.Value,
                ordinal);
            var validationFailure = draft.Validate();
            if (validationFailure is not null)
            {
                return NarrativeChangeResults.Fail<CreateWorkingNarrativeChangeSetResult>(
                    NarrativeChangeError.InvalidChangeOperation,
                    validationFailure.Value.ToString());
            }

            drafts.Add(draft);
        }

        try
        {
            var persisted = store.CreateWorkingChangeSet(new PersistWorkingChangeSetRequest(
                command.ScopeKind,
                command.ScopeId,
                command.ProposerKind,
                command.ProposerId,
                drafts));
            if (!persisted.Succeeded)
            {
                return new NarrativeChangeResult<CreateWorkingNarrativeChangeSetResult>(null, persisted.Failure);
            }

            return NarrativeChangeResults.Success(new CreateWorkingNarrativeChangeSetResult(
                persisted.Value!.ChangeSetId,
                persisted.Value.Changes));
        }
        catch (Exception exception)
        {
            return NarrativeChangeResults.Fail<CreateWorkingNarrativeChangeSetResult>(
                NarrativeChangeError.InfrastructureFailure,
                exception.Message);
        }
    }

    public NarrativeChangeResult<ApplyNarrativeChangeSetResult> Apply(
        ApplyNarrativeChangeSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ChangeSetId) || string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(NarrativeChangeError.ChangeSetNotApplicable);
        }

        if (command.DeciderKind != NarrativeDecisionKind.AuthorConfirmed)
        {
            return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                NarrativeChangeError.DecisionAuthorityNotAvailable,
                command.DeciderKind.ToString());
        }

        try
        {
            var changeSet = store.LoadChangeSet(command.ChangeSetId);
            if (changeSet is null)
            {
                return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(NarrativeChangeError.ChangeSetNotFound);
            }

            NarrativeImpactAnalysisRecord? analysis = null;
            if (!StringComparer.Ordinal.Equals(changeSet.Status, "applied"))
            {
                var preconditionFailure = store.ValidateApplyPreconditions(changeSet, cancellationToken);
                if (preconditionFailure is not null)
                {
                    return new NarrativeChangeResult<ApplyNarrativeChangeSetResult>(null, preconditionFailure);
                }

                var analysisResult = AssessAndPersistImpact(changeSet, cancellationToken);
                if (!analysisResult.Succeeded)
                {
                    return new NarrativeChangeResult<ApplyNarrativeChangeSetResult>(null, analysisResult.Failure);
                }

                analysis = analysisResult.Value;
            }
            else if (changeSet.ImpactAnalysisId is null)
            {
                return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(NarrativeChangeError.ChangeSetNotApplicable);
            }

            var applied = store.Apply(
                new NarrativeApplyStoreRequest(
                    command.ChangeSetId,
                    command.IdempotencyKey,
                    command.DeciderKind,
                    command.DeciderId,
                    analysis?.ImpactAnalysisId ?? changeSet.ImpactAnalysisId),
                cancellationToken);
            if (!applied.Succeeded)
            {
                return new NarrativeChangeResult<ApplyNarrativeChangeSetResult>(null, applied.Failure);
            }

            var warnings = analysis?.Warnings ?? [];
            return NarrativeChangeResults.Success(new ApplyNarrativeChangeSetResult(
                applied.Value!.ChangeSetId,
                applied.Value.TransactionId,
                applied.Value.ImpactAnalysisId,
                applied.Value.TransactionState,
                applied.Value.Existing,
                warnings));
        }
        catch (Exception exception)
        {
            return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                NarrativeChangeError.InfrastructureFailure,
                exception.Message);
        }
    }

    private NarrativeChangeResult<NarrativeImpactAnalysisRecord> AssessAndPersistImpact(
        NarrativeChangeSetSnapshot changeSet,
        CancellationToken cancellationToken)
    {
        StructuralDependencyAssessment structural;
        SemanticDependencyAssessment semantic;
        try
        {
            structural = store.AssessStructuralDependencies(changeSet);
            semantic = semanticDependencyAssessor.Assess(changeSet, cancellationToken);
        }
        catch (Exception exception)
        {
            var failed = PersistAnalysis(
                changeSet.ChangeSetId,
                NarrativeImpactAnalysisStatus.Failed,
                [],
                [],
                JsonSerializer.Serialize(new { stage = "dependency_assessment", failure = exception.GetType().Name }),
                []);
            if (!failed.Succeeded)
            {
                return new NarrativeChangeResult<NarrativeImpactAnalysisRecord>(null, failed.Failure);
            }

            return NarrativeChangeResults.Fail<NarrativeImpactAnalysisRecord>(
                NarrativeChangeError.DependencyAssessmentFailed,
                exception.Message);
        }

        if (!structural.HasRelevantDependency && semantic.Finding == SemanticDependencyFinding.NoEvidenceFound)
        {
            return PersistAnalysis(
                changeSet.ChangeSetId,
                NarrativeImpactAnalysisStatus.NoRelevantDependency,
                [],
                [],
                JsonSerializer.Serialize(new
                {
                    structural = "no_relevant_dependency",
                    semantic = "no_evidence_found",
                    semanticEvidence = semantic.EvidenceJson
                }),
                []);
        }

        if (!structural.HasRelevantDependency && semantic.Finding == SemanticDependencyFinding.Uncertain)
        {
            var warning = UncertaintyWarning(semantic);
            return PersistAnalysis(
                changeSet.ChangeSetId,
                NarrativeImpactAnalysisStatus.Uncertain,
                [],
                [],
                JsonSerializer.Serialize(new
                {
                    structural = "no_relevant_dependency",
                    semantic = "uncertain",
                    semanticEvidence = semantic.EvidenceJson,
                    semantic.UncertaintyReason,
                    semantic.CoverageMetadata
                }),
                [warning]);
        }

        NarrativeImpactAnalysisResult impact;
        try
        {
            impact = impactAnalyzer.Analyze(changeSet, structural, semantic, cancellationToken);
        }
        catch (Exception exception)
        {
            var failed = PersistAnalysis(
                changeSet.ChangeSetId,
                NarrativeImpactAnalysisStatus.Failed,
                [],
                [],
                JsonSerializer.Serialize(new { stage = "impact_analysis", failure = exception.GetType().Name }),
                []);
            if (!failed.Succeeded)
            {
                return new NarrativeChangeResult<NarrativeImpactAnalysisRecord>(null, failed.Failure);
            }

            return NarrativeChangeResults.Fail<NarrativeImpactAnalysisRecord>(
                NarrativeChangeError.ImpactAnalysisFailed,
                exception.Message);
        }

        var warnings = impact.Warnings.ToList();
        if (semantic.Finding == SemanticDependencyFinding.Uncertain)
        {
            warnings.Add(UncertaintyWarning(semantic));
        }

        var impactedEdges = impact.AffectedDependencyEdgeIds
            .Concat(structural.Edges.Select(edge => edge.EdgeId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var affectedObjects = impact.AffectedObjectIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var evidence = JsonSerializer.Serialize(new
        {
            structuralEdges = structural.Edges.OrderBy(edge => edge.EdgeId, StringComparer.Ordinal),
            semantic = semantic.Finding.ToString(),
            semanticEvidence = semantic.EvidenceJson,
            impactEvidence = impact.EvidenceJson
        });

        var persisted = PersistAnalysis(
            changeSet.ChangeSetId,
            impact.Status,
            affectedObjects,
            impactedEdges,
            evidence,
            warnings);
        if (!persisted.Succeeded || impact.Status != NarrativeImpactAnalysisStatus.Failed)
        {
            return persisted;
        }

        return NarrativeChangeResults.Fail<NarrativeImpactAnalysisRecord>(NarrativeChangeError.ImpactAnalysisFailed);
    }

    private NarrativeChangeResult<NarrativeImpactAnalysisRecord> PersistAnalysis(
        string changeSetId,
        NarrativeImpactAnalysisStatus status,
        IReadOnlyList<string> affectedObjectIds,
        IReadOnlyList<string> affectedEdgeIds,
        string evidenceJson,
        IReadOnlyList<string> warnings)
    {
        var affectedSetJson = JsonSerializer.Serialize(new
        {
            objectIds = affectedObjectIds.OrderBy(value => value, StringComparer.Ordinal),
            dependencyEdgeIds = affectedEdgeIds.OrderBy(value => value, StringComparer.Ordinal)
        });
        var warningsJson = JsonSerializer.Serialize(warnings.OrderBy(value => value, StringComparer.Ordinal));
        var persisted = store.PersistImpactAnalysis(new PersistImpactAnalysisRequest(
            changeSetId,
            status,
            affectedSetJson,
            evidenceJson,
            warningsJson,
            warnings));
        return persisted.Succeeded
            ? NarrativeChangeResults.Success(persisted.Value!)
            : new NarrativeChangeResult<NarrativeImpactAnalysisRecord>(null, persisted.Failure);
    }

    private NarrativeChangeResult<string?> ResolveAfterPayloadDigest(
        WorkingNarrativeChangeInput input,
        CancellationToken cancellationToken)
    {
        var requiresPayload = input.ChangeKind is NarrativeChangeKind.Add or NarrativeChangeKind.Modify or NarrativeChangeKind.Reintroduce;
        if (!requiresPayload)
        {
            return input.AfterPayload is null && string.IsNullOrWhiteSpace(input.ExistingAfterPayloadDigest)
                ? NarrativeChangeResults.Success<string?>(null)
                : NarrativeChangeResults.Fail<string?>(NarrativeChangeError.InvalidChangeOperation);
        }

        try
        {
            if (input.AfterPayload is not null)
            {
                if (!input.AfterPayload.CanRead)
                {
                    return NarrativeChangeResults.Fail<string?>(NarrativeChangeError.PayloadMissing);
                }

                return NarrativeChangeResults.Success<string?>(blobStore.Stage(
                    input.AfterPayload,
                    input.ExistingAfterPayloadDigest,
                    cancellationToken).Digest);
            }

            if (string.IsNullOrWhiteSpace(input.ExistingAfterPayloadDigest))
            {
                return NarrativeChangeResults.Fail<string?>(NarrativeChangeError.PayloadMissing);
            }

            return blobStore.Verify(input.ExistingAfterPayloadDigest, cancellationToken)
                ? NarrativeChangeResults.Success<string?>(input.ExistingAfterPayloadDigest.ToLowerInvariant())
                : NarrativeChangeResults.Fail<string?>(NarrativeChangeError.PayloadVerificationFailed);
        }
        catch (Exception exception)
        {
            return NarrativeChangeResults.Fail<string?>(NarrativeChangeError.PayloadVerificationFailed, exception.Message);
        }
    }

    private static string UncertaintyWarning(SemanticDependencyAssessment semantic) =>
        $"Semantic dependency assessment is UNCERTAIN: {semantic.UncertaintyReason ?? "coverage is insufficient"}; " +
        $"coverage={semantic.CoverageMetadata ?? "not supplied"}.";
}
