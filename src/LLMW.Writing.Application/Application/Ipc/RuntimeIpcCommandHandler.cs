using System.Text.Json;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Runtime;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Ipc;

public sealed class RuntimeIpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly RuntimeSchedulerService scheduler;
    private readonly string workspaceInstanceId;

    public RuntimeIpcCommandHandler(RuntimeSchedulerService scheduler, string workspaceInstanceId)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        this.workspaceInstanceId = workspaceInstanceId;
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return Task.FromResult(Handle(context));
        }
        catch (SchedulerFaultInjectedException)
        {
            throw;
        }
        catch (JsonException)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(
                Error(context, IpcErrorCodes.MalformedFrame, "The WP12 command payload is malformed."));
        }
    }

    private IpcApplicationCommandResult? Handle(IpcApplicationCommandContext context)
    {
        return context.SemanticType switch
        {
            IpcSemanticTypes.LoadSchedulerSnapshot => LoadSnapshot(context),
            IpcSemanticTypes.CreateWorkflowRun => CreateWorkflow(context),
            IpcSemanticTypes.CreateRun => CreateRun(context),
            IpcSemanticTypes.CreateTask => CreateTask(context),
            IpcSemanticTypes.DispatchReadyTask => Dispatch(context),
            IpcSemanticTypes.CancelRuntimeScope => Cancel(context),
            IpcSemanticTypes.RetryTask => Retry(context),
            IpcSemanticTypes.PersistCheckpoint => PersistCheckpoint(context),
            IpcSemanticTypes.ClassifyResume => ClassifyResume(context),
            IpcSemanticTypes.LaunchRunWorker => LaunchWorker(context),
            IpcSemanticTypes.ReleaseRunWorker => ReleaseWorker(context),
            IpcSemanticTypes.ReconcileRunWorkers => Reconcile(context),
            IpcSemanticTypes.SpawnChildRun => Spawn(context),
            _ => null
        };
    }

    private IpcApplicationCommandResult LoadSnapshot(IpcApplicationCommandContext context)
    {
        var snapshot = scheduler.LoadSnapshot();
        var view = scheduler.RebuildView();
        var dto = new SchedulerSnapshotDto(
            snapshot.WorkflowRuns.Select(item => new RuntimeWorkflowDto(item.WorkflowRunId, item.Status, item.CreatedAtMs, item.UpdatedAtMs)).ToArray(),
            snapshot.Runs.Select(item => new RuntimeRunDto(item.RunId, item.WorkflowRunId, item.ParentRunId, item.Role, item.Status, item.Depth, item.CreatedAtMs, item.UpdatedAtMs)).ToArray(),
            snapshot.Tasks.Select(item => new RuntimeTaskDto(item.TaskId, item.RunId, item.ParentTaskId, item.TaskKind, item.Status, item.Priority, item.CreatedAtMs, item.UpdatedAtMs)).ToArray(),
            snapshot.Attempts.Select(item => new RuntimeAttemptDto(item.AttemptId, item.TaskId, item.AttemptNo, item.Status, item.StartedAtMs, item.CompletedAtMs)).ToArray(),
            snapshot.Dependencies.Select(item => new RuntimeDependencyDto(item.DependencyId, item.ConsumerTaskId, item.ProducerTaskId, item.DependencyKind, item.Status)).ToArray(),
            snapshot.ToolCalls.Select(item => new RuntimeToolCallDto(item.ToolCallId, item.RunId, item.TaskId, item.ToolName, item.Status, item.SideEffectState)).ToArray(),
            snapshot.Checkpoints.Select(item => new RuntimeCheckpointDto(item.CheckpointId, item.RunId, item.TaskId, item.SchemaVersion, item.CreatedAtMs)).ToArray(),
            view.ReadyTaskIds.ToArray(),
            view.BlockedTaskIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            view.ActiveRunCount,
            view.EffectiveBudget);
        return Respond(context, new LoadSchedulerSnapshotResponse(dto), IpcJsonContext.Default.LoadSchedulerSnapshotResponseEnvelope);
    }

    private IpcApplicationCommandResult CreateWorkflow(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateWorkflowRunRequest);
        _ = request;
        var result = scheduler.CreateWorkflowRun(null);
        return result.Succeeded && result.Value is not null
            ? Respond(context, new CreateWorkflowRunResponse(result.Value.WorkflowRunId, result.Value.Status), IpcJsonContext.Default.CreateWorkflowRunResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult CreateRun(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateRunRequest);
        var result = scheduler.CreateRun(request.WorkflowRunId, request.Role, request.ParentRunId, request.RunId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, new CreateRunResponse(result.Value.RunId, result.Value.Depth, result.Value.Status), IpcJsonContext.Default.CreateRunResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult CreateTask(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateTaskRequest);
        var result = scheduler.CreateTask(request.RunId, request.TaskKind, request.Priority, request.ParentTaskId, request.TaskId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, new CreateTaskResponse(result.Value.TaskId, result.Value.Status), IpcJsonContext.Default.CreateTaskResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Dispatch(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.DispatchReadyTaskRequest);
        var result = scheduler.DispatchReadyTask(request.TaskId);
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new DispatchReadyTaskResponse(result.Value.TaskId, result.Value.RunId, result.Value.AttemptId, result.Value.AttemptNo, result.Value.Outcome),
                IpcJsonContext.Default.DispatchReadyTaskResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Cancel(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CancelRuntimeScopeRequest);
        var result = scheduler.CancelScope(request.ScopeKind, request.ScopeId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, new CancelRuntimeScopeResponse(result.Value.Cancelled, result.Value.AffectedRunIds), IpcJsonContext.Default.CancelRuntimeScopeResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Retry(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.RetryTaskRequest);
        var result = scheduler.RetryTask(request.TaskId);
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new RetryTaskResponse(result.Value.TaskId, result.Value.AttemptId, result.Value.AttemptNo, result.Value.Outcome),
                IpcJsonContext.Default.RetryTaskResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult PersistCheckpoint(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.PersistCheckpointRequest);
        var result = scheduler.PersistCheckpoint(
            request.RunId,
            request.TaskId,
            request.SchemaVersion,
            request.PayloadJson,
            request.InputDigestSetJson);
        return result.Succeeded && result.Value is not null
            ? Respond(context, new PersistCheckpointResponse(result.Value), IpcJsonContext.Default.PersistCheckpointResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult ClassifyResume(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ClassifyResumeRequest);
        var inputs = new FreshnessInputs(
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            request.StructuralInvalidation,
            request.PlanInvalid,
            request.UnrelatedDraftOnly,
            false);
        var result = scheduler.ClassifyResume(request.RunId, inputs);
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new ClassifyResumeResponse(
                    ResumeDecisionCodec.ToDurableValue(result.Value.Kind),
                    result.Value.Reason,
                    result.Value.CheckpointId),
                IpcJsonContext.Default.ClassifyResumeResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult LaunchWorker(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.LaunchRunWorkerRequest);
        var result = scheduler.LaunchRunWorker(request.RunId, request.TaskId, request.AttemptId);
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new LaunchRunWorkerResponse(result.Value.WorkerInstanceId, result.Value.LaunchBindingId, "launched"),
                IpcJsonContext.Default.LaunchRunWorkerResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult ReleaseWorker(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ReleaseRunWorkerRequest);
        var result = scheduler.ReleaseRunWorker(request.WorkerInstanceId);
        return result.Succeeded
            ? Respond(context, new ReleaseRunWorkerResponse(result.Value), IpcJsonContext.Default.ReleaseRunWorkerResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Reconcile(IpcApplicationCommandContext context)
    {
        _ = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ReconcileRunWorkersRequest);
        var result = scheduler.ReconcileWorkers();
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new ReconcileRunWorkersResponse(result.Value.Select(item => new WorkerReconcileDto(item.Classification, item.RunId, item.WorkerInstanceId)).ToArray()),
                IpcJsonContext.Default.ReconcileRunWorkersResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Spawn(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.SpawnChildRunRequest);
        if (!string.IsNullOrWhiteSpace(context.Channel?.BoundRunId) &&
            !StringComparer.Ordinal.Equals(context.Channel.BoundRunId, request.ParentRunId))
        {
            return Error(context, IpcErrorCodes.BindingMismatch, "Worker BoundRunId does not match the parent Run.");
        }

        if (context.Principal is { Kind: PrincipalKind.AgentRun } &&
            context.EnvelopeRunId is Guid envelopeRun &&
            Guid.TryParse(request.ParentRunId, out var parentGuid) &&
            envelopeRun != parentGuid)
        {
            return Error(context, IpcErrorCodes.BindingMismatch, "Envelope run identity does not match the parent Run.");
        }

        var result = scheduler.SpawnChildRun(
            request.ParentRunId,
            request.ParentTaskId,
            request.Role,
            request.RequestedDepth,
            context.Principal);
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new SpawnChildRunResponse(result.Value.Outcome, result.Value.ChildRunId, result.Value.Depth, result.Value.Reason),
                IpcJsonContext.Default.SpawnChildRunResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Respond<T>(
        IpcApplicationCommandContext context,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IpcEnvelope<T>> typeInfo) =>
        new(IpcJson.Serialize(
            IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                context.SemanticType,
                workspaceInstanceId,
                payload,
                context.EnvelopeProjectId,
                context.EnvelopeRunId,
                context.CorrelationId,
                context.RequestId),
            typeInfo));

    private IpcApplicationCommandResult Fail(IpcApplicationCommandContext context, RuntimeFailure? failure) =>
        Error(context, Map(failure?.Code), failure?.Detail ?? failure?.Code.ToString() ?? "runtime-failure");

    private IpcApplicationCommandResult Error(IpcApplicationCommandContext context, string code, string message) =>
        new(IpcJson.Serialize(
            IpcEnvelopeFactory.Create(
                IpcMessageType.Response,
                context.SemanticType,
                workspaceInstanceId,
                new IpcError(code, message, null, false),
                context.EnvelopeProjectId,
                context.EnvelopeRunId,
                context.CorrelationId,
                context.RequestId),
            IpcJsonContext.Default.ErrorEnvelope));

    private static string Map(RuntimeError? error) => error switch
    {
        RuntimeError.DepthLimit => IpcErrorCodes.AgentDepthLimit,
        RuntimeError.DepthSpoof => IpcErrorCodes.AgentDepthSpoof,
        RuntimeError.SpawnDenied => IpcErrorCodes.AgentSpawnDenied,
        RuntimeError.UnknownSideEffect => IpcErrorCodes.AgentUnknownSideEffect,
        RuntimeError.CheckpointUnsupported => IpcErrorCodes.AgentCheckpointUnsupported,
        RuntimeError.IllegalTransition => IpcErrorCodes.AgentIllegalTransition,
        RuntimeError.Cancelled => IpcErrorCodes.AgentIllegalTransition,
        RuntimeError.BindingUnavailable => IpcErrorCodes.TrustedBindingUnavailable,
        RuntimeError.CheckpointCorrupt => IpcErrorCodes.AgentCheckpointUnsupported,
        _ => IpcErrorCodes.CommandUnavailable
    };
}

public sealed class CompositeIpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly IIpcApplicationCommandHandler[] handlers;

    public CompositeIpcCommandHandler(params IIpcApplicationCommandHandler[] handlers)
    {
        this.handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    public async Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        foreach (var handler in handlers)
        {
            var result = await handler.HandleAsync(context).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}

public sealed class MutableIpcCommandHandler : IIpcApplicationCommandHandler
{
    public IIpcApplicationCommandHandler Inner { get; set; } = UnavailableIpcCommandHandler.Instance;

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context) =>
        Inner.HandleAsync(context);
}
