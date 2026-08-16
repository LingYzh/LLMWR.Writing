using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.NarrativeChange;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Narrative;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Application.Tests;

internal static class Wp13ApplicationTests
{
    private static readonly Guid ProjectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab");
    private static readonly ProjectScope Scope = new(ProjectId, "workspace-13");

    public static int Run()
    {
        DeterministicCompletionPassesAndIsIdempotent();
        MissingResultAndBlockingDiagnosticFailCompletion();
        SemanticReviewRequiredWithoutEvaluator();
        SemanticPassWithInjectedEvaluator();
        RequiredStaleBlocksDispatchAndCompletion();
        AdvisoryAndOptionalStaleDoNotHardBlock();
        ProposedDowngradeDoesNotMutateEffectiveEdge();
        OversightDefaultsAndTaskWins();
        BypassDoesNotDelegateAndAgentCannotSelfDelegate();
        ForwardOnlyPendingBindUntilCheckpoint();
        PendingApprovalReevaluationIsNotBlindAuto();
        RuntimeGrillPausesAndAuthorResolves();
        RuntimeGrillAgentDeniedWhenAuthorRequired();
        RuntimeGrillDuplicateAndRace();
        BuiltInSpecialistIsImmutableAndDuplicatePreservesBase();
        InvalidAndForbiddenSpecialistScopesFail();
        IsolatedPacketOmitsTranscriptAndIncludesRequiredResults();
        TemporaryChildIsNotInsertedIntoLibraryAndSharesBudget();
        BackgroundLifecycleAndStopDoNotTouchUnrelated();
        HandlerRejectsAgentOversightMutationAndDirectMessageApis();
        DelegatedAuthorityRequiresExplicitOversightAndDoesNotTrustBypass();
        DelegatedAcceptStillEnforcesRoleTrustAndUnknownGrill();
        BackgroundRecoveryDoesNotFailQueuedOrUnknown();
        AgentCannotMutateForeignTask();
        ReadyAndMissingAttemptCannotJumpToCompleted();
        IncompleteAndFailedResultCannotComplete();
        CompletedHandoffResultIsFrozen();
        FreshnessSpoofIsNotAcceptedAsCurrent();
        DependencyStatusIsCoreDerivedAndOptionalStaleDoesNotBlock();
        OversightTaskAndStorylineWinOverProject();
        WorkflowStorylineLinkPersistsInMemory();
        ForwardOnlyTaskOverrideIgnoresUnrelatedCheckpoint();
        CallerCheckpointCannotForceImmediateActivation();
        ManualToAutoReevaluatesPendingApprovals();
        PlanInvalidContinueStaysPlanBlocked();
        GrillCrossRunOwnershipIsDenied();
        GrillCompareAndSetHasOneWinner();
        DelegatedIdentityIgnoresCallerPayload();
        PostCommitProvenanceRetryIsIdempotent();
        ToolCallStopDoesNotCancelOwnerRun();
        ForgedBackgroundExecutionCannotBeStopped();
        SpecialistUpdateIdentityMustMatch();
        SecretMaterialInFreshnessRejectsArtifact();
        IpcRunACannotSubmitResultForTaskB();
        RequiredDependencyIgnoresProvisionalProducerResult();
        ConsumerStalesWhenProducerFinalResultChanges();
        SameRunEvidenceIsNotOwnedWithoutReference();
        NewRunAfterPolicyChangeUsesNewPolicy();
        PendingReevaluationHandlesAllOutcomes();
        GrillResumeAnchorsToExactCheckpoint();
        GrillResumeIgnoresLaterUnrelatedCheckpoint();
        GrillUnknownRemainsBlockedAndUnchangedContinues();
        ToolCallStopUnavailableLeavesRunning();
        ToolCallStopConfirmedCancelsExactCallOnly();
        HistoricalDelegatedProvenanceSurvivesOversightChange();
        DelegatedProvenanceConflictIsVisible();
        MissingDependencyEdgesAreEmitted();
        TransitiveRequiredFreshnessPropagatesAndRestores();
        GrillPromptProviderModelChangedSinceExactCheckpoint();
        SameMillisecondOversightOrderingIsDeterministic();
        Console.WriteLine("Application WP13 tests passed (61).");
        return 61;
    }

    private static void DeterministicCompletionPassesAndIsIdempotent()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-complete", "writer");
        SeedDispatchedTask(harness, "run-complete", "task-complete");
        SubmitComplete(harness, agent, "task-complete");
        var first = Success(harness.Wp13.RequestTaskCompletion("task-complete", agent));
        AssertEqual("pass", first.Outcome, "Deterministic completion must pass.");
        var second = Success(harness.Wp13.RequestTaskCompletion("task-complete", agent));
        AssertEqual(first.ResultArtifactId, second.ResultArtifactId, "Duplicate RequestComplete must be idempotent.");
        AssertEqual("completed", harness.Store.GetTask("task-complete")?.Status, "Task must complete with the Result.");
        AssertTrue(harness.Store.GetLatestResultArtifact("task-complete") is not null, "Completed task must have a Result Artifact.");
    }

    private static void MissingResultAndBlockingDiagnosticFailCompletion()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-missing", "writer");
        SeedDispatchedTask(harness, "run-missing", "task-missing");
        AssertEqual(RuntimeError.CompletionFailed, harness.Wp13.RequestTaskCompletion("task-missing", agent).Failure?.Code,
            "Missing Result Artifact must fail completion.");
        SeedDispatchedTask(harness, "run-missing", "task-blocking");
        var artifact = Artifact("task-blocking", ResultArtifactStatus.Complete, blocking: true);
        harness.Store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(artifact));
        AssertEqual(RuntimeError.CompletionFailed, harness.Wp13.RequestTaskCompletion("task-blocking", agent).Failure?.Code,
            "Blocking diagnostics must fail deterministic completion.");
    }

    private static void SemanticReviewRequiredWithoutEvaluator()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-semantic", "writer");
        SeedDispatchedTask(harness, "run-semantic", "task-semantic");
        harness.Wp13.SetTaskCompletionContract("task-semantic", new TaskCompletionContractV1(
            1, [], [], [], [], true, [new SemanticCompletionCriterion("coverage", "required coverage")]));
        SubmitComplete(harness, agent, "task-semantic");
        AssertEqual(RuntimeError.SemanticReviewRequired, harness.Wp13.RequestTaskCompletion("task-semantic", agent).Failure?.Code,
            "Production without a semantic evaluator must not silently PASS.");
    }

    private static void SemanticPassWithInjectedEvaluator()
    {
        var harness = Harness(new PassSemanticEvaluator());
        var agent = Agent(harness, "run-semantic-pass", "writer");
        SeedDispatchedTask(harness, "run-semantic-pass", "task-semantic-pass");
        harness.Wp13.SetTaskCompletionContract("task-semantic-pass", new TaskCompletionContractV1(
            1, [], [], [], [], true, [new SemanticCompletionCriterion("coverage", "required coverage")]));
        SubmitComplete(harness, agent, "task-semantic-pass");
        AssertEqual("pass", Success(harness.Wp13.RequestTaskCompletion("task-semantic-pass", agent)).Outcome,
            "Injected semantic evaluator must be able to PASS.");
    }

    private static void RequiredStaleBlocksDispatchAndCompletion()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-stale", "writer");
        SeedDispatchedTask(harness, "run-stale", "producer");
        Success(harness.Scheduler.CreateTask("run-stale", "consume", 1, null, "consumer"));
        SubmitComplete(harness, agent, "producer");
        Success(harness.Wp13.RequestTaskCompletion("producer", agent));
        Success(harness.Wp13.CreateResultDependency("consumer", "producer", "required"));
        var stale = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("producer")!);
        var staleRecord = ResultArtifactCanonicalJson.ToDurable(stale with
        {
            ResultArtifactId = Guid.NewGuid().ToString("D"),
            ProducedAtMs = stale.ProducedAtMs + 1,
            Freshness = stale.Freshness with { State = ResultFreshnessState.Stale }
        });
        harness.Store.InsertResultArtifact(staleRecord);
        Success(harness.Wp13.RefreshResultDependencyStatus("producer", "consumer"));
        AssertEqual("blocked", harness.Store.GetTask("consumer")?.Status, "REQUIRED stale must block readiness.");
        var dispatch = harness.Scheduler.DispatchReadyTask("consumer");
        AssertEqual("blocked", dispatch.Value?.Outcome, "REQUIRED stale must not dispatch as READY.");
        harness.Store.UpdateTaskStatus("consumer", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 2_000);
        harness.Store.InsertAttempt(new DurableAttemptRecord(
            "attempt-consumer",
            "consumer",
            1,
            AttemptStatusCodec.ToDurableValue(AttemptStatus.Running),
            2_000,
            null));
        SubmitComplete(harness, agent, "consumer");
        AssertEqual(RuntimeError.CompletionFailed, harness.Wp13.RequestTaskCompletion("consumer", agent).Failure?.Code,
            "Completion must fail while a REQUIRED Result is stale.");
    }

    private static void AdvisoryAndOptionalStaleDoNotHardBlock()
    {
        var harness = Harness();
        SeedRunningTask(harness, "run-warn", "producer-a");
        Success(harness.Scheduler.CreateTask("run-warn", "consume", 1, null, "consumer-a"));
        harness.Store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(Artifact("producer-a", ResultArtifactStatus.Complete)));
        Success(harness.Wp13.CreateResultDependency("consumer-a", "producer-a", "advisory"));
        Success(harness.Wp13.CreateResultDependency("consumer-a", "producer-a", "optional"));
        var artifact = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("producer-a")!);
        harness.Store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(artifact with
        {
            ResultArtifactId = Guid.NewGuid().ToString("D"),
            ProducedAtMs = artifact.ProducedAtMs + 1,
            Freshness = artifact.Freshness with { State = ResultFreshnessState.Stale }
        }));
        Success(harness.Wp13.RefreshResultDependencyStatus("producer-a", "consumer-a"));
        AssertEqual("ready", harness.Store.GetTask("consumer-a")?.Status, "ADVISORY/OPTIONAL stale must not hard-block.");
        var handoff = Success(harness.Wp13.GetTaskHandoff("consumer-a", false));
        AssertTrue(handoff.Warnings.Length > 0, "ADVISORY stale must warn.");
        AssertEqual(false, handoff.IncludeTranscript, "Default handoff must omit transcript.");
        AssertTrue(handoff.Edges.Any(item =>
                StringComparer.Ordinal.Equals(item.DependencyKind, "optional") &&
                StringComparer.Ordinal.Equals(item.DependencyStatus, "stale") &&
                !item.BlocksDispatch &&
                !item.BlocksCompletion),
            "OPTIONAL stale must remain stale without a hard block.");
    }

    private static void ProposedDowngradeDoesNotMutateEffectiveEdge()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-propose", "writer");
        SeedRunningTask(harness, "run-propose", "prod");
        Success(harness.Scheduler.CreateTask("run-propose", "cons", 1, null, "cons"));
        var created = Success(harness.Wp13.CreateResultDependency("cons", "prod", "required"));
        var proposed = Success(harness.Wp13.ProposeResultDependencyChange(created.DependencyId, "optional", "too strict", agent));
        AssertEqual("required", proposed.EffectiveKind, "Proposal must not silently change the effective REQUIRED edge.");
        AssertEqual("required", harness.Store.GetDependency(created.DependencyId)?.DependencyKind, "Durable kind must remain required.");
    }

    private static void OversightDefaultsAndTaskWins()
    {
        var harness = Harness();
        var user = User();
        var defaults = Success(harness.Wp13.GetEffectiveOversight(null, null, null));
        AssertEqual("author_confirmed_required", defaults.NarrativeAuthority, "Default narrative axis must be Author-required.");
        AssertEqual("ask", defaults.RuntimePermissionMode, "Default permission axis must be ASK.");
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), user));
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-win", "author_confirmed_required", "ask", null), user));
        var task = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, "task-win"));
        AssertEqual("author_confirmed_required", task.NarrativeAuthority, "Task override must win.");
        AssertEqual("task", task.WinningScope, "Winning scope must be task.");
    }

    private static void BypassDoesNotDelegateAndAgentCannotSelfDelegate()
    {
        var harness = Harness();
        var user = User();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "author_confirmed_required", "bypass_permissions", null), user));
        var bypass = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, null));
        AssertEqual("author_confirmed_required", bypass.NarrativeAuthority, "BYPASS must not create AGENT_DELEGATED.");
        var agent = Agent(harness, "run-bypass", "pm");
        AssertEqual(RuntimeError.OversightDenied,
            harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
                "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), agent).Failure?.Code,
            "Agent must not self-delegate.");
    }

    private static void ForwardOnlyPendingBindUntilCheckpoint()
    {
        var harness = Harness();
        SeedRunningTask(harness, "run-forward", "task-forward");
        Success(harness.Scheduler.DispatchReadyTask("task-forward"));
        var created = Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-forward", "agent_delegated", "auto_approve_scoped", null), User()));
        AssertEqual(false, created.Active, "In-flight override must wait for the next checkpoint.");
        var checkpoint = CheckpointV1.Create("plan", "digest", "{}", "{}", "summary", [], [], [], [], [], [], null, null, null, null);
        Success(harness.Scheduler.PersistCheckpoint("run-forward", "task-forward", 1, CanonicalJson.WriteCheckpoint(checkpoint), "{}"));
        var after = Success(harness.Wp13.GetEffectiveOversight(null, null, "task-forward"));
        AssertEqual("agent_delegated", after.NarrativeAuthority, "Override must activate at the next safe checkpoint.");
    }

    private static void PendingApprovalReevaluationIsNotBlindAuto()
    {
        var pending = new PendingApprovalSnapshot(
            "a1",
            ApprovalKindCodec.RuntimeGrill,
            true,
            true,
            true,
            true,
            true,
            false,
            false,
            EffectiveOversightPolicy.ApplicationDefault with
            {
                NarrativeAuthority = NarrativeDecisionAuthority.AgentDelegated,
                RuntimePermission = RuntimePermissionMode.AutoApproveScoped
            });
        AssertEqual(PendingApprovalReevaluation.Denied, PendingApprovalReevaluator.Reevaluate(pending),
            "AUTO must not blindly approve when Project Trust is missing.");
    }

    private static void RuntimeGrillPausesAndAuthorResolves()
    {
        var harness = Harness();
        SeedRunningTask(harness, "run-grill", "task-grill");
        Success(harness.Scheduler.DispatchReadyTask("task-grill"));
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill",
            "task-grill",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next-beat", ["continue", "replan"], "What next?"),
            "baseline-1"));
        AssertEqual("pending", paused.Status, "Grill pause must be durable.");
        AssertEqual("paused", harness.Store.GetTask("task-grill")?.Status, "Task must pause for Runtime Grill.");
        var resolved = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
            User()));
        AssertEqual("resolved", resolved.Status, "Author must resolve Author-required Grill.");
    }

    private static void RuntimeGrillAgentDeniedWhenAuthorRequired()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-grill-agent", "writer");
        SeedRunningTask(harness, "run-grill-agent", "task-grill-agent");
        Success(harness.Scheduler.DispatchReadyTask("task-grill-agent"));
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill-agent",
            "task-grill-agent",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next-beat", ["continue"], "What next?"),
            "baseline-2"));
        AssertEqual(RuntimeError.GrillAuthorRequired,
            harness.Wp13.ResolveRuntimeGrill(
                new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
                agent).Failure?.Code,
            "Author-required Grill must not be Agent-resolved.");
        AssertEqual(RuntimeError.GrillOptionRejected,
            harness.Wp13.ResolveRuntimeGrill(
                new ResolveRuntimeGrillRequest(paused.ApprovalId, "expand", "expand-scope", null),
                agent).Failure?.Code,
            "Unknown Grill option must be rejected.");
    }

    private static void RuntimeGrillDuplicateAndRace()
    {
        var harness = Harness();
        SeedRunningTask(harness, "run-race", "task-race");
        Success(harness.Scheduler.DispatchReadyTask("task-race"));
        var first = Success(harness.Wp13.PauseRuntimeGrill(
            "run-race",
            "task-race",
            RuntimeGrillPauseReason.PlanAuthorityAmbiguous,
            new RuntimeGrillQuestionV1("choice", ["a"], "Choose"),
            "baseline-3"));
        var second = Success(harness.Wp13.PauseRuntimeGrill(
            "run-race",
            "task-race",
            RuntimeGrillPauseReason.PlanAuthorityAmbiguous,
            new RuntimeGrillQuestionV1("choice", ["a"], "Choose"),
            "baseline-3"));
        AssertEqual(first.ApprovalId, second.ApprovalId, "Duplicate Grill requests must share identity.");
        Success(harness.Wp13.ResolveRuntimeGrill(new ResolveRuntimeGrillRequest(first.ApprovalId, "a", "a", null), User()));
        AssertEqual(RuntimeError.GrillAlreadyResolved,
            harness.Wp13.ResolveRuntimeGrill(new ResolveRuntimeGrillRequest(first.ApprovalId, "a", "a", null), User()).Failure?.Code,
            "Competing resolution must return already-resolved.");
    }

    private static void BuiltInSpecialistIsImmutableAndDuplicatePreservesBase()
    {
        var harness = Harness();
        var listed = Success(harness.Wp13.ListSpecialists("builtin"));
        AssertTrue(listed.Items.Length >= 1, "Synthetic built-in must be listed.");
        AssertEqual(RuntimeError.SpecialistImmutable,
            harness.Wp13.CreateSpecialist("builtin", listed.Items[0].Name, User()).Failure?.Code,
            "Built-in must be immutable.");
        var duplicate = Success(harness.Wp13.DuplicateSpecialist(listed.Items[0].ProfileId, "builtin", "user", User()));
        AssertTrue(!string.IsNullOrWhiteSpace(duplicate.BaseDefinitionDigest), "Duplicate must preserve base digest.");
        AssertTrue(harness.UserStore.Find(duplicate.ProfileId) is not null, "User Library duplicate must not live only in project.db.");
    }

    private static void InvalidAndForbiddenSpecialistScopesFail()
    {
        var harness = Harness();
        AssertEqual(RuntimeError.SpecialistImmutable,
            harness.Wp13.CreateSpecialist("task", "{}", User()).Failure?.Code,
            "Persistent task scope must be forbidden.");
        var invalid = Success(harness.Wp13.ValidateSpecialist("{\"schemaVersion\":9,\"profileId\":\"x\",\"name\":\"x\",\"displayName\":\"x\",\"description\":\"d\",\"version\":1,\"scopeKind\":\"project\",\"applicableWorkflowStages\":[],\"whenToUse\":[],\"whenNotToUse\":[],\"exampleTasks\":[],\"allowOrchestratorAutoCall\":true,\"behavioralPrompt\":\"p\",\"primaryResponsibilities\":[],\"outOfScope\":[],\"requestsEditCapability\":false,\"requestsDelegationCapability\":false,\"inputContract\":{\"required\":[],\"advisory\":[],\"optional\":[]},\"outputContractKeys\":[],\"completionContract\":{\"schemaVersion\":1,\"requiredOutputKeys\":[],\"requiredProducerTaskIds\":[],\"requiredInputRefs\":[],\"requiredResultShapeKeys\":[],\"requireZeroBlockingDiagnostics\":true,\"semanticCriteria\":[]},\"requestedCapabilities\":[],\"enabled\":true}"));
        AssertEqual(false, invalid.Valid, "Unsupported profile version must fail validation.");
    }

    private static void IsolatedPacketOmitsTranscriptAndIncludesRequiredResults()
    {
        var harness = Harness();
        SeedRunningTask(harness, "run-packet", "producer-p");
        Success(harness.Scheduler.CreateTask("run-packet", "child", 1, "producer-p", "child-p"));
        var result = ResultArtifactCanonicalJson.ToDurable(Artifact("producer-p", ResultArtifactStatus.Complete));
        harness.Store.InsertResultArtifact(result);
        harness.Store.UpdateTaskStatus("producer-p", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Completed), 2_000);
        Success(harness.Wp13.CreateResultDependency("child-p", "producer-p", "required"));
        var packet = Success(harness.Wp13.BuildIsolatedTaskPacket("run-packet", "child-p", "builtin.reviewer", "temp"));
        AssertEqual(SpecialistContextMode.Isolated, packet.ContextMode, "Default context must be isolated.");
        AssertTrue(packet.RequiredResultArtifactIds.Contains(result.ResultArtifactId), "Required upstream Result must be included.");
        AssertTrue(packet.TemporaryInstructions is not null, "Temporary instructions belong on the packet, not the library.");
    }

    private static void TemporaryChildIsNotInsertedIntoLibraryAndSharesBudget()
    {
        var harness = Harness();
        harness.Auth.Allow = true;
        for (var index = 0; index < 4; index++)
        {
            var runId = "occ-" + index;
            Success(harness.Scheduler.CreateRun("wf-temp", "writer", null, runId));
            Success(harness.Scheduler.CreateTask(runId, "write", 1, null, runId + "-t"));
            var dispatched = Success(harness.Scheduler.DispatchReadyTask(runId + "-t"));
            Success(harness.Scheduler.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId));
        }

        Success(harness.Scheduler.CreateRun("wf-temp", "writer", null, "parent-temp"));
        Success(harness.Scheduler.CreateTask("parent-temp", "write", 1, null, "parent-temp-t"));
        var before = harness.Wp13.ListSpecialists(null).Value!.Items.Length;
        var spawned = Success(harness.Wp13.SpawnTemporarySpecialist("parent-temp", "parent-temp-t", "writer", User()));
        AssertEqual("queued", spawned.Outcome, "Background/temporary child must share the WP12 budget.");
        AssertEqual(before, harness.Wp13.ListSpecialists(null).Value!.Items.Length,
            "Temporary Specialist must not be inserted into the library.");
        var listed = Success(harness.Wp13.ListBackgroundTasks("parent-temp"));
        AssertEqual(1, listed.Items.Length, "Temporary child must appear as a Background Task.");
    }

    private static void BackgroundLifecycleAndStopDoNotTouchUnrelated()
    {
        var harness = Harness();
        harness.Auth.Allow = true;
        Success(harness.Scheduler.CreateRun("wf-bg", "writer", null, "owner-a"));
        Success(harness.Scheduler.CreateTask("owner-a", "write", 1, null, "owner-a-t"));
        Success(harness.Scheduler.CreateRun("wf-bg", "writer", null, "owner-b"));
        Success(harness.Scheduler.CreateTask("owner-b", "write", 1, null, "owner-b-t"));
        var childA = Success(harness.Wp13.SpawnTemporarySpecialist("owner-a", "owner-a-t", "writer", User()));
        var childB = Success(harness.Wp13.SpawnTemporarySpecialist("owner-b", "owner-b-t", "writer", User()));
        var list = Success(harness.Wp13.ListBackgroundTasks(null));
        var stopA = list.Items.Single(item => StringComparer.Ordinal.Equals(item.OwnerRunId, "owner-a"));
        Success(harness.Wp13.StopBackgroundTask(stopA.BackgroundTaskId));
        AssertEqual("cancelled", harness.Store.GetBackgroundTask(stopA.BackgroundTaskId)?.Status, "Stop must cancel the intended task.");
        var other = list.Items.Single(item => StringComparer.Ordinal.Equals(item.OwnerRunId, "owner-b"));
        AssertTrue(!StringComparer.Ordinal.Equals(harness.Store.GetBackgroundTask(other.BackgroundTaskId)?.Status, "cancelled"),
            "Unrelated background task must survive.");
        _ = childA;
        _ = childB;
        AssertEqual(RuntimeError.BackgroundIllegalTransition,
            harness.Wp13.StopBackgroundTask(stopA.BackgroundTaskId).Failure?.Code,
            "cancelled → running/stop must be illegal.");
    }

    private static void HandlerRejectsAgentOversightMutationAndDirectMessageApis()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-ipc", "writer");
        var handler = new Wp13IpcCommandHandler(harness.Wp13, "workspace-13");
        var payload = JsonSerializer.SerializeToElement(
            new SetOversightOverrideRequest("project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null),
            IpcJsonContext.Default.SetOversightOverrideRequest);
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
            IpcClientKind.Worker,
            "c1",
            null,
            agent,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            IpcSemanticTypes.SetOversightOverride,
            payload,
            CancellationToken.None)).GetAwaiter().GetResult();
        AssertTrue(result is not null, "Handler must respond.");
        var error = IpcJson.Deserialize(result!.ResponseUtf8, IpcJsonContext.Default.ErrorEnvelope);
        AssertEqual(IpcErrorCodes.OversightMutationDenied, error.Payload.Code, "Agent setOversightOverride must be denied.");
        AssertTrue(!IpcSemanticTypes.All.Contains("sendMessageToSpecialist"), "Direct Specialist messaging must not exist.");
        AssertTrue(!IpcSemanticTypes.All.Contains("appendSpecialistInstruction"), "Direct Specialist instruction must not exist.");
        AssertTrue(!IpcSemanticTypes.All.Contains("forceSpecialistComplete"), "Force-complete must not exist.");
    }

    private static void DelegatedAuthorityRequiresExplicitOversightAndDoesNotTrustBypass()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-auth", "pm");
        var service = new ChapterAuthorityService(
            new UnusedBlobStore(),
            new UnusedTransactionCoordinator(),
            new NullAcceptanceStore(),
            new UnusedReviewer(),
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            new AllowAuth { Allow = true },
            harness.Wp13,
            harness.Wp13);
        var denied = service.AcceptChapterCandidate(new AcceptChapterCandidateCommand("c1", "k1", "agent", Principal: agent));
        AssertEqual(ChapterAuthorityError.AcceptanceNotAuthorized, denied.Failure?.Code,
            "Agent formal accept must stay denied without AGENT_DELEGATED Oversight.");

        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project",
            ProjectId.ToString("D"),
            "agent_delegated",
            "auto_approve_scoped",
            null), User()));
        var gated = service.AcceptChapterCandidate(new AcceptChapterCandidateCommand("c1", "k1", "agent", Principal: agent));
        AssertEqual(ChapterAuthorityError.CandidateNotFound, gated.Failure?.Code,
            "Explicit AGENT_DELEGATED must pass the Oversight gate into normal Authority lookup.");

        var closed = new ChapterAuthorityService(
            new UnusedBlobStore(),
            new UnusedTransactionCoordinator(),
            new NullAcceptanceStore(),
            new UnusedReviewer(),
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            new AllowAuth { Allow = false },
            harness.Wp13,
            harness.Wp13);
        AssertEqual(ChapterAuthorityError.CapabilityDenied,
            closed.AcceptChapterCandidate(new AcceptChapterCandidateCommand("c1", "k1", "agent", Principal: agent)).Failure?.Code,
            "AGENT_DELEGATED must still fail closed on capability denial.");

        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project",
            ProjectId.ToString("D"),
            "author_confirmed_required",
            "bypass_permissions",
            null), User()));
        AssertEqual(ChapterAuthorityError.AcceptanceNotAuthorized,
            service.AcceptChapterCandidate(new AcceptChapterCandidateCommand("c1", "k1", "agent", Principal: agent)).Failure?.Code,
            "BYPASS_PERMISSIONS must not create Narrative Authority.");
    }

    private static void DelegatedAcceptStillEnforcesRoleTrustAndUnknownGrill()
    {
        var harness = Harness();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project",
            ProjectId.ToString("D"),
            "agent_delegated",
            "auto_approve_scoped",
            null), User()));
        var trusted = new CoreAuthorizationService(new TrustedProjectPolicy(), harness.Wp13);
        var writer = Agent(harness, "run-writer-deny", "writer");
        var writerDecision = trusted.Authorize(writer, new AuthorizationRequest(Capability.AuthorityAccept));
        AssertEqual(CapabilityDecisionKind.Denied, writerDecision.Decision, "Writer role deny must survive AGENT_DELEGATED.");
        AssertTrue(writerDecision.Reasons.Contains(CapabilityDecisionReason.RoleDenied), "Writer denial must be role-based.");

        var pm = Agent(harness, "run-pm-trust", "pm");
        var missingTrust = new CoreAuthorizationService(new TrustedProjectPolicy { ProjectTrusted = false }, harness.Wp13)
            .Authorize(pm, new AuthorizationRequest(Capability.AuthorityAccept));
        AssertTrue(missingTrust.Reasons.Contains(CapabilityDecisionReason.TrustRequired),
            "AGENT_DELEGATED must not synthesize Project Trust.");

        var pmAllowed = trusted.Authorize(pm, new AuthorizationRequest(Capability.AuthorityAccept));
        AssertEqual(CapabilityDecisionKind.Allowed, pmAllowed.Decision,
            "PM + trust + explicit AGENT_DELEGATED + AutoApproveScoped must allow Authority.Accept.");

        SeedRunningTask(harness, "run-unknown-grill", "task-unknown-grill");
        Success(harness.Scheduler.DispatchReadyTask("task-unknown-grill"));
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-unknown",
            "run-unknown-grill",
            "task-unknown-grill",
            "Shell.Execute",
            "running",
            "unknown"));
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-unknown-grill",
            "task-unknown-grill",
            RuntimeGrillPauseReason.PlanAuthorityAmbiguous,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "unknown-base"));
        var resolved = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
            User()));
        AssertEqual("BlockUnknown", resolved.ResumeDecision,
            "A Grill answer must not erase UNKNOWN side-effect safety.");
    }

    private static void BackgroundRecoveryDoesNotFailQueuedOrUnknown()
    {
        var harness = Harness();
        Success(harness.Scheduler.CreateRun("wf-bg", "writer", null, "recover-owner"));
        Success(harness.Scheduler.CreateTask("recover-owner", "write", 1, null, "recover-owner-t"));
        harness.Store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-queued",
            "recover-owner",
            "recover-owner-t",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(BackgroundTaskKind.SubAgentRun, "child-queued", null, null, null)),
            "queued",
            null,
            1_000,
            null));
        harness.Store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-unknown",
            "recover-owner",
            "recover-owner-t",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(BackgroundTaskKind.ToolCall, "recover-owner", "tool-u", null, "recover-owner-t")),
            "running",
            "cp-1",
            1_000,
            null));
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-u",
            "recover-owner",
            "recover-owner-t",
            "Shell.Execute",
            "running",
            "unknown"));
        harness.Store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-gone",
            "recover-owner",
            "recover-owner-t",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(BackgroundTaskKind.Worker, "missing-run", null, "w-gone", null)),
            "running",
            null,
            1_000,
            null));
        harness.Wp13.RecoverBackgroundTasks();
        AssertEqual("queued", harness.Store.GetBackgroundTask("bg-queued")?.Status, "Queued background work must not be failed on restart.");
        AssertEqual("interrupted", harness.Store.GetBackgroundTask("bg-unknown")?.Status, "UNKNOWN background work must interrupt, not auto-retry.");
        AssertEqual("interrupted", harness.Store.GetBackgroundTask("bg-gone")?.Status, "Gone worker must classify interrupted.");
        AssertEqual(
            BackgroundRecoveryClassification.UnknownSideEffect,
            BackgroundTaskLifecycle.ClassifyRestart(
                harness.Store.GetBackgroundTask("bg-unknown")!,
                false,
                true,
                true,
                true),
            "UNKNOWN classification must survive recovery.");
    }

    private static void AgentCannotMutateForeignTask()
    {
        var harness = Harness();
        var runA = Agent(harness, "run-own-a", "writer");
        SeedDispatchedTask(harness, "run-own-b", "task-own-b");
        SeedDispatchedTask(harness, "run-own-a", "task-own-a");
        AssertEqual(RuntimeError.TaskOwnershipDenied,
            harness.Wp13.SubmitResultArtifact(
                new SubmitResultArtifactRequest(
                    "task-own-b",
                    "complete",
                    "{}",
                    "{}",
                    "{}",
                    "{}",
                    "{}",
                    "{}",
                    null),
                runA).Failure?.Code,
            "Run A must not submit a Result for Task B.");
        AssertEqual(RuntimeError.TaskOwnershipDenied,
            harness.Wp13.RequestTaskCompletion("task-own-b", runA).Failure?.Code,
            "Run A must not complete Task B.");
        Success(harness.Wp13.CreateResultDependency("task-own-b", "task-own-a", "required"));
        var dependency = harness.Store.LoadSnapshot().Dependencies.Single(item =>
            StringComparer.Ordinal.Equals(item.ConsumerTaskId, "task-own-b"));
        AssertEqual(RuntimeError.TaskOwnershipDenied,
            harness.Wp13.ProposeResultDependencyChange(dependency.DependencyId, "optional", "stolen", runA).Failure?.Code,
            "Run A must not propose a dependency change for Task B.");
    }

    private static void ReadyAndMissingAttemptCannotJumpToCompleted()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-ready", "writer");
        SeedRunningTask(harness, "run-ready", "task-ready");
        AssertEqual(RuntimeError.IllegalCompletionLifecycle,
            harness.Wp13.RequestTaskCompletion("task-ready", agent).Failure?.Code,
            "READY must not jump to COMPLETED.");
        harness.Store.UpdateTaskStatus("task-ready", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 2_000);
        AssertEqual(RuntimeError.IllegalCompletionLifecycle,
            harness.Wp13.RequestTaskCompletion("task-ready", agent).Failure?.Code,
            "Running without an active Attempt must not complete.");
    }

    private static void IncompleteAndFailedResultCannotComplete()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-incomplete", "writer");
        SeedDispatchedTask(harness, "run-incomplete", "task-incomplete");
        var incomplete = Artifact("task-incomplete", ResultArtifactStatus.Incomplete);
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "task-incomplete",
                "incomplete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", incomplete),
                ResultArtifactCanonicalJson.WriteColumn("findings", incomplete),
                ResultArtifactCanonicalJson.WriteColumn("evidence", incomplete),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", incomplete),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", incomplete),
                ResultArtifactCanonicalJson.WriteColumn("freshness", incomplete),
                null),
            agent));
        AssertEqual(RuntimeError.CompletionFailed,
            harness.Wp13.RequestTaskCompletion("task-incomplete", agent).Failure?.Code,
            "Incomplete Result must not complete a Task.");
        SeedDispatchedTask(harness, "run-incomplete", "task-failed-result");
        var failed = Artifact("task-failed-result", ResultArtifactStatus.Failed);
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "task-failed-result",
                "failed",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", failed),
                ResultArtifactCanonicalJson.WriteColumn("findings", failed),
                ResultArtifactCanonicalJson.WriteColumn("evidence", failed),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", failed),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", failed),
                ResultArtifactCanonicalJson.WriteColumn("freshness", failed),
                null),
            agent));
        AssertEqual(RuntimeError.CompletionFailed,
            harness.Wp13.RequestTaskCompletion("task-failed-result", agent).Failure?.Code,
            "Failed Result must not complete a Task.");
    }

    private static void CompletedHandoffResultIsFrozen()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-freeze", "writer");
        SeedDispatchedTask(harness, "run-freeze", "task-freeze");
        Success(harness.Scheduler.CreateTask("run-freeze", "consume", 1, null, "task-freeze-consumer"));
        SubmitComplete(harness, agent, "task-freeze");
        var completed = Success(harness.Wp13.RequestTaskCompletion("task-freeze", agent));
        Success(harness.Wp13.CreateResultDependency("task-freeze-consumer", "task-freeze", "required"));
        var before = Success(harness.Wp13.GetTaskHandoff("task-freeze-consumer", false));
        AssertEqual(RuntimeError.ResultFrozen,
            harness.Wp13.SubmitResultArtifact(
                new SubmitResultArtifactRequest(
                    "task-freeze",
                    "complete",
                    "{}",
                    "{}",
                    "{}",
                    "{}",
                    "{}",
                    "{}",
                    null),
                agent).Failure?.Code,
            "Completed Task must reject a later Result submission.");
        var after = Success(harness.Wp13.GetTaskHandoff("task-freeze-consumer", false));
        AssertEqual(completed.ResultArtifactId, after.ResultArtifactIds.Single(), "Handoff Result identity must stay frozen.");
        AssertEqual(before.ResultArtifactIds.Single(), after.ResultArtifactIds.Single(), "Downstream handoff must not silently change.");
    }

    private static void FreshnessSpoofIsNotAcceptedAsCurrent()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-fresh", "writer");
        SeedDispatchedTask(harness, "run-fresh", "task-fresh");
        harness.Store.InsertEvidence(new EvidenceRecord(
            "ev-stale",
            "run-fresh",
            "task-fresh",
            "narrative",
            "obj-1",
            "old-digest",
            "{}",
            true,
            1_000));
        var spoofed = Artifact("task-fresh", ResultArtifactStatus.Complete) with
        {
            EvidenceIds = ["ev-stale"],
            Freshness = new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, []),
                new ResultProvenanceV1("stolen-run", "stolen-task", "stolen-attempt", null, null, null, null, null))
        };
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "task-fresh",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", spoofed),
                ResultArtifactCanonicalJson.WriteColumn("findings", spoofed),
                ResultArtifactCanonicalJson.WriteColumn("evidence", spoofed),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", spoofed),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", spoofed),
                ResultArtifactCanonicalJson.WriteColumn("freshness", spoofed),
                null),
            agent));
        var stored = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("task-fresh")!);
        AssertTrue(stored.Freshness.State != ResultFreshnessState.Current,
            "Caller freshness=current against stale evidence must not become Core CURRENT.");
        AssertEqual("run-fresh", stored.Freshness.Provenance.ProducedByRunId, "Core must stamp ProducedByRunId.");
        AssertEqual("task-fresh", stored.Freshness.Provenance.ProducedByTaskId, "Core must stamp ProducedByTaskId.");
        AssertTrue(!StringComparer.Ordinal.Equals(stored.Freshness.Provenance.AttemptId, "stolen-attempt"),
            "Caller AttemptId must not become provenance truth.");
    }

    private static void DependencyStatusIsCoreDerivedAndOptionalStaleDoesNotBlock()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-dep", "writer");
        SeedDispatchedTask(harness, "run-dep", "prod-dep");
        Success(harness.Scheduler.CreateTask("run-dep", "consume", 1, null, "cons-dep"));
        SubmitComplete(harness, agent, "prod-dep");
        Success(harness.Wp13.RequestTaskCompletion("prod-dep", agent));
        var required = Success(harness.Wp13.CreateResultDependency("cons-dep", "prod-dep", "required"));
        var current = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("prod-dep")!);
        harness.Store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(current with
        {
            ResultArtifactId = Guid.NewGuid().ToString("D"),
            ProducedAtMs = current.ProducedAtMs + 1,
            Freshness = current.Freshness with { State = ResultFreshnessState.Stale }
        }));
        var updated = Success(harness.Wp13.UpdateResultDependency(required.DependencyId, "required"));
        AssertEqual("stale", updated.Status, "Core must recompute REQUIRED stale instead of trusting caller status.");
        AssertEqual("blocked", harness.Store.GetTask("cons-dep")?.Status, "REQUIRED stale must hard-block.");
        Success(harness.Scheduler.CreateTask("run-dep", "consume", 1, null, "cons-opt"));
        Success(harness.Wp13.CreateResultDependency("cons-opt", "prod-dep", "optional"));
        Success(harness.Wp13.RefreshResultDependencyStatus("prod-dep", "cons-opt"));
        AssertEqual("ready", harness.Store.GetTask("cons-opt")?.Status, "OPTIONAL stale must not hard-block.");
        var optional = harness.Store.DependenciesForConsumer("cons-opt").Single();
        AssertEqual("stale", optional.Status, "OPTIONAL stale must remain stale.");
        AssertTrue(!ResultDependencyPolicy.Evaluate(optional).BlocksDispatch, "OPTIONAL stale BlocksDispatch must be false.");
    }

    private static void OversightTaskAndStorylineWinOverProject()
    {
        var harness = Harness();
        var user = User();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), user));
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-manual", "author_confirmed_required", "ask", null), user));
        var projectAutoTaskManual = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, "task-manual"));
        AssertEqual("author_confirmed_required", projectAutoTaskManual.NarrativeAuthority, "Project AUTO + Task MANUAL must remain Author-required.");
        AssertEqual("task", projectAutoTaskManual.WinningScope, "Task must win over Project AUTO.");

        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "author_confirmed_required", "ask", null), user));
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-auto", "agent_delegated", "auto_approve_scoped", null), user));
        var projectManualTaskAuto = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, "task-auto"));
        AssertEqual("agent_delegated", projectManualTaskAuto.NarrativeAuthority, "Project MANUAL + Task AUTO must use Task delegation.");

        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), user));
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "storyline", "story-manual", "author_confirmed_required", "ask", null), user));
        var storylineManual = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), "story-manual", null));
        AssertEqual("author_confirmed_required", storylineManual.NarrativeAuthority, "Project AUTO + Storyline MANUAL must remain Manual.");

        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "storyline", "story-auto", "agent_delegated", "auto_approve_scoped", null), user));
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-beats-story", "author_confirmed_required", "ask", null), user));
        var taskWins = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), "story-auto", "task-beats-story"));
        AssertEqual("author_confirmed_required", taskWins.NarrativeAuthority, "Storyline AUTO + Task MANUAL must let Task win.");
        AssertEqual("task", taskWins.WinningScope, "Winning scope must be task.");
    }

    private static void WorkflowStorylineLinkPersistsInMemory()
    {
        var harness = Harness();
        var created = Success(harness.Scheduler.CreateWorkflowRun("wf-story-link", "story-line-1"));
        AssertEqual("story-line-1", created.StorylineId, "CreateWorkflowRun must persist the supplied StorylineId.");
        AssertEqual("story-line-1", harness.Store.GetWorkflowRun(created.WorkflowRunId)?.StorylineId,
            "Reloaded workflow record must keep the Storyline link.");
    }

    private static void ForwardOnlyTaskOverrideIgnoresUnrelatedCheckpoint()
    {
        var harness = Harness();
        SeedDispatchedTask(harness, "run-scoped", "task-b");
        SeedRunningTask(harness, "run-scoped", "task-a");
        var created = Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-b", "agent_delegated", "auto_approve_scoped", null), User()));
        AssertEqual(false, created.Active, "In-flight Task B override must wait for B's checkpoint.");
        var checkpoint = CheckpointV1.Create("plan", "digest-a", "{}", "{}", "a", [], [], [], [], [], [], null, null, null, null);
        Success(harness.Scheduler.PersistCheckpoint("run-scoped", "task-a", 1, CanonicalJson.WriteCheckpoint(checkpoint), "{}"));
        var afterA = Success(harness.Wp13.GetEffectiveOversight(null, null, "task-b"));
        AssertEqual("author_confirmed_required", afterA.NarrativeAuthority, "Task A checkpoint must not activate Task B override.");
        Success(harness.Scheduler.PersistCheckpoint("run-scoped", "task-b", 1, CanonicalJson.WriteCheckpoint(checkpoint), "{}"));
        var afterB = Success(harness.Wp13.GetEffectiveOversight(null, null, "task-b"));
        AssertEqual("agent_delegated", afterB.NarrativeAuthority, "Task B checkpoint must activate the Task B override.");
    }

    private static void CallerCheckpointCannotForceImmediateActivation()
    {
        var harness = Harness();
        SeedDispatchedTask(harness, "run-spoof-cp", "task-spoof-cp");
        var existing = CheckpointV1.Create("plan", "digest", "{}", "{}", "summary", [], [], [], [], [], [], null, null, null, null);
        var checkpointId = Success(harness.Scheduler.PersistCheckpoint(
            "run-spoof-cp",
            "task-spoof-cp",
            1,
            CanonicalJson.WriteCheckpoint(existing),
            "{}"));
        var created = Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task",
            "task-spoof-cp",
            "agent_delegated",
            "auto_approve_scoped",
            checkpointId), User()));
        AssertEqual(false, created.Active, "Existing checkpoint id must not make an in-flight override immediate.");
        var before = Success(harness.Wp13.GetEffectiveOversight(null, null, "task-spoof-cp"));
        AssertEqual("author_confirmed_required", before.NarrativeAuthority, "Caller checkpoint spoof must remain inactive.");
        Success(harness.Scheduler.PersistCheckpoint(
            "run-spoof-cp",
            "task-spoof-cp",
            1,
            CanonicalJson.WriteCheckpoint(existing),
            "{}"));
        var after = Success(harness.Wp13.GetEffectiveOversight(null, null, "task-spoof-cp"));
        AssertEqual("agent_delegated", after.NarrativeAuthority, "The next Core-chosen checkpoint must activate the override.");
    }

    private static void ManualToAutoReevaluatesPendingApprovals()
    {
        var harness = Harness();
        harness.Store.InsertApproval(new DurableApprovalRecord(
            "pending-reeval",
            "run-reeval",
            "task-reeval",
            ApprovalKindCodec.RuntimeGrill,
            ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Pending),
            "digest",
            null,
            null,
            1_000));
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project",
            ProjectId.ToString("D"),
            "agent_delegated",
            "auto_approve_scoped",
            null), User()));
        AssertEqual(
            ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Denied),
            harness.Store.GetApproval("pending-reeval")?.Status,
            "AUTO activation must re-evaluate pending rows, not blindly approve, when Project Trust is missing.");
    }

    private static void PlanInvalidContinueStaysPlanBlocked()
    {
        var harness = Harness();
        SeedDispatchedTask(harness, "run-plan-invalid", "task-plan-invalid");
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-plan-invalid",
            "task-plan-invalid",
            RuntimeGrillPauseReason.PlanAssumptionsInvalid,
            new RuntimeGrillQuestionV1("next", ["continue"], "Continue anyway?"),
            "plan-invalid-base"));
        var resolved = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
            User()));
        AssertEqual("PlanBlocked", resolved.ResumeDecision, "Persisted PlanInvalid plus caller continue must stay PLAN BLOCKED.");
    }

    private static void GrillCrossRunOwnershipIsDenied()
    {
        var harness = Harness();
        var runA = Agent(harness, "run-grill-a", "writer");
        SeedDispatchedTask(harness, "run-grill-b", "task-grill-b");
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill-b",
            "task-grill-b",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "What next?"),
            "cross-run-base"));
        AssertEqual(RuntimeError.GrillOwnershipDenied,
            harness.Wp13.ResolveRuntimeGrill(
                new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
                runA).Failure?.Code,
            "Run A must not resolve Run B's Grill.");
    }

    private static void GrillCompareAndSetHasOneWinner()
    {
        var harness = Harness();
        SeedDispatchedTask(harness, "run-cas", "task-cas");
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-cas",
            "task-cas",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "What next?"),
            "cas-base"));
        var request = new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null);
        RuntimeResult<RuntimeGrillResolveOutcome> first = null!;
        RuntimeResult<RuntimeGrillResolveOutcome> second = null!;
        Parallel.Invoke(
            () => first = harness.Wp13.ResolveRuntimeGrill(request, User()),
            () => second = harness.Wp13.ResolveRuntimeGrill(request, User()));
        var wins = new[] { first, second }.Count(item => item.Succeeded);
        var stale = new[] { first, second }.Count(item => item.Failure?.Code == RuntimeError.GrillAlreadyResolved);
        AssertEqual(1, wins, "Exactly one concurrent Grill resolver must win.");
        AssertEqual(1, stale, "The loser must observe already-resolved.");
    }

    private static void DelegatedIdentityIgnoresCallerPayload()
    {
        var harness = Harness();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project",
            ProjectId.ToString("D"),
            "agent_delegated",
            "auto_approve_scoped",
            null), User()));
        var agent = Agent(harness, "run-delegated-id", "pm");
        var store = new RecordingNarrativeStore();
        var sink = new RecordingDelegatedSink();
        var service = new NarrativeChangeService(
            new UnusedBlobStore(),
            store,
            new UnusedSemanticAssessor(),
            new UnusedImpactAnalyzer(),
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            harness.Auth,
            harness.Wp13,
            sink);
        var applied = service.Apply(new ApplyNarrativeChangeSetCommand(
            RecordingNarrativeStore.ChangeSetId,
            "idem-delegated",
            NarrativeDecisionKind.AgentDelegated,
            "forged-user",
            Principal: agent));
        AssertTrue(applied.Succeeded, "AGENT_DELEGATED apply must reach COMMIT with injected trusted policy.");
        AssertEqual(agent.ToString(), store.LastDeciderId, "Caller DeciderId must not become audit identity.");
        AssertEqual(agent.ToString(), sink.Last?.DecidedBy, "Delegated provenance must be Core-stamped.");
        AssertEqual(OversightScopeKind.Project, sink.Last?.ScopeKind, "Winning Project scope must be persisted when Project authorized the decision.");
    }

    private static void PostCommitProvenanceRetryIsIdempotent()
    {
        var harness = Harness();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project",
            ProjectId.ToString("D"),
            "agent_delegated",
            "auto_approve_scoped",
            null), User()));
        var agent = Agent(harness, "run-prov-retry", "pm");
        var store = new RecordingNarrativeStore();
        var sink = new OneShotThrowingDelegatedSink(harness.Wp13);
        var service = new NarrativeChangeService(
            new UnusedBlobStore(),
            store,
            new UnusedSemanticAssessor(),
            new UnusedImpactAnalyzer(),
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            harness.Auth,
            harness.Wp13,
            sink);
        var first = service.Apply(new ApplyNarrativeChangeSetCommand(
            RecordingNarrativeStore.ChangeSetId,
            "idem-retry",
            NarrativeDecisionKind.AgentDelegated,
            "forged-user",
            Principal: agent));
        AssertTrue(first.Succeeded, "Authority COMMIT must succeed even if delegated provenance insert fails.");
        AssertEqual(0, harness.Store.ListDelegatedDecisions().Count, "Failed provenance insert must not roll back COMMIT.");
        store.MarkApplied();
        var retry = service.Apply(new ApplyNarrativeChangeSetCommand(
            RecordingNarrativeStore.ChangeSetId,
            "idem-retry",
            NarrativeDecisionKind.AgentDelegated,
            "forged-user",
            Principal: agent));
        AssertTrue(retry.Succeeded, "Retry after COMMIT must repair provenance.");
        AssertEqual(1, harness.Store.ListDelegatedDecisions().Count, "Retry must persist exactly one delegated decision.");
        var secondRetry = service.Apply(new ApplyNarrativeChangeSetCommand(
            RecordingNarrativeStore.ChangeSetId,
            "idem-retry",
            NarrativeDecisionKind.AgentDelegated,
            "forged-user",
            Principal: agent));
        AssertTrue(secondRetry.Succeeded, "Duplicate provenance insert must be idempotent.");
        AssertEqual(1, harness.Store.ListDelegatedDecisions().Count, "Duplicate insert must not create a second row.");
    }

    private static void ToolCallStopDoesNotCancelOwnerRun()
    {
        var harness = Harness(toolCancel: new ConfirmingToolCallCancellationPort(_ => true));
        SeedDispatchedTask(harness, "owner-tool", "owner-tool-t");
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-stop",
            "owner-tool",
            "owner-tool-t",
            "Shell.Execute",
            "running",
            "none"));
        harness.Store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-tool-stop",
            "owner-tool",
            "owner-tool-t",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(
                BackgroundTaskKind.ToolCall,
                "owner-tool",
                "tool-stop",
                null,
                "owner-tool-t")),
            "running",
            null,
            1_000,
            null));
        Success(harness.Wp13.StopBackgroundTask("bg-tool-stop"));
        AssertEqual("cancelled", harness.Store.GetToolCall("tool-stop")?.Status, "ToolCall stop must cancel the tool.");
        AssertTrue(!StringComparer.Ordinal.Equals(harness.Store.GetRun("owner-tool")?.Status, "cancelled"),
            "ToolCall stop must not cancel the owner Run.");
    }

    private static void ForgedBackgroundExecutionCannotBeStopped()
    {
        var harness = Harness();
        SeedRunningTask(harness, "owner-a-forge", "owner-a-forge-t");
        SeedDispatchedTask(harness, "owner-b-forge", "owner-b-forge-t");
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-b",
            "owner-b-forge",
            "owner-b-forge-t",
            "Shell.Execute",
            "running",
            "none"));
        harness.Store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-forged",
            "owner-a-forge",
            "owner-a-forge-t",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(
                BackgroundTaskKind.ToolCall,
                "owner-b-forge",
                "tool-b",
                null,
                "owner-b-forge-t")),
            "running",
            null,
            1_000,
            null));
        AssertEqual(RuntimeError.TaskOwnershipDenied,
            harness.Wp13.StopBackgroundTask("bg-forged").Failure?.Code,
            "Forged execution ref must not stop unrelated work.");
        AssertEqual("running", harness.Store.GetToolCall("tool-b")?.Status, "Unrelated ToolCall must survive.");
        AssertTrue(!StringComparer.Ordinal.Equals(harness.Store.GetRun("owner-b-forge")?.Status, "cancelled"),
            "Forged stop must not cancel the foreign owner Run.");
    }

    private static void SpecialistUpdateIdentityMustMatch()
    {
        var harness = Harness();
        var listed = Success(harness.Wp13.ListSpecialists("builtin"));
        var duplicate = Success(harness.Wp13.DuplicateSpecialist(listed.Items[0].ProfileId, "builtin", "user", User()));
        var got = Success(harness.Wp13.GetSpecialist(duplicate.ProfileId, "user"));
        var parsed = SpecialistProfileCanonicalJson.Parse(got.DefinitionJson);
        var mismatched = SpecialistProfileCanonicalJson.Write(parsed with { ProfileId = "stolen.profile" });
        AssertEqual(RuntimeError.SpecialistIdentityMismatch,
            harness.Wp13.UpdateSpecialist(duplicate.ProfileId, "user", mismatched, User()).Failure?.Code,
            "Update target/body ProfileId mismatch must be rejected.");
    }

    private static void SecretMaterialInFreshnessRejectsArtifact()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-secret", "writer");
        SeedDispatchedTask(harness, "run-secret", "task-secret");
        var secret = Artifact("task-secret", ResultArtifactStatus.Complete) with
        {
            Freshness = new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1("password-leak", [], null, null, null, null, [], null, null, []),
                new ResultProvenanceV1(null, "task-secret", null, null, null, null, null, null))
        };
        var submitted = harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "task-secret",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", secret),
                ResultArtifactCanonicalJson.WriteColumn("findings", secret),
                ResultArtifactCanonicalJson.WriteColumn("evidence", secret),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", secret),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", secret),
                ResultArtifactCanonicalJson.WriteColumn("freshness", secret),
                null),
            agent);
        if (submitted.Succeeded)
        {
            var stored = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("task-secret")!);
            AssertTrue(
                !SecretRedaction.ContainsSecretMaterial(ResultArtifactCanonicalJson.Write(stored)),
                "Accepted Result Artifact must not retain secret material after sanitization.");
        }
        else
        {
            AssertEqual(RuntimeError.CompletionFailed, submitted.Failure?.Code,
                "Secret material remaining after sanitization must reject the Artifact.");
        }
    }

    private static void IpcRunACannotSubmitResultForTaskB()
    {
        var harness = Harness();
        var agentA = Agent(harness, "run-ipc-own-a", "writer");
        SeedDispatchedTask(harness, "run-ipc-own-b", "task-ipc-own-b");
        var handler = new Wp13IpcCommandHandler(harness.Wp13, "workspace-13");
        var payload = JsonSerializer.SerializeToElement(
            new SubmitResultArtifactRequest("task-ipc-own-b", "complete", "{}", "{}", "{}", "{}", "{}", "{}", null),
            IpcJsonContext.Default.SubmitResultArtifactRequest);
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
            IpcClientKind.Worker,
            "c-own",
            null,
            agentA,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            IpcSemanticTypes.SubmitResultArtifact,
            payload,
            CancellationToken.None)).GetAwaiter().GetResult();
        AssertTrue(result is not null, "IPC submit must respond.");
        var error = IpcJson.Deserialize(result!.ResponseUtf8, IpcJsonContext.Default.ErrorEnvelope);
        AssertEqual(IpcErrorCodes.TaskOwnershipDenied, error.Payload.Code,
            "Envelope Run A plus payload Task B must be denied at IPC.");
    }

    private static void RequiredDependencyIgnoresProvisionalProducerResult()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-prov-r1", "writer");
        SeedDispatchedTask(harness, "run-prov-r1", "prod-r1");
        Success(harness.Scheduler.CreateTask("run-prov-r1", "consume", 1, null, "cons-r1"));
        SubmitComplete(harness, agent, "prod-r1");
        var created = Success(harness.Wp13.CreateResultDependency("cons-r1", "prod-r1", "required"));
        AssertEqual("missing", created.Status, "REQUIRED must not be CURRENT on a Running producer Result.");
        AssertEqual("blocked", harness.Store.GetTask("cons-r1")?.Status, "Provisional REQUIRED must block dispatch.");
        Success(harness.Wp13.RequestTaskCompletion("prod-r1", agent));
        Success(harness.Wp13.RefreshResultDependencyStatus("prod-r1", "cons-r1"));
        AssertEqual("current", harness.Store.GetDependency(created.DependencyId)?.Status,
            "REQUIRED becomes CURRENT only after formal completion.");
    }

    private static void ConsumerStalesWhenProducerFinalResultChanges()
    {
        var clock = new MutableClock(1_000);
        var harness = Harness(clock: clock);
        var agent = Agent(harness, "run-r1r2", "writer");
        SeedDispatchedTask(harness, "run-r1r2", "prod-final");
        SeedDispatchedTask(harness, "run-r1r2", "cons-final");
        SubmitComplete(harness, agent, "prod-final");
        var r1 = harness.Store.GetLatestResultArtifact("prod-final")!.ResultArtifactId;
        Success(harness.Wp13.CreateResultDependency("cons-final", "prod-final", "required"));
        var againstR1 = Artifact("cons-final", ResultArtifactStatus.Complete) with
        {
            Freshness = new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, [r1]),
                new ResultProvenanceV1(null, "cons-final", null, null, null, null, null, null))
        };
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "cons-final",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", againstR1),
                ResultArtifactCanonicalJson.WriteColumn("findings", againstR1),
                ResultArtifactCanonicalJson.WriteColumn("evidence", againstR1),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", againstR1),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", againstR1),
                ResultArtifactCanonicalJson.WriteColumn("freshness", againstR1),
                null),
            agent));
        clock.UnixMs = 2_000;
        var r2 = Artifact("prod-final", ResultArtifactStatus.Complete);
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "prod-final",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", r2),
                ResultArtifactCanonicalJson.WriteColumn("findings", r2),
                ResultArtifactCanonicalJson.WriteColumn("evidence", r2),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", r2),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", r2),
                ResultArtifactCanonicalJson.WriteColumn("freshness", r2),
                null),
            agent));
        Success(harness.Wp13.RequestTaskCompletion("prod-final", agent));
        var consumer = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("cons-final")!);
        AssertTrue(consumer.Freshness.State != ResultFreshnessState.Current,
            "Consumer Result citing R1 must not remain CURRENT after producer freezes R2.");
        AssertEqual(RuntimeError.CompletionFailed,
            harness.Wp13.RequestTaskCompletion("cons-final", agent).Failure?.Code,
            "Consumer must not formally complete against a stale required upstream.");
    }

    private static void SameRunEvidenceIsNotOwnedWithoutReference()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-ev-own", "writer");
        SeedDispatchedTask(harness, "run-ev-own", "task-ev-a");
        SeedDispatchedTask(harness, "run-ev-own", "task-ev-b");
        harness.Store.InsertEvidence(new EvidenceRecord(
            "ev-b-only",
            "run-ev-own",
            "task-ev-b",
            "narrative",
            "obj-b",
            "digest-b",
            "{}",
            false,
            1_000));
        var claimed = Artifact("task-ev-a", ResultArtifactStatus.Complete) with { EvidenceIds = ["ev-b-only"] };
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "task-ev-a",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", claimed),
                ResultArtifactCanonicalJson.WriteColumn("findings", claimed),
                ResultArtifactCanonicalJson.WriteColumn("evidence", claimed),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", claimed),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", claimed),
                ResultArtifactCanonicalJson.WriteColumn("freshness", claimed),
                null),
            agent));
        var stored = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("task-ev-a")!);
        AssertEqual(ResultFreshnessState.NeedsRevalidation, stored.Freshness.State,
            "Same-Run evidence from another Task must not be implicitly CURRENT.");
    }

    private static void NewRunAfterPolicyChangeUsesNewPolicy()
    {
        var clock = new MutableClock(1_000);
        var harness = Harness(clock: clock);
        var user = User();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), user));
        SeedRunningTask(harness, "run-old-auto", "task-old-auto");
        harness.Store.UpdateTaskStatus("task-old-auto", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 1_000);
        clock.UnixMs = 2_000;
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "author_confirmed_required", "ask", null), user));
        var runA = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, "task-old-auto"));
        AssertEqual("agent_delegated", runA.NarrativeAuthority, "In-flight Run A must keep AUTO until its safe checkpoint.");
        clock.UnixMs = 3_000;
        SeedRunningTask(harness, "run-new-manual", "task-new-manual");
        var runB = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, "task-new-manual"));
        AssertEqual("author_confirmed_required", runB.NarrativeAuthority, "Run B created after AUTO→MANUAL must start MANUAL.");
        Success(harness.Scheduler.PersistCheckpoint(
            "run-old-auto",
            "task-old-auto",
            1,
            CanonicalJson.WriteCheckpoint(CheckpointV1.Create(
                "safe",
                "d",
                "{}",
                "{}",
                "task",
                [],
                [],
                [],
                [],
                [],
                [],
                null,
                null,
                null,
                null)),
            "{}"));
        var runAAfter = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, "task-old-auto"));
        AssertEqual("author_confirmed_required", runAAfter.NarrativeAuthority, "Run A must switch to MANUAL after its safe checkpoint.");
        clock.UnixMs = 4_000;
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), user));
        clock.UnixMs = 4_001;
        SeedRunningTask(harness, "run-new-auto", "task-new-auto");
        var inverse = Success(harness.Wp13.GetEffectiveOversight(ProjectId.ToString("D"), null, "task-new-auto"));
        AssertEqual("agent_delegated", inverse.NarrativeAuthority, "Run created after MANUAL→AUTO must start AUTO.");
    }

    private static void PendingReevaluationHandlesAllOutcomes()
    {
        ApprovalSafetyFacts facts = new(false, false, false, false, false, false);
        var harness = Harness(pending: new ProductionPendingApprovalSafetyEvaluator((_, _) => facts));
        void SeedPending(string id) =>
            harness.Store.InsertApproval(new DurableApprovalRecord(
                id, "run-reeval-all", "task-reeval-all", ApprovalKindCodec.RuntimeGrill,
                ApprovalStatusCodec.ToDurableValue(ApprovalStatus.Pending), "digest", null, null, 1_000));

        SeedPending("pending-no-trust");
        facts = new ApprovalSafetyFacts(true, false, false, true, true, true);
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), User()));
        AssertEqual("denied", harness.Store.GetApproval("pending-no-trust")?.Status, "No Project Trust must not auto-approve.");

        SeedPending("pending-delegated");
        facts = new ApprovalSafetyFacts(true, true, false, true, true, true);
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-reeval-all", "agent_delegated", "auto_approve_scoped", null), User()));
        AssertEqual("resolved", harness.Store.GetApproval("pending-delegated")?.Status,
            "Injected trust + capability + AUTO must approve delegated.");

        SeedPending("pending-hard");
        facts = new ApprovalSafetyFacts(true, true, true, true, true, true);
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-reeval-all", "agent_delegated", "auto_approve_scoped", null), User()));
        AssertEqual("denied", harness.Store.GetApproval("pending-hard")?.Status, "HardDeny must deny.");

        SeedPending("pending-replan");
        facts = new ApprovalSafetyFacts(true, true, false, false, false, true);
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-reeval-all", "agent_delegated", "auto_approve_scoped", null), User()));
        AssertEqual("denied", harness.Store.GetApproval("pending-replan")?.Status, "Invalid plan/gate must replan, not approve.");
        AssertEqual("core:replan_required", harness.Store.GetApproval("pending-replan")?.DecidedBy, "ReplanRequired must be distinct.");

        SeedPending("pending-stale");
        facts = new ApprovalSafetyFacts(true, true, false, true, true, false);
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "task", "task-reeval-all", "agent_delegated", "auto_approve_scoped", null), User()));
        AssertEqual("pending", harness.Store.GetApproval("pending-stale")?.Status, "Stale inputs must remain pending.");
    }

    private static void GrillResumeAnchorsToExactCheckpoint()
    {
        var harness = Harness();
        var user = User();
        SeedDispatchedTask(harness, "run-grill-cp", "task-grill-cp");
        var producer = "prod-grill-cp";
        SeedDispatchedTask(harness, "run-grill-cp", producer);
        var agent = Agent(harness, "run-grill-cp", "writer");
        SubmitComplete(harness, agent, producer);
        Success(harness.Wp13.RequestTaskCompletion(producer, agent));
        Success(harness.Wp13.CreateResultDependency("task-grill-cp", producer, "required"));
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill-cp",
            "task-grill-cp",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "grill-cp-base"));
        var cp1 = harness.Store.CheckpointsForRun("run-grill-cp")
            .Last(item => item.PayloadJson.Contains(paused.ApprovalId, StringComparison.Ordinal)).CheckpointId;
        AssertTrue(!string.IsNullOrWhiteSpace(cp1), "Grill pause must persist an exact checkpoint.");
        var r2 = Artifact(producer, ResultArtifactStatus.Complete);
        harness.Store.ReplaceResultArtifact(ResultArtifactCanonicalJson.ToDurable(
            ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact(producer)!) with
            {
                Freshness = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact(producer)!).Freshness with
                {
                    State = ResultFreshnessState.Stale
                }
            }));
        Success(harness.Wp13.RefreshResultDependencyStatus(producer, "task-grill-cp"));
        var changed = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null), user));
        AssertTrue(!StringComparer.Ordinal.Equals(changed.ResumeDecision, "Continue"),
            "Required input change at CP1 must not Continue.");
    }

    private static void GrillResumeIgnoresLaterUnrelatedCheckpoint()
    {
        var harness = Harness();
        var user = User();
        SeedDispatchedTask(harness, "run-grill-cp2", "task-grill-cp2");
        SeedDispatchedTask(harness, "run-grill-cp2", "prod-grill-cp2");
        var agent = Agent(harness, "run-grill-cp2", "writer");
        SubmitComplete(harness, agent, "prod-grill-cp2");
        Success(harness.Wp13.RequestTaskCompletion("prod-grill-cp2", agent));
        Success(harness.Wp13.CreateResultDependency("task-grill-cp2", "prod-grill-cp2", "required"));
        var first = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill-cp2",
            "task-grill-cp2",
            RuntimeGrillPauseReason.PlanAuthorityAmbiguous,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "grill-cp2-base"));
        var grillCheckpoint = harness.Store.CheckpointsForRun("run-grill-cp2")
            .Last(item => item.PayloadJson.Contains(first.ApprovalId, StringComparison.Ordinal)).CheckpointId;
        Success(harness.Scheduler.CreateTask("run-grill-cp2", "write", 1, null, "task-unrelated-cp"));
        Success(harness.Scheduler.PersistCheckpoint(
            "run-grill-cp2",
            "task-unrelated-cp",
            1,
            CanonicalJson.WriteCheckpoint(CheckpointV1.Create(
                "later",
                "other",
                "{}",
                "{}",
                "task",
                [],
                [],
                [],
                [],
                [],
                [],
                null, null, null, null)),
            "{}"));
        AssertEqual("pending", harness.Store.GetApproval(first.ApprovalId)?.Status,
            "An unrelated later checkpoint must not resolve the grill approval.");
        var resolved = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(first.ApprovalId, "continue", "continue", null), user));
        AssertEqual("Continue", resolved.ResumeDecision,
            "Resolution must stay anchored to the grill checkpoint, not an unrelated later CP.");
        AssertTrue(harness.Store.CheckpointsForRun("run-grill-cp2").Any(item =>
                !StringComparer.Ordinal.Equals(item.CheckpointId, grillCheckpoint)),
            "A later unrelated checkpoint must exist without becoming the resume anchor.");
    }

    private static void GrillUnknownRemainsBlockedAndUnchangedContinues()
    {
        var harness = Harness();
        var user = User();
        SeedDispatchedTask(harness, "run-grill-unk", "task-grill-unk");
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-unk", "run-grill-unk", "task-grill-unk", "Shell.Execute", "running", "unknown"));
        var unk = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill-unk",
            "task-grill-unk",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "unk-base"));
        var blocked = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(unk.ApprovalId, "continue", "continue", null), user));
        AssertEqual("BlockUnknown", blocked.ResumeDecision, "UNKNOWN must remain BlockUnknown.");

        SeedDispatchedTask(harness, "run-grill-cont", "task-grill-cont");
        var unchanged = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill-cont",
            "task-grill-cont",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "cont-base"));
        var continued = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(unchanged.ApprovalId, "continue", "continue", null), user));
        AssertEqual("Continue", continued.ResumeDecision, "Unchanged verified inputs must Continue.");
    }

    private static void ToolCallStopUnavailableLeavesRunning()
    {
        var harness = Harness();
        SeedRunningTask(harness, "owner-tool-unavail", "owner-tool-unavail-t");
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-unavail", "owner-tool-unavail", "owner-tool-unavail-t", "Shell.Execute", "running", "none"));
        harness.Store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-tool-unavail",
            "owner-tool-unavail",
            "owner-tool-unavail-t",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(
                BackgroundTaskKind.ToolCall, "owner-tool-unavail", "tool-unavail", null, "owner-tool-unavail-t")),
            "running",
            null,
            1_000,
            null));
        AssertEqual(RuntimeError.BackgroundStopUnavailable,
            harness.Wp13.StopBackgroundTask("bg-tool-unavail").Failure?.Code,
            "Unavailable cancellation must fail closed.");
        AssertEqual("running", harness.Store.GetToolCall("tool-unavail")?.Status, "ToolCall must remain running.");
        AssertEqual("running", harness.Store.GetBackgroundTask("bg-tool-unavail")?.Status, "Background Task must remain running.");
        AssertTrue(!StringComparer.Ordinal.Equals(harness.Store.GetRun("owner-tool-unavail")?.Status, "cancelled"),
            "Unavailable ToolCall stop must not cancel the owner Run.");
    }

    private static void ToolCallStopConfirmedCancelsExactCallOnly()
    {
        var harness = Harness(toolCancel: new ConfirmingToolCallCancellationPort(id =>
            StringComparer.Ordinal.Equals(id, "tool-exact")));
        SeedRunningTask(harness, "owner-tool-exact", "owner-tool-exact-t");
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-exact", "owner-tool-exact", "owner-tool-exact-t", "Shell.Execute", "running", "none"));
        harness.Store.InsertToolCall(new DurableToolCallRecord(
            "tool-other", "owner-tool-exact", "owner-tool-exact-t", "Shell.Execute", "running", "none"));
        harness.Store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-tool-exact",
            "owner-tool-exact",
            "owner-tool-exact-t",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(
                BackgroundTaskKind.ToolCall, "owner-tool-exact", "tool-exact", null, "owner-tool-exact-t")),
            "running",
            null,
            1_000,
            null));
        Success(harness.Wp13.StopBackgroundTask("bg-tool-exact"));
        AssertEqual("cancelled", harness.Store.GetToolCall("tool-exact")?.Status, "Confirmed cancel must update the exact ToolCall.");
        AssertEqual("running", harness.Store.GetToolCall("tool-other")?.Status, "Another ToolCall in the same Run must survive.");
        AssertTrue(!StringComparer.Ordinal.Equals(harness.Store.GetRun("owner-tool-exact")?.Status, "cancelled"),
            "Exact ToolCall stop must not cancel the owner Run.");
    }

    private static void HistoricalDelegatedProvenanceSurvivesOversightChange()
    {
        var harness = Harness();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), User()));
        var agent = Agent(harness, "run-hist", "pm");
        var store = new RecordingNarrativeStore();
        var sink = new OneShotThrowingDelegatedSink(harness.Wp13);
        var service = new NarrativeChangeService(
            new UnusedBlobStore(),
            store,
            new UnusedSemanticAssessor(),
            new UnusedImpactAnalyzer(),
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            harness.Auth,
            harness.Wp13,
            sink);
        var first = service.Apply(new ApplyNarrativeChangeSetCommand(
            RecordingNarrativeStore.ChangeSetId,
            "idem-hist",
            NarrativeDecisionKind.AgentDelegated,
            "forged-user",
            Principal: agent));
        AssertTrue(first.Succeeded, "COMMIT must succeed even if provenance insert fails.");
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "author_confirmed_required", "ask", null), User()));
        var retry = service.Apply(new ApplyNarrativeChangeSetCommand(
            RecordingNarrativeStore.ChangeSetId,
            "idem-hist",
            NarrativeDecisionKind.AgentDelegated,
            "forged-user",
            Principal: agent));
        AssertTrue(retry.Succeeded, "Recovery must not require current AGENT_DELEGATED.");
        AssertEqual(1, harness.Store.ListDelegatedDecisions().Count, "Exactly one provenance row must be repaired.");
        var row = harness.Store.ListDelegatedDecisions().Single();
        AssertEqual(OversightScopeKind.Project, row.ScopeKind, "Historical provenance must keep the original winning scope.");
        AssertEqual(ProjectId.ToString("D"), row.ScopeId, "Historical provenance must keep the original winning scope id.");
        AssertTrue(row.OversightMode.Contains("agent_delegated", StringComparison.Ordinal),
            "Current MANUAL policy must not rewrite historical axes.");
    }

    private static void DelegatedProvenanceConflictIsVisible()
    {
        var harness = Harness();
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", ProjectId.ToString("D"), "agent_delegated", "auto_approve_scoped", null), User()));
        var policy = harness.Wp13.Resolve(ProjectId.ToString("D"), null, null);
        var first = NarrativeDecisionProvenance.AgentDelegated(
            "decision-conflict",
            "tx-1",
            policy.WinningScope,
            policy.WinningScopeId ?? ProjectId.ToString("D"),
            "agent-a",
            policy,
            null,
            1);
        AssertEqual(DelegatedProvenanceWriteResult.Written, harness.Wp13.Record(first), "First provenance write must succeed.");
        var conflict = first with { DecidedBy = "other-agent" };
        AssertEqual(DelegatedProvenanceWriteResult.Conflict, harness.Wp13.Record(conflict),
            "Conflicting audit data for the same decision id must be visible.");
        AssertEqual(DelegatedProvenanceWriteResult.Equivalent, harness.Wp13.Record(first),
            "Equivalent duplicate must succeed.");
    }

    private static void MissingDependencyEdgesAreEmitted()
    {
        var harness = Harness();
        SeedRunningTask(harness, "run-edge", "prod-missing");
        Success(harness.Scheduler.CreateTask("run-edge", "consume", 1, null, "cons-missing"));
        Success(harness.Wp13.CreateResultDependency("cons-missing", "prod-missing", "required"));
        Success(harness.Scheduler.CreateTask("run-edge", "consume", 1, null, "cons-adv"));
        Success(harness.Wp13.CreateResultDependency("cons-adv", "prod-missing", "advisory"));
        Success(harness.Scheduler.CreateTask("run-edge", "consume", 1, null, "cons-opt"));
        Success(harness.Wp13.CreateResultDependency("cons-opt", "prod-missing", "optional"));
        var required = Success(harness.Wp13.GetTaskHandoff("cons-missing", false)).Edges.Single();
        AssertTrue(string.IsNullOrWhiteSpace(required.ResultArtifactId), "Missing ResultArtifactId must be null.");
        AssertEqual("required", required.DependencyKind, "Kind must be present for a missing edge.");
        AssertEqual("missing", required.DependencyStatus, "Status must be present for a missing edge.");
        AssertEqual(true, required.BlocksDispatch, "Blocked REQUIRED must be visible.");
        AssertEqual("missing", required.FreshnessState, "Missing freshness must be visible.");
        var advisory = Success(harness.Wp13.GetTaskHandoff("cons-adv", false)).Edges.Single();
        AssertEqual("advisory", advisory.DependencyKind, "ADVISORY missing edges must be emitted.");
        AssertTrue(string.IsNullOrWhiteSpace(advisory.ResultArtifactId), "ADVISORY missing Result must be null.");
        var optional = Success(harness.Wp13.GetTaskHandoff("cons-opt", false)).Edges.Single();
        AssertEqual("optional", optional.DependencyKind, "OPTIONAL missing edges must be emitted.");
        AssertTrue(string.IsNullOrWhiteSpace(optional.ResultArtifactId), "OPTIONAL missing Result must be null.");
    }

    private static void TransitiveRequiredFreshnessPropagatesAndRestores()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-trans", "writer");
        SeedDispatchedTask(harness, "run-trans", "task-a");
        SeedDispatchedTask(harness, "run-trans", "task-b");
        Success(harness.Scheduler.CreateTask("run-trans", "write", 1, null, "task-c"));
        Success(harness.Scheduler.CreateTask("run-trans", "write", 1, null, "task-d"));
        SubmitComplete(harness, agent, "task-a");
        Success(harness.Wp13.RequestTaskCompletion("task-a", agent));
        var aId = harness.Store.GetLatestResultArtifact("task-a")!.ResultArtifactId;
        var ab = Success(harness.Wp13.CreateResultDependency("task-b", "task-a", "required"));
        SubmitAgainst(harness, agent, "task-b", [aId]);
        Success(harness.Wp13.RequestTaskCompletion("task-b", agent));
        var bId = harness.Store.GetLatestResultArtifact("task-b")!.ResultArtifactId;
        var bc = Success(harness.Wp13.CreateResultDependency("task-c", "task-b", "required"));
        var ad = Success(harness.Wp13.CreateResultDependency("task-d", "task-a", "required"));
        AssertEqual("current", harness.Store.GetDependency(ab.DependencyId)?.Status, "A→B must start CURRENT.");
        AssertEqual("current", harness.Store.GetDependency(bc.DependencyId)?.Status, "B→C must start CURRENT.");
        AssertEqual("current", harness.Store.GetDependency(ad.DependencyId)?.Status, "A→D must start CURRENT.");
        AssertEqual("ready", harness.Store.GetTask("task-c")?.Status, "C must be ready while required inputs are current.");

        var frozenA = ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("task-a")!);
        harness.Store.ReplaceResultArtifact(ResultArtifactCanonicalJson.ToDurable(
            frozenA with { Freshness = frozenA.Freshness with { State = ResultFreshnessState.Stale } }));
        Success(harness.Wp13.RefreshResultDependencyStatus("task-a", null));
        AssertEqual("stale", harness.Store.GetDependency(ab.DependencyId)?.Status, "A→B must become STALE.");
        AssertEqual(ResultFreshnessState.Stale,
            ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("task-b")!).Freshness.State,
            "B Result must become STALE.");
        AssertEqual(bId, harness.Store.GetLatestResultArtifact("task-b")!.ResultArtifactId,
            "Completed B ResultArtifactId must stay frozen.");
        AssertEqual("stale", harness.Store.GetDependency(bc.DependencyId)?.Status, "B→C must become STALE.");
        AssertEqual("blocked", harness.Store.GetTask("task-c")?.Status, "C must be blocked/non-current.");
        AssertEqual("stale", harness.Store.GetDependency(ad.DependencyId)?.Status, "A→D branch must recompute.");
        AssertEqual("blocked", harness.Store.GetTask("task-d")?.Status, "D must be blocked after A stales.");

        harness.Store.ReplaceResultArtifact(ResultArtifactCanonicalJson.ToDurable(
            frozenA with { Freshness = frozenA.Freshness with { State = ResultFreshnessState.Current } }));
        Success(harness.Wp13.RefreshResultDependencyStatus("task-a", null));
        AssertEqual("current", harness.Store.GetDependency(ab.DependencyId)?.Status, "Restored A must make A→B CURRENT.");
        AssertEqual(ResultFreshnessState.Current,
            ResultArtifactCanonicalJson.FromDurable(harness.Store.GetLatestResultArtifact("task-b")!).Freshness.State,
            "B Result must return to CURRENT after A is restored.");
        AssertEqual("current", harness.Store.GetDependency(bc.DependencyId)?.Status, "B→C must return to CURRENT.");
        AssertEqual("ready", harness.Store.GetTask("task-c")?.Status, "C must become ready after restore.");
        AssertEqual("current", harness.Store.GetDependency(ad.DependencyId)?.Status, "A→D must return to CURRENT.");

        var reloaded = new Wp13RuntimeService(harness.Store, harness.Scheduler, new FixedClock(1_000));
        Success(reloaded.RefreshResultDependencyStatus(null, null));
        AssertEqual("current", harness.Store.GetDependency(ab.DependencyId)?.Status, "Reload must keep A→B CURRENT.");
        AssertEqual("current", harness.Store.GetDependency(bc.DependencyId)?.Status, "Reload must keep B→C CURRENT.");
        AssertEqual("current", harness.Store.GetDependency(ad.DependencyId)?.Status, "Reload must keep A→D CURRENT.");
    }

    private static void GrillPromptProviderModelChangedSinceExactCheckpoint()
    {
        AssertGrillBaselineChange(run => run, "Continue");
        AssertGrillBaselineChange(run => run with { PromptConfigId = "P2" }, "Replan");
        AssertGrillBaselineChange(run => run with { ProviderId = "B" }, "Replan");
        AssertGrillBaselineChange(run => run with { ModelId = "M2" }, "Replan");
        AssertGrillBaselineChange(run => run with { EffectivePromptDigest = "E2" }, "Replan");
        AssertGrillBaselineChange(run => run with { PromptConfigId = "P2" }, "Replan", persistUnrelatedCheckpoint: true);
    }

    private static void AssertGrillBaselineChange(
        Func<DurableRunRecord, DurableRunRecord> mutate,
        string expected,
        bool persistUnrelatedCheckpoint = false)
    {
        var harness = Harness();
        var user = User();
        SeedDispatchedTask(harness, "run-grill-base", "task-grill-base");
        var seeded = harness.Store.GetRun("run-grill-base")!;
        harness.Store.UpdateRun(seeded with
        {
            PromptConfigId = "P1",
            ProviderId = "A",
            ModelId = "M1",
            EffectivePromptDigest = "E1"
        });
        var paused = Success(harness.Wp13.PauseRuntimeGrill(
            "run-grill-base",
            "task-grill-base",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "grill-base"));
        var cp1 = harness.Store.CheckpointsForRun("run-grill-base")
            .Last(item => item.PayloadJson.Contains(paused.ApprovalId, StringComparison.Ordinal));
        AssertTrue(cp1.InputDigestSetJson.Contains("\"promptConfigId\":\"P1\"", StringComparison.Ordinal),
            "Grill pause must retain promptConfigId in the exact checkpoint digest set.");
        if (persistUnrelatedCheckpoint)
        {
            Success(harness.Scheduler.CreateTask("run-grill-base", "write", 1, null, "task-unrelated-base"));
            Success(harness.Scheduler.PersistCheckpoint(
                "run-grill-base",
                "task-unrelated-base",
                1,
                CanonicalJson.WriteCheckpoint(CheckpointV1.Create(
                    "later",
                    "other",
                    "{}",
                    "{}",
                    "task",
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    null,
                    null,
                    null,
                    null)),
                "{}"));
        }

        var live = harness.Store.GetRun("run-grill-base")!;
        harness.Store.UpdateRun(mutate(live));
        var resolved = Success(harness.Wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null), user));
        AssertEqual(expected, resolved.ResumeDecision, "Exact CP1 baseline comparison drifted.");
    }

    private static void SameMillisecondOversightOrderingIsDeterministic()
    {
        var clock = new MutableClock(50_000);
        var harness = Harness(clock: clock);
        var user = User();
        var project = ProjectId.ToString("D");
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", project, "agent_delegated", "auto_approve_scoped", null), user));
        SeedRunningTask(harness, "run-a-same", "task-a-same");
        harness.Store.UpdateTaskStatus("task-a-same", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 50_000);
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", project, "author_confirmed_required", "ask", null), user));
        var runA = Success(harness.Wp13.GetEffectiveOversight(project, null, "task-a-same"));
        AssertEqual("agent_delegated", runA.NarrativeAuthority, "Case A AUTO→MANUAL: Run A remains AUTO.");
        SeedRunningTask(harness, "run-b-same", "task-b-same");
        var runB = Success(harness.Wp13.GetEffectiveOversight(project, null, "task-b-same"));
        AssertEqual("author_confirmed_required", runB.NarrativeAuthority, "Case B AUTO→MANUAL: Run B is MANUAL immediately.");

        SeedRunningTask(harness, "run-c-same", "task-c-same");
        harness.Store.UpdateTaskStatus("task-c-same", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 50_000);
        Success(harness.Wp13.SetOversightOverride(new SetOversightOverrideRequest(
            "project", project, "agent_delegated", "auto_approve_scoped", null), user));
        var runC = Success(harness.Wp13.GetEffectiveOversight(project, null, "task-c-same"));
        AssertEqual("author_confirmed_required", runC.NarrativeAuthority, "Case A MANUAL→AUTO: Run C remains MANUAL.");
        SeedRunningTask(harness, "run-d-same", "task-d-same");
        var runD = Success(harness.Wp13.GetEffectiveOversight(project, null, "task-d-same"));
        AssertEqual("agent_delegated", runD.NarrativeAuthority, "Case B MANUAL→AUTO: Run D is AUTO immediately.");

        var runACreated = harness.Store.GetRun("run-a-same")!.CreatedAtMs;
        var manual = harness.Store.ListOversightOverrides()
            .Where(item => item.NarrativeAuthority == NarrativeDecisionAuthority.AuthorConfirmedRequired)
            .OrderBy(item => item.CreatedAtMs)
            .Last();
        AssertTrue(runACreated < manual.CreatedAtMs,
            "Persisted created_at_ms must distinguish Run A from the same-clock MANUAL override.");
    }

    private static HarnessState Harness(
        ISemanticCompletionEvaluator? semantic = null,
        IPendingApprovalSafetyEvaluator? pending = null,
        IRuntimeGrillSafetyEvaluator? grill = null,
        IToolCallCancellationPort? toolCancel = null,
        ISecurityClock? clock = null)
    {
        var store = new MemoryRuntimeStore();
        var auth = new AllowAuth { Allow = true };
        clock ??= new FixedClock(1_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            auth);
        Success(scheduler.CreateWorkflowRun("wf-temp"));
        Success(scheduler.CreateWorkflowRun("wf-bg"));
        Success(scheduler.CreateWorkflowRun("wf-warn"));
        Success(scheduler.CreateWorkflowRun("wf-stale"));
        Success(scheduler.CreateWorkflowRun("wf-propose"));
        Success(scheduler.CreateWorkflowRun("wf-complete"));
        Success(scheduler.CreateWorkflowRun("wf-missing"));
        Success(scheduler.CreateWorkflowRun("wf-semantic"));
        Success(scheduler.CreateWorkflowRun("wf-semantic-pass"));
        Success(scheduler.CreateWorkflowRun("wf-bypass"));
        Success(scheduler.CreateWorkflowRun("wf-forward"));
        Success(scheduler.CreateWorkflowRun("wf-grill"));
        Success(scheduler.CreateWorkflowRun("wf-grill-agent"));
        Success(scheduler.CreateWorkflowRun("wf-race"));
        Success(scheduler.CreateWorkflowRun("wf-packet"));
        Success(scheduler.CreateWorkflowRun("wf-ipc"));
        var userStore = new MemoryUserSpecialistProfileStore();
        var wp13 = new Wp13RuntimeService(
            store,
            scheduler,
            clock,
            new MemoryApplicationOversightDefaults(),
            userStore,
            SyntheticBuiltInSpecialistCatalog.Instance,
            semantic,
            new MutableSchedulerFaultInjector(),
            pending,
            grill,
            toolCancel);
        scheduler.OversightActivationListener = wp13;
        return new HarnessState(store, scheduler, wp13, auth, userStore);
    }

    private static void SeedRunningTask(HarnessState harness, string runId, string taskId)
    {
        if (harness.Store.GetRun(runId) is null)
        {
            var workflow = harness.Store.LoadSnapshot().WorkflowRuns[0].WorkflowRunId;
            Success(harness.Scheduler.CreateRun(workflow, "writer", null, runId));
        }

        if (harness.Store.GetTask(taskId) is null)
        {
            Success(harness.Scheduler.CreateTask(runId, "write", 1, null, taskId));
        }
    }

    private static void SeedDispatchedTask(HarnessState harness, string runId, string taskId)
    {
        SeedRunningTask(harness, runId, taskId);
        var dispatched = Success(harness.Scheduler.DispatchReadyTask(taskId));
        AssertEqual("dispatched", dispatched.Outcome, "Seeded task must enter the Attempt lifecycle.");
    }

    private static void SubmitComplete(HarnessState harness, CallerPrincipal agent, string taskId)
    {
        var artifact = Artifact(taskId, ResultArtifactStatus.Complete);
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                taskId,
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", artifact),
                ResultArtifactCanonicalJson.WriteColumn("findings", artifact),
                ResultArtifactCanonicalJson.WriteColumn("evidence", artifact),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", artifact),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", artifact),
                ResultArtifactCanonicalJson.WriteColumn("freshness", artifact),
                null),
            agent));
    }

    private static void SubmitAgainst(HarnessState harness, CallerPrincipal agent, string taskId, IReadOnlyList<string> upstream)
    {
        var artifact = Artifact(taskId, ResultArtifactStatus.Complete) with
        {
            Freshness = new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, upstream),
                new ResultProvenanceV1(null, taskId, null, null, null, null, null, null))
        };
        Success(harness.Wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                taskId,
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", artifact),
                ResultArtifactCanonicalJson.WriteColumn("findings", artifact),
                ResultArtifactCanonicalJson.WriteColumn("evidence", artifact),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", artifact),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", artifact),
                ResultArtifactCanonicalJson.WriteColumn("freshness", artifact),
                null),
            agent));
    }

    private static TaskResultArtifactV1 Artifact(string taskId, ResultArtifactStatus status, bool blocking = false) =>
        new(
            Guid.NewGuid().ToString("D"),
            taskId,
            status,
            "done",
            [],
            [],
            null,
            blocking ? [new ResultDiagnosticV1("block", "blocking", "blocked")] : [],
            [],
            [],
            new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, []),
                new ResultProvenanceV1(null, taskId, null, null, null, null, null, null)),
            null,
            1_000);

    private static CallerPrincipal User() => new TrustedNativePrincipalSource("wp13-tests").ResolveUserInteractive();

    private static CallerPrincipal Agent(HarnessState harness, string runId, string role)
    {
        SeedRunningTask(harness, runId, runId + "-seed");
        var sessions = new RunSessionService(new AgentSessionStore(runId, role), new FixedClock(1_000));
        var channel = new AuthenticatedChannelContext("ch-13", AuthenticatedClientKind.Worker, "worker-13", Scope, runId);
        var created = sessions.Create(new LLMW.Writing.Application.Security.CreateRunSessionRequest(runId, channel, DateTimeOffset.FromUnixTimeMilliseconds(1_000).AddMinutes(5)));
        var token = created.Value!.Token.ExportOnceForAuthenticatedTransport();
        return sessions.Resolve(new ResolveRunSessionRequest(runId, token, channel)).Value!;
    }

    private static T Success<T>(RuntimeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success, got {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed record HarnessState(
        MemoryRuntimeStore Store,
        RuntimeSchedulerService Scheduler,
        Wp13RuntimeService Wp13,
        AllowAuth Auth,
        MemoryUserSpecialistProfileStore UserStore);

    private sealed class PassSemanticEvaluator : ISemanticCompletionEvaluator
    {
        public SemanticCompletionOutcome? Evaluate(TaskCompletionContractV1 contract, TaskResultArtifactV1 artifact)
        {
            _ = contract;
            _ = artifact;
            return SemanticCompletionOutcome.Pass;
        }
    }

    private sealed class AllowAuth : IAuthorizationService
    {
        public bool Allow { get; set; } = true;

        public CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request) =>
            new(
                request.Capability,
                principal?.Kind ?? PrincipalKind.AgentRun,
                Allow ? CapabilityDecisionKind.Allowed : CapabilityDecisionKind.Denied,
                [Allow ? CapabilityDecisionReason.Allowed : CapabilityDecisionReason.RoleDenied],
                principal?.Role,
                Allow ? RoleCapabilityLevel.Allowed : RoleCapabilityLevel.Denied,
                SecurityScopeClassification.InScope,
                HardDenied: false);
    }

    private sealed class FixedClock(long unixMs) : ISecurityClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }

    private sealed class MutableClock(long unixMs) : ISecurityClock
    {
        public long UnixMs { get; set; } = unixMs;

        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(UnixMs);
    }

    private sealed class AgentSessionStore(string runId, string role) : IRunSessionStore
    {
        private readonly Dictionary<string, StoredRunSession> byHandle = new(StringComparer.Ordinal);

        public DurableRunIdentity? LoadRun(string id) =>
            StringComparer.Ordinal.Equals(id, runId) ? new DurableRunIdentity(runId, role) : null;

        public StoredRunSession IssueReplacingActive(PersistRunSessionRequest request)
        {
            var stored = new StoredRunSession(
                Guid.NewGuid().ToString("D"),
                request.RunId,
                request.WorkerInstanceId,
                request.ChannelInstanceId,
                request.ProjectScope,
                request.TokenHash,
                request.ExpiresAtMs,
                null,
                request.CreatedAtMs);
            byHandle[stored.HandleId] = stored;
            return stored;
        }

        public StoredRunSession? FindByTokenHash(string tokenHash) =>
            byHandle.Values.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.TokenHash, tokenHash));

        public StoredRunSession? FindByHandleId(string handleId) =>
            byHandle.TryGetValue(handleId, out var session) ? session : null;

        public int RevokeHandle(string handleId, long revokedAtMs)
        {
            _ = handleId;
            _ = revokedAtMs;
            return 0;
        }

        public int RevokeByRun(string id, long revokedAtMs)
        {
            _ = id;
            _ = revokedAtMs;
            return 0;
        }

        public int RevokeByChannelWorker(string channelInstanceId, string workerInstanceId, long revokedAtMs)
        {
            _ = channelInstanceId;
            _ = workerInstanceId;
            _ = revokedAtMs;
            return 0;
        }
    }

    private sealed class NullAcceptanceStore : IChapterAuthorityStore
    {
        public SubmissionContext? LoadSubmissionContext(string chapterId) => null;

        public SubmitChapterDraftResult PersistCandidate(AuthorityTransactionHandle transaction, PersistCandidateRequest request) =>
            throw new InvalidOperationException("unused");

        public CandidateReviewContext? LoadReviewContext(string candidateId) => null;

        public ReviewChapterCandidateResult PersistReview(PersistReviewRequest request) =>
            throw new InvalidOperationException("unused");

        public CandidateAcceptanceContext? LoadAcceptanceContext(string candidateId) => null;

        public CandidateAcceptanceContext PrepareAcceptance(PrepareAcceptanceRequest request) =>
            throw new InvalidOperationException("unused");

        public AcceptChapterCandidateResult CommitAcceptance(
            CandidateAcceptanceContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");

        public AcceptChapterCandidateResult RecoverAcceptance(
            CandidateAcceptanceContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");
    }

    private sealed class TrustedProjectPolicy : ISecurityPolicySource
    {
        public bool ProjectTrusted { get; set; } = true;

        public SecurityPolicySnapshot Resolve(CallerPrincipal principal, Capability capability)
        {
            _ = principal;
            _ = capability;
            return new SecurityPolicySnapshot(
                ProductAllowed: true,
                ToolGranted: true,
                ExtensionGranted: true,
                ProjectTrusted,
                SecurityScopeClassification.InScope,
                HardDeny.None,
                NarrativeAuthorityAvailable: false,
                ExplicitUserTask: false);
        }
    }

    private sealed class UnusedBlobStore : IImmutableBlobStore
    {
        public BlobStageResult Stage(Stream source, string? expectedDigest = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");

        public Stream OpenRead(string digest) => throw new InvalidOperationException("unused");

        public bool Verify(string digest, CancellationToken cancellationToken = default) => false;
    }

    private sealed class UnusedTransactionCoordinator : IAuthorityTransactionCoordinator
    {
        public AuthorityTransactionHandle Begin(string transactionKind, string idempotencyKey) =>
            throw new InvalidOperationException("unused");

        public BlobStageResult StageBlob(
            AuthorityTransactionHandle handle,
            Stream source,
            string? expectedDigest = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");

        public AuthorityTransactionHandle Commit(
            AuthorityTransactionHandle handle,
            AuthorityCommitRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");

        public AuthorityTransactionHandle Recover(string transactionId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");

        public IReadOnlyList<AuthorityRecoveryResult> RecoverIncomplete(CancellationToken cancellationToken = default) => [];

        public AuthorityRecoveryResult Inspect(string transactionId) => throw new InvalidOperationException("unused");
    }

    private sealed class UnusedSemanticAssessor : ISemanticDependencyAssessor
    {
        public SemanticDependencyAssessment Assess(
            NarrativeChangeSetSnapshot changeSet,
            CancellationToken cancellationToken = default)
        {
            _ = changeSet;
            _ = cancellationToken;
            return new SemanticDependencyAssessment(SemanticDependencyFinding.NoEvidenceFound, "{}");
        }
    }

    private sealed class UnusedImpactAnalyzer : INarrativeImpactAnalyzer
    {
        public NarrativeImpactAnalysisResult Analyze(
            NarrativeChangeSetSnapshot changeSet,
            StructuralDependencyAssessment structuralAssessment,
            SemanticDependencyAssessment semanticAssessment,
            CancellationToken cancellationToken = default)
        {
            _ = changeSet;
            _ = structuralAssessment;
            _ = semanticAssessment;
            _ = cancellationToken;
            throw new InvalidOperationException("unused");
        }
    }

    private sealed class RecordingDelegatedSink : IDelegatedDecisionSink
    {
        public DelegatedDecisionRecord? Last { get; private set; }

        public DelegatedProvenanceWriteResult Record(DelegatedDecisionRecord record)
        {
            Last = record;
            return DelegatedProvenanceWriteResult.Written;
        }
    }

    private sealed class OneShotThrowingDelegatedSink(IDelegatedDecisionSink inner) : IDelegatedDecisionSink
    {
        private int calls;

        public DelegatedProvenanceWriteResult Record(DelegatedDecisionRecord record)
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("provenance-fault");
            }

            return inner.Record(record);
        }
    }

    private sealed class RecordingNarrativeStore : INarrativeChangeStore
    {
        public const string ChangeSetId = "018f3e78-1234-7abc-8def-0123456789c1";
        private const string ImpactId = "018f3e78-1234-7abc-8def-0123456789c2";
        private const string TransactionId = "018f3e78-1234-7abc-8def-0123456789c3";
        private string status = "working";

        public string? LastDeciderId { get; private set; }

        public string? LastAuthorizationSnapshotJson { get; private set; }

        public void MarkApplied() => status = "applied";

        public NarrativeStoreResult<NarrativeChangeSetSnapshot> CreateWorkingChangeSet(PersistWorkingChangeSetRequest request)
        {
            _ = request;
            throw new InvalidOperationException("unused");
        }

        public NarrativeChangeSetSnapshot? LoadChangeSet(string changeSetId)
        {
            _ = changeSetId;
            return new(
                ChangeSetId,
                "storyline",
                "storyline-1",
                status,
                "agent",
                "agent-1",
                null,
                null,
                TransactionId,
                ImpactId,
                []);
        }

        public NarrativeChangeFailure? ValidateApplyPreconditions(
            NarrativeChangeSetSnapshot changeSet,
            CancellationToken cancellationToken = default)
        {
            _ = changeSet;
            _ = cancellationToken;
            return null;
        }

        public StructuralDependencyAssessment AssessStructuralDependencies(NarrativeChangeSetSnapshot changeSet)
        {
            _ = changeSet;
            return new([]);
        }

        public NarrativeStoreResult<NarrativeImpactAnalysisRecord> PersistImpactAnalysis(PersistImpactAnalysisRequest request) =>
            NarrativeStoreResults.Success(new NarrativeImpactAnalysisRecord(
                ImpactId,
                request.Status,
                request.AffectedSetJson,
                request.EvidenceJson,
                request.WarningsJson,
                request.Warnings));

        public NarrativeStoreResult<NarrativeApplyStoreResult> Apply(
            NarrativeApplyStoreRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            LastDeciderId = request.DeciderId;
            if (!string.IsNullOrWhiteSpace(request.AuthorizationSnapshotJson))
            {
                LastAuthorizationSnapshotJson = request.AuthorizationSnapshotJson;
            }

            status = "applied";
            return NarrativeStoreResults.Success(new NarrativeApplyStoreResult(
                request.ChangeSetId,
                TransactionId,
                request.ImpactAnalysisId ?? ImpactId,
                AuthorityTransactionState.Complete,
                Existing: false));
        }

        public string? LoadAuthorizationSnapshot(string transactionId)
        {
            _ = transactionId;
            return LastAuthorizationSnapshotJson;
        }
    }

    private sealed class UnusedReviewer : IChapterReviewer
    {
        public ChapterReviewDecision Review(
            CandidateReviewInput candidate,
            Stream candidateContent,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");
    }
}
