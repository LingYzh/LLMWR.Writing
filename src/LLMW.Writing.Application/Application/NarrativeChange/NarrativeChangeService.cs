using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.NarrativeChange;

public sealed class NarrativeChangeService
{
    private readonly IImmutableBlobStore blobStore;
    private readonly INarrativeChangeStore store;
    private readonly ISemanticDependencyAssessor semanticDependencyAssessor;
    private readonly INarrativeImpactAnalyzer impactAnalyzer;
    private readonly IAuthoritySurfaceHealthGate authoritySurfaceHealthGate;
    private readonly IAuthorizationService authorizationService;
    private readonly IEffectiveOversightSource oversightSource;
    private readonly IDelegatedDecisionSink delegatedDecisionSink;

    public NarrativeChangeService(
        IImmutableBlobStore blobStore,
        INarrativeChangeStore store,
        ISemanticDependencyAssessor semanticDependencyAssessor,
        INarrativeImpactAnalyzer impactAnalyzer,
        IAuthoritySurfaceHealthGate authoritySurfaceHealthGate,
        IAuthorizationService? authorizationService = null,
        IEffectiveOversightSource? oversightSource = null,
        IDelegatedDecisionSink? delegatedDecisionSink = null)
    {
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.semanticDependencyAssessor = semanticDependencyAssessor ?? throw new ArgumentNullException(nameof(semanticDependencyAssessor));
        this.impactAnalyzer = impactAnalyzer ?? throw new ArgumentNullException(nameof(impactAnalyzer));
        this.authoritySurfaceHealthGate = authoritySurfaceHealthGate ?? throw new ArgumentNullException(nameof(authoritySurfaceHealthGate));
        this.authorizationService = authorizationService ?? DenyAllAuthorizationService.Instance;
        this.oversightSource = oversightSource ?? FailClosedOversightSource.Instance;
        this.delegatedDecisionSink = delegatedDecisionSink ?? NullDelegatedDecisionSink.Instance;
    }

    public NarrativeChangeResult<CreateWorkingNarrativeChangeSetResult> CreateWorkingChangeSet(
        CreateWorkingNarrativeChangeSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var authorization = Authorize<CreateWorkingNarrativeChangeSetResult>(
            command.Principal,
            Capability.StructuredWrite);
        if (authorization is not null)
        {
            return authorization;
        }

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
        var authorization = Authorize<ApplyNarrativeChangeSetResult>(
            command.Principal,
            Capability.AuthorityAccept);
        if (authorization is not null)
        {
            return authorization;
        }

        var principalKind = command.Principal?.Kind;
        var existing = store.LoadChangeSet(command.ChangeSetId);
        if (existing is not null && StringComparer.Ordinal.Equals(existing.Status, "applied"))
        {
            var appliedExisting = store.Apply(
                new NarrativeApplyStoreRequest(
                    command.ChangeSetId,
                    command.IdempotencyKey,
                    command.DeciderKind,
                    command.Principal?.Kind == PrincipalKind.AgentRun
                        ? command.Principal.ToString()
                        : command.Principal?.TrustedInstanceId ?? command.DeciderId,
                    existing.ImpactAnalysisId),
                cancellationToken);
            if (!appliedExisting.Succeeded)
            {
                return new NarrativeChangeResult<ApplyNarrativeChangeSetResult>(null, appliedExisting.Failure);
            }

            var recoveredSnapshot = store.LoadAuthorizationSnapshot(appliedExisting.Value!.TransactionId);
            var recoveredProvenance = RecordFrozenDelegatedProvenance(
                recoveredSnapshot,
                appliedExisting.Value.ChangeSetId,
                appliedExisting.Value.TransactionId,
                command.Principal);
            return NarrativeChangeResults.Success(new ApplyNarrativeChangeSetResult(
                appliedExisting.Value.ChangeSetId,
                appliedExisting.Value.TransactionId,
                appliedExisting.Value.ImpactAnalysisId,
                appliedExisting.Value.TransactionState,
                appliedExisting.Value.Existing,
                [],
                recoveredProvenance == DelegatedProvenanceWriteResult.Conflict));
        }

        if (principalKind == PrincipalKind.UserInteractive)
        {
            if (command.DeciderKind != NarrativeDecisionKind.AuthorConfirmed)
            {
                return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                    NarrativeChangeError.DecisionAuthorityNotAvailable,
                    command.DeciderKind.ToString());
            }
        }
        else if (principalKind == PrincipalKind.AgentRun)
        {
            var policy = oversightSource.ResolveForPrincipal(command.Principal);
            if (!policy.NarrativeDelegated || command.DeciderKind != NarrativeDecisionKind.AgentDelegated)
            {
                return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                    NarrativeChangeError.DecisionAuthorityNotAvailable,
                    "Effective Oversight remains AUTHOR_CONFIRMED_REQUIRED.");
            }
        }
        else
        {
            return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                NarrativeChangeError.DecisionAuthorityNotAvailable,
                "Formal narrative change requires USER_INTERACTIVE or effective AGENT_DELEGATED Oversight.");
        }

        if (string.IsNullOrWhiteSpace(command.ChangeSetId) || string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(NarrativeChangeError.ChangeSetNotApplicable);
        }

        var resolvingIds = new HashSet<string>(
            command.ResolvingReconcileObjectIds ?? [],
            StringComparer.Ordinal);
        var healthRequest = new AuthoritySurfaceHealthRequest(
            resolvingIds,
            command.ResolvingReconcilePhysicalDigests);
        var initialHealth = authoritySurfaceHealthGate.Check(healthRequest, cancellationToken);
        if (!initialHealth.IsHealthy)
        {
            return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                NarrativeChangeError.AuthorityDirty,
                FormatHealthFailure(initialHealth));
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

            var finalHealth = authoritySurfaceHealthGate.Check(healthRequest, cancellationToken);
            if (!finalHealth.IsHealthy)
            {
                return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                    NarrativeChangeError.AuthorityDirty,
                    FormatHealthFailure(finalHealth));
            }

            var commitAuthorization = Authorize<ApplyNarrativeChangeSetResult>(
                command.Principal,
                Capability.AuthorityAccept);
            if (commitAuthorization is not null)
            {
                return commitAuthorization;
            }

            var deciderId = command.Principal?.Kind == PrincipalKind.AgentRun
                ? command.Principal.ToString()
                : command.Principal?.TrustedInstanceId ?? command.DeciderId;
            string? snapshotJson = null;
            if (command.DeciderKind == NarrativeDecisionKind.AgentDelegated)
            {
                var policy = oversightSource.ResolveForPrincipal(command.Principal);
                snapshotJson = FormalAuthorizationSnapshot.Capture(
                    command.ChangeSetId,
                    changeSet.TransactionId,
                    policy,
                    deciderId ?? "agent",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).WriteCanonical();
            }

            var applied = store.Apply(
                new NarrativeApplyStoreRequest(
                    command.ChangeSetId,
                    command.IdempotencyKey,
                    command.DeciderKind,
                    deciderId,
                    analysis?.ImpactAnalysisId ?? changeSet.ImpactAnalysisId,
                    snapshotJson),
                cancellationToken);
            if (!applied.Succeeded)
            {
                return new NarrativeChangeResult<ApplyNarrativeChangeSetResult>(null, applied.Failure);
            }

            var provenance = RecordFrozenDelegatedProvenance(
                snapshotJson,
                applied.Value!.ChangeSetId,
                applied.Value.TransactionId,
                command.Principal);

            var warnings = analysis?.Warnings ?? [];
            return NarrativeChangeResults.Success(new ApplyNarrativeChangeSetResult(
                applied.Value.ChangeSetId,
                applied.Value.TransactionId,
                applied.Value.ImpactAnalysisId,
                applied.Value.TransactionState,
                applied.Value.Existing,
                warnings,
                provenance == DelegatedProvenanceWriteResult.Conflict));
        }
        catch (Exception exception)
        {
            return NarrativeChangeResults.Fail<ApplyNarrativeChangeSetResult>(
                NarrativeChangeError.InfrastructureFailure,
                exception.Message);
        }
    }

    private DelegatedProvenanceWriteResult RecordFrozenDelegatedProvenance(
        string? snapshotJson,
        string decisionId,
        string transactionId,
        CallerPrincipal? principal)
    {
        var snapshot = FormalAuthorizationSnapshot.TryParse(snapshotJson);
        if (snapshot is null)
        {
            return DelegatedProvenanceWriteResult.Written;
        }

        try
        {
            return delegatedDecisionSink.Record((snapshot with
            {
                DecisionId = decisionId,
                TransactionId = transactionId,
                WinningScopeId = string.IsNullOrWhiteSpace(snapshot.WinningScopeId)
                    ? principal?.ProjectScope?.ProjectId.ToString("D") ?? decisionId
                    : snapshot.WinningScopeId
            }).ToDelegatedDecision());
        }
        catch (DelegatedDecisionConflictException)
        {
            return DelegatedProvenanceWriteResult.Conflict;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            return DelegatedProvenanceWriteResult.Unavailable;
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

    private static string FormatHealthFailure(AuthoritySurfaceHealth health) =>
        string.Join("; ", health.Issues.Select(issue => $"{issue.Kind}:{issue.RelativePath}:{issue.Detail}"));

    private NarrativeChangeResult<T>? Authorize<T>(
        CallerPrincipal? principal,
        Capability capability)
    {
        var decision = authorizationService.Authorize(principal, new AuthorizationRequest(capability));
        return decision.Decision switch
        {
            CapabilityDecisionKind.Allowed => null,
            CapabilityDecisionKind.RequiresApproval => NarrativeChangeResults.Fail<T>(
                NarrativeChangeError.ApprovalRequired,
                string.Join(',', decision.Reasons)),
            _ => NarrativeChangeResults.Fail<T>(
                principal is null ? NarrativeChangeError.InvalidPrincipal : NarrativeChangeError.CapabilityDenied,
                string.Join(',', decision.Reasons))
        };
    }
}
