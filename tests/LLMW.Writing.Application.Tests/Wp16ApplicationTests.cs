using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Application.Authority;
using LLMW.Writing.Application.Editor;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Tests;

internal static class Wp16ApplicationTests
{
    private const string ChapterId = "018f3e78-1234-7abc-8def-0123456789a1";
    private const string OtherChapter = "018f3e78-1234-7abc-8def-0123456789a2";
    private const string ProjectId = "018f3e78-1234-7abc-8def-0123456789ad";
    private static readonly CallerPrincipal User =
        new TrustedNativePrincipalSource("wp16-app").ResolveUserInteractive();

    public static int Run()
    {
        var count = 0;
        count += CodecTests();
        count += ResolverTests();
        count += LeaseMatrix();
        count += SessionDenials();
        count += SavePreconditions();
        count += UploadAdversarial();
        count += SaveIdempotencyAndFaults();
        count += SaveBindingAndUnknownOutcome();
        Console.WriteLine("WP16 application tests passed (" + count + ").");
        return count;
    }

    private static int CodecTests()
    {
        AssertEqual("hello\nworld", TextDocumentCodec.TryDecode("hello\nworld"u8).Value!.LogicalText, "LF decode.");
        var bomLf = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', (byte)'\n' };
        var bom = TextDocumentCodec.TryDecode(bomLf);
        AssertTrue(bom.Succeeded && bom.Value!.HadUtf8Bom && bom.Value.LogicalText == "A\n", "BOM must be stripped.");
        var crlf = TextDocumentCodec.TryDecode("A\r\nB"u8);
        AssertTrue(crlf.Succeeded && crlf.Value!.HadCarriageReturn && crlf.Value.LogicalText == "A\nB", "CRLF must become LF.");
        var bomCrlf = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'A', (byte)'\r', (byte)'\n' };
        AssertEqual("A\n", TextDocumentCodec.TryDecode(bomCrlf).Value!.LogicalText, "BOM+CRLF.");
        AssertTrue(!TextDocumentCodec.TryDecode(new byte[] { 0xFF, 0xFE, 0x00 }).Succeeded, "Invalid UTF-8 must fail.");
        var encoded = TextDocumentCodec.EncodeUtf8NoBomLf("A\r\nB");
        AssertTrue(encoded[0] != 0xEF, "Save must not emit BOM.");
        AssertEqual("A\nB", Encoding.UTF8.GetString(encoded), "Save must emit LF.");
        return 7;
    }

    private static int ResolverTests()
    {
        var ok = DraftDocumentResolver.Resolve(ChapterId, "chapter.md");
        AssertTrue(ok.Succeeded && ok.Value!.RelativePath == "Draft/" + ChapterId + "/chapter.md", "Draft relative path.");
        AssertTrue(ok.Value!.FormatKind == EditorFormatKind.Md, "md format.");
        AssertTrue(!DraftDocumentResolver.Resolve(ChapterId, "../escape.md").Succeeded, "escape filename.");
        AssertTrue(!DraftDocumentResolver.Resolve(ChapterId, "C:secret.md").Succeeded, "ADS/drive.");
        AssertTrue(!DraftDocumentResolver.Resolve("not-a-uuid", "chapter.md").Succeeded, "chapter identity.");
        AssertTrue(!DraftDocumentResolver.IsDraftWorkspacePath("Manuscript/current/" + ChapterId + ".md"), "Manuscript is not Draft.");
        AssertTrue(DraftDocumentResolver.IsManuscriptPath("Manuscript/current/x.md"), "Manuscript detector.");
        AssertTrue(!DraftDocumentResolver.Resolve(ChapterId, "CON.txt").Succeeded, "device name.");
        return 8;
    }

    private static int LeaseMatrix()
    {
        var files = Seed();
        var runtime = new EditorRuntime(ProjectId, files, new MemoryEditorBlobStore());
        var first = Must(runtime.Open(User, "conn-a", ProjectId, ChapterId, "chapter.md", true));
        AssertTrue(first.Writable, "First user acquires writable lease.");
        var second = Must(runtime.Open(User, "conn-b", ProjectId, ChapterId, "chapter.md", true));
        AssertTrue(!second.Writable, "Second user on same Draft is read-only.");
        var other = Must(runtime.Open(User, "conn-b", ProjectId, OtherChapter, "notes.txt", true));
        AssertTrue(other.Writable, "Different Draft may be writable.");
        var agent = runtime.AcquireAgentWrite(files.LeaseKeyFor(ChapterId, "chapter.md"), "run-1", "agent-session", first.LastPersistedDigest);
        AssertEqual(IpcErrorCodes.EditorLeaseConflict, agent.ErrorCode, "Agent cannot bypass user lease.");
        Must(runtime.Release(User, "conn-a", first.EditorSessionId));
        var after = Must(runtime.Open(User, "conn-b", ProjectId, ChapterId, "chapter.md", true));
        AssertTrue(after.Writable, "Release allows next owner.");
        var agentOk = runtime.AcquireAgentWrite(files.LeaseKeyFor(OtherChapter, "notes.txt"), "run-1", "agent-session", other.LastPersistedDigest);
        AssertTrue(!agentOk.Succeeded, "User still owns other draft.");
        Must(runtime.Release(User, "conn-b", other.EditorSessionId));
        var agentLease = Must(runtime.AcquireAgentWrite(files.LeaseKeyFor(OtherChapter, "notes.txt"), "run-1", "agent-session", other.LastPersistedDigest));
        AssertEqual(EditorLeaseOwnerKind.AgentWrite, agentLease.OwnerKind, "Agent acquires free Draft.");
        var userWhileAgent = Must(runtime.Open(User, "conn-a", ProjectId, OtherChapter, "notes.txt", true));
        AssertTrue(!userWhileAgent.Writable, "User is read-only while agent holds lease.");
        files.Write(OtherChapter, "notes.txt", "changed-externally"u8.ToArray());
        var staleTransfer = runtime.TransferLease(
            files.LeaseKeyFor(OtherChapter, "notes.txt"),
            EditorLeaseOwnerKind.AgentWrite,
            "run-1",
            EditorLeaseOwnerKind.UserEditor,
            "conn-a",
            userWhileAgent.EditorSessionId,
            other.LastPersistedDigest);
        AssertEqual(IpcErrorCodes.EditorStaleBase, staleTransfer.ErrorCode, "Stale transfer denied.");
        var fresh = files.Read("Draft/" + OtherChapter + "/notes.txt").Value!.Digest;
        var transferred = Must(runtime.TransferLease(
            files.LeaseKeyFor(OtherChapter, "notes.txt"),
            EditorLeaseOwnerKind.AgentWrite,
            "run-1",
            EditorLeaseOwnerKind.UserEditor,
            "conn-a",
            userWhileAgent.EditorSessionId,
            fresh));
        AssertEqual(EditorLeaseOwnerKind.UserEditor, transferred.OwnerKind, "Fresh transfer allowed.");
        return 12;
    }

    private static int SessionDenials()
    {
        var runtime = new EditorRuntime(ProjectId, Seed(), new MemoryEditorBlobStore());
        var opened = Must(runtime.Open(User, "conn-a", ProjectId, ChapterId, "chapter.md", true));
        AssertEqual(IpcErrorCodes.EditorSessionInvalid, runtime.GetState(User, "conn-a", Guid.NewGuid().ToString("D")).ErrorCode, "forged session.");
        AssertEqual(IpcErrorCodes.EditorSessionInvalid, runtime.GetState(User, "conn-b", opened.EditorSessionId).ErrorCode, "wrong connection.");
        AssertEqual(IpcErrorCodes.EditorSessionInvalid, runtime.Open(User, "conn-a", Guid.NewGuid().ToString("D"), ChapterId, "chapter.md", true).ErrorCode, "wrong project.");
        Must(runtime.Release(User, "conn-a", opened.EditorSessionId));
        AssertEqual(IpcErrorCodes.EditorSessionInvalid, runtime.GetState(User, "conn-a", opened.EditorSessionId).ErrorCode, "closed session.");
        runtime.ReleaseByConnection("conn-a");
        return 5;
    }

    private static int SavePreconditions()
    {
        var files = Seed();
        var blobs = new MemoryEditorBlobStore();
        var runtime = new EditorRuntime(ProjectId, files, blobs);
        var opened = Must(runtime.Open(User, "conn-a", ProjectId, ChapterId, "chapter.md", true));
        var payload = TextDocumentCodec.EncodeUtf8NoBomLf("hello editor");
        var blob = Upload(runtime, opened.EditorSessionId, "op-1", payload);
        var saved = Must(runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(opened.EditorSessionId, "op-1", opened.LastPersistedDigest, blob),
            CancellationToken.None));
        AssertEqual(ContentDigest.Sha256Hex(payload), saved.PersistedDigest, "fresh save digest.");
        AssertTrue(!files.AuthorityTouched, "Draft save must not touch Authority.");
        var stale = runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(
                opened.EditorSessionId,
                "op-2",
                opened.LastPersistedDigest,
                Upload(runtime, opened.EditorSessionId, "op-2", payload)),
            CancellationToken.None);
        AssertEqual(IpcErrorCodes.EditorStaleBase, stale.ErrorCode, "stale base denied.");
        var wrongBlob = runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(
                opened.EditorSessionId,
                "op-3",
                saved.PersistedDigest,
                blob with { Digest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", Locator = "blob:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }),
            CancellationToken.None);
        AssertTrue(wrongBlob.ErrorCode is IpcErrorCodes.EditorUploadInvalid or IpcErrorCodes.EditorUploadHashMismatch, "wrong blob.");
        var identity = runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(
                opened.EditorSessionId,
                "op-1",
                saved.PersistedDigest,
                Upload(runtime, opened.EditorSessionId, "op-1", "other"u8.ToArray())),
            CancellationToken.None);
        AssertEqual(IpcErrorCodes.EditorSaveIdentityConflict, identity.ErrorCode, "same op different content.");
        var replay = Must(runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(opened.EditorSessionId, "op-1", opened.LastPersistedDigest, blob),
            CancellationToken.None));
        AssertTrue(replay.IdempotentReplay, "exact retry is idempotent.");
        Must(runtime.Release(User, "conn-a", opened.EditorSessionId));
        var lost = runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(opened.EditorSessionId, "op-4", saved.PersistedDigest, blob),
            CancellationToken.None);
        AssertEqual(IpcErrorCodes.EditorSessionInvalid, lost.ErrorCode, "session lost.");
        return 8;
    }

    private static int UploadAdversarial()
    {
        var blobs = new MemoryEditorBlobStore();
        var runtime = new EditorRuntime(ProjectId, Seed(), blobs);
        var opened = Must(runtime.Open(User, "conn-a", ProjectId, ChapterId, "chapter.md", true));
        var text = "chunk-body";
        var bytes = Encoding.UTF8.GetBytes(text);
        var digest = ContentDigest.Sha256Hex(bytes);
        AssertEqual(
            IpcErrorCodes.EditorDocumentTooLarge,
            runtime.BeginUpload(
                User,
                "conn-a",
                new BeginEditorContentUploadRequest(opened.EditorSessionId, "op-u", EditorTransportLimits.MaximumDocumentUtf8Bytes + 1, digest)).ErrorCode,
            "oversize document.");
        var begin = Must(runtime.BeginUpload(
            User,
            "conn-a",
            new BeginEditorContentUploadRequest(opened.EditorSessionId, "op-u", bytes.Length, digest)));
        AssertEqual(
            IpcErrorCodes.EditorUploadInvalid,
            runtime.UploadChunk(User, "conn-a", new EditorContentUploadChunkRequest(begin.UploadId, 1, 1, Convert.ToBase64String(bytes))).ErrorCode,
            "wrong index.");
        Must(runtime.UploadChunk(User, "conn-a", new EditorContentUploadChunkRequest(begin.UploadId, 0, 1, Convert.ToBase64String(bytes))));
        AssertEqual(
            IpcErrorCodes.EditorUploadInvalid,
            runtime.UploadChunk(User, "conn-a", new EditorContentUploadChunkRequest(begin.UploadId, 0, 2, Convert.ToBase64String(bytes))).ErrorCode,
            "wrong count.");
        var committed = Must(runtime.CommitUpload(User, "conn-a", new CommitEditorContentUploadRequest(begin.UploadId), CancellationToken.None));
        AssertEqual(digest, committed.BlobRef.Digest, "commit hash.");
        var mismatchBegin = Must(runtime.BeginUpload(
            User,
            "conn-a",
            new BeginEditorContentUploadRequest(opened.EditorSessionId, "op-bad", bytes.Length, digest)));
        Must(runtime.UploadChunk(User, "conn-a", new EditorContentUploadChunkRequest(mismatchBegin.UploadId, 0, 1, Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', bytes.Length))))));
        AssertEqual(
            IpcErrorCodes.EditorUploadHashMismatch,
            runtime.CommitUpload(User, "conn-a", new CommitEditorContentUploadRequest(mismatchBegin.UploadId), CancellationToken.None).ErrorCode,
            "hash mismatch.");
        return 6;
    }

    private static int SaveIdempotencyAndFaults()
    {
        var files = Seed();
        var blobs = new MemoryEditorBlobStore();
        var faults = new MutableEditorSaveFaultInjector { Fault = EditorSaveFaultPoint.BeforeAtomicReplace };
        var runtime = new EditorRuntime(ProjectId, files, blobs, faults);
        var opened = Must(runtime.Open(User, "conn-a", ProjectId, ChapterId, "chapter.md", true));
        var original = files.Read("Draft/" + ChapterId + "/chapter.md").Value!.Bytes;
        var payload = TextDocumentCodec.EncodeUtf8NoBomLf("mutated");
        try
        {
            runtime.Save(
                User,
                "conn-a",
                new SaveDraftEditorSessionRequest(
                    opened.EditorSessionId,
                    "op-fault",
                    opened.LastPersistedDigest,
                    Upload(runtime, opened.EditorSessionId, "op-fault", payload)),
                CancellationToken.None);
            throw new InvalidOperationException("pre-publish fault must throw.");
        }
        catch (EditorSaveFaultInjectedException)
        {
        }

        AssertTrue(original.AsSpan().SequenceEqual(files.Read("Draft/" + ChapterId + "/chapter.md").Value!.Bytes), "pre-publish keeps old Draft.");
        faults.Fault = EditorSaveFaultPoint.None;
        var saved = Must(runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(
                opened.EditorSessionId,
                "op-ok",
                opened.LastPersistedDigest,
                Upload(runtime, opened.EditorSessionId, "op-ok", payload)),
            CancellationToken.None));
        AssertEqual(ContentDigest.Sha256Hex(payload), files.Read("Draft/" + ChapterId + "/chapter.md").Value!.Digest, "post-publish new Draft.");
        _ = saved;
        return 3;
    }

    private static int SaveBindingAndUnknownOutcome()
    {
        var files = Seed();
        var blobs = new MemoryEditorBlobStore();
        var runtime = new EditorRuntime(ProjectId, files, blobs);
        var first = Must(runtime.Open(User, "conn-a", ProjectId, ChapterId, "chapter.md", true));
        var second = Must(runtime.Open(User, "conn-a", ProjectId, OtherChapter, "notes.txt", true));
        var foreignPayload = "foreign-session"u8.ToArray();
        var foreign = Upload(runtime, second.EditorSessionId, "op-foreign", foreignPayload);

        var crossSession = runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(
                first.EditorSessionId,
                "op-target",
                first.LastPersistedDigest,
                foreign),
            CancellationToken.None);
        AssertEqual(IpcErrorCodes.EditorUploadInvalid, crossSession.ErrorCode, "BlobRef must stay bound to its editor session and save operation.");
        AssertEqual("alpha", Encoding.UTF8.GetString(files.Read("Draft/" + ChapterId + "/chapter.md").Value!.Bytes), "Foreign BlobRef must not alter another Draft.");

        var local = Upload(runtime, first.EditorSessionId, "op-upload", "local"u8.ToArray());
        var wrongOperation = runtime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(
                first.EditorSessionId,
                "op-save",
                first.LastPersistedDigest,
                local),
            CancellationToken.None);
        AssertEqual(IpcErrorCodes.EditorUploadInvalid, wrongOperation.ErrorCode, "BlobRef must stay bound to its SaveOperationId.");

        var faultFiles = Seed();
        var faultBlobs = new MemoryEditorBlobStore();
        var faults = new MutableEditorSaveFaultInjector { Fault = EditorSaveFaultPoint.BeforeIpcResponse };
        var faultRuntime = new EditorRuntime(ProjectId, faultFiles, faultBlobs, faults);
        var faultSession = Must(faultRuntime.Open(User, "conn-a", ProjectId, ChapterId, "chapter.md", true));
        var published = "published-before-response"u8.ToArray();
        try
        {
            faultRuntime.Save(
                User,
                "conn-a",
                new SaveDraftEditorSessionRequest(
                    faultSession.EditorSessionId,
                    "op-unknown",
                    faultSession.LastPersistedDigest,
                    Upload(faultRuntime, faultSession.EditorSessionId, "op-unknown", published)),
                CancellationToken.None);
            throw new InvalidOperationException("post-publish response fault must throw.");
        }
        catch (EditorSaveFaultInjectedException exception) when (exception.Point == EditorSaveFaultPoint.BeforeIpcResponse)
        {
        }

        var afterUnknown = Must(faultRuntime.GetState(User, "conn-a", faultSession.EditorSessionId));
        AssertEqual(ContentDigest.Sha256Hex(published), afterUnknown.LastPersistedDigest, "Core query must observe the published bytes after an unknown response outcome.");
        AssertEqual(2L, afterUnknown.LastPersistedRevision, "Core session revision must advance after a known publish even when the response is lost.");
        faults.Fault = EditorSaveFaultPoint.None;
        var nextPayload = "next"u8.ToArray();
        var next = Must(faultRuntime.Save(
            User,
            "conn-a",
            new SaveDraftEditorSessionRequest(
                faultSession.EditorSessionId,
                "op-next",
                afterUnknown.LastPersistedDigest,
                Upload(faultRuntime, faultSession.EditorSessionId, "op-next", nextPayload)),
            CancellationToken.None));
        AssertEqual(3L, next.PersistedRevision, "The next save must not reuse the lost-response revision.");
        return 6;
    }

    private static MemoryDraftFileStore Seed()
    {
        var files = new MemoryDraftFileStore();
        files.Write(ChapterId, "chapter.md", "alpha"u8.ToArray());
        files.Write(OtherChapter, "notes.txt", "beta"u8.ToArray());
        return files;
    }

    private static IpcBlobRef Upload(EditorRuntime runtime, string editorSessionId, string saveOperationId, byte[] payload)
    {
        var digest = ContentDigest.Sha256Hex(payload);
        var begin = Must(runtime.BeginUpload(
            User,
            "conn-a",
            new BeginEditorContentUploadRequest(editorSessionId, saveOperationId, payload.Length, digest)));
        var count = payload.Length == 0
            ? 0
            : (payload.Length + begin.MaxChunkBytes - 1) / begin.MaxChunkBytes;
        for (var index = 0; index < count; index++)
        {
            var offset = index * begin.MaxChunkBytes;
            var length = Math.Min(begin.MaxChunkBytes, payload.Length - offset);
            Must(runtime.UploadChunk(
                User,
                "conn-a",
                new EditorContentUploadChunkRequest(
                    begin.UploadId,
                    index,
                    count,
                    Convert.ToBase64String(payload, offset, length))));
        }

        return Must(runtime.CommitUpload(
            User,
            "conn-a",
            new CommitEditorContentUploadRequest(begin.UploadId),
            CancellationToken.None)).BlobRef;
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

    private sealed class MemoryDraftFileStore : IDraftFileStore
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        public bool AuthorityTouched { get; private set; }

        public void Write(string chapterId, string fileName, byte[] bytes)
        {
            files["Draft/" + chapterId + "/" + fileName] = bytes.ToArray();
        }

        public string LeaseKeyFor(string chapterId, string fileName)
        {
            _ = files;
            return "lease:Draft/" + chapterId + "/" + fileName;
        }

        public EditorResult<string> ResolveLeaseKey(string relativePath) =>
            DraftDocumentResolver.IsDraftWorkspacePath(relativePath)
                ? EditorResult<string>.Ok("lease:" + relativePath)
                : EditorResult<string>.Fail(IpcErrorCodes.EditorDocumentNotWritable);

        public EditorResult<DraftFileSnapshot> Read(string relativePath)
        {
            if (!DraftDocumentResolver.IsDraftWorkspacePath(relativePath) || DraftDocumentResolver.IsManuscriptPath(relativePath))
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
            }

            if (!files.TryGetValue(relativePath, out var bytes))
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
            }

            return EditorResult<DraftFileSnapshot>.Ok(new DraftFileSnapshot(
                relativePath,
                "lease:" + relativePath,
                bytes.ToArray(),
                ContentDigest.Sha256Hex(bytes),
                bytes.Length));
        }

        public EditorResult<DraftFileSnapshot> ReadFromLeaseKey(string leaseKey)
        {
            if (!leaseKey.StartsWith("lease:", StringComparison.Ordinal))
            {
                return EditorResult<DraftFileSnapshot>.Fail(IpcErrorCodes.EditorDocumentNotWritable);
            }

            return Read(leaseKey["lease:".Length..]);
        }

        public string DigestOf(ReadOnlySpan<byte> bytes) => ContentDigest.Sha256Hex(bytes);

        public EditorResult<DraftFileSnapshot> AtomicReplace(
            string relativePath,
            string expectedDigest,
            byte[] utf8NoBomLf,
            IEditorSaveFaultInjector faults)
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

            faults.ThrowIf(EditorSaveFaultPoint.AfterTempFileWrite);
            faults.ThrowIf(EditorSaveFaultPoint.AfterFlush);
            faults.ThrowIf(EditorSaveFaultPoint.BeforeAtomicReplace);
            files[relativePath] = utf8NoBomLf.ToArray();
            faults.ThrowIf(EditorSaveFaultPoint.AfterAtomicReplace);
            return Read(relativePath);
        }
    }

    private sealed class MemoryEditorBlobStore : IImmutableBlobStore
    {
        private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

        public BlobStageResult Stage(Stream source, string? expectedDigest = null, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var bytes = memory.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (expectedDigest is not null && !StringComparer.Ordinal.Equals(ContentDigest.Normalize(expectedDigest), digest))
            {
                throw new InvalidOperationException("Digest mismatch.");
            }

            blobs[digest] = bytes;
            return new BlobStageResult(digest, digest, bytes.Length, false);
        }

        public Stream OpenRead(string digest) => new MemoryStream(blobs[ContentDigest.Normalize(digest)], writable: false);

        public bool Verify(string digest, CancellationToken cancellationToken = default) =>
            blobs.ContainsKey(ContentDigest.Normalize(digest));
    }
}
