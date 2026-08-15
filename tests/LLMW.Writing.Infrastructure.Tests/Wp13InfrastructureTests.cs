using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Specialists;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp13InfrastructureTests()
    {
        Run(nameof(SqliteWp13RoundTripAndUserVersionRemainOne), SqliteWp13RoundTripAndUserVersionRemainOne);
        Run(nameof(SqliteCompletionTransactionRollsBackSplitState), SqliteCompletionTransactionRollsBackSplitState);
        Run(nameof(SqliteGrillAndBackgroundSurviveReload), SqliteGrillAndBackgroundSurviveReload);
        Run(nameof(FileUserSpecialistStoreIsApplicationScopedNotProjectDb), FileUserSpecialistStoreIsApplicationScopedNotProjectDb);
        Run(nameof(SqliteWorkflowStorylineSurvivesReload), SqliteWorkflowStorylineSurvivesReload);
        Run(nameof(FileUserSpecialistIdsDoNotCollide), FileUserSpecialistIdsDoNotCollide);
    }

    private static void SqliteWp13RoundTripAndUserVersionRemainOne()
    {
        using var database = MigratedDatabase.Create();
        var store = new SqliteRuntimeStore(database.Path);
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowSpawnAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        RuntimeSuccess(scheduler.CreateWorkflowRun("wf-13"));
        RuntimeSuccess(scheduler.CreateRun("wf-13", "writer", null, "run-13"));
        RuntimeSuccess(scheduler.CreateTask("run-13", "write", 1, null, "task-13"));
        var artifact = new TaskResultArtifactV1(
            Guid.NewGuid().ToString("D"),
            "task-13",
            ResultArtifactStatus.Complete,
            "done",
            [new ResultFindingV1("f1", "ok", null)],
            ["ev-1"],
            "low",
            [],
            ["obj-1"],
            ["follow"],
            new ResultFreshnessV1(
                1,
                ResultFreshnessState.Current,
                new ResultProducedAgainstV1("rev-1", ["n1"], "e1", null, null, null, [], null, null, []),
                new ResultProvenanceV1("run-13", "task-13", null, null, "plan", "req", null, "tx")),
            "cs-1",
            10_000);
        store.InsertResultArtifact(ResultArtifactCanonicalJson.ToDurable(artifact));
        store.InsertEvidence(new EvidenceRecord("ev-1", "run-13", "task-13", "narrative", "obj-1", "digest", "{}", false, 10_000));
        RuntimeSuccess(wp13.CreateResultDependency("task-13", "task-13", "optional"));
        RuntimeSuccess(wp13.SetOversightOverride(
            new SetOversightOverrideRequest("project", "proj-1", "author_confirmed_required", "ask", null),
            new TrustedNativePrincipalSource("wp13-infra").ResolveUserInteractive()));
        store.InsertDelegatedDecision(NarrativeDecisionProvenance.AgentDelegated(
            Guid.NewGuid().ToString("D"),
            "tx",
            OversightScopeKind.Project,
            "proj-1",
            "agent",
            wp13.Resolve("proj-1", null, null),
            "digest",
            10_000));
        store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-1",
            "run-13",
            "task-13",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(BackgroundTaskKind.SubAgentRun, "run-13", null, null, "task-13")),
            "running",
            null,
            10_000,
            null));
        store.UpsertProjectSpecialist(new DurableProjectSpecialistRecord(
            "proj.spec",
            "project",
            "proj-1",
            "proj-spec",
            1,
            SpecialistProfileCanonicalJson.Write(SyntheticBuiltInSpecialistCatalog.Instance.List()[0] with
            {
                ProfileId = "proj.spec",
                Name = "proj-spec",
                ScopeKind = SpecialistScopeKind.Project
            }),
            null,
            true,
            10_000,
            10_000));

        var reloaded = new SqliteRuntimeStore(database.Path);
        Wp09AssertEqual("complete", reloaded.GetLatestResultArtifact("task-13")?.Status, "Result Artifact did not reload.");
        Wp09AssertEqual(1, reloaded.EvidenceForTask("task-13").Count, "Evidence did not reload.");
        Wp09AssertEqual(1, reloaded.ListOversightOverrides().Count, "Oversight override did not reload.");
        Wp09AssertEqual(1, reloaded.ListDelegatedDecisions().Count, "Delegated decision did not reload.");
        Wp09AssertEqual(1, reloaded.ListBackgroundTasks(null).Count, "Background task did not reload.");
        Wp09AssertEqual("proj.spec", reloaded.GetProjectSpecialist("proj.spec")?.SpecialistProfileId, "Project specialist did not reload.");
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(database.Path);
        Wp09AssertEqual(1L, Scalar<long>(connection, "PRAGMA user_version;"), "WP13 must not change user_version.");
        Wp09AssertEqual(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM schema_migrations;"), "WP13 must not add a migration.");
    }

    private static void SqliteCompletionTransactionRollsBackSplitState()
    {
        using var database = MigratedDatabase.Create();
        var store = new SqliteRuntimeStore(database.Path);
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(20_000));
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowSpawnAuth { Allow = true });
        RuntimeSuccess(scheduler.CreateWorkflowRun("wf-roll"));
        RuntimeSuccess(scheduler.CreateRun("wf-roll", "writer", null, "run-roll"));
        RuntimeSuccess(scheduler.CreateTask("run-roll", "write", 1, null, "task-roll"));
        var artifact = ResultArtifactCanonicalJson.ToDurable(new TaskResultArtifactV1(
            Guid.NewGuid().ToString("D"),
            "task-roll",
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
                new ResultProvenanceV1(null, "task-roll", null, null, null, null, null, null)),
            null,
            20_000));
        store.InsertResultArtifact(artifact);
        try
        {
            store.InTransaction(() =>
            {
                store.UpdateTaskStatus("task-roll", "completed", 20_000);
                throw new SchedulerFaultInjectedException(SchedulerFaultPoint.AfterTaskBeforeResultPersist);
            });
            throw new InvalidOperationException("Fault injection must throw.");
        }
        catch (SchedulerFaultInjectedException)
        {
        }

        var reloaded = new SqliteRuntimeStore(database.Path);
        Wp09AssertEqual("ready", reloaded.GetTask("task-roll")?.Status,
            "Task must not complete when the completion transaction is aborted.");
        AssertTrue(reloaded.GetLatestResultArtifact("task-roll") is not null, "Result Artifact insert outside the aborted transaction remains.");
        using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(database.Path);
        Wp09AssertEqual(1L, Scalar<long>(connection, "PRAGMA user_version;"), "Rollback test must keep schema v1.");
    }

    private static void SqliteGrillAndBackgroundSurviveReload()
    {
        using var database = MigratedDatabase.Create();
        var store = new SqliteRuntimeStore(database.Path);
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(30_000));
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            new AllowSpawnAuth { Allow = true });
        var wp13 = new Wp13RuntimeService(store, scheduler, clock);
        RuntimeSuccess(scheduler.CreateWorkflowRun("wf-grill"));
        RuntimeSuccess(scheduler.CreateRun("wf-grill", "writer", null, "run-grill"));
        RuntimeSuccess(scheduler.CreateTask("run-grill", "write", 1, null, "task-grill"));
        RuntimeSuccess(scheduler.DispatchReadyTask("task-grill"));
        var paused = RuntimeSuccess(wp13.PauseRuntimeGrill(
            "run-grill",
            "task-grill",
            RuntimeGrillPauseReason.NewCreativeDecisionRequired,
            new RuntimeGrillQuestionV1("next", ["continue"], "Choose"),
            "base"));
        store.InsertBackgroundTask(new DurableBackgroundTaskRecord(
            "bg-grill",
            "run-grill",
            "task-grill",
            BackgroundExecutionRefCodec.WriteKindColumn(new BackgroundExecutionRef(BackgroundTaskKind.Worker, "run-grill", null, "w1", "task-grill")),
            "running",
            store.CheckpointsForRun("run-grill")[^1].CheckpointId,
            30_000,
            null));

        var reloaded = new SqliteRuntimeStore(database.Path);
        var reloadedWp13 = new Wp13RuntimeService(
            reloaded,
            new RuntimeSchedulerService(
                reloaded,
                new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
                clock,
                new FakeRunWorkerSupervisor(),
                new AllowSpawnAuth { Allow = true }),
            clock);
        Wp09AssertEqual("pending", reloaded.GetApproval(paused.ApprovalId)?.Status, "Runtime Grill pause must survive reload.");
        Wp09AssertEqual("paused", reloaded.GetTask("task-grill")?.Status, "Paused task must survive reload.");
        var recovered = reloadedWp13.ClassifyBackgroundRecovery();
        AssertTrue(recovered.Count == 1, "Background recovery must classify the reloaded row.");
        AssertTrue(recovered[0] is not BackgroundRecoveryClassification.Completed,
            "Running background work must not be marked completed solely because of restart.");
    }

    private static void FileUserSpecialistStoreIsApplicationScopedNotProjectDb()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        var store = new FileUserSpecialistProfileStore(root);
        var profile = SyntheticBuiltInSpecialistCatalog.Instance.List()[0] with
        {
            ProfileId = "user.spec",
            Name = "user-spec",
            ScopeKind = SpecialistScopeKind.UserLibrary
        };
        store.Upsert(new DurableProjectSpecialistRecord(
            profile.ProfileId,
            "user",
            null,
            profile.Name,
            profile.Version,
            SpecialistProfileCanonicalJson.Write(profile),
            "base",
            true,
            1,
            1));
        Wp09AssertEqual("user.spec", store.Find("user.spec")?.SpecialistProfileId, "User Library must persist outside project.db.");
        Wp09AssertEqual(1, store.List().Count, "User Library list must roundtrip.");
    }

    private static void SqliteWorkflowStorylineSurvivesReload()
    {
        using var database = MigratedDatabase.Create();
        const string storylineId = "018f3e78-1234-7abc-8def-0123456789d1";
        using (var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(database.Path))
        {
            InsertObject(connection, storylineId);
            Execute(
                connection,
                $"INSERT INTO storylines(storyline_id, workflow_state, updated_at_ms) VALUES ('{storylineId}', 'active', 1);");
        }

        var store = new SqliteRuntimeStore(database.Path);
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(40_000)),
            new FakeRunWorkerSupervisor(),
            new AllowSpawnAuth { Allow = true });
        var created = RuntimeSuccess(scheduler.CreateWorkflowRun(workflowRunId: null, storylineId: storylineId));
        Wp09AssertEqual(storylineId, created.StorylineId, "CreateWorkflowRun must persist storyline_id.");
        var reloaded = new SqliteRuntimeStore(database.Path);
        Wp09AssertEqual(storylineId, reloaded.GetWorkflowRun(created.WorkflowRunId)?.StorylineId,
            "Reloaded workflow_runs.storyline_id must match.");
        using var version = new SqliteDatabaseConnectionFactory().OpenConfigured(database.Path);
        Wp09AssertEqual(1L, Scalar<long>(version, "PRAGMA user_version;"), "Storyline persistence must keep schema v1.");
        Wp09AssertEqual(1L, Scalar<long>(version, "SELECT COUNT(*) FROM schema_migrations;"), "Storyline persistence must not add a migration.");
    }

    private static void FileUserSpecialistIdsDoNotCollide()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP13", Guid.NewGuid().ToString("N"));
        var store = new FileUserSpecialistProfileStore(root);
        var first = SyntheticBuiltInSpecialistCatalog.Instance.List()[0] with
        {
            ProfileId = "a_b",
            Name = "first",
            ScopeKind = SpecialistScopeKind.UserLibrary
        };
        var second = first with { ProfileId = "a/b", Name = "second" };
        store.Upsert(new DurableProjectSpecialistRecord(
            first.ProfileId, "user", null, first.Name, first.Version, SpecialistProfileCanonicalJson.Write(first), null, true, 1, 1));
        store.Upsert(new DurableProjectSpecialistRecord(
            second.ProfileId, "user", null, second.Name, second.Version, SpecialistProfileCanonicalJson.Write(second), null, true, 1, 1));
        Wp09AssertEqual("first", store.Find("a_b")?.Name, "Underscore id must keep its own file.");
        Wp09AssertEqual("second", store.Find("a/b")?.Name, "Slash id must not overwrite the underscore id.");
        Wp09AssertEqual(2, store.List().Count, "Distinct ProfileIds must not collapse to one file.");
        Wp09AssertEqual(2, Directory.GetFiles(root, "*.json").Count(path => !path.EndsWith(".tmp.json", StringComparison.OrdinalIgnoreCase)),
            "Injective filenames must produce two JSON files.");
    }
}
