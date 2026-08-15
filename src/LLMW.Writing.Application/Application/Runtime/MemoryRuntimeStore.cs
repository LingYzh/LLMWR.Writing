using LLMW.Writing.Domain.Runtime;

namespace LLMW.Writing.Application.Runtime;

public sealed class MemoryRuntimeStore : IRuntimePersistence
{
    private readonly object gate = new();
    private readonly Dictionary<string, DurableWorkflowRunRecord> workflows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DurableRunRecord> runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DurableTaskRecord> tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DurableAttemptRecord> attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DurableCheckpointRecord> checkpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DurableToolCallRecord> toolCalls = new(StringComparer.Ordinal);
    private readonly List<DurableDependencyRecord> dependencies = [];

    public SchedulerSnapshot LoadSnapshot()
    {
        lock (gate)
        {
            return new SchedulerSnapshot(
                workflows.Values.OrderBy(item => item.CreatedAtMs).ThenBy(item => item.WorkflowRunId, StringComparer.Ordinal).ToArray(),
                runs.Values.OrderBy(item => item.CreatedAtMs).ThenBy(item => item.RunId, StringComparer.Ordinal).ToArray(),
                tasks.Values.OrderBy(item => item.CreatedAtMs).ThenBy(item => item.TaskId, StringComparer.Ordinal).ToArray(),
                attempts.Values.OrderBy(item => item.StartedAtMs).ThenBy(item => item.AttemptId, StringComparer.Ordinal).ToArray(),
                dependencies.ToArray(),
                toolCalls.Values.OrderBy(item => item.ToolCallId, StringComparer.Ordinal).ToArray(),
                checkpoints.Values.OrderBy(item => item.CreatedAtMs).ThenBy(item => item.CheckpointId, StringComparer.Ordinal).ToArray());
        }
    }

    public DurableWorkflowRunRecord InsertWorkflowRun(string workflowRunId, string status, long nowMs)
    {
        lock (gate)
        {
            var record = new DurableWorkflowRunRecord(workflowRunId, status, nowMs, nowMs);
            workflows[workflowRunId] = record;
            return record;
        }
    }

    public DurableRunRecord InsertRun(DurableRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (gate)
        {
            runs[run.RunId] = run;
            return run;
        }
    }

    public DurableTaskRecord InsertTask(DurableTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (gate)
        {
            tasks[task.TaskId] = task;
            return task;
        }
    }

    public DurableAttemptRecord InsertAttempt(DurableAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (gate)
        {
            attempts[attempt.AttemptId] = attempt;
            return attempt;
        }
    }

    public void UpdateWorkflowRunStatus(string workflowRunId, string status, long nowMs)
    {
        lock (gate)
        {
            if (!workflows.TryGetValue(workflowRunId, out var current))
            {
                return;
            }

            workflows[workflowRunId] = current with { Status = status, UpdatedAtMs = nowMs };
        }
    }

    public void UpdateRunStatus(string runId, string status, long nowMs)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var current))
            {
                return;
            }

            runs[runId] = current with { Status = status, UpdatedAtMs = nowMs };
        }
    }

    public void UpdateTaskStatus(string taskId, string status, long nowMs)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var current))
            {
                return;
            }

            tasks[taskId] = current with { Status = status, UpdatedAtMs = nowMs };
        }
    }

    public void UpdateAttemptStatus(string attemptId, string status, long? completedAtMs)
    {
        lock (gate)
        {
            if (!attempts.TryGetValue(attemptId, out var current))
            {
                return;
            }

            attempts[attemptId] = current with { Status = status, CompletedAtMs = completedAtMs };
        }
    }

    public void UpdateDependencyStatus(string producerTaskId, string status)
    {
        lock (gate)
        {
            for (var index = 0; index < dependencies.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(dependencies[index].ProducerTaskId, producerTaskId))
                {
                    var current = dependencies[index];
                    dependencies[index] = current with { Status = status };
                }
            }
        }
    }

    public string InsertCheckpoint(DurableCheckpointRecord checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (gate)
        {
            checkpoints[checkpoint.CheckpointId] = checkpoint;
            return checkpoint.CheckpointId;
        }
    }

    public DurableWorkflowRunRecord? GetWorkflowRun(string workflowRunId)
    {
        lock (gate)
        {
            return workflows.TryGetValue(workflowRunId, out var record) ? record : null;
        }
    }

    public DurableRunRecord? GetRun(string runId)
    {
        lock (gate)
        {
            return runs.TryGetValue(runId, out var record) ? record : null;
        }
    }

    public DurableTaskRecord? GetTask(string taskId)
    {
        lock (gate)
        {
            return tasks.TryGetValue(taskId, out var record) ? record : null;
        }
    }

    public DurableAttemptRecord? GetAttempt(string attemptId)
    {
        lock (gate)
        {
            return attempts.TryGetValue(attemptId, out var record) ? record : null;
        }
    }

    public DurableAttemptRecord? FindStartingAttempt(string taskId)
    {
        lock (gate)
        {
            return attempts.Values
                .Where(item =>
                    StringComparer.Ordinal.Equals(item.TaskId, taskId) &&
                    StringComparer.Ordinal.Equals(item.Status, AttemptStatusCodec.ToDurableValue(AttemptStatus.Starting)))
                .OrderByDescending(item => item.AttemptNo)
                .ThenByDescending(item => item.AttemptId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    public void InsertDependency(DurableDependencyRecord dependency) => AddDependency(dependency);

    public int MaxAttemptNo(string taskId)
    {
        lock (gate)
        {
            var max = 0;
            foreach (var attempt in attempts.Values)
            {
                if (StringComparer.Ordinal.Equals(attempt.TaskId, taskId) && attempt.AttemptNo > max)
                {
                    max = attempt.AttemptNo;
                }
            }

            return max;
        }
    }

    public IReadOnlyList<DurableCheckpointRecord> CheckpointsForRun(string runId)
    {
        lock (gate)
        {
            return checkpoints.Values
                .Where(item => StringComparer.Ordinal.Equals(item.RunId, runId))
                .OrderByDescending(item => item.CreatedAtMs)
                .ThenByDescending(item => item.CheckpointId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<DurableToolCallRecord> ToolCallsFor(string? runId, string? taskId)
    {
        lock (gate)
        {
            return toolCalls.Values.Where(item =>
                    (runId is null || StringComparer.Ordinal.Equals(item.RunId, runId)) &&
                    (taskId is null || StringComparer.Ordinal.Equals(item.TaskId, taskId)))
                .ToArray();
        }
    }

    public void InsertToolCall(DurableToolCallRecord toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        lock (gate)
        {
            toolCalls[toolCall.ToolCallId] = toolCall;
        }
    }

    public void MarkRunningToolCallsUnknown(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (gate)
        {
            foreach (var pair in toolCalls.ToArray())
            {
                if (StringComparer.Ordinal.Equals(pair.Value.RunId, runId) &&
                    StringComparer.Ordinal.Equals(pair.Value.Status, "running") &&
                    !StringComparer.Ordinal.Equals(pair.Value.SideEffectState, "unknown"))
                {
                    toolCalls[pair.Key] = pair.Value with
                    {
                        SideEffectState = SideEffectStateCodec.ToDurableValue(SideEffectState.Unknown)
                    };
                }
            }
        }
    }

    public void AddDependency(DurableDependencyRecord dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        lock (gate)
        {
            dependencies.Add(dependency);
        }
    }

    public void InTransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            action();
        }
    }
}

public sealed class FakeRunWorkerSupervisor : IRunWorkerSupervisor
{
    private readonly object gate = new();
    private readonly Dictionary<string, LiveWorkerObservation> workers = new(StringComparer.Ordinal);
    private readonly HashSet<string> usedIds = new(StringComparer.Ordinal);
    private int sequence;

    public IReadOnlyList<string> LaunchOrder
    {
        get
        {
            lock (gate)
            {
                return workers.Values.Select(item => item.WorkerInstanceId).ToArray();
            }
        }
    }

    public WorkerLaunchResult Launch(WorkerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            sequence++;
            var workerId = "worker-" + sequence.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            var bindingId = sequence.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            if (!usedIds.Add(workerId))
            {
                throw new InvalidOperationException("Worker identity reuse is forbidden.");
            }

            workers[workerId] = new LiveWorkerObservation(workerId, bindingId, request.RunId, Alive: true);
            return new WorkerLaunchResult(workerId, bindingId, "channel-" + workerId);
        }
    }

    public bool Release(string workerInstanceId)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(workerInstanceId, out var current))
            {
                return false;
            }

            workers[workerInstanceId] = current with { Alive = false };
            return true;
        }
    }

    public bool IsAlive(string workerInstanceId)
    {
        lock (gate)
        {
            return workers.TryGetValue(workerInstanceId, out var current) && current.Alive;
        }
    }

    public IReadOnlyList<LiveWorkerObservation> Snapshot()
    {
        lock (gate)
        {
            return workers.Values.ToArray();
        }
    }

    public void Crash(string workerInstanceId) => Release(workerInstanceId);
}
