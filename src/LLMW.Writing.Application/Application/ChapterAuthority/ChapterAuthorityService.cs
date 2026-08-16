using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.ChapterAuthority;

public sealed class ChapterAuthorityService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };

    private readonly IImmutableBlobStore blobStore;
    private readonly IAuthorityTransactionCoordinator transactionCoordinator;
    private readonly IChapterAuthorityStore store;
    private readonly IChapterReviewer reviewer;
    private readonly IAuthoritySurfaceHealthGate authoritySurfaceHealthGate;
    private readonly IAuthorizationService authorizationService;
    private readonly IEffectiveOversightSource oversightSource;
    private readonly IDelegatedDecisionSink delegatedDecisionSink;

    public ChapterAuthorityService(
        IImmutableBlobStore blobStore,
        IAuthorityTransactionCoordinator transactionCoordinator,
        IChapterAuthorityStore store,
        IChapterReviewer reviewer,
        IAuthoritySurfaceHealthGate authoritySurfaceHealthGate,
        IAuthorizationService? authorizationService = null,
        IEffectiveOversightSource? oversightSource = null,
        IDelegatedDecisionSink? delegatedDecisionSink = null)
    {
        this.blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        this.transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.reviewer = reviewer ?? throw new ArgumentNullException(nameof(reviewer));
        this.authoritySurfaceHealthGate = authoritySurfaceHealthGate ?? throw new ArgumentNullException(nameof(authoritySurfaceHealthGate));
        this.authorizationService = authorizationService ?? DenyAllAuthorizationService.Instance;
        this.oversightSource = oversightSource ?? FailClosedOversightSource.Instance;
        this.delegatedDecisionSink = delegatedDecisionSink ?? NullDelegatedDecisionSink.Instance;
    }

    public ChapterAuthorityResult<SubmitChapterDraftResult> SubmitChapterDraft(
        SubmitChapterDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var authorization = Authorize<SubmitChapterDraftResult>(
            command.Principal,
            Capability.AuthoritySubmit);
        if (authorization is not null)
        {
            return authorization;
        }

        if (!File.Exists(command.DraftPath))
        {
            return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(ChapterAuthorityError.DraftMissing);
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(command.DraftPath)))
        {
            return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(ChapterAuthorityError.UnsupportedDraftFormat);
        }

        var context = store.LoadSubmissionContext(command.ChapterId);
        if (context is null || command.Eligibility == SubmissionEligibility.None)
        {
            return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(ChapterAuthorityError.SubmissionNotEligible);
        }

        if (context.ActiveSubmissionExists)
        {
            return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(ChapterAuthorityError.ActiveSubmissionExists);
        }

        var health = authoritySurfaceHealthGate.Check(AuthoritySurfaceHealthRequest.Standard, cancellationToken);
        if (!health.IsHealthy)
        {
            return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(
                ChapterAuthorityError.AuthorityDirty,
                FormatHealthFailure(health));
        }

        var projectTransition = ProjectSubmissionStateMachine.Instance.Transition(
            context.ProjectSubmissionState,
            ProjectSubmissionEvent.Submit,
            new ProjectSubmissionContext(command.Eligibility, context.ActiveSubmissionExists, AcceptanceDecisionContext.Unauthorized));
        if (!projectTransition.Allowed)
        {
            return TransitionFailure<SubmitChapterDraftResult>(projectTransition.Rejection);
        }

        var chapterSubmitted = ChapterStateMachine.Instance.Transition(
            context.ChapterState,
            ChapterEvent.Submit,
            ChapterContext.Empty);
        if (!chapterSubmitted.Allowed)
        {
            return TransitionFailure<SubmitChapterDraftResult>(chapterSubmitted.Rejection);
        }

        var chapterReviewing = ChapterStateMachine.Instance.Transition(
            chapterSubmitted.NextState!.Value,
            ChapterEvent.BeginReview,
            ChapterContext.Empty);
        var candidateReviewing = CandidateStateMachine.Instance.Transition(
            CandidateState.Created,
            CandidateEvent.BeginReview,
            CandidateContext.Empty);
        var projectReviewing = ProjectSubmissionStateMachine.Instance.Transition(
            projectTransition.NextState!.Value,
            ProjectSubmissionEvent.CandidatePersisted,
            ProjectSubmissionContext.Empty);
        if (!chapterReviewing.Allowed || !candidateReviewing.Allowed || !projectReviewing.Allowed)
        {
            return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(ChapterAuthorityError.FsmTransitionRejected);
        }

        try
        {
            var transaction = transactionCoordinator.Begin("chapter_submission", command.IdempotencyKey);
            if (transaction.State != AuthorityTransactionState.Pending || transaction.Existing)
            {
                return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(ChapterAuthorityError.ActiveSubmissionExists);
            }

            using var source = new FileStream(command.DraftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var artifact = transactionCoordinator.StageBlob(transaction, source, cancellationToken: cancellationToken);
            var result = store.PersistCandidate(
                transaction,
                new PersistCandidateRequest(
                    command.ChapterId,
                    Path.GetFullPath(command.DraftPath),
                    artifact.Digest,
                    context.ParentCandidateId,
                    command.Eligibility));
            return ChapterAuthorityResults.Success(result);
        }
        catch (Exception exception)
        {
            return ChapterAuthorityResults.Fail<SubmitChapterDraftResult>(
                ChapterAuthorityError.InfrastructureFailure,
                exception.Message);
        }
    }

    public ChapterAuthorityResult<ReviewChapterCandidateResult> ReviewChapterCandidate(
        ReviewChapterCandidateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var authorization = Authorize<ReviewChapterCandidateResult>(
            command.Principal,
            Capability.AuthorityReview);
        if (authorization is not null)
        {
            return authorization;
        }

        var context = store.LoadReviewContext(command.CandidateId);
        if (context is null)
        {
            return ChapterAuthorityResults.Fail<ReviewChapterCandidateResult>(ChapterAuthorityError.CandidateNotFound);
        }

        if (context.CandidateState != CandidateState.UnderReview ||
            context.ChapterState != ChapterState.UnderReview ||
            context.ProjectSubmissionState != ProjectSubmissionState.Reviewing)
        {
            return ChapterAuthorityResults.Fail<ReviewChapterCandidateResult>(ChapterAuthorityError.CandidateNotCurrent);
        }

        if (!blobStore.Verify(context.ArtifactDigest, cancellationToken))
        {
            return ChapterAuthorityResults.Fail<ReviewChapterCandidateResult>(ChapterAuthorityError.ArtifactVerificationFailed);
        }

        try
        {
            using var content = blobStore.OpenRead(context.ArtifactDigest);
            var decision = reviewer.Review(
                new CandidateReviewInput(
                    context.CandidateId,
                    context.ChapterId,
                    context.ArtifactDigest,
                    context.SourceDraftPath),
                content,
                cancellationToken);

            var projectEvent = decision.Outcome == ChapterReviewOutcome.Pass
                ? ProjectSubmissionEvent.ReviewPassed
                : ProjectSubmissionEvent.ReviewFailed;
            var projectTransition = ProjectSubmissionStateMachine.Instance.Transition(
                context.ProjectSubmissionState,
                projectEvent,
                ProjectSubmissionContext.Empty);
            if (!projectTransition.Allowed)
            {
                return TransitionFailure<ReviewChapterCandidateResult>(projectTransition.Rejection);
            }

            if (decision.Outcome == ChapterReviewOutcome.Fail)
            {
                var candidateFailed = CandidateStateMachine.Instance.Transition(
                    context.CandidateState,
                    CandidateEvent.FailReview,
                    CandidateContext.Empty);
                var chapterFailed = ChapterStateMachine.Instance.Transition(
                    context.ChapterState,
                    ChapterEvent.FailReview,
                    ChapterContext.Empty);
                var chapterDraft = chapterFailed.Allowed
                    ? ChapterStateMachine.Instance.Transition(
                        chapterFailed.NextState!.Value,
                        ChapterEvent.ReturnToDraft,
                        ChapterContext.Empty)
                    : null;
                if (!candidateFailed.Allowed || chapterDraft is null || !chapterDraft.Allowed)
                {
                    return ChapterAuthorityResults.Fail<ReviewChapterCandidateResult>(ChapterAuthorityError.FsmTransitionRejected);
                }
            }

            return ChapterAuthorityResults.Success(
                store.PersistReview(new PersistReviewRequest(context, decision)));
        }
        catch (Exception exception)
        {
            return ChapterAuthorityResults.Fail<ReviewChapterCandidateResult>(
                ChapterAuthorityError.InfrastructureFailure,
                exception.Message);
        }
    }

    public ChapterAuthorityResult<AcceptChapterCandidateResult> AcceptChapterCandidate(
        AcceptChapterCandidateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var authorization = Authorize<AcceptChapterCandidateResult>(
            command.Principal,
            Capability.AuthorityAccept);
        if (authorization is not null)
        {
            return authorization;
        }

        var formal = EnsureFormalNarrativeDecision<AcceptChapterCandidateResult>(command.Principal, out var authorityKind, out var formalPolicy);
        if (formal is not null)
        {
            return formal;
        }

        var context = store.LoadAcceptanceContext(command.CandidateId);
        if (context is null)
        {
            return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(ChapterAuthorityError.CandidateNotFound);
        }

        if (!StringComparer.Ordinal.Equals(context.IdempotencyKey, command.IdempotencyKey))
        {
            return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(ChapterAuthorityError.CandidateNotCurrent);
        }

        try
        {
            if (context.TransactionState is AuthorityTransactionState.CommittedButDirty or AuthorityTransactionState.Complete)
            {
                var recoveryAuthorization = Authorize<AcceptChapterCandidateResult>(
                    command.Principal,
                    Capability.AuthorityAccept);
                if (recoveryAuthorization is not null)
                {
                    return recoveryAuthorization;
                }

                var recovered = store.RecoverAcceptance(context, cancellationToken);
                var recoveredProvenance = RepairDelegatedProvenance(
                    context.AuthorizationSnapshotJson,
                    recovered.AcceptanceId,
                    recovered.TransactionId,
                    command.Principal,
                    context.ChapterId);
                return ChapterAuthorityResults.Success(recovered with { ProvenanceConflict = recoveredProvenance == DelegatedProvenanceWriteResult.Conflict });
            }

            if (context.TransactionState == AuthorityTransactionState.RecoveryRequired)
            {
                return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(ChapterAuthorityError.RecoveryRequired);
            }

            if (context.ReviewOutcome != ChapterReviewOutcome.Pass)
            {
                return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(ChapterAuthorityError.ReviewNotPassed);
            }

            if (!blobStore.Verify(context.ArtifactDigest, cancellationToken))
            {
                return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(ChapterAuthorityError.ArtifactVerificationFailed);
            }

            var decision = new AcceptanceDecisionContext(
                true,
                authorityKind,
                authorityKind == DecisionAuthorityKind.AgentDelegated
                    ? NarrativeOversightMode.Auto
                    : command.OversightMode,
                BypassPermissions: false);

            var transitionAuthorization = Authorize<AcceptChapterCandidateResult>(
                command.Principal,
                Capability.AuthorityAccept);
            if (transitionAuthorization is not null)
            {
                return transitionAuthorization;
            }

            var requiresPreparation = false;
            if (context.ProjectSubmissionState == ProjectSubmissionState.Resolving)
            {
                var projectAccepting = ProjectSubmissionStateMachine.Instance.Transition(
                    context.ProjectSubmissionState,
                    ProjectSubmissionEvent.BeginAcceptance,
                    new ProjectSubmissionContext(SubmissionEligibility.None, true, decision));
                var candidateAccepted = CandidateStateMachine.Instance.Transition(
                    context.CandidateState,
                    CandidateEvent.Accept,
                    new CandidateContext(decision, false));
                var chapterAccepted = ChapterStateMachine.Instance.Transition(
                    context.ChapterState,
                    ChapterEvent.Accept,
                    new ChapterContext(decision));
                var projectCommitting = projectAccepting.Allowed
                    ? ProjectSubmissionStateMachine.Instance.Transition(
                        projectAccepting.NextState!.Value,
                        ProjectSubmissionEvent.BeginCommit,
                        ProjectSubmissionContext.Empty)
                    : null;
                var projectRevalidating = projectCommitting is { Allowed: true }
                    ? ProjectSubmissionStateMachine.Instance.Transition(
                        projectCommitting.NextState!.Value,
                        ProjectSubmissionEvent.CommitCompleted,
                        ProjectSubmissionContext.Empty)
                    : null;
                var projectIdle = projectRevalidating is { Allowed: true }
                    ? ProjectSubmissionStateMachine.Instance.Transition(
                        projectRevalidating.NextState!.Value,
                        ProjectSubmissionEvent.RevalidationCompleted,
                        ProjectSubmissionContext.Empty)
                    : null;
                var chapterMaterialized = chapterAccepted.Allowed
                    ? ChapterStateMachine.Instance.Transition(
                        chapterAccepted.NextState!.Value,
                        ChapterEvent.Materialize,
                        ChapterContext.Empty)
                    : null;
                if (!projectAccepting.Allowed || !candidateAccepted.Allowed || !chapterAccepted.Allowed ||
                    projectCommitting is null || !projectCommitting.Allowed ||
                    projectRevalidating is null || !projectRevalidating.Allowed ||
                    projectIdle is null || !projectIdle.Allowed ||
                    chapterMaterialized is null || !chapterMaterialized.Allowed)
                {
                    return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(ChapterAuthorityError.FsmTransitionRejected);
                }

                requiresPreparation = true;
            }
            else if (context.ProjectSubmissionState == ProjectSubmissionState.Committing &&
                      (context.AcceptanceId is null || context.ManuscriptRevisionId is null ||
                       context.MaterializedRelativePath is null))
            {
                requiresPreparation = true;
            }
            else if (context.ProjectSubmissionState != ProjectSubmissionState.Committing)
            {
                return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(ChapterAuthorityError.CandidateNotCurrent);
            }

            var health = authoritySurfaceHealthGate.Check(AuthoritySurfaceHealthRequest.Standard, cancellationToken);
            if (!health.IsHealthy)
            {
                return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(
                    ChapterAuthorityError.AuthorityDirty,
                    FormatHealthFailure(health));
            }

            var commitAuthorization = Authorize<AcceptChapterCandidateResult>(
                command.Principal,
                Capability.AuthorityAccept);
            if (commitAuthorization is not null)
            {
                return commitAuthorization;
            }

            var commitFormal = EnsureFormalNarrativeDecision<AcceptChapterCandidateResult>(command.Principal, out authorityKind, out var commitPolicy);
            if (commitFormal is not null)
            {
                return commitFormal;
            }

            var acceptedById = command.Principal?.Kind == PrincipalKind.AgentRun
                ? command.Principal.ToString()
                : command.Principal?.TrustedInstanceId ?? "user-interactive";

            if (requiresPreparation)
            {
                var extension = Path.GetExtension(context.SourceDraftPath).ToLowerInvariant();
                var relativePath = Path.Combine("Manuscript", "current", context.ChapterId + extension)
                    .Replace(Path.DirectorySeparatorChar, '/');
                context = store.PrepareAcceptance(
                    new PrepareAcceptanceRequest(context, decision, acceptedById, relativePath));
            }

            string? snapshotJson = null;
            if (authorityKind == DecisionAuthorityKind.AgentDelegated && commitPolicy is not null)
            {
                snapshotJson = FormalAuthorizationSnapshot.Capture(
                    context.AcceptanceId ?? command.CandidateId,
                    context.TransactionId,
                    commitPolicy,
                    acceptedById,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).WriteCanonical();
                context = context with { AuthorizationSnapshotJson = snapshotJson };
            }

            var committed = store.CommitAcceptance(context, cancellationToken);
            var provenance = RepairDelegatedProvenance(
                context.AuthorizationSnapshotJson ?? snapshotJson,
                committed.AcceptanceId,
                committed.TransactionId,
                command.Principal,
                context.ChapterId);
            return ChapterAuthorityResults.Success(committed with { ProvenanceConflict = provenance == DelegatedProvenanceWriteResult.Conflict });
        }
        catch (Exception exception)
        {
            try
            {
                if (transactionCoordinator.Inspect(context.TransactionId).State == AuthorityTransactionState.CommittedButDirty)
                {
                    return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(
                        ChapterAuthorityError.AuthorityDirty,
                        exception.Message);
                }
            }
            catch (Exception)
            {
            }

            return ChapterAuthorityResults.Fail<AcceptChapterCandidateResult>(
                ChapterAuthorityError.InfrastructureFailure,
                exception.Message);
        }
    }

    private DelegatedProvenanceWriteResult RepairDelegatedProvenance(
        string? snapshotJson,
        string decisionId,
        string transactionId,
        CallerPrincipal? principal,
        string fallbackScopeId)
    {
        var snapshot = FormalAuthorizationSnapshot.TryParse(snapshotJson);
        if (snapshot is null)
        {
            return DelegatedProvenanceWriteResult.Written;
        }

        var record = (snapshot with
        {
            DecisionId = decisionId,
            TransactionId = transactionId,
            WinningScopeId = string.IsNullOrWhiteSpace(snapshot.WinningScopeId)
                ? principal?.ProjectScope?.ProjectId.ToString("D") ?? fallbackScopeId
                : snapshot.WinningScopeId
        }).ToDelegatedDecision();
        try
        {
            return delegatedDecisionSink.Record(record);
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

    private static string FormatHealthFailure(AuthoritySurfaceHealth health) =>
        string.Join("; ", health.Issues.Select(issue => $"{issue.Kind}:{issue.RelativePath}:{issue.Detail}"));

    private ChapterAuthorityResult<T>? Authorize<T>(
        CallerPrincipal? principal,
        Capability capability)
    {
        var decision = authorizationService.Authorize(
            principal,
            new AuthorizationRequest(capability));
        return decision.Decision switch
        {
            CapabilityDecisionKind.Allowed => null,
            CapabilityDecisionKind.RequiresApproval => ChapterAuthorityResults.Fail<T>(
                ChapterAuthorityError.ApprovalRequired,
                string.Join(',', decision.Reasons)),
            _ => ChapterAuthorityResults.Fail<T>(
                principal is null ? ChapterAuthorityError.InvalidPrincipal : ChapterAuthorityError.CapabilityDenied,
                string.Join(',', decision.Reasons))
        };
    }

    private ChapterAuthorityResult<T>? EnsureFormalNarrativeDecision<T>(
        CallerPrincipal? principal,
        out DecisionAuthorityKind authorityKind,
        out EffectiveOversightPolicy? policy)
    {
        authorityKind = DecisionAuthorityKind.AuthorConfirmed;
        policy = null;
        if (principal?.Kind == PrincipalKind.UserInteractive)
        {
            return null;
        }

        if (principal?.Kind != PrincipalKind.AgentRun)
        {
            return ChapterAuthorityResults.Fail<T>(
                ChapterAuthorityError.AcceptanceNotAuthorized,
                "Formal acceptance requires USER_INTERACTIVE or effective AGENT_DELEGATED Oversight.");
        }

        policy = oversightSource.ResolveForPrincipal(principal);
        if (!policy.NarrativeDelegated)
        {
            return ChapterAuthorityResults.Fail<T>(
                ChapterAuthorityError.AcceptanceNotAuthorized,
                "Effective Oversight remains AUTHOR_CONFIRMED_REQUIRED.");
        }

        authorityKind = DecisionAuthorityKind.AgentDelegated;
        return null;
    }

    private static ChapterAuthorityResult<T> TransitionFailure<T>(AuthorityRejection? rejection)
    {
        var code = rejection?.Code switch
        {
            AuthorityRejectionCode.ActiveSubmissionExists => ChapterAuthorityError.ActiveSubmissionExists,
            AuthorityRejectionCode.EligibilityDenied => ChapterAuthorityError.SubmissionNotEligible,
            AuthorityRejectionCode.AcceptanceNotAuthorized => ChapterAuthorityError.AcceptanceNotAuthorized,
            _ => ChapterAuthorityError.FsmTransitionRejected
        };
        return ChapterAuthorityResults.Fail<T>(code, rejection?.Detail);
    }
}
