using System.Security.Cryptography;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Editor;
using LLMW.Writing.Application.History;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Tests;

internal static class Wp18LocalHistoryTests
{
    private const string ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";
    private const string OtherProjectId = "018f3e78-1234-7abc-8def-0123456789ae";
    private const string ChapterId = "018f3e78-1234-7abc-8def-0123456789a1";
    private const string OtherChapterId = "018f3e78-1234-7abc-8def-0123456789a2";
    private static readonly CallerPrincipal User =
        new TrustedNativePrincipalSource("wp18-app").ResolveUserInteractive();

    public static int Run()
    {
        var count = 0;
        count += CheckpointCreationAndRestore();
        count += RetentionCleanupPreservesActiveRecovery();
        count += RestoreDenials();
        Console.WriteLine("WP18 Local History application tests passed (" + count + ").");
        return count;
    }

    private static int CheckpointCreationAndRestore()
    {
        var files = Seed();
        var blobs = new MemoryBlobStore();
        var metadata = new MemoryMetadataStore();
        var history = new LocalHistoryService(blobs, metadata, clock: new FixedTimeProvider());
        var runtime = new EditorRuntime(ProjectId, files, blobs, history: history);
        var opened = Must(runtime.Open(User, "connection-a", ProjectId, ChapterId, "chapter.md", true));
        var saved = Save(runtime, opened, "op-1", "B"u8.ToArray(), HistoryCheckpointTriggerKind.ExplicitSave);
        var entries = Must(history.List(ProjectId, new HistoryDocumentIdentity(ChapterId, "chapter.md")));

        AssertEqual(1, entries.Count, "Changed explicit save must create a history entry.");
        AssertEqual(opened.LastPersistedDigest, entries[0].BaseDigest, "History base digest must bind the preceding Draft.");
        AssertEqual(saved.PersistedDigest, entries[0].ContentDigest, "History content digest must bind restored bytes.");
        AssertEqual(HistoryCheckpointTriggerKind.ExplicitSave, entries[0].TriggerKind, "Checkpoint trigger was lost.");

        files.Set("Draft/" + ChapterId + "/chapter.md", "A"u8.ToArray());
        var restored = Must(runtime.RestoreHistory(
            User,
            "connection-a",
            ProjectId,
            new RestoreHistoryEntryRequest(entries[0].HistoryId, opened.EditorSessionId, ContentDigest.Sha256Hex("A"u8)),
            CancellationToken.None));

        AssertTrue(restored.Restored, "Matching Draft base must restore a Local History entry.");
        AssertEqual("B", System.Text.Encoding.UTF8.GetString(files.Read("Draft/" + ChapterId + "/chapter.md").Value!.Bytes), "Restore must only rewrite the target Draft.");
        AssertEqual(3L, restored.PersistedRevision, "Restore must advance the live editor persistence revision.");
        return 6;
    }

    private static int RetentionCleanupPreservesActiveRecovery()
    {
        var blobs = new MemoryBlobStore();
        var metadata = new MemoryMetadataStore();
        var history = new LocalHistoryService(
            blobs,
            metadata,
            new LocalHistoryRetentionPolicy(1, 2, TimeSpan.FromDays(30)),
            new FixedTimeProvider());
        var document = new HistoryDocumentIdentity(ChapterId, "chapter.md");
        var session = "018f3e78-1234-7abc-8def-0123456789b1";
        var a = ContentDigest.Sha256Hex("A"u8);
        var b = ContentDigest.Sha256Hex("B"u8);
        var c = ContentDigest.Sha256Hex("C"u8);

        var first = Must(history.Capture(new LocalHistoryCheckpoint(ProjectId, document, session, a, "B"u8.ToArray(), HistoryCheckpointTriggerKind.Autosave))).Entry!;
        var recovery = Must(history.Capture(new LocalHistoryCheckpoint(ProjectId, document, session, b, "C"u8.ToArray(), HistoryCheckpointTriggerKind.CrashRecovery))).Entry!;
        var entries = Must(history.List(ProjectId, document));

        AssertEqual(1, entries.Count, "Per-document retention must remove obsolete versions.");
        AssertEqual(recovery.HistoryId, entries[0].HistoryId, "Active recovery point must survive retention cleanup.");
        AssertTrue(entries[0].IsActiveRecoveryPoint, "Crash checkpoint must be marked as active recovery.");
        AssertEqual(IpcErrorCodes.HistoryEntryNotFound, history.ReadForRestore(new LocalHistoryRestoreRequest(ProjectId, document, first.HistoryId, a)).ErrorCode!, "Discarded metadata must not be restorable.");
        AssertEqual(c, entries[0].ContentDigest, "Retained content digest changed.");
        return 5;
    }

    private static int RestoreDenials()
    {
        var files = Seed();
        var blobs = new MemoryBlobStore();
        var metadata = new MemoryMetadataStore();
        var history = new LocalHistoryService(blobs, metadata, clock: new FixedTimeProvider());
        var runtime = new EditorRuntime(ProjectId, files, blobs, history: history);
        var first = Must(runtime.Open(User, "connection-a", ProjectId, ChapterId, "chapter.md", true));
        var second = Must(runtime.Open(User, "connection-a", ProjectId, OtherChapterId, "notes.md", true));
        _ = Save(runtime, first, "op-1", "B"u8.ToArray(), HistoryCheckpointTriggerKind.Autosave);
        var entry = Must(history.List(ProjectId, new HistoryDocumentIdentity(ChapterId, "chapter.md"))).Single();

        AssertEqual(
            IpcErrorCodes.HistoryEntryInvalid,
            runtime.RestoreHistory(User, "connection-a", OtherProjectId, new RestoreHistoryEntryRequest(entry.HistoryId, first.EditorSessionId, first.LastPersistedDigest), CancellationToken.None).ErrorCode!,
            "Cross-project restore must be rejected.");
        AssertEqual(
            IpcErrorCodes.HistoryEntryInvalid,
            runtime.RestoreHistory(User, "connection-a", ProjectId, new RestoreHistoryEntryRequest(entry.HistoryId, second.EditorSessionId, second.LastPersistedDigest), CancellationToken.None).ErrorCode!,
            "Cross-document restore must be rejected.");

        files.Set("Draft/" + ChapterId + "/chapter.md", "externally-modified"u8.ToArray());
        var externalDigest = files.Read("Draft/" + ChapterId + "/chapter.md").Value!.Digest;
        AssertEqual(
            IpcErrorCodes.HistoryRestoreConflict,
            runtime.RestoreHistory(User, "connection-a", ProjectId, new RestoreHistoryEntryRequest(entry.HistoryId, first.EditorSessionId, externalDigest), CancellationToken.None).ErrorCode!,
            "External modification must not be silently overwritten.");
        AssertEqual(
            IpcErrorCodes.HistoryEntryNotFound,
            runtime.RestoreHistory(User, "connection-a", ProjectId, new RestoreHistoryEntryRequest(Guid.NewGuid().ToString("D"), first.EditorSessionId, externalDigest), CancellationToken.None).ErrorCode!,
            "Forged history identity must be rejected.");
        AssertTrue(!files.AuthorityTouched, "Local History must not touch Authority/Manuscript state.");
        return 5;
    }

    private static SaveDraftEditorSessionResponse Save(
        EditorRuntime runtime,
        OpenDraftEditorSessionResponse opened,
        string operationId,
        byte[] content,
        HistoryCheckpointTriggerKind trigger)
    {
        var digest = ContentDigest.Sha256Hex(content);
        var uploaded = Must(runtime.BeginUpload(User, "connection-a", new BeginEditorContentUploadRequest(opened.EditorSessionId, operationId, content.Length, digest)));
        Must(runtime.UploadChunk(User, "connection-a", new EditorContentUploadChunkRequest(uploaded.UploadId, 0, 1, Convert.ToBase64String(content))));
        var blob = Must(runtime.CommitUpload(User, "connection-a", new CommitEditorContentUploadRequest(uploaded.UploadId), CancellationToken.None)).BlobRef;
        return Must(runtime.Save(User, "connection-a", new SaveDraftEditorSessionRequest(opened.EditorSessionId, operationId, opened.LastPersistedDigest, blob, trigger), CancellationToken.None));
    }

    private static MemoryDraftStore Seed()
    {
        var files = new MemoryDraftStore();
        files.Set("Draft/" + ChapterId + "/chapter.md", "A"u8.ToArray());
        files.Set("Draft/" + OtherChapterId + "/notes.md", "other"u8.ToArray());
        return files;
    }

    private static T Must<T>(EditorResult<T> result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.ErrorCode);
        }

        return result.Value!;
    }

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

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(20_323);
    }

    private sealed class MemoryMetadataStore : ILocalHistoryMetadataStore
    {
        private List<HistoryEntry> entries = [];

        public EditorResult<IReadOnlyList<HistoryEntry>> Load(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EditorResult<IReadOnlyList<HistoryEntry>>.Ok(entries.ToArray());
        }

        public EditorResult<bool> Replace(IReadOnlyList<HistoryEntry> entries, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.entries = entries.ToList();
            return EditorResult<bool>.Ok(true);
        }
    }

    private sealed class MemoryBlobStore : IImmutableBlobStore
    {
        private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

        public BlobStageResult Stage(Stream source, string? expectedDigest = null, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            cancellationToken.ThrowIfCancellationRequested();
            var content = memory.ToArray();
            var digest = ContentDigest.Sha256Hex(content);
            if (expectedDigest is not null && !StringComparer.Ordinal.Equals(ContentDigest.Normalize(expectedDigest), digest))
            {
                throw new InvalidDataException("Blob digest mismatch.");
            }

            var deduplicated = blobs.ContainsKey(digest);
            blobs[digest] = content;
            return new BlobStageResult(digest, digest, content.Length, deduplicated);
        }

        public Stream OpenRead(string digest) => new MemoryStream(blobs[ContentDigest.Normalize(digest)], writable: false);

        public bool Verify(string digest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return blobs.TryGetValue(ContentDigest.Normalize(digest), out var content)
                && StringComparer.Ordinal.Equals(ContentDigest.Sha256Hex(content), ContentDigest.Normalize(digest));
        }
    }

    private sealed class MemoryDraftStore : IDraftFileStore
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        public bool AuthorityTouched { get; private set; }

        public void Set(string relativePath, byte[] content) => files[relativePath] = content.ToArray();

        public EditorResult<string> ResolveLeaseKey(string relativePath) =>
            files.ContainsKey(relativePath)
                ? EditorResult<string>.Ok("lease:" + relativePath)
                : EditorResult<string>.Fail(IpcErrorCodes.EditorDocumentNotWritable);

        public EditorResult<DraftFileSnapshot> Read(string relativePath)
        {
            if (!DraftDocumentResolver.IsDraftWorkspacePath(relativePath)
                || DraftDocumentResolver.IsManuscriptPath(relativePath)
                || !files.TryGetValue(relativePath, out var content))
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
            }

            return EditorResult<DraftFileSnapshot>.Ok(new DraftFileSnapshot(relativePath, "lease:" + relativePath, content.ToArray(), ContentDigest.Sha256Hex(content), content.Length));
        }

        public EditorResult<DraftFileSnapshot> ReadFromLeaseKey(string leaseKey) =>
            !leaseKey.StartsWith("lease:", StringComparison.Ordinal)
                ? EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable)
                : Read(leaseKey["lease:".Length..]);

        public string DigestOf(ReadOnlySpan<byte> bytes) => ContentDigest.Sha256Hex(bytes);

        public EditorResult<DraftFileSnapshot> AtomicReplace(string relativePath, string expectedDigest, byte[] contentBytes, IEditorSaveFaultInjector faults)
        {
            var current = Read(relativePath);
            if (!current.Succeeded)
            {
                return current;
            }

            if (!StringComparer.Ordinal.Equals(current.Value!.Digest, ContentDigest.Normalize(expectedDigest)))
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorStaleBase);
            }

            files[relativePath] = contentBytes.ToArray();
            return Read(relativePath);
        }
    }
}
