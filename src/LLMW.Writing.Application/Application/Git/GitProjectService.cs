using LLMW.Writing.Application.Security;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Git;

/// <summary>
/// Application authority for one bound Project's Git operations. Queries remain project-scoped;
/// mutations additionally require a trusted interactive user and an explicit command path.
/// </summary>
public sealed class GitProjectService
{
    private readonly IGitService git;
    private readonly GitProjectBinding binding;

    public GitProjectService(IGitService git, GitProjectBinding binding)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public string ProjectId => binding.ProjectId;

    public GitResult<GitRepositoryDescriptor> DetectRepository() => git.DetectRepository(binding);

    public GitResult<GitStatusSnapshot> GetStatus() => git.GetStatus(binding);

    public GitResult<GitDiffSummary> GetDiffSummary() => git.GetDiffSummary(binding);

    public GitResult<GitBranchInfo> GetCurrentBranch() => git.GetCurrentBranch(binding);

    public GitResult<IReadOnlyList<GitCommitSummary>> GetCommitHistory(int maximumCount) =>
        git.GetCommitHistory(binding, maximumCount);

    public GitResult<GitCommitMetadata> GetCommitMetadata(string commitId) =>
        git.GetCommitMetadata(binding, commitId);

    public GitResult<GitCommitMetadata> Commit(
        CallerPrincipal? principal,
        bool explicitlyUserInitiated,
        GitCommitRequest request)
    {
        if (!explicitlyUserInitiated || principal is not { Kind: PrincipalKind.UserInteractive })
        {
            return GitResults.Fail<GitCommitMetadata>(GitFailureCode.MutationDenied);
        }

        return git.Commit(binding, request);
    }
}

public sealed class GitProjectServiceHolder
{
    private GitProjectService? current;

    public GitProjectService? Current => Volatile.Read(ref current);

    public void PublishOnce(GitProjectService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref current, service, null) is not null)
        {
            throw new InvalidOperationException("Git project service is already published.");
        }
    }

    public bool TryAbandon(GitProjectService expected) =>
        ReferenceEquals(Interlocked.CompareExchange(ref current, null, expected), expected);
}
