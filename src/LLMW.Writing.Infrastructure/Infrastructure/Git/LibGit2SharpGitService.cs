using LibGit2Sharp;
using LLMW.Writing.Application.Git;

namespace LLMW.Writing.Infrastructure.Git;

/// <summary>
/// The only LibGit2Sharp boundary. It opens only repositories physically bound to the current
/// Project and exposes Application-owned records; no LibGit2Sharp type crosses this assembly.
/// LibGit2Sharp does not execute Git hooks, shell commands, push, merge, rebase, or checkout here.
/// </summary>
public sealed class LibGit2SharpGitService : IGitService
{
    private const int MaximumHistoryCount = 200;
    private const int MaximumCommitMessageLength = 4096;

    public GitResult<GitRepositoryDescriptor> DetectRepository(GitProjectBinding binding)
    {
        var opened = Open(binding);
        if (!opened.Succeeded)
        {
            return GitResults.Fail<GitRepositoryDescriptor>(opened.Failure!.Code, opened.Failure.Detail);
        }

        using var repository = opened.Value!;
        return GitResults.Success(repository.Descriptor);
    }

    public GitResult<GitStatusSnapshot> GetStatus(GitProjectBinding binding)
    {
        var opened = Open(binding);
        if (!opened.Succeeded)
        {
            return GitResults.Fail<GitStatusSnapshot>(opened.Failure!.Code, opened.Failure.Detail);
        }

        using var repository = opened.Value!;
        try
        {
            var entries = repository.Repository.RetrieveStatus()
                .Select(entry => new GitStatusEntry(NormalizeRepositoryRelativePath(entry.FilePath), entry.State.ToString()))
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray();
            return GitResults.Success(new GitStatusSnapshot(repository.Descriptor, entries));
        }
        catch (LibGit2SharpException exception)
        {
            return GitResults.Fail<GitStatusSnapshot>(GitFailureCode.BackendFailure, exception.GetType().Name);
        }
    }

    public GitResult<GitDiffSummary> GetDiffSummary(GitProjectBinding binding)
    {
        var opened = Open(binding);
        if (!opened.Succeeded)
        {
            return GitResults.Fail<GitDiffSummary>(opened.Failure!.Code, opened.Failure.Detail);
        }

        using var repository = opened.Value!;
        try
        {
            var entries = repository.Repository.RetrieveStatus().ToArray();
            var added = 0;
            var modified = 0;
            var deleted = 0;
            var renamed = 0;
            var typeChanged = 0;
            var untracked = 0;
            foreach (var entry in entries)
            {
                var state = entry.State;
                if (Has(state, FileStatus.NewInWorkdir))
                {
                    untracked++;
                }
                else if (Has(state, FileStatus.RenamedInIndex) || Has(state, FileStatus.RenamedInWorkdir))
                {
                    renamed++;
                }
                else if (Has(state, FileStatus.DeletedFromIndex) || Has(state, FileStatus.DeletedFromWorkdir))
                {
                    deleted++;
                }
                else if (Has(state, FileStatus.TypeChangeInIndex) || Has(state, FileStatus.TypeChangeInWorkdir))
                {
                    typeChanged++;
                }
                else if (Has(state, FileStatus.NewInIndex))
                {
                    added++;
                }
                else
                {
                    modified++;
                }
            }

            return GitResults.Success(new GitDiffSummary(
                repository.Descriptor,
                entries.Length,
                added,
                modified,
                deleted,
                renamed,
                typeChanged,
                untracked));
        }
        catch (LibGit2SharpException exception)
        {
            return GitResults.Fail<GitDiffSummary>(GitFailureCode.BackendFailure, exception.GetType().Name);
        }
    }

    public GitResult<GitBranchInfo> GetCurrentBranch(GitProjectBinding binding)
    {
        var opened = Open(binding);
        if (!opened.Succeeded)
        {
            return GitResults.Fail<GitBranchInfo>(opened.Failure!.Code, opened.Failure.Detail);
        }

        using var repository = opened.Value!;
        try
        {
            var head = repository.Repository.Head;
            return GitResults.Success(new GitBranchInfo(
                repository.Repository.Info.IsHeadDetached ? null : head.FriendlyName,
                repository.Repository.Info.IsHeadDetached,
                head.Tip?.Sha));
        }
        catch (LibGit2SharpException exception)
        {
            return GitResults.Fail<GitBranchInfo>(GitFailureCode.BackendFailure, exception.GetType().Name);
        }
    }

    public GitResult<IReadOnlyList<GitCommitSummary>> GetCommitHistory(GitProjectBinding binding, int maximumCount)
    {
        if (maximumCount is < 1 or > MaximumHistoryCount)
        {
            return GitResults.Fail<IReadOnlyList<GitCommitSummary>>(GitFailureCode.InvalidCommitReference);
        }

        var opened = Open(binding);
        if (!opened.Succeeded)
        {
            return GitResults.Fail<IReadOnlyList<GitCommitSummary>>(opened.Failure!.Code, opened.Failure.Detail);
        }

        using var repository = opened.Value!;
        try
        {
            if (repository.Repository.Head.Tip is null)
            {
                return GitResults.Success<IReadOnlyList<GitCommitSummary>>([]);
            }

            var commits = repository.Repository.Commits
                .QueryBy(new CommitFilter
                {
                    IncludeReachableFrom = repository.Repository.Head,
                    SortBy = CommitSortStrategies.Time
                })
                .Take(maximumCount)
                .Select(ToSummary)
                .ToArray();
            return GitResults.Success<IReadOnlyList<GitCommitSummary>>(commits);
        }
        catch (LibGit2SharpException exception)
        {
            return GitResults.Fail<IReadOnlyList<GitCommitSummary>>(GitFailureCode.BackendFailure, exception.GetType().Name);
        }
    }

    public GitResult<GitCommitMetadata> GetCommitMetadata(GitProjectBinding binding, string commitId)
    {
        if (!IsCommitReference(commitId))
        {
            return GitResults.Fail<GitCommitMetadata>(GitFailureCode.InvalidCommitReference);
        }

        var opened = Open(binding);
        if (!opened.Succeeded)
        {
            return GitResults.Fail<GitCommitMetadata>(opened.Failure!.Code, opened.Failure.Detail);
        }

        using var repository = opened.Value!;
        try
        {
            var commit = repository.Repository.Lookup<Commit>(commitId);
            return commit is null
                ? GitResults.Fail<GitCommitMetadata>(GitFailureCode.InvalidCommitReference)
                : GitResults.Success(ToMetadata(commit));
        }
        catch (LibGit2SharpException exception)
        {
            return GitResults.Fail<GitCommitMetadata>(GitFailureCode.BackendFailure, exception.GetType().Name);
        }
    }

    public GitResult<GitCommitMetadata> Commit(GitProjectBinding binding, GitCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > MaximumCommitMessageLength)
        {
            return GitResults.Fail<GitCommitMetadata>(GitFailureCode.InvalidCommitMessage);
        }

        var opened = Open(binding);
        if (!opened.Succeeded)
        {
            return GitResults.Fail<GitCommitMetadata>(opened.Failure!.Code, opened.Failure.Detail);
        }

        using var repository = opened.Value!;
        try
        {
            // Stage-all is an explicit field on the typed request. No application workflow calls this method.
            if (request.StageAll)
            {
                Commands.Stage(repository.Repository, "*");
            }

            var signature = repository.Repository.Config.BuildSignature(DateTimeOffset.UtcNow);
            if (signature is null)
            {
                return GitResults.Fail<GitCommitMetadata>(GitFailureCode.UserIdentityUnavailable);
            }

            var commit = repository.Repository.Commit(request.Message.Trim(), signature, signature);
            return GitResults.Success(ToMetadata(commit));
        }
        catch (LibGit2SharpException exception)
        {
            return GitResults.Fail<GitCommitMetadata>(GitFailureCode.BackendFailure, exception.GetType().Name);
        }
    }

    private static bool Has(FileStatus value, FileStatus flag) => (value & flag) != 0;

    private static bool IsCommitReference(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit);

    private static string NormalizeRepositoryRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static GitCommitSummary ToSummary(Commit commit) =>
        new(
            commit.Sha,
            commit.MessageShort,
            commit.Author.Name,
            commit.Author.Email,
            commit.Author.When);

    private static GitCommitMetadata ToMetadata(Commit commit) =>
        new(
            commit.Sha,
            commit.Tree.Sha,
            commit.Message,
            commit.Author.Name,
            commit.Author.Email,
            commit.Author.When,
            commit.Committer.Name,
            commit.Committer.Email,
            commit.Committer.When,
            commit.Parents.Select(parent => parent.Sha).ToArray());

    private static GitResult<OpenedRepository> Open(GitProjectBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!Guid.TryParseExact(binding.ProjectId, "D", out _))
        {
            return GitResults.Fail<OpenedRepository>(GitFailureCode.ProjectBindingInvalid);
        }

        // Fail closed before the managed binding calls into any native Git routine.
        LibGit2SharpNativeRuntime.Verify();

        var root = ProjectGitBoundary.ValidateProjectRoot(binding.ProjectRoot);
        if (!root.Succeeded)
        {
            return GitResults.Fail<OpenedRepository>(root.Failure!.Code, root.Failure.Detail);
        }

        try
        {
            var discovered = Repository.Discover(root.Value!);
            if (string.IsNullOrWhiteSpace(discovered))
            {
                return GitResults.Fail<OpenedRepository>(GitFailureCode.RepositoryNotFound);
            }

            var repository = new Repository(discovered);
            var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Info.WorkingDirectory));
            var gitDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Info.Path));
            var boundary = ProjectGitBoundary.ValidateRepositoryRoot(root.Value!, repositoryRoot, gitDirectory);
            if (!boundary.Succeeded)
            {
                repository.Dispose();
                return GitResults.Fail<OpenedRepository>(boundary.Failure!.Code, boundary.Failure.Detail);
            }

            return GitResults.Success(new OpenedRepository(
                repository,
                new GitRepositoryDescriptor(
                    binding.ProjectId,
                    root.Value!,
                    repositoryRoot,
                    StringComparer.OrdinalIgnoreCase.Equals(root.Value, repositoryRoot))));
        }
        catch (RepositoryNotFoundException)
        {
            return GitResults.Fail<OpenedRepository>(GitFailureCode.RepositoryNotFound);
        }
        catch (LibGit2SharpException exception)
        {
            return GitResults.Fail<OpenedRepository>(GitFailureCode.BackendFailure, exception.GetType().Name);
        }
        catch (IOException exception)
        {
            return GitResults.Fail<OpenedRepository>(GitFailureCode.PathRejected, exception.GetType().Name);
        }
        catch (UnauthorizedAccessException exception)
        {
            return GitResults.Fail<OpenedRepository>(GitFailureCode.PathRejected, exception.GetType().Name);
        }
    }

    private sealed class OpenedRepository : IDisposable
    {
        public OpenedRepository(Repository repository, GitRepositoryDescriptor descriptor)
        {
            Repository = repository;
            Descriptor = descriptor;
        }

        public Repository Repository { get; }

        public GitRepositoryDescriptor Descriptor { get; }

        public void Dispose() => Repository.Dispose();
    }
}
