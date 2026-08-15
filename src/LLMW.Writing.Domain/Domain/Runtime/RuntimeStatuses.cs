namespace LLMW.Writing.Domain.Runtime;

public enum WorkflowRunStatus
{
    Created,
    Planned,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum RunStatus
{
    Created,
    Queued,
    Starting,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public enum TaskStatus
{
    Pending,
    Blocked,
    Ready,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum AttemptStatus
{
    Starting,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
    Unknown
}

public enum SideEffectState
{
    None,
    Planned,
    Completed,
    Failed,
    Unknown
}

public enum ResumeDecisionKind
{
    Continue,
    Replan,
    RestartTask,
    RestartRun,
    BlockUnknown
}

public enum SpawnOutcomeKind
{
    Allowed,
    Queued,
    Denied
}

public enum SpawnDenialReason
{
    None,
    AgentSpawnDenied,
    DepthLimit,
    Cancelled,
    DepthSpoof,
    ScopeMismatch,
    UnknownSideEffect
}

public enum WorkerReconcileClassification
{
    ActiveRunWithLiveWorker,
    ActiveRunWorkerGone,
    WorkerWithoutDurableRun,
    WorkerIdentityMismatch
}

public enum RuntimeRejectionCode
{
    IllegalTransition,
    DepthLimit,
    SpawnCapabilityDenied,
    Cancelled,
    UnknownSideEffect,
    CheckpointUnsupported,
    CheckpointCorrupt,
    DepthSpoof
}

public static class WorkflowRunStatusCodec
{
    public static string ToDurableValue(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Created => "created",
        WorkflowRunStatus.Planned => "planned",
        WorkflowRunStatus.Running => "running",
        WorkflowRunStatus.Paused => "paused",
        WorkflowRunStatus.Completed => "completed",
        WorkflowRunStatus.Failed => "failed",
        WorkflowRunStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string value, out WorkflowRunStatus status)
    {
        status = value switch
        {
            "created" => WorkflowRunStatus.Created,
            "planned" => WorkflowRunStatus.Planned,
            "running" => WorkflowRunStatus.Running,
            "paused" => WorkflowRunStatus.Paused,
            "completed" => WorkflowRunStatus.Completed,
            "failed" => WorkflowRunStatus.Failed,
            "cancelled" => WorkflowRunStatus.Cancelled,
            _ => default
        };
        return value is "created" or "planned" or "running" or "paused" or "completed" or "failed" or "cancelled";
    }
}

public static class RunStatusCodec
{
    public static string ToDurableValue(RunStatus status) => status switch
    {
        RunStatus.Created => "created",
        RunStatus.Queued => "queued",
        RunStatus.Starting => "starting",
        RunStatus.Running => "running",
        RunStatus.Paused => "paused",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        RunStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string value, out RunStatus status)
    {
        status = value switch
        {
            "created" => RunStatus.Created,
            "queued" => RunStatus.Queued,
            "starting" => RunStatus.Starting,
            "running" => RunStatus.Running,
            "paused" => RunStatus.Paused,
            "completed" => RunStatus.Completed,
            "failed" => RunStatus.Failed,
            "cancelled" => RunStatus.Cancelled,
            "interrupted" => RunStatus.Interrupted,
            _ => default
        };
        return value is "created" or "queued" or "starting" or "running" or "paused" or "completed" or "failed"
            or "cancelled" or "interrupted";
    }
}

public static class TaskStatusCodec
{
    public static string ToDurableValue(TaskStatus status) => status switch
    {
        TaskStatus.Pending => "pending",
        TaskStatus.Blocked => "blocked",
        TaskStatus.Ready => "ready",
        TaskStatus.Running => "running",
        TaskStatus.Paused => "paused",
        TaskStatus.Completed => "completed",
        TaskStatus.Failed => "failed",
        TaskStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string value, out TaskStatus status)
    {
        status = value switch
        {
            "pending" => TaskStatus.Pending,
            "blocked" => TaskStatus.Blocked,
            "ready" => TaskStatus.Ready,
            "running" => TaskStatus.Running,
            "paused" => TaskStatus.Paused,
            "completed" => TaskStatus.Completed,
            "failed" => TaskStatus.Failed,
            "cancelled" => TaskStatus.Cancelled,
            _ => default
        };
        return value is "pending" or "blocked" or "ready" or "running" or "paused" or "completed" or "failed"
            or "cancelled";
    }
}

public static class AttemptStatusCodec
{
    public static string ToDurableValue(AttemptStatus status) => status switch
    {
        AttemptStatus.Starting => "starting",
        AttemptStatus.Running => "running",
        AttemptStatus.Completed => "completed",
        AttemptStatus.Failed => "failed",
        AttemptStatus.Cancelled => "cancelled",
        AttemptStatus.Interrupted => "interrupted",
        AttemptStatus.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static bool TryParse(string value, out AttemptStatus status)
    {
        status = value switch
        {
            "starting" => AttemptStatus.Starting,
            "running" => AttemptStatus.Running,
            "completed" => AttemptStatus.Completed,
            "failed" => AttemptStatus.Failed,
            "cancelled" => AttemptStatus.Cancelled,
            "interrupted" => AttemptStatus.Interrupted,
            "unknown" => AttemptStatus.Unknown,
            _ => default
        };
        return value is "starting" or "running" or "completed" or "failed" or "cancelled" or "interrupted" or "unknown";
    }
}

public static class SideEffectStateCodec
{
    public static string ToDurableValue(SideEffectState state) => state switch
    {
        SideEffectState.None => "none",
        SideEffectState.Planned => "planned",
        SideEffectState.Completed => "completed",
        SideEffectState.Failed => "failed",
        SideEffectState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static bool TryParse(string value, out SideEffectState state)
    {
        state = value switch
        {
            "none" => SideEffectState.None,
            "planned" => SideEffectState.Planned,
            "completed" => SideEffectState.Completed,
            "failed" => SideEffectState.Failed,
            "unknown" => SideEffectState.Unknown,
            _ => default
        };
        return value is "none" or "planned" or "completed" or "failed" or "unknown";
    }
}

public static class ResumeDecisionCodec
{
    public static string ToDurableValue(ResumeDecisionKind kind) => kind switch
    {
        ResumeDecisionKind.Continue => "CONTINUE",
        ResumeDecisionKind.Replan => "REPLAN",
        ResumeDecisionKind.RestartTask => "RESTART_TASK",
        ResumeDecisionKind.RestartRun => "RESTART_RUN",
        ResumeDecisionKind.BlockUnknown => "BLOCK_UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
