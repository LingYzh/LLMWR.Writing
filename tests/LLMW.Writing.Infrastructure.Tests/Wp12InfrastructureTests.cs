using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static void RunWp12InfrastructureTests()
    {
        Run(nameof(SqliteRuntimeStorePersistsHierarchyCheckpointAndReload), SqliteRuntimeStorePersistsHierarchyCheckpointAndReload);
        Run(nameof(SqliteRuntimeStoreAtomicDispatchAndUnknownQuery), SqliteRuntimeStoreAtomicDispatchAndUnknownQuery);
        Run(nameof(SqliteRuntimeStoreKeepsUserVersionOneAndForeignKeys), SqliteRuntimeStoreKeepsUserVersionOneAndForeignKeys);
    }

    private static void SqliteRuntimeStorePersistsHierarchyCheckpointAndReload()
    {
        using var database = MigratedDatabase.Create();
        var clock = new MutableSecurityClock(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var store = new SqliteRuntimeStore(database.Path);
        var service = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            clock,
            new FakeRunWorkerSupervisor());
        var workflow = RuntimeSuccess(service.CreateWorkflowRun("wf-sqlite"));
        var root = RuntimeSuccess(service.CreateRun(workflow.WorkflowRunId, "writer", null, "run-root"));
        var child = RuntimeSuccess(service.CreateRun(workflow.WorkflowRunId, "writer", root.RunId, "run-child"));
        Wp09AssertEqual(0, root.Depth, "Root depth must persist as 0.");
        Wp09AssertEqual(1, child.Depth, "Child depth must persist as parent+1.");
        var task = RuntimeSuccess(service.CreateTask(root.RunId, "write", 3, null, "task-1"));
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

    private static T RuntimeSuccess<T>(RuntimeResult<T> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            throw new InvalidOperationException($"Expected success, got {result.Failure?.Code}: {result.Failure?.Detail}");
        }

        return result.Value;
    }
}
