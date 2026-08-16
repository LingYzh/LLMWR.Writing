using System.Text.Json;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.ChapterAuthority;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Authority;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static readonly List<string> Wp13PassedTests = [];

    private static void RunWp13Tests()
    {
        RunWp13(nameof(SqliteWp13SemanticsReloadWithoutMigration), SqliteWp13SemanticsReloadWithoutMigration);
        RunWp13(nameof(OpenProjectStillDoesNotGrantProjectTrust), OpenProjectStillDoesNotGrantProjectTrust);
        RunWp13(nameof(NoDirectSpecialistMessageCommandsExist), NoDirectSpecialistMessageCommandsExist);
        RunWp13(nameof(SqliteWorkflowStorylineReloads), SqliteWorkflowStorylineReloads);
        RunWp13(nameof(SqliteRunACannotSubmitResultForTaskB), SqliteRunACannotSubmitResultForTaskB);
        RunWp13(nameof(SqliteResultCompletionAndRequiredDependency), SqliteResultCompletionAndRequiredDependency);
        RunWp13(nameof(SqliteOversightScopesReload), SqliteOversightScopesReload);
        RunWp13(nameof(SqliteGrillReloadThenResolve), SqliteGrillReloadThenResolve);
        RunWp13(nameof(SqliteToolCallStopDoesNotCancelOwnerRun), SqliteToolCallStopDoesNotCancelOwnerRun);
        RunWp13(nameof(SqliteDelegatedAuthorityCommitAndProvenanceRetry), SqliteDelegatedAuthorityCommitAndProvenanceRetry);
        RunWp13(nameof(SqliteProvisionalResultDoesNotSatisfyRequired), SqliteProvisionalResultDoesNotSatisfyRequired);
        RunWp13(nameof(SqliteNewRunAfterAutoToManualUsesNewPolicy), SqliteNewRunAfterAutoToManualUsesNewPolicy);
        RunWp13(nameof(SqliteGrillResumeAnchorsToExactCheckpoint), SqliteGrillResumeAnchorsToExactCheckpoint);
        RunWp13(nameof(SqliteToolCallCancellationUnavailableVsConfirmed), SqliteToolCallCancellationUnavailableVsConfirmed);
        RunWp13(nameof(SqliteHistoricalDelegatedProvenanceAfterOversightChange), SqliteHistoricalDelegatedProvenanceAfterOversightChange);
        RunWp13(nameof(SqliteTransitiveRequiredFreshnessPropagates), SqliteTransitiveRequiredFreshnessPropagates);
        RunWp13(nameof(SqliteGrillPromptProviderModelChangedSinceCheckpoint), SqliteGrillPromptProviderModelChangedSinceCheckpoint);
        RunWp13(nameof(SqliteSameMillisecondOversightOrderingReloads), SqliteSameMillisecondOversightOrderingReloads);
        RunWp13(nameof(SqliteDelegatedProvenanceIgnoresWarningsAckDigest), SqliteDelegatedProvenanceIgnoresWarningsAckDigest);
        Console.WriteLine($"WP13 integration tests passed ({Wp13PassedTests.Count}).");
        foreach (var test in Wp13PassedTests)
        {
            Console.WriteLine($"PASS {test}");
        }
    }

    private static void RunWp13(string name, Action test)
    {
        test();
        Wp13PassedTests.Add(name);
    }

    private static void SqliteWp13SemanticsReloadWithoutMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(10_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-int"));
        var run = Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-int"));
        Success(scheduler.CreateTask(run.RunId, "write", 1, null, "task-int"));
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", "proj-int", "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        Success(scheduler.DispatchReadyTask("task-int"));
        var paused = Success(wp13.PauseRuntimeGrill(
            run.RunId,
            "task-int",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "int-base"));
        store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(new TaskResultArtifactV1(
            Guid.NewGuid().ToString("D"),
            "task-int",
            ResultArtifactStatus.Complete,
            "done",
            [],
            [],
            null,
            [],
            [],
            [],
            new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, []),
                new ResultProvenanceV1(run.RunId, "task-int", null, null, null, null, null, null)),
            null,
            10_000)));

        var reloaded = new SqliteRuntimeStore(databasePath);
        var reloadedWp13 = new Wp13RuntimeService(
            reloaded,
            new RuntimeSchedulerService(
                reloaded,
                new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                clock,
                new FakeRunWorkerSupervisor(),
                new AllowIntegrationAuth { Allow = true }),
            clock);
        AssertWp13Equal("pending", reloaded.GetApproval(paused.ApprovalId)?.Status, "Grill pause must survive Core/Runtime restart.");
        AssertWp13Equal("complete", reloaded.GetLatestResultArtifact("task-int")?.Status, "Result Artifact must survive reload.");
        AssertWp13Equal("agent_delegated", Success(reloadedWp13.GetEffectiveOversight("proj-int", null, null)).NarrativeAuthority,
            "Oversight override must survive reload.");
        var resolved = Success(reloadedWp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
            Wp09UserPrincipal));
        AssertWp13Equal("resolved", resolved.Status, "Reloaded Grill request must resolve from durable Checkpoint state.");
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(databasePath);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        AssertWp13Equal(1L, (long)(version.ExecuteScalar() ?? 0L), "user_version must remain 1.");
        using var migrations = connection.CreateCommand();
        migrations.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        AssertWp13Equal(1L, (long)(migrations.ExecuteScalar() ?? 0L), "schema_migrations must remain 1.");
    }

    private static void OpenProjectStillDoesNotGrantProjectTrust()
    {
        var decision = new CoreAuthorizationService().Authorize(
            Wp09UserPrincipal,
            new AuthorizationRequest(Capability.AuthorityAccept));
        AssertWp13Equal(CapabilityDecisionKind.Denied, decision.Decision, "OpenProject-equivalent FailClosed policy must not grant trust.");
        AssertTrue(decision.Reasons.Contains(CapabilityDecisionReason.ProductDenied) ||
                   decision.Reasons.Contains(CapabilityDecisionReason.TrustRequired),
            "Fail-closed production policy must still deny Authority.Accept without an explicit trust source.");
    }

    private static void NoDirectSpecialistMessageCommandsExist()
    {
        AssertTrue(!IpcSemanticTypes.IsKnown("sendMessageToSpecialist"), "sendMessageToSpecialist must not exist.");
        AssertTrue(!IpcSemanticTypes.IsKnown("appendSpecialistInstruction"), "appendSpecialistInstruction must not exist.");
        AssertTrue(!IpcSemanticTypes.IsKnown("forceSpecialistComplete"), "forceSpecialistComplete must not exist.");
        var handler = new Wp13IpcCommandHandler(
            new Wp13RuntimeService(
                new MemoryRuntimeStore(),
                new RuntimeSchedulerService(
                    new MemoryRuntimeStore(),
                    new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                    new IntegrationClock(1),
                    new FakeRunWorkerSupervisor()),
                new IntegrationClock(1)),
            "workspace-13");
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
            IpcClientKind.Ui,
            "c1",
            null,
            Wp09UserPrincipal,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            "sendMessageToSpecialist",
            JsonDocument.Parse("{}").RootElement,
            CancellationToken.None)).GetAwaiter().GetResult();
        AssertTrue(result is null, "Unknown direct-message semantic type must not be handled.");
    }

    private static void SqliteWorkflowStorylineReloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        const string storylineId = "0198a8a0-0000-7000-8000-0000000000aa";
        using (var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(databasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO objects(object_id,object_type,schema_version,status,created_at_ms,updated_at_ms)
                VALUES($id,'storyline',1,'current',1,1);
                INSERT INTO storylines(storyline_id,workflow_state,updated_at_ms)
                VALUES($id,'active',1);
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id";
            parameter.Value = storylineId;
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        }

        var store = new SqliteRuntimeStore(databasePath);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            new IntegrationClock(11_000),
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var created = Success(scheduler.CreateWorkflowRun(workflowRunId: null, storylineId: storylineId));
        AssertWp13Equal(storylineId, created.StorylineId, "CreateWorkflowRun must persist storyline_id.");
        var reloaded = new SqliteRuntimeStore(databasePath);
        AssertWp13Equal(storylineId, reloaded.GetWorkflowRun(created.WorkflowRunId)?.StorylineId,
            "workflow_runs.storyline_id must survive reload.");
    }

    private static void SqliteRunACannotSubmitResultForTaskB()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(12_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-own"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-a"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-b"));
        Success(scheduler.CreateTask("run-a", "write", 1, null, "task-a"));
        Success(scheduler.CreateTask("run-b", "write", 1, null, "task-b"));
        Success(scheduler.DispatchReadyTask("task-a"));
        Success(scheduler.DispatchReadyTask("task-b"));
        var agentA = CreateSqliteAgent(databasePath, "run-a", "writer");
        AssertWp13Equal(RuntimeError.TaskOwnershipDenied,
            wp13.SubmitResultArtifact(
                new SubmitResultArtifactRequest("task-b", "complete", "{}", "{}", "{}", "{}", "{}", "{}", null),
                agentA).Failure?.Code,
            "Real RunSession for Run A must not submit a Result for Task B.");
    }

    private static void SqliteResultCompletionAndRequiredDependency()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(13_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-flow"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-flow"));
        Success(scheduler.CreateTask("run-flow", "write", 1, null, "prod-flow"));
        Success(scheduler.CreateTask("run-flow", "write", 1, null, "cons-flow"));
        Success(scheduler.DispatchReadyTask("prod-flow"));
        var agent = CreateSqliteAgent(databasePath, "run-flow", "writer");
        var artifact = new TaskResultArtifactV1(
            Guid.NewGuid().ToString("D"),
            "prod-flow",
            ResultArtifactStatus.Complete,
            "done",
            [],
            [],
            null,
            [],
            [],
            [],
            new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, []),
                new ResultProvenanceV1("run-flow", "prod-flow", null, null, null, null, null, null)),
            null,
            13_000);
        Success(wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "prod-flow",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", artifact),
                ResultArtifactCanonicalJson.WriteColumn("findings", artifact),
                ResultArtifactCanonicalJson.WriteColumn("evidence", artifact),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", artifact),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", artifact),
                ResultArtifactCanonicalJson.WriteColumn("freshness", artifact),
                null),
            agent));
        var completed = Success(wp13.RequestTaskCompletion("prod-flow", agent));
        AssertWp13Equal("pass", completed.Outcome, "Dispatched Result completion must pass.");
        var dependency = Success(wp13.CreateResultDependency("cons-flow", "prod-flow", "required"));
        AssertWp13Equal("current", store.GetDependency(dependency.DependencyId)?.Status, "REQUIRED dependency must be current after completion.");
        var handoff = Success(wp13.GetTaskHandoff("cons-flow", false));
        AssertWp13Equal(completed.ResultArtifactId, handoff.ResultArtifactIds.Single(), "Handoff must include the completed Result.");
        AssertTrue(handoff.Edges.Any(item =>
                StringComparer.Ordinal.Equals(item.DependencyKind, "required") &&
                StringComparer.Ordinal.Equals(item.DependencyStatus, "current") &&
                !item.BlocksDispatch),
            "Handoff edges must carry freshness/dependency metadata.");
    }

    private static void SqliteOversightScopesReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(14_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", "proj-scope", "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("task", "task-scope", "author_confirmed_required", "ask", null),
            Wp09UserPrincipal));
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("storyline", "story-scope", "author_confirmed_required", "ask", null),
            Wp09UserPrincipal));
        var reloaded = new Wp13RuntimeService(
            new SqliteRuntimeStore(databasePath),
            scheduler,
            clock);
        AssertWp13Equal("author_confirmed_required",
            Success(reloaded.GetEffectiveOversight("proj-scope", null, "task-scope")).NarrativeAuthority,
            "Task MANUAL must win over Project AUTO after reload.");
        AssertWp13Equal("author_confirmed_required",
            Success(reloaded.GetEffectiveOversight("proj-scope", "story-scope", null)).NarrativeAuthority,
            "Storyline MANUAL must remain Manual after reload.");
    }

    private static void SqliteGrillReloadThenResolve()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(15_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-grill-int"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-grill-int"));
        Success(scheduler.CreateTask("run-grill-int", "write", 1, null, "task-grill-int"));
        Success(scheduler.DispatchReadyTask("task-grill-int"));
        var paused = Success(wp13.PauseRuntimeGrill(
            "run-grill-int",
            "task-grill-int",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "grill-int-base"));
        var reloadedStore = new SqliteRuntimeStore(databasePath);
        var reloaded = new Wp13RuntimeService(
            reloadedStore,
            new RuntimeSchedulerService(
                reloadedStore,
                new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                clock,
                new FakeRunWorkerSupervisor(),
                new AllowIntegrationAuth { Allow = true }),
            clock);
        AssertWp13Equal("pending", reloadedStore.GetApproval(paused.ApprovalId)?.Status, "Durable Grill request must reload.");
        var resolved = Success(reloaded.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
            Wp09UserPrincipal));
        AssertWp13Equal("resolved", resolved.Status, "Valid resolve after reload must succeed.");
    }

    private static void SqliteToolCallStopDoesNotCancelOwnerRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(16_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(
            store,
            scheduler,
            clock,
            toolCancellation: new ConfirmingToolCallCancellationPort(_ => true));
        var workflow = Success(scheduler.CreateWorkflowRun("wf-tool-stop"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-tool-stop"));
        Success(scheduler.CreateTask("run-tool-stop", "write", 1, null, "task-tool-stop"));
        store.InsertToolCall(new DurableToolCallRecord(
            "tool-int",
            "run-tool-stop",
            "task-tool-stop",
            "Shell.Execute",
            "running",
            "none"));
        store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-tool-int",
            "run-tool-stop",
            "task-tool-stop",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(
                BackgroundTaskKind.ToolCall,
                "run-tool-stop",
                "tool-int",
                null,
                "task-tool-stop")),
            "running",
            null,
            16_000,
            null));
        Success(wp13.StopBackgroundTask("bg-tool-int"));
        AssertWp13Equal("cancelled", store.GetToolCall("tool-int")?.Status, "Exact ToolCall stop must cancel the tool.");
        AssertTrue(!StringComparer.Ordinal.Equals(store.GetRun("run-tool-stop")?.Status, "cancelled"),
            "Exact ToolCall stop must not cancel the owner Run.");
    }

    private static void SqliteDelegatedAuthorityCommitAndProvenanceRetry()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        var runtimeStore = new SqliteRuntimeStore(fixture.DatabasePath);
        var clock = new IntegrationClock(17_000);
        var scheduler = new RuntimeSchedulerService(
            runtimeStore,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(runtimeStore, scheduler, clock);
        var sink = new OneShotThrowingDelegatedSink(wp13);
        var authorization = new CoreAuthorizationService(new Wp09TestSecurityPolicySource(), wp13);
        var service = new ChapterAuthorityService(
            fixture.BlobStore,
            fixture.Coordinator,
            fixture.AuthorityStore,
            fixture.Reviewer,
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            authorization,
            wp13,
            sink);
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest(
                "project",
                Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab").ToString("D"),
                "agent_delegated",
                "auto_approve_scoped",
                null),
            Wp09UserPrincipal));
        File.WriteAllText(fixture.DraftPath, "wp13 delegated manuscript");
        var submitted = Success(service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp13-delegated", Principal: Wp09UserPrincipal)));
        Success(service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        var pm = CreateAgentPrincipal(
            fixture.DatabasePath,
            "wp13-pm",
            "pm",
            "worker-wp13-pm",
            "channel-wp13-pm",
            RuntimePermissionMode.AutoApproveScoped);
        var first = Success(service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp13-delegated",
            "forged-user",
            Principal: pm)));
        AssertWp13Equal(AuthorityTransactionState.Complete, first.TransactionState, "AGENT_DELEGATED must reach Authority COMMIT.");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM delegated_decisions;");
        fixture.AssertScalar("AGENT_DELEGATED", "SELECT accepted_by_kind FROM acceptance_records LIMIT 1;");
        var retry = Success(service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp13-delegated",
            "forged-user",
            Principal: pm)));
        AssertWp13Equal(first.AcceptanceId, retry.AcceptanceId, "Retry must not create a second Authority identity.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM delegated_decisions;");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM acceptance_records;");
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accepted_by_id FROM acceptance_records LIMIT 1;";
        var acceptedBy = command.ExecuteScalar()?.ToString();
        AssertTrue(!StringComparer.Ordinal.Equals(acceptedBy, "forged-user"), "Caller AcceptedById must not become audit identity.");
    }

    private static void SqliteProvisionalResultDoesNotSatisfyRequired()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(18_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-prov"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-prov"));
        Success(scheduler.CreateTask("run-prov", "write", 1, null, "prod-prov"));
        Success(scheduler.CreateTask("run-prov", "write", 1, null, "cons-prov"));
        Success(scheduler.DispatchReadyTask("prod-prov"));
        Success(scheduler.DispatchReadyTask("cons-prov"));
        var agent = CreateSqliteAgent(databasePath, "run-prov", "writer");
        var r1 = SubmitSqliteComplete(wp13, agent, "prod-prov", 18_000);
        var dependency = Success(wp13.CreateResultDependency("cons-prov", "prod-prov", "required"));
        AssertWp13Equal("missing", store.GetDependency(dependency.DependencyId)?.Status,
            "REQUIRED must ignore a provisional Running producer Result.");
        var consumer = ArtifactAgainst("cons-prov", [r1.ResultArtifactId], 18_000);
        Success(wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "cons-prov",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", consumer),
                ResultArtifactCanonicalJson.WriteColumn("findings", consumer),
                ResultArtifactCanonicalJson.WriteColumn("evidence", consumer),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", consumer),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", consumer),
                ResultArtifactCanonicalJson.WriteColumn("freshness", consumer),
                null),
            agent));
        clock.UnixMs = 18_001;
        var r2 = SubmitSqliteComplete(wp13, agent, "prod-prov", 18_001);
        Success(wp13.RequestTaskCompletion("prod-prov", agent));
        AssertWp13Equal("current", store.GetDependency(dependency.DependencyId)?.Status,
            "REQUIRED becomes CURRENT only after formal completion.");
        AssertWp13Equal(r2.ResultArtifactId, store.GetDependency(dependency.DependencyId)?.ResultArtifactId,
            "REQUIRED must pin the frozen completion Result, not R1.");
        var restamped = ResultArtifactCanonicalJson.FromDurable(store.GetLatestResultArtifact("cons-prov")!);
        AssertTrue(restamped.Freshness.State != ResultFreshnessState.Current,
            "Consumer produced against R1 must not remain CURRENT after frozen R2.");
    }

    private static void SqliteNewRunAfterAutoToManualUsesNewPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(19_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var projectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab").ToString("D");
        var workflow = Success(scheduler.CreateWorkflowRun("wf-policy"));
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-old-auto"));
        Success(scheduler.CreateTask("run-old-auto", "write", 1, null, "task-old-auto"));
        store.UpdateTaskStatus("task-old-auto", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 19_000);
        clock.UnixMs = 20_000;
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "author_confirmed_required", "ask", null),
            Wp09UserPrincipal));
        var runA = Success(wp13.GetEffectiveOversight(projectId, null, "task-old-auto"));
        AssertWp13Equal("agent_delegated", runA.NarrativeAuthority, "In-flight Run A must keep AUTO until a safe checkpoint.");
        clock.UnixMs = 21_000;
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-new-manual"));
        Success(scheduler.CreateTask("run-new-manual", "write", 1, null, "task-new-manual"));
        var runB = Success(wp13.GetEffectiveOversight(projectId, null, "task-new-manual"));
        AssertWp13Equal("author_confirmed_required", runB.NarrativeAuthority, "Run B created after AUTO→MANUAL must start MANUAL.");
        clock.UnixMs = 22_000;
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        clock.UnixMs = 22_001;
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-new-auto"));
        Success(scheduler.CreateTask("run-new-auto", "write", 1, null, "task-new-auto"));
        var inverse = Success(wp13.GetEffectiveOversight(projectId, null, "task-new-auto"));
        AssertWp13Equal("agent_delegated", inverse.NarrativeAuthority, "Run created after MANUAL→AUTO must start AUTO.");
    }

    private static void SqliteGrillResumeAnchorsToExactCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(23_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-grill-cp"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-grill-cp"));
        Success(scheduler.CreateTask("run-grill-cp", "write", 1, null, "task-grill-cp"));
        Success(scheduler.CreateTask("run-grill-cp", "write", 1, null, "prod-grill-cp"));
        Success(scheduler.DispatchReadyTask("task-grill-cp"));
        Success(scheduler.DispatchReadyTask("prod-grill-cp"));
        var agent = CreateSqliteAgent(databasePath, "run-grill-cp", "writer");
        SubmitSqliteComplete(wp13, agent, "prod-grill-cp", 23_000);
        Success(wp13.RequestTaskCompletion("prod-grill-cp", agent));
        Success(wp13.CreateResultDependency("task-grill-cp", "prod-grill-cp", "required"));
        var paused = Success(wp13.PauseRuntimeGrill(
            "run-grill-cp",
            "task-grill-cp",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "int-grill-cp"));
        var latest = store.GetLatestResultArtifact("prod-grill-cp")!;
        var parsed = ResultArtifactCanonicalJson.FromDurable(latest);
        store.ReplaceResultArtifact(ResultArtifactCanonicalJson.ToDurable(parsed with
        {
            Freshness = parsed.Freshness with { State = ResultFreshnessState.Stale }
        }));
        Success(wp13.RefreshResultDependencyStatus("prod-grill-cp", "task-grill-cp"));
        var changed = Success(wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
            Wp09UserPrincipal));
        AssertTrue(!StringComparer.Ordinal.Equals(changed.ResumeDecision, "Continue"),
            "Required input change at the grill checkpoint must not Continue.");
    }

    private static void SqliteToolCallCancellationUnavailableVsConfirmed()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(24_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var unavailable = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-tool-unavail"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-tool-unavail"));
        Success(scheduler.CreateTask("run-tool-unavail", "write", 1, null, "task-tool-unavail"));
        store.InsertToolCall(new DurableToolCallRecord(
            "tool-unavail", "run-tool-unavail", "task-tool-unavail", "Shell.Execute", "running", "none"));
        store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-tool-unavail",
            "run-tool-unavail",
            "task-tool-unavail",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(
                BackgroundTaskKind.ToolCall, "run-tool-unavail", "tool-unavail", null, "task-tool-unavail")),
            "running",
            null,
            24_000,
            null));
        AssertWp13Equal(RuntimeError.BackgroundStopUnavailable,
            unavailable.StopBackgroundTask("bg-tool-unavail").Failure?.Code,
            "Unavailable cancellation must fail closed.");
        AssertWp13Equal("running", store.GetToolCall("tool-unavail")?.Status, "ToolCall must remain running.");
        AssertWp13Equal("running", store.GetBackgroundTask("bg-tool-unavail")?.Status, "Background Task must remain running.");

        var confirmed = new Wp13RuntimeService(
            store,
            scheduler,
            clock,
            toolCancellation: new ConfirmingToolCallCancellationPort(id =>
                StringComparer.Ordinal.Equals(id, "tool-unavail")));
        store.InsertToolCall(new DurableToolCallRecord(
            "tool-other-int", "run-tool-unavail", "task-tool-unavail", "Shell.Execute", "running", "none"));
        Success(confirmed.StopBackgroundTask("bg-tool-unavail"));
        AssertWp13Equal("cancelled", store.GetToolCall("tool-unavail")?.Status, "Confirmed cancel must update the exact ToolCall.");
        AssertWp13Equal("running", store.GetToolCall("tool-other-int")?.Status, "Another ToolCall in the same Run must survive.");
    }

    private static void SqliteHistoricalDelegatedProvenanceAfterOversightChange()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        var runtimeStore = new SqliteRuntimeStore(fixture.DatabasePath);
        var clock = new IntegrationClock(25_000);
        var scheduler = new RuntimeSchedulerService(
            runtimeStore,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(runtimeStore, scheduler, clock);
        var sink = new OneShotThrowingDelegatedSink(wp13);
        var authorization = new CoreAuthorizationService(new Wp09TestSecurityPolicySource(), wp13);
        var service = new ChapterAuthorityService(
            fixture.BlobStore,
            fixture.Coordinator,
            fixture.AuthorityStore,
            fixture.Reviewer,
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            authorization,
            wp13,
            sink);
        var projectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab").ToString("D");
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        File.WriteAllText(fixture.DraftPath, "wp13 historical manuscript");
        var submitted = Success(service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp13-hist", Principal: Wp09UserPrincipal)));
        Success(service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        var pm = CreateAgentPrincipal(
            fixture.DatabasePath,
            "wp13-hist-pm",
            "pm",
            "worker-wp13-hist-pm",
            "channel-wp13-hist-pm",
            RuntimePermissionMode.AutoApproveScoped);
        var first = Success(service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp13-hist",
            "forged-user",
            Principal: pm)));
        AssertWp13Equal(AuthorityTransactionState.Complete, first.TransactionState, "AGENT_DELEGATED COMMIT must succeed.");
        fixture.AssertScalar(0L, "SELECT COUNT(*) FROM delegated_decisions;");
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "author_confirmed_required", "ask", null),
            Wp09UserPrincipal));
        var retry = Success(service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp13-hist",
            "forged-user",
            Principal: pm)));
        AssertWp13Equal(first.AcceptanceId, retry.AcceptanceId, "Recovery must keep the committed Authority identity.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM delegated_decisions;");
        fixture.AssertScalar("project", "SELECT scope_kind FROM delegated_decisions LIMIT 1;");
        fixture.AssertScalar(projectId, "SELECT scope_id FROM delegated_decisions LIMIT 1;");
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(fixture.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT oversight_mode FROM delegated_decisions LIMIT 1;";
        var mode = command.ExecuteScalar()?.ToString() ?? "";
        AssertTrue(mode.Contains("agent_delegated", StringComparison.Ordinal),
            "Current MANUAL policy must not rewrite historical provenance axes.");
    }

    private static void SqliteTransitiveRequiredFreshnessPropagates()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(30_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-trans"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-trans"));
        Success(scheduler.CreateTask("run-trans", "write", 1, null, "task-a"));
        Success(scheduler.CreateTask("run-trans", "write", 1, null, "task-b"));
        Success(scheduler.CreateTask("run-trans", "write", 1, null, "task-c"));
        Success(scheduler.DispatchReadyTask("task-a"));
        Success(scheduler.DispatchReadyTask("task-b"));
        var agent = CreateSqliteAgent(databasePath, "run-trans", "writer");
        var a = SubmitSqliteComplete(wp13, agent, "task-a", 30_000);
        Success(wp13.RequestTaskCompletion("task-a", agent));
        var ab = Success(wp13.CreateResultDependency("task-b", "task-a", "required"));
        var againstA = ArtifactAgainst("task-b", [a.ResultArtifactId], 30_000);
        Success(wp13.SubmitResultArtifact(
            new SubmitResultArtifactRequest(
                "task-b",
                "complete",
                ResultArtifactCanonicalJson.WriteColumn("conclusion", againstA),
                ResultArtifactCanonicalJson.WriteColumn("findings", againstA),
                ResultArtifactCanonicalJson.WriteColumn("evidence", againstA),
                ResultArtifactCanonicalJson.WriteColumn("uncertainty", againstA),
                ResultArtifactCanonicalJson.WriteColumn("diagnostics", againstA),
                ResultArtifactCanonicalJson.WriteColumn("freshness", againstA),
                null),
            agent));
        Success(wp13.RequestTaskCompletion("task-b", agent));
        var bId = store.GetLatestResultArtifact("task-b")!.ResultArtifactId;
        var bc = Success(wp13.CreateResultDependency("task-c", "task-b", "required"));
        AssertWp13Equal("current", store.GetDependency(ab.DependencyId)?.Status, "A→B must start CURRENT.");
        AssertWp13Equal("current", store.GetDependency(bc.DependencyId)?.Status, "B→C must start CURRENT.");
        AssertWp13Equal("ready", store.GetTask("task-c")?.Status, "C must be ready while B is current.");

        var frozenA = ResultArtifactCanonicalJson.FromDurable(store.GetLatestResultArtifact("task-a")!);
        store.ReplaceResultArtifact(ResultArtifactCanonicalJson.ToDurable(
            frozenA with { Freshness = frozenA.Freshness with { State = ResultFreshnessState.Stale } }));
        Success(wp13.RefreshResultDependencyStatus("task-a", null));
        AssertWp13Equal("stale", store.GetDependency(ab.DependencyId)?.Status, "A→B must become STALE.");
        AssertWp13Equal(ResultFreshnessState.Stale,
            ResultArtifactCanonicalJson.FromDurable(store.GetLatestResultArtifact("task-b")!).Freshness.State,
            "B Result must become STALE.");
        AssertWp13Equal(bId, store.GetLatestResultArtifact("task-b")!.ResultArtifactId,
            "Completed B ResultArtifactId must stay frozen.");
        AssertWp13Equal("stale", store.GetDependency(bc.DependencyId)?.Status, "B→C must become STALE.");
        AssertWp13Equal("blocked", store.GetTask("task-c")?.Status, "C must be blocked/non-current.");

        store.ReplaceResultArtifact(ResultArtifactCanonicalJson.ToDurable(
            frozenA with { Freshness = frozenA.Freshness with { State = ResultFreshnessState.Current } }));
        Success(wp13.RefreshResultDependencyStatus("task-a", null));
        AssertWp13Equal("current", store.GetDependency(bc.DependencyId)?.Status, "Restore A must walk CURRENT back to C.");

        var reloaded = new SqliteRuntimeStore(databasePath);
        var wp13Reload = new Wp13RuntimeService(
            reloaded,
            new RuntimeSchedulerService(
                reloaded,
                new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                clock,
                new FakeRunWorkerSupervisor(),
                new AllowIntegrationAuth { Allow = true }),
            clock);
        Success(wp13Reload.RefreshResultDependencyStatus(null, null));
        AssertWp13Equal("current", reloaded.GetDependency(ab.DependencyId)?.Status, "Reload must keep A→B CURRENT.");
        AssertWp13Equal("current", reloaded.GetDependency(bc.DependencyId)?.Status, "Reload must keep B→C CURRENT.");
        AssertWp13Equal("ready", reloaded.GetTask("task-c")?.Status, "Reload must keep C ready after restore.");
    }

    private static void SqliteGrillPromptProviderModelChangedSinceCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(31_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var workflow = Success(scheduler.CreateWorkflowRun("wf-grill-prompt"));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-grill-prompt"));
        Success(scheduler.CreateTask("run-grill-prompt", "write", 1, null, "task-grill-prompt"));
        Success(scheduler.DispatchReadyTask("task-grill-prompt"));
        store.UpdateRun(store.GetRun("run-grill-prompt")! with
        {
            PromptConfigId = "P1",
            ProviderId = "A",
            ModelId = "M1",
            EffectivePromptDigest = "E1"
        });
        var paused = Success(wp13.PauseRuntimeGrill(
            "run-grill-prompt",
            "task-grill-prompt",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "int-grill-prompt"));
        var cp1 = store.CheckpointsForRun("run-grill-prompt")
            .Last(item => item.PayloadJson.Contains(paused.ApprovalId, StringComparison.Ordinal));
        AssertTrue(cp1.InputDigestSetJson.Contains("\"promptConfigId\":\"P1\"", StringComparison.Ordinal),
            "Grill CP1 must retain promptConfigId.");
        Success(scheduler.CreateTask("run-grill-prompt", "write", 1, null, "task-unrelated-prompt"));
        Success(scheduler.PersistCheckpoint(
            "run-grill-prompt",
            "task-unrelated-prompt",
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
        store.UpdateRun(store.GetRun("run-grill-prompt")! with { PromptConfigId = "P2" });
        var changed = Success(wp13.ResolveRuntimeGrill(
            new ResolveRuntimeGrillRequest(paused.ApprovalId, "continue", "continue", null),
            Wp09UserPrincipal));
        AssertTrue(!StringComparer.Ordinal.Equals(changed.ResumeDecision, "Continue"),
            "Prompt change vs CP1 must not Continue even when a later CP2 exists.");
    }

    private static void SqliteSameMillisecondOversightOrderingReloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        var databasePath = Path.Combine(root, ".llmw", "project.db");
        new SqliteMigrationRunner().Migrate(databasePath, "wp13-tests", 1735689600000);
        var store = new SqliteRuntimeStore(databasePath);
        var clock = new IntegrationClock(40_000);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        var projectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab").ToString("D");
        var workflow = Success(scheduler.CreateWorkflowRun("wf-same-ms"));
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-a-same"));
        Success(scheduler.CreateTask("run-a-same", "write", 1, null, "task-a-same"));
        store.UpdateTaskStatus("task-a-same", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 40_000);
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "author_confirmed_required", "ask", null),
            Wp09UserPrincipal));
        var runA = Success(wp13.GetEffectiveOversight(projectId, null, "task-a-same"));
        AssertWp13Equal("agent_delegated", runA.NarrativeAuthority, "Same-ms Case A: Run A remains AUTO.");
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-b-same"));
        Success(scheduler.CreateTask("run-b-same", "write", 1, null, "task-b-same"));
        var runB = Success(wp13.GetEffectiveOversight(projectId, null, "task-b-same"));
        AssertWp13Equal("author_confirmed_required", runB.NarrativeAuthority, "Same-ms Case B: Run B is MANUAL immediately.");

        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-c-same"));
        Success(scheduler.CreateTask("run-c-same", "write", 1, null, "task-c-same"));
        store.UpdateTaskStatus("task-c-same", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), 40_000);
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", projectId, "agent_delegated", "auto_approve_scoped", null),
            Wp09UserPrincipal));
        var runC = Success(wp13.GetEffectiveOversight(projectId, null, "task-c-same"));
        AssertWp13Equal("author_confirmed_required", runC.NarrativeAuthority, "Same-ms MANUAL→AUTO Case A: Run C remains MANUAL.");
        Success(scheduler.CreateRun(workflow.WorkflowRunId, "writer", null, "run-d-same"));
        Success(scheduler.CreateTask("run-d-same", "write", 1, null, "task-d-same"));
        var runD = Success(wp13.GetEffectiveOversight(projectId, null, "task-d-same"));
        AssertWp13Equal("agent_delegated", runD.NarrativeAuthority, "Same-ms MANUAL→AUTO Case B: Run D is AUTO immediately.");

        var reloaded = new SqliteRuntimeStore(databasePath);
        var wp13Reload = new Wp13RuntimeService(
            reloaded,
            new RuntimeSchedulerService(
                reloaded,
                new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                clock,
                new FakeRunWorkerSupervisor(),
                new AllowIntegrationAuth { Allow = true }),
            clock);
        AssertWp13Equal("agent_delegated",
            Success(wp13Reload.GetEffectiveOversight(projectId, null, "task-a-same")).NarrativeAuthority,
            "Reload must preserve Run A AUTO.");
        AssertWp13Equal("author_confirmed_required",
            Success(wp13Reload.GetEffectiveOversight(projectId, null, "task-b-same")).NarrativeAuthority,
            "Reload must preserve Run B MANUAL.");
        AssertWp13Equal("author_confirmed_required",
            Success(wp13Reload.GetEffectiveOversight(projectId, null, "task-c-same")).NarrativeAuthority,
            "Reload must preserve Run C MANUAL.");
        AssertWp13Equal("agent_delegated",
            Success(wp13Reload.GetEffectiveOversight(projectId, null, "task-d-same")).NarrativeAuthority,
            "Reload must preserve Run D AUTO.");
        AssertTrue(reloaded.GetRun("run-a-same")!.CreatedAtMs <
                   reloaded.ListOversightOverrides()
                       .Where(item => item.NarrativeAuthority == NarrativeDecisionAuthority.AuthorConfirmedRequired)
                       .OrderBy(item => item.CreatedAtMs)
                       .First().CreatedAtMs,
            "Persisted created_at_ms must keep Run A before the MANUAL override after reload.");
    }

    private static void SqliteDelegatedProvenanceIgnoresWarningsAckDigest()
    {
        using var fixture = Wp05Fixture.Create(ChapterReviewOutcome.Pass);
        var runtimeStore = new SqliteRuntimeStore(fixture.DatabasePath);
        var clock = new IntegrationClock(32_000);
        var scheduler = new RuntimeSchedulerService(
            runtimeStore,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowIntegrationAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(runtimeStore, scheduler, clock);
        var authorization = new CoreAuthorizationService(new Wp09TestSecurityPolicySource(), wp13);
        var service = new ChapterAuthorityService(
            fixture.BlobStore,
            fixture.Coordinator,
            fixture.AuthorityStore,
            fixture.Reviewer,
            LLMW.Writing.Application.Reconcile.NoOpAuthoritySurfaceHealthGate.Instance,
            authorization,
            wp13,
            wp13);
        Success(wp13.SetOversightOverride(
            new SetOversightOverrideRequest(
                "project",
                Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab").ToString("D"),
                "agent_delegated",
                "auto_approve_scoped",
                null),
            Wp09UserPrincipal));
        File.WriteAllText(fixture.DraftPath, "wp13 warnings-ack manuscript");
        var submitted = Success(service.SubmitChapterDraft(
            new SubmitChapterDraftCommand(fixture.ChapterId, fixture.DraftPath, "wp13-ack", Principal: Wp09UserPrincipal)));
        Success(service.ReviewChapterCandidate(new ReviewChapterCandidateCommand(submitted.CandidateId, Wp09UserPrincipal)));
        var pm = CreateAgentPrincipal(
            fixture.DatabasePath,
            "wp13-ack-pm",
            "pm",
            "worker-wp13-ack-pm",
            "channel-wp13-ack-pm",
            RuntimePermissionMode.AutoApproveScoped);
        var first = Success(service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp13-ack",
            "forged-user",
            Principal: pm)));
        AssertWp13Equal(AuthorityTransactionState.Complete, first.TransactionState, "Delegated accept must COMMIT.");
        using (var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(fixture.DatabasePath))
        {
            using var warnings = connection.CreateCommand();
            warnings.CommandText = "SELECT warnings_ack_digest FROM acceptance_records LIMIT 1;";
            var digest = warnings.ExecuteScalar();
            AssertTrue(digest is null or DBNull, "warnings_ack_digest must stay null unless real warning-ack semantics wrote it.");
            using var ev = connection.CreateCommand();
            ev.CommandText =
                "SELECT event_payload_json FROM authority_events WHERE event_type='wp13.delegated_authorization' ORDER BY event_seq DESC LIMIT 1;";
            var payload = ev.ExecuteScalar()?.ToString();
            AssertTrue(FormalAuthorizationSnapshot.TryParse(payload) is not null,
                "Authority Event must hold the frozen authorization snapshot.");
            using var poison = connection.CreateCommand();
            poison.CommandText =
                "UPDATE acceptance_records SET warnings_ack_digest='deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef';";
            poison.ExecuteNonQuery();
        }

        var retry = Success(service.AcceptChapterCandidate(new AcceptChapterCandidateCommand(
            submitted.CandidateId,
            "wp13-ack",
            "forged-user",
            Principal: pm)));
        AssertTrue(!retry.ProvenanceConflict, "Recovery must use the authorization event, not warnings_ack_digest.");
        fixture.AssertScalar(1L, "SELECT COUNT(*) FROM delegated_decisions;");
        using (var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(fixture.DatabasePath))
        {
            using var warnings = connection.CreateCommand();
            warnings.CommandText = "SELECT warnings_ack_digest FROM acceptance_records LIMIT 1;";
            AssertWp13Equal(
                "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
                warnings.ExecuteScalar()?.ToString(),
                "Recovery must not parse or overwrite a real-looking warning digest.");
        }
    }

    private static TaskResultArtifactV1 SubmitSqliteComplete(
        Wp13RuntimeService wp13,
        CallerPrincipal agent,
        string taskId,
        long producedAtMs)
    {
        var artifact = ArtifactAgainst(taskId, [], producedAtMs);
        var submitted = Success(wp13.SubmitResultArtifact(
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
        return artifact with { ResultArtifactId = submitted.ResultArtifactId };
    }

    private static TaskResultArtifactV1 ArtifactAgainst(string taskId, IReadOnlyList<string> upstream, long producedAtMs) =>
        new(
            Guid.NewGuid().ToString("D"),
            taskId,
            ResultArtifactStatus.Complete,
            "done",
            [],
            [],
            null,
            [],
            [],
            [],
            new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1(null, [], null, null, null, null, [], null, null, upstream),
                new ResultProvenanceV1(null, taskId, null, null, null, null, null, null)),
            null,
            producedAtMs);

    private static CallerPrincipal CreateSqliteAgent(string databasePath, string runId, string role)
    {
        var channel = new AuthenticatedChannelContext(
            "ch-" + runId,
            AuthenticatedClientKind.AgentRuntime,
            "worker-" + runId,
            new ProjectScope(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), "workspace-13"),
            runId);
        var sessions = new RunSessionService(new SqliteRunSessionStore(databasePath));
        var created = sessions.Create(new LLMW.Writing.Application.Security.CreateRunSessionRequest(runId, channel, DateTimeOffset.UtcNow.AddMinutes(5)));
        if (!created.Succeeded || created.Value is null)
        {
            throw new InvalidOperationException("RunSession create failed: " + created.Failure?.Code);
        }

        var resolved = sessions.Resolve(new ResolveRunSessionRequest(
            runId,
            created.Value.Token.ExportOnceForAuthenticatedTransport(),
            channel));
        if (!resolved.Succeeded || resolved.Value is null)
        {
            throw new InvalidOperationException("RunSession resolve failed: " + resolved.Failure?.Code);
        }

        return resolved.Value;
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

    private static T Success<T>(RuntimeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success, got {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }

    private static void AssertWp13Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class IntegrationClock(long unixMs) : ISecurityClock
    {
        public long UnixMs { get; set; } = unixMs;

        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(UnixMs);
    }

    private sealed class AllowIntegrationAuth : IAuthorizationService
    {
        public bool Allow { get; set; }

        public CapabilityDecision Authorize(CallerPrincipal? principal, AuthorizationRequest request) =>
            new(
                request.Capability,
                principal?.Kind ?? PrincipalKind.UserInteractive,
                Allow ? CapabilityDecisionKind.Allowed : CapabilityDecisionKind.Denied,
                [Allow ? CapabilityDecisionReason.Allowed : CapabilityDecisionReason.RoleDenied],
                principal?.Role,
                Allow ? RoleCapabilityLevel.Allowed : RoleCapabilityLevel.Denied,
                SecurityScopeClassification.InScope,
                HardDenied: false);
    }
}
