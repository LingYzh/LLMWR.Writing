using LLMW.Writing.Application.ProjectPackages;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// Narrow, native-UI-only entrypoint for project packages. Neither package destinations nor
/// project filesystem paths are representable in these contracts.
/// </summary>
public sealed class Wp20IpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly ProjectPackageServiceHolder services;
    private readonly string workspaceInstanceId;

    public Wp20IpcCommandHandler(ProjectPackageServiceHolder services, string workspaceInstanceId)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        this.workspaceInstanceId = workspaceInstanceId;
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsPackageCommand(context.SemanticType))
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
                Error(context, IpcErrorCodes.MalformedFrame, "The package command payload is malformed."));
        }
    }

    private IpcApplicationCommandResult Handle(IpcApplicationCommandContext context)
    {
        if (context.ClientKind != IpcClientKind.Ui || context.Principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Error(context, IpcErrorCodes.PackageMutationDenied, "Project packages require the authenticated native UI.");
        }

        var service = services.Current;
        if (service is null)
        {
            return Error(context, IpcErrorCodes.CommandUnavailable, "Project packages are unavailable until a project is open.");
        }

        if (context.EnvelopeProjectId is null ||
            !StringComparer.Ordinal.Equals(context.EnvelopeProjectId.Value.ToString("D"), service.ProjectId))
        {
            return Error(context, IpcErrorCodes.BindingMismatch, "The package project binding is invalid.");
        }

        return context.SemanticType switch
        {
            IpcSemanticTypes.CreateProjectBackup => CreateBackup(service, context),
            IpcSemanticTypes.CreateProjectArchive => CreateArchive(service, context),
            IpcSemanticTypes.CreateFinalPackage => CreateFinalPackage(service, context),
            IpcSemanticTypes.VerifyFinalPackage => VerifyFinalPackage(service, context),
            _ => Error(context, IpcErrorCodes.CommandUnavailable, "Unknown project package command.")
        };
    }

    private IpcApplicationCommandResult CreateBackup(ProjectPackageService service, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateProjectBackupRequest);
        return Create(service, context, new ProjectPackageRequest(
            ProjectPackageKind.Backup, service.ProjectId, request.OperationId));
    }

    private IpcApplicationCommandResult CreateArchive(ProjectPackageService service, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateProjectArchiveRequest);
        return Create(service, context, new ProjectPackageRequest(
            ProjectPackageKind.Archive, service.ProjectId, request.OperationId, request.IncludeHistory));
    }

    private IpcApplicationCommandResult CreateFinalPackage(ProjectPackageService service, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CreateFinalPackageRequest);
        return Create(service, context, new ProjectPackageRequest(
            ProjectPackageKind.FinalPackage, service.ProjectId, request.OperationId, false, request.AcceptedSnapshotId));
    }

    private IpcApplicationCommandResult Create(
        ProjectPackageService service,
        IpcApplicationCommandContext context,
        ProjectPackageRequest request)
    {
        var result = service.Create(context.Principal, explicitlyUserInitiated: true, request, context.CancellationToken);
        return result.Succeeded
            ? Ok(context, new ProjectPackageResponse(
                    result.Value!.PackageId,
                    result.Value.FileName,
                    result.Value.CreatedAt,
                    result.Value.ReachableBlobCount),
                IpcJsonContext.Default.ProjectPackageResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult VerifyFinalPackage(ProjectPackageService service, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.VerifyFinalPackageRequest);
        var result = service.VerifyFinalPackage(context.Principal, request.PackageId, context.CancellationToken);
        return result.Succeeded
            ? Ok(context, new VerifyFinalPackageResponse(
                    result.Value!.PackageId,
                    result.Value.Status.ToString(),
                    result.Value.Detail),
                IpcJsonContext.Default.VerifyFinalPackageResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult Failure(IpcApplicationCommandContext context, ProjectPackageFailure failure) =>
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

    private static bool IsPackageCommand(string semanticType) => semanticType is
        IpcSemanticTypes.CreateProjectBackup or
        IpcSemanticTypes.CreateProjectArchive or
        IpcSemanticTypes.CreateFinalPackage or
        IpcSemanticTypes.VerifyFinalPackage;

    private static string ToErrorCode(ProjectPackageFailureCode code) => code switch
    {
        ProjectPackageFailureCode.ProjectBindingInvalid => IpcErrorCodes.PackageProjectBindingInvalid,
        ProjectPackageFailureCode.MutationDenied => IpcErrorCodes.PackageMutationDenied,
        ProjectPackageFailureCode.OperationIdentityConflict => IpcErrorCodes.PackageOperationIdentityConflict,
        ProjectPackageFailureCode.AcceptedSnapshotInvalid => IpcErrorCodes.PackageAcceptedSnapshotInvalid,
        ProjectPackageFailureCode.AcceptedSnapshotNotFound => IpcErrorCodes.PackageAcceptedSnapshotNotFound,
        ProjectPackageFailureCode.PackageNotFound => IpcErrorCodes.PackageNotFound,
        ProjectPackageFailureCode.PackageNotFinal => IpcErrorCodes.PackageNotFinal,
        ProjectPackageFailureCode.VerificationFailed => IpcErrorCodes.PackageVerificationFailed,
        _ => IpcErrorCodes.PackageStorageFailure
    };

    private static string Safe(ProjectPackageFailureCode code) => code switch
    {
        ProjectPackageFailureCode.ProjectBindingInvalid => "The package project binding is invalid.",
        ProjectPackageFailureCode.MutationDenied => "This package operation requires an explicit user command.",
        ProjectPackageFailureCode.OperationIdentityConflict => "The operation identity was replayed with different request data.",
        ProjectPackageFailureCode.AcceptedSnapshotInvalid => "The accepted snapshot does not contain a final manuscript.",
        ProjectPackageFailureCode.AcceptedSnapshotNotFound => "The accepted snapshot does not exist in this Project.",
        ProjectPackageFailureCode.PackageNotFound => "The requested package is not available.",
        _ => "The project package could not be completed."
    };
}
