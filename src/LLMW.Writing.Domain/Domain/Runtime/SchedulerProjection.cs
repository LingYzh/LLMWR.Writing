namespace LLMW.Writing.Domain.Runtime;

/// <summary>
/// Deterministic READY ordering: higher business priority first, then earlier created_at_ms,
/// then ordinal task identity. Equal priority is not FIFO-of-insertion.
/// </summary>
public readonly struct ReadySortKey
{
    public ReadySortKey(int priority, long createdAtMs, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        Priority = priority;
        CreatedAtMs = createdAtMs;
        TaskId = taskId;
    }

    public int Priority { get; }

    public long CreatedAtMs { get; }

    public string TaskId { get; }
}

public sealed class ReadySortKeyComparer : IComparer<ReadySortKey>
{
    public static ReadySortKeyComparer Instance { get; } = new();

    public int Compare(ReadySortKey x, ReadySortKey y)
    {
        var priority = y.Priority.CompareTo(x.Priority);
        if (priority != 0)
        {
            return priority;
        }

        var created = x.CreatedAtMs.CompareTo(y.CreatedAtMs);
        return created != 0 ? created : string.CompareOrdinal(x.TaskId, y.TaskId);
    }
}

public sealed class DeterministicReadyOrder
{
    private readonly PriorityQueue<DurableTaskRecord, ReadySortKey> queue = new(ReadySortKeyComparer.Instance);
    private readonly HashSet<string> enqueued = new(StringComparer.Ordinal);

    public int Count => queue.Count;

    public void Enqueue(DurableTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!enqueued.Add(task.TaskId))
        {
            return;
        }

        queue.Enqueue(task, new ReadySortKey(task.Priority, task.CreatedAtMs, task.TaskId));
    }

    public bool TryDequeue(out DurableTaskRecord task)
    {
        if (queue.TryDequeue(out task!, out _))
        {
            enqueued.Remove(task.TaskId);
            return true;
        }

        task = null!;
        return false;
    }

    public IReadOnlyList<string> PeekOrderedTaskIds()
    {
        var copy = new PriorityQueue<DurableTaskRecord, ReadySortKey>(ReadySortKeyComparer.Instance);
        foreach (var (element, priority) in queue.UnorderedItems)
        {
            copy.Enqueue(element, priority);
        }

        var ids = new List<string>(copy.Count);
        while (copy.TryDequeue(out var task, out _))
        {
            ids.Add(task.TaskId);
        }

        return ids;
    }
}

public sealed record DurableWorkflowRunRecord(
    string WorkflowRunId,
    string Status,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record DurableRunRecord(
    string RunId,
    string WorkflowRunId,
    string? ParentRunId,
    string Role,
    string Status,
    int Depth,
    long CreatedAtMs,
    long UpdatedAtMs,
    string? ProviderId = null,
    string? ModelId = null,
    string? PromptConfigId = null,
    string? EffectivePromptDigest = null);

public sealed record DurableTaskRecord(
    string TaskId,
    string RunId,
    string? ParentTaskId,
    string TaskKind,
    string Status,
    int Priority,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record DurableAttemptRecord(
    string AttemptId,
    string TaskId,
    int AttemptNo,
    string Status,
    long StartedAtMs,
    long? CompletedAtMs);

public sealed record DurableDependencyRecord(
    string DependencyId,
    string ConsumerTaskId,
    string ProducerTaskId,
    string DependencyKind,
    string Status);

public sealed record DurableToolCallRecord(
    string ToolCallId,
    string RunId,
    string? TaskId,
    string ToolName,
    string Status,
    string SideEffectState);

public sealed record DurableCheckpointRecord(
    string CheckpointId,
    string RunId,
    string? TaskId,
    int SchemaVersion,
    string PayloadJson,
    string InputDigestSetJson,
    long CreatedAtMs);

public sealed record SchedulerSnapshot(
    IReadOnlyList<DurableWorkflowRunRecord> WorkflowRuns,
    IReadOnlyList<DurableRunRecord> Runs,
    IReadOnlyList<DurableTaskRecord> Tasks,
    IReadOnlyList<DurableAttemptRecord> Attempts,
    IReadOnlyList<DurableDependencyRecord> Dependencies,
    IReadOnlyList<DurableToolCallRecord> ToolCalls,
    IReadOnlyList<DurableCheckpointRecord> Checkpoints)
{
    public static SchedulerSnapshot Empty { get; } = new([], [], [], [], [], [], []);
}

public sealed record SchedulerView(
    IReadOnlyList<string> ReadyTaskIds,
    IReadOnlySet<string> BlockedTaskIds,
    IReadOnlySet<string> ActiveTaskIds,
    IReadOnlyDictionary<string, int> RunDepths,
    int ActiveRunCount,
    int EffectiveBudget,
    IReadOnlyDictionary<string, ResumeDecisionKind> ResumeByRunId,
    IReadOnlySet<string> UnknownSideEffectTaskIds)
{
    public IReadOnlyList<string> BlockedReasonOrder => BlockedTaskIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
}

public static class StructuralReadiness
{
    public const string RequiredKind = "required";
    public const string SatisfiedStatus = "satisfied";

    public static bool IsTaskStructurallyReady(
        string taskId,
        IReadOnlyList<DurableDependencyRecord> dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(dependencies);
        foreach (var dependency in dependencies)
        {
            if (!StringComparer.Ordinal.Equals(dependency.ConsumerTaskId, taskId))
            {
                continue;
            }

            if (!StringComparer.Ordinal.Equals(dependency.DependencyKind, RequiredKind))
            {
                continue;
            }

            if (!StringComparer.Ordinal.Equals(dependency.Status, SatisfiedStatus))
            {
                return false;
            }
        }

        return true;
    }
}

public static class SchedulerProjection
{
    public static SchedulerView Rebuild(SchedulerSnapshot snapshot, ConcurrencyBudget budget)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(budget);

        var runs = snapshot.Runs.OrderBy(run => run.CreatedAtMs).ThenBy(run => run.RunId, StringComparer.Ordinal).ToArray();
        var tasks = snapshot.Tasks.OrderBy(task => task.CreatedAtMs).ThenBy(task => task.TaskId, StringComparer.Ordinal).ToArray();
        var runById = runs.ToDictionary(run => run.RunId, StringComparer.Ordinal);
        var depths = runs.ToDictionary(run => run.RunId, run => run.Depth, StringComparer.Ordinal);
        var blocked = new SortedSet<string>(StringComparer.Ordinal);
        var activeTasks = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var ready = new DeterministicReadyOrder();

        foreach (var tool in snapshot.ToolCalls)
        {
            if (StringComparer.Ordinal.Equals(tool.SideEffectState, SideEffectStateCodec.ToDurableValue(SideEffectState.Unknown)) &&
                !string.IsNullOrWhiteSpace(tool.TaskId))
            {
                unknown.Add(tool.TaskId);
            }
        }

        foreach (var task in tasks)
        {
            if (!TaskStatusCodec.TryParse(task.Status, out var status))
            {
                continue;
            }

            if (status == TaskStatus.Completed)
            {
                continue;
            }

            if (status == TaskStatus.Running)
            {
                activeTasks.Add(task.TaskId);
                continue;
            }

            if (status is TaskStatus.Cancelled)
            {
                continue;
            }

            if (unknown.Contains(task.TaskId))
            {
                blocked.Add(task.TaskId);
                continue;
            }

            var structurallyReady = StructuralReadiness.IsTaskStructurallyReady(task.TaskId, snapshot.Dependencies);
            if (!structurallyReady)
            {
                blocked.Add(task.TaskId);
                continue;
            }

            if (status is TaskStatus.Failed or TaskStatus.Paused)
            {
                continue;
            }

            if (status is TaskStatus.Ready or TaskStatus.Pending or TaskStatus.Blocked)
            {
                ready.Enqueue(task);
            }
        }

        var activeRuns = runs.Count(run =>
            RunStatusCodec.TryParse(run.Status, out var status) && RuntimeLifecycle.IsDispatchOccupying(status));
        var resume = new SortedDictionary<string, ResumeDecisionKind>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (!RunStatusCodec.TryParse(run.Status, out var status) || RuntimeLifecycle.IsTerminal(status))
            {
                continue;
            }

            var runUnknown = snapshot.ToolCalls.Any(tool =>
                StringComparer.Ordinal.Equals(tool.RunId, run.RunId) &&
                StringComparer.Ordinal.Equals(tool.SideEffectState, "unknown"));
            var latest = snapshot.Checkpoints
                .Where(checkpoint => StringComparer.Ordinal.Equals(checkpoint.RunId, run.RunId))
                .OrderByDescending(checkpoint => checkpoint.CreatedAtMs)
                .ThenByDescending(checkpoint => checkpoint.CheckpointId, StringComparer.Ordinal)
                .FirstOrDefault();
            resume[run.RunId] = ResumeClassifier.ClassifyForRebuild(run, latest, runUnknown);
        }

        return new SchedulerView(
            ready.PeekOrderedTaskIds(),
            blocked,
            activeTasks,
            depths,
            activeRuns,
            budget.Effective,
            resume,
            unknown);
    }

    public static string RootRunId(DurableRunRecord run, IReadOnlyDictionary<string, DurableRunRecord> runs)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(runs);
        var current = run;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(current.ParentRunId) && runs.TryGetValue(current.ParentRunId, out var parent))
        {
            current = parent;
            if (++guard > DelegationDepth.MaximumDepth + 1)
            {
                break;
            }
        }

        return current.RunId;
    }

    public static int CountActiveInTree(string runId, SchedulerSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(snapshot);
        var runs = snapshot.Runs.ToDictionary(run => run.RunId, StringComparer.Ordinal);
        if (!runs.TryGetValue(runId, out var seed))
        {
            return 0;
        }

        var root = RootRunId(seed, runs);
        var count = 0;
        foreach (var run in snapshot.Runs)
        {
            if (!RunStatusCodec.TryParse(run.Status, out var status) || !RuntimeLifecycle.IsDispatchOccupying(status))
            {
                continue;
            }

            if (StringComparer.Ordinal.Equals(RootRunId(run, runs), root))
            {
                count++;
            }
        }

        return count;
    }
}

public static class CancellationCascade
{
    public static IReadOnlyList<string> CascadeRunIds(string originRunId, SchedulerSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originRunId);
        ArgumentNullException.ThrowIfNull(snapshot);
        var children = snapshot.Runs
            .Where(run => StringComparer.Ordinal.Equals(run.ParentRunId, originRunId))
            .Select(run => run.RunId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var result = new List<string> { originRunId };
        foreach (var child in children)
        {
            result.AddRange(CascadeRunIds(child, snapshot));
        }

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> CascadeTaskIds(IReadOnlyList<string> runIds, SchedulerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        ArgumentNullException.ThrowIfNull(snapshot);
        var set = new HashSet<string>(runIds, StringComparer.Ordinal);
        return snapshot.Tasks
            .Where(task => set.Contains(task.RunId) && TaskStatusCodec.TryParse(task.Status, out var status) &&
                           !RuntimeLifecycle.IsTerminal(status))
            .Select(task => task.TaskId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> CascadeOwnedTaskIds(string originTaskId, SchedulerSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originTaskId);
        ArgumentNullException.ThrowIfNull(snapshot);
        var childrenByParent = new Dictionary<string, List<DurableTaskRecord>>(StringComparer.Ordinal);
        foreach (var task in snapshot.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.ParentTaskId))
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(task.ParentTaskId, out var children))
            {
                children = [];
                childrenByParent[task.ParentTaskId] = children;
            }

            children.Add(task);
        }

        foreach (var pair in childrenByParent)
        {
            pair.Value.Sort((left, right) => string.CompareOrdinal(left.TaskId, right.TaskId));
        }

        var owned = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string taskId)
        {
            if (!seen.Add(taskId))
            {
                return;
            }

            owned.Add(taskId);
            if (childrenByParent.TryGetValue(taskId, out var children))
            {
                foreach (var child in children)
                {
                    Walk(child.TaskId);
                }
            }
        }

        if (snapshot.Tasks.Any(task => StringComparer.Ordinal.Equals(task.TaskId, originTaskId)))
        {
            Walk(originTaskId);
        }

        owned.Sort(StringComparer.Ordinal);
        return owned;
    }

    public static IReadOnlyList<string> CascadeOwnedChildRunIds(string originTaskId, SchedulerSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originTaskId);
        ArgumentNullException.ThrowIfNull(snapshot);
        var origin = snapshot.Tasks.FirstOrDefault(task => StringComparer.Ordinal.Equals(task.TaskId, originTaskId));
        if (origin is null)
        {
            return [];
        }

        var ownedTasks = new HashSet<string>(CascadeOwnedTaskIds(originTaskId, snapshot), StringComparer.Ordinal);
        var direct = snapshot.Runs
            .Where(run =>
                !StringComparer.Ordinal.Equals(run.RunId, origin.RunId) &&
                snapshot.Tasks.Any(task =>
                    StringComparer.Ordinal.Equals(task.RunId, run.RunId) &&
                    task.ParentTaskId is not null &&
                    ownedTasks.Contains(task.ParentTaskId)))
            .Select(run => run.RunId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var all = new List<string>();
        foreach (var runId in direct)
        {
            all.AddRange(CascadeRunIds(runId, snapshot));
        }

        return all.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> CascadeAttemptIds(IReadOnlyList<string> taskIds, SchedulerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        ArgumentNullException.ThrowIfNull(snapshot);
        var set = new HashSet<string>(taskIds, StringComparer.Ordinal);
        return snapshot.Attempts
            .Where(attempt => set.Contains(attempt.TaskId) &&
                              AttemptStatusCodec.TryParse(attempt.Status, out var status) &&
                              status is AttemptStatus.Starting or AttemptStatus.Running)
            .Select(attempt => attempt.AttemptId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }
}

public static class UnknownSideEffectPolicy
{
    public static bool BlocksAutomaticRetry(IEnumerable<DurableToolCallRecord> toolCalls, string? taskId)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        return toolCalls.Any(tool =>
            (taskId is null || StringComparer.Ordinal.Equals(tool.TaskId, taskId)) &&
            StringComparer.Ordinal.Equals(tool.SideEffectState, "unknown"));
    }
}
