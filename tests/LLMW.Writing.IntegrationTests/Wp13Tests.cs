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
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
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

        public void Record(DelegatedDecisionRecord record)
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("provenance-fault");
            }

            inner.Record(record);
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
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
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
