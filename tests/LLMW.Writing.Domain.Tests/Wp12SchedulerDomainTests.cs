using LLMW.Writing.Domain.Runtime;
using RuntimeTaskStatus = LLMW.Writing.Domain.Runtime.TaskStatus;

namespace LLMW.Writing.Domain.Tests;

internal static partial class Program
{
    private static void RunWp12SchedulerDomainTests()
    {
        Run(nameof(ReadyQueueOrdersByPriorityThenStableCreationThenTaskId), ReadyQueueOrdersByPriorityThenStableCreationThenTaskId);
        Run(nameof(EqualPriorityUsesCreationOrderNotInsertionFifo), EqualPriorityUsesCreationOrderNotInsertionFifo);
        Run(nameof(RebuildFromSameSnapshotIsDeterministic), RebuildFromSameSnapshotIsDeterministic);
        Run(nameof(RootDepthIsZeroAndDepthFiveIsDenied), RootDepthIsZeroAndDepthFiveIsDenied);
        Run(nameof(ConcurrencyFourQueuesTheFifthReadyTask), ConcurrencyFourQueuesTheFifthReadyTask);
        Run(nameof(ChildSharesParentTreeBudget), ChildSharesParentTreeBudget);
        Run(nameof(BudgetCanAdaptDownAndUpButNeverExceedFour), BudgetCanAdaptDownAndUpButNeverExceedFour);
        Run(nameof(CapacityFullIsQueuedNotSecurityDenial), CapacityFullIsQueuedNotSecurityDenial);
        Run(nameof(CancelledParentSpawnIsDenied), CancelledParentSpawnIsDenied);
        Run(nameof(CancelCascadeIsIdempotentAndScoped), CancelCascadeIsIdempotentAndScoped);
        Run(nameof(RetryContinuesSameTaskWithNewAttemptNumber), RetryContinuesSameTaskWithNewAttemptNumber);
        Run(nameof(IllegalLifecycleTransitionsAreRejected), IllegalLifecycleTransitionsAreRejected);
        Run(nameof(CompletedTaskIsNeverRequeued), CompletedTaskIsNeverRequeued);
        Run(nameof(CheckpointV1SerializesDeterministicallyAndExcludesSecrets), CheckpointV1SerializesDeterministicallyAndExcludesSecrets);
        Run(nameof(CheckpointRetainsLatestTwentyMessagesAndTruncatesToolRefs), CheckpointRetainsLatestTwentyMessagesAndTruncatesToolRefs);
        Run(nameof(ResumeClassifierCoversFrozenDecisionsAndUnknownSideEffect), ResumeClassifierCoversFrozenDecisionsAndUnknownSideEffect);
        Run(nameof(ResumeClassifierComparesCurrentInputsToExactCheckpointBaseline), ResumeClassifierComparesCurrentInputsToExactCheckpointBaseline);
        Run(nameof(UnrelatedDraftChangeDoesNotStaleTask), UnrelatedDraftChangeDoesNotStaleTask);
        Run(nameof(UnknownSideEffectBlocksAutomaticRetry), UnknownSideEffectBlocksAutomaticRetry);
        Run(nameof(RequiredUnsatisfiedDependencyBlocksDispatch), RequiredUnsatisfiedDependencyBlocksDispatch);
        Run(nameof(DepthSpoofIsRejectedWithoutClamping), DepthSpoofIsRejectedWithoutClamping);
        Run(nameof(FailedAndPausedTasksAreNotReadyAfterRebuild), FailedAndPausedTasksAreNotReadyAfterRebuild);
        Run(nameof(TaskCancelCascadeOwnsDescendantsWithoutTheContainingRun), TaskCancelCascadeOwnsDescendantsWithoutTheContainingRun);
    }

    private static void ReadyQueueOrdersByPriorityThenStableCreationThenTaskId()
    {
        var queue = new DeterministicReadyOrder();
        queue.Enqueue(Task("t-low", 1, 10));
        queue.Enqueue(Task("t-high", 9, 50));
        queue.Enqueue(Task("t-mid", 5, 20));
        AssertEqual("t-high,t-mid,t-low", string.Join(',', queue.PeekOrderedTaskIds()), "READY ordering drifted.");
    }

    private static void EqualPriorityUsesCreationOrderNotInsertionFifo()
    {
        var queue = new DeterministicReadyOrder();
        queue.Enqueue(Task("t-z", 3, 300));
        queue.Enqueue(Task("t-a", 3, 100));
        queue.Enqueue(Task("t-m", 3, 200));
        AssertEqual("t-a,t-m,t-z", string.Join(',', queue.PeekOrderedTaskIds()),
            "Equal priority must use created_at_ms then task id, not insertion FIFO.");
    }

    private static void RebuildFromSameSnapshotIsDeterministic()
    {
        var snapshot = Snapshot(
            runs: [Run("root", null, 0, "running")],
            tasks:
            [
                Task("t-b", 1, 20, "ready", "root"),
                Task("t-a", 1, 10, "ready", "root"),
                Task("t-c", 5, 30, "ready", "root"),
                Task("done", 9, 1, "completed", "root")
            ]);
        var first = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        var second = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        AssertEqual(string.Join(',', first.ReadyTaskIds), string.Join(',', second.ReadyTaskIds), "Rebuild ordering was not stable.");
        AssertEqual("t-c,t-a,t-b", string.Join(',', first.ReadyTaskIds), "Rebuild READY set is wrong.");
        AssertEqual(0, first.BlockedTaskIds.Count, "Unexpected blocked tasks.");
        AssertEqual(1, first.ActiveRunCount, "Active run accounting drifted.");
        AssertEqual(4, first.EffectiveBudget, "Default budget must be 4.");
        AssertEqual(0, first.RunDepths["root"], "Root depth must be 0.");
    }

    private static void RootDepthIsZeroAndDepthFiveIsDenied()
    {
        AssertEqual(0, DelegationDepth.RootDepth, "Root depth convention must be 0.");
        AssertEqual(4, DelegationDepth.MaximumDepth, "Max depth must be 4.");
        AssertEqual(1, DelegationDepth.ChildDepth(0), "Child of root must be 1.");
        AssertTrue(DelegationDepth.CanSpawnFrom(3), "Depth 3 may spawn depth 4.");
        AssertTrue(!DelegationDepth.CanSpawnFrom(4), "Depth 4 must not spawn.");
        var denied = DelegationDepth.EvaluateRequestedDepth(4, null);
        AssertEqual(SpawnOutcomeKind.Denied, denied.Outcome, "Spawn from depth 4 must be denied.");
        AssertEqual(SpawnDenialReason.DepthLimit, denied.Denial, "Depth 5 must use DEPTH_LIMIT.");
    }

    private static void ConcurrencyFourQueuesTheFifthReadyTask()
    {
        var runs = Enumerable.Range(0, 4).Select(index => Run($"run-{index}", null, 0, "running")).ToArray();
        var snapshot = Snapshot(runs, [Task("queued", 1, 1, "ready", "run-0")]);
        var view = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        AssertEqual(4, view.ActiveRunCount, "Four active runs must occupy the budget.");
        AssertEqual("queued", view.ReadyTaskIds.Single(), "Fifth READY task must remain queued, not failed.");
        var spawn = SpawnPolicy.Evaluate(true, false, 0, null, 4, ConcurrencyBudget.Default, false);
        AssertEqual(SpawnOutcomeKind.Queued, spawn.Outcome, "Capacity 4/4 must queue, not fail.");
    }

    private static void ChildSharesParentTreeBudget()
    {
        var snapshot = Snapshot(
            [
                Run("root", null, 0, "running"),
                Run("c1", "root", 1, "running"),
                Run("c2", "root", 1, "running"),
                Run("c3", "root", 1, "starting")
            ],
            []);
        AssertEqual(4, SchedulerProjection.CountActiveInTree("c2", snapshot), "Children must share the parent tree budget.");
        var spawn = SpawnPolicy.Evaluate(true, false, 1, null, 4, ConcurrencyBudget.Default, false);
        AssertEqual(SpawnOutcomeKind.Queued, spawn.Outcome, "A child must not receive a fresh budget of 4.");
    }

    private static void BudgetCanAdaptDownAndUpButNeverExceedFour()
    {
        var policy = new MutableConcurrencyBudgetPolicy();
        policy.SetEffective(2);
        AssertEqual(2, policy.Current.Effective, "Budget 4→2 failed.");
        policy.SetEffective(1);
        AssertEqual(1, policy.Current.Effective, "Budget 2→1 failed.");
        policy.SetEffective(4);
        AssertEqual(4, policy.Current.Effective, "Budget 1→4 failed.");
        AssertEqual(4, ConcurrencyBudget.FromEffective(99).Effective, "Budget increase must clamp to ConfiguredMax.");
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new ConcurrencyBudget(5), "ConfiguredMax must reject 5.");
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new ConcurrencyBudget(0), "Budget 0 is illegal.");
    }

    private static void CapacityFullIsQueuedNotSecurityDenial()
    {
        var queued = SpawnPolicy.Evaluate(true, false, 0, null, 4, ConcurrencyBudget.Default, false);
        var denied = SpawnPolicy.Evaluate(false, false, 0, null, 0, ConcurrencyBudget.Default, false);
        AssertEqual(SpawnOutcomeKind.Queued, queued.Outcome, "Full capacity was treated as a security denial.");
        AssertEqual(SpawnDenialReason.None, queued.Denial, "Queued spawn must not carry a denial reason.");
        AssertEqual(SpawnOutcomeKind.Denied, denied.Outcome, "Missing Agent.Spawn must deny.");
        AssertEqual(SpawnDenialReason.AgentSpawnDenied, denied.Denial, "Missing capability must be AgentSpawnDenied.");
    }

    private static void CancelledParentSpawnIsDenied()
    {
        var denied = SpawnPolicy.Evaluate(true, true, 0, null, 0, ConcurrencyBudget.Default, false);
        AssertEqual(SpawnOutcomeKind.Denied, denied.Outcome, "Cancelled parent spawn must be denied.");
        AssertEqual(SpawnDenialReason.Cancelled, denied.Denial, "Cancelled parent must use Cancelled, not busy.");
    }

    private static void CancelCascadeIsIdempotentAndScoped()
    {
        var snapshot = Snapshot(
            [
                Run("root", null, 0, "running", "wf-a"),
                Run("child", "root", 1, "running", "wf-a"),
                Run("other", null, 0, "running", "wf-b")
            ],
            [
                Task("t-root", 1, 1, "running", "root"),
                Task("t-child", 1, 2, "running", "child"),
                Task("t-other", 1, 3, "running", "other")
            ],
            attempts:
            [
                Attempt("a1", "t-root", 1, "running"),
                Attempt("a2", "t-child", 1, "running"),
                Attempt("a3", "t-other", 1, "running")
            ]);
        var first = CancellationCascade.CascadeRunIds("root", snapshot);
        var second = CancellationCascade.CascadeRunIds("root", snapshot);
        AssertEqual(string.Join(',', first), string.Join(',', second), "Cancel cascade must be idempotent.");
        AssertTrue(first.Contains("root") && first.Contains("child"), "Cascade missed tree members.");
        AssertTrue(!first.Contains("other"), "Cascade leaked into another workflow/run.");
        var tasks = CancellationCascade.CascadeTaskIds(first, snapshot);
        AssertTrue(tasks.Contains("t-root") && tasks.Contains("t-child") && !tasks.Contains("t-other"),
            "Task cancel scope leaked.");
    }

    private static void RetryContinuesSameTaskWithNewAttemptNumber()
    {
        AssertTrue(RuntimeLifecycle.CanRetryTask(RuntimeTaskStatus.Failed), "Failed tasks must be retryable.");
        AssertTrue(!RuntimeLifecycle.IsLegal(RuntimeTaskStatus.Failed, RuntimeTaskStatus.Completed),
            "Retry must not jump failed → completed.");
        AssertTrue(RuntimeLifecycle.IsLegal(RuntimeTaskStatus.Failed, RuntimeTaskStatus.Ready), "Retry returns the same task to READY.");
        AssertTrue(!RuntimeLifecycle.IsLegal(AttemptStatus.Failed, AttemptStatus.Running),
            "A failed attempt must not resume; a new attempt is required.");
    }

    private static void IllegalLifecycleTransitionsAreRejected()
    {
        AssertTrue(!RuntimeLifecycle.Transition(RuntimeTaskStatus.Completed, RuntimeTaskStatus.Running).Allowed, "completed → running");
        AssertTrue(!RuntimeLifecycle.Transition(RuntimeTaskStatus.Cancelled, RuntimeTaskStatus.Running).Allowed, "cancelled → running");
        AssertTrue(!RuntimeLifecycle.Transition(RuntimeTaskStatus.Failed, RuntimeTaskStatus.Completed).Allowed, "failed → completed");
        AssertTrue(!RuntimeLifecycle.Transition(RunStatus.Completed, RunStatus.Running).Allowed, "run completed → running");
        AssertTrue(!RuntimeLifecycle.Transition(RunStatus.Cancelled, RunStatus.Running).Allowed, "run cancelled → running");
        AssertTrue(!RuntimeLifecycle.Transition(WorkflowRunStatus.Completed, WorkflowRunStatus.Running).Allowed,
            "workflow completed → running");
        AssertTrue(RuntimeLifecycle.Transition(RunStatus.Interrupted, RunStatus.Starting).Allowed,
            "Resume continues the same interrupted Run.");
        AssertEqual(RuntimeRejectionCode.IllegalTransition,
            RuntimeLifecycle.Transition(AttemptStatus.Unknown, AttemptStatus.Running).Rejection,
            "UNKNOWN attempts cannot automatically resume.");
    }

    private static void CompletedTaskIsNeverRequeued()
    {
        var snapshot = Snapshot(
            [Run("root", null, 0, "running")],
            [
                Task("done", 9, 1, "completed", "root"),
                Task("ready", 1, 2, "ready", "root")
            ]);
        var view = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        AssertTrue(!view.ReadyTaskIds.Contains("done"), "Completed tasks must never re-enter READY.");
        AssertEqual("ready", view.ReadyTaskIds.Single(), "Only unfinished READY work should remain.");
    }

    private static void CheckpointV1SerializesDeterministicallyAndExcludesSecrets()
    {
        var first = CheckpointV1.Create(
            "plan-1",
            "plan-digest",
            "{\"b\":2,\"a\":1}",
            "{\"token\":\"secret-value\",\"visible\":\"ok\"}",
            "summary",
            [new CheckpointCriticalMessage(1, "user", "hi")],
            [new CheckpointToolReference("tool-b", "read", "payload"), new CheckpointToolReference("tool-a", "read", "payload")],
            ["appr-b", "appr-a"],
            ["ctx-1"],
            ["ev-1"],
            ["digest-b", "digest-a"],
            "prompt-1",
            "provider-1",
            "model-1",
            "eff-1");
        var json = CanonicalJson.WriteCheckpoint(first);
        var again = CanonicalJson.WriteCheckpoint(first);
        AssertEqual(json, again, "Checkpoint payload must be deterministic.");
        AssertTrue(json.Contains("\"schemaVersion\":1", StringComparison.Ordinal), "Checkpoint v1 version field missing.");
        AssertTrue(!json.Contains("secret-value", StringComparison.Ordinal), "Secrets must be excluded from agent state.");
        AssertTrue(!json.Contains("\"token\"", StringComparison.Ordinal), "Token properties must be redacted.");
        AssertTrue(json.Contains("visible", StringComparison.Ordinal), "Non-secret agent state was dropped.");
        var parsed = CanonicalJson.Parse(json, 1);
        AssertEqual(1, parsed.SchemaVersion, "Parsed schema version drifted.");
        AssertThrows<CheckpointSchemaException>(() => CanonicalJson.Parse(json, 99), "Unsupported schema must fail typed.");
        AssertThrows<CheckpointSchemaException>(() => CanonicalJson.Parse("{", 1), "Corrupt checkpoint must fail typed.");
    }

    private static void CheckpointRetainsLatestTwentyMessagesAndTruncatesToolRefs()
    {
        var messages = Enumerable.Range(1, 25).Select(index => new CheckpointCriticalMessage(index, "assistant", "m" + index)).ToArray();
        var retained = CheckpointV1.RetainLatestMessages(messages);
        AssertEqual(20, retained.Count, "Must retain latest 20 critical messages.");
        AssertEqual(6, retained[0].Sequence, "Oldest retained sequence is wrong.");
        AssertEqual(25, retained[^1].Sequence, "Newest retained sequence is wrong.");
        var oversized = new string('x', (256 * 1024 * 2) + 50);
        var truncated = CheckpointV1.TruncateHeadTail(oversized, CheckpointV1.ToolReferenceHeadTailBytes);
        AssertTrue(truncated.Contains('…'), "Oversized tool refs must use head+tail truncation.");
        AssertTrue(truncated.Length < oversized.Length, "Tool ref truncation did not shrink payload.");
    }

    private static void ResumeClassifierCoversFrozenDecisionsAndUnknownSideEffect()
    {
        var run = Run("run-1", null, 0, "interrupted");
        var checkpoint = new DurableCheckpointRecord("cp-1", "run-1", "t-1", 1, "{}", "{\"object:obj-1\":\"digest-old\"}", 10);
        var unchanged = ResumeClassifier.Classify(run, checkpoint, Fresh());
        AssertEqual(ResumeDecisionKind.Continue, unchanged.Kind, "Unchanged inputs must CONTINUE.");
        var replan = ResumeClassifier.Classify(run, checkpoint, Fresh(objectChanged: true));
        AssertEqual(ResumeDecisionKind.Replan, replan.Kind, "Changed but valid plan must REPLAN.");
        var restartTask = ResumeClassifier.Classify(run, checkpoint, Fresh(planInvalid: true));
        AssertEqual(ResumeDecisionKind.RestartTask, restartTask.Kind, "Invalid plan must RESTART_TASK.");
        var restartRun = ResumeClassifier.Classify(run, checkpoint, Fresh(structural: true));
        AssertEqual(ResumeDecisionKind.RestartRun, restartRun.Kind, "Structural invalidation must RESTART_RUN.");
        var blocked = ResumeClassifier.Classify(run, checkpoint, Fresh(unknown: true));
        AssertEqual(ResumeDecisionKind.BlockUnknown, blocked.Kind, "UNKNOWN side effect must block.");
    }

    private static void ResumeClassifierComparesCurrentInputsToExactCheckpointBaseline()
    {
        var run = Run("run-1", null, 0, "interrupted") with
        {
            PromptConfigId = "P-run",
            ProviderId = "prov-run",
            ModelId = "m-run",
            EffectivePromptDigest = "e-run"
        };
        var checkpoint = new DurableCheckpointRecord(
            "cp-1",
            "run-1",
            "t-1",
            1,
            "{}",
            """{"effectivePromptDigest":"E1","modelId":"M1","promptConfigId":"P1","providerId":"A"}""",
            10);
        var matching = Fresh() with
        {
            PromptConfigId = "P1",
            ProviderId = "A",
            ModelId = "M1",
            EffectivePromptDigest = "E1"
        };
        AssertEqual(ResumeDecisionKind.Continue, ResumeClassifier.Classify(run, checkpoint, matching).Kind,
            "CURRENT matching the exact checkpoint baseline must CONTINUE even when the Run row differs.");
        AssertEqual(ResumeDecisionKind.Replan, ResumeClassifier.Classify(run, checkpoint, matching with { PromptConfigId = "P2" }).Kind,
            "PromptConfigId change vs checkpoint baseline must REPLAN.");
        AssertEqual(ResumeDecisionKind.Replan, ResumeClassifier.Classify(run, checkpoint, matching with { ProviderId = "B" }).Kind,
            "ProviderId change vs checkpoint baseline must REPLAN.");
        AssertEqual(ResumeDecisionKind.Replan, ResumeClassifier.Classify(run, checkpoint, matching with { ModelId = "M2" }).Kind,
            "ModelId change vs checkpoint baseline must REPLAN.");
        AssertEqual(ResumeDecisionKind.Replan, ResumeClassifier.Classify(run, checkpoint, matching with { EffectivePromptDigest = "E2" }).Kind,
            "EffectivePromptDigest change vs checkpoint baseline must REPLAN.");
        var later = checkpoint with { CheckpointId = "cp-2", CreatedAtMs = 20, InputDigestSetJson = "{}" };
        AssertEqual(ResumeDecisionKind.Continue, ResumeClassifier.Classify(run, checkpoint, matching).Kind,
            "An unrelated later checkpoint must not replace CP1 as the comparison baseline.");
        _ = later;
        var unknownBaseline = new DurableCheckpointRecord("cp-unknown", "run-1", "t-1", 1, "{}", "{}", 10);
        AssertEqual(ResumeDecisionKind.Continue,
            ResumeClassifier.Classify(run, unknownBaseline, matching with { PromptConfigId = "P2" }).Kind,
            "A checkpoint that retained no prompt/provider/model baseline must not invent a difference.");
        AssertEqual(ResumeDecisionKind.BlockUnknown, ResumeClassifier.Classify(run, checkpoint, matching with { UnknownSideEffect = true }).Kind,
            "UNKNOWN must remain BLOCK_UNKNOWN.");
    }

    private static void UnrelatedDraftChangeDoesNotStaleTask()
    {
        var run = Run("run-1", null, 0, "interrupted");
        var checkpoint = new DurableCheckpointRecord("cp-1", "run-1", "t-1", 1, "{}", "{\"object:obj-1\":\"digest-old\"}", 10);
        var decision = ResumeClassifier.Classify(run, checkpoint, Fresh(unrelatedDraftOnly: true, objectChanged: true));
        AssertEqual(ResumeDecisionKind.Continue, decision.Kind, "Unrelated Draft changes must not stale the Task.");
    }

    private static void UnknownSideEffectBlocksAutomaticRetry()
    {
        var tools = new[] { new DurableToolCallRecord("tc-1", "run-1", "task-1", "write", "running", "unknown") };
        AssertTrue(UnknownSideEffectPolicy.BlocksAutomaticRetry(tools, "task-1"), "UNKNOWN must block auto retry.");
        AssertTrue(!UnknownSideEffectPolicy.BlocksAutomaticRetry(tools, "task-2"), "UNKNOWN must stay scoped.");
        var spawn = SpawnPolicy.Evaluate(true, false, 0, null, 0, ConcurrencyBudget.Default, unknownSideEffect: true);
        AssertEqual(SpawnDenialReason.UnknownSideEffect, spawn.Denial, "UNKNOWN must not auto-spawn/retry.");
    }

    private static void RequiredUnsatisfiedDependencyBlocksDispatch()
    {
        var snapshot = Snapshot(
            [Run("root", null, 0, "running")],
            [Task("consumer", 1, 1, "pending", "root"), Task("producer", 1, 2, "ready", "root")],
            dependencies:
            [
                new DurableDependencyRecord("dep-1", "consumer", "producer", "required", "unsatisfied")
            ]);
        var view = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        AssertTrue(view.BlockedTaskIds.Contains("consumer"), "Required unsatisfied dependency must block.");
        AssertTrue(view.ReadyTaskIds.Contains("producer"), "Producer without required deps should be READY.");
    }

    private static void DepthSpoofIsRejectedWithoutClamping()
    {
        var spoof = DelegationDepth.EvaluateRequestedDepth(3, 0);
        AssertEqual(SpawnOutcomeKind.Denied, spoof.Outcome, "Depth spoof must be denied.");
        AssertEqual(SpawnDenialReason.DepthSpoof, spoof.Denial, "Spoofed depth=0 must not be clamped.");
        var legal = DelegationDepth.EvaluateRequestedDepth(3, 4);
        AssertEqual(SpawnOutcomeKind.Allowed, legal.Outcome, "Derived depth 4 must be allowed.");
    }

    private static void FailedAndPausedTasksAreNotReadyAfterRebuild()
    {
        var snapshot = Snapshot(
            [Run("root", null, 0, "running")],
            [
                Task("failed", 9, 1, "failed", "root"),
                Task("paused", 8, 2, "paused", "root"),
                Task("ready", 1, 3, "ready", "root"),
                Task("pending", 1, 4, "pending", "root"),
                Task("blocked-status", 1, 5, "blocked", "root"),
                Task("done", 9, 6, "completed", "root"),
                Task("cancelled", 9, 7, "cancelled", "root"),
                Task("struct-blocked", 1, 8, "pending", "root")
            ],
            dependencies:
            [
                new DurableDependencyRecord("dep-1", "struct-blocked", "ready", "required", "unsatisfied")
            ]);
        var view = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        AssertTrue(!view.ReadyTaskIds.Contains("failed"), "Failed tasks must not become READY on rebuild.");
        AssertTrue(!view.ReadyTaskIds.Contains("paused"), "Paused tasks must not become READY on rebuild.");
        AssertTrue(!view.ReadyTaskIds.Contains("done"), "Completed tasks must never requeue.");
        AssertTrue(!view.ReadyTaskIds.Contains("cancelled"), "Cancelled tasks must never requeue.");
        AssertTrue(view.BlockedTaskIds.Contains("struct-blocked"), "Structurally blocked tasks must remain blocked.");
        AssertEqual("ready,pending,blocked-status", string.Join(',', view.ReadyTaskIds),
            "Ready/Pending/structurally-unblocked Blocked may enqueue; Failed/Paused must not.");
        var again = SchedulerProjection.Rebuild(snapshot, ConcurrencyBudget.Default);
        AssertEqual(string.Join(',', view.ReadyTaskIds), string.Join(',', again.ReadyTaskIds), "Rebuild ordering must stay deterministic.");
    }

    private static void TaskCancelCascadeOwnsDescendantsWithoutTheContainingRun()
    {
        var snapshot = Snapshot(
            [
                Run("root", null, 0, "running"),
                Run("owned-child", "root", 1, "running"),
                Run("unrelated", null, 0, "running")
            ],
            [
                Task("t-a", 1, 1, "running", "root"),
                Task("t-b", 1, 2, "ready", "root"),
                Task("t-owned", 1, 3, "ready", "owned-child", "t-a"),
                Task("t-unrelated", 1, 4, "ready", "unrelated")
            ]);
        var ownedTasks = CancellationCascade.CascadeOwnedTaskIds("t-a", snapshot);
        AssertTrue(ownedTasks.Contains("t-a") && ownedTasks.Contains("t-owned") && !ownedTasks.Contains("t-b"),
            "Task cancel ownership must follow durable parent_task_id only.");
        var ownedRuns = CancellationCascade.CascadeOwnedChildRunIds("t-a", snapshot);
        AssertTrue(ownedRuns.Contains("owned-child") && !ownedRuns.Contains("root") && !ownedRuns.Contains("unrelated"),
            "Task cancel must not treat the containing Run as cancelled.");
    }

    private static DurableTaskRecord Task(string id, int priority, long created, string status = "ready", string runId = "run", string? parentTaskId = null) =>
        new(id, runId, parentTaskId, "write", status, priority, created, created);

    private static DurableRunRecord Run(string id, string? parent, int depth, string status, string workflow = "wf-1") =>
        new(id, workflow, parent, "pm", status, depth, 1, 1);

    private static DurableAttemptRecord Attempt(string id, string taskId, int number, string status) =>
        new(id, taskId, number, status, 1, null);

    private static SchedulerSnapshot Snapshot(
        DurableRunRecord[] runs,
        DurableTaskRecord[] tasks,
        DurableAttemptRecord[]? attempts = null,
        DurableDependencyRecord[]? dependencies = null) =>
        new(
            [new DurableWorkflowRunRecord(runs[0].WorkflowRunId, "running", 1, 1)],
            runs,
            tasks,
            attempts ?? [],
            dependencies ?? [],
            [],
            []);

    private static FreshnessInputs Fresh(
        bool objectChanged = false,
        bool planInvalid = false,
        bool structural = false,
        bool unrelatedDraftOnly = false,
        bool unknown = false) =>
        new(
            "rev-1",
            objectChanged
                ? new Dictionary<string, string> { ["obj-1"] = "digest-new" }
                : new Dictionary<string, string>(),
            null,
            null,
            null,
            new Dictionary<string, string>(),
            null,
            null,
            new Dictionary<string, string>(),
            structural,
            planInvalid,
            unrelatedDraftOnly,
            unknown);
}
