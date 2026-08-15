namespace LLMW.Writing.Contracts.Ipc;

public sealed record LoadSchedulerSnapshotRequest(string? WorkflowRunId);

public sealed record RuntimeWorkflowDto(string WorkflowRunId, string Status, long CreatedAtMs, long UpdatedAtMs);

public sealed record RuntimeRunDto(
    string RunId,
    string WorkflowRunId,
    string? ParentRunId,
    string Role,
    string Status,
    int Depth,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record RuntimeTaskDto(
    string TaskId,
    string RunId,
    string? ParentTaskId,
    string TaskKind,
    string Status,
    int Priority,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record RuntimeAttemptDto(
    string AttemptId,
    string TaskId,
    int AttemptNo,
    string Status,
    long StartedAtMs,
    long? CompletedAtMs);

public sealed record RuntimeDependencyDto(
    string DependencyId,
    string ConsumerTaskId,
    string ProducerTaskId,
    string DependencyKind,
    string Status);

public sealed record RuntimeToolCallDto(
    string ToolCallId,
    string RunId,
    string? TaskId,
    string ToolName,
    string Status,
    string SideEffectState);

public sealed record RuntimeCheckpointDto(
    string CheckpointId,
    string RunId,
    string? TaskId,
    int SchemaVersion,
    long CreatedAtMs);

public sealed record SchedulerSnapshotDto(
    RuntimeWorkflowDto[] WorkflowRuns,
    RuntimeRunDto[] Runs,
    RuntimeTaskDto[] Tasks,
    RuntimeAttemptDto[] Attempts,
    RuntimeDependencyDto[] Dependencies,
    RuntimeToolCallDto[] ToolCalls,
    RuntimeCheckpointDto[] Checkpoints,
    string[] ReadyTaskIds,
    string[] BlockedTaskIds,
    int ActiveRunCount,
    int EffectiveBudget);

public sealed record LoadSchedulerSnapshotResponse(SchedulerSnapshotDto Snapshot);

public sealed record CreateWorkflowRunRequest(string? StorylineId);

public sealed record CreateWorkflowRunResponse(string WorkflowRunId, string Status);

public sealed record CreateRunRequest(string WorkflowRunId, string Role, string? ParentRunId, string? RunId);

public sealed record CreateRunResponse(string RunId, int Depth, string Status);

public sealed record CreateTaskRequest(string RunId, string TaskKind, int Priority, string? ParentTaskId, string? TaskId);

public sealed record CreateTaskResponse(string TaskId, string Status);

public sealed record DispatchReadyTaskRequest(string TaskId);

public sealed record DispatchReadyTaskResponse(
    string TaskId,
    string RunId,
    string AttemptId,
    int AttemptNo,
    string Outcome);

public sealed record CancelRuntimeScopeRequest(string ScopeKind, string ScopeId);

public sealed record CancelRuntimeScopeResponse(bool Cancelled, string[] AffectedRunIds);

public sealed record RetryTaskRequest(string TaskId);

public sealed record RetryTaskResponse(string TaskId, string AttemptId, int AttemptNo, string Outcome);

public sealed record PersistCheckpointRequest(
    string RunId,
    string? TaskId,
    int SchemaVersion,
    string PayloadJson,
    string InputDigestSetJson);

public sealed record PersistCheckpointResponse(string CheckpointId);

public sealed record ClassifyResumeRequest(
    string RunId,
    bool UnrelatedDraftOnly,
    bool PlanInvalid,
    bool StructuralInvalidation);

public sealed record ClassifyResumeResponse(string Decision, string Reason, string? CheckpointId);

public sealed record LaunchRunWorkerRequest(string RunId, string TaskId, string AttemptId);

public sealed record LaunchRunWorkerResponse(string WorkerInstanceId, string LaunchBindingId, string Outcome);

public sealed record ReleaseRunWorkerRequest(string WorkerInstanceId);

public sealed record ReleaseRunWorkerResponse(bool Released);

public sealed record ReconcileRunWorkersRequest();

public sealed record WorkerReconcileDto(
    string Classification,
    string? RunId,
    string? WorkerInstanceId);

public sealed record ReconcileRunWorkersResponse(WorkerReconcileDto[] Items);

public sealed record SpawnChildRunRequest(
    string ParentRunId,
    string ParentTaskId,
    string Role,
    int? RequestedDepth,
    RunSessionProof? Session);

public sealed record SpawnChildRunResponse(string Outcome, string? ChildRunId, int? Depth, string? Reason);
