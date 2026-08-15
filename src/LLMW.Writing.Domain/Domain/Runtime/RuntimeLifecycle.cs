namespace LLMW.Writing.Domain.Runtime;

public sealed record RuntimeTransition<TStatus>(
    bool Allowed,
    TStatus Current,
    TStatus? Next,
    RuntimeRejectionCode? Rejection)
    where TStatus : struct, Enum;

public static class RuntimeLifecycle
{
    public static RuntimeTransition<WorkflowRunStatus> Transition(WorkflowRunStatus current, WorkflowRunStatus next)
        => Allow(current, next, IsLegal(current, next));

    public static RuntimeTransition<RunStatus> Transition(RunStatus current, RunStatus next)
        => Allow(current, next, IsLegal(current, next));

    public static RuntimeTransition<TaskStatus> Transition(TaskStatus current, TaskStatus next)
        => Allow(current, next, IsLegal(current, next));

    public static RuntimeTransition<AttemptStatus> Transition(AttemptStatus current, AttemptStatus next)
        => Allow(current, next, IsLegal(current, next));

    public static bool IsTerminal(WorkflowRunStatus status) =>
        status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled;

    public static bool IsTerminal(RunStatus status) =>
        status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;

    public static bool IsActive(RunStatus status) =>
        status is RunStatus.Starting or RunStatus.Running or RunStatus.Paused;

    public static bool IsDispatchOccupying(RunStatus status) =>
        status is RunStatus.Starting or RunStatus.Running;

    public static bool IsTerminal(TaskStatus status) =>
        status is TaskStatus.Completed or TaskStatus.Cancelled;

    public static bool CanRetryTask(TaskStatus status) =>
        status is TaskStatus.Failed or TaskStatus.Paused;

    public static bool IsLegal(WorkflowRunStatus current, WorkflowRunStatus next) => (current, next) switch
    {
        (WorkflowRunStatus.Created, WorkflowRunStatus.Planned) => true,
        (WorkflowRunStatus.Created, WorkflowRunStatus.Running) => true,
        (WorkflowRunStatus.Created, WorkflowRunStatus.Cancelled) => true,
        (WorkflowRunStatus.Planned, WorkflowRunStatus.Running) => true,
        (WorkflowRunStatus.Planned, WorkflowRunStatus.Cancelled) => true,
        (WorkflowRunStatus.Running, WorkflowRunStatus.Paused) => true,
        (WorkflowRunStatus.Running, WorkflowRunStatus.Completed) => true,
        (WorkflowRunStatus.Running, WorkflowRunStatus.Failed) => true,
        (WorkflowRunStatus.Running, WorkflowRunStatus.Cancelled) => true,
        (WorkflowRunStatus.Paused, WorkflowRunStatus.Running) => true,
        (WorkflowRunStatus.Paused, WorkflowRunStatus.Cancelled) => true,
        (WorkflowRunStatus.Paused, WorkflowRunStatus.Failed) => true,
        _ => false
    };

    public static bool IsLegal(RunStatus current, RunStatus next) => (current, next) switch
    {
        (RunStatus.Created, RunStatus.Queued) => true,
        (RunStatus.Created, RunStatus.Starting) => true,
        (RunStatus.Created, RunStatus.Cancelled) => true,
        (RunStatus.Queued, RunStatus.Starting) => true,
        (RunStatus.Queued, RunStatus.Cancelled) => true,
        (RunStatus.Starting, RunStatus.Running) => true,
        (RunStatus.Starting, RunStatus.Failed) => true,
        (RunStatus.Starting, RunStatus.Cancelled) => true,
        (RunStatus.Starting, RunStatus.Interrupted) => true,
        (RunStatus.Running, RunStatus.Paused) => true,
        (RunStatus.Running, RunStatus.Completed) => true,
        (RunStatus.Running, RunStatus.Failed) => true,
        (RunStatus.Running, RunStatus.Cancelled) => true,
        (RunStatus.Running, RunStatus.Interrupted) => true,
        (RunStatus.Paused, RunStatus.Running) => true,
        (RunStatus.Paused, RunStatus.Cancelled) => true,
        (RunStatus.Paused, RunStatus.Failed) => true,
        (RunStatus.Interrupted, RunStatus.Starting) => true,
        (RunStatus.Interrupted, RunStatus.Cancelled) => true,
        (RunStatus.Interrupted, RunStatus.Failed) => true,
        _ => false
    };

    public static bool IsLegal(TaskStatus current, TaskStatus next) => (current, next) switch
    {
        (TaskStatus.Pending, TaskStatus.Blocked) => true,
        (TaskStatus.Pending, TaskStatus.Ready) => true,
        (TaskStatus.Pending, TaskStatus.Cancelled) => true,
        (TaskStatus.Blocked, TaskStatus.Ready) => true,
        (TaskStatus.Blocked, TaskStatus.Cancelled) => true,
        (TaskStatus.Blocked, TaskStatus.Failed) => true,
        (TaskStatus.Ready, TaskStatus.Running) => true,
        (TaskStatus.Ready, TaskStatus.Blocked) => true,
        (TaskStatus.Ready, TaskStatus.Cancelled) => true,
        (TaskStatus.Running, TaskStatus.Paused) => true,
        (TaskStatus.Running, TaskStatus.Completed) => true,
        (TaskStatus.Running, TaskStatus.Failed) => true,
        (TaskStatus.Running, TaskStatus.Cancelled) => true,
        (TaskStatus.Paused, TaskStatus.Ready) => true,
        (TaskStatus.Paused, TaskStatus.Running) => true,
        (TaskStatus.Paused, TaskStatus.Cancelled) => true,
        (TaskStatus.Failed, TaskStatus.Ready) => true,
        (TaskStatus.Failed, TaskStatus.Cancelled) => true,
        _ => false
    };

    public static bool IsLegal(AttemptStatus current, AttemptStatus next) => (current, next) switch
    {
        (AttemptStatus.Starting, AttemptStatus.Running) => true,
        (AttemptStatus.Starting, AttemptStatus.Failed) => true,
        (AttemptStatus.Starting, AttemptStatus.Cancelled) => true,
        (AttemptStatus.Starting, AttemptStatus.Interrupted) => true,
        (AttemptStatus.Starting, AttemptStatus.Unknown) => true,
        (AttemptStatus.Running, AttemptStatus.Completed) => true,
        (AttemptStatus.Running, AttemptStatus.Failed) => true,
        (AttemptStatus.Running, AttemptStatus.Cancelled) => true,
        (AttemptStatus.Running, AttemptStatus.Interrupted) => true,
        (AttemptStatus.Running, AttemptStatus.Unknown) => true,
        _ => false
    };

    private static RuntimeTransition<TStatus> Allow<TStatus>(TStatus current, TStatus next, bool legal)
        where TStatus : struct, Enum =>
        legal
            ? new RuntimeTransition<TStatus>(true, current, next, null)
            : new RuntimeTransition<TStatus>(false, current, null, RuntimeRejectionCode.IllegalTransition);
}
