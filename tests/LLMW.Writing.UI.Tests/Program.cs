using System.Text;
using System.Text.Json;
using LLMW.Writing.UI.WebView;

namespace LLMW.Writing.UI.Tests;

internal static class Program
{
    private const string AppDocument = "https://app.llmw.invalid/index.html";
    private const string AppRoot = "https://app.llmw.invalid/";

    private static int Main()
    {
        try
        {
            var count = 0;
            count += Wp15OriginTests();
            count += Wp15NavigationAndResourceTests();
            count += Wp15ExternalUriTests();
            count += Wp15BridgeContractTests();
            count += Wp15SessionAndReplayTests();
            count += Wp15SecuritySurfaceTests();
            count += Wp15CorrectiveLifecycleTests();
            Console.WriteLine($"UI WebView bridge tests passed ({count}).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Wp15OriginTests()
    {
        AssertTrue(AppOriginPolicy.IsApplicationDocument(AppRoot), "app origin root must be an application document.");
        AssertTrue(AppOriginPolicy.IsApplicationDocument(AppDocument), "index.html must be an application document.");
        AssertTrue(AppOriginPolicy.IsApplicationResource("https://app.llmw.invalid/bridge.js"), "bridge.js must be an application resource.");
        AssertTrue(AppOriginPolicy.IsApplicationResource("https://app.llmw.invalid/app.css"), "app.css must be an application resource.");
        AssertTrue(!AppOriginPolicy.IsApplicationDocument("http://app.llmw.invalid/"), "http app host must be rejected.");
        AssertTrue(!AppOriginPolicy.IsApplicationDocument("https://app.llmw.invalid.evil.example/"), "lookalike suffix host must be rejected.");
        AssertTrue(!AppOriginPolicy.IsApplicationDocument("https://app.llmw.invalid@evil.example/"), "userinfo lookalike must be rejected.");
        AssertTrue(!AppOriginPolicy.IsApplicationDocument("file:///C:/Windows/notepad.exe"), "file URI must be rejected.");
        AssertTrue(!AppOriginPolicy.IsApplicationDocument("javascript:alert(1)"), "javascript URI must be rejected.");
        AssertTrue(!AppOriginPolicy.IsApplicationDocument("data:text/html,hello"), "data URI must be rejected.");
        AssertTrue(!AppOriginPolicy.IsApplicationDocument("https://app.llmw.invalid:444/"), "non-default port must be rejected.");
        AssertTrue(!AppOriginPolicy.IsTrustedMessageSource(AppDocument, "https://evil.example/"), "current document mismatch must fail.");
        Console.WriteLine("WP15 origin tests passed.");
        return 12;
    }

    private static int Wp15NavigationAndResourceTests()
    {
        AssertTrue(NavigationPolicy.EvaluateTopLevel(AppDocument) == NavigationDecision.AllowApplication, "same-origin app navigation must be allowed.");
        AssertTrue(NavigationPolicy.EvaluateTopLevel("https://example.com/path") == NavigationDecision.CancelAndOfferExternal, "external https must leave WebView.");
        AssertTrue(NavigationPolicy.EvaluateTopLevel("http://example.com/") == NavigationDecision.CancelAndOfferExternal, "external http must leave WebView.");
        AssertTrue(NavigationPolicy.EvaluateTopLevel("file:///C:/secrets.txt") == NavigationDecision.Block, "file navigation must be blocked.");
        AssertTrue(NavigationPolicy.EvaluateTopLevel("data:text/html,x") == NavigationDecision.Block, "data navigation must be blocked.");
        AssertTrue(NavigationPolicy.EvaluateTopLevel("javascript:alert(1)") == NavigationDecision.Block, "javascript navigation must be blocked.");
        AssertTrue(NavigationPolicy.EvaluateFrame(AppDocument) == NavigationDecision.Block, "frame navigation must be blocked even for app origin.");
        AssertTrue(NavigationPolicy.EvaluateNewWindow("file:///C:/x") == NavigationDecision.Block, "file new-window must be blocked.");
        AssertTrue(NavigationPolicy.EvaluateNewWindow("https://example.com/") == NavigationDecision.CancelAndOfferExternal, "https new-window uses native flow.");
        AssertTrue(WebResourcePolicy.IsAllowed(AppDocument), "app index resource must be allowed.");
        AssertTrue(WebResourcePolicy.IsAllowed("https://app.llmw.invalid/bridge.js"), "app script resource must be allowed.");
        AssertTrue(WebResourcePolicy.IsAllowed("https://app.llmw.invalid/app.css"), "app css resource must be allowed.");
        AssertTrue(!WebResourcePolicy.IsAllowed("https://evil.example/x.js"), "external script fetch must be blocked.");
        AssertTrue(!WebResourcePolicy.IsAllowed("http://evil.example/"), "http fetch must be blocked.");
        AssertTrue(!WebResourcePolicy.IsAllowed("file:///C:/project/draft.md"), "file fetch must be blocked.");
        AssertTrue(!WebResourcePolicy.IsAllowed(@"X:\Project\Manuscript\chapter.md"), "project filesystem path must be blocked.");
        Console.WriteLine("WP15 navigation/resource tests passed.");
        return 16;
    }

    private static int Wp15ExternalUriTests()
    {
        AssertTrue(ExternalUriPolicy.TryValidate("https://example.com/path", out var https, out _), "https example must be accepted.");
        AssertEqual("https://example.com/path", https!.AbsoluteUri, "validated https URI changed.");
        AssertTrue(ExternalUriPolicy.TryValidate("http://example.com/", out _, out _), "http example must be accepted.");
        AssertTrue(!ExternalUriPolicy.TryValidate("file:///C:/Windows/notepad.exe", out _, out var fileCode), "file external target must be rejected.");
        AssertEqual(BridgeErrorCodes.ExternalUrlDenied, fileCode!, "file deny code changed.");
        AssertTrue(!ExternalUriPolicy.TryValidate("javascript:alert(1)", out _, out _), "javascript external target must be rejected.");
        AssertTrue(!ExternalUriPolicy.TryValidate("data:text/html,hi", out _, out _), "data external target must be rejected.");
        AssertTrue(!ExternalUriPolicy.TryValidate("https://user:password@example.com/", out _, out _), "embedded credentials must be rejected.");
        AssertTrue(!ExternalUriPolicy.TryValidate("https://example.com/\u0001", out _, out _), "control-character URL must be rejected.");
        AssertTrue(!ExternalUriPolicy.TryValidate("ms-appx://example", out _, out _), "custom scheme must be rejected.");
        AssertTrue(!ExternalUriPolicy.TryValidate(@"\\server\share", out _, out _), "UNC path must be rejected.");

        var launcher = new RecordingExternalBrowserLauncher();
        var allowed = ExternalLinkFlow.OpenAsync("https://example.com/path", new AlwaysAllowExternalLinkConsent(), launcher).GetAwaiter().GetResult();
        AssertTrue(allowed.AllowedByPolicy && allowed.Accepted, "allowing consent must launch.");
        AssertEqual(1, launcher.Opened.Count, "launcher must be invoked once.");
        var denied = ExternalLinkFlow.OpenAsync("https://example.com/path", new DenyExternalLinkConsent(), launcher).GetAwaiter().GetResult();
        AssertTrue(denied.AllowedByPolicy && !denied.Accepted, "denying consent must not launch.");
        AssertEqual(1, launcher.Opened.Count, "denied consent must not invoke the launcher again.");
        var blocked = ExternalLinkFlow.OpenAsync("file:///C:/x", new AlwaysAllowExternalLinkConsent(), launcher).GetAwaiter().GetResult();
        AssertTrue(!blocked.AllowedByPolicy, "invalid URI must not reach the launcher.");
        AssertEqual(1, launcher.Opened.Count, "invalid URI must not invoke the launcher.");
        Console.WriteLine("WP15 external URI tests passed.");
        return 15;
    }

    private static int Wp15BridgeContractTests()
    {
        var hello = BridgeOutboundJson.HostHello("session-hello", "msg-hello");
        AssertTrue(hello.Contains("\"semanticType\":\"host.hello\"", StringComparison.Ordinal), "host.hello golden type missing.");
        AssertTrue(hello.Contains("\"protocol\":\"llmw-web-bridge\"", StringComparison.Ordinal), "host.hello protocol missing.");
        AssertTrue(hello.Contains("\"appName\":\"LLMW.Writing\"", StringComparison.Ordinal), "host.hello metadata missing.");
        AssertTrue(!hello.Contains("BOOTSTRAP", StringComparison.OrdinalIgnoreCase), "host.hello leaked bootstrap material.");
        AssertTrue(!hello.Contains("token", StringComparison.OrdinalIgnoreCase), "host.hello leaked a token field.");

        var ready = BridgeEnvelopeParser.Parse(Envelope(BridgeSemanticTypes.RendererReady, "session-hello", "ready-1", "{\"shell\":\"wp15-static\"}"));
        AssertTrue(ready.Success, "valid renderer.ready must parse.");
        AssertEqual(BridgeSemanticTypes.RendererReady, ready.Message!.SemanticType, "renderer.ready type changed.");

        var ping = BridgeEnvelopeParser.Parse(Envelope(BridgeSemanticTypes.BridgePing, "session-hello", "ping-1", "{}"));
        AssertTrue(ping.Success, "valid ping must parse.");

        var ext = BridgeEnvelopeParser.Parse(Envelope(BridgeSemanticTypes.ExternalLinkRequest, "session-hello", "ext-1", "{\"uri\":\"https://example.com/path\"}"));
        AssertTrue(ext.Success, "valid externalLink.request must parse.");

        var pong = BridgeOutboundJson.BridgePong("session-hello", "pong-1", "ping-1", "n1");
        AssertTrue(pong.Contains("\"semanticType\":\"bridge.pong\"", StringComparison.Ordinal), "pong type missing.");

        AssertEqual(BridgeErrorCodes.ProtocolUnsupported, BridgeEnvelopeParser.Parse(Envelope(BridgeSemanticTypes.BridgePing, "s", "m", "{}", protocol: "nope")).Error!.Code, "wrong protocol.");
        AssertEqual(BridgeErrorCodes.ProtocolUnsupported, BridgeEnvelopeParser.Parse(VersionEnvelope(0)).Error!.Code, "version 0.");
        AssertEqual(BridgeErrorCodes.ProtocolUnsupported, BridgeEnvelopeParser.Parse(VersionEnvelope(2)).Error!.Code, "version 2.");
        AssertEqual(BridgeErrorCodes.InvalidSchema, BridgeEnvelopeParser.Parse("{\"protocol\":\"llmw-web-bridge\",\"version\":1,\"messageId\":\"m\",\"semanticType\":\"bridge.ping\",\"payload\":{}}").Error!.Code, "missing session.");
        AssertEqual(BridgeErrorCodes.InvalidSchema, BridgeEnvelopeParser.Parse("{\"protocol\":\"llmw-web-bridge\",\"version\":1,\"documentSessionId\":\"s\",\"semanticType\":\"bridge.ping\",\"payload\":{}}").Error!.Code, "missing message id.");
        AssertEqual(BridgeErrorCodes.UnknownMessageType, BridgeEnvelopeParser.Parse(Envelope("readFile", "s", "m", "{}")).Error!.Code, "unknown type.");
        AssertEqual(BridgeErrorCodes.UnknownMessageType, BridgeEnvelopeParser.Parse(Envelope("host.hello", "s", "m", "{}")).Error!.Code, "host.hello inbound.");
        AssertEqual(BridgeErrorCodes.InvalidSchema, BridgeEnvelopeParser.Parse(Envelope(BridgeSemanticTypes.BridgePing, "s", "m", "\"x\"")).Error!.Code, "wrong payload type.");
        AssertEqual(BridgeErrorCodes.InvalidSchema, BridgeEnvelopeParser.Parse("{\"method\":\"readFile\",\"args\":[\"C:\\\\x\"]}").Error!.Code, "generic proxy shape.");
        AssertEqual(BridgeErrorCodes.MalformedJson, BridgeEnvelopeParser.Parse("{").Error!.Code, "malformed JSON.");
        AssertEqual(BridgeErrorCodes.MessageTooLarge, BridgeEnvelopeParser.Parse(new string('x', BridgeProtocol.MaximumEnvelopeBytes + 1)).Error!.Code, "oversized JSON.");
        AssertEqual(BridgeErrorCodes.JsonTooDeep, BridgeEnvelopeParser.Parse(DeepJson(BridgeProtocol.MaximumJsonDepth + 2)).Error!.Code, "deep JSON.");
        foreach (var forbidden in new[] { "writeFile", "listDirectory", "runCommand", "shell", "git", "provider", "credential", "mcp", "core.invoke", "agent.invoke" })
        {
            AssertEqual(BridgeErrorCodes.UnknownMessageType, BridgeEnvelopeParser.Parse(Envelope(forbidden, "s", "m", "{}")).Error!.Code, forbidden);
        }

        Console.WriteLine("WP15 bridge contract tests passed.");
        return 24;
    }

    private static int Wp15SessionAndReplayTests()
    {
        var log = new RecordingBridgeLog();
        var processor = new BridgeMessageProcessor(log);
        var hello = processor.BeginDocumentSession();
        var session = JsonDocument.Parse(hello).RootElement.GetProperty("documentSessionId").GetString()!;
        var ready = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session, "ready-1", "{}")));
        AssertTrue(ready.Dispatched && processor.IsReady, "S1 ready must be accepted.");

        var ping = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session, "ping-1", "{}")));
        AssertTrue(ping.Dispatched, "S1 ping must be accepted.");
        AssertTrue(ping.OutboundJson[0].Contains("bridge.pong", StringComparison.Ordinal), "ping must pong.");

        processor.InvalidateSession();
        var stale = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session, "ping-2", "{}")));
        AssertEqual(BridgeErrorCodes.StaleSession, stale.Error!.Code, "navigation start must invalidate S1.");
        AssertTrue(!stale.Dispatched, "stale message must not dispatch.");

        var hello2 = processor.BeginDocumentSession();
        var session2 = JsonDocument.Parse(hello2).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(!string.Equals(session, session2, StringComparison.Ordinal), "reload must mint a new session.");
        var ready2 = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session2, "ready-2", "{}")));
        AssertTrue(ready2.Dispatched, "S2 ready must be accepted.");
        var ping2 = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session2, "ping-s2", "{}")));
        AssertTrue(ping2.Dispatched, "S2 ping must be accepted.");

        var firstExt = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.ExternalLinkRequest, session2, "ext-dup", "{\"uri\":\"https://example.com/path\"}")));
        AssertTrue(firstExt.Dispatched && firstExt.ExternalUri is not null, "first external link must dispatch.");
        var replay = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.ExternalLinkRequest, session2, "ext-dup", "{\"uri\":\"https://example.com/path\"}")));
        AssertEqual(BridgeErrorCodes.Replay, replay.Error!.Code, "duplicate messageId must replay-fail.");
        AssertTrue(replay.ExternalUri is null && !replay.Dispatched, "replay must not repeat the native side effect.");

        var extra = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session2, "extra-1", "{}"), additional: 2));
        AssertEqual(BridgeErrorCodes.AdditionalObjectsDenied, extra.Error!.Code, "AdditionalObjects must be denied.");
        AssertTrue(!extra.Dispatched && extra.OutboundJson.Count == 0, "AdditionalObjects must not dispatch or reply.");

        var wrongOrigin = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session2, "wo-1", "{}"), source: "https://evil.example/"));
        AssertEqual(BridgeErrorCodes.WrongOrigin, wrongOrigin.Error!.Code, "wrong origin must reject.");

        var notReady = new BridgeMessageProcessor();
        notReady.BeginDocumentSession();
        var early = notReady.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, notReady.DocumentSessionId!, "early-1", "{}")));
        AssertEqual(BridgeErrorCodes.NotReady, early.Error!.Code, "non-handshake commands before ready must fail.");

        var secret = "super-secret-payload-value";
        var malformed = processor.ProcessIncoming(Msg("{\"not\":\"json\"" + secret));
        AssertEqual(BridgeErrorCodes.MalformedJson, malformed.Error!.Code, "malformed JSON after origin check.");
        AssertTrue(!log.Entries.Exists(entry => entry.Contains(secret, StringComparison.Ordinal)), "logs must not contain untrusted payload.");

        Console.WriteLine("WP15 session/replay tests passed.");
        return 14;
    }

    private static int Wp15SecuritySurfaceTests()
    {
        var release = WebViewSecuritySettings.Release;
        AssertTrue(!release.AreHostObjectsAllowed, "host objects must be disabled.");
        AssertTrue(!release.AreDevToolsEnabled, "release DevTools must be disabled.");
        AssertTrue(!release.AreDefaultContextMenusEnabled, "context menus must be disabled.");
        AssertTrue(!release.IsGeneralAutofillEnabled, "autofill must be off.");
        AssertTrue(!release.IsPasswordAutosaveEnabled, "password autosave must be off.");
        AssertTrue(release.IsWebMessageEnabled, "WebMessage must remain enabled.");
        AssertTrue(!release.AreDefaultScriptDialogsEnabled, "script dialogs must be off.");
#if DEBUG
        AssertTrue(WebViewSecuritySettings.ForCurrentBuild.AreDevToolsEnabled, "debug DevTools may be enabled.");
#else
        AssertTrue(!WebViewSecuritySettings.ForCurrentBuild.AreDevToolsEnabled, "release build DevTools must stay off.");
#endif

        var udf = WebViewUserDataFolder.Resolve();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AssertTrue(udf.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase), "UDF must be under LocalAppData.");
        AssertTrue(udf.Contains(Path.Combine("LLMW.Writing", "WebView2"), StringComparison.OrdinalIgnoreCase), "UDF path changed.");
        AssertTrue(!udf.Contains("Project", StringComparison.OrdinalIgnoreCase), "UDF must not use Project.");
        AssertTrue(!udf.Contains(".llmw", StringComparison.OrdinalIgnoreCase), "UDF must not use .llmw.");

        var assets = RendererAssetLayout.FromApplicationBase(AppContext.BaseDirectory);
        AssertTrue(assets.Exists, "WP15 static renderer assets must copy to output.");
        AssertTrue(assets.DirectoryPath.Contains("web-editor", StringComparison.OrdinalIgnoreCase), "assets must be application web-editor files.");

        var root = FindRepoRoot();
        var hostPath = Path.Combine(root, "src", "LLMW.Writing.UI", "Hosting", "WebViewRuntimeHost.cs");
        var hostText = File.ReadAllText(hostPath);
        AssertTrue(!hostText.Contains("AddHostObjectToScript", StringComparison.Ordinal), "AddHostObjectToScript must not exist.");
        AssertTrue(!hostText.Contains("AddScriptToExecuteOnDocumentCreated", StringComparison.Ordinal), "privileged script injection must not exist.");
        AssertTrue(hostText.Contains("AreHostObjectsAllowed", StringComparison.Ordinal), "host object setting must be applied.");
        AssertTrue(hostText.Contains("DenyCors", StringComparison.Ordinal), "virtual host mapping must deny CORS.");
        AssertTrue(hostText.Contains("CoreWebView2WebResourceRequestSourceKinds", StringComparison.Ordinal), "resource filter must include request source kinds.");
        AssertTrue(hostText.Contains("AdditionalObjects", StringComparison.Ordinal), "AdditionalObjects must be inspected.");
        AssertTrue(!hostText.Contains("LLMW_UI_BOOTSTRAP_TOKEN", StringComparison.Ordinal), "bootstrap token must not enter the WebView host.");
        AssertTrue(hostText.Contains("ApplyPreNavigationHardeningAndNavigateAsync", StringComparison.Ordinal), "pre-navigation hardening sequence must be extracted.");
        AssertTrue(hostText.Contains("WebViewProcessRecoveryPolicy.Evaluate", StringComparison.Ordinal), "ProcessFailed must use the recovery policy.");
        AssertTrue(hostText.Contains("RecreateRenderer", StringComparison.Ordinal), "Browser process recovery must recreate the WebView control.");
        AssertTrue(hostText.Contains("IsUserInitiated", StringComparison.Ordinal), "native consent must require trusted user-initiated facts.");
        AssertTrue(hostText.Contains("ExternalLinkCoordinator", StringComparison.Ordinal), "external links must use the single-flight coordinator.");
        AssertTrue(hostText.Contains("ExternalLinkBridgeReply.Busy", StringComparison.Ordinal), "busy external-link replies must be typed.");
        AssertTrue(!hostText.Contains("replyTo: null", StringComparison.Ordinal), "request/response replies must not use a null replyTo.");

        var index = File.ReadAllText(Path.Combine(root, "src", "web-editor", "app", "index.html"));
        var bridge = File.ReadAllText(Path.Combine(root, "src", "web-editor", "app", "bridge.js"));
        AssertTrue(index.Contains("default-src 'none'", StringComparison.Ordinal), "CSP default-src missing.");
        AssertTrue(index.Contains("connect-src 'none'", StringComparison.Ordinal), "CSP connect-src missing.");
        AssertTrue(!index.Contains("unsafe-eval", StringComparison.Ordinal), "CSP must not allow unsafe-eval.");
        AssertTrue(!index.Contains("unsafe-inline", StringComparison.Ordinal), "CSP must not allow unsafe-inline.");
        AssertTrue(!index.Contains("<script>", StringComparison.Ordinal), "index.html must not use inline script.");
        AssertTrue(bridge.Contains("textContent", StringComparison.Ordinal), "project sample must use textContent.");
        AssertTrue(!bridge.Contains("innerHTML", StringComparison.Ordinal), "renderer must not assign innerHTML.");
        AssertTrue(!bridge.Contains("insertAdjacentHTML", StringComparison.Ordinal), "renderer must not use insertAdjacentHTML.");
        AssertTrue(bridge.Contains("semanticType: \"externalLink.request\"", StringComparison.Ordinal), "project-shaped sample missing.");
        AssertTrue(!bridge.Contains("LLMW_UI_BOOTSTRAP_TOKEN", StringComparison.Ordinal), "renderer must not receive bootstrap secrets.");
        AssertTrue(!bridge.Contains("chrome.webview.hostObjects", StringComparison.Ordinal), "host objects must be unused.");

        var bootstrap = File.ReadAllText(Path.Combine(root, "src", "LLMW.Writing.UI", "ProcessBootstrapper.cs"));
        AssertTrue(bootstrap.Contains("LLMW_UI_BOOTSTRAP_TOKEN", StringComparison.Ordinal), "ProcessBootstrapper must remain native-only.");
        var app = File.ReadAllText(Path.Combine(root, "src", "LLMW.Writing.UI", "App.xaml.cs"));
        AssertTrue(!app.Contains("ProcessBootstrapper", StringComparison.Ordinal), "WP15 must not start Core from App.");

        Console.WriteLine("WP15 security surface tests passed.");
        return 36;
    }

    private static int Wp15CorrectiveLifecycleTests()
    {
        var lifecycle = new NavigationSessionLifecycle();
        var processor = new BridgeMessageProcessor();
        var hello1 = processor.BeginDocumentSession();
        var session1 = JsonDocument.Parse(hello1).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session1, "ready-s1", "{}"))).Dispatched, "S1 ready must be accepted.");

        processor.InvalidateSession();
        lifecycle.NoteStarting(41, hostCancelled: true);
        var staleS1 = processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session1, "ping-after-cancel", "{}")));
        AssertEqual(BridgeErrorCodes.StaleSession, staleS1.Error!.Code, "cancelled navigation must invalidate S1 immediately.");

        var cancelledAction = lifecycle.NoteCompleted(41, isSuccess: false, isOperationCanceled: true, currentSourceIsApplicationDocument: true);
        AssertTrue(cancelledAction == NavigationCompletionAction.BeginNewSession, "host-cancelled navigation that kept the app document must mint a new session.");
        var hello2 = processor.BeginDocumentSession();
        var session2 = JsonDocument.Parse(hello2).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(!string.Equals(session1, session2, StringComparison.Ordinal), "S2 must not resurrect S1.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session2, "ready-s2", "{}"))).Dispatched, "S2 renderer.ready must be accepted.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session2, "ping-s2", "{}"))).Dispatched, "S2 ping must be accepted.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session1, "ping-s1-after-s2", "{}"))).Error!.Code, "S1 must remain permanently stale.");

        var failedLifecycle = new NavigationSessionLifecycle();
        failedLifecycle.NoteStarting(42, hostCancelled: false);
        var failedAction = failedLifecycle.NoteCompleted(42, isSuccess: false, isOperationCanceled: false, currentSourceIsApplicationDocument: true);
        AssertTrue(failedAction == NavigationCompletionAction.ShowNativeFailure, "genuine application navigation failure must remain a native error.");
        AssertTrue(failedAction != NavigationCompletionAction.BeginNewSession, "genuine application navigation failure must not re-handshake.");

        var coordinator = new ExternalLinkCoordinator();
        var launcher = new RecordingExternalBrowserLauncher();
        AssertTrue(ExternalUriPolicy.TryValidate("https://example.com/one", out var firstUri, out _), "first external URI must validate.");
        AssertTrue(ExternalUriPolicy.TryValidate("https://example.com/two", out var secondUri, out _), "second external URI must validate.");
        var first = new PendingExternalLink
        {
            Source = ExternalLinkSource.BridgeRequest,
            DocumentSessionId = session2,
            RequestMessageId = "ext-first",
            Uri = firstUri!
        };
        var second = new PendingExternalLink
        {
            Source = ExternalLinkSource.BridgeRequest,
            DocumentSessionId = session2,
            RequestMessageId = "ext-second",
            Uri = secondUri!
        };
        AssertTrue(coordinator.TryAdmit(first) == ExternalLinkAdmitResult.Admitted, "first external confirmation must be admitted.");
        AssertTrue(coordinator.TryAdmit(second) == ExternalLinkAdmitResult.Busy, "second concurrent external confirmation must be busy.");
        AssertTrue(coordinator.HasPending, "busy rejection must not queue a second pending confirmation.");
        var busyJson = ExternalLinkBridgeReply.Busy(session2, "ext-second");
        AssertTrue(busyJson.Contains("\"code\":\"EXTERNAL_LINK_BUSY\"", StringComparison.Ordinal), "busy reply must use EXTERNAL_LINK_BUSY.");
        AssertTrue(busyJson.Contains("\"replyTo\":\"ext-second\"", StringComparison.Ordinal), "busy reply must correlate to the blocked request.");
        var launched = coordinator.Complete(first, userAccepted: true, session2, sessionReady: true, currentSourceIsApplicationDocument: true, launcher);
        AssertTrue(launched == ExternalLinkLaunchResult.Launched, "accepted current-session confirmation must launch.");
        AssertEqual(1, launcher.Opened.Count, "single-flight must not start a second ProcessStart.");
        AssertEqual("https://example.com/one", launcher.Opened[0], "only the admitted URI may launch.");

        var decision = NavigationPolicy.EvaluateTopLevel("https://evil.example/scripted?q=1");
        for (var i = 0; i < 32; i++)
        {
            AssertTrue(!ExternalNavigationIntent.MayOfferNativeDialog(decision, isUserInitiated: false), "script-driven external navigation must not offer native consent.");
        }

        AssertTrue(ExternalNavigationIntent.MayOfferNativeDialog(decision, isUserInitiated: true), "user-initiated external navigation may offer native consent.");

        var staleCoordinator = new ExternalLinkCoordinator();
        var staleLauncher = new RecordingExternalBrowserLauncher();
        var pendingS1 = new PendingExternalLink
        {
            Source = ExternalLinkSource.BridgeRequest,
            DocumentSessionId = session1,
            RequestMessageId = "ext-stale-s1",
            Uri = firstUri!
        };
        AssertTrue(staleCoordinator.TryAdmit(pendingS1) == ExternalLinkAdmitResult.Admitted, "S1 confirmation must admit before reload.");
        var staleLaunch = staleCoordinator.Complete(
            pendingS1,
            userAccepted: true,
            currentSessionId: session2,
            sessionReady: true,
            currentSourceIsApplicationDocument: true,
            staleLauncher);
        AssertTrue(staleLaunch == ExternalLinkLaunchResult.StaleSession, "reload to S2 must cancel the S1 side effect.");
        AssertEqual(0, staleLauncher.Opened.Count, "S1 URI must never launch after the session is replaced.");

        var ack = BridgeMessageProcessor.CompleteExternalLink(session2, "ext-first", accepted: true);
        AssertTrue(ack.Contains("\"semanticType\":\"bridge.ack\"", StringComparison.Ordinal), "external completion must be bridge.ack.");
        AssertTrue(ack.Contains("\"replyTo\":\"ext-first\"", StringComparison.Ordinal), "bridge.ack replyTo must equal the original request MessageId.");
        AssertTrue(!ack.Contains("\"replyTo\":null", StringComparison.Ordinal), "bridge.ack must not emit replyTo null.");

        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.BrowserProcessExited, 0) == WebViewProcessRecoveryAction.RecreateControl, "BrowserProcessExited must recreate the control.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.RenderProcessExited, 0) == WebViewProcessRecoveryAction.ReloadApplicationDocument, "RenderProcessExited must reload the application document.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.GpuProcessExited, 0) == WebViewProcessRecoveryAction.IgnoreAutoRecovered, "GPU process exit must not force navigation/recreation.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.UtilityProcessExited, 0) == WebViewProcessRecoveryAction.IgnoreAutoRecovered, "utility process exit must not force navigation/recreation.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.SandboxIdleProcessExited, 0) == WebViewProcessRecoveryAction.IgnoreAutoRecovered, "sandbox idle exit must not force navigation/recreation.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.FrameRenderProcessExited, 0) == WebViewProcessRecoveryAction.FailClosedNoNavigate, "unexpected frame process failure must fail closed.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.RenderProcessUnresponsive, 0) == WebViewProcessRecoveryAction.ReloadApplicationDocument, "first unresponsive recovery may reload.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.RenderProcessUnresponsive, 1) == WebViewProcessRecoveryAction.FailClosedNoNavigate, "repeated unresponsive recovery must not loop.");

        var hostPath = Path.Combine(FindRepoRoot(), "src", "LLMW.Writing.UI", "Hosting", "WebViewRuntimeHost.cs");
        var hostText = File.ReadAllText(hostPath);
        AssertTrue(hostText.Contains("ApplyPreNavigationHardeningAndNavigateAsync", StringComparison.Ordinal)
            && hostText.Contains("RecreateControlAsync", StringComparison.Ordinal), "recreate and hardening methods must exist.");
        AssertTrue(hostText.Contains("await ApplyPreNavigationHardeningAndNavigateAsync(_site.Renderer)", StringComparison.Ordinal)
            || hostText.Contains("await ApplyPreNavigationHardeningAndNavigateAsync(replacement)", StringComparison.Ordinal), "recreated WebView must reuse the pre-navigation hardening sequence.");
        AssertTrue(hostText.Contains("case WebViewProcessRecoveryAction.RecreateControl:", StringComparison.Ordinal), "BrowserProcessExited recovery must dispatch RecreateControl.");
        AssertTrue(!hostText.Contains("core.Navigate", StringComparison.Ordinal) || hostText.Contains("ApplyPreNavigationHardeningAndNavigateAsync", StringComparison.Ordinal), "Navigate must remain inside the hardening/reload paths.");

        var log = new RecordingBridgeLog();
        log.Write(BridgeErrorCodes.NavigationBlocked, "navigation", "mid", "sid", "https://evil.example:8443/secret/path?token=abc#frag");
        AssertEqual(1, log.Entries.Count, "blocked navigation must emit one sanitized log entry.");
        AssertTrue(log.Entries[0].Contains("https://evil.example:8443", StringComparison.Ordinal), "safe log must keep scheme/host/port.");
        AssertTrue(!log.Entries[0].Contains("/secret/path", StringComparison.Ordinal), "safe log must not emit the URI path.");
        AssertTrue(!log.Entries[0].Contains("token=abc", StringComparison.Ordinal), "safe log must not emit the URI query.");
        AssertTrue(!log.Entries[0].Contains("#frag", StringComparison.Ordinal), "safe log must not emit the URI fragment.");
        AssertEqual("https://evil.example", SafeOriginLog.Describe("https://user:pass@evil.example/hidden"), "safe log must not emit userinfo.");
        AssertTrue(!SafeOriginLog.Describe("https://user:pass@evil.example/hidden").Contains("user:pass", StringComparison.Ordinal), "userinfo must be stripped.");

        Console.WriteLine("WP15 corrective lifecycle tests passed.");
        return 32;
    }

    private static IncomingWebMessage Msg(string json, string? source = AppDocument, string? current = AppDocument, int additional = 0)
        => new()
        {
            Source = source,
            CurrentDocument = current ?? source,
            Json = json,
            AdditionalObjectCount = additional
        };

    private static string Envelope(string semanticType, string session, string messageId, string payload, string protocol = BridgeProtocol.Name)
        => "{\"protocol\":\"" + protocol + "\",\"version\":1,\"documentSessionId\":\"" + session + "\",\"messageId\":\"" + messageId + "\",\"semanticType\":\"" + semanticType + "\",\"payload\":" + payload + "}";

    private static string VersionEnvelope(int version)
        => "{\"protocol\":\"llmw-web-bridge\",\"version\":" + version + ",\"documentSessionId\":\"s\",\"messageId\":\"m\",\"semanticType\":\"bridge.ping\",\"payload\":{}}";

    private static string DeepJson(int depth)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            builder.Append("{\"a\":");
        }

        builder.Append('1');
        for (var i = 0; i < depth; i++)
        {
            builder.Append('}');
        }

        return builder.ToString();
    }

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

        throw new InvalidOperationException("Repository root was not found from the test output directory.");
    }

    internal static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void AssertEqual<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }
}

internal sealed class RecordingExternalBrowserLauncher : IExternalBrowserLauncher
{
    public List<string> Opened { get; } = [];

    public void Open(ValidatedExternalUri uri)
    {
        Opened.Add(uri.AbsoluteUri);
    }
}

internal sealed class DenyExternalLinkConsent : IExternalLinkConsent
{
    public Task<bool> ConfirmAsync(ValidatedExternalUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Task.FromResult(false);
    }
}
