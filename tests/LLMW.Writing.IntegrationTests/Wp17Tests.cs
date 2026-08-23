using LLMW.Writing.Application.Editor;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Infrastructure.Docx;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    private static async Task RunWp17TestsAsync()
    {
        var root = CreateWp16Project();
        var adapter = new OpenXmlDocxDocumentAdapter();
        var original = adapter.Create(DocxEditorDocument.FromLogicalText("before"));
        AssertTrue(original.Succeeded, "DOCX fixture creation failed.");
        File.WriteAllBytes(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.docx"), original.Value!);

        var workspace = "wp17slice" + Guid.NewGuid().ToString("N");
        var uiToken = IpcBootstrapToken.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var core = StartCore(workspace, uiToken, IpcBootstrapToken.Create());
        try
        {
            await using var ui = await ConnectAndHandshakeAsync(
                IpcPipeNames.Core(workspace), workspace, uiToken, IpcClientKind.Ui, timeout.Token);
            var projectId = Guid.Parse((await ui.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(root),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                timeout.Token)).Payload.ProjectId);
            var opened = await ui.RequestAsync(
                IpcSemanticTypes.OpenDraftEditorSession,
                new OpenDraftEditorSessionRequest(Wp16ChapterA, "chapter.docx", true),
                IpcJsonContext.Default.OpenDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.OpenDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);
            AssertTrue(opened.Payload.Writable && opened.Payload.FormatKind == EditorFormatKind.Docx, "DOCX must use the existing writable Draft session and lease.");

            var editorText = "after\nsecond paragraph"u8.ToArray();
            var blob = await UploadAsync(ui, projectId, opened.Payload.EditorSessionId, "op-docx", editorText, timeout.Token);
            await ui.RequestAsync(
                IpcSemanticTypes.SaveDraftEditorSession,
                new SaveDraftEditorSessionRequest(opened.Payload.EditorSessionId, "op-docx", opened.Payload.LastPersistedDigest, blob),
                IpcJsonContext.Default.SaveDraftEditorSessionRequestEnvelope,
                IpcJsonContext.Default.SaveDraftEditorSessionResponseEnvelope,
                timeout.Token,
                projectId);

            var persisted = adapter.Read(File.ReadAllBytes(Path.Combine(root, "Draft", Wp16ChapterA, "chapter.docx")));
            AssertTrue(persisted.Succeeded, "Core must persist a valid DOCX package.");
            AssertEqual("after\nsecond paragraph", persisted.Value!.LogicalText, "DOCX save must serialize editor text deterministically.");
            AssertTrue(!Directory.Exists(Path.Combine(root, "Manuscript")), "DOCX save must not create or update Current Manuscript.");
            AssertTrue(!Directory.Exists(Path.Combine(root, "Candidate")), "DOCX save must not create a Candidate.");
        }
        finally
        {
            StopCore(core);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Console.WriteLine("WP17 integration tests passed.");
    }
}
