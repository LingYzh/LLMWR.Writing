namespace LLMW.Writing.Contracts.Ipc;

// Project-scoped semantic Git contracts. They intentionally contain no paths, command strings, or
// arbitrary option arrays; the Core supplies the Project binding and Application selects the action.
public sealed record GetGitStatusRequest();

public sealed record GitStatusEntryResponse(string RelativePath, string State);

public sealed record GetGitStatusResponse(bool IsClean, GitStatusEntryResponse[] Entries);

public sealed record GetGitDiffSummaryRequest();

public sealed record GetGitDiffSummaryResponse(
    int FilesChanged,
    int Added,
    int Modified,
    int Deleted,
    int Renamed,
    int TypeChanged,
    int Untracked);

public sealed record GetGitCurrentBranchRequest();

public sealed record GetGitCurrentBranchResponse(string? Name, bool IsDetached, string? HeadCommitId);

public sealed record ListGitCommitHistoryRequest(int MaximumCount);

public sealed record GitCommitSummaryResponse(
    string CommitId,
    string ShortMessage,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt);

public sealed record ListGitCommitHistoryResponse(GitCommitSummaryResponse[] Commits);

public sealed record GetGitCommitMetadataRequest(string CommitId);

public sealed record GitCommitMetadataResponse(
    string CommitId,
    string TreeId,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string CommitterName,
    string CommitterEmail,
    DateTimeOffset CommittedAt,
    string[] ParentCommitIds);

public sealed record GetGitCommitMetadataResponse(GitCommitMetadataResponse Commit);

public sealed record CommitGitChangesRequest(string Message, bool StageAll);

public sealed record CommitGitChangesResponse(GitCommitMetadataResponse Commit);
