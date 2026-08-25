namespace LLMW.Writing.Application.ProjectPackages;

public enum ProjectPackageKind
{
    Backup,
    Archive,
    FinalPackage
}

public enum ProjectPackageFailureCode
{
    InvalidRequest,
    ProjectBindingInvalid,
    MutationDenied,
    OperationIdentityConflict,
    AcceptedSnapshotInvalid,
    AcceptedSnapshotNotFound,
    StorageFailure,
    PackageNotFound,
    PackageNotFinal,
    VerificationFailed
}

public sealed record ProjectPackageFailure(ProjectPackageFailureCode Code, string? Detail = null);

public sealed record ProjectPackageResult(
    ProjectPackageKind Kind,
    string PackageId,
    string FileName,
    DateTimeOffset CreatedAt,
    int ReachableBlobCount);

public sealed record ProjectPackageVerification(
    string PackageId,
    FinalPackageVerificationStatus Status,
    string? Detail);

public enum FinalPackageVerificationStatus
{
    Verified,
    ModifiedAfterFinalAcceptance,
    Unavailable
}

/// <summary>
/// Path-free typed command data. The project binding and package destination are Core-composed,
/// never supplied by IPC or the renderer.
/// </summary>
public sealed record ProjectPackageRequest(
    ProjectPackageKind Kind,
    string ProjectId,
    string OperationId,
    bool IncludeHistory = false,
    string? AcceptedSnapshotId = null);

public sealed record ProjectPackageStoreResult(
    ProjectPackageResult? Value,
    ProjectPackageFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public sealed record ProjectPackageStoreVerification(
    ProjectPackageVerification? Value,
    ProjectPackageFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Infrastructure owns all SQLite and filesystem work behind this port. Application never sees
/// database connections, physical project paths, blob paths, or ZIP implementation details.
/// </summary>
public interface IProjectPackageStore
{
    ProjectPackageStoreResult Build(ProjectPackageRequest request, CancellationToken cancellationToken = default);

    ProjectPackageStoreVerification VerifyFinalPackage(string packageId, CancellationToken cancellationToken = default);
}
