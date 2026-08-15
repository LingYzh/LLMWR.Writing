using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Authority.Arc;
using LLMW.Writing.Domain.Authority.Candidate;
using LLMW.Writing.Domain.Authority.Chapter;
using LLMW.Writing.Domain.Authority.ProjectSubmission;
using LLMW.Writing.Domain.Authority.RevisionBarrier;
using LLMW.Writing.Domain.Authority.Storyline;
using LLMW.Writing.Domain.Narrative;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private static readonly AcceptanceDecisionContext AuthorDecision =
        new(true, DecisionAuthorityKind.AuthorConfirmed, NarrativeOversightMode.Manual, false);

    private static readonly AcceptanceDecisionContext AgentDecision =
        new(true, DecisionAuthorityKind.AgentDelegated, NarrativeOversightMode.Auto, false);

    private static readonly List<string> PassedTests = [];
    private static readonly List<MatrixMetric> Metrics = [];

    private static int Main()
    {
        try
        {
            Run(nameof(ProjectSubmissionMatrixIsExhaustive), ProjectSubmissionMatrixIsExhaustive);
            Run(nameof(CandidateMatrixIsExhaustive), CandidateMatrixIsExhaustive);
            Run(nameof(ChapterMatrixIsExhaustive), ChapterMatrixIsExhaustive);
            Run(nameof(RevisionBarrierMatrixIsExhaustive), RevisionBarrierMatrixIsExhaustive);
            Run(nameof(ArcMatrixIsExhaustive), ArcMatrixIsExhaustive);
            Run(nameof(StorylineMatrixIsExhaustive), StorylineMatrixIsExhaustive);
            Run(nameof(ProjectSubmissionHappyPathAndFailureBranches), ProjectSubmissionHappyPathAndFailureBranches);
            Run(nameof(ProjectSubmissionRejectsCancelAndWorkflowSkips), ProjectSubmissionRejectsCancelAndWorkflowSkips);
            Run(nameof(CandidateRetryRequiresNewIdentityAndSupersedeRequiresLineage), CandidateRetryRequiresNewIdentityAndSupersedeRequiresLineage);
            Run(nameof(ChapterFailureCannotMaterialize), ChapterFailureCannotMaterialize);
            Run(nameof(RevisionBarrierReleaseAndIdentityGuards), RevisionBarrierReleaseAndIdentityGuards);
            Run(nameof(AllConditionalGuardsRejectInvalidContext), AllConditionalGuardsRejectInvalidContext);
            Run(nameof(HigherScopeAuthorityRequiresSnapshotAndPreservesHistory), HigherScopeAuthorityRequiresSnapshotAndPreservesHistory);
            Run(nameof(DelegatedAuthoritySharesStateButPreservesProvenance), DelegatedAuthoritySharesStateButPreservesProvenance);
            Run(nameof(BypassAndAutoCannotRewriteAuthorityRules), BypassAndAutoCannotRewriteAuthorityRules);
            Run(nameof(AuthorityScopesUseIndependentTypes), AuthorityScopesUseIndependentTypes);
            RunWp06NarrativeChangeDomainTests();
            RunWp07RegistryDomainTests();
            RunWp09SecurityDomainTests();
            RunWp12SchedulerDomainTests();

            Console.WriteLine($"Domain Authority FSM tests passed ({PassedTests.Count}).");
            foreach (var test in PassedTests)
            {
                Console.WriteLine($"PASS {test}");
            }

            foreach (var metric in Metrics)
            {
                Console.WriteLine(
                    $"MATRIX {metric.Name} states={metric.States} events={metric.Events} total={metric.Total} " +
                    $"legal={metric.Legal} illegal={metric.Illegal} conditional={metric.Conditional} explicit=yes");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void ProjectSubmissionMatrixIsExhaustive()
    {
        string[] matrix =
        [
            "CIIIIIIII",
            "ILIIIIIII",
            "IILLLIIII",
            "IIIILCIII",
            "IIIIIILII",
            "IIIIIIILI",
            "IIIIIIIIL"
        ];
        var next = new Dictionary<(ProjectSubmissionState, ProjectSubmissionEvent), ProjectSubmissionState>
        {
            [(ProjectSubmissionState.Idle, ProjectSubmissionEvent.Submit)] = ProjectSubmissionState.Submitting,
            [(ProjectSubmissionState.Submitting, ProjectSubmissionEvent.CandidatePersisted)] = ProjectSubmissionState.Reviewing,
            [(ProjectSubmissionState.Reviewing, ProjectSubmissionEvent.ReviewPassed)] = ProjectSubmissionState.Resolving,
            [(ProjectSubmissionState.Reviewing, ProjectSubmissionEvent.ReviewFailed)] = ProjectSubmissionState.Idle,
            [(ProjectSubmissionState.Reviewing, ProjectSubmissionEvent.Cancel)] = ProjectSubmissionState.Idle,
            [(ProjectSubmissionState.Resolving, ProjectSubmissionEvent.Cancel)] = ProjectSubmissionState.Idle,
            [(ProjectSubmissionState.Resolving, ProjectSubmissionEvent.BeginAcceptance)] = ProjectSubmissionState.Accepting,
            [(ProjectSubmissionState.Accepting, ProjectSubmissionEvent.BeginCommit)] = ProjectSubmissionState.Committing,
            [(ProjectSubmissionState.Committing, ProjectSubmissionEvent.CommitCompleted)] = ProjectSubmissionState.Revalidating,
            [(ProjectSubmissionState.Revalidating, ProjectSubmissionEvent.RevalidationCompleted)] = ProjectSubmissionState.Idle
        };
        VerifyMatrix(
            "ProjectSubmission",
            matrix,
            next,
            (state, @event) => ProjectSubmissionStateMachine.Instance.Transition(state, @event, ProjectContext(@event)));
    }

    private static void CandidateMatrixIsExhaustive()
    {
        string[] matrix = ["LIIII", "ILLCI", "IIIII", "IIIII", "IIIIC", "IIIII"];
        var next = new Dictionary<(CandidateState, CandidateEvent), CandidateState>
        {
            [(CandidateState.Created, CandidateEvent.BeginReview)] = CandidateState.UnderReview,
            [(CandidateState.UnderReview, CandidateEvent.FailReview)] = CandidateState.Failed,
            [(CandidateState.UnderReview, CandidateEvent.CancelReview)] = CandidateState.Cancelled,
            [(CandidateState.UnderReview, CandidateEvent.Accept)] = CandidateState.Accepted,
            [(CandidateState.Accepted, CandidateEvent.Supersede)] = CandidateState.Superseded
        };
        VerifyMatrix(
            "Candidate",
            matrix,
            next,
            (state, @event) => CandidateStateMachine.Instance.Transition(state, @event, CandidateContextFor(@event)));
    }

    private static void ChapterMatrixIsExhaustive()
    {
        string[] matrix =
        [
            "LIIIIIII", "ILIIIIII", "IILIIIII", "IIILIIII",
            "IIIILICI", "IIIIILII", "IIIIIIIL", "IIIIIIII"
        ];
        var next = new Dictionary<(ChapterState, ChapterEvent), ChapterState>
        {
            [(ChapterState.OutlineContract, ChapterEvent.MarkReady)] = ChapterState.Ready,
            [(ChapterState.Ready, ChapterEvent.BeginDraft)] = ChapterState.Draft,
            [(ChapterState.Draft, ChapterEvent.Submit)] = ChapterState.Submitted,
            [(ChapterState.Submitted, ChapterEvent.BeginReview)] = ChapterState.UnderReview,
            [(ChapterState.UnderReview, ChapterEvent.FailReview)] = ChapterState.Failed,
            [(ChapterState.UnderReview, ChapterEvent.Accept)] = ChapterState.Accepted,
            [(ChapterState.Failed, ChapterEvent.ReturnToDraft)] = ChapterState.Draft,
            [(ChapterState.Accepted, ChapterEvent.Materialize)] = ChapterState.Materialized
        };
        VerifyMatrix(
            "Chapter",
            matrix,
            next,
            (state, @event) => ChapterStateMachine.Instance.Transition(
                state,
                @event,
                new ChapterContext(@event == ChapterEvent.Accept ? AuthorDecision : AcceptanceDecisionContext.Unauthorized)));
    }

    private static void RevisionBarrierMatrixIsExhaustive()
    {
        string[] matrix = ["LIIIIII", "ICCCIII", "IIIIICC"];
        var next = new Dictionary<(RevisionBarrierState, RevisionBarrierEvent), RevisionBarrierState>
        {
            [(RevisionBarrierState.Inactive, RevisionBarrierEvent.Activate)] = RevisionBarrierState.ActiveInitial,
            [(RevisionBarrierState.ActiveInitial, RevisionBarrierEvent.AuthorityCommitted)] = RevisionBarrierState.Resolving,
            [(RevisionBarrierState.ActiveInitial, RevisionBarrierEvent.ReviewFailed)] = RevisionBarrierState.Inactive,
            [(RevisionBarrierState.ActiveInitial, RevisionBarrierEvent.Cancel)] = RevisionBarrierState.Inactive,
            [(RevisionBarrierState.Resolving, RevisionBarrierEvent.CompleteResolution)] = RevisionBarrierState.Inactive,
            [(RevisionBarrierState.Resolving, RevisionBarrierEvent.SubmitRemediation)] = RevisionBarrierState.Resolving
        };
        VerifyMatrix(
            "RevisionBarrier",
            matrix,
            next,
            (state, @event) => RevisionBarrierStateMachine.Instance.Transition(state, @event, BarrierContext(@event)));
    }

    private static void ArcMatrixIsExhaustive()
    {
        string[] matrix = ["LI", "IC", "II"];
        var next = new Dictionary<(ArcState, ArcEvent), ArcState>
        {
            [(ArcState.Open, ArcEvent.BeginClosureReview)] = ArcState.UnderClosureReview,
            [(ArcState.UnderClosureReview, ArcEvent.Accept)] = ArcState.Accepted
        };
        VerifyMatrix(
            "Arc",
            matrix,
            next,
            (state, @event) => ArcStateMachine.Instance.Transition(
                state,
                @event,
                new ArcContext(@event == ArcEvent.Accept ? AuthorDecision : AcceptanceDecisionContext.Unauthorized)));
    }

    private static void StorylineMatrixIsExhaustive()
    {
        string[] matrix = ["LII", "ICI", "IIC", "III"];
        var next = new Dictionary<(StorylineState, StorylineEvent), StorylineState>
        {
            [(StorylineState.RevisionComplete, StorylineEvent.BeginFinalReview)] = StorylineState.UnderFinalReview,
            [(StorylineState.UnderFinalReview, StorylineEvent.AcceptFinal)] = StorylineState.FinalAccepted,
            [(StorylineState.FinalAccepted, StorylineEvent.BeginPostAcceptanceRevision)] = StorylineState.PostAcceptanceRevision
        };
        VerifyMatrix(
            "Storyline",
            matrix,
            next,
            (state, @event) => StorylineStateMachine.Instance.Transition(state, @event, StorylineContextFor(@event)));
    }

    private static void ProjectSubmissionHappyPathAndFailureBranches()
    {
        var state = ProjectSubmissionState.Idle;
        state = Next(ProjectSubmissionStateMachine.Instance.Transition(state, ProjectSubmissionEvent.Submit, ProjectContext(ProjectSubmissionEvent.Submit)));
        state = Next(ProjectSubmissionStateMachine.Instance.Transition(state, ProjectSubmissionEvent.CandidatePersisted, ProjectSubmissionContext.Empty));
        state = Next(ProjectSubmissionStateMachine.Instance.Transition(state, ProjectSubmissionEvent.ReviewPassed, ProjectSubmissionContext.Empty));
        state = Next(ProjectSubmissionStateMachine.Instance.Transition(state, ProjectSubmissionEvent.BeginAcceptance, ProjectContext(ProjectSubmissionEvent.BeginAcceptance)));
        state = Next(ProjectSubmissionStateMachine.Instance.Transition(state, ProjectSubmissionEvent.BeginCommit, ProjectSubmissionContext.Empty));
        state = Next(ProjectSubmissionStateMachine.Instance.Transition(state, ProjectSubmissionEvent.CommitCompleted, ProjectSubmissionContext.Empty));
        state = Next(ProjectSubmissionStateMachine.Instance.Transition(state, ProjectSubmissionEvent.RevalidationCompleted, ProjectSubmissionContext.Empty));
        AssertEqual(ProjectSubmissionState.Idle, state, "Happy path did not return to IDLE.");

        AssertNext(ProjectSubmissionState.Idle, ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Reviewing, ProjectSubmissionEvent.ReviewFailed, ProjectSubmissionContext.Empty));
        AssertNext(ProjectSubmissionState.Idle, ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Reviewing, ProjectSubmissionEvent.Cancel, ProjectSubmissionContext.Empty));
        AssertNext(ProjectSubmissionState.Idle, ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Resolving, ProjectSubmissionEvent.Cancel, ProjectSubmissionContext.Empty));
    }

    private static void ProjectSubmissionRejectsCancelAndWorkflowSkips()
    {
        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Accepting, ProjectSubmissionEvent.Cancel, ProjectSubmissionContext.Empty), AuthorityRejectionCode.CancelNotAllowed);
        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Committing, ProjectSubmissionEvent.Cancel, ProjectSubmissionContext.Empty), AuthorityRejectionCode.CancelNotAllowed);
        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Revalidating, ProjectSubmissionEvent.Cancel, ProjectSubmissionContext.Empty), AuthorityRejectionCode.CancelNotAllowed);

        foreach (var state in Enum.GetValues<ProjectSubmissionState>().Where(value => value != ProjectSubmissionState.Idle))
        {
            AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
                state, ProjectSubmissionEvent.Submit, ProjectContext(ProjectSubmissionEvent.Submit)), AuthorityRejectionCode.IllegalTransition);
        }

        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Reviewing, ProjectSubmissionEvent.BeginCommit, ProjectSubmissionContext.Empty), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Idle, ProjectSubmissionEvent.BeginAcceptance, ProjectContext(ProjectSubmissionEvent.BeginAcceptance)), AuthorityRejectionCode.IllegalTransition);

        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Idle,
            ProjectSubmissionEvent.Submit,
            new ProjectSubmissionContext(SubmissionEligibility.None, false, AcceptanceDecisionContext.Unauthorized)),
            AuthorityRejectionCode.EligibilityDenied);
        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Idle,
            ProjectSubmissionEvent.Submit,
            new ProjectSubmissionContext(SubmissionEligibility.Normal, true, AcceptanceDecisionContext.Unauthorized)),
            AuthorityRejectionCode.ActiveSubmissionExists);
    }

    private static void CandidateRetryRequiresNewIdentityAndSupersedeRequiresLineage()
    {
        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.Failed, CandidateEvent.Accept, CandidateContext.Empty), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.Cancelled, CandidateEvent.Accept, CandidateContext.Empty), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.Failed, CandidateEvent.BeginReview, CandidateContext.Empty), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.Cancelled, CandidateEvent.BeginReview, CandidateContext.Empty), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.Accepted, CandidateEvent.Supersede, CandidateContext.Empty), AuthorityRejectionCode.LineageIneligible);
        AssertNext(CandidateState.Superseded, CandidateStateMachine.Instance.Transition(
            CandidateState.Accepted, CandidateEvent.Supersede, new CandidateContext(AcceptanceDecisionContext.Unauthorized, true)));
    }

    private static void ChapterFailureCannotMaterialize()
    {
        AssertRejected(ChapterStateMachine.Instance.Transition(
            ChapterState.Failed, ChapterEvent.Materialize, ChapterContext.Empty), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(ChapterStateMachine.Instance.Transition(
            ChapterState.Draft, ChapterEvent.Materialize, ChapterContext.Empty), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(ChapterStateMachine.Instance.Transition(
            ChapterState.Ready, ChapterEvent.Accept, new ChapterContext(AuthorDecision)), AuthorityRejectionCode.IllegalTransition);
        AssertRejected(ChapterStateMachine.Instance.Transition(
            ChapterState.Submitted, ChapterEvent.Accept, new ChapterContext(AuthorDecision)), AuthorityRejectionCode.IllegalTransition);
    }

    private static void RevisionBarrierReleaseAndIdentityGuards()
    {
        AssertNext(RevisionBarrierState.Inactive, RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.ActiveInitial, RevisionBarrierEvent.ReviewFailed, BarrierContext(RevisionBarrierEvent.ReviewFailed)));
        AssertNext(RevisionBarrierState.Inactive, RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.ActiveInitial, RevisionBarrierEvent.Cancel, BarrierContext(RevisionBarrierEvent.Cancel)));
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.ActiveInitial, RevisionBarrierEvent.Cancel,
            BarrierContext(RevisionBarrierEvent.Cancel) with { AuthorityCommitted = true }), AuthorityRejectionCode.BarrierNotResolved);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving, RevisionBarrierEvent.OrdinaryFailure, BarrierContext(RevisionBarrierEvent.OrdinaryFailure)),
            AuthorityRejectionCode.IllegalTransition);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving, RevisionBarrierEvent.CompleteResolution,
            BarrierContext(RevisionBarrierEvent.CompleteResolution) with { AffectedSetClean = false }), AuthorityRejectionCode.BarrierNotResolved);

        var valid = BarrierContext(RevisionBarrierEvent.SubmitRemediation);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving, RevisionBarrierEvent.SubmitRemediation,
            valid with { RemediationBarrierId = "wrong" }), AuthorityRejectionCode.BarrierIdentityMismatch);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving, RevisionBarrierEvent.SubmitRemediation,
            valid with { RemediationOriginatingTransactionId = "wrong" }), AuthorityRejectionCode.OriginatingTransactionMismatch);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving, RevisionBarrierEvent.SubmitRemediation,
            valid with { RemediationBarrierId = null }), AuthorityRejectionCode.BarrierIdentityMismatch);
        AssertNext(RevisionBarrierState.Resolving, RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving, RevisionBarrierEvent.SubmitRemediation, valid));
    }

    private static void HigherScopeAuthorityRequiresSnapshotAndPreservesHistory()
    {
        var final = StorylineStateMachine.Instance.Transition(
            StorylineState.UnderFinalReview, StorylineEvent.AcceptFinal, new StorylineContext(AuthorDecision, null));
        AssertNext(StorylineState.FinalAccepted, final);
        AssertEqual(AuthorityEffectRequirement.AcceptedSnapshotRequired, final.Metadata.EffectRequirement,
            "Final Acceptance did not require an Accepted Snapshot.");

        AssertRejected(StorylineStateMachine.Instance.Transition(
            StorylineState.FinalAccepted, StorylineEvent.BeginPostAcceptanceRevision, StorylineContext.Empty),
            AuthorityRejectionCode.AcceptedSnapshotIdentityMissing);
        var revision = StorylineStateMachine.Instance.Transition(
            StorylineState.FinalAccepted,
            StorylineEvent.BeginPostAcceptanceRevision,
            new StorylineContext(AcceptanceDecisionContext.Unauthorized, "snapshot-v1"));
        AssertNext(StorylineState.PostAcceptanceRevision, revision);
        AssertEqual(AuthorityEffectRequirement.PreserveHistoricalSnapshotIdentity, revision.Metadata.EffectRequirement,
            "Post-acceptance revision did not preserve the historical snapshot identity.");
    }

    private static void AllConditionalGuardsRejectInvalidContext()
    {
        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Idle, ProjectSubmissionEvent.Submit, ProjectSubmissionContext.Empty),
            AuthorityRejectionCode.EligibilityDenied);
        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Resolving, ProjectSubmissionEvent.BeginAcceptance, ProjectSubmissionContext.Empty),
            AuthorityRejectionCode.AcceptanceNotAuthorized);

        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.UnderReview, CandidateEvent.Accept, CandidateContext.Empty),
            AuthorityRejectionCode.AcceptanceNotAuthorized);
        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.Accepted, CandidateEvent.Supersede, CandidateContext.Empty),
            AuthorityRejectionCode.LineageIneligible);

        AssertRejected(ChapterStateMachine.Instance.Transition(
            ChapterState.UnderReview, ChapterEvent.Accept, ChapterContext.Empty),
            AuthorityRejectionCode.AcceptanceNotAuthorized);

        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.ActiveInitial, RevisionBarrierEvent.AuthorityCommitted, RevisionBarrierContext.Empty),
            AuthorityRejectionCode.GuardFailed);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.ActiveInitial,
            RevisionBarrierEvent.ReviewFailed,
            RevisionBarrierContext.Empty with { AuthorityCommitted = true }),
            AuthorityRejectionCode.BarrierNotResolved);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.ActiveInitial,
            RevisionBarrierEvent.Cancel,
            RevisionBarrierContext.Empty with { AuthorityCommitted = true }),
            AuthorityRejectionCode.BarrierNotResolved);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving,
            RevisionBarrierEvent.CompleteResolution,
            RevisionBarrierContext.Empty),
            AuthorityRejectionCode.BarrierNotResolved);
        AssertRejected(RevisionBarrierStateMachine.Instance.Transition(
            RevisionBarrierState.Resolving,
            RevisionBarrierEvent.SubmitRemediation,
            RevisionBarrierContext.Empty),
            AuthorityRejectionCode.BarrierIdentityMismatch);

        AssertRejected(ArcStateMachine.Instance.Transition(
            ArcState.UnderClosureReview, ArcEvent.Accept, ArcContext.Empty),
            AuthorityRejectionCode.AcceptanceNotAuthorized);

        AssertRejected(StorylineStateMachine.Instance.Transition(
            StorylineState.UnderFinalReview, StorylineEvent.AcceptFinal, StorylineContext.Empty),
            AuthorityRejectionCode.AcceptanceNotAuthorized);
        AssertRejected(StorylineStateMachine.Instance.Transition(
            StorylineState.FinalAccepted, StorylineEvent.BeginPostAcceptanceRevision, StorylineContext.Empty),
            AuthorityRejectionCode.AcceptedSnapshotIdentityMissing);
    }

    private static void DelegatedAuthoritySharesStateButPreservesProvenance()
    {
        var author = ChapterStateMachine.Instance.Transition(
            ChapterState.UnderReview, ChapterEvent.Accept, new ChapterContext(AuthorDecision));
        var delegated = ChapterStateMachine.Instance.Transition(
            ChapterState.UnderReview, ChapterEvent.Accept, new ChapterContext(AgentDecision));
        AssertEqual(author.NextState, delegated.NextState, "Author and delegated acceptance reached different states.");
        AssertEqual(DecisionAuthorityKind.AuthorConfirmed, author.Metadata.DecisionProvenance!.AuthorityKind,
            "Author provenance was not retained.");
        AssertEqual(DecisionAuthorityKind.AgentDelegated, delegated.Metadata.DecisionProvenance!.AuthorityKind,
            "Delegated provenance was not retained.");
    }

    private static void BypassAndAutoCannotRewriteAuthorityRules()
    {
        var bypassOnly = new AcceptanceDecisionContext(
            false, null, NarrativeOversightMode.BypassPermissions, true);
        AssertRejected(CandidateStateMachine.Instance.Transition(
            CandidateState.UnderReview, CandidateEvent.Accept, new CandidateContext(bypassOnly, false)),
            AuthorityRejectionCode.AcceptanceNotAuthorized);

        AssertRejected(ProjectSubmissionStateMachine.Instance.Transition(
            ProjectSubmissionState.Idle,
            ProjectSubmissionEvent.BeginAcceptance,
            new ProjectSubmissionContext(SubmissionEligibility.Normal, false, AgentDecision)),
            AuthorityRejectionCode.IllegalTransition);
    }

    private static void AuthorityScopesUseIndependentTypes()
    {
        AssertTrue(typeof(ChapterState) != typeof(ArcState), "Chapter and Arc states share a type.");
        AssertTrue(typeof(ArcState) != typeof(StorylineState), "Arc and Storyline states share a type.");
        AssertTrue(typeof(ChapterState) != typeof(CandidateState), "Chapter and Candidate states share a type.");
    }

    private static void VerifyMatrix<TState, TEvent>(
        string name,
        IReadOnlyList<string> matrix,
        IReadOnlyDictionary<(TState, TEvent), TState> nextStates,
        Func<TState, TEvent, TransitionResult<TState>> transition)
        where TState : struct, Enum
        where TEvent : struct, Enum
    {
        var states = Enum.GetValues<TState>();
        var events = Enum.GetValues<TEvent>();
        AssertEqual(states.Length, matrix.Count, $"{name} golden matrix state dimension is stale.");

        var legal = 0;
        var illegal = 0;
        var conditional = 0;
        for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            AssertEqual(events.Length, matrix[stateIndex].Length, $"{name} golden matrix event dimension is stale.");
            for (var eventIndex = 0; eventIndex < events.Length; eventIndex++)
            {
                var state = states[stateIndex];
                var @event = events[eventIndex];
                var expected = matrix[stateIndex][eventIndex];
                var result = transition(state, @event);
                var expectedClassification = expected switch
                {
                    'L' => TransitionClassification.Legal,
                    'I' => TransitionClassification.Illegal,
                    'C' => TransitionClassification.GuardConditional,
                    _ => throw new InvalidOperationException($"{name} golden matrix contains unknown classification '{expected}'.")
                };

                AssertEqual(expectedClassification, result.Classification,
                    $"{name} classified ({state}, {@event}) incorrectly.");
                AssertEqual(expected != 'I', result.Allowed, $"{name} allowed flag is wrong for ({state}, {@event}).");
                AssertEqual(state, result.CurrentState, $"{name} changed CurrentState for ({state}, {@event}).");

                if (expected == 'I')
                {
                    illegal++;
                    AssertTrue(result.NextState is null, $"{name} illegal case ({state}, {@event}) returned NextState.");
                    AssertTrue(result.Rejection is not null, $"{name} illegal case ({state}, {@event}) lacks rejection.");
                    continue;
                }

                if (expected == 'L') legal++; else conditional++;
                AssertTrue(nextStates.TryGetValue((state, @event), out var expectedNext),
                    $"{name} golden NextState is missing for ({state}, {@event}).");
                AssertEqual(expectedNext, result.NextState!.Value,
                    $"{name} NextState is wrong for ({state}, {@event}).");
            }
        }

        AssertEqual(legal + illegal + conditional, states.Length * events.Length,
            $"{name} did not classify its Cartesian product.");
        AssertEqual(nextStates.Count, legal + conditional, $"{name} golden NextState map has extra or missing cases.");
        Metrics.Add(new MatrixMetric(name, states.Length, events.Length, states.Length * events.Length, legal, illegal, conditional));
    }

    private static ProjectSubmissionContext ProjectContext(ProjectSubmissionEvent @event)
        => @event switch
        {
            ProjectSubmissionEvent.Submit => new(SubmissionEligibility.Normal, false, AcceptanceDecisionContext.Unauthorized),
            ProjectSubmissionEvent.BeginAcceptance => new(SubmissionEligibility.None, false, AuthorDecision),
            _ => ProjectSubmissionContext.Empty
        };

    private static CandidateContext CandidateContextFor(CandidateEvent @event)
        => @event switch
        {
            CandidateEvent.Accept => new(AuthorDecision, false),
            CandidateEvent.Supersede => new(AcceptanceDecisionContext.Unauthorized, true),
            _ => CandidateContext.Empty
        };

    private static RevisionBarrierContext BarrierContext(RevisionBarrierEvent @event)
        => @event switch
        {
            RevisionBarrierEvent.AuthorityCommitted => new(true, false, "barrier", "transaction", null, null),
            RevisionBarrierEvent.ReviewFailed or RevisionBarrierEvent.Cancel => RevisionBarrierContext.Empty,
            RevisionBarrierEvent.CompleteResolution => new(true, true, "barrier", "transaction", null, null),
            RevisionBarrierEvent.SubmitRemediation => new(true, false, "barrier", "transaction", "barrier", "transaction"),
            _ => RevisionBarrierContext.Empty
        };

    private static StorylineContext StorylineContextFor(StorylineEvent @event)
        => @event switch
        {
            StorylineEvent.AcceptFinal => new(AuthorDecision, null),
            StorylineEvent.BeginPostAcceptanceRevision => new(AcceptanceDecisionContext.Unauthorized, "snapshot-v1"),
            _ => StorylineContext.Empty
        };

    private static TState Next<TState>(TransitionResult<TState> result) where TState : struct, Enum
    {
        if (!result.Allowed || result.NextState is not TState nextState)
        {
            throw new InvalidOperationException("Expected an allowed transition.");
        }

        return nextState;
    }

    private static void AssertNext<TState>(TState expected, TransitionResult<TState> result) where TState : struct, Enum
        => AssertEqual(expected, Next(result), "Transition reached the wrong state.");

    private static void AssertRejected<TState>(TransitionResult<TState> result, AuthorityRejectionCode code)
        where TState : struct, Enum
    {
        AssertTrue(!result.Allowed, "Expected transition rejection.");
        AssertEqual(code, result.Rejection!.Code, "Transition returned the wrong typed rejection.");
    }

    private static void Run(string name, Action test)
    {
        test();
        PassedTests.Add(name);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed record MatrixMetric(
        string Name,
        int States,
        int Events,
        int Total,
        int Legal,
        int Illegal,
        int Conditional);
}
