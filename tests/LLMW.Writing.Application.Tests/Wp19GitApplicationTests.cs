using System.Text.Json;
using LLMW.Writing.Application.Git;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Tests;

internal static class Wp19GitApplicationTests
{
    private const string ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";

    public static int Run()
    {
        var count = 0;
        count += MutationRequiresTrustedExplicitUserAction();
        count += RendererFakeRequestCannotReachGitAdapter();
        count += InvalidProjectBindingCannotReachGitAdapter();
        count += GitContractHasNoAutomaticOrGenericOperations();
        Console.WriteLine("WP19 Git Application tests passed (" + count + ").");
        return count;
    }

    private static int MutationRequiresTrustedExplicitUserAction()
    {
        var fake = new FakeGitService();
        var service = new GitProjectService(fake, new GitProjectBinding(ProjectId, "C:\\Projects\\one"));
        var user = new TrustedNativePrincipalSource("wp19-app").ResolveUserInteractive();

        AssertEqual(GitFailureCode.MutationDenied, service.Commit(null, true, new GitCommitRequest("message", true)).Failure!.Code,
            "Missing principal must deny a Git mutation.");
        AssertEqual(GitFailureCode.MutationDenied, service.Commit(user, false, new GitCommitRequest("message", true)).Failure!.Code,
            "A non-explicit user action must deny a Git mutation.");
        AssertEqual(0, fake.CommitCalls, "Denied mutation reached Infrastructure.");
        AssertTrue(service.Commit(user, true, new GitCommitRequest("message", true)).Succeeded,
            "Trusted explicit mutation was not delegated.");
        AssertEqual(1, fake.CommitCalls, "Trusted explicit mutation did not reach Infrastructure exactly once.");
        return 4;
    }

    private static int RendererFakeRequestCannotReachGitAdapter()
    {
        var fake = new FakeGitService();
        var holder = new GitProjectServiceHolder();
        holder.PublishOnce(new GitProjectService(fake, new GitProjectBinding(ProjectId, "C:\\Projects\\one")));
        var handler = new Wp19IpcCommandHandler(holder, "workspace-01");

        var response = Handle(handler, IpcClientKind.AgentRuntime, null, Guid.Parse(ProjectId), IpcSemanticTypes.GetGitStatus);
        AssertEqual(IpcErrorCodes.GitMutationDenied, Error(response).Code,
            "A renderer/runtime-originated fake request was not rejected before Git.");
        AssertEqual(0, fake.StatusCalls, "Renderer fake request reached the Git adapter.");
        return 2;
    }

    private static int InvalidProjectBindingCannotReachGitAdapter()
    {
        var fake = new FakeGitService();
        var holder = new GitProjectServiceHolder();
        holder.PublishOnce(new GitProjectService(fake, new GitProjectBinding(ProjectId, "C:\\Projects\\one")));
        var handler = new Wp19IpcCommandHandler(holder, "workspace-01");
        var user = new TrustedNativePrincipalSource("wp19-ipc").ResolveUserInteractive();

        var response = Handle(handler, IpcClientKind.Ui, user, Guid.Parse("018f3e78-1234-7abc-8def-0123456789ae"), IpcSemanticTypes.GetGitStatus);
        AssertEqual(IpcErrorCodes.BindingMismatch, Error(response).Code,
            "A cross-project Git request was not rejected.");
        AssertEqual(0, fake.StatusCalls, "Cross-project request reached the Git adapter.");
        return 2;
    }

    private static int GitContractHasNoAutomaticOrGenericOperations()
    {
        var names = typeof(IGitService).GetMethods().Select(method => method.Name).ToArray();
        AssertTrue(!names.Any(name => name.Contains("Push", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Rebase", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Checkout", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Execute", StringComparison.OrdinalIgnoreCase)),
            "IGitService exposed an automatic or generic Git operation.");
        AssertEqual(1, names.Count(name => name == nameof(IGitService.Commit)), "IGitService mutation surface changed.");
        return 2;
    }

    private static byte[] Handle(
        Wp19IpcCommandHandler handler,
        IpcClientKind clientKind,
        CallerPrincipal? principal,
        Guid projectId,
        string semanticType)
    {
        using var document = JsonDocument.Parse("{}");
        var result = handler.HandleAsync(new IpcApplicationCommandContext(
                clientKind,
                "connection-a",
                null,
                principal,
                Guid.NewGuid(),
                Guid.NewGuid(),
                projectId,
                null,
                semanticType,
                document.RootElement.Clone(),
                CancellationToken.None))
            .GetAwaiter().GetResult();
        return result?.ResponseUtf8 ?? throw new InvalidOperationException("WP19 handler did not return a response.");
    }

    private static IpcError Error(byte[] response) =>
        IpcJson.Deserialize(response, IpcJsonContext.Default.ErrorEnvelope).Payload;

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message + " Expected: " + expected + "; actual: " + actual);
        }
    }

    private sealed class FakeGitService : IGitService
    {
        public int CommitCalls { get; private set; }

        public int StatusCalls { get; private set; }

        public GitResult<GitRepositoryDescriptor> DetectRepository(GitProjectBinding binding) =>
            GitResults.Success(Descriptor(binding));

        public GitResult<GitStatusSnapshot> GetStatus(GitProjectBinding binding)
        {
            StatusCalls++;
            return GitResults.Success(new GitStatusSnapshot(Descriptor(binding), []));
        }

        public GitResult<GitDiffSummary> GetDiffSummary(GitProjectBinding binding) =>
            GitResults.Success(new GitDiffSummary(Descriptor(binding), 0, 0, 0, 0, 0, 0, 0));

        public GitResult<GitBranchInfo> GetCurrentBranch(GitProjectBinding binding) =>
            GitResults.Success(new GitBranchInfo("main", false, null));

        public GitResult<IReadOnlyList<GitCommitSummary>> GetCommitHistory(GitProjectBinding binding, int maximumCount) =>
            GitResults.Success<IReadOnlyList<GitCommitSummary>>([]);

        public GitResult<GitCommitMetadata> GetCommitMetadata(GitProjectBinding binding, string commitId) =>
            GitResults.Fail<GitCommitMetadata>(GitFailureCode.InvalidCommitReference);

        public GitResult<GitCommitMetadata> Commit(GitProjectBinding binding, GitCommitRequest request)
        {
            CommitCalls++;
            return GitResults.Success(new GitCommitMetadata(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                request.Message,
                "User",
                "user@example.test",
                DateTimeOffset.UnixEpoch,
                "User",
                "user@example.test",
                DateTimeOffset.UnixEpoch,
                []));
        }

        private static GitRepositoryDescriptor Descriptor(GitProjectBinding binding) =>
            new(binding.ProjectId, binding.ProjectRoot, binding.ProjectRoot, true);
    }
}
