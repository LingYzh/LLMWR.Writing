using LibGit2Sharp;
using LLMW.Writing.Application.Git;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Application.Watcher;
using LLMW.Writing.Infrastructure.FileSystem;
using LLMW.Writing.Infrastructure.Git;
using LLMW.Writing.Infrastructure.Watcher;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private const string Wp19ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";

    private static void RunWp19GitWatcherInfrastructureTests()
    {
        Run(nameof(ControlledNativeRuntimeVersionAndHashAreVerified), ControlledNativeRuntimeVersionAndHashAreVerified);
        Run(nameof(GitRepositoryDetectionRejectsInvalidBindingAndMissingRepository), GitRepositoryDetectionRejectsInvalidBindingAndMissingRepository);
        Run(nameof(GitRepositoryQueriesReturnStatusBranchHistoryAndMetadata), GitRepositoryQueriesReturnStatusBranchHistoryAndMetadata);
        Run(nameof(GitRepositoryCommitIsTypedAndDoesNotRequireGitExe), GitRepositoryCommitIsTypedAndDoesNotRequireGitExe);
        Run(nameof(GitRepositoryRejectsSymlinkAndPathEscape), GitRepositoryRejectsSymlinkAndPathEscape);
        Run(nameof(WatcherDebouncesAndCoalescesBurstsAndDuplicates), WatcherDebouncesAndCoalescesBurstsAndDuplicates);
        Run(nameof(WatcherPreservesRenameAndDeleteEvents), WatcherPreservesRenameAndDeleteEvents);
        Run(nameof(WatcherRecognizesProjectConfigurationAndGitWorkspace), WatcherRecognizesProjectConfigurationAndGitWorkspace);
        Run(nameof(WatcherOverflowRequestsApplicationRecoveryOnly), WatcherOverflowRequestsApplicationRecoveryOnly);
        Run(nameof(WatcherGitBatchDefersPublication), WatcherGitBatchDefersPublication);
    }

    private static void ControlledNativeRuntimeVersionAndHashAreVerified()
    {
        var runtime = LibGit2SharpNativeRuntime.Verify();
        AssertEqual(LibGit2SharpNativeRuntime.ExpectedLibGit2Version, runtime.Version,
            "Native libgit2 runtime version did not match the WP19 security pin.");
        AssertEqual(LibGit2SharpNativeRuntime.ExpectedNativeSha256, runtime.Sha256,
            "Native libgit2 runtime hash did not match the dependency audit.");
        AssertTrue(File.Exists(runtime.NativePath), "Verified native runtime is not deployed to the Infrastructure executable output.");
    }

    private static void GitRepositoryDetectionRejectsInvalidBindingAndMissingRepository()
    {
        var root = CreateWp19Directory("missing");
        try
        {
            var service = new LibGit2SharpGitService();
            AssertEqual((int)GitFailureCode.ProjectBindingInvalid,
                (int)service.DetectRepository(new GitProjectBinding("not-a-project-id", root)).Failure!.Code,
                "Invalid Project identity was accepted by the Git adapter.");
            AssertEqual((int)GitFailureCode.RepositoryNotFound,
                (int)service.DetectRepository(new GitProjectBinding(Wp19ProjectId, root)).Failure!.Code,
                "A non-repository project was treated as a repository.");
            AssertEqual((int)GitFailureCode.PathRejected,
                (int)service.DetectRepository(new GitProjectBinding(Wp19ProjectId, root + ":alternate")).Failure!.Code,
                "Alternate data stream project root was accepted.");
            AssertEqual((int)GitFailureCode.PathRejected,
                (int)service.DetectRepository(new GitProjectBinding(Wp19ProjectId, "\\\\server\\share\\project")).Failure!.Code,
                "UNC project root was accepted.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static void GitRepositoryQueriesReturnStatusBranchHistoryAndMetadata()
    {
        var root = CreateRepository();
        try
        {
            var service = new LibGit2SharpGitService();
            var binding = new GitProjectBinding(Wp19ProjectId, root);
            var detected = service.DetectRepository(binding);
            AssertTrue(detected.Succeeded && detected.Value!.IsProjectRootRepository,
                "Repository discovery did not bind the repository to the Project root.");

            var initial = service.GetCommitHistory(binding, 10);
            AssertTrue(initial.Succeeded && initial.Value!.Count == 1, "Seeded repository history was not available.");
            var metadata = service.GetCommitMetadata(binding, initial.Value![0].CommitId);
            AssertTrue(metadata.Succeeded && metadata.Value!.Message.TrimEnd() == "Initial commit",
                "Commit metadata did not round-trip through the Application-owned record.");
            AssertEqual((int)GitFailureCode.InvalidCommitReference, (int)service.GetCommitMetadata(binding, "../escape").Failure!.Code,
                "Path-like commit reference was accepted.");

            Directory.CreateDirectory(Path.Combine(root, "Draft"));
            File.WriteAllText(Path.Combine(root, "Draft", "changed.md"), "working copy");
            var status = service.GetStatus(binding);
            AssertTrue(status.Succeeded && status.Value!.Entries.Any(entry => entry.RelativePath == "Draft/changed.md"),
                "Untracked Draft file did not appear in Git status.");
            var diff = service.GetDiffSummary(binding);
            AssertTrue(diff.Succeeded && diff.Value!.Untracked >= 1,
                "Diff summary did not count the untracked Draft file.");
            var branch = service.GetCurrentBranch(binding);
            AssertTrue(branch.Succeeded && !branch.Value!.IsDetached && !string.IsNullOrWhiteSpace(branch.Value.Name),
                "Current branch query did not return the seeded branch.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static void GitRepositoryCommitIsTypedAndDoesNotRequireGitExe()
    {
        var root = CreateRepository();
        try
        {
            var service = new LibGit2SharpGitService();
            var binding = new GitProjectBinding(Wp19ProjectId, root);
            Directory.CreateDirectory(Path.Combine(root, "Draft"));
            File.WriteAllText(Path.Combine(root, "Draft", "committed.md"), "typed mutation");

            var result = service.Commit(binding, new GitCommitRequest("Typed user commit", StageAll: true));
            AssertTrue(result.Succeeded && result.Value!.Message.TrimEnd() == "Typed user commit",
                "Typed commit did not produce commit metadata.");
            AssertTrue(service.GetStatus(binding).Value!.IsClean, "Typed stage-all commit left a dirty worktree.");
            AssertTrue(!typeof(LibGit2SharpGitService).Assembly.GetTypes().Any(type => type.Name.Contains("GitProcess", StringComparison.Ordinal)),
                "Infrastructure unexpectedly contains a Git process adapter.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static void GitRepositoryRejectsSymlinkAndPathEscape()
    {
        var root = CreateRepository();
        var linkRoot = CreateWp19Directory("link-parent");
        try
        {
            var service = new LibGit2SharpGitService();
            var nestedProject = Path.Combine(root, "Draft");
            Directory.CreateDirectory(nestedProject);
            AssertTrue(service.DetectRepository(new GitProjectBinding(Wp19ProjectId, nestedProject)).Succeeded,
                "A Project subdirectory within a monorepo should retain its enclosing repository binding.");

            var link = Path.Combine(linkRoot, "project-link");
            try
            {
                Directory.CreateSymbolicLink(link, root);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            AssertEqual((int)GitFailureCode.PathRejected,
                (int)service.DetectRepository(new GitProjectBinding(Wp19ProjectId, link)).Failure!.Code,
                "Symlink project root was accepted for Git traversal.");
        }
        finally
        {
            DeleteWp19Directory(root);
            DeleteWp19Directory(linkRoot);
        }
    }

    private static void WatcherDebouncesAndCoalescesBurstsAndDuplicates()
    {
        var root = CreateWp19Directory("watch-burst");
        try
        {
            var now = DateTimeOffset.UnixEpoch;
            var sink = new RecordingWatcherSink();
            using var watcher = new ProjectWatcherBatcher(
                Wp19ProjectId,
                new ProjectPathResolver(root),
                sink,
                TimeSpan.FromMilliseconds(300),
                () => now);
            watcher.InjectNativeEvent(FileEventKind.Modified, "Draft/a.md");
            watcher.InjectNativeEvent(FileEventKind.Modified, "Draft/a.md");
            watcher.InjectNativeEvent(FileEventKind.Modified, "Draft/b.md");
            AssertFalse(watcher.FlushPending(), "Watcher flushed before its debounce interval.");

            now += TimeSpan.FromMilliseconds(300);
            AssertTrue(watcher.FlushPending(), "Watcher did not flush at the debounce interval.");
            var batch = sink.Single();
            AssertEqual(4, batch.Events.Count, "Burst/duplicate events were not coalesced before project and Git classification.");
            AssertEqual(2, batch.Events.Count(item => item.Kind == ProjectWatcherEventKind.ProjectFileChanged),
                "Draft burst did not emit one Project event per distinct file.");
            AssertEqual(2, batch.Events.Count(item => item.Kind == ProjectWatcherEventKind.GitWorkspaceChanged),
                "Draft burst did not emit one Git workspace event per distinct file.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static void WatcherPreservesRenameAndDeleteEvents()
    {
        var root = CreateWp19Directory("watch-rename");
        try
        {
            var sink = new RecordingWatcherSink();
            using var watcher = new ProjectWatcherBatcher(Wp19ProjectId, new ProjectPathResolver(root), sink);
            watcher.InjectNativeEvent(FileEventKind.Renamed, "Draft/new.md", "Draft/old.md");
            watcher.InjectNativeEvent(FileEventKind.Deleted, "Draft/deleted.md");
            AssertTrue(watcher.FlushPending(force: true), "Rename/delete watcher batch was not emitted.");
            var records = sink.Single().Events.Select(item => item.FileEvent).ToArray();
            AssertTrue(records.Any(item => item.Kind == FileEventKind.Renamed && item.OldRelativePath == "Draft/old.md"),
                "Rename lost the source path.");
            AssertTrue(records.Any(item => item.Kind == FileEventKind.Deleted && item.RelativePath == "Draft/deleted.md"),
                "Delete event was lost.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static void WatcherRecognizesProjectConfigurationAndGitWorkspace()
    {
        var root = CreateWp19Directory("watch-surfaces");
        try
        {
            var sink = new RecordingWatcherSink();
            using var watcher = new ProjectWatcherBatcher(Wp19ProjectId, new ProjectPathResolver(root), sink);
            watcher.InjectNativeEvent(FileEventKind.Modified, "project.llmw.json");
            watcher.InjectNativeEvent(FileEventKind.Modified, "README.md");
            AssertTrue(watcher.FlushPending(force: true), "Configuration/Git workspace event batch was not emitted.");
            var events = sink.Single().Events;
            AssertTrue(events.Any(item => item.Kind == ProjectWatcherEventKind.ProjectFileChanged && item.FileEvent.RelativePath == "project.llmw.json"),
                "Project configuration change was not surfaced to Application.");
            AssertTrue(events.Any(item => item.Kind == ProjectWatcherEventKind.GitWorkspaceChanged && item.FileEvent.RelativePath == "README.md"),
                "Generic Git workspace change was not surfaced to Application.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static void WatcherOverflowRequestsApplicationRecoveryOnly()
    {
        var root = CreateWp19Directory("watch-overflow");
        try
        {
            var sink = new RecordingWatcherSink();
            using var watcher = new ProjectWatcherBatcher(Wp19ProjectId, new ProjectPathResolver(root), sink);
            watcher.MarkOverflow();
            AssertTrue(watcher.FlushPending(force: true), "Watcher overflow did not produce a recovery request.");
            var batch = sink.Single();
            AssertTrue(batch.RequiresFullRescan, "Watcher overflow did not require Application-selected recovery.");
            AssertTrue(batch.Events.All(item => item.Kind == ProjectWatcherEventKind.OverflowRecoveryRequired),
                "Watcher overflow produced a business mutation event instead of a recovery hint.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static void WatcherGitBatchDefersPublication()
    {
        var root = CreateWp19Directory("watch-git-batch");
        try
        {
            var sink = new RecordingWatcherSink();
            using var watcher = new ProjectWatcherBatcher(Wp19ProjectId, new ProjectPathResolver(root), sink);
            watcher.BeginGitBatch();
            watcher.InjectNativeEvent(FileEventKind.Modified, "Draft/a.md");
            AssertFalse(watcher.FlushPending(force: true), "Git watcher batch published before its explicit end.");
            watcher.EndGitBatch();
            AssertEqual(1, sink.Batches.Count, "Git watcher batch did not publish once when ended.");
        }
        finally
        {
            DeleteWp19Directory(root);
        }
    }

    private static string CreateRepository()
    {
        var root = CreateWp19Directory("repository");
        using var repository = new Repository(Repository.Init(root));
        // The adapter correctly requires a configured identity for a user-triggered commit.
        // Keep this fixture independent of a developer or hosted-runner global Git config.
        repository.Config.Set("user.name", "WP19 Test");
        repository.Config.Set("user.email", "wp19@example.test");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        repository.Index.Add("README.md");
        repository.Index.Write();
        var signature = new Signature("WP19 Test", "wp19@example.test", DateTimeOffset.UtcNow);
        _ = repository.Commit("Initial commit", signature, signature);
        return root;
    }

    private static string CreateWp19Directory(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP19", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteWp19Directory(string root)
    {
        for (var attempt = 0; attempt != 5; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception) when (OperatingSystem.IsWindows())
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
        }

        // libgit2 keeps object files mapped until its process-global shutdown on some Windows
        // builds. The test has already completed its assertions; leave this OS-temp fixture for
        // the process to release instead of turning a successful adapter test into a cleanup test.
    }

    private sealed class RecordingWatcherSink : IProjectWatcherBatchSink
    {
        public List<ProjectWatcherBatch> Batches { get; } = [];

        public void Publish(ProjectWatcherBatch batch) => Batches.Add(batch);

        public ProjectWatcherBatch Single() => Batches.Single();
    }
}
