using System.Text;
using System.Text.Json;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.UI.WebView;

namespace LLMW.Writing.UI.Tests;

internal static class Wp16UiTests
{
    private const string AppDocument = "https://app.llmw.invalid/index.html";

    public static int Run()
    {
        var count = 0;
        count += OriginAndAssets();
        count += ParserClosedSet();
        count += MaliciousEditorMessages();
        count += CrashShadowAndAutosave();
        count += SaveStateMachine();
        count += DocumentChunker();
        count += SecuritySurface();
        Console.WriteLine("WP16 UI tests passed (" + count + ").");
        return count;
    }

    private static int OriginAndAssets()
    {
        Program.AssertTrue(AppOriginPolicy.IsApplicationResource("https://app.llmw.invalid/editor.bundle.js"), "editor bundle must be an application resource.");
        Program.AssertTrue(WebResourcePolicy.IsAllowed("https://app.llmw.invalid/editor.bundle.js"), "editor bundle fetch must be allowed.");
        Program.AssertTrue(!WebResourcePolicy.IsAllowed("https://cdn.example/codemirror.js"), "CDN script must be blocked.");
        return 3;
    }

    private static int ParserClosedSet()
    {
        var change = BridgeEnvelopeParser.Parse(Envelope(
            BridgeSemanticTypes.EditorChange,
            "s",
            "c1",
            "{\"editorSessionId\":\"es1\",\"sequence\":1,\"expectedSequence\":0,\"from\":0,\"to\":0,\"text\":\"x\"}"));
        Program.AssertTrue(change.Success && change.Message!.Editor is not null, "editor.change must parse.");

        Program.AssertEqual(
            BridgeErrorCodes.UnknownMessageType,
            BridgeEnvelopeParser.Parse(Envelope("editor.openPath", "s", "m", "{}")).Error!.Code,
            "path open must stay unknown.");
        Program.AssertEqual(
            BridgeErrorCodes.UnknownMessageType,
            BridgeEnvelopeParser.Parse(Envelope("readFile", "s", "m", "{}")).Error!.Code,
            "readFile must stay unknown.");
        Program.AssertEqual(
            BridgeErrorCodes.UnknownMessageType,
            BridgeEnvelopeParser.Parse(Envelope("writeFile", "s", "m", "{}")).Error!.Code,
            "writeFile must stay unknown.");
        Program.AssertEqual(
            BridgeErrorCodes.InvalidSchema,
            BridgeEnvelopeParser.Parse(Envelope(
                BridgeSemanticTypes.EditorChange,
                "s",
                "bad",
                "{\"editorSessionId\":\"es1\",\"sequence\":1,\"expectedSequence\":0,\"from\":0,\"to\":0,\"text\":\"x\",\"path\":\"C:\\\\x\"}")).Error!.Code,
            "path field on change must be rejected.");
        return 5;
    }

    private static int MaliciousEditorMessages()
    {
        var processor = new BridgeMessageProcessor();
        var hello = processor.BeginDocumentSession();
        var session = JsonDocument.Parse(hello).RootElement.GetProperty("documentSessionId").GetString()!;
        Program.AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session, "ready", "{\"shell\":\"wp16-editor\"}"))).Dispatched, "ready.");

        var stale = processor.ProcessIncoming(Msg(Envelope(
            BridgeSemanticTypes.EditorChange,
            "not-current",
            "chg-stale",
            "{\"editorSessionId\":\"es1\",\"sequence\":1,\"expectedSequence\":0,\"from\":0,\"to\":0,\"text\":\"x\"}")));
        Program.AssertEqual(BridgeErrorCodes.StaleSession, stale.Error!.Code, "stale DocumentSession.");
        Program.AssertTrue(stale.Editor is null, "stale must not dispatch editor.");

        var unknown = processor.ProcessIncoming(Msg(Envelope("editor.invoke", session, "inv", "{\"method\":\"save\"}")));
        Program.AssertEqual(BridgeErrorCodes.UnknownMessageType, unknown.Error!.Code, "generic invoke.");

        var extra = processor.ProcessIncoming(Msg(Envelope(
            BridgeSemanticTypes.EditorChange,
            session,
            "chg-ok",
            "{\"editorSessionId\":\"es1\",\"sequence\":1,\"expectedSequence\":0,\"from\":0,\"to\":0,\"text\":\"x\"}"), additional: 1));
        Program.AssertEqual(BridgeErrorCodes.AdditionalObjectsDenied, extra.Error!.Code, "AdditionalObjects.");

        var huge = new string('a', BridgeProtocol.MaximumEditorInsertChars + 1);
        var oversized = BridgeEnvelopeParser.Parse(Envelope(
            BridgeSemanticTypes.EditorChange,
            session,
            "huge",
            "{\"editorSessionId\":\"es1\",\"sequence\":1,\"expectedSequence\":0,\"from\":0,\"to\":0,\"text\":\"" + huge + "\"}"));
        Program.AssertEqual(BridgeErrorCodes.InvalidSchema, oversized.Error!.Code, "oversized patch text.");

        var wrongSession = processor.ProcessIncoming(Msg(Envelope(
            BridgeSemanticTypes.EditorChange,
            session,
            "chg-ok2",
            "{\"editorSessionId\":\"forged\",\"sequence\":1,\"expectedSequence\":0,\"from\":0,\"to\":0,\"text\":\"x\"}")));
        Program.AssertTrue(wrongSession.Dispatched && wrongSession.Editor is not null, "processor delivers; host binding rejects forged EditorSession.");
        return 6;
    }

    private static int CrashShadowAndAutosave()
    {
        var shadow = new EditorCrashShadow("es1", RepeatHex('a'), "hello", dirty: false);
        Program.AssertTrue(shadow.ApplyChange(1, 0, 5, 5, "x").Succeeded, "valid patch.");
        Program.AssertEqual("hellox", shadow.LogicalText, "patch apply.");
        Program.AssertTrue(shadow.Dirty, "document change is dirty.");
        Program.AssertEqual(IpcErrorCodes.EditorPatchSequence, shadow.ApplyChange(1, 0, 0, 0, "z").ErrorCode ?? "", "sequence rollback.");
        Program.AssertEqual("hellox", shadow.LogicalText, "invalid patch must not mutate.");
        Program.AssertEqual(IpcErrorCodes.EditorPatchSequence, shadow.ApplyChange(2, 0, 0, 0, "z").ErrorCode ?? "", "duplicate/wrong expected.");
        Program.AssertEqual(IpcErrorCodes.EditorPatchInvalid, shadow.ApplyChange(2, 1, 9, 9, "z").ErrorCode ?? "", "invalid range.");
        var empty = shadow.ApplyChange(2, 1, 0, 0, "");
        Program.AssertTrue(empty.Succeeded, "empty insert at start is a valid no-op patch.");

        var scheduler = new EditorAutosaveScheduler();
        var t0 = DateTimeOffset.UnixEpoch;
        scheduler.NoteValidatedDocumentChange(t0);
        scheduler.NoteValidatedDocumentChange(t0.AddMilliseconds(100));
        scheduler.NoteValidatedDocumentChange(t0.AddMilliseconds(250));
        scheduler.NoteValidatedDocumentChange(t0.AddMilliseconds(499));
        Program.AssertTrue(!scheduler.TryBeginSave(t0.AddMilliseconds(499), explicitSave: false), "no save before debounce.");
        Program.AssertTrue(scheduler.TryBeginSave(t0.AddMilliseconds(999), explicitSave: false), "one coalesced save.");
        Program.AssertTrue(!scheduler.TryBeginSave(t0.AddMilliseconds(1000), explicitSave: false), "no overlapping save.");
        scheduler.NoteValidatedDocumentChange(t0.AddMilliseconds(1001));
        Program.AssertTrue(scheduler.DirtyAfterSave, "edit during save stays dirty.");
        scheduler.CompleteSave(true, shadowStillNewer: true, t0.AddMilliseconds(1100), scheduleRetryOnFailure: false);
        Program.AssertTrue(scheduler.DueAt is not null, "next save scheduled after dirty-during-save.");

        var fail = new EditorAutosaveScheduler();
        fail.NoteValidatedDocumentChange(t0);
        Program.AssertTrue(fail.TryBeginSave(t0.AddMilliseconds(500), false), "fail path start.");
        fail.CompleteSave(false, true, t0.AddMilliseconds(600), scheduleRetryOnFailure: false);
        Program.AssertTrue(fail.DueAt is null, "failure must not tight-loop.");
        Program.AssertTrue(fail.TryBeginSave(t0.AddMilliseconds(601), explicitSave: true), "explicit save remains available.");

        var resync = new EditorCrashShadow("es1", RepeatHex('a'), "old", true);
        var bytes = Encoding.UTF8.GetBytes("NEW");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        Program.AssertTrue(resync.BeginResync("t1", bytes.Length, sha).Succeeded, "resync begin.");
        Program.AssertTrue(resync.AcceptResyncChunk("t1", 0, 1, Convert.ToBase64String(bytes)).Succeeded, "resync chunk.");
        Program.AssertTrue(resync.CommitResync("t1").Succeeded, "resync commit.");
        Program.AssertEqual("NEW", resync.LogicalText, "resync replaces atomically.");
        Program.AssertTrue(!resync.BeginResync("t2", 1, sha).Succeeded || resync.LogicalText == "NEW", "partial never replaces on failed begin of wrong size/hash later.");
        var partial = new EditorCrashShadow("es1", RepeatHex('a'), "keep", true);
        Program.AssertTrue(partial.BeginResync("t3", bytes.Length, sha).Succeeded, "partial begin.");
        Program.AssertEqual("keep", partial.LogicalText, "partial transfer must not replace shadow.");
        return 18;
    }

    private static int SaveStateMachine()
    {
        Program.AssertTrue(EditorSaveStateMachine.IsLegal(EditorSaveUiState.Dirty, EditorSaveUiState.Saving), "dirty to saving.");
        Program.AssertTrue(!EditorSaveStateMachine.IsLegal(EditorSaveUiState.Clean, EditorSaveUiState.Saving), "clean cannot skip to saving.");
        Program.AssertTrue(!EditorSaveStateMachine.IsLegal(EditorSaveUiState.RecoveryConflict, EditorSaveUiState.Dirty), "conflict is not last-writer-wins dirty.");
        var fsm = new EditorSaveStateMachine();
        Program.AssertTrue(fsm.TryTransition(EditorSaveUiState.Dirty), "clean to dirty.");
        Program.AssertEqual("unsaved", fsm.WireName, "dirty wire name.");
        return 4;
    }

    private static int DocumentChunker()
    {
        var chunks = EditorDocumentChunker.Split("hello");
        Program.AssertTrue(chunks.TotalBytes == Encoding.UTF8.GetByteCount("hello"), "utf8 length.");
        Program.AssertTrue(chunks.ChunkCount == 1, "small doc one chunk.");
        var outbound = EditorDocumentChunker.ToOutbound("doc", "es", chunks);
        Program.AssertTrue(outbound[0].Contains("editor.document.begin", StringComparison.Ordinal), "begin.");
        Program.AssertTrue(outbound[^1].Contains("editor.document.commit", StringComparison.Ordinal), "commit.");
        Program.AssertTrue(!outbound[0].Contains("C:\\", StringComparison.Ordinal), "no path in transfer.");
        return 5;
    }

    private static int SecuritySurface()
    {
        var root = FindRepoRoot();
        var index = File.ReadAllText(Path.Combine(root, "src", "web-editor", "app", "index.html"));
        var bridge = File.ReadAllText(Path.Combine(root, "src", "web-editor", "app", "bridge.js"));
        var editor = File.ReadAllText(Path.Combine(root, "src", "web-editor", "src", "editor.js"));
        Program.AssertTrue(index.Contains("nonce-llmw-editor", StringComparison.Ordinal), "CSP nonce for CodeMirror styles.");
        Program.AssertTrue(!index.Contains("unsafe-inline", StringComparison.Ordinal), "CSP must not allow unsafe-inline.");
        Program.AssertTrue(!index.Contains("unsafe-eval", StringComparison.Ordinal), "CSP must not allow unsafe-eval.");
        Program.AssertTrue(index.Contains("editor.bundle.js", StringComparison.Ordinal), "bundle script.");
        Program.AssertTrue(bridge.Contains("wp16-editor", StringComparison.Ordinal), "renderer ready shell.");
        Program.AssertTrue(!bridge.Contains("innerHTML", StringComparison.Ordinal) && !editor.Contains("innerHTML", StringComparison.Ordinal), "no innerHTML.");
        Program.AssertTrue(!bridge.Contains("LLMW_UI_BOOTSTRAP_TOKEN", StringComparison.Ordinal), "renderer must not receive bootstrap secrets.");
        var app = File.ReadAllText(Path.Combine(root, "src", "LLMW.Writing.UI", "App.xaml.cs"));
        Program.AssertTrue(!app.Contains("LLMW_UI_BOOTSTRAP_TOKEN", StringComparison.Ordinal), "App must not embed the bootstrap secret.");
        Program.AssertTrue(app.Contains("CoreHostService", StringComparison.Ordinal), "WP16 may start Core from the UI process.");
        var bind = BridgeOutboundJson.EditorBind("s", "m", "es", "t", "md", "chapter.md", true, "saved", "userEditor", "none", RepeatHex('a'));
        Program.AssertTrue(!bind.Contains("Draft/", StringComparison.Ordinal), "bind must not include a filesystem path.");
        Program.AssertTrue(!bind.Contains("C:", StringComparison.Ordinal), "bind must not include a drive path.");
        return 10;
    }

    private static IncomingWebMessage Msg(string json, int additional = 0)
        => new()
        {
            Source = AppDocument,
            CurrentDocument = AppDocument,
            Json = json,
            AdditionalObjectCount = additional
        };

    private static string Envelope(string semanticType, string session, string messageId, string payload)
        => "{\"protocol\":\"llmw-web-bridge\",\"version\":1,\"documentSessionId\":\"" + session + "\",\"messageId\":\"" + messageId + "\",\"semanticType\":\"" + semanticType + "\",\"payload\":" + payload + "}";

    private static string RepeatHex(char value) => new(value, 64);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LLMW.Writing.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
