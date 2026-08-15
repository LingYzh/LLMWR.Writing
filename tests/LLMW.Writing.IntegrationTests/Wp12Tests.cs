using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static async Task RunWp12TestsAsync()
    {
        await SchedulerIpcDispatchesFourAndQueuesFifthAsync();
        await ForgedWorkerBindingCannotSelectAnotherRunAsync();
        Console.WriteLine("WP12 scheduler integration tests passed (2).");
    }

    private static async Task SchedulerIpcDispatchesFourAndQueuesFifthAsync()
    {
        var token = IpcBootstrapToken.Create();
        var bindings = new TrustedIpcBindingRegistry();
        var scope = new ProjectScope(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), "wp12-int");
        bindings.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.AgentRuntime, "runtime-1", "runtime-ch", scope));
        var store = new MemoryRuntimeStore();
        var workers = new FakeRunWorkerSupervisor();
        var scheduler = new RuntimeSchedulerService(
            store,
            new FixedConcurrencyBudgetPolicy(ConcurrencyBudget.Default),
            SystemSecurityClock.Instance,
            workers);
        var options = new IpcServerOptions
        {
            WorkspaceInstanceId = "wp12-int",
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(token),
            EventRing = new IpcEventRing(Guid.NewGuid().ToString("D")),
            Bindings = bindings,
            Commands = new RuntimeIpcCommandHandler(scheduler, "wp12-int")
        };
        var (left, right) = IpcConnectedStreamPair.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(() => IpcServerSession.ServeAsync(left, options, timeout.Token), timeout.Token);
        await using var client = await IpcClientSession.HandshakeAsync(
            right, "wp12-int", token, IpcClientKind.AgentRuntime, TimeSpan.FromMilliseconds(200), timeout.Token);
        var workflow = await client.RequestAsync(
            IpcSemanticTypes.CreateWorkflowRun,
            new CreateWorkflowRunRequest(null),
            IpcJsonContext.Default.CreateWorkflowRunRequestEnvelope,
            IpcJsonContext.Default.CreateWorkflowRunResponseEnvelope,
            timeout.Token);
        for (var index = 0; index < 5; index++)
        {
            var run = await client.RequestAsync(
                IpcSemanticTypes.CreateRun,
                new CreateRunRequest(workflow.Payload.WorkflowRunId, "writer", null, "int-run-" + index),
                IpcJsonContext.Default.CreateRunRequestEnvelope,
                IpcJsonContext.Default.CreateRunResponseEnvelope,
                timeout.Token);
            await client.RequestAsync(
                IpcSemanticTypes.CreateTask,
                new CreateTaskRequest(run.Payload.RunId, "write", 1, null, "int-task-" + index),
                IpcJsonContext.Default.CreateTaskRequestEnvelope,
                IpcJsonContext.Default.CreateTaskResponseEnvelope,
                timeout.Token);
        }

        for (var index = 0; index < 4; index++)
        {
            var dispatched = await client.RequestAsync(
                IpcSemanticTypes.DispatchReadyTask,
                new DispatchReadyTaskRequest("int-task-" + index),
                IpcJsonContext.Default.DispatchReadyTaskRequestEnvelope,
                IpcJsonContext.Default.DispatchReadyTaskResponseEnvelope,
                timeout.Token);
            AssertEqual("dispatched", dispatched.Payload.Outcome, "First four IPC dispatches must succeed.");
            await client.RequestAsync(
                IpcSemanticTypes.LaunchRunWorker,
                new LaunchRunWorkerRequest(dispatched.Payload.RunId, dispatched.Payload.TaskId, dispatched.Payload.AttemptId),
                IpcJsonContext.Default.LaunchRunWorkerRequestEnvelope,
                IpcJsonContext.Default.LaunchRunWorkerResponseEnvelope,
                timeout.Token);
        }

        var fifth = await client.RequestAsync(
            IpcSemanticTypes.DispatchReadyTask,
            new DispatchReadyTaskRequest("int-task-4"),
            IpcJsonContext.Default.DispatchReadyTaskRequestEnvelope,
            IpcJsonContext.Default.DispatchReadyTaskResponseEnvelope,
            timeout.Token);
        AssertEqual("queued", fifth.Payload.Outcome, "Fifth IPC dispatch must queue.");
        AssertEqual(4, workers.Snapshot().Count(item => item.Alive), "IPC launch must keep one Worker per Run, max 4.");
        timeout.Cancel();
        try
        {
            await server.ConfigureAwait(false);
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

    private static async Task ForgedWorkerBindingCannotSelectAnotherRunAsync()
    {
        var registry = new TrustedIpcBindingRegistry();
        var scope = new ProjectScope(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), "wp12-int");
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-a", "channel-a", scope, "aaaaaaaaaaaaaaaa", "run-a"));
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-b", "channel-b", scope, "bbbbbbbbbbbbbbbb", "run-b"));
        AssertTrue(registry.TryBind("aaaaaaaaaaaaaaaa", AuthenticatedClientKind.Worker, out var a), "Worker A missing.");
        AssertTrue(!StringComparer.Ordinal.Equals(a.WorkerInstanceId, "worker-b"), "Forged Worker A became Worker B.");
        AssertTrue(!registry.TryBind("aaaaaaaaaaaaaaaa", AuthenticatedClientKind.AgentRuntime, out _), "Worker record bound as Runtime.");
        await Task.CompletedTask;
    }
}
