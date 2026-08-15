using System.Text.Json;
using LLMW.Writing.Application.Runtime;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Ipc;

public sealed class Wp13IpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly Wp13RuntimeService service;
    private readonly string workspaceInstanceId;

    public Wp13IpcCommandHandler(Wp13RuntimeService service, string workspaceInstanceId)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
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
                Error(context, IpcErrorCodes.MalformedFrame, "The WP13 command payload is malformed."));
        }
    }

    private IpcApplicationCommandResult? Handle(IpcApplicationCommandContext context) =>
        context.SemanticType switch
        {
            IpcSemanticTypes.RequestTaskCompletion => RequestComplete(context),
            IpcSemanticTypes.SubmitResultArtifact => Submit(context),
            IpcSemanticTypes.GetResultArtifact => GetResult(context),
            IpcSemanticTypes.GetTaskHandoff => Handoff(context),
            IpcSemanticTypes.CreateResultDependency => CreateDep(context),
            IpcSemanticTypes.UpdateResultDependency => UpdateDep(context),
            IpcSemanticTypes.ProposeResultDependencyChange => ProposeDep(context),
            IpcSemanticTypes.RefreshResultDependencyStatus => Refresh(context),
            IpcSemanticTypes.GetEffectiveOversight => GetOversight(context),
            IpcSemanticTypes.SetOversightOverride => SetOversight(context),
            IpcSemanticTypes.ListPendingApprovals => ListApprovals(context),
            IpcSemanticTypes.ResolveRuntimeGrill => ResolveGrill(context),
            IpcSemanticTypes.ListSpecialists => ListSpecialists(context),
            IpcSemanticTypes.GetSpecialist => GetSpecialist(context),
            IpcSemanticTypes.CreateSpecialist => CreateSpecialist(context),
            IpcSemanticTypes.UpdateSpecialist => UpdateSpecialist(context),
            IpcSemanticTypes.DuplicateSpecialist => DuplicateSpecialist(context),
            IpcSemanticTypes.ValidateSpecialist => ValidateSpecialist(context),
            IpcSemanticTypes.CreateSpecialistTestRun => TestRun(context),
            IpcSemanticTypes.ListBackgroundTasks => ListBackground(context),
            IpcSemanticTypes.GetBackgroundTask => GetBackground(context),
            IpcSemanticTypes.StopBackgroundTask => StopBackground(context),
            _ => null
        };

    private IpcApplicationCommandResult RequestComplete(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.RequestTaskCompletionRequest);
        var result = service.RequestTaskCompletion(request.TaskId, context.Principal);
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new RequestTaskCompletionResponse(result.Value.Outcome, result.Value.ResultArtifactId, result.Value.Failures.ToArray()),
                IpcJsonContext.Default.RequestTaskCompletionResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Submit(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.SubmitResultArtifactRequest);
        var result = service.SubmitResultArtifact(request, context.Principal);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.SubmitResultArtifactResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult GetResult(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetResultArtifactRequest);
        var result = service.GetResultArtifact(request.TaskId, request.ResultArtifactId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.GetResultArtifactResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Handoff(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetTaskHandoffRequest);
        var result = service.GetTaskHandoff(request.ConsumerTaskId, request.IncludeEvidence);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.GetTaskHandoffResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult CreateDep(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateResultDependencyRequest);
        var result = service.CreateResultDependency(request.ConsumerTaskId, request.ProducerTaskId, request.DependencyKind);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.CreateResultDependencyResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult UpdateDep(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.UpdateResultDependencyRequest);
        var result = service.UpdateResultDependency(request.DependencyId, request.DependencyKind, request.Status);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.UpdateResultDependencyResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult ProposeDep(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ProposeResultDependencyChangeRequest);
        var result = service.ProposeResultDependencyChange(request.DependencyId, request.ProposedKind, request.Reason, context.Principal);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.ProposeResultDependencyChangeResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult Refresh(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.RefreshResultDependencyStatusRequest);
        var result = service.RefreshResultDependencyStatus(request.ProducerTaskId, request.ConsumerTaskId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.RefreshResultDependencyStatusResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult GetOversight(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetEffectiveOversightRequest);
        var result = service.GetEffectiveOversight(request.ProjectId, request.StorylineId, request.TaskId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.GetEffectiveOversightResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult SetOversight(IpcApplicationCommandContext context)
    {
        if (context.Principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Error(context, IpcErrorCodes.OversightMutationDenied, "Agent cannot set Oversight.");
        }

        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.SetOversightOverrideRequest);
        var result = service.SetOversightOverride(request, context.Principal);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.SetOversightOverrideResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult ListApprovals(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ListPendingApprovalsRequest);
        var result = service.ListPendingApprovals(request.RunId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.ListPendingApprovalsResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult ResolveGrill(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ResolveRuntimeGrillRequest);
        var result = service.ResolveRuntimeGrill(request, context.Principal);
        return result.Succeeded && result.Value is not null
            ? Respond(
                context,
                new ResolveRuntimeGrillResponse(result.Value.Status, result.Value.Resolution, result.Value.ResumeDecision),
                IpcJsonContext.Default.ResolveRuntimeGrillResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult ListSpecialists(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ListSpecialistsRequest);
        var result = service.ListSpecialists(request.ScopeKind);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.ListSpecialistsResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult GetSpecialist(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetSpecialistRequest);
        var result = service.GetSpecialist(request.ProfileId, request.ScopeKind);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.GetSpecialistResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult CreateSpecialist(IpcApplicationCommandContext context)
    {
        if (context.Principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Error(context, IpcErrorCodes.OversightMutationDenied, "Specialist mutation requires USER_INTERACTIVE.");
        }

        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateSpecialistRequest);
        var result = service.CreateSpecialist(request.ScopeKind, request.DefinitionJson, context.Principal);
        return SpecialistMutation(context, result);
    }

    private IpcApplicationCommandResult UpdateSpecialist(IpcApplicationCommandContext context)
    {
        if (context.Principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Error(context, IpcErrorCodes.OversightMutationDenied, "Specialist mutation requires USER_INTERACTIVE.");
        }

        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.UpdateSpecialistRequest);
        var result = service.UpdateSpecialist(request.ProfileId, request.ScopeKind, request.DefinitionJson, context.Principal);
        if (!result.Succeeded || result.Value is null)
        {
            return Fail(context, result.Failure);
        }

        return Respond(
            context,
            new UpdateSpecialistResponse(result.Value.ProfileId, result.Value.ValidationErrors.ToArray()),
            IpcJsonContext.Default.UpdateSpecialistResponseEnvelope);
    }

    private IpcApplicationCommandResult DuplicateSpecialist(IpcApplicationCommandContext context)
    {
        if (context.Principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Error(context, IpcErrorCodes.OversightMutationDenied, "Specialist mutation requires USER_INTERACTIVE.");
        }

        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.DuplicateSpecialistRequest);
        var result = service.DuplicateSpecialist(request.ProfileId, request.SourceScopeKind, request.TargetScopeKind, context.Principal);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.DuplicateSpecialistResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult ValidateSpecialist(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ValidateSpecialistRequest);
        var result = service.ValidateSpecialist(request.DefinitionJson);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.ValidateSpecialistResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult TestRun(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateSpecialistTestRunRequest);
        var result = service.CreateSpecialistTestRun(request.ProfileId, request.ScopeKind, context.Principal);
        if (!result.Succeeded || result.Value is null)
        {
            return Fail(context, result.Failure);
        }

        if (StringComparer.Ordinal.Equals(result.Value.Outcome, "provider_unavailable"))
        {
            return Error(context, IpcErrorCodes.SpecialistTestUnavailable, "WP14 provider execution is not available.");
        }

        return Respond(context, result.Value, IpcJsonContext.Default.CreateSpecialistTestRunResponseEnvelope);
    }

    private IpcApplicationCommandResult ListBackground(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ListBackgroundTasksRequest);
        var result = service.ListBackgroundTasks(request.OwnerRunId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.ListBackgroundTasksResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult GetBackground(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetBackgroundTaskRequest);
        var result = service.GetBackgroundTask(request.BackgroundTaskId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.GetBackgroundTaskResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult StopBackground(IpcApplicationCommandContext context)
    {
        if (context.ClientKind == IpcClientKind.Worker)
        {
            return Error(context, IpcErrorCodes.RuntimeManagementDenied, "Worker cannot stop Background Tasks.");
        }

        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.StopBackgroundTaskRequest);
        var result = service.StopBackgroundTask(request.BackgroundTaskId);
        return result.Succeeded && result.Value is not null
            ? Respond(context, result.Value, IpcJsonContext.Default.StopBackgroundTaskResponseEnvelope)
            : Fail(context, result.Failure);
    }

    private IpcApplicationCommandResult SpecialistMutation(
        IpcApplicationCommandContext context,
        RuntimeResult<SpecialistMutationOutcome> result)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return Fail(context, result.Failure);
        }

        if (result.Value.ValidationErrors.Count > 0)
        {
            return Error(context, IpcErrorCodes.SpecialistValidationFailed, string.Join(';', result.Value.ValidationErrors));
        }

        return Respond(
            context,
            new CreateSpecialistResponse(result.Value.ProfileId, result.Value.ValidationErrors.ToArray()),
            IpcJsonContext.Default.CreateSpecialistResponseEnvelope);
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
        RuntimeError.CompletionFailed => IpcErrorCodes.CompletionContractFailed,
        RuntimeError.SemanticReviewRequired => IpcErrorCodes.SemanticReviewRequired,
        RuntimeError.OversightDenied => IpcErrorCodes.OversightMutationDenied,
        RuntimeError.GrillAuthorRequired => IpcErrorCodes.RuntimeGrillAuthorRequired,
        RuntimeError.GrillAlreadyResolved => IpcErrorCodes.RuntimeGrillAlreadyResolved,
        RuntimeError.SpecialistImmutable => IpcErrorCodes.SpecialistImmutable,
        RuntimeError.SpecialistInvalid => IpcErrorCodes.SpecialistValidationFailed,
        RuntimeError.BackgroundIllegalTransition => IpcErrorCodes.BackgroundIllegalTransition,
        RuntimeError.NotFound => IpcErrorCodes.CommandUnavailable,
        _ => IpcErrorCodes.CommandUnavailable
    };
}
