using System.Threading;
using LLMW.Writing.Domain.Runtime;

namespace LLMW.Writing.Application.Runtime;

public enum RuntimeError
{
    NotFound,
    IllegalTransition,
    DepthLimit,
    DepthSpoof,
    SpawnDenied,
    UnknownSideEffect,
    CheckpointUnsupported,
    CheckpointCorrupt,
    Cancelled,
    BindingUnavailable,
    WorkerLaunchFailed,
    CompletionFailed,
    SemanticReviewRequired,
    OversightDenied,
    TaskOwnershipDenied,
    ResultFrozen,
    IllegalCompletionLifecycle,
    GrillAuthorRequired,
    GrillAlreadyResolved,
    GrillOptionRejected,
    GrillOwnershipDenied,
    SpecialistImmutable,
    SpecialistInvalid,
    SpecialistIdentityMismatch,
    BackgroundIllegalTransition,
    BackgroundStopUnavailable
}

public sealed record RuntimeFailure(RuntimeError Code, string? Detail = null);

public sealed record RuntimeResult<T>(T? Value, RuntimeFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class RuntimeResults
{
    public static RuntimeResult<T> Success<T>(T value) => new(value, null);

    public static RuntimeResult<T> Fail<T>(RuntimeError code, string? detail = null) =>
        new(default, new RuntimeFailure(code, detail));
}

public enum SchedulerFaultPoint
{
    None,
    BeforeDurableDispatch,
    AfterTaskRunningBeforeAttempt,
    AfterAttemptBeforeWorkerLaunch,
    AfterWorkerLaunchBeforeAck,
    CheckpointWrite,
    CancelRacingStart,
    BudgetChangeDuringDispatch,
    AfterResultBeforeTaskComplete,
    AfterTaskBeforeResultPersist,
    GrillResolutionRace,
    BackgroundStopRaceComplete
}

public interface ISchedulerFaultInjector
{
    SchedulerFaultPoint Fault { get; }
}

public sealed class NoSchedulerFaultInjector : ISchedulerFaultInjector
{
    public static NoSchedulerFaultInjector Instance { get; } = new();

    private NoSchedulerFaultInjector()
    {
    }

    public SchedulerFaultPoint Fault => SchedulerFaultPoint.None;
}

public sealed class MutableSchedulerFaultInjector : ISchedulerFaultInjector
{
    public SchedulerFaultPoint Fault { get; set; } = SchedulerFaultPoint.None;
}

public enum RuntimeLinearizationGate
{
    None,
    BeforeOversightActivationLock,
    InsideOversightActivationLock,
    BeforeProviderInvocationPersist
}

public interface IRuntimeLinearizationBarrier
{
    void Enter(RuntimeLinearizationGate gate);
}

public sealed class NoRuntimeLinearizationBarrier : IRuntimeLinearizationBarrier
{
    public static NoRuntimeLinearizationBarrier Instance { get; } = new();

    private NoRuntimeLinearizationBarrier()
    {
    }

    public void Enter(RuntimeLinearizationGate gate) => _ = gate;
}

public sealed class ManualRuntimeLinearizationBarrier : IRuntimeLinearizationBarrier, IDisposable
{
    public const int DefaultTimeoutMs = 15_000;

    private readonly RuntimeLinearizationGate waitAt;
    private readonly ManualResetEventSlim entered = new(false);
    private readonly ManualResetEventSlim hold = new(false);
    private readonly int timeoutMs;
    private volatile bool armed;

    public ManualRuntimeLinearizationBarrier(RuntimeLinearizationGate waitAt, int timeoutMs = DefaultTimeoutMs)
    {
        this.waitAt = waitAt;
        this.timeoutMs = timeoutMs;
    }

    public void Arm() => armed = true;

    public void Enter(RuntimeLinearizationGate gate)
    {
        if (!armed || gate != waitAt)
        {
            return;
        }

        entered.Set();
        if (!hold.Wait(timeoutMs))
        {
            throw new TimeoutException("Runtime linearization barrier was not released.");
        }
    }

    public void WaitUntilEntered()
    {
        if (!entered.Wait(timeoutMs))
        {
            throw new TimeoutException("Runtime linearization barrier was not entered.");
        }
    }

    public void Release() => hold.Set();

    public void Dispose()
    {
        entered.Dispose();
        hold.Dispose();
    }
}

public sealed class SchedulerFaultInjectedException : Exception
{
    public SchedulerFaultInjectedException(SchedulerFaultPoint point)
        : base("Injected scheduler fault: " + point)
    {
        Point = point;
    }

    public SchedulerFaultPoint Point { get; }
}

public interface IRuntimeLogicalTimestampAllocator
{
    long AllocateCreatedAtMs(long wallClockMs);
}

public interface IRuntimePersistence
{
    SchedulerSnapshot LoadSnapshot();

    DurableWorkflowRunRecord InsertWorkflowRun(string workflowRunId, string status, long nowMs, string? storylineId = null);

    DurableRunRecord InsertRun(DurableRunRecord run);

    DurableTaskRecord InsertTask(DurableTaskRecord task);

    DurableAttemptRecord InsertAttempt(DurableAttemptRecord attempt);

    void UpdateWorkflowRunStatus(string workflowRunId, string status, long nowMs);

    void UpdateRunStatus(string runId, string status, long nowMs);

    void UpdateRun(DurableRunRecord run);

    void UpdateTaskStatus(string taskId, string status, long nowMs);

    void UpdateAttemptStatus(string attemptId, string status, long? completedAtMs);

    void UpdateDependencyStatus(string producerTaskId, string status);

    string InsertCheckpoint(DurableCheckpointRecord checkpoint);

    DurableWorkflowRunRecord? GetWorkflowRun(string workflowRunId);

    DurableRunRecord? GetRun(string runId);

    DurableTaskRecord? GetTask(string taskId);

    DurableAttemptRecord? GetAttempt(string attemptId);

    DurableAttemptRecord? FindStartingAttempt(string taskId);

    int MaxAttemptNo(string taskId);

    void InsertDependency(DurableDependencyRecord dependency);

    IReadOnlyList<DurableCheckpointRecord> CheckpointsForRun(string runId);

    IReadOnlyList<DurableToolCallRecord> ToolCallsFor(string? runId, string? taskId);

    void InsertToolCall(DurableToolCallRecord toolCall);

    void MarkRunningToolCallsUnknown(string runId);

    void InTransaction(Action action);

    void UpdateTaskCompletionContract(string taskId, string? completionContractJson);

    DurableResultArtifactRecord InsertResultArtifact(DurableResultArtifactRecord artifact);

    void ReplaceResultArtifact(DurableResultArtifactRecord artifact);

    DurableResultArtifactRecord? GetLatestResultArtifact(string taskId);

    DurableResultArtifactRecord? GetResultArtifact(string resultArtifactId);

    void InsertEvidence(EvidenceRecord evidence);

    IReadOnlyList<EvidenceRecord> EvidenceForTask(string taskId);

    EvidenceRecord? GetEvidence(string evidenceId);

    void MarkEvidenceStale(string evidenceId, bool stale);

    DurableDependencyRecord? GetDependency(string dependencyId);

    IReadOnlyList<DurableDependencyRecord> DependenciesForConsumer(string consumerTaskId);

    void UpdateDependencyRecord(string dependencyId, string kind, string status, string? resultArtifactId);

    OversightOverrideRecord InsertOversightOverride(OversightOverrideRecord record);

    IReadOnlyList<OversightOverrideRecord> ListOversightOverrides();

    void BindPendingOversightOverrides(string checkpointId, string runId, string? taskId, long checkpointCreatedAtMs);

    void InsertDelegatedDecision(DelegatedDecisionRecord record);

    DelegatedDecisionRecord? GetDelegatedDecision(string delegatedDecisionId);

    IReadOnlyList<DelegatedDecisionRecord> ListDelegatedDecisions();

    void InsertApproval(DurableApprovalRecord record);

    DurableApprovalRecord? GetApproval(string approvalId);

    void UpdateApproval(DurableApprovalRecord record);

    bool TryCompareAndSetApproval(string approvalId, string expectedStatus, DurableApprovalRecord replacement);

    IReadOnlyList<DurableApprovalRecord> ListApprovals(string? runId);

    void InsertBackgroundTask(DurableBackgroundTaskRecord record);

    void UpdateBackgroundTask(DurableBackgroundTaskRecord record);

    DurableBackgroundTaskRecord? GetBackgroundTask(string backgroundTaskId);

    IReadOnlyList<DurableBackgroundTaskRecord> ListBackgroundTasks(string? ownerRunId);

    void UpsertProjectSpecialist(DurableProjectSpecialistRecord record);

    DurableProjectSpecialistRecord? GetProjectSpecialist(string profileId);

    IReadOnlyList<DurableProjectSpecialistRecord> ListProjectSpecialists();

    DurableAttemptRecord? FindActiveAttempt(string taskId);

    bool StorylineExists(string storylineId);

    DurableToolCallRecord? GetToolCall(string toolCallId);

    bool TryCancelToolCall(string toolCallId);
}

public sealed record WorkerLaunchRequest(
    string RunId,
    string TaskId,
    string AttemptId,
    string Role);

public sealed record WorkerLaunchResult(
    string WorkerInstanceId,
    string LaunchBindingId,
    string ChannelInstanceId);

public interface IRunWorkerSupervisor
{
    WorkerLaunchResult Launch(WorkerLaunchRequest request);

    bool Release(string workerInstanceId);

    bool IsAlive(string workerInstanceId);

    IReadOnlyList<LiveWorkerObservation> Snapshot();
}

public sealed record LiveWorkerObservation(
    string WorkerInstanceId,
    string LaunchBindingId,
    string RunId,
    bool Alive);
