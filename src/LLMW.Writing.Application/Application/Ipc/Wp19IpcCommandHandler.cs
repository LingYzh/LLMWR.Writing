using LLMW.Writing.Application.Git;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// Typed project-level Git IPC only. It deliberately exposes neither paths nor arbitrary Git verbs,
/// and rejects Agent Runtime/renderer-originated requests before they reach the adapter.
/// </summary>
public sealed class Wp19IpcCommandHandler : IIpcApplicationCommandHandler
{
    private readonly GitProjectServiceHolder services;
    private readonly string workspaceInstanceId;

    public Wp19IpcCommandHandler(GitProjectServiceHolder services, string workspaceInstanceId)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        this.workspaceInstanceId = workspaceInstanceId;
    }

    public Task<IpcApplicationCommandResult?> HandleAsync(IpcApplicationCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsGitCommand(context.SemanticType))
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
                Error(context, IpcErrorCodes.MalformedFrame, "The Git command payload is malformed."));
        }
    }

    private IpcApplicationCommandResult Handle(IpcApplicationCommandContext context)
    {
        if (context.ClientKind != IpcClientKind.Ui || context.Principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return Error(context, IpcErrorCodes.GitMutationDenied, "Git commands require the authenticated native UI.");
        }

        var service = services.Current;
        if (service is null)
        {
            return Error(context, IpcErrorCodes.CommandUnavailable, "Git is unavailable until a project is open.");
        }

        if (context.EnvelopeProjectId is null ||
            !StringComparer.Ordinal.Equals(context.EnvelopeProjectId.Value.ToString("D"), service.ProjectId))
        {
            return Error(context, IpcErrorCodes.BindingMismatch, "The Git command project binding is invalid.");
        }

        return context.SemanticType switch
        {
            IpcSemanticTypes.GetGitStatus => Status(service, context),
            IpcSemanticTypes.GetGitDiffSummary => DiffSummary(service, context),
            IpcSemanticTypes.GetGitCurrentBranch => CurrentBranch(service, context),
            IpcSemanticTypes.ListGitCommitHistory => CommitHistory(service, context),
            IpcSemanticTypes.GetGitCommitMetadata => CommitMetadata(service, context),
            IpcSemanticTypes.CommitGitChanges => Commit(service, context),
            _ => Error(context, IpcErrorCodes.CommandUnavailable, "Unknown Git command.")
        };
    }

    private IpcApplicationCommandResult Status(GitProjectService service, IpcApplicationCommandContext context)
    {
        _ = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetGitStatusRequest);
        var result = service.GetStatus();
        return result.Succeeded
            ? Ok(context, new GetGitStatusResponse(
                result.Value!.IsClean,
                result.Value.Entries.Select(entry => new GitStatusEntryResponse(entry.RelativePath, entry.State)).ToArray()),
                IpcJsonContext.Default.GetGitStatusResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult DiffSummary(GitProjectService service, IpcApplicationCommandContext context)
    {
        _ = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetGitDiffSummaryRequest);
        var result = service.GetDiffSummary();
        return result.Succeeded
            ? Ok(context, new GetGitDiffSummaryResponse(
                result.Value!.FilesChanged,
                result.Value.Added,
                result.Value.Modified,
                result.Value.Deleted,
                result.Value.Renamed,
                result.Value.TypeChanged,
                result.Value.Untracked),
                IpcJsonContext.Default.GetGitDiffSummaryResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult CurrentBranch(GitProjectService service, IpcApplicationCommandContext context)
    {
        _ = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetGitCurrentBranchRequest);
        var result = service.GetCurrentBranch();
        return result.Succeeded
            ? Ok(context, new GetGitCurrentBranchResponse(result.Value!.Name, result.Value.IsDetached, result.Value.HeadCommitId),
                IpcJsonContext.Default.GetGitCurrentBranchResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult CommitHistory(GitProjectService service, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.ListGitCommitHistoryRequest);
        var result = service.GetCommitHistory(request.MaximumCount);
        return result.Succeeded
            ? Ok(context, new ListGitCommitHistoryResponse(result.Value!
                    .Select(summary => new GitCommitSummaryResponse(
                        summary.CommitId,
                        summary.ShortMessage,
                        summary.AuthorName,
                        summary.AuthorEmail,
                        summary.AuthoredAt))
                    .ToArray()),
                IpcJsonContext.Default.ListGitCommitHistoryResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult CommitMetadata(GitProjectService service, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.GetGitCommitMetadataRequest);
        var result = service.GetCommitMetadata(request.CommitId);
        return result.Succeeded
            ? Ok(context, new GetGitCommitMetadataResponse(ToResponse(result.Value!)),
                IpcJsonContext.Default.GetGitCommitMetadataResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult Commit(GitProjectService service, IpcApplicationCommandContext context)
    {
        var request = IpcJson.DeserializePayload(context.Payload, IpcJsonContext.Default.CommitGitChangesRequest);
        var result = service.Commit(context.Principal, explicitlyUserInitiated: true, new GitCommitRequest(request.Message, request.StageAll));
        return result.Succeeded
            ? Ok(context, new CommitGitChangesResponse(ToResponse(result.Value!)),
                IpcJsonContext.Default.CommitGitChangesResponseEnvelope)
            : Failure(context, result.Failure!);
    }

    private IpcApplicationCommandResult Failure(IpcApplicationCommandContext context, GitFailure failure) =>
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

    private static GitCommitMetadataResponse ToResponse(GitCommitMetadata metadata) =>
        new(
            metadata.CommitId,
            metadata.TreeId,
            metadata.Message,
            metadata.AuthorName,
            metadata.AuthorEmail,
            metadata.AuthoredAt,
            metadata.CommitterName,
            metadata.CommitterEmail,
            metadata.CommittedAt,
            metadata.ParentCommitIds.ToArray());

    private static bool IsGitCommand(string semanticType) => semanticType is
        IpcSemanticTypes.GetGitStatus or
        IpcSemanticTypes.GetGitDiffSummary or
        IpcSemanticTypes.GetGitCurrentBranch or
        IpcSemanticTypes.ListGitCommitHistory or
        IpcSemanticTypes.GetGitCommitMetadata or
        IpcSemanticTypes.CommitGitChanges;

    private static string ToErrorCode(GitFailureCode code) => code switch
    {
        GitFailureCode.ProjectBindingInvalid => IpcErrorCodes.GitProjectBindingInvalid,
        GitFailureCode.RepositoryNotFound => IpcErrorCodes.GitRepositoryNotFound,
        GitFailureCode.RepositoryOutsideProject => IpcErrorCodes.GitRepositoryOutsideProject,
        GitFailureCode.UnsupportedRepositoryLayout => IpcErrorCodes.GitRepositoryUnsupported,
        GitFailureCode.PathRejected => IpcErrorCodes.GitPathRejected,
        GitFailureCode.InvalidCommitReference => IpcErrorCodes.GitCommitReferenceInvalid,
        GitFailureCode.InvalidCommitMessage => IpcErrorCodes.GitCommitMessageInvalid,
        GitFailureCode.UserIdentityUnavailable => IpcErrorCodes.GitUserIdentityUnavailable,
        GitFailureCode.MutationDenied => IpcErrorCodes.GitMutationDenied,
        _ => IpcErrorCodes.GitBackendFailure
    };

    private static string Safe(GitFailureCode code) => code switch
    {
        GitFailureCode.RepositoryNotFound => "No Git repository is available for this Project.",
        GitFailureCode.RepositoryOutsideProject or GitFailureCode.PathRejected => "The repository path is outside the allowed Project boundary.",
        GitFailureCode.InvalidCommitReference => "The commit reference is invalid.",
        GitFailureCode.InvalidCommitMessage => "The commit message is invalid.",
        GitFailureCode.UserIdentityUnavailable => "Git author identity is not configured for this Project.",
        GitFailureCode.MutationDenied => "This Git mutation requires an explicit user command.",
        _ => "The Git operation could not be completed."
    };
}
