using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Reconcile;
using LLMW.Writing.Infrastructure.FileSystem;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private const string Wp08DigestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Wp08DigestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static void RunWp08InfrastructureTests()
    {
        Run(nameof(NativeHintsNormalizeWithoutLeakingFileSystemEventArgs), NativeHintsNormalizeWithoutLeakingFileSystemEventArgs);
        Run(nameof(DuplicateEventsDebounceDeterministically), DuplicateEventsDebounceDeterministically);
        Run(nameof(AtomicReplaceDeleteCreateCoalescesToModified), AtomicReplaceDeleteCreateCoalescesToModified);
        Run(nameof(SelfWriteOperationTokenSuppressesOnlyExpectedDigest), SelfWriteOperationTokenSuppressesOnlyExpectedDigest);
        Run(nameof(LateSelfWriteUsesPathDigestFallback), LateSelfWriteUsesPathDigestFallback);
        Run(nameof(AtomicMaterializerRegistersExpectedSelfWrites), AtomicMaterializerRegistersExpectedSelfWrites);
        Run(nameof(ProjectPathResolverRejectsEscape), ProjectPathResolverRejectsEscape);
        Run(nameof(ProjectPathResolverRejectsReparseTraversal), ProjectPathResolverRejectsReparseTraversal);
    }

    private static void NativeHintsNormalizeWithoutLeakingFileSystemEventArgs()
    {
        var now = DateTimeOffset.UnixEpoch;
        var records = FileEventCoalescer.Coalesce(
        [
            new FileEventRecord(1, "Narrative/a.md", null, FileEventKind.Created, null, FileEventSource.NativeWatcher, now),
            new FileEventRecord(2, "Narrative/b.md", null, FileEventKind.Modified, null, FileEventSource.NativeWatcher, now),
            new FileEventRecord(3, "Narrative/c.md", null, FileEventKind.Deleted, null, FileEventSource.NativeWatcher, now)
        ]);
        AssertEqual(3, records.Count, "Created/Modified/Deleted hints were not normalized independently.");
        AssertEqual((int)FileEventKind.Created, (int)records[0].Kind, "Created hint changed kind.");
        AssertEqual((int)FileEventKind.Modified, (int)records[1].Kind, "Modified hint changed kind.");
        AssertEqual((int)FileEventKind.Deleted, (int)records[2].Kind, "Deleted hint changed kind.");
    }

    private static void DuplicateEventsDebounceDeterministically()
    {
        var now = DateTimeOffset.UnixEpoch;
        var records = FileEventCoalescer.Coalesce(
        [
            new FileEventRecord(4, "Narrative/a.md", null, FileEventKind.Modified, null, FileEventSource.NativeWatcher, now),
            new FileEventRecord(1, "Narrative/a.md", null, FileEventKind.Modified, null, FileEventSource.NativeWatcher, now),
            new FileEventRecord(2, "Narrative/a.md", null, FileEventKind.Modified, Wp08DigestA, FileEventSource.NativeWatcher, now)
        ]);
        AssertEqual(1, records.Count, "Duplicate events did not coalesce.");
        AssertEqual(1L, records[0].Sequence, "Coalescing did not retain deterministic first sequence.");
        AssertEqual((int)FileEventKind.Modified, (int)records[0].Kind, "Duplicate Modified events changed kind.");
    }

    private static void AtomicReplaceDeleteCreateCoalescesToModified()
    {
        var now = DateTimeOffset.UnixEpoch;
        var records = FileEventCoalescer.Coalesce(
        [
            new FileEventRecord(1, "Narrative/a.md", null, FileEventKind.Deleted, null, FileEventSource.NativeWatcher, now),
            new FileEventRecord(2, "Narrative/a.md", null, FileEventKind.Created, Wp08DigestA, FileEventSource.NativeWatcher, now)
        ]);
        AssertEqual(1, records.Count, "Atomic replace produced more than one event.");
        AssertEqual((int)FileEventKind.Modified, (int)records[0].Kind, "delete+create was not normalized to Modified.");
    }

    private static void SelfWriteOperationTokenSuppressesOnlyExpectedDigest()
    {
        var tracker = new SelfWriteTracker();
        using var operation = tracker.BeginOperation(
            [new SelfWriteExpectation("Narrative/a.md", Wp08DigestA)]);
        var token = tracker.TryGetActiveToken("Narrative/a.md");
        AssertTrue(token is not null, "Active self-write token was not correlated by path.");
        AssertTrue(tracker.ShouldSuppress(token, "Narrative/a.md", Wp08DigestA),
            "Expected active self-write digest was not suppressed.");
        AssertFalse(tracker.ShouldSuppress(token, "Narrative/a.md", Wp08DigestB),
            "Same path with wrong digest was incorrectly suppressed.");
    }

    private static void LateSelfWriteUsesPathDigestFallback()
    {
        var tracker = new SelfWriteTracker();
        using (tracker.BeginOperation([new SelfWriteExpectation("Narrative/a.md", Wp08DigestA)]))
        {
        }

        AssertTrue(tracker.ShouldSuppress(null, "Narrative/a.md", Wp08DigestA),
            "Late self-write did not use path+digest fallback.");
        AssertFalse(tracker.ShouldSuppress(null, "Narrative/a.md", Wp08DigestB),
            "Late path match with wrong digest was incorrectly suppressed.");
    }

    private static void AtomicMaterializerRegistersExpectedSelfWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP08.Unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blobs = new ImmutableBlobStore(root);
            var bytes = Encoding.UTF8.GetBytes("core self write");
            using var source = new MemoryStream(bytes, writable: false);
            var staged = blobs.Stage(source);
            var tracker = new SelfWriteTracker();
            var materializer = new AtomicAuthorityMaterializer(root, blobs, tracker);
            var plan = new AuthorityMaterializationPlan("Narrative/a.md", staged.Digest);
            materializer.Materialize("wp08-operation", [plan]);
            materializer.Verify("wp08-operation", [plan]);
            var observed = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "Narrative", "a.md"))))
                .ToLowerInvariant();
            AssertTrue(tracker.ShouldSuppress(null, "Narrative/a.md", observed),
                "Atomic materializer did not register its expected self-write.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ProjectPathResolverRejectsEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP08.Paths", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resolver = new ProjectPathResolver(root);
            AssertEqual("Narrative/a.md", resolver.NormalizeRelativePath("Narrative/a.md"),
                "Safe project-relative path changed unexpectedly.");
            AssertThrows<UnauthorizedAccessException>(() => resolver.NormalizeRelativePath("../outside.md"),
                "Parent traversal was accepted.");
            AssertThrows<UnauthorizedAccessException>(() => resolver.NormalizeRelativePath(Path.Combine(root, "a.md")),
                "Rooted path was accepted.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ProjectPathResolverRejectsReparseTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP08.Reparse", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP08.ReparseTarget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(root, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var resolver = new ProjectPathResolver(root);
            AssertThrows<UnauthorizedAccessException>(() => resolver.Resolve("linked/outside.md"),
                "Reparse-point traversal was accepted as a project-relative path.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
