using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private const string Wp16ChapterA = "018f3e78-1234-7abc-8def-0123456789a1";
    private const string Wp16ChapterB = "018f3e78-1234-7abc-8def-0123456789a2";

    private static async Task RunWp16TestsAsync()
    {
        await RealCoreEditorVerticalSliceAsync();
        await LeaseTwoSessionsSameDraftAsync();
        await StaleExternalSaveDeniedAsync();
        await ManuscriptSelectorDeniedAsync();
        await LargeTextUploadAndSaveAsync();
        await ReconnectUnknownOutcomeDoesNotReplayAsync();
        Console.WriteLine("WP16 integration tests passed.");
    }

    private static async Task RealCoreEditorVerticalSliceAsync()
    {
        var root = CreateWp16Project();
        var workspaceInstanceId = "wp16slice" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        var runtimeToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var core = StartCore(workspaceInstanceId, uiToken, runtimeToken);
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            var openedProject = await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token);
            var projectId = Guid.Parse(openedProject.Payload.ProjectId);
            var session = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterA, "chapter.md", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            AssertTrue(session.Payload.Writable, "First Draft open must be writable.");
            var payload = "hello editor\n"u8.ToArray();
            var digest = ContentDigest.Sha256Hex(payload);
            var begin = await ui.RequestAsync(
                IpcSemanticTypes.BeginEditorContentUpload,
                new BeginEditorContentUploadRequest(session.Payload.EditorSessionId, "op-slice", payload.Length, digest),
                IpcJsonContext.Default.BeginEditorContentUploadRequestEnvelope,
                IpcJsonContext.Default.BeginEditorContentUploadResponseEnvelope,
                timeout.Token,
                projectId);
            await ui.RequestAsync(
                IpcSemanticTypes.EditorContentUploadChunk,
                new EditorContentUploadChunkRequest(begin.Payload.UploadId, 0, 1, Convert.ToBase64String(payload)),
                IpcJsonContext.Default.EditorContentUploadChunkRequestEnvelope,
                IpcJsonContext.Default.EditorContentUploadChunkResponseEnvelope,
                timeout.Token,
                projectId);
            var committed = await ui.RequestAsync(
                IpcSemanticTypes.CommitEditorContentUpload,
                new CommitEditorContentUploadRequest(begin.Payload.UploadId),
                IpcJsonContext.Default.CommitEditorContentUploadRequestEnvelope,
                IpcJsonContext.Default.CommitEditorContentUploadResponseEnvelope,
                timeout.Token,
                projectId);
            var saved = await ui.RequestAsync(
                IpcSemanticTypes.SaveDraftEditorSession,
                new SaveDraftEditorSessionRequest(
                    session.Payload.EditorSessionId,
                    "op-slice",
                    session.Payload.LastPersistedDigest,
                    committed.Payload.BlobRef),
                IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            var onDisk = File.ReadAllBytes(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.md"));
            AssertTrue(onDisk.AsSpan().SequenceEqual(payload), "Persisted Draft bytes must match the save.");
            AssertEqual(digest, saved.Payload.PersistedDigest, "Returned digest must match disk.");
            AssertTrue(!Directory.Exists(Path.Combine(root, "Manuscript")), "Draft save must not create Manuscript.");
            AssertTrue(!File.Exists(Path.Combine(root, "Candidate")), "Draft save must not create Candidate.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task LeaseTwoSessionsSameDraftAsync()
    {
        var root = CreateWp16Project();
        var workspaceInstanceId = "wp16lease" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var core = StartCore(workspaceInstanceId, uiToken, IpcBootstrapToken.Create());
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            var projectId = Guid.Parse((await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token)).Payload.ProjectId);
            var first = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterA, "chapter.md", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            var second = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterA, "chapter.md", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            var other = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterB, "notes.txt", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            AssertTrue(first.Payload.Writable, "first writer.");
            AssertTrue(!second.Payload.Writable, "same Draft second session is read-only.");
            AssertTrue(other.Payload.Writable, "different Draft is writable.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task StaleExternalSaveDeniedAsync()
    {
        var root = CreateWp16Project();
        var workspaceInstanceId = "wp16stale" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var core = StartCore(workspaceInstanceId, uiToken, IpcBootstrapToken.Create());
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            var projectId = Guid.Parse((await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token)).Payload.ProjectId);
            var session = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterA, "chapter.md", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            File.WriteAllBytes(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.md"), "C"u8.ToArray());
            var payload = "B"u8.ToArray();
            var blob = await UploadAsync(ui, projectId, session.Payload.EditorSessionId, "op-stale", payload, timeout.Token);
            try
            {
                await ui.RequestAsync(
                    IpcSemanticTypes.SaveDraftEditorSession,
                    new SaveDraftEditorSessionRequest(
                        session.Payload.EditorSessionId,
                        "op-stale",
                        session.Payload.LastPersistedDigest,
                        blob),
                    IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
                    IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
                    timeout.Token,
                    projectId);
                throw new InvalidOperationException("stale save must fail.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.EditorStaleBase, exception.ErrorCode, "external change is stale.");
            }

            AssertEqual("C", File.ReadAllText(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.md")), "external bytes preserved.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ManuscriptSelectorDeniedAsync()
    {
        var root = CreateWp16Project();
        var workspaceInstanceId = "wp16ms" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var core = StartCore(workspaceInstanceId, uiToken, IpcBootstrapToken.Create());
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            var projectId = Guid.Parse((await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token)).Payload.ProjectId);
            try
            {
                await ui.RequestAsync(
                    IpcSemanticTypes.OpenDraftEditorSession,
                    new OpenDraftEditorSessionRequest(Wp16ChapterA, "../Manuscript.md", true),
                    IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                    IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                    timeout.Token,
                    projectId);
                throw new InvalidOperationException("Manuscript escape must be denied.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.EditorDocumentNotWritable, exception.ErrorCode, "escape filename denied.");
            }
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task LargeTextUploadAndSaveAsync()
    {
        var root = CreateWp16Project();
        var workspaceInstanceId = "wp16large" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var core = StartCore(workspaceInstanceId, uiToken, IpcBootstrapToken.Create());
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            var projectId = Guid.Parse((await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token)).Payload.ProjectId);
            var session = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterA, "chapter.md", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            var payload = new byte[(1024 * 1024) + 4096];
            Array.Fill(payload, (byte)'a');
            var blob = await UploadAsync(ui, projectId, session.Payload.EditorSessionId, "op-large", payload, timeout.Token);
            var saved = await ui.RequestAsync(
                IpcSemanticTypes.SaveDraftEditorSession,
                new SaveDraftEditorSessionRequest(
                    session.Payload.EditorSessionId,
                    "op-large",
                    session.Payload.LastPersistedDigest,
                    blob),
                IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            var onDisk = File.ReadAllBytes(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.md"));
            AssertTrue(onDisk.AsSpan().SequenceEqual(payload), "large save bytes.");
            AssertEqual(ContentDigest.Sha256Hex(payload), saved.Payload.PersistedDigest, "large digest.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ReconnectUnknownOutcomeDoesNotReplayAsync()
    {
        var root = CreateWp16Project();
        var workspaceInstanceId = "wp16unk" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var core = StartCore(
            workspaceInstanceId,
            uiToken,
            IpcBootstrapToken.Create(),
            extraEnvironment: new Dictionary<string, string> { ["LLMW_EDITOR_SAVE_FAULT"] = "BeforeIpcResponse" });
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspaceInstanceId),
                workspaceInstanceId,
                uiToken,
                IpcClientKind.Ui,
                timeout.Token);
            var projectId = Guid.Parse((await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token)).Payload.ProjectId);
            var session = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterA, "chapter.md", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            var payload = "published-before-response\n"u8.ToArray();
            var blob = await UploadAsync(ui, projectId, session.Payload.EditorSessionId, "op-unk", payload, timeout.Token);
            try
            {
                await ui.RequestAsync(
                    IpcSemanticTypes.SaveDraftEditorSession,
                    new SaveDraftEditorSessionRequest(
                        session.Payload.EditorSessionId,
                        "op-unk",
                        session.Payload.LastPersistedDigest,
                        blob),
                    IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
                    IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
                    timeout.Token,
                    projectId);
                throw new InvalidOperationException("faulted save must surface unknown/error.");
            }
            catch (IpcProtocolException exception)
            {
                AssertEqual(IpcErrorCodes.EditorSaveOutcomeUnknown, exception.ErrorCode, "unknown outcome.");
            }

            var onDisk = File.ReadAllBytes(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.md"));
            AssertTrue(onDisk.AsSpan().SequenceEqual(payload), "post-publish bytes are truth.");
            var state = await ui.RequestAsync(
                IpcSemanticTypes.GetDraftEditorSessionState,
                new GetDraftEditorSessionStateRequest(session.Payload.EditorSessionId),
                IpcJsonContext.Default.GetDraftEditorSessionStateRequestEnvelope,
                IpcJsonContext.Default.GetDraftEditorSessionStateResponseEnvelope,
                timeout.Token,
                projectId);
            AssertEqual(ContentDigest.Sha256Hex(payload), state.Payload.LastPersistedDigest, "query sees persisted digest; no automatic replay.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<IpcBlobRef> UploadAsync(
        IpcClientSession ui,
        Guid projectId,
        string editorSessionId,
        string saveOperationId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var digest = ContentDigest.Sha256Hex(payload);
        var begin = await ui.RequestAsync(
            IpcSemanticTypes.BeginEditorContentUpload,
            new BeginEditorContentUploadRequest(editorSessionId, saveOperationId, payload.Length, digest),
            IpcJsonContext.Default.BeginEditorContentUploadRequestEnvelope,
            IpcJsonContext.Default.BeginEditorContentUploadResponseEnvelope,
            cancellationToken,
            projectId);
        var chunk = EditorTransportLimits.MaximumChunkUtf8Bytes;
        var count = payload.Length == 0 ? 0 : (payload.Length + chunk - 1) / chunk;
        for (var index = 0; index < count; index++)
        {
            var offset = index * chunk;
            var length = Math.Min(chunk, payload.Length - offset);
            await ui.RequestAsync(
                IpcSemanticTypes.EditorContentUploadChunk,
                new EditorContentUploadChunkRequest(
                    begin.Payload.UploadId,
                    index,
                    count,
                    Convert.ToBase64String(payload, offset, length)),
                IpcJsonContext.Default.EditorContentUploadChunkRequestEnvelope,
                IpcJsonContext.Default.EditorContentUploadChunkResponseEnvelope,
                cancellationToken,
                projectId);
        }

        var committed = await ui.RequestAsync(
            IpcSemanticTypes.CommitEditorContentUpload,
            new CommitEditorContentUploadRequest(begin.Payload.UploadId),
            IpcJsonContext.Default.CommitEditorContentUploadRequestEnvelope,
            IpcJsonContext.Default.CommitEditorContentUploadResponseEnvelope,
            cancellationToken,
            projectId);
        return committed.Payload.BlobRef;
    }

    private static string CreateWp16Project()
    {
        var root = CreateValidProjectFixture();
        Directory.CreateDirectory(Path.Combine(root, "Draft", Wp16ChapterA));
        Directory.CreateDirectory(Path.Combine(root, "Draft", Wp16ChapterB));
        File.WriteAllBytes(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.md"), "alpha"u8.ToArray());
        File.WriteAllBytes(Path.Combine(root, "Draft", Wp16ChapterB, "notes.txt"), "beta"u8.ToArray());
        return root;
    }
}
