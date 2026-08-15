using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Application.Tests;

internal static class Wp12ApplicationTests
{
    private static readonly Guid ProjectId = Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab");
    private static readonly ProjectScope Scope = new(ProjectId, "workspace-01");
    private const string Workspace = "workspace-01";

    public static int Run()
    {
        ConcurrentWorkerBindingsRemainDistinctAndForgedIdsAreDenied();
        StaleWorkerRecordCannotAuthorizeReplacement();
        DisconnectUnregistersOnlyIntendedWorkerBinding();
        SchedulerQueuesFifthRunAndSharesTreeBudget();
        BudgetChangeDoesNotKillRunningOrExceedFour();
        DepthFiveAndSpoofAreDenied();
        SpawnCapabilityDenialIsNotCapacityQueue();
        CancelledParentCannotSpawn();
        RetrySameTaskNewAttemptAndUnknownBlocksRetry();
        CheckpointResumeAndSecretExclusion();
        CancelCascadeIsScopedAndIdempotent();
        ReconcileWorkerGoneBlocksUnknownRetryAndDoesNotReviveCancelled();
        SecondLiveWorkerForSameRunIsRejected();
        WorkerBoundRunCannotIssueSessionForAnotherRun();
        TrustedLaunchEnvironmentRejectsCoreBootstrap();
        ManagementCommandsRequireRuntimeChannel();
        IpcSurfaceStillCannotReachSandboxHost();
        Console.WriteLine("Application WP12 scheduler tests passed (17).");
        return 17;
    }

    private static void ConcurrentWorkerBindingsRemainDistinctAndForgedIdsAreDenied()
    {
        var registry = new TrustedIpcBindingRegistry();
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-a", "channel-a", Scope, "aaaaaaaaaaaaaaaa", "run-a"));
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-b", "channel-b", Scope, "bbbbbbbbbbbbbbbb", "run-b"));
        AssertTrue(registry.TryBind("aaaaaaaaaaaaaaaa", AuthenticatedClientKind.Worker, out var a), "Worker A binding missing.");
        AssertTrue(registry.TryBind("bbbbbbbbbbbbbbbb", AuthenticatedClientKind.Worker, out var b), "Worker B binding missing.");
        AssertEqual("run-a", a.BoundRunId, "Worker A BoundRunId drifted.");
        AssertEqual("run-b", b.BoundRunId, "Worker B BoundRunId drifted.");
        AssertTrue(!StringComparer.Ordinal.Equals(a.WorkerInstanceId, b.WorkerInstanceId), "Concurrent workers collapsed to one identity.");
        AssertTrue(!registry.TryBind("aaaaaaaaaaaaaaaa", AuthenticatedClientKind.AgentRuntime, out _), "Worker record must not bind as Runtime.");
        AssertTrue(!registry.TryBind("ffffffffffffffff", AuthenticatedClientKind.Worker, out _), "Forged launch binding must fail.");
        AssertTrue(!registry.TryBind(AuthenticatedClientKind.Worker, out _), "Worker must not bind by kind alone.");
    }

    private static void StaleWorkerRecordCannotAuthorizeReplacement()
    {
        var registry = new TrustedIpcBindingRegistry();
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-old", "channel-old", Scope, "cccccccccccccccc", "run-1"));
        registry.Unregister("cccccccccccccccc");
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-new", "channel-new", Scope, "dddddddddddddddd", "run-1"));
        AssertTrue(!registry.TryBind("cccccccccccccccc", AuthenticatedClientKind.Worker, out _), "Stale Worker launch record remained authoritative.");
        AssertTrue(registry.TryBind("dddddddddddddddd", AuthenticatedClientKind.Worker, out var bound), "Replacement Worker binding missing.");
        AssertEqual("worker-new", bound.WorkerInstanceId, "Replacement Worker identity drifted.");
    }

    private static void DisconnectUnregistersOnlyIntendedWorkerBinding()
    {
        var registry = new TrustedIpcBindingRegistry();
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.AgentRuntime, "runtime-1", "runtime-ch", Scope));
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-a", "channel-a", Scope, "aaaaaaaaaaaaaaaa", "run-a"));
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-b", "channel-b", Scope, "bbbbbbbbbbbbbbbb", "run-b"));
        registry.Unregister("aaaaaaaaaaaaaaaa");
        AssertTrue(!registry.TryBind("aaaaaaaaaaaaaaaa", AuthenticatedClientKind.Worker, out _), "Disconnected Worker A still bound.");
        AssertTrue(registry.TryBind("bbbbbbbbbbbbbbbb", AuthenticatedClientKind.Worker, out _), "Unrelated Worker B was removed.");
        AssertTrue(registry.TryBind(AuthenticatedClientKind.AgentRuntime, out _), "Runtime binding was removed by a Worker disconnect.");
    }

    private static void SchedulerQueuesFifthRunAndSharesTreeBudget()
    {
        var service = CreateService(out var workers, out _);
        var workflow = Success(service.CreateWorkflowRun("wf-1"));
        for (var index = 0; index < 5; index++)
        {
            var run = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "run-" + index));
            Success(service.CreateTask(run.RunId, "write", 1, null, "task-" + index));
        }

        for (var index = 0; index < 4; index++)
        {
            var dispatched = Success(service.DispatchReadyTask("task-" + index));
            AssertEqual("dispatched", dispatched.Outcome, "First four READY runs must dispatch.");
            Success(service.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId));
        }

        var fifth = Success(service.DispatchReadyTask("task-4"));
        AssertEqual("queued", fifth.Outcome, "Fifth READY run must queue rather than fail.");
        AssertEqual(4, workers.Snapshot().Count(item => item.Alive), "Only four Workers may be live.");
        AssertTrue(!string.Equals(workers.Snapshot()[0].WorkerInstanceId, workers.Snapshot()[1].WorkerInstanceId, StringComparison.Ordinal),
            "Workers were reused.");
    }

    private static void BudgetChangeDoesNotKillRunningOrExceedFour()
    {
        var policy = new MutableConcurrencyBudgetPolicy();
        var store = new MemoryRuntimeStore();
        var workers = new FakeRunWorkerSupervisor();
        var service = new RuntimeSchedulerService(store, policy, new FixedClock(1_000), workers);
        var workflow = Success(service.CreateWorkflowRun("wf-b"));
        for (var index = 0; index < 3; index++)
        {
            var run = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "br-" + index));
            Success(service.CreateTask(run.RunId, "write", 1, null, "bt-" + index));
            var dispatched = Success(service.DispatchReadyTask("bt-" + index));
            Success(service.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId));
        }

        policy.SetEffective(1);
        AssertEqual(3, workers.Snapshot().Count(item => item.Alive), "Budget decrease must not kill running Runs.");
        var extraRun = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "br-extra"));
        Success(service.CreateTask(extraRun.RunId, "write", 1, null, "bt-extra"));
        AssertEqual("queued", Success(service.DispatchReadyTask("bt-extra")).Outcome, "Dispatch must stop while active >= effective.");
        policy.SetEffective(4);
        AssertEqual(4, policy.Current.Effective, "Budget 1→4 must restore ceiling.");
        AssertEqual(4, ConcurrencyBudget.FromEffective(99).Effective, "Effective budget must never exceed 4.");
    }

    private static void DepthFiveAndSpoofAreDenied()
    {
        var service = CreateService(out _, out var auth);
        auth.Allow = true;
        var workflow = Success(service.CreateWorkflowRun("wf-d"));
        var root = Success(service.CreateRun(workflow.WorkflowRunId, "pm", null, "d0"));
        AssertEqual(0, root.Depth, "Root depth must be 0.");
        var current = root;
        for (var depth = 1; depth <= 4; depth++)
        {
            current = Success(service.CreateRun(workflow.WorkflowRunId, "writer", current.RunId, "d" + depth));
            AssertEqual(depth, current.Depth, "Derived child depth drifted.");
        }

        var denied = service.CreateRun(workflow.WorkflowRunId, "writer", current.RunId, "d5");
        AssertEqual(RuntimeError.DepthLimit, denied.Failure?.Code, "Depth 5 must be denied.");
        var spawnSpoof = service.SpawnChildRun(root.RunId, "missing", "writer", 0, Principal());
        AssertEqual(RuntimeError.DepthSpoof, spawnSpoof.Failure?.Code, "Spoofed depth=0 on a child must be rejected, not clamped.");
        var fromFour = service.SpawnChildRun(current.RunId, "missing", "writer", null, Principal());
        AssertEqual(RuntimeError.DepthLimit, fromFour.Failure?.Code, "Spawn from depth 4 must be DEPTH_LIMIT.");
    }

    private static void SpawnCapabilityDenialIsNotCapacityQueue()
    {
        var service = CreateService(out _, out var auth);
        var workflow = Success(service.CreateWorkflowRun("wf-s"));
        var parent = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "spawn-parent"));
        Success(service.CreateTask(parent.RunId, "write", 1, null, "spawn-task"));
        auth.Allow = false;
        var denied = service.SpawnChildRun(parent.RunId, "spawn-task", "writer", null, Principal());
        AssertEqual(RuntimeError.SpawnDenied, denied.Failure?.Code, "Missing Agent.Spawn must deny.");
        auth.Allow = true;
        for (var index = 0; index < 4; index++)
        {
            var run = Success(service.CreateRun(workflow.WorkflowRunId, "writer", parent.RunId, "child-" + index));
            DispatchAndLaunch(service, run.RunId, "ct-" + index);
        }

        var queued = Success(service.SpawnChildRun(parent.RunId, "spawn-task", "writer", null, Principal()));
        AssertEqual("queued", queued.Outcome, "Capacity full must queue a valid spawn.");
    }

    private static void CancelledParentCannotSpawn()
    {
        var service = CreateService(out _, out var auth);
        auth.Allow = true;
        var workflow = Success(service.CreateWorkflowRun("wf-cancel-spawn"));
        var parent = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "cancel-spawn-parent"));
        Success(service.CreateTask(parent.RunId, "write", 1, null, "cancel-spawn-task"));
        Success(service.CancelScope("run", parent.RunId));
        var denied = service.SpawnChildRun(parent.RunId, "cancel-spawn-task", "writer", null, Principal());
        AssertEqual(RuntimeError.Cancelled, denied.Failure?.Code, "Cancelled parent must not spawn.");
    }

    private static void RetrySameTaskNewAttemptAndUnknownBlocksRetry()
    {
        var service = CreateService(out _, out _, out var store);
        var workflow = Success(service.CreateWorkflowRun("wf-r"));
        var run = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "retry-run"));
        Success(service.CreateTask(run.RunId, "write", 1, null, "retry-task"));
        var first = Success(service.DispatchReadyTask("retry-task"));
        AssertEqual(1, first.AttemptNo, "First dispatch must be Attempt 1.");
        service.CompleteTask("retry-task");
        AssertEqual(RuntimeError.IllegalTransition, service.RetryTask("retry-task").Failure?.Code, "Completed tasks must not retry.");

        var run2 = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "retry-run-2"));
        Success(service.CreateTask(run2.RunId, "write", 1, null, "fail-task"));
        var attempt = Success(service.DispatchReadyTask("fail-task"));
        store.UpdateTaskStatus("fail-task", TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Failed), 2);
        store.UpdateAttemptStatus(attempt.AttemptId, AttemptStatusCodec.ToDurableValue(AttemptStatus.Failed), 2);
        var retry = Success(service.RetryTask("fail-task"));
        AssertEqual("fail-task", retry.TaskId, "Retry must keep the same Task.");
        AssertEqual(2, retry.AttemptNo, "Retry must allocate Attempt 2.");
        var secondDispatch = Success(service.DispatchReadyTask("fail-task"));
        AssertEqual(2, secondDispatch.AttemptNo, "Dispatch after retry must reuse Attempt 2.");

        var run3 = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "unknown-run"));
        Success(service.CreateTask(run3.RunId, "write", 1, null, "unknown-task"));
        service.RecordUnknownToolCall(run3.RunId, "unknown-task", "write");
        AssertEqual(RuntimeError.UnknownSideEffect, service.RetryTask("unknown-task").Failure?.Code,
            "UNKNOWN side effect must not auto-retry.");
        AssertEqual(RuntimeError.UnknownSideEffect, service.DispatchReadyTask("unknown-task").Failure?.Code,
            "UNKNOWN side effect must not dispatch a new attempt.");
    }

    private static void CheckpointResumeAndSecretExclusion()
    {
        var service = CreateService(out _, out _);
        var workflow = Success(service.CreateWorkflowRun("wf-c"));
        var run = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "cp-run"));
        var checkpoint = CheckpointV1.Create(
            "plan-1",
            "plan-digest",
            "{}",
            "{\"visible\":\"ok\",\"token\":\"secret-value\"}",
            "summary",
            Enumerable.Range(1, 25).Select(index => new CheckpointCriticalMessage(index, "user", "m" + index)).ToArray(),
            [new CheckpointToolReference("tool-1", "read", new string('x', CheckpointV1.ToolReferenceHeadTailBytes * 3))],
            [],
            [],
            [],
            ["authorityRevision:1"],
            "prompt-1",
            "provider-1",
            "model-1",
            "eff-1");
        var payload = CanonicalJson.WriteCheckpoint(checkpoint);
        AssertTrue(!payload.Contains("secret-value", StringComparison.Ordinal), "Checkpoint leaked a secret.");
        AssertTrue(payload.Contains("\"schemaVersion\":1", StringComparison.Ordinal), "Checkpoint v1 version missing.");
        var id = Success(service.PersistCheckpoint(run.RunId, null, 1, payload, "{\"authorityRevision\":\"1\"}"));
        var cont = Success(service.ClassifyResume(run.RunId, Fresh(unrelatedDraft: true)));
        AssertEqual(ResumeDecisionKind.Continue, cont.Kind, "Unrelated Draft must CONTINUE.");
        var replan = Success(service.ClassifyResume(run.RunId, Fresh(planInvalid: false) with
        {
            PromptConfigId = "other-prompt"
        }));
        AssertEqual(ResumeDecisionKind.Replan, replan.Kind, "Changed inputs with valid plan must REPLAN.");
        var restartTask = Success(service.ClassifyResume(run.RunId, Fresh(planInvalid: true)));
        AssertEqual(ResumeDecisionKind.RestartTask, restartTask.Kind, "Invalid plan must RESTART_TASK.");
        var restartRun = Success(service.ClassifyResume(run.RunId, Fresh(structural: true)));
        AssertEqual(ResumeDecisionKind.RestartRun, restartRun.Kind, "Structural invalidation must RESTART_RUN.");
        AssertEqual(RuntimeError.CheckpointUnsupported, service.PersistCheckpoint(run.RunId, null, 2, payload, "{}").Failure?.Code,
            "Unsupported checkpoint schema must fail.");
        _ = id;
    }

    private static void CancelCascadeIsScopedAndIdempotent()
    {
        var service = CreateService(out var workers, out _);
        var wfA = Success(service.CreateWorkflowRun("wf-a"));
        var wfB = Success(service.CreateWorkflowRun("wf-b"));
        var root = Success(service.CreateRun(wfA.WorkflowRunId, "writer", null, "cancel-root"));
        var child = Success(service.CreateRun(wfA.WorkflowRunId, "writer", root.RunId, "cancel-child"));
        var other = Success(service.CreateRun(wfB.WorkflowRunId, "writer", null, "cancel-other"));
        Success(service.CreateTask(root.RunId, "write", 1, null, "t-root"));
        Success(service.CreateTask(child.RunId, "write", 1, null, "t-child"));
        Success(service.CreateTask(other.RunId, "write", 1, null, "t-other"));
        var dRoot = Success(service.DispatchReadyTask("t-root"));
        Success(service.LaunchRunWorker(dRoot.RunId, dRoot.TaskId, dRoot.AttemptId));
        var dChild = Success(service.DispatchReadyTask("t-child"));
        Success(service.LaunchRunWorker(dChild.RunId, dChild.TaskId, dChild.AttemptId));
        var dOther = Success(service.DispatchReadyTask("t-other"));
        Success(service.LaunchRunWorker(dOther.RunId, dOther.TaskId, dOther.AttemptId));
        var first = Success(service.CancelScope("run", root.RunId));
        var second = Success(service.CancelScope("run", root.RunId));
        AssertEqual(string.Join(',', first.AffectedRunIds.OrderBy(id => id, StringComparer.Ordinal)),
            string.Join(',', second.AffectedRunIds.OrderBy(id => id, StringComparer.Ordinal)),
            "Cancel must be idempotent.");
        AssertTrue(first.AffectedRunIds.Contains("cancel-root") && first.AffectedRunIds.Contains("cancel-child"),
            "Cancel missed descendants.");
        AssertTrue(!first.AffectedRunIds.Contains("cancel-other"), "Cancel leaked into another WorkflowRun.");
        AssertTrue(!workers.IsAlive(workers.Snapshot().First(item => item.RunId == "cancel-root").WorkerInstanceId),
            "Cancelled Worker remained alive.");
        AssertTrue(workers.IsAlive(workers.Snapshot().First(item => item.RunId == "cancel-other").WorkerInstanceId),
            "Unrelated Worker was terminated.");
    }

    private static void ReconcileWorkerGoneBlocksUnknownRetryAndDoesNotReviveCancelled()
    {
        var service = CreateService(out var workers, out _, out var store);
        var workflow = Success(service.CreateWorkflowRun("wf-orph"));
        var live = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "orph-live"));
        Success(service.CreateTask(live.RunId, "write", 1, null, "orph-task"));
        var dispatched = Success(service.DispatchReadyTask("orph-task"));
        Success(service.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId));
        store.InsertToolCall(new DurableToolCallRecord(
            "tool-inflight",
            live.RunId,
            "orph-task",
            "write",
            "running",
            SideEffectStateCodec.ToDurableValue(SideEffectState.None)));
        var workerId = workers.Snapshot().Single(item => item.RunId == live.RunId).WorkerInstanceId;
        workers.Crash(workerId);

        var cancelled = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "orph-cancelled"));
        Success(service.CreateTask(cancelled.RunId, "write", 1, null, "orph-cancelled-task"));
        var cancelledDispatch = Success(service.DispatchReadyTask("orph-cancelled-task"));
        Success(service.LaunchRunWorker(cancelledDispatch.RunId, cancelledDispatch.TaskId, cancelledDispatch.AttemptId));
        Success(service.CancelScope("run", cancelled.RunId));

        var items = Success(service.ReconcileWorkers());
        AssertTrue(items.Any(item =>
                StringComparer.Ordinal.Equals(item.Classification, "activeRunWorkerGone") &&
                StringComparer.Ordinal.Equals(item.RunId, live.RunId)),
            "Missing worker must classify as activeRunWorkerGone.");
        AssertEqual("interrupted", store.GetRun(live.RunId)?.Status, "Worker loss must interrupt the Run, not fail it.");
        AssertEqual("unknown", store.ToolCallsFor(live.RunId, "orph-task").Single().SideEffectState,
            "In-flight tool calls must become UNKNOWN.");
        AssertEqual(RuntimeError.UnknownSideEffect, service.RetryTask("orph-task").Failure?.Code,
            "UNKNOWN after worker loss must not auto-retry.");
        AssertEqual(ResumeDecisionKind.BlockUnknown, Success(service.ClassifyResume(live.RunId, Fresh())).Kind,
            "UNKNOWN after worker loss must BLOCK_UNKNOWN.");
        AssertEqual("cancelled", store.GetRun(cancelled.RunId)?.Status, "Reconcile must not revive a cancelled Run.");
        AssertTrue(!workers.IsAlive(workers.Snapshot().First(item => item.RunId == cancelled.RunId).WorkerInstanceId),
            "Cancelled Run Worker must stay released.");
    }

    private static void SecondLiveWorkerForSameRunIsRejected()
    {
        var service = CreateService(out var workers, out _);
        var workflow = Success(service.CreateWorkflowRun("wf-one"));
        var run = Success(service.CreateRun(workflow.WorkflowRunId, "writer", null, "one-run"));
        Success(service.CreateTask(run.RunId, "write", 1, null, "one-task"));
        var dispatched = Success(service.DispatchReadyTask("one-task"));
        Success(service.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId));
        var second = service.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId);
        AssertEqual(RuntimeError.WorkerLaunchFailed, second.Failure?.Code, "A second live Worker for the same Run must be rejected.");
        AssertEqual(1, workers.Snapshot().Count(item => item.Alive), "Worker process identity must not be reused or duplicated.");
    }

    private static void WorkerBoundRunCannotIssueSessionForAnotherRun()
    {
        var registry = new TrustedIpcBindingRegistry();
        registry.Register(new TrustedIpcLaunchRecord(
            AuthenticatedClientKind.Worker,
            "worker-a",
            "channel-a",
            Scope,
            "aaaaaaaaaaaaaaaa",
            "run-a"));
        var token = IpcBootstrapToken.Create();
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.Worker,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = registry,
            LaunchBindingId = "aaaaaaaaaaaaaaaa",
            RunSessions = new RunSessionService(new UnusedRunStore())
        }, async client =>
        {
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.CreateRunSession,
                    new LLMW.Writing.Contracts.Ipc.CreateRunSessionRequest("run-b", null),
                    IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                    IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("Worker A must not issue a RunSession for Run B.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.BindingMismatch, exception.ErrorCode, "Forged Worker run identity must fail closed.");
            }
        }, IpcClientKind.Worker);
    }

    private static void TrustedLaunchEnvironmentRejectsCoreBootstrap()
    {
        var rejected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LLMW_WORKER_BOOTSTRAP_TOKEN"] = "worker-secret",
            ["LLMW_CORE_BOOTSTRAP_TOKEN"] = "core-secret"
        };
        AssertTrue(SandboxEnvironmentPolicy.ValidateTrustedLaunchEnvironment(rejected) is not null,
            "Worker overlay must reject Core bootstrap credentials.");
        var missingWorker = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LLMW_WORKSPACE_INSTANCE_ID"] = "workspace-01"
        };
        AssertTrue(SandboxEnvironmentPolicy.ValidateTrustedLaunchEnvironment(missingWorker) is not null,
            "Worker overlay without a Worker bootstrap token must fail closed.");
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LLMW_WORKER_BOOTSTRAP_TOKEN"] = "worker-secret",
            ["LLMW_WORKSPACE_INSTANCE_ID"] = "workspace-01",
            ["LLMW_WORKER_INSTANCE_ID"] = "worker-1",
            ["LLMW_RUN_ID"] = "run-1",
            ["LLMW_LAUNCH_BINDING_ID"] = "aaaaaaaaaaaaaaaa",
            ["LLMW_WORKER_PIPE_NAME"] = "llmw-writing-workspace-01-w-aaaaaaaaaaaaaaaa"
        };
        AssertTrue(SandboxEnvironmentPolicy.ValidateTrustedLaunchEnvironment(allowed) is null,
            "The trusted Worker overlay names must be accepted.");
    }

    private static void ManagementCommandsRequireRuntimeChannel()
    {
        var token = IpcBootstrapToken.Create();
        var store = new MemoryRuntimeStore();
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            new FixedClock(1),
            new FakeRunWorkerSupervisor());
        RunHosted(token, new IpcServerOptions
        {
            WorkspaceInstanceId = Workspace,
            ExpectedClientKind = IpcClientKind.Ui,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            NativeUi = new TrustedNativePrincipalSource("wp12-ui"),
            Commands = new RuntimeIpcCommandHandler(scheduler, Workspace)
        }, async client =>
        {
            try
            {
                await client.RequestAsync(
                    IpcSemanticTypes.LoadSchedulerSnapshot,
                    new LoadSchedulerSnapshotRequest(null),
                    IpcJsonContext.Default.LoadSchedulerSnapshotRequestEnvelope,
                    IpcJsonContext.Default.LoadSchedulerSnapshotResponseEnvelope,
                    CancellationToken.None);
                throw new InvalidOperationException("UI must not invoke Runtime management commands.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.RuntimeManagementDenied, exception.ErrorCode, "UI Runtime management must be denied.");
            }
        }, IpcClientKind.Ui);
    }

    private static void IpcSurfaceStillCannotReachSandboxHost()
    {
        AssertTrue(!typeof(IpcServerOptions).GetProperties().Any(property =>
                typeof(ISandboxHost).IsAssignableFrom(property.PropertyType)),
            "IpcServerOptions must not expose ISandboxHost.");
        AssertTrue(!IpcSemanticTypes.All.Any(type =>
                type.Contains("sandbox", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("execute", StringComparison.OrdinalIgnoreCase)),
            "WP12 IPC must not expose sandbox execution.");
    }

    private static void DispatchAndLaunch(RuntimeSchedulerService service, string runId, string taskId)
    {
        Success(service.CreateTask(runId, "write", 1, null, taskId));
        var dispatched = Success(service.DispatchReadyTask(taskId));
        Success(service.LaunchRunWorker(dispatched.RunId, dispatched.TaskId, dispatched.AttemptId));
    }

    private static RuntimeSchedulerService CreateService(out FakeRunWorkerSupervisor workers, out AllowSpawnAuth auth) =>
        CreateService(out workers, out auth, out _);

    private static RuntimeSchedulerService CreateService(
        out FakeRunWorkerSupervisor workers,
        out AllowSpawnAuth auth,
        out MemoryRuntimeStore store)
    {
        workers = new FakeRunWorkerSupervisor();
        auth = new AllowSpawnAuth();
        store = new MemoryRuntimeStore();
        return new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            new FixedClock(1_000),
            workers,
            auth);
    }

    private static CallerPrincipal Principal() => new TrustedNativePrincipalSource("wp12-tests").ResolveUserInteractive();

    private static FreshnessInputs Fresh(bool unrelatedDraft = false, bool planInvalid = false, bool structural = false) =>
        new(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            structural,
            planInvalid,
            unrelatedDraft,
            false);

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

    private static void RunHosted(string token, IpcServerOptions options, Func<IpcClientSession, Task> test, IpcClientKind kind)
    {
        var (left, right) = IpcConnectedStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(() => IpcServerSession.ServeAsync(left, options, timeout.Token), timeout.Token);
        IpcClientSession? client = null;
        try
        {
            client = IpcClientSession.HandshakeAsync(right, Workspace, token, kind, TimeSpan.FromMilliseconds(200), timeout.Token)
                .GetAwaiter()
                .GetResult();
            test(client).GetAwaiter().GetResult();
        }
        finally
        {
            client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            timeout.Cancel();
            try
            {
                server.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException)
            {
            }

            left.Dispose();
            right.Dispose();
        }
    }

    private sealed class UnusedRunStore : IRunSessionStore
    {
        public DurableRunIdentity? LoadRun(string runId) => null;

        public StoredRunSession IssueReplacingActive(PersistRunSessionRequest request) =>
            throw new InvalidOperationException("Worker A must not issue a RunSession for another Run.");

        public StoredRunSession? FindByTokenHash(string tokenHash) => null;

        public StoredRunSession? FindByHandleId(string handleId) => null;

        public int RevokeHandle(string handleId, long revokedAtMs) => 0;

        public int RevokeByRun(string runId, long revokedAtMs) => 0;

        public int RevokeByChannelWorker(string channelInstanceId, string workerInstanceId, long revokedAtMs) => 0;
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

    private sealed class FixedClock(long unixMs) : ISecurityClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
