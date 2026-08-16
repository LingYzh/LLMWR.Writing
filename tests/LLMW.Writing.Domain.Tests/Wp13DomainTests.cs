using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private static void RunWp13DomainTests()
    {
        Run(nameof(CompletionContractDeterministicPassAndMissingOutput), CompletionContractDeterministicPassAndMissingOutput);
        Run(nameof(CompletionContractBlockingDiagnosticAndSchemaMismatch), CompletionContractBlockingDiagnosticAndSchemaMismatch);
        Run(nameof(CompletionContractSemanticReviewPendingWithoutEvaluator), CompletionContractSemanticReviewPendingWithoutEvaluator);
        Run(nameof(CompletionContractSemanticPassWithInjectedOutcome), CompletionContractSemanticPassWithInjectedOutcome);
        Run(nameof(ResultArtifactCanonicalSerializationIsDeterministicAndSecretSafe), ResultArtifactCanonicalSerializationIsDeterministicAndSecretSafe);
        Run(nameof(ResultFreshnessStaleVsUnrelatedDraft), ResultFreshnessStaleVsUnrelatedDraft);
        Run(nameof(RequiredMissingAndStaleBlockAdvisoryAndOptionalDoNot), RequiredMissingAndStaleBlockAdvisoryAndOptionalDoNot);
        Run(nameof(OversightDefaultsAndTaskWinsPrecedence), OversightDefaultsAndTaskWinsPrecedence);
        Run(nameof(BypassPresetDoesNotDelegateNarrativeAuthority), BypassPresetDoesNotDelegateNarrativeAuthority);
        Run(nameof(ForwardOnlyCheckpointActivation), ForwardOnlyCheckpointActivation);
        Run(nameof(PendingApprovalsAreReevaluatedNotBlindlyApproved), PendingApprovalsAreReevaluatedNotBlindlyApproved);
        Run(nameof(AuthorConfirmedAndAgentDelegatedProvenanceDiffer), AuthorConfirmedAndAgentDelegatedProvenanceDiffer);
        Run(nameof(RuntimeGrillAuthorRequiredAndPlanBlocked), RuntimeGrillAuthorRequiredAndPlanBlocked);
        Run(nameof(RuntimeGrillDecisionIdentityIsStable), RuntimeGrillDecisionIdentityIsStable);
        Run(nameof(SpecialistBuiltInCannotBeTaskScopeAndDoesNotGrantCapability), SpecialistBuiltInCannotBeTaskScopeAndDoesNotGrantCapability);
        Run(nameof(SpecialistRoutingIsDeterministicOrOrchestratorLater), SpecialistRoutingIsDeterministicOrOrchestratorLater);
        Run(nameof(IsolatedTaskPacketOmitsProducerTranscript), IsolatedTaskPacketOmitsProducerTranscript);
        Run(nameof(BackgroundLifecycleForbidsCompletedToRunning), BackgroundLifecycleForbidsCompletedToRunning);
        Run(nameof(BackgroundExecutionIdentityRoundtripsThroughKindColumn), BackgroundExecutionIdentityRoundtripsThroughKindColumn);
        Run(nameof(EvidenceStaleWhenSourceDigestChanges), EvidenceStaleWhenSourceDigestChanges);
        Run(nameof(ResultFreshnessAuthorityIgnoresCallerCurrent), ResultFreshnessAuthorityIgnoresCallerCurrent);
        Run(nameof(ForwardOnlyActivationIsExecutionScoped), ForwardOnlyActivationIsExecutionScoped);
        Run(nameof(RequiredDependencyIgnoresProvisionalProducerResult), RequiredDependencyIgnoresProvisionalProducerResult);
        Run(nameof(ConsumerFreshnessRequiresFrozenUpstreamRefs), ConsumerFreshnessRequiresFrozenUpstreamRefs);
        Run(nameof(SameRunEvidenceIsNotImplicitlyOwned), SameRunEvidenceIsNotImplicitlyOwned);
        Run(nameof(NewExecutionAfterOverrideUsesNewPolicyImmediately), NewExecutionAfterOverrideUsesNewPolicyImmediately);
        Run(nameof(RuntimeLogicalTimestampIsStrictlyMonotonic), RuntimeLogicalTimestampIsStrictlyMonotonic);
        Run(nameof(FormalAuthorizationSnapshotRoundtripsWinningScope), FormalAuthorizationSnapshotRoundtripsWinningScope);
    }

    private static void CompletionContractDeterministicPassAndMissingOutput()
    {
        var contract = new TaskCompletionContractV1(
            1, ["conclusion"], [], [], ["conclusion"], true, []);
        var pass = TaskCompletionContractChecker.Check(contract, Inputs(outputs: ["conclusion"], shape: ["conclusion"]));
        AssertEqual(CompletionCheckOutcome.Pass, pass.Outcome, "Deterministic completion should pass.");
        var missing = TaskCompletionContractChecker.Check(contract, Inputs(outputs: [], shape: ["conclusion"]));
        AssertEqual(CompletionCheckOutcome.DeterministicFail, missing.Outcome, "Missing required output must fail.");
        AssertTrue(missing.Failures.Any(item => item.Contains("missing-required-output", StringComparison.Ordinal)),
            "Missing output failure code drifted.");
        var noArtifact = TaskCompletionContractChecker.Check(contract, Inputs(outputs: ["conclusion"], shape: ["conclusion"], artifact: false));
        AssertTrue(noArtifact.Failures.Contains("required-result-artifact-missing"), "Missing Result Artifact must block complete.");
    }

    private static void CompletionContractBlockingDiagnosticAndSchemaMismatch()
    {
        var contract = new TaskCompletionContractV1(1, [], [], [], ["findings"], true, []);
        var blocking = TaskCompletionContractChecker.Check(contract, Inputs(shape: ["findings"], blocking: 1));
        AssertEqual(CompletionCheckOutcome.DeterministicFail, blocking.Outcome, "Blocking diagnostics must fail.");
        var shape = TaskCompletionContractChecker.Check(contract, Inputs(shape: ["conclusion"]));
        AssertTrue(shape.Failures.Any(item => item.StartsWith("result-shape-mismatch", StringComparison.Ordinal)),
            "Schema mismatch was not reported.");
    }

    private static void CompletionContractSemanticReviewPendingWithoutEvaluator()
    {
        var contract = new TaskCompletionContractV1(
            1, [], [], [], [], true, [new SemanticCompletionCriterion("coverage", "coverage sufficient")]);
        var pending = TaskCompletionContractChecker.Check(contract, Inputs());
        AssertEqual(CompletionCheckOutcome.SemanticReviewRequired, pending.Outcome,
            "Production without a semantic evaluator must not silently PASS.");
    }

    private static void CompletionContractSemanticPassWithInjectedOutcome()
    {
        var contract = new TaskCompletionContractV1(
            1, [], [], [], [], true, [new SemanticCompletionCriterion("coverage", "coverage sufficient")]);
        var pass = TaskCompletionContractChecker.Check(contract, Inputs(semantic: SemanticCompletionOutcome.Pass));
        AssertEqual(CompletionCheckOutcome.Pass, pass.Outcome, "Injected semantic pass must complete.");
    }

    private static void ResultArtifactCanonicalSerializationIsDeterministicAndSecretSafe()
    {
        var artifact = SampleArtifact("apiKey=secret-value");
        var first = ResultArtifactCanonicalJson.Write(artifact);
        var second = ResultArtifactCanonicalJson.Write(artifact);
        AssertEqual(first, second, "Result Artifact JSON must be deterministic.");
        AssertTrue(!first.Contains("secret-value", StringComparison.Ordinal), "Secrets must be redacted.");
        AssertTrue(!first.Contains("transcript", StringComparison.OrdinalIgnoreCase), "Transcript must not be embedded.");
        var digest = ResultArtifactCanonicalJson.Digest(artifact);
        AssertEqual(64, digest.Length, "SHA-256 hex digest length drifted.");
        var columns = ResultArtifactCanonicalJson.ParseColumns(
            artifact.ResultArtifactId,
            artifact.TaskId,
            "complete",
            ResultArtifactCanonicalJson.WriteColumn("conclusion", artifact with { Conclusion = "ok" }),
            ResultArtifactCanonicalJson.WriteColumn("findings", artifact with { Conclusion = "ok" }),
            ResultArtifactCanonicalJson.WriteColumn("evidence", artifact with { Conclusion = "ok" }),
            ResultArtifactCanonicalJson.WriteColumn("uncertainty", artifact with { Conclusion = "ok" }),
            ResultArtifactCanonicalJson.WriteColumn("diagnostics", artifact with { Conclusion = "ok" }),
            ResultArtifactCanonicalJson.WriteColumn("freshness", artifact with { Conclusion = "ok" }),
            artifact.ProducedAtMs);
        AssertEqual("ok", columns.Conclusion, "Conclusion column mapping drifted.");
        AssertEqual("ev-1", columns.EvidenceIds.Single(), "Evidence refs must persist by identity.");
        AssertEqual("cs-1", columns.ProposedChangeSetRef, "Proposed Change Set must be referenced, not applied.");
    }

    private static void ResultFreshnessStaleVsUnrelatedDraft()
    {
        var produced = new ResultProducedAgainstV1("rev-1", ["obj:a"], "ev-1", null, null, null, [], null, null, ["ra-up"]);
        var same = ResultFreshnessPolicy.Classify(produced, produced, unrelatedDraftOnly: false);
        AssertEqual(ResultFreshnessState.Current, same, "Unchanged inputs must stay current.");
        var stale = ResultFreshnessPolicy.Classify(
            produced,
            produced with { AuthorityRevision = "rev-2" },
            unrelatedDraftOnly: false);
        AssertEqual(ResultFreshnessState.Stale, stale, "Authority revision change must stale.");
        var draft = ResultFreshnessPolicy.Classify(
            produced,
            produced with { AuthorityRevision = "rev-2" },
            unrelatedDraftOnly: true);
        AssertEqual(ResultFreshnessState.Current, draft, "Unrelated Draft must not stale a Result.");
    }

    private static void RequiredMissingAndStaleBlockAdvisoryAndOptionalDoNot()
    {
        var requiredMissing = new DurableDependencyRecord("d1", "c", "p", "required", "missing");
        var requiredStale = new DurableDependencyRecord("d2", "c", "p2", "required", "stale");
        var advisory = new DurableDependencyRecord("d3", "c", "p3", "advisory", "stale");
        var optional = new DurableDependencyRecord("d4", "c", "p4", "optional", "stale");
        var requiredCurrent = new DurableDependencyRecord("d5", "c", "p5", "required", "satisfied");
        AssertTrue(ResultDependencyPolicy.HardBlocks(requiredMissing), "REQUIRED missing must block.");
        AssertTrue(ResultDependencyPolicy.HardBlocks(requiredStale), "REQUIRED stale must block.");
        AssertTrue(!ResultDependencyPolicy.HardBlocks(advisory), "ADVISORY stale must not hard-block.");
        AssertTrue(ResultDependencyPolicy.Evaluate(advisory).HasWarning, "ADVISORY stale must warn.");
        var optionalRecompute = ResultDependencyPolicy.Recompute(
            ResultDependencyKind.Optional, "ra-1", ResultFreshnessState.Stale, true);
        AssertEqual(ResultDependencyStatus.Stale, optionalRecompute, "OPTIONAL stale must remain stale.");
        AssertTrue(!ResultDependencyPolicy.Evaluate(optional).BlocksDispatch,
            "OPTIONAL stale must not hard-block.");
        AssertTrue(!ResultDependencyPolicy.HardBlocks(requiredCurrent), "REQUIRED current/satisfied must be ready.");
        AssertTrue(!ResultDependencyPolicy.ProposalMutatesEffectiveEdge, "A proposal must not mutate the effective edge.");

        var snapshot = new SchedulerSnapshot(
            [],
            [new DurableRunRecord("root", "wf", null, "writer", "running", 0, 1, 1)],
            [
                new DurableTaskRecord("c", "root", null, "write", "pending", 1, 1, 1),
                new DurableTaskRecord("p", "root", null, "write", "completed", 1, 1, 1)
            ],
            [],
            [requiredStale],
            [],
            []);
        var view = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        AssertTrue(view.BlockedTaskIds.Contains("c"), "Scheduler READY must not contradict required stale.");
        var advisorySnapshot = snapshot with { Dependencies = [advisory] };
        var advisoryView = SchedulerProjection.Rebuild(advisorySnapshot, ConcurrencyBudget.Default);
        AssertTrue(advisoryView.ReadyTaskIds.Contains("c"), "Advisory stale must remain runnable.");
    }

    private static void OversightDefaultsAndTaskWinsPrecedence()
    {
        AssertEqual(NarrativeDecisionAuthority.AuthorConfirmedRequired, EffectiveOversightPolicy.ApplicationDefault.NarrativeAuthority,
            "Default narrative authority must be author-confirmed.");
        AssertEqual(RuntimePermissionMode.Ask, EffectiveOversightPolicy.ApplicationDefault.RuntimePermission,
            "Default permission must be ASK.");
        var overrides = new[]
        {
            new OversightOverrideRecord("o-p", OversightScopeKind.Project, "proj", NarrativeDecisionAuthority.AgentDelegated,
                RuntimePermissionMode.AutoApproveScoped, null, "user", 1),
            new OversightOverrideRecord("o-s", OversightScopeKind.Storyline, "story", NarrativeDecisionAuthority.AgentDelegated,
                RuntimePermissionMode.Ask, null, "user", 2),
            new OversightOverrideRecord("o-t", OversightScopeKind.Task, "task-1", NarrativeDecisionAuthority.AuthorConfirmedRequired,
                RuntimePermissionMode.Ask, null, "user", 3)
        };
        var resolved = OversightResolver.Resolve(
            EffectiveOversightPolicy.ApplicationDefault,
            overrides,
            new HashSet<string>(StringComparer.Ordinal),
            "proj",
            "story",
            "task-1");
        AssertEqual(OversightScopeKind.Task, resolved.WinningScope, "Task override must win.");
        AssertEqual(NarrativeDecisionAuthority.AuthorConfirmedRequired, resolved.NarrativeAuthority, "Task axis drifted.");
    }

    private static void BypassPresetDoesNotDelegateNarrativeAuthority()
    {
        var bypass = OversightPresetMap.FromPreset(OversightProductPreset.BypassPermissions);
        AssertEqual(NarrativeDecisionAuthority.AuthorConfirmedRequired, bypass.NarrativeAuthority,
            "BYPASS must not create AGENT_DELEGATED.");
        AssertEqual(RuntimePermissionMode.BypassPermissions, bypass.RuntimePermission, "BYPASS permission axis drifted.");
        var auto = OversightPresetMap.FromPreset(OversightProductPreset.Auto);
        AssertEqual(NarrativeDecisionAuthority.AgentDelegated, auto.NarrativeAuthority, "AUTO must delegate narrative.");
    }

    private static void ForwardOnlyCheckpointActivation()
    {
        var pending = new OversightOverrideRecord(
            "o-1",
            OversightScopeKind.Project,
            "proj",
            NarrativeDecisionAuthority.AgentDelegated,
            RuntimePermissionMode.AutoApproveScoped,
            "cp-next",
            "user",
            1);
        AssertTrue(!OversightActivation.IsActive(pending, new HashSet<string>(StringComparer.Ordinal)),
            "Override must wait for the safe checkpoint.");
        AssertTrue(OversightActivation.IsActive(pending, new HashSet<string>(StringComparer.Ordinal) { "cp-next" }),
            "Override must activate at the recorded checkpoint.");
        var immediate = pending with { EffectiveAfterCheckpointId = null };
        AssertTrue(OversightActivation.IsActive(immediate, new HashSet<string>(StringComparer.Ordinal)),
            "Idle-scope override with null checkpoint is the current safe boundary.");
    }

    private static void PendingApprovalsAreReevaluatedNotBlindlyApproved()
    {
        var delegated = EffectiveOversightPolicy.ApplicationDefault with
        {
            NarrativeAuthority = NarrativeDecisionAuthority.AgentDelegated,
            RuntimePermission = RuntimePermissionMode.AutoApproveScoped
        };
        var approved = PendingApprovalReevaluator.Reevaluate(new PendingApprovalSnapshot(
            "a1", ApprovalKindCodec.RuntimeGrill, true, true, true, true, true, true, false, delegated));
        AssertEqual(PendingApprovalReevaluation.ApprovedDelegated, approved, "Valid delegated item may resolve.");
        var denied = PendingApprovalReevaluator.Reevaluate(new PendingApprovalSnapshot(
            "a2", ApprovalKindCodec.RuntimeGrill, true, true, true, true, true, false, false, delegated));
        AssertEqual(PendingApprovalReevaluation.Denied, denied, "Missing Project Trust must survive AUTO.");
        var hard = PendingApprovalReevaluator.Reevaluate(new PendingApprovalSnapshot(
            "a3", ApprovalKindCodec.ToolApproval, false, true, true, true, true, true, true, delegated));
        AssertEqual(PendingApprovalReevaluation.Denied, hard, "HardDeny must survive AutoApproveScoped.");
        var author = PendingApprovalReevaluator.Reevaluate(new PendingApprovalSnapshot(
            "a4", ApprovalKindCodec.RuntimeGrill, true, true, true, true, true, true, false,
            EffectiveOversightPolicy.ApplicationDefault));
        AssertEqual(PendingApprovalReevaluation.StillPending, author, "Author-required items stay pending under MANUAL.");
    }

    private static void AuthorConfirmedAndAgentDelegatedProvenanceDiffer()
    {
        var policy = EffectiveOversightPolicy.ApplicationDefault;
        var author = NarrativeDecisionProvenance.AuthorConfirmed(
            "d1", "tx-1", OversightScopeKind.Task, "task-1", "agent-1", "user-1", policy, "digest", 1);
        var delegated = NarrativeDecisionProvenance.AgentDelegated(
            "d2", "tx-2", OversightScopeKind.Task, "task-1", "agent-1",
            policy with { NarrativeAuthority = NarrativeDecisionAuthority.AgentDelegated }, "digest", 1);
        AssertEqual(DecisionAuthorityKind.AuthorConfirmed, author.AuthorityKind, "Author confirmation was mislabeled.");
        AssertEqual("user-1", author.ConfirmedBy, "ConfirmedBy must be the user.");
        AssertEqual(DecisionAuthorityKind.AgentDelegated, delegated.AuthorityKind, "Delegated decision was mislabeled.");
        AssertTrue(delegated.ConfirmedBy is null, "Agent-delegated must not pretend to be user-confirmed.");
        AssertEqual("AUTHOR_CONFIRMED", NarrativeDecisionProvenance.AuthorityKindDurable(author.AuthorityKind),
            "Durable author kind drifted.");
        AssertEqual("AGENT_DELEGATED", NarrativeDecisionProvenance.AuthorityKindDurable(delegated.AuthorityKind),
            "Durable delegated kind drifted.");
    }

    private static void RuntimeGrillAuthorRequiredAndPlanBlocked()
    {
        AssertTrue(RuntimeGrillPolicy.RequiresUserDecision(EffectiveOversightPolicy.ApplicationDefault),
            "Author-required grill cannot be Agent-resolved.");
        var delegated = EffectiveOversightPolicy.ApplicationDefault with
        {
            NarrativeAuthority = NarrativeDecisionAuthority.AgentDelegated
        };
        AssertTrue(RuntimeGrillPolicy.AgentMayResolve(delegated, RuntimeGrillPauseReason.PlanAuthorityAmbiguous, true, true, true, true),
            "Delegated grill inside plan should be allowed.");
        AssertTrue(!RuntimeGrillPolicy.AgentMayResolve(delegated, RuntimeGrillPauseReason.TaskScopeExpansion, true, false, true, true),
            "Task-scope expansion must not be Agent self-approved.");
        AssertEqual(
            RuntimeGrillResolutionKind.PlanBlocked,
            RuntimeGrillPolicy.MapResume(RuntimeGrillPauseReason.PlanAssumptionsInvalid, ResumeDecisionKind.Continue),
            "Invalidated Plan must return to Planning, not Grill-auto-continue.");
        AssertEqual(
            RuntimeGrillResolutionKind.BlockUnknown,
            RuntimeGrillPolicy.MapResume(RuntimeGrillPauseReason.PlanAuthorityAmbiguous, ResumeDecisionKind.BlockUnknown),
            "UNKNOWN remains blocked after a Grill answer.");
        AssertEqual(
            ApprovalStatus.StaleAlreadyResolved,
            RuntimeGrillPolicy.Compete(ApprovalStatus.Resolved, ApprovalStatus.Resolved),
            "Competing resolution must yield one winner.");
    }

    private static void RuntimeGrillDecisionIdentityIsStable()
    {
        var request = new RuntimeGrillDecisionRequestV1(
            1,
            "appr-1",
            "run-1",
            "task-1",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("choose_next_action", ["continue", "replan"], "What next?"),
            NarrativeDecisionAuthority.AuthorConfirmedRequired,
            "baseline",
            "cp-1");
        AssertEqual(RuntimeGrillPolicy.WriteCanonical(request), RuntimeGrillPolicy.WriteCanonical(request),
            "Grill payload must be deterministic.");
        AssertEqual(
            RuntimeGrillPolicy.StableApprovalId("run-1", "task-1", "baseline", RuntimeGrillPauseReason.NewCreativeDecisionRequired, request.Question),
            RuntimeGrillPolicy.StableApprovalId("run-1", "task-1", "baseline", RuntimeGrillPauseReason.NewCreativeDecisionRequired, request.Question),
            "Grill approval identity must be stable.");
        AssertTrue(
            !StringComparer.Ordinal.Equals(
                RuntimeGrillPolicy.StableApprovalId("run-1", "task-1", "baseline", RuntimeGrillPauseReason.NewCreativeDecisionRequired, request.Question),
                RuntimeGrillPolicy.StableApprovalId(
                    "run-1",
                    "task-1",
                    "baseline",
                    RuntimeGrillPauseReason.PlanAssumptionsInvalid,
                    request.Question)),
            "Different grill decisions under the same baseline must not collide.");
        AssertEqual(ApprovalKindCodec.RuntimeGrill, ApprovalKindCodec.ToDurableValue(ApprovalKind.RuntimeGrill),
            "Grill kind must stay distinct from tool approval.");
        AssertTrue(!StringComparer.Ordinal.Equals(ApprovalKindCodec.RuntimeGrill, ApprovalKindCodec.ToolApproval),
            "Runtime Grill must not collapse into tool approval.");
    }

    private static void SpecialistBuiltInCannotBeTaskScopeAndDoesNotGrantCapability()
    {
        AssertTrue(SpecialistScopeKindCodec.IsPersistentForbidden("task"), "Task must not be a persistent Specialist scope.");
        AssertTrue(SpecialistScopeKindCodec.IsPersistentForbidden("session"), "Session must not be a persistent Specialist scope.");
        var profile = SampleProfile(SpecialistScopeKind.BuiltIn, "review", true, ["Authority.Accept", "Shell.Execute"]);
        var validation = SpecialistProfileValidator.Validate(profile);
        AssertTrue(validation.IsValid, "Valid built-in profile was rejected: " + string.Join(',', validation.Errors.Select(item => item.Code)));
        AssertTrue(!SpecialistProfileValidator.GrantsCapability(profile, Capability.AuthorityAccept),
            "Profile capability is a request, not a grant.");
        var invalid = SpecialistProfileValidator.Validate(profile with { RequestedCapabilities = ["Not.A.Capability"] });
        AssertTrue(!invalid.IsValid, "Invalid capability must fail structured validation.");
        var json = SpecialistProfileCanonicalJson.Write(profile);
        AssertEqual(json, SpecialistProfileCanonicalJson.Write(profile), "Profile serialization must be deterministic.");
        AssertEqual(64, SpecialistProfileCanonicalJson.Digest(profile).Length, "Profile digest length drifted.");
    }

    private static void SpecialistRoutingIsDeterministicOrOrchestratorLater()
    {
        var reviewer = SampleProfile(SpecialistScopeKind.BuiltIn, "review", true, []);
        var disabled = reviewer with { ProfileId = "disabled", Enabled = false };
        var excluded = reviewer with { ProfileId = "excluded", WhenNotToUse = ["draft"] };
        var one = SpecialistRouter.RouteDeterministic("review", [reviewer, disabled]);
        AssertEqual(SpecialistRouteOutcome.Deterministic, one.Outcome, "Known workflow stage must route deterministically.");
        AssertEqual(reviewer.ProfileId, one.ProfileId, "Deterministic route picked the wrong profile.");
        var many = SpecialistRouter.RouteDeterministic("review", [reviewer, reviewer with { ProfileId = "other" }]);
        AssertEqual(SpecialistRouteOutcome.AmbiguousOrchestratorLater, many.Outcome,
            "Ambiguous routing must not randomly select a Specialist.");
        var skip = SpecialistRouter.RouteDeterministic("draft", [excluded]);
        AssertEqual(SpecialistRouteOutcome.Excluded, skip.Outcome, "when-not-to-use must exclude.");
        var off = SpecialistRouter.RouteDeterministic("review", [disabled]);
        AssertEqual(SpecialistRouteOutcome.Disabled, off.Outcome, "Disabled profiles are not routable.");
    }

    private static void IsolatedTaskPacketOmitsProducerTranscript()
    {
        var packet = SpecialistTaskPacketV1.Isolated(
            "run-child",
            "task-child",
            "spec-1",
            "temporary instructions",
            "{\"goal\":\"review\"}",
            ["ra-1"],
            ["advisory-stale:ra-2"],
            null);
        AssertEqual(SpecialistContextMode.Isolated, packet.ContextMode, "Default context must be isolated.");
        AssertEqual("ra-1", packet.RequiredResultArtifactIds.Single(), "Required upstream Result must be included.");
        AssertTrue(packet.AdvisoryWarnings.Count == 1, "Advisory Result must be marked warning.");
        AssertTrue(!packet.TaskContractJson.Contains("transcript", StringComparison.OrdinalIgnoreCase),
            "Producer transcript must not be the default handoff.");
    }

    private static void BackgroundLifecycleForbidsCompletedToRunning()
    {
        AssertTrue(!BackgroundTaskLifecycle.IsLegal(BackgroundTaskStatus.Completed, BackgroundTaskStatus.Running),
            "completed → running is forbidden.");
        AssertTrue(!BackgroundTaskLifecycle.IsLegal(BackgroundTaskStatus.Cancelled, BackgroundTaskStatus.Running),
            "cancelled → running is forbidden.");
        AssertTrue(BackgroundTaskLifecycle.IsLegal(BackgroundTaskStatus.Running, BackgroundTaskStatus.Cancelled),
            "running → cancelled must be legal.");
        var record = new DurableBackgroundTaskRecord("bg-1", "run-1", "task-1", "{}", "running", "cp-1", 10, null);
        AssertEqual(10L, BackgroundTaskLifecycle.DurationMs(record with { CompletedAtMs = 20 }), "Duration drifted.");
        AssertEqual(
            BackgroundRecoveryClassification.UnknownSideEffect,
            BackgroundTaskLifecycle.ClassifyRestart(record, false, true, true, true),
            "UNKNOWN must not auto-retry.");
        AssertEqual(
            BackgroundRecoveryClassification.StillQueued,
            BackgroundTaskLifecycle.ClassifyRestart(record with { Status = "queued" }, false, false, false, false),
            "Queued background work must remain queued across restart.");
        AssertEqual(
            BackgroundRecoveryClassification.ResumableInterrupted,
            BackgroundTaskLifecycle.ClassifyRestart(record with { Status = "interrupted" }, false, true, false, false),
            "Interrupted background work must not be blindly failed.");
    }

    private static void BackgroundExecutionIdentityRoundtripsThroughKindColumn()
    {
        var execution = new BackgroundExecutionRef(BackgroundTaskKind.SubAgentRun, "run-child", null, "worker-1", "task-child");
        var json = BackgroundExecutionRefCodec.WriteKindColumn(execution);
        var round = BackgroundExecutionRefCodec.ParseKindColumn(json);
        AssertEqual(execution.Kind, round.Kind, "Background kind did not round-trip.");
        AssertEqual(execution.RunId, round.RunId, "Sub-agent run identity did not round-trip.");
        AssertEqual(execution.WorkerInstanceId, round.WorkerInstanceId, "Worker identity did not round-trip.");
        AssertTrue(json.Contains("schemaVersion", StringComparison.Ordinal), "Kind column must be versioned canonical JSON.");
    }

    private static void EvidenceStaleWhenSourceDigestChanges()
    {
        AssertTrue(EvidenceFreshness.IsStale("aaa", "bbb"), "Evidence must stale when the source digest changes.");
        AssertTrue(!EvidenceFreshness.IsStale("aaa", "aaa"), "Matching digest must remain current.");
    }

    private static void ResultFreshnessAuthorityIgnoresCallerCurrent()
    {
        var submitted = new ResultFreshnessV1(
            1,
            ResultFreshnessState.Current,
            new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, []),
            new ResultProvenanceV1("stolen-run", "stolen-task", "stolen-attempt", null, null, null, null, null));
        var evidence = new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal)
        {
            ["ev-1"] = new EvidenceRecord("ev-1", "run-1", "task-1", "narrative", "obj", "digest", "{}", true, 1)
        };
        var stamped = ResultFreshnessAuthority.Stamp(
            submitted,
            new ResultFreshnessAuthorityInputs(
                "run-1",
                "task-1",
                "att-1",
                new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal),
                evidence,
                null,
                new HashSet<string>(StringComparer.Ordinal)),
            ["ev-1"]);
        AssertEqual("run-1", stamped.Provenance.ProducedByRunId, "Core must stamp run identity.");
        AssertEqual(ResultFreshnessState.Stale, stamped.State, "Stale evidence must not remain CURRENT.");
        var unknown = ResultFreshnessAuthority.Stamp(
            submitted with
            {
                ProducedAgainst = submitted.ProducedAgainst with { PromptConfigId = "prompt-1" },
                Provenance = new ResultProvenanceV1("run-1", "task-1", "att-1", null, null, null, null, null)
            },
            new ResultFreshnessAuthorityInputs(
                "run-1",
                "task-1",
                "att-1",
                new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal),
                new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal),
                null,
                new HashSet<string>(StringComparer.Ordinal)),
            []);
        AssertEqual(ResultFreshnessState.NeedsRevalidation, unknown.State, "Unvalidatable WP14 claims must not silently be CURRENT.");
    }

    private static void ForwardOnlyActivationIsExecutionScoped()
    {
        var pendingB = new OversightOverrideRecord(
            "o-b",
            OversightScopeKind.Task,
            "task-b",
            NarrativeDecisionAuthority.AgentDelegated,
            RuntimePermissionMode.AutoApproveScoped,
            OversightActivation.PendingBindToken("o-b"),
            "user",
            10);
        var checkpointA = new DurableCheckpointRecord("cp-a", "run-1", "task-a", 1, "{}", "{}", 20);
        var contextB = new OversightActivationContext("proj", null, "task-b", "run-1", [checkpointA]);
        AssertTrue(!OversightActivation.IsActiveForExecution(pendingB, contextB),
            "Task A checkpoint must not activate a pending Task B override.");
        var checkpointB = new DurableCheckpointRecord("cp-b", "run-1", "task-b", 1, "{}", "{}", 30);
        AssertTrue(!OversightActivation.IsActiveForExecution(
                pendingB,
                contextB with { ExecutionCheckpoints = [checkpointA, checkpointB] }),
            "A still-pending Task override must not activate from another Task's or pre-bind checkpoints.");
        var boundB = pendingB with { EffectiveAfterCheckpointId = "cp-b" };
        AssertTrue(OversightActivation.IsActiveForExecution(
                boundB,
                contextB with { ExecutionCheckpoints = [checkpointA, checkpointB] }),
            "Binding the pending token to Task B's own checkpoint must activate it.");
    }

    private static void RequiredDependencyIgnoresProvisionalProducerResult()
    {
        var provisional = ResultDependencyPolicy.Recompute(
            ResultDependencyKind.Required,
            "r1",
            ResultFreshnessState.Current,
            true,
            producerFormallyCompleted: false);
        AssertEqual(ResultDependencyStatus.Missing, provisional,
            "REQUIRED must not become CURRENT on a provisional Running producer Result.");
        var frozen = ResultDependencyPolicy.Recompute(
            ResultDependencyKind.Required,
            "r2",
            ResultFreshnessState.Current,
            true,
            producerFormallyCompleted: true);
        AssertEqual(ResultDependencyStatus.Current, frozen,
            "REQUIRED becomes CURRENT only after formal producer completion.");
        var advisoryProvisional = ResultDependencyPolicy.Recompute(
            ResultDependencyKind.Advisory,
            "r1",
            ResultFreshnessState.Current,
            true,
            producerFormallyCompleted: false);
        AssertEqual(ResultDependencyStatus.Warning, advisoryProvisional,
            "ADVISORY provisional data must not masquerade as a completed CURRENT Result.");
    }

    private static void ConsumerFreshnessRequiresFrozenUpstreamRefs()
    {
        var submitted = new ResultFreshnessV1(
            1,
            ResultFreshnessState.Current,
            new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, ["r1"]),
            new ResultProvenanceV1("run-1", "consumer", "att-1", null, null, null, null, null));
        var omitted = ResultFreshnessAuthority.Stamp(
            submitted with { ProducedAgainst = submitted.ProducedAgainst with { UpstreamRequiredResultRefs = [] } },
            new ResultFreshnessAuthorityInputs(
                "run-1",
                "consumer",
                "att-1",
                new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal),
                new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal),
                null,
                new HashSet<string>(StringComparer.Ordinal),
                ["r2"],
                1),
            []);
        AssertEqual(ResultFreshnessState.Stale, omitted.State,
            "Omitting the current REQUIRED frozen Result must not remain CURRENT.");
        var oldR1 = ResultFreshnessAuthority.Stamp(
            submitted,
            new ResultFreshnessAuthorityInputs(
                "run-1",
                "consumer",
                "att-1",
                new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal),
                new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal),
                null,
                new HashSet<string>(StringComparer.Ordinal),
                ["r2"],
                1),
            []);
        AssertEqual(ResultFreshnessState.Stale, oldR1.State,
            "Produced-against R1 must stale when the frozen producer Result is R2.");
        var incompleteProducer = ResultFreshnessAuthority.Stamp(
            submitted,
            new ResultFreshnessAuthorityInputs(
                "run-1",
                "consumer",
                "att-1",
                new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal),
                new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal),
                null,
                new HashSet<string>(StringComparer.Ordinal),
                [],
                1),
            []);
        AssertEqual(ResultFreshnessState.NeedsRevalidation, incompleteProducer.State,
            "REQUIRED edges without a frozen producer Result must not silently be CURRENT.");
    }

    private static void SameRunEvidenceIsNotImplicitlyOwned()
    {
        var submitted = new ResultFreshnessV1(
            1,
            ResultFreshnessState.Current,
            new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, []),
            new ResultProvenanceV1("run-1", "task-a", "att-1", null, null, null, null, null));
        var foreign = new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal)
        {
            ["ev-b"] = new EvidenceRecord("ev-b", "run-1", "task-b", "narrative", "obj", "digest", "{}", false, 1)
        };
        var stamped = ResultFreshnessAuthority.Stamp(
            submitted,
            new ResultFreshnessAuthorityInputs(
                "run-1",
                "task-a",
                "att-1",
                new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal),
                foreign,
                null,
                new HashSet<string>(StringComparer.Ordinal)),
            ["ev-b"]);
        AssertEqual(ResultFreshnessState.NeedsRevalidation, stamped.State,
            "Same-Run evidence from another Task is not implicitly owned.");
        var allowed = ResultFreshnessAuthority.Stamp(
            submitted,
            new ResultFreshnessAuthorityInputs(
                "run-1",
                "task-a",
                "att-1",
                new Dictionary<string, DurableResultArtifactRecord>(StringComparer.Ordinal),
                foreign,
                null,
                new HashSet<string>(StringComparer.Ordinal),
                AllowedCrossTaskEvidenceIds: new HashSet<string>(["ev-b"], StringComparer.Ordinal)),
            ["ev-b"]);
        AssertEqual(ResultFreshnessState.Current, allowed.State,
            "Explicit cross-task evidence references may be current.");
    }

    private static void NewExecutionAfterOverrideUsesNewPolicyImmediately()
    {
        var pending = new OversightOverrideRecord(
            "o-manual",
            OversightScopeKind.Project,
            "proj",
            NarrativeDecisionAuthority.AuthorConfirmedRequired,
            RuntimePermissionMode.Ask,
            OversightActivation.PendingBindToken("o-manual"),
            "user",
            50);
        var inFlight = new OversightActivationContext("proj", null, "task-a", "run-a", [], 10, 10);
        AssertTrue(!OversightActivation.IsActiveForExecution(pending, inFlight),
            "In-flight Run A must keep the pinned old policy until its safe checkpoint.");
        var sameClock = new OversightActivationContext("proj", null, "task-same", "run-same", [], 50, 50);
        AssertTrue(!OversightActivation.IsActiveForExecution(pending, sameClock),
            "Equal created_at_ms must treat the execution as pre-override, not born after.");
        var bornAfter = new OversightActivationContext("proj", null, "task-b", "run-b", [], 80, 80);
        AssertTrue(OversightActivation.IsActiveForExecution(pending, bornAfter),
            "Run B created after the override must start under the new policy immediately.");
        var inverse = pending with
        {
            NarrativeAuthority = NarrativeDecisionAuthority.AgentDelegated,
            RuntimePermission = RuntimePermissionMode.AutoApproveScoped
        };
        AssertTrue(OversightActivation.IsActiveForExecution(inverse, bornAfter),
            "MANUAL→AUTO must also apply immediately to executions created after the change.");
    }

    private static void FormalAuthorizationSnapshotRoundtripsWinningScope()
    {
        var policy = new EffectiveOversightPolicy(
            NarrativeDecisionAuthority.AgentDelegated,
            RuntimePermissionMode.AutoApproveScoped,
            OversightScopeKind.Task,
            "task-win",
            "ovr-1",
            null,
            true);
        var snapshot = FormalAuthorizationSnapshot.Capture("acc-1", "tx-1", policy, "agent-1", 9);
        var parsed = FormalAuthorizationSnapshot.TryParse(snapshot.WriteCanonical());
        AssertTrue(parsed is not null, "Authorization snapshot must round-trip.");
        AssertEqual(OversightScopeKind.Task, parsed!.WinningScope, "Winning scope must be frozen.");
        AssertEqual("task-win", parsed.WinningScopeId, "Winning scope id must be frozen.");
        AssertEqual("task-win", parsed.ToDelegatedDecision().ScopeId, "Delegated repair must use the frozen scope.");
    }

    private static void RuntimeLogicalTimestampIsStrictlyMonotonic()
    {
        AssertEqual(1_000L, RuntimeLogicalTimestamp.Allocate(1_000, null), "Empty store must use wall-clock.");
        AssertEqual(1_001L, RuntimeLogicalTimestamp.Allocate(1_000, 1_000), "Same-millisecond wall-clock must bump past persisted max.");
        AssertEqual(5_000L, RuntimeLogicalTimestamp.Allocate(5_000, 1_000), "Later wall-clock must win when it is already ahead.");
        AssertEqual(1_001L, RuntimeLogicalTimestamp.Allocate(1_000, 1_000), "Restart must not allocate at or below the persisted max.");
    }

    private static CompletionCheckInputs Inputs(
        IReadOnlyList<string>? outputs = null,
        IReadOnlyList<string>? shape = null,
        int blocking = 0,
        bool artifact = true,
        SemanticCompletionOutcome? semantic = null) =>
        new(
            new HashSet<string>(outputs ?? ["conclusion"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(shape ?? ["conclusion"], StringComparer.Ordinal),
            blocking,
            [],
            artifact,
            semantic);

    private static TaskResultArtifactV1 SampleArtifact(string conclusion) =>
        new(
            "ra-1",
            "task-1",
            ResultArtifactStatus.Complete,
            conclusion,
            [new ResultFindingV1("f1", "finding", "obj-1")],
            ["ev-1"],
            "low",
            [new ResultDiagnosticV1("d1", "info", "ok")],
            ["obj-1"],
            ["follow-up"],
            new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1("rev-1", ["obj:a"], "ev", null, null, null, [], null, null, []),
                new ResultProvenanceV1("run-1", "task-1", "att-1", "spec-1", "plan-1", "req-1", "cs-1", "tx-1")),
            "cs-1",
            1);

    private static SpecialistProfileDefinitionV1 SampleProfile(
        SpecialistScopeKind scope,
        string stage,
        bool enabled,
        IReadOnlyList<string> capabilities) =>
        new(
            1,
            "spec-" + stage,
            "reviewer",
            "Reviewer",
            "reviews",
            1,
            scope,
            [stage],
            ["use for " + stage],
            [],
            ["review chapter"],
            true,
            "behavioral prompt body",
            ["review"],
            ["write canon"],
            false,
            false,
            new SpecialistInputContractV1(["required"], ["advisory"], ["optional"]),
            ["conclusion"],
            TaskCompletionContractV1.Empty,
            capabilities,
            RuntimePermissionMode.Ask,
            enabled,
            null,
            null,
            null);
}
