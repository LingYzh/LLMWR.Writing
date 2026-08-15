using System.Text;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Contracts.Tests;

internal static class Wp12ContractTests
{
    public static void Run()
    {
        Wp12SemanticTypesAreKnownRequestResponseAndNotReplaySafe();
        WorkerPipeNamesAreLaunchBindingSpecific();
        GoldenJsonForWp12Dtos();
        SpawnRequestDoesNotCarryPrincipalSelectors();
        Console.WriteLine("WP12 contract tests passed.");
    }

    private static void Wp12SemanticTypesAreKnownRequestResponseAndNotReplaySafe()
    {
        string[] types =
        [
            IpcSemanticTypes.LoadSchedulerSnapshot,
            IpcSemanticTypes.CreateWorkflowRun,
            IpcSemanticTypes.CreateRun,
            IpcSemanticTypes.CreateTask,
            IpcSemanticTypes.DispatchReadyTask,
            IpcSemanticTypes.CancelRuntimeScope,
            IpcSemanticTypes.RetryTask,
            IpcSemanticTypes.PersistCheckpoint,
            IpcSemanticTypes.ClassifyResume,
            IpcSemanticTypes.LaunchRunWorker,
            IpcSemanticTypes.ReleaseRunWorker,
            IpcSemanticTypes.ReconcileRunWorkers,
            IpcSemanticTypes.SpawnChildRun
        ];
        foreach (var type in types)
        {
            Program.AssertTrue(IpcSemanticTypes.IsKnown(type), type + " must be known.");
            Program.AssertTrue(IpcSemanticTypes.IsWellFormed(type, IpcMessageType.Request), type + " must be a request.");
            Program.AssertTrue(IpcSemanticTypes.IsWellFormed(type, IpcMessageType.Response), type + " must be a response.");
            Program.AssertTrue(!IpcSemanticTypes.IsSafeToReplayAfterReconnect(type), type + " must not auto-replay.");
            Program.AssertTrue(!type.Contains("sandbox", StringComparison.OrdinalIgnoreCase), "IPC must not expose sandbox.");
            Program.AssertTrue(!type.Contains("shell", StringComparison.OrdinalIgnoreCase), "IPC must not expose shell.");
            Program.AssertTrue(!type.Contains("execute", StringComparison.OrdinalIgnoreCase), "IPC must not expose execute.");
        }
    }

    private static void WorkerPipeNamesAreLaunchBindingSpecific()
    {
        Program.AssertEqual(
            "llmw-writing-workspace-01-w-0123456789abcdef",
            IpcPipeNames.Worker("workspace-01", "0123456789abcdef"),
            "Worker pipe name drifted.");
        Program.AssertTrue(
            !StringComparer.Ordinal.Equals(
                IpcPipeNames.Worker("workspace-01", "0123456789abcdef"),
                IpcPipeNames.Worker("workspace-01", "0123456789abcdee")),
            "Distinct launch bindings must not share a Worker pipe.");
    }

    private static void GoldenJsonForWp12Dtos()
    {
        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.LoadSchedulerSnapshot, new LoadSchedulerSnapshotRequest(null)),
            IpcJsonContext.Default.LoadSchedulerSnapshotRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"loadSchedulerSnapshot\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{}}");

        var snapshot = new SchedulerSnapshotDto([], [], [], [], [], [], [], [], [], 0, 4);
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.LoadSchedulerSnapshot, new LoadSchedulerSnapshotResponse(snapshot)),
            IpcJsonContext.Default.LoadSchedulerSnapshotResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"loadSchedulerSnapshot\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"snapshot\":{\"workflowRuns\":[],\"runs\":[],\"tasks\":[],\"attempts\":[],\"dependencies\":[],\"toolCalls\":[],\"checkpoints\":[],\"readyTaskIds\":[],\"blockedTaskIds\":[],\"activeRunCount\":0,\"effectiveBudget\":4}}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateWorkflowRun, new CreateWorkflowRunRequest(null)),
            IpcJsonContext.Default.CreateWorkflowRunRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"createWorkflowRun\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CreateWorkflowRun, new CreateWorkflowRunResponse("wf-1", "created")),
            IpcJsonContext.Default.CreateWorkflowRunResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"createWorkflowRun\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"workflowRunId\":\"wf-1\",\"status\":\"created\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateRun, new CreateRunRequest("wf-1", "pm", null, null)),
            IpcJsonContext.Default.CreateRunRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"createRun\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"workflowRunId\":\"wf-1\",\"role\":\"pm\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CreateRun, new CreateRunResponse("run-1", 0, "created")),
            IpcJsonContext.Default.CreateRunResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"createRun\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"runId\":\"run-1\",\"depth\":0,\"status\":\"created\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CreateTask, new CreateTaskRequest("run-1", "write", 1, null, null)),
            IpcJsonContext.Default.CreateTaskRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"createTask\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"runId\":\"run-1\",\"taskKind\":\"write\",\"priority\":1}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CreateTask, new CreateTaskResponse("task-1", "ready")),
            IpcJsonContext.Default.CreateTaskResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"createTask\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"taskId\":\"task-1\",\"status\":\"ready\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.DispatchReadyTask, new DispatchReadyTaskRequest("task-1")),
            IpcJsonContext.Default.DispatchReadyTaskRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"dispatchReadyTask\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"taskId\":\"task-1\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.DispatchReadyTask, new DispatchReadyTaskResponse("task-1", "run-1", "attempt-1", 1, "dispatched")),
            IpcJsonContext.Default.DispatchReadyTaskResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"dispatchReadyTask\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"taskId\":\"task-1\",\"runId\":\"run-1\",\"attemptId\":\"attempt-1\",\"attemptNo\":1,\"outcome\":\"dispatched\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.CancelRuntimeScope, new CancelRuntimeScopeRequest("run", "run-1")),
            IpcJsonContext.Default.CancelRuntimeScopeRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"cancelRuntimeScope\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"scopeKind\":\"run\",\"scopeId\":\"run-1\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.CancelRuntimeScope, new CancelRuntimeScopeResponse(true, ["run-1"])),
            IpcJsonContext.Default.CancelRuntimeScopeResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"cancelRuntimeScope\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"cancelled\":true,\"affectedRunIds\":[\"run-1\"]}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.RetryTask, new RetryTaskRequest("task-1")),
            IpcJsonContext.Default.RetryTaskRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"retryTask\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"taskId\":\"task-1\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.RetryTask, new RetryTaskResponse("task-1", "attempt-2", 2, "retried")),
            IpcJsonContext.Default.RetryTaskResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"retryTask\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"taskId\":\"task-1\",\"attemptId\":\"attempt-2\",\"attemptNo\":2,\"outcome\":\"retried\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.PersistCheckpoint, new PersistCheckpointRequest("run-1", "task-1", 1, "{}", "{}")),
            IpcJsonContext.Default.PersistCheckpointRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"persistCheckpoint\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"runId\":\"run-1\",\"taskId\":\"task-1\",\"schemaVersion\":1,\"payloadJson\":\"{}\",\"inputDigestSetJson\":\"{}\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.PersistCheckpoint, new PersistCheckpointResponse("cp-1")),
            IpcJsonContext.Default.PersistCheckpointResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"persistCheckpoint\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"checkpointId\":\"cp-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ClassifyResume, new ClassifyResumeRequest("run-1", false, false, false)),
            IpcJsonContext.Default.ClassifyResumeRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"classifyResume\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"runId\":\"run-1\",\"unrelatedDraftOnly\":false,\"planInvalid\":false,\"structuralInvalidation\":false}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ClassifyResume, new ClassifyResumeResponse("CONTINUE", "unchanged", "cp-1")),
            IpcJsonContext.Default.ClassifyResumeResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"classifyResume\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"decision\":\"CONTINUE\",\"reason\":\"unchanged\",\"checkpointId\":\"cp-1\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.LaunchRunWorker, new LaunchRunWorkerRequest("run-1", "task-1", "attempt-1")),
            IpcJsonContext.Default.LaunchRunWorkerRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"launchRunWorker\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"runId\":\"run-1\",\"taskId\":\"task-1\",\"attemptId\":\"attempt-1\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.LaunchRunWorker, new LaunchRunWorkerResponse("worker-1", "0123456789abcdef", "launched")),
            IpcJsonContext.Default.LaunchRunWorkerResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"launchRunWorker\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"workerInstanceId\":\"worker-1\",\"launchBindingId\":\"0123456789abcdef\",\"outcome\":\"launched\"}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ReleaseRunWorker, new ReleaseRunWorkerRequest("worker-1")),
            IpcJsonContext.Default.ReleaseRunWorkerRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"releaseRunWorker\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"workerInstanceId\":\"worker-1\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ReleaseRunWorker, new ReleaseRunWorkerResponse(true)),
            IpcJsonContext.Default.ReleaseRunWorkerResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"releaseRunWorker\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"released\":true}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.ReconcileRunWorkers, new ReconcileRunWorkersRequest()),
            IpcJsonContext.Default.ReconcileRunWorkersRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"reconcileRunWorkers\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.ReconcileRunWorkers, new ReconcileRunWorkersResponse([new WorkerReconcileDto("activeRunWorkerGone", "run-1", "worker-1")])),
            IpcJsonContext.Default.ReconcileRunWorkersResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"reconcileRunWorkers\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"items\":[{\"classification\":\"activeRunWorkerGone\",\"runId\":\"run-1\",\"workerInstanceId\":\"worker-1\"}]}}");

        AssertGolden(
            Program.Envelope(IpcMessageType.Request, IpcSemanticTypes.SpawnChildRun, new SpawnChildRunRequest("run-1", "task-1", "writer", null, null)),
            IpcJsonContext.Default.SpawnChildRunRequestEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"request\",\"semanticType\":\"spawnChildRun\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"parentRunId\":\"run-1\",\"parentTaskId\":\"task-1\",\"role\":\"writer\"}}");
        AssertGolden(
            Program.Envelope(IpcMessageType.Response, IpcSemanticTypes.SpawnChildRun, new SpawnChildRunResponse("queued", null, 1, null)),
            IpcJsonContext.Default.SpawnChildRunResponseEnvelope,
            "{\"protocolVersion\":1,\"messageType\":\"response\",\"semanticType\":\"spawnChildRun\",\"requestId\":\"018f3e78-1234-7abc-8def-0123456789ab\",\"correlationId\":\"018f3e78-1234-7abc-8def-0123456789ac\",\"projectId\":\"018f3e78-1234-7abc-8def-0123456789ad\",\"workspaceInstanceId\":\"workspace-01\",\"timestampMs\":1735689600000,\"payload\":{\"outcome\":\"queued\",\"depth\":1}}");
    }

    private static void SpawnRequestDoesNotCarryPrincipalSelectors()
    {
        var json = Encoding.UTF8.GetString(IpcJson.Serialize(
            Program.Envelope(
                IpcMessageType.Request,
                IpcSemanticTypes.SpawnChildRun,
                new SpawnChildRunRequest("run-1", "task-1", "writer", 1, new RunSessionProof("run-1", "secret"))),
            IpcJsonContext.Default.SpawnChildRunRequestEnvelope));
        Program.AssertTrue(!json.Contains("principalKind", StringComparison.OrdinalIgnoreCase), "Spawn must not select a principal kind.");
        Program.AssertTrue(!json.Contains("coreInternal", StringComparison.OrdinalIgnoreCase), "Spawn must not select CORE_INTERNAL.");
        Program.AssertTrue(!json.Contains("capability", StringComparison.OrdinalIgnoreCase), "Spawn must not serialize capability.");
    }

    private static void AssertGolden<T>(
        IpcEnvelope<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<T>> typeInfo,
        string expected)
    {
        var actual = Encoding.UTF8.GetString(IpcJson.Serialize(envelope, typeInfo));
        Program.AssertEqual(expected, actual, envelope.SemanticType + " golden JSON changed. Actual=" + actual);
        var roundTrip = IpcJson.Deserialize(IpcJson.GetBytes(actual), typeInfo);
        Program.AssertEqual(envelope.SemanticType, roundTrip.SemanticType, envelope.SemanticType + " lost discriminator.");
    }
}
