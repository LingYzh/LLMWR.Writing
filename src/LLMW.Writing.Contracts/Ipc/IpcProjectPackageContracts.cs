namespace LLMW.Writing.Contracts.Ipc;

// WP20 package contracts are intentionally path-free. The Core owns both project binding and the
// configured external package location; a renderer cannot select a filesystem destination.
public sealed record CreateProjectBackupRequest(string OperationId);

public sealed record CreateProjectArchiveRequest(string OperationId, bool IncludeHistory);

public sealed record CreateFinalPackageRequest(string OperationId, string AcceptedSnapshotId);

public sealed record ProjectPackageResponse(
    string PackageId,
    string FileName,
    DateTimeOffset CreatedAt,
    int ReachableBlobCount);

public sealed record VerifyFinalPackageRequest(string PackageId);

public sealed record VerifyFinalPackageResponse(string PackageId, string Status, string? Detail);
