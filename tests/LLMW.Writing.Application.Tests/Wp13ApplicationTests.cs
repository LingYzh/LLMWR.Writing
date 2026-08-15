using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
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
        Console.WriteLine("Application WP13 tests passed (23).");
        return 23;
    }

    private static void DeterministicCompletionPassesAndIsIdempotent()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-complete", "writer");
        SeedRunningTask(harness, "run-complete", "task-complete");
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
        SeedRunningTask(harness, "run-missing", "task-missing");
        AssertEqual(RuntimeError.CompletionFailed, harness.Wp13.RequestTaskCompletion("task-missing", agent).Failure?.Code,
            "Missing Result Artifact must fail completion.");
        SeedRunningTask(harness, "run-missing", "task-blocking");
        var artifact = Artifact("task-blocking", ResultArtifactStatus.Complete, blocking: true);
        harness.Store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(artifact));
        AssertEqual(RuntimeError.CompletionFailed, harness.Wp13.RequestTaskCompletion("task-blocking", agent).Failure?.Code,
            "Blocking diagnostics must fail deterministic completion.");
    }

    private static void SemanticReviewRequiredWithoutEvaluator()
    {
        var harness = Harness();
        var agent = Agent(harness, "run-semantic", "writer");
        SeedRunningTask(harness, "run-semantic", "task-semantic");
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
        SeedRunningTask(harness, "run-semantic-pass", "task-semantic-pass");
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
        SeedRunningTask(harness, "run-stale", "producer");
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
        AssertEqual(RuntimeError.GrillAuthorRequired,
            harness.Wp13.ResolveRuntimeGrill(
                new ResolveRuntimeGrillRequest(paused.ApprovalId, "expand", "expand-scope", null),
                agent).Failure?.Code,
            "Scope expansion must not be Agent-resolved via Grill.");
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
        Success(harness.Wp13.ResolveRuntimeGrill(new ResolveRuntimeGrillRequest(first.ApprovalId, "continue", "continue", null), User()));
        AssertEqual(RuntimeError.GrillAlreadyResolved,
            harness.Wp13.ResolveRuntimeGrill(new ResolveRuntimeGrillRequest(first.ApprovalId, "replan", "replan", null), User()).Failure?.Code,
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

    private static HarnessState Harness(ISemanticCompletionEvaluator? semantic = null)
    {
        var store = new MemoryRuntimeStore();
        var auth = new AllowAuth { Allow = true };
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            new FixedClock(1_000),
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
            new FixedClock(1_000),
            new MemoryApplicationOversightDefaults(),
            userStore,
            SyntheticBuiltInSpecialistCatalog.Instance,
            semantic,
            new MutableSchedulerFaultInjector());
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

    private sealed class UnusedReviewer : IChapterReviewer
    {
        public ChapterReviewDecision Review(
            CandidateReviewInput candidate,
            Stream candidateContent,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unused");
    }
}
