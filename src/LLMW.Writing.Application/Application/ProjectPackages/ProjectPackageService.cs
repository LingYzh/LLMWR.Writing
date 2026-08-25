using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.ProjectPackages;

/// <summary>
/// Core-side gate for backup/archive/final-package requests. Package creation is a filesystem
/// mutation, but is never Narrative Authority mutation.
/// </summary>
public sealed class ProjectPackageService
{
    private readonly IProjectPackageStore store;
    private readonly string projectId;
    private readonly object gate = new();
    private readonly Dictionary<string, (ProjectPackageRequest Request, ProjectPackageStoreResult Result)> completed =
        new(StringComparer.Ordinal);

    public ProjectPackageService(IProjectPackageStore store, string projectId)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        if (!Guid.TryParseExact(projectId, "D", out _))
        {
            throw new ArgumentException("Project identity must be a canonical UUID.", nameof(projectId));
        }

        this.projectId = projectId;
    }

    public string ProjectId => projectId;

    public ProjectPackageStoreResult Create(
        CallerPrincipal? principal,
        bool explicitlyUserInitiated,
        ProjectPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var denial = ValidateMutation(principal, explicitlyUserInitiated, request);
        if (denial is not null)
        {
            return denial;
        }

        lock (gate)
        {
            if (completed.TryGetValue(request.OperationId, out var prior))
            {
                return prior.Request == request
                    ? prior.Result
                    : Fail(ProjectPackageFailureCode.OperationIdentityConflict);
            }

            var result = store.Build(request, cancellationToken);
            if (result.Succeeded)
            {
                completed.Add(request.OperationId, (request, result));
            }

            return result;
        }
    }

    public ProjectPackageStoreVerification VerifyFinalPackage(
        CallerPrincipal? principal,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (principal is not { Kind: PrincipalKind.UserInteractive } || !Guid.TryParseExact(packageId, "D", out _))
        {
            return new ProjectPackageStoreVerification(
                null,
                new ProjectPackageFailure(ProjectPackageFailureCode.MutationDenied));
        }

        return store.VerifyFinalPackage(packageId, cancellationToken);
    }

    private ProjectPackageStoreResult? ValidateMutation(
        CallerPrincipal? principal,
        bool explicitlyUserInitiated,
        ProjectPackageRequest request)
    {
        if (principal is { Kind: PrincipalKind.CoreInternal })
        {
            return request.Kind == ProjectPackageKind.Backup
                ? null
                : Fail(ProjectPackageFailureCode.MutationDenied);
        }

        if (principal is not { Kind: PrincipalKind.UserInteractive } || !explicitlyUserInitiated)
        {
            return Fail(ProjectPackageFailureCode.MutationDenied);
        }

        if (!StringComparer.Ordinal.Equals(projectId, request.ProjectId))
        {
            return Fail(ProjectPackageFailureCode.ProjectBindingInvalid);
        }

        if (!Guid.TryParseExact(request.OperationId, "D", out _) ||
            !Enum.IsDefined(request.Kind) ||
            request.Kind == ProjectPackageKind.FinalPackage &&
            !Guid.TryParseExact(request.AcceptedSnapshotId, "D", out _))
        {
            return Fail(ProjectPackageFailureCode.InvalidRequest);
        }

        if (request.Kind != ProjectPackageKind.Archive && request.IncludeHistory ||
            request.Kind != ProjectPackageKind.FinalPackage && request.AcceptedSnapshotId is not null)
        {
            return Fail(ProjectPackageFailureCode.InvalidRequest);
        }

        return null;
    }

    private static ProjectPackageStoreResult Fail(ProjectPackageFailureCode code) =>
        new(null, new ProjectPackageFailure(code));
}

public sealed class ProjectPackageServiceHolder
{
    private ProjectPackageService? current;

    public ProjectPackageService? Current => Volatile.Read(ref current);

    public void PublishOnce(ProjectPackageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref current, service, null) is not null)
        {
            throw new InvalidOperationException("Project package service is already published.");
        }
    }

    public bool TryAbandon(ProjectPackageService expected) =>
        ReferenceEquals(Interlocked.CompareExchange(ref current, null, expected), expected);
}
