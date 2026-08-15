using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private const string Wp12ProjectId = "018f3e78-1234-7abc-8def-0123456789ab";

    private static void RunWp12InfrastructureTests()
    {
        Run(nameof(SqliteRuntimeStorePersistsHierarchyCheckpointAndReload), SqliteRuntimeStorePersistsHierarchyCheckpointAndReload);
        Run(nameof(SqliteRuntimeStoreAtomicDispatchAndUnknownQuery), SqliteRuntimeStoreAtomicDispatchAndUnknownQuery);
        Run(nameof(SqliteRuntimeStoreKeepsUserVersionOneAndForeignKeys), SqliteRuntimeStoreKeepsUserVersionOneAndForeignKeys);
        Run(nameof(ExistingProjectPreflightRejectsArbitraryDirectoryWithoutMutation), ExistingProjectPreflightRejectsArbitraryDirectoryWithoutMutation);
        Run(nameof(ExistingProjectPreflightReadsDescriptorIdentityAndCanonicalDb), ExistingProjectPreflightReadsDescriptorIdentityAndCanonicalDb);
        Run(nameof(ExistingProjectPreflightRefusesFutureVersionsWithoutMutation), ExistingProjectPreflightRefusesFutureVersionsWithoutMutation);
        Run(nameof(QueuedChildSpawnSurvivesSqliteReload), QueuedChildSpawnSurvivesSqliteReload);
    }

    private static void SqliteRuntimeStorePersistsHierarchyCheckpointAndReload()
    {
        using var database = MigratedDatabase.Create();
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var store = new SqliteRuntimeStore(database.Path);
        var auth = new AllowSpawnAuth { Allow = true };
        var service = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor(),
            auth);
        var workflow = RuntimeSuccess(service.CreateWorkflowRun("wf-sqlite"));
        var root = RuntimeSuccess(service.CreateRun(workflow.WorkflowRunId, "writer", null, "run-root"));
        RuntimeSuccess(service.CreateTask(root.RunId, "write", 3, null, "task-1"));
        var child = RuntimeSuccess(service.SpawnChildRun(
            root.RunId,
            "task-1",
            "writer",
            null,
            new TrustedNativePrincipalSource("wp12-infra").ResolveUserInteractive()));
        Wp09AssertEqual(0, root.Depth, "Root depth must persist as 0.");
        Wp09AssertEqual(1, child.Depth, "Child depth must persist as parent+1.");
        var task = store.GetTask("task-1")!;
        var dispatch = RuntimeSuccess(service.DispatchReadyTask(task.TaskId));
        Wp09AssertEqual(1, dispatch.AttemptNo, "First attempt must be 1.");
        var checkpoint = CheckpointV1.Create(
            "plan",
            "digest",
            "{}",
            "{}",
            "summary",
            [new CheckpointCriticalMessage(1, "user", "hi")],
            [],
            [],
            [],
            [],
            [],
            null,
            null,
            null,
            null);
        var checkpointId = RuntimeSuccess(service.PersistCheckpoint(root.RunId, task.TaskId, 1, CanonicalJson.WriteCheckpoint(checkpoint), "{}"));
        service.CancelScope("run", root.RunId);

        var reloaded = new SqliteRuntimeStore(database.Path);
        var snapshot = reloaded.LoadSnapshot();
        Wp09AssertEqual("cancelled", snapshot.Runs.Single(item => item.RunId == "run-root").Status, "Cancellation did not persist.");
        Wp09AssertEqual("cancelled", snapshot.Runs.Single(item => item.RunId == child.ChildRunId).Status, "Child cancellation did not persist.");
        Wp09AssertEqual(checkpointId, snapshot.Checkpoints.Single().CheckpointId, "Checkpoint did not reload.");
        Wp09AssertEqual(1, reloaded.MaxAttemptNo("task-1"), "Attempt numbering did not persist.");
        AssertTrue(reloaded.CheckpointsForRun("run-root").Count == 1, "Latest checkpoint selection failed.");
    }

    private static void SqliteRuntimeStoreAtomicDispatchAndUnknownQuery()
    {
        using var database = MigratedDatabase.Create();
        var store = new SqliteRuntimeStore(database.Path);
        var faults = new MutableSchedulerFaultInjector { Fault = SchedulerFaultPoint.AfterTaskRunningBeforeAttempt };
        var service = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(20_000)),
            new FakeRunWorkerSupervisor(),
            faults: faults);
        RuntimeSuccess(service.CreateWorkflowRun("wf-atom"));
        RuntimeSuccess(service.CreateRun("wf-atom", "writer", null, "run-atom"));
        RuntimeSuccess(service.CreateTask("run-atom", "write", 1, null, "task-atom"));
        try
        {
            _ = service.DispatchReadyTask("task-atom");
            throw new InvalidOperationException("Injected dispatch fault did not throw.");
        }
        catch (SchedulerFaultInjectedException)
        {
        }

        var snapshot = store.LoadSnapshot();
        Wp09AssertEqual("ready", snapshot.Tasks.Single().Status, "Failed atomic dispatch left a partial RUNNING task.");
        Wp09AssertEqual(0, snapshot.Attempts.Count, "Failed atomic dispatch left an Attempt row.");

        faults.Fault = SchedulerFaultPoint.None;
        var producer = RuntimeSuccess(service.CreateTask("run-atom", "write", 1, null, "producer"));
        store.InsertDependency(new DurableDependencyRecord(
            "dep-1",
            "task-atom",
            producer.TaskId,
            StructuralReadiness.RequiredKind,
            "blocked"));
        var blocked = RuntimeSuccess(service.DispatchReadyTask("task-atom"));
        Wp09AssertEqual("blocked", blocked.Outcome, "Unsatisfied required dependency must block.");
        service.RecordUnknownToolCall("run-atom", "task-atom", "write");
        AssertTrue(store.ToolCallsFor("run-atom", "task-atom").Any(item => item.SideEffectState == "unknown"),
            "UNKNOWN tool side effect was not queryable.");

        store.InsertToolCall(new DurableToolCallRecord(
            "tool-running",
            "run-atom",
            "task-atom",
            "write",
            "running",
            "none"));
        store.MarkRunningToolCallsUnknown("run-atom");
        AssertTrue(store.ToolCallsFor("run-atom", "task-atom").Any(item =>
                item.ToolCallId == "tool-running" && item.SideEffectState == "unknown"),
            "SQLite must mark in-flight tool calls UNKNOWN without converting them to FAILED.");
    }

    private static void SqliteRuntimeStoreKeepsUserVersionOneAndForeignKeys()
    {
        using var database = MigratedDatabase.Create();
        using var connection = OpenConfigured(database.Path);
        Wp09AssertEqual(1L, Scalar<long>(connection, "PRAGMA user_version;"), "WP12 must not change user_version.");
        Wp09AssertEqual(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM schema_migrations;"), "WP12 must not add a migration.");
        try
        {
            Execute(connection, "INSERT INTO tasks(task_id,run_id,task_kind,status,priority,created_at_ms,updated_at_ms) VALUES ('orphan','missing','write','ready',1,1,1);");
            throw new InvalidOperationException("Orphan task insert must fail foreign keys.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
    }

    private static void ExistingProjectPreflightRejectsArbitraryDirectoryWithoutMutation()
    {
        var empty = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP12", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var denied = ExistingProjectPreflight.TryBind(empty);
            AssertTrue(!denied.Succeeded, "Arbitrary existing directory must be rejected.");
            AssertTrue(!File.Exists(Path.Combine(empty, "project.db")), "Rejection must not create root project.db.");
            AssertTrue(!File.Exists(Path.Combine(empty, ".llmw", "project.db")), "Rejection must not create .llmw/project.db.");
            AssertTrue(!File.Exists(Path.Combine(empty, "project.llmw.json")), "Rejection must not create a descriptor.");
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }

        var invalidId = CreateProjectFixture("550e8400-e29b-41d4-a716-446655440000", 1, 1, withDatabase: true);
        try
        {
            var denied = ExistingProjectPreflight.TryBind(invalidId);
            AssertTrue(!denied.Succeeded, "Non-UUIDv7 ProjectId must be rejected.");
            AssertTrue(!File.Exists(Path.Combine(invalidId, "project.db")), "Invalid ProjectId must not create root project.db.");
        }
        finally
        {
            Directory.Delete(invalidId, recursive: true);
        }

        var missingDb = CreateProjectFixture(Wp12ProjectId, 1, 1, withDatabase: false);
        try
        {
            var denied = ExistingProjectPreflight.TryBind(missingDb);
            AssertTrue(!denied.Succeeded, "Missing .llmw/project.db must fail closed.");
            AssertTrue(!File.Exists(Path.Combine(missingDb, ".llmw", "project.db")), "Missing DB preflight must not create project.db.");
            AssertTrue(!File.Exists(Path.Combine(missingDb, "project.db")), "Missing DB preflight must not create root project.db.");
        }
        finally
        {
            Directory.Delete(missingDb, recursive: true);
        }

        if (OperatingSystem.IsWindows())
        {
            var valid = CreateProjectFixture(Wp12ProjectId, 1, 1, withDatabase: true);
            var linkParent = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP12", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(linkParent);
            var link = Path.Combine(linkParent, "junction-root");
            try
            {
                CreateJunction(link, valid);
                var denied = ExistingProjectPreflight.TryBind(link);
                AssertTrue(!denied.Succeeded, "Requested project directory that is a reparse point must be rejected.");
            }
            finally
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }

                Directory.Delete(linkParent, recursive: true);
                Directory.Delete(valid, recursive: true);
            }
        }
    }

    private static void ExistingProjectPreflightReadsDescriptorIdentityAndCanonicalDb()
    {
        var first = CreateProjectFixture(Wp12ProjectId, 1, 1, withDatabase: true);
        var copy = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP12", Guid.NewGuid().ToString("N"));
        try
        {
            var bound = ExistingProjectPreflight.TryBind(first);
            AssertTrue(bound.Succeeded, "Valid existing project must bind.");
            Wp09AssertEqual(Guid.Parse(Wp12ProjectId), bound.ProjectId, "ProjectId must come from the descriptor, not the path.");
            Wp09AssertEqual(Path.GetFullPath(Path.Combine(first, ".llmw", "project.db")), bound.DatabasePath, "DB path must be exactly .llmw/project.db.");
            AssertTrue(!File.Exists(Path.Combine(first, "project.db")), "Open must never create root/project.db.");

            CopyDirectory(first, copy);
            var copied = ExistingProjectPreflight.TryBind(copy);
            AssertTrue(copied.Succeeded, "Copied valid descriptor must still bind.");
            Wp09AssertEqual(bound.ProjectId, copied.ProjectId, "Moving/copying the same descriptor must not manufacture a new ProjectId.");

            var registry = new TrustedIpcBindingRegistry();
            AssertTrue(!registry.TryBind(AuthenticatedClientKind.AgentRuntime, out _), "Runtime binding must not exist before preflight.");
            registry.Register(new TrustedIpcLaunchRecord(
                AuthenticatedClientKind.AgentRuntime,
                "runtime-1",
                "runtime-ch",
                new ProjectScope(copied.ProjectId, "workspace-01")));
            AssertTrue(registry.TryBind(AuthenticatedClientKind.AgentRuntime, out var record), "Trusted Runtime binding is created only after preflight.");
            Wp09AssertEqual(copied.ProjectId, record.ProjectScope.ProjectId, "Worker/Runtime scope must use descriptor ProjectId.");
        }
        finally
        {
            if (Directory.Exists(first))
            {
                Directory.Delete(first, recursive: true);
            }

            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }
        }
    }

    private static void ExistingProjectPreflightRefusesFutureVersionsWithoutMutation()
    {
        var futureFormat = CreateProjectFixture(Wp12ProjectId, 2, 1, withDatabase: true);
        var futureSchema = CreateProjectFixture(Wp12ProjectId, 1, 2, withDatabase: true);
        try
        {
            var dbWrite = File.GetLastWriteTimeUtc(Path.Combine(futureFormat, ".llmw", "project.db"));
            var descriptorWrite = File.GetLastWriteTimeUtc(Path.Combine(futureFormat, "project.llmw.json"));
            var deniedFormat = ExistingProjectPreflight.TryBind(futureFormat);
            AssertTrue(!deniedFormat.Succeeded, "Future formatVersion must be refused.");
            Wp09AssertEqual(dbWrite, File.GetLastWriteTimeUtc(Path.Combine(futureFormat, ".llmw", "project.db")),
                "Future format refusal must not mutate project.db.");
            Wp09AssertEqual(descriptorWrite, File.GetLastWriteTimeUtc(Path.Combine(futureFormat, "project.llmw.json")),
                "Future format refusal must not mutate the descriptor.");
            AssertTrue(!File.Exists(Path.Combine(futureFormat, "project.db")), "Future refusal must not create root project.db.");

            var deniedSchema = ExistingProjectPreflight.TryBind(futureSchema);
            AssertTrue(!deniedSchema.Succeeded, "Future schemaVersion must be refused.");
            AssertTrue(!File.Exists(Path.Combine(futureSchema, "project.db")), "Future schema refusal must not create root project.db.");
        }
        finally
        {
            Directory.Delete(futureFormat, recursive: true);
            Directory.Delete(futureSchema, recursive: true);
        }
    }

    private static void QueuedChildSpawnSurvivesSqliteReload()
    {
        using var database = MigratedDatabase.Create();
        var store = new SqliteRuntimeStore(database.Path);
        var workers = new FakeRunWorkerSupervisor();
        var auth = new AllowSpawnAuth { Allow = true };
        var service = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(30_000)),
            workers,
            auth);
        RuntimeSuccess(service.CreateWorkflowRun("wf-queue"));
        RuntimeSuccess(service.CreateRun("wf-queue", "writer", null, "queue-parent"));
        RuntimeSuccess(service.CreateTask("queue-parent", "write", 1, null, "queue-parent-task"));
        for (var index = 0; index < 4; index++)
        {
            RuntimeSuccess(service.CreateRun("wf-queue", "writer", null, "occ-" + index));
            RuntimeSuccess(service.CreateTask("occ-" + index, "write", 1, null, "occ-task-" + index));
            var dispatched = RuntimeSuccess(service.DispatchReadyTask("occ-task-" + index));
            RuntimeSuccess(service.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId));
        }

        var queued = RuntimeSuccess(service.SpawnChildRun(
            "queue-parent",
            "queue-parent-task",
            "writer",
            null,
            new TrustedNativePrincipalSource("wp12-infra").ResolveUserInteractive()));
        Wp09AssertEqual("queued", queued.Outcome, "Capacity-full spawn must queue.");
        AssertTrue(!string.IsNullOrWhiteSpace(queued.ChildRunId), "Queued spawn must persist ChildRunId.");
        Wp09AssertEqual(4, workers.Snapshot().Count(item => item.Alive), "Queued spawn must not launch a fifth Worker.");

        var reloaded = new SqliteRuntimeStore(database.Path);
        var snapshot = reloaded.LoadSnapshot();
        var child = snapshot.Runs.Single(item => item.RunId == queued.ChildRunId);
        Wp09AssertEqual("queued", child.Status, "Queued child must survive reload.");
        Wp09AssertEqual("queue-parent", child.ParentRunId ?? "", "Queued child must keep parent identity.");
        AssertTrue(snapshot.Tasks.Any(task =>
                task.RunId == queued.ChildRunId && task.ParentTaskId == "queue-parent-task"),
            "Queued child must keep durable parent_task_id.");
        Wp09AssertEqual(1, snapshot.Runs.Count(item => item.RunId == queued.ChildRunId), "Reload must not duplicate the queued child.");
    }

    private static string CreateProjectFixture(string projectId, int formatVersion, int schemaVersion, bool withDatabase)
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP12", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        File.WriteAllText(
            Path.Combine(root, "project.llmw.json"),
            "{\"projectId\":\"" + projectId + "\",\"formatVersion\":" + formatVersion + ",\"schemaVersion\":" + schemaVersion + "}");
        if (withDatabase)
        {
            new SqliteMigrationRunner().Migrate(Path.Combine(root, ".llmw", "project.db"), "wp12-tests", 1735689600000);
        }

        return root;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class AllowSpawnAuth : IAuthorizationService
    {
        public bool Allow { get; set; }

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

    private static T RuntimeSuccess<T>(RuntimeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success, got {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }
}
