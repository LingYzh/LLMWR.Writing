using System.IO.Pipes;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private const string Wp12DescriptorProjectId = "018f3e78-1234-7abc-8def-0123456789ab";

    private static async Task RunWp12TestsAsync()
    {
        await SchedulerIpcDispatchesFourAndQueuesFifthAsync();
        await ForgedWorkerBindingCannotSelectAnotherRunAsync();
        await ProductionOpenProjectBindsDescriptorIdentityAndCanonicalDbAsync();
        await ProductionOpenProjectRejectsEmptyDirectoryWithoutMutationAsync();
        Console.WriteLine("WP12 scheduler integration tests passed (4).");
    }

    private static async Task SchedulerIpcDispatchesFourAndQueuesFifthAsync()
    {
        var token = IpcBootstrapToken.Create();
        var bindings = new TrustedIpcBindingRegistry();
        var scope = new ProjectScope(Guid.Parse(Wp12DescriptorProjectId), "wp12-int");
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

        try
        {
            await client.RequestAsync(
                IpcSemanticTypes.CreateRun,
                new CreateRunRequest(workflow.Payload.WorkflowRunId, "writer", "int-run-0", "int-child"),
                IpcJsonContext.Default.CreateRunRequestEnvelope,
                IpcJsonContext.Default.CreateRunResponseEnvelope,
                timeout.Token);
            throw new InvalidOperationException("IPC createRun(parentRunId) must be denied.");
        }
        catch (IpcProtocolException exception)
        {
            AssertEqual(IpcErrorCodes.AgentSpawnDenied, exception.ErrorCode, "Runtime management createRun(parentRunId) must be denied.");
        }

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
        var scope = new ProjectScope(Guid.Parse(Wp12DescriptorProjectId), "wp12-int");
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-a", "channel-a", scope, "aaaaaaaaaaaaaaaa", "run-a"));
        registry.Register(new TrustedIpcLaunchRecord(AuthenticatedClientKind.Worker, "worker-b", "channel-b", scope, "bbbbbbbbbbbbbbbb", "run-b"));
        AssertTrue(registry.TryBind("aaaaaaaaaaaaaaaa", AuthenticatedClientKind.Worker, out var a), "Worker A missing.");
        AssertTrue(!StringComparer.Ordinal.Equals(a.WorkerInstanceId, "worker-b"), "Forged Worker A became Worker B.");
        AssertTrue(!registry.TryBind("aaaaaaaaaaaaaaaa", AuthenticatedClientKind.AgentRuntime, out _), "Worker record bound as Runtime.");
        await Task.CompletedTask;
    }

    private static async Task ProductionOpenProjectBindsDescriptorIdentityAndCanonicalDbAsync()
    {
        var root = CreateValidProjectFixture();
        var workspaceInstanceId = "wp12open" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        var runtimeToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var core = StartCore(workspaceInstanceId, uiToken, runtimeToken);
        try
        {
            await using var runtime = await ConnectAndHandshakeAsync(
                IpcPipeNames.Runtime(workspaceInstanceId),
                workspaceInstanceId,
                runtimeToken,
                IpcClientKind.AgentRuntime,
                timeout.Token);
            try
            {
                await runtime.RequestAsync(
                    IpcSemanticTypes.LoadSchedulerSnapshot,
                    new LoadSchedulerSnapshotRequest(null),
                    IpcJsonContext.Default.LoadSchedulerSnapshotRequestEnvelope,
                    IpcJsonContext.Default.LoadSchedulerSnapshotResponseEnvelope,
                    timeout.Token);
                throw new InvalidOperationException("Runtime management must be unavailable before existing-project preflight.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.TrustedBindingUnavailable, exception.ErrorCode,
                    "Trusted Runtime binding must be created only after full preflight.");
            }

            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            var opened = await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token);
            AssertEqual(Wp12DescriptorProjectId, opened.Payload.ProjectId, "OpenProject must return descriptor ProjectId, not a path hash.");
            AssertTrue(!File.Exists(Path.Combine(root, "project.db")), "Production OpenProject must not create root/project.db.");

            var snapshot = await runtime.RequestAsync(
                IpcSemanticTypes.LoadSchedulerSnapshot,
                new LoadSchedulerSnapshotRequest(null),
                IpcJsonContext.Default.LoadSchedulerSnapshotRequestEnvelope,
                IpcJsonContext.Default.LoadSchedulerSnapshotResponseEnvelope,
                timeout.Token);
            AssertEqual(4, snapshot.Payload.Snapshot.EffectiveBudget, "Scheduler must be bound after OpenProject.");

            var workflow = await runtime.RequestAsync(
                IpcSemanticTypes.CreateWorkflowRun,
                new CreateWorkflowRunRequest(null),
                IpcJsonContext.Default.CreateWorkflowRunRequestEnvelope,
                IpcJsonContext.Default.CreateWorkflowRunResponseEnvelope,
                timeout.Token);
            var sqlite = new SqliteRuntimeStore(Path.Combine(root, ".llmw", "project.db"));
            AssertTrue(sqlite.LoadSnapshot().WorkflowRuns.Any(item => item.WorkflowRunId == workflow.Payload.WorkflowRunId),
                "Scheduler must read and write the SAME .llmw/project.db.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ProductionOpenProjectRejectsEmptyDirectoryWithoutMutationAsync()
    {
        var empty = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP12", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        var workspaceInstanceId = "wp12empty" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        var runtimeToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var core = StartCore(workspaceInstanceId, uiToken, runtimeToken);
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            try
            {
                await ui.RequestAsync(
                    IpcSemanticTypes.OpenProject,
                    new OpenProjectRequest(empty),
                    IpcJsonContext.Default.OpenProjectRequestEnvelope,
                    IpcJsonContext.Default.OpenProjectResponseEnvelope,
                    timeout.Token);
                throw new InvalidOperationException("Empty directory OpenProject must be rejected.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.BindingMismatch, exception.ErrorCode, "Arbitrary directory OpenProject must fail closed.");
            }

            AssertTrue(!File.Exists(Path.Combine(empty, "project.db")), "Rejected OpenProject must not create root project.db.");
            AssertTrue(!File.Exists(Path.Combine(empty, ".llmw", "project.db")), "Rejected OpenProject must not create .llmw/project.db.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(empty))
            {
                Directory.Delete(empty, recursive: true);
            }
        }
    }

    private static string CreateValidProjectFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP12", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".llmw"));
        File.WriteAllText(
            Path.Combine(root, "project.llmw.json"),
            "{\"projectId\":\"" + Wp12DescriptorProjectId + "\",\"formatVersion\":1,\"schemaVersion\":1}");
        new SqliteMigrationRunner().Migrate(Path.Combine(root, ".llmw", "project.db"), "wp12-int", 1735689600000);
        return root;
    }

    private static async Task<IpcClientSession> ConnectAndHandshakeAsync(
        string pipeName,
        string workspaceInstanceId,
        string bootstrapToken,
        IpcClientKind kind,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(400), cancellationToken);
                return await IpcClientSession.HandshakeAsync(
                    client,
                    workspaceInstanceId,
                    bootstrapToken,
                    kind,
                    TimeSpan.FromMilliseconds(200),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or IpcProtocolException)
            {
                last = exception;
                await client.DisposeAsync();
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new InvalidOperationException("Timed out connecting to " + pipeName + ": " + last);
    }
}
