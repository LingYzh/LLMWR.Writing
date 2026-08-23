namespace LLMW.Writing.Application.Git;

/// <summary>
/// Project-scoped Git input. The Infrastructure adapter validates the physical path boundary before
/// opening a repository; callers never supply a repository path or arbitrary Git arguments.
/// </summary>
public sealed record GitProjectBinding(string ProjectId, string ProjectRoot);

public enum GitFailureCode
{
    ProjectBindingInvalid,
    RepositoryNotFound,
    RepositoryOutsideProject,
    UnsupportedRepositoryLayout,
    PathRejected,
    InvalidCommitReference,
    InvalidCommitMessage,
    UserIdentityUnavailable,
    MutationDenied,
    BackendFailure
}

public sealed record GitFailure(GitFailureCode Code, string? Detail = null);

public sealed record GitResult<T>(T? Value, GitFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class GitResults
{
    public static GitResult<T> Success<T>(T value) => new(value, null);

    public static GitResult<T> Fail<T>(GitFailureCode code, string? detail = null) =>
        new(default, new GitFailure(code, detail));
}

public sealed record GitRepositoryDescriptor(
    string ProjectId,
    string ProjectRoot,
    string RepositoryRoot,
    bool IsProjectRootRepository);

public sealed record GitStatusEntry(string RelativePath, string State);

public sealed record GitStatusSnapshot(
    GitRepositoryDescriptor Repository,
    IReadOnlyList<GitStatusEntry> Entries)
{
    public bool IsClean => Entries.Count == 0;
}

public sealed record GitDiffSummary(
    GitRepositoryDescriptor Repository,
    int FilesChanged,
    int Added,
    int Modified,
    int Deleted,
    int Renamed,
    int TypeChanged,
    int Untracked);

public sealed record GitBranchInfo(string? Name, bool IsDetached, string? HeadCommitId);

public sealed record GitCommitSummary(
    string CommitId,
    string ShortMessage,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt);

public sealed record GitCommitMetadata(
    string CommitId,
    string TreeId,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommittedAt,
    IReadOnlyList<string> ParentCommitIds);

/// <summary>
/// A deliberately small mutation surface. There is no push, merge, rebase, checkout or arbitrary
/// command capability in this Application contract.
/// </summary>
public sealed record GitCommitRequest(string Message, bool StageAll);

public interface IGitService
{
    GitResult<GitRepositoryDescriptor> DetectRepository(GitProjectBinding binding);

    GitResult<GitStatusSnapshot> GetStatus(GitProjectBinding binding);

    GitResult<GitDiffSummary> GetDiffSummary(GitProjectBinding binding);

    GitResult<GitBranchInfo> GetCurrentBranch(GitProjectBinding binding);

    GitResult<IReadOnlyList<GitCommitSummary>> GetCommitHistory(GitProjectBinding binding, int maximumCount);

    GitResult<GitCommitMetadata> GetCommitMetadata(GitProjectBinding binding, string commitId);

    GitResult<GitCommitMetadata> Commit(GitProjectBinding binding, GitCommitRequest request);
}
