using LLMW.Writing.Application.Extensions;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// The only public WP21 command surface. It is native-UI-only, typed and path-free. Discovery
/// never grants execution; trust and per-extension activation remain distinct mutations.
/// </summary>
public sealed class Wp21IpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly ExtensionActivationServiceHolder services;
    private readonly string workspaceInstanceId;

    public Wp21IpcCommandHandler(ExtensionActivationServiceHolder services, string workspaceInstanceId)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        this.workspaceInstanceId = workspaceInstanceId;
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsExtensionCommand(context.SemanticType))
        {
            return Task.FromResult<IpcApplicationCommandResult?>(null);
        }

        try
        {
            return Task.FromResult<IpcApplicationCommandResult?>(Handle(context));
        }
        catch (System.Text.Json.JsonException)
        {
            return Task.FromResult<IpcApplicationCommandResult?>(
                Error(context, IpcErrorCodes.MalformedFrame, "The extension command payload is malformed."));
        }
    }

    private IpcApplicationCommandResult Handle(IpcApplicationCommandContext context)
    {
        if (context.ClientKind != IpcClientKind.Ui || context.Principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Error(context, IpcErrorCodes.ExtensionMutationDenied, "Extension commands require the authenticated native UI.");
        }

        var service = services.Current;
        if (service is null)
        {
            return Error(context, IpcErrorCodes.CommandUnavailable, "Extensions are unavailable until a project is open.");
        }

        if (context.EnvelopeProjectId is null ||
            !StringComparer.Ordinal.Equals(context.EnvelopeProjectId.Value.ToString("D"), service.ProjectId))
        {
            return Error(context, IpcErrorCodes.BindingMismatch, "The extension project binding is invalid.");
        }

        return context.SemanticType switch
        {
            IpcSemanticTypes.ListExtensions => List(service, context),
            IpcSemanticTypes.TrustProjectExtensions => Trust(service, context, trusted: true),
            IpcSemanticTypes.RevokeProjectExtensionsTrust => Trust(service, context, trusted: false),
            IpcSemanticTypes.ActivateExtension => Activate(service, context, activate: true),
            IpcSemanticTypes.DeactivateExtension => Activate(service, context, activate: false),
            _ => Error(context, IpcErrorCodes.CommandUnavailable, "Unknown extension command.")
        };
    }

    private IpcApplicationCommandResult List(ExtensionActivationService service, IpcApplicationCommandContext context)
    {
        _ = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ListExtensionsRequest);
        var view = service.List();
        return Ok(context, new ListExtensionsResponse(
                view.ProjectTrusted,
                view.Extensions.Select(item => new ExtensionStatusResponse(
                    item.ExtensionId, item.Kind, item.Scope, item.Version, item.Activated, item.Invalidated)).ToArray(),
                view.Diagnostics.ToArray()),
            IpcJsonContext.Default.ListExtensionsResponseEnvelope);
    }

    private IpcApplicationCommandResult Trust(
        ExtensionActivationService service,
        IpcApplicationCommandContext context,
        bool trusted)
    {
        var operationId = trusted
            ? IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.TrustProjectExtensionsRequest).OperationId
            : IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.RevokeProjectExtensionsTrustRequest).OperationId;
        var result = trusted
            ? service.TrustProject(context.Principal, new ExtensionOperationRequest(service.ProjectId, operationId))
            : service.RevokeProjectTrust(context.Principal, new ExtensionOperationRequest(service.ProjectId, operationId));
        if (!result.Succeeded)
        {
            return Failure(context, result.Failure!);
        }

        return trusted
            ? Ok(context, new TrustProjectExtensionsResponse(result.Value!.ProjectTrusted),
                IpcJsonContext.Default.TrustProjectExtensionsResponseEnvelope)
            : Ok(context, new RevokeProjectExtensionsTrustResponse(result.Value!.ProjectTrusted),
                IpcJsonContext.Default.RevokeProjectExtensionsTrustResponseEnvelope);
    }

    private IpcApplicationCommandResult Activate(
        ExtensionActivationService service,
        IpcApplicationCommandContext context,
        bool activate)
    {
        var (extensionId, operationId) = activate
            ? ReadActivationRequest(context)
            : ReadDeactivationRequest(context);
        var command = new ActivateExtensionCommand(service.ProjectId, extensionId, operationId);
        var result = activate ? service.Activate(context.Principal, command) : service.Deactivate(context.Principal, command);
        if (!result.Succeeded)
        {
            return Failure(context, result.Failure!);
        }

        return activate
            ? Ok(context, new ActivateExtensionResponse(
                    result.Value!.ExtensionId, result.Value.Activated, result.Value.ProjectTrusted),
                IpcJsonContext.Default.ActivateExtensionResponseEnvelope)
            : Ok(context, new DeactivateExtensionResponse(
                    result.Value!.ExtensionId, result.Value.Activated, result.Value.ProjectTrusted),
                IpcJsonContext.Default.DeactivateExtensionResponseEnvelope);
    }

    private static (string ExtensionId, string OperationId) ReadActivationRequest(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ActivateExtensionRequest);
        return (request.ExtensionId, request.OperationId);
    }

    private static (string ExtensionId, string OperationId) ReadDeactivationRequest(IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.DeactivateExtensionRequest);
        return (request.ExtensionId, request.OperationId);
    }

    private IpcApplicationCommandResult Failure(IpcApplicationCommandContext context, ExtensionFailure failure) =>
        Error(context, ToErrorCode(failure.Code), Safe(failure.Code));

    private IpcApplicationCommandResult Ok<T>(
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

    private static bool IsExtensionCommand(string semanticType) => semanticType is
        IpcSemanticTypes.ListExtensions or
        IpcSemanticTypes.TrustProjectExtensions or
        IpcSemanticTypes.RevokeProjectExtensionsTrust or
        IpcSemanticTypes.ActivateExtension or
        IpcSemanticTypes.DeactivateExtension;

    private static string ToErrorCode(ExtensionFailureCode code) => code switch
    {
        ExtensionFailureCode.ProjectBindingInvalid => IpcErrorCodes.ExtensionProjectBindingInvalid,
        ExtensionFailureCode.MutationDenied => IpcErrorCodes.ExtensionMutationDenied,
        ExtensionFailureCode.ProjectTrustRequired => IpcErrorCodes.ExtensionTrustRequired,
        ExtensionFailureCode.ExtensionNotFound => IpcErrorCodes.ExtensionNotFound,
        ExtensionFailureCode.ExtensionInvalid => IpcErrorCodes.ExtensionInvalid,
        ExtensionFailureCode.ExtensionDependencyInactive => IpcErrorCodes.ExtensionDependencyInactive,
        ExtensionFailureCode.OperationIdentityConflict => IpcErrorCodes.ExtensionOperationIdentityConflict,
        _ => IpcErrorCodes.ExtensionStorageFailure
    };

    private static string Safe(ExtensionFailureCode code) => code switch
    {
        ExtensionFailureCode.ProjectBindingInvalid => "The extension project binding is invalid.",
        ExtensionFailureCode.MutationDenied => "This extension operation requires an explicit user command.",
        ExtensionFailureCode.ProjectTrustRequired => "Project Trust is required before activation.",
        ExtensionFailureCode.ExtensionNotFound => "The requested extension is not available.",
        ExtensionFailureCode.ExtensionInvalid => "The extension metadata is invalid.",
        ExtensionFailureCode.ExtensionDependencyInactive => "A required extension dependency is not active.",
        ExtensionFailureCode.OperationIdentityConflict => "The operation identity was replayed with different request data.",
        _ => "The extension operation could not be completed."
    };
}
