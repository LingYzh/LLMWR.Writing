using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Application.Runtime;

public sealed class RuntimeSchedulerService
{
    private readonly IRuntimePersistence store;
    private readonly IConcurrencyBudgetPolicy budgetPolicy;
    private readonly ISecurityClock clock;
    private readonly IRunWorkerSupervisor workers;
    private readonly IAuthorizationService authorization;
    private readonly ISchedulerFaultInjector faults;
    private readonly ITrustedIpcBindingRegistry? bindings;
    private readonly RunSessionService? sessions;

    public IOversightCheckpointListener? OversightActivationListener { get; set; }

    public RuntimeSchedulerService(
        IRuntimePersistence store,
        IConcurrencyBudgetPolicy budgetPolicy,
        ISecurityClock clock,
        IRunWorkerSupervisor workers,
        IAuthorizationService? authorization = null,
        ISchedulerFaultInjector? faults = null,
        ITrustedIpcBindingRegistry? bindings = null,
        RunSessionService? sessions = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.budgetPolicy = budgetPolicy ?? throw new ArgumentNullException(nameof(budgetPolicy));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.workers = workers ?? throw new ArgumentNullException(nameof(workers));
        this.authorization = authorization ?? new CoreAuthorizationService();
        this.faults = faults ?? NoSchedulerFaultInjector.Instance;
        this.bindings = bindings;
        this.sessions = sessions;
    }

    public SchedulerSnapshot LoadSnapshot() => store.LoadSnapshot();

    public SchedulerView RebuildView() =>
        SchedulerProjection.Rebuild(store.LoadSnapshot(), budgetPolicy.Current);

    public bool ProbeCapability(CallerPrincipal? principal, Capability capability) =>
        authorization.Authorize(principal, new AuthorizationRequest(capability)).Decision ==
        CapabilityDecisionKind.Allowed;

    public bool WorkerIsAlive(string workerInstanceId) => workers.IsAlive(workerInstanceId);

    public IReadOnlyList<LiveWorkerObservation> WorkerSnapshot() => workers.Snapshot();

    public RuntimeResult<DurableWorkflowRunRecord> CreateWorkflowRun(string? workflowRunId = null, string? storylineId = null)
    {
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var id = string.IsNullOrWhiteSpace(workflowRunId) ? Guid.NewGuid().ToString("D") : workflowRunId;
        if (!string.IsNullOrWhiteSpace(storylineId) && !store.StorylineExists(storylineId))
        {
            return RuntimeResults.Fail<DurableWorkflowRunRecord>(RuntimeError.NotFound, "storyline");
        }

        return RuntimeResults.Success(store.InsertWorkflowRun(
            id,
            WorkflowRunStatusCodec.ToDurableValue(WorkflowRunStatus.Created),
            now,
            string.IsNullOrWhiteSpace(storylineId) ? null : storylineId));
    }

    public RuntimeResult<DurableRunRecord> CreateRun(string workflowRunId, string role, string? parentRunId, string? runId)
    {
        if (!string.IsNullOrWhiteSpace(parentRunId))
        {
            return RuntimeResults.Fail<DurableRunRecord>(RuntimeError.SpawnDenied, "createRun-is-root-only");
        }

        if (store.GetWorkflowRun(workflowRunId) is null)
        {
            return RuntimeResults.Fail<DurableRunRecord>(RuntimeError.NotFound, "workflow");
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var id = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("D") : runId;
        var run = new DurableRunRecord(
            id,
            workflowRunId,
            null,
            role,
            RunStatusCodec.ToDurableValue(RunStatus.Created),
            DelegationDepth.RootDepth,
            now,
            now);
        return RuntimeResults.Success(store.InsertRun(run));
    }

    public RuntimeResult<DurableTaskRecord> CreateTask(string runId, string taskKind, int priority, string? parentTaskId, string? taskId)
    {
        if (store.GetRun(runId) is null)
        {
            return RuntimeResults.Fail<DurableTaskRecord>(RuntimeError.NotFound, "run");
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var id = string.IsNullOrWhiteSpace(taskId) ? Guid.NewGuid().ToString("D") : taskId;
        var snapshot = store.LoadSnapshot();
        var pending = new DurableTaskRecord(
            id,
            runId,
            parentTaskId,
            taskKind,
            TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Pending),
            priority,
            now,
            now);
        var ready = StructuralReadiness.IsTaskStructurallyReady(id, snapshot.Dependencies);
        var status = ready ? RuntimeTaskStatus.Ready : RuntimeTaskStatus.Blocked;
        var stored = pending with { Status = TaskStatusCodec.ToDurableValue(status) };
        return RuntimeResults.Success(store.InsertTask(stored));
    }

    public RuntimeResult<DispatchReadyTaskResponseModel> DispatchReadyTask(string taskId)
    {
        ThrowIfFault(SchedulerFaultPoint.BeforeDurableDispatch);
        var task = store.GetTask(taskId);
        if (task is null)
        {
            return RuntimeResults.Fail<DispatchReadyTaskResponseModel>(RuntimeError.NotFound, "task");
        }

        if (!TaskStatusCodec.TryParse(task.Status, out var taskStatus) ||
            taskStatus is RuntimeTaskStatus.Completed or RuntimeTaskStatus.Cancelled or RuntimeTaskStatus.Failed
                or RuntimeTaskStatus.Running or RuntimeTaskStatus.Paused)
        {
            return RuntimeResults.Fail<DispatchReadyTaskResponseModel>(RuntimeError.IllegalTransition, "not-dispatchable");
        }

        if (UnknownSideEffectPolicy.BlocksAutomaticRetry(store.ToolCallsFor(null, taskId), taskId))
        {
            return RuntimeResults.Fail<DispatchReadyTaskResponseModel>(RuntimeError.UnknownSideEffect);
        }

        var snapshot = store.LoadSnapshot();
        if (!StructuralReadiness.IsTaskStructurallyReady(taskId, snapshot.Dependencies))
        {
            store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Blocked), clock.UtcNow.ToUnixTimeMilliseconds());
            return RuntimeResults.Success(new DispatchReadyTaskResponseModel(taskId, task.RunId, "", 0, "blocked"));
        }

        var budget = budgetPolicy.Current;
        ThrowIfFault(SchedulerFaultPoint.BudgetChangeDuringDispatch);
        var occupying = snapshot.Runs.Count(item =>
            RunStatusCodec.TryParse(item.Status, out var occupyingStatus) &&
            RuntimeLifecycle.IsDispatchOccupying(occupyingStatus));
        if (occupying >= budget.Effective)
        {
            if (taskStatus != RuntimeTaskStatus.Ready)
            {
                store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Ready), clock.UtcNow.ToUnixTimeMilliseconds());
            }

            return RuntimeResults.Success(new DispatchReadyTaskResponseModel(taskId, task.RunId, "", 0, "queued"));
        }

        var run = store.GetRun(task.RunId);
        if (run is null)
        {
            return RuntimeResults.Fail<DispatchReadyTaskResponseModel>(RuntimeError.NotFound, "run");
        }

        if (RunStatusCodec.TryParse(run.Status, out var runStatus) && runStatus == RunStatus.Cancelled)
        {
            return RuntimeResults.Fail<DispatchReadyTaskResponseModel>(RuntimeError.Cancelled);
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var existing = store.FindStartingAttempt(taskId);
        var attemptNo = existing?.AttemptNo ?? store.MaxAttemptNo(taskId) + 1;
        var attemptId = existing?.AttemptId ?? Guid.NewGuid().ToString("D");
            store.InTransaction(() =>
            {
                store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Running), now);
                ThrowIfFault(SchedulerFaultPoint.AfterTaskRunningBeforeAttempt);
                if (existing is null)
                {
                    store.InsertAttempt(new DurableAttemptRecord(
                        attemptId,
                        taskId,
                        attemptNo,
                        AttemptStatusCodec.ToDurableValue(AttemptStatus.Starting),
                        now,
                        null));
                }

            var nextRun = runStatus is RunStatus.Created or RunStatus.Queued or RunStatus.Interrupted
                ? RunStatus.Starting
                : RunStatus.Running;
            if (runStatus != RunStatus.Running)
            {
                store.UpdateRunStatus(task.RunId, RunStatusCodec.ToDurableValue(nextRun), now);
            }

            if (WorkflowRunStatusCodec.TryParse(store.GetWorkflowRun(run.WorkflowRunId)?.Status ?? "", out var wf) &&
                wf is WorkflowRunStatus.Created or WorkflowRunStatus.Planned)
            {
                store.UpdateWorkflowRunStatus(run.WorkflowRunId, WorkflowRunStatusCodec.ToDurableValue(WorkflowRunStatus.Running), now);
            }
        });

        ThrowIfFault(SchedulerFaultPoint.AfterAttemptBeforeWorkerLaunch);
        return RuntimeResults.Success(new DispatchReadyTaskResponseModel(taskId, task.RunId, attemptId, attemptNo, "dispatched"));
    }

    public RuntimeResult<CancelRuntimeScopeResponseModel> CancelScope(string scopeKind, string scopeId)
    {
        var snapshot = store.LoadSnapshot();
        IReadOnlyList<string> runIds;
        IReadOnlyList<string> taskIds;
        if (scopeKind == "task")
        {
            if (!snapshot.Tasks.Any(task => StringComparer.Ordinal.Equals(task.TaskId, scopeId)))
            {
                return RuntimeResults.Success(new CancelRuntimeScopeResponseModel(true, []));
            }

            taskIds = CancellationCascade.CascadeOwnedTaskIds(scopeId, snapshot);
            runIds = CancellationCascade.CascadeOwnedChildRunIds(scopeId, snapshot);
            var descendantTasks = CancellationCascade.CascadeTaskIds(runIds, snapshot);
            taskIds = taskIds.Concat(descendantTasks).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
        else
        {
            runIds = scopeKind switch
            {
                "workflowRun" => snapshot.Runs
                    .Where(run => StringComparer.Ordinal.Equals(run.WorkflowRunId, scopeId))
                    .Select(run => run.RunId)
                    .ToArray(),
                "run" => CancellationCascade.CascadeRunIds(scopeId, snapshot),
                _ => []
            };
            if (runIds.Count == 0)
            {
                return RuntimeResults.Success(new CancelRuntimeScopeResponseModel(true, []));
            }

            taskIds = CancellationCascade.CascadeTaskIds(runIds, snapshot);
        }

        var attemptIds = CancellationCascade.CascadeAttemptIds(taskIds, snapshot);
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        store.InTransaction(() =>
        {
            foreach (var runId in runIds)
            {
                store.UpdateRunStatus(runId, RunStatusCodec.ToDurableValue(RunStatus.Cancelled), now);
                sessions?.RevokeByRun(runId);
            }

            foreach (var cancelledTaskId in taskIds)
            {
                store.UpdateTaskStatus(cancelledTaskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Cancelled), now);
            }

            foreach (var attemptId in attemptIds)
            {
                store.UpdateAttemptStatus(attemptId, AttemptStatusCodec.ToDurableValue(AttemptStatus.Cancelled), now);
            }

            if (scopeKind == "workflowRun")
            {
                store.UpdateWorkflowRunStatus(scopeId, WorkflowRunStatusCodec.ToDurableValue(WorkflowRunStatus.Cancelled), now);
            }
        });

        foreach (var observation in workers.Snapshot().Where(item => runIds.Contains(item.RunId, StringComparer.Ordinal)))
        {
            workers.Release(observation.WorkerInstanceId);
            bindings?.Unregister(observation.LaunchBindingId);
        }

        return RuntimeResults.Success(new CancelRuntimeScopeResponseModel(true, runIds.ToArray()));
    }

    public RuntimeResult<RetryTaskResponseModel> RetryTask(string taskId)
    {
        var task = store.GetTask(taskId);
        if (task is null)
        {
            return RuntimeResults.Fail<RetryTaskResponseModel>(RuntimeError.NotFound, "task");
        }

        if (UnknownSideEffectPolicy.BlocksAutomaticRetry(store.ToolCallsFor(null, taskId), taskId))
        {
            return RuntimeResults.Fail<RetryTaskResponseModel>(RuntimeError.UnknownSideEffect);
        }

        if (!TaskStatusCodec.TryParse(task.Status, out var status) || !RuntimeLifecycle.CanRetryTask(status))
        {
            return RuntimeResults.Fail<RetryTaskResponseModel>(RuntimeError.IllegalTransition);
        }

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var attemptNo = store.MaxAttemptNo(taskId) + 1;
        var attemptId = Guid.NewGuid().ToString("D");
        store.InTransaction(() =>
        {
            store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Ready), now);
            store.InsertAttempt(new DurableAttemptRecord(
                attemptId,
                taskId,
                attemptNo,
                AttemptStatusCodec.ToDurableValue(AttemptStatus.Starting),
                now,
                null));
        });
        return RuntimeResults.Success(new RetryTaskResponseModel(taskId, attemptId, attemptNo, "retried"));
    }

    public RuntimeResult<string> PersistCheckpoint(string runId, string? taskId, int schemaVersion, string payloadJson, string inputDigestSetJson)
    {
        if (schemaVersion != CheckpointV1.CurrentSchemaVersion)
        {
            return RuntimeResults.Fail<string>(RuntimeError.CheckpointUnsupported);
        }

        if (SecretRedaction.ContainsSecretMaterial(payloadJson))
        {
            payloadJson = SecretRedaction.RedactObjectJson(payloadJson);
        }

        try
        {
            _ = CanonicalJson.Parse(payloadJson, schemaVersion);
        }
        catch (CheckpointSchemaException exception)
        {
            return RuntimeResults.Fail<string>(
                exception.Code == RuntimeRejectionCode.CheckpointUnsupported
                    ? RuntimeError.CheckpointUnsupported
                    : RuntimeError.CheckpointCorrupt);
        }

        ThrowIfFault(SchedulerFaultPoint.CheckpointWrite);
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var id = Guid.NewGuid().ToString("D");
        store.InsertCheckpoint(new DurableCheckpointRecord(id, runId, taskId, schemaVersion, payloadJson, inputDigestSetJson, now));
        store.BindPendingOversightOverrides(id, runId, taskId, now);
        OversightActivationListener?.OnSafeCheckpoint(id, runId, taskId, now);
        return RuntimeResults.Success(id);
    }

    public RuntimeResult<ResumeDecision> ClassifyResume(string runId, FreshnessInputs inputs)
    {
        var run = store.GetRun(runId);
        if (run is null)
        {
            return RuntimeResults.Fail<ResumeDecision>(RuntimeError.NotFound, "run");
        }

        var unknown = UnknownSideEffectPolicy.BlocksAutomaticRetry(store.ToolCallsFor(runId, null), null);
        var checkpoints = store.CheckpointsForRun(runId);
        var latest = checkpoints.Count == 0 ? null : checkpoints[0];
        try
        {
            return RuntimeResults.Success(ResumeClassifier.Classify(run, latest, inputs with { UnknownSideEffect = unknown || inputs.UnknownSideEffect }));
        }
        catch (CheckpointSchemaException exception)
        {
            return RuntimeResults.Fail<ResumeDecision>(
                exception.Code == RuntimeRejectionCode.CheckpointUnsupported
                    ? RuntimeError.CheckpointUnsupported
                    : RuntimeError.CheckpointCorrupt);
        }
    }

    public RuntimeResult<WorkerLaunchResult> LaunchRunWorker(string runId, string taskId, string attemptId)
    {
        var run = store.GetRun(runId);
        var task = store.GetTask(taskId);
        var attempt = store.GetAttempt(attemptId);
        if (run is null || task is null || attempt is null)
        {
            return RuntimeResults.Fail<WorkerLaunchResult>(RuntimeError.NotFound);
        }

        if (!StringComparer.Ordinal.Equals(task.RunId, runId) || !StringComparer.Ordinal.Equals(attempt.TaskId, taskId))
        {
            return RuntimeResults.Fail<WorkerLaunchResult>(RuntimeError.NotFound, "identity-mismatch");
        }

        if (!RunStatusCodec.TryParse(run.Status, out var runStatus) ||
            !TaskStatusCodec.TryParse(task.Status, out var taskStatus) ||
            !AttemptStatusCodec.TryParse(attempt.Status, out var attemptStatus) ||
            runStatus != RunStatus.Starting ||
            taskStatus != RuntimeTaskStatus.Running ||
            attemptStatus != AttemptStatus.Starting)
        {
            return RuntimeResults.Fail<WorkerLaunchResult>(RuntimeError.IllegalTransition, "dispatch-reservation-required");
        }

        if (workers.Snapshot().Any(item => item.Alive && StringComparer.Ordinal.Equals(item.RunId, runId)))
        {
            return RuntimeResults.Fail<WorkerLaunchResult>(RuntimeError.WorkerLaunchFailed, "one-run-one-worker");
        }

        WorkerLaunchResult launched;
        try
        {
            launched = workers.Launch(new WorkerLaunchRequest(runId, taskId, attemptId, run.Role));
        }
        catch (InvalidOperationException)
        {
            return RuntimeResults.Fail<WorkerLaunchResult>(RuntimeError.WorkerLaunchFailed);
        }

        ThrowIfFault(SchedulerFaultPoint.AfterWorkerLaunchBeforeAck);
        store.UpdateAttemptStatus(attemptId, AttemptStatusCodec.ToDurableValue(AttemptStatus.Running), null);
        store.UpdateRunStatus(runId, RunStatusCodec.ToDurableValue(RunStatus.Running), clock.UtcNow.ToUnixTimeMilliseconds());
        return RuntimeResults.Success(launched);
    }

    public RuntimeResult<bool> ReleaseRunWorker(string workerInstanceId)
    {
        var released = workers.Release(workerInstanceId);
        return RuntimeResults.Success(released);
    }

    public RuntimeResult<IReadOnlyList<WorkerReconcileDtoModel>> ReconcileWorkers()
    {
        var snapshot = store.LoadSnapshot();
        var items = new List<WorkerReconcileDtoModel>();
        var alive = workers.Snapshot().Where(item => item.Alive).ToArray();
        var aliveByRun = new Dictionary<string, List<LiveWorkerObservation>>(StringComparer.Ordinal);
        foreach (var observation in alive)
        {
            if (!aliveByRun.TryGetValue(observation.RunId, out var list))
            {
                list = [];
                aliveByRun[observation.RunId] = list;
            }

            list.Add(observation);
        }

        foreach (var pair in aliveByRun)
        {
            if (pair.Value.Count <= 1)
            {
                continue;
            }

            var ordered = pair.Value.OrderBy(item => item.WorkerInstanceId, StringComparer.Ordinal).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                items.Add(new WorkerReconcileDtoModel("workerIdentityMismatch", pair.Key, ordered[index].WorkerInstanceId));
                ReleaseWorkerBinding(ordered[index]);
            }
        }

        var liveByRun = workers.Snapshot()
            .Where(item => item.Alive)
            .GroupBy(item => item.RunId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.WorkerInstanceId, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);

        foreach (var run in snapshot.Runs)
        {
            if (!RunStatusCodec.TryParse(run.Status, out var status))
            {
                continue;
            }

            if (RuntimeLifecycle.IsTerminal(status))
            {
                if (liveByRun.TryGetValue(run.RunId, out var leftover))
                {
                    items.Add(new WorkerReconcileDtoModel("workerForTerminalRun", run.RunId, leftover.WorkerInstanceId));
                    ReleaseWorkerBinding(leftover);
                }

                continue;
            }

            if (liveByRun.TryGetValue(run.RunId, out var observation))
            {
                items.Add(new WorkerReconcileDtoModel("activeRunWithLiveWorker", run.RunId, observation.WorkerInstanceId));
                continue;
            }

            if (RuntimeLifecycle.IsActive(status) || status is RunStatus.Starting or RunStatus.Queued)
            {
                items.Add(new WorkerReconcileDtoModel("activeRunWorkerGone", run.RunId, null));
                InterruptRunAfterWorkerLoss(run.RunId, snapshot);
            }
        }

        foreach (var observation in workers.Snapshot().Where(item => item.Alive))
        {
            var run = store.GetRun(observation.RunId);
            if (run is null)
            {
                items.Add(new WorkerReconcileDtoModel("workerWithoutDurableRun", null, observation.WorkerInstanceId));
                ReleaseWorkerBinding(observation);
            }
        }

        return RuntimeResults.Success<IReadOnlyList<WorkerReconcileDtoModel>>(items);
    }

    private void InterruptRunAfterWorkerLoss(string runId, SchedulerSnapshot snapshot)
    {
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var taskIds = snapshot.Tasks
            .Where(task => StringComparer.Ordinal.Equals(task.RunId, runId))
            .Select(task => task.TaskId)
            .ToHashSet(StringComparer.Ordinal);
        store.InTransaction(() =>
        {
            store.UpdateRunStatus(runId, RunStatusCodec.ToDurableValue(RunStatus.Interrupted), now);
            foreach (var taskId in taskIds)
            {
                var task = store.GetTask(taskId);
                if (task is not null &&
                    TaskStatusCodec.TryParse(task.Status, out var taskStatus) &&
                    taskStatus == RuntimeTaskStatus.Running)
                {
                    store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Paused), now);
                }
            }

            foreach (var attempt in snapshot.Attempts)
            {
                if (!taskIds.Contains(attempt.TaskId) ||
                    !AttemptStatusCodec.TryParse(attempt.Status, out var attemptStatus))
                {
                    continue;
                }

                if (attemptStatus == AttemptStatus.Running)
                {
                    store.UpdateAttemptStatus(
                        attempt.AttemptId,
                        AttemptStatusCodec.ToDurableValue(AttemptStatus.Unknown),
                        now);
                }
                else if (attemptStatus == AttemptStatus.Starting)
                {
                    store.UpdateAttemptStatus(
                        attempt.AttemptId,
                        AttemptStatusCodec.ToDurableValue(AttemptStatus.Interrupted),
                        now);
                }
            }

            store.MarkRunningToolCallsUnknown(runId);
        });
    }

    private void ReleaseWorkerBinding(LiveWorkerObservation observation)
    {
        workers.Release(observation.WorkerInstanceId);
        bindings?.Unregister(observation.LaunchBindingId);
    }

    public RuntimeResult<SpawnChildRunResponseModel> SpawnChildRun(
        string parentRunId,
        string parentTaskId,
        string role,
        int? requestedDepth,
        CallerPrincipal? principal)
    {
        if (principal is { Kind: PrincipalKind.AgentRun } &&
            !StringComparer.Ordinal.Equals(principal.RunId, parentRunId))
        {
            return RuntimeResults.Fail<SpawnChildRunResponseModel>(RuntimeError.SpawnDenied, "scope-mismatch");
        }

        var parent = store.GetRun(parentRunId);
        if (parent is null)
        {
            return RuntimeResults.Fail<SpawnChildRunResponseModel>(RuntimeError.NotFound, "parent-run");
        }

        var parentTask = store.GetTask(parentTaskId);
        if (parentTask is null)
        {
            return RuntimeResults.Fail<SpawnChildRunResponseModel>(RuntimeError.NotFound, "parent-task");
        }

        if (!StringComparer.Ordinal.Equals(parentTask.RunId, parentRunId))
        {
            return RuntimeResults.Fail<SpawnChildRunResponseModel>(RuntimeError.SpawnDenied, "parent-task-scope");
        }

        if (TaskStatusCodec.TryParse(parentTask.Status, out var parentTaskStatus) &&
            parentTaskStatus is RuntimeTaskStatus.Cancelled or RuntimeTaskStatus.Completed)
        {
            return RuntimeResults.Fail<SpawnChildRunResponseModel>(
                parentTaskStatus == RuntimeTaskStatus.Cancelled ? RuntimeError.Cancelled : RuntimeError.SpawnDenied,
                "parent-task");
        }

        var parentCancelled = RunStatusCodec.TryParse(parent.Status, out var parentStatus) &&
                              parentStatus == RunStatus.Cancelled;
        var unknown = UnknownSideEffectPolicy.BlocksAutomaticRetry(store.ToolCallsFor(parentRunId, parentTaskId), parentTaskId);
        var agentSpawn = principal is not null &&
                         authorization.Authorize(principal, new AuthorizationRequest(Capability.AgentSpawn)).IsAllowed;
        var snapshot = store.LoadSnapshot();
        var occupying = snapshot.Runs.Count(item =>
            RunStatusCodec.TryParse(item.Status, out var occupyingStatus) &&
            RuntimeLifecycle.IsDispatchOccupying(occupyingStatus));
        var decision = SpawnPolicy.Evaluate(
            agentSpawn,
            parentCancelled,
            parent.Depth,
            requestedDepth,
            occupying,
            budgetPolicy.Current,
            unknown);
        if (decision.Outcome == SpawnOutcomeKind.Denied)
        {
            var error = decision.Denial switch
            {
                SpawnDenialReason.DepthLimit => RuntimeError.DepthLimit,
                SpawnDenialReason.DepthSpoof => RuntimeError.DepthSpoof,
                SpawnDenialReason.UnknownSideEffect => RuntimeError.UnknownSideEffect,
                SpawnDenialReason.Cancelled => RuntimeError.Cancelled,
                _ => RuntimeError.SpawnDenied
            };
            return RuntimeResults.Fail<SpawnChildRunResponseModel>(error, decision.Denial.ToString());
        }

        var status = decision.Outcome == SpawnOutcomeKind.Queued ? RunStatus.Queued : RunStatus.Created;
        var created = CreateChildRunAfterAuthorizedSpawn(parent, role, decision.DerivedDepth!.Value, status, parentTaskId);
        if (!created.Succeeded || created.Value is null)
        {
            return RuntimeResults.Fail<SpawnChildRunResponseModel>(created.Failure?.Code ?? RuntimeError.NotFound, created.Failure?.Detail);
        }

        var outcome = decision.Outcome == SpawnOutcomeKind.Queued ? "queued" : "spawned";
        return RuntimeResults.Success(new SpawnChildRunResponseModel(outcome, created.Value.RunId, created.Value.Depth, null));
    }

    private RuntimeResult<DurableRunRecord> CreateChildRunAfterAuthorizedSpawn(
        DurableRunRecord parent,
        string role,
        int derivedDepth,
        RunStatus status,
        string parentTaskId)
    {
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var run = new DurableRunRecord(
            Guid.NewGuid().ToString("D"),
            parent.WorkflowRunId,
            parent.RunId,
            role,
            RunStatusCodec.ToDurableValue(status),
            derivedDepth,
            now,
            now);
        DurableRunRecord stored = run;
        store.InTransaction(() =>
        {
            stored = store.InsertRun(run);
            var taskStatus = RuntimeTaskStatus.Ready;
            store.InsertTask(new DurableTaskRecord(
                Guid.NewGuid().ToString("D"),
                stored.RunId,
                parentTaskId,
                "spawned",
                TaskStatusCodec.ToDurableValue(taskStatus),
                1,
                now,
                now));
        });
        return RuntimeResults.Success(stored);
    }

    public void RecordUnknownToolCall(string runId, string taskId, string toolName)
    {
        store.InsertToolCall(new DurableToolCallRecord(
            Guid.NewGuid().ToString("D"),
            runId,
            taskId,
            toolName,
            "running",
            SideEffectStateCodec.ToDurableValue(SideEffectState.Unknown)));
    }

    public void CompleteTask(string taskId)
    {
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        store.UpdateTaskStatus(taskId, TaskStatusCodec.ToDurableValue(RuntimeTaskStatus.Completed), now);
        store.UpdateDependencyStatus(taskId, StructuralReadiness.SatisfiedStatus);
    }

    private void ThrowIfFault(SchedulerFaultPoint point)
    {
        if (faults.Fault == point)
        {
            throw new SchedulerFaultInjectedException(point);
        }
    }
}

public sealed record DispatchReadyTaskResponseModel(string TaskId, string RunId, string AttemptId, int AttemptNo, string Outcome);

public sealed record CancelRuntimeScopeResponseModel(bool Cancelled, string[] AffectedRunIds);

public sealed record RetryTaskResponseModel(string TaskId, string AttemptId, int AttemptNo, string Outcome);

public sealed record SpawnChildRunResponseModel(string Outcome, string? ChildRunId, int? Depth, string? Reason);

public sealed record WorkerReconcileDtoModel(string Classification, string? RunId, string? WorkerInstanceId);
