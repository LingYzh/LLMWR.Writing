using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using LLMW.Writing.UI.Hosting;
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
            count += Wp15CorrectivePass2Tests();
            count += Wp15CorrectivePass3Tests();
            count += Wp15CorrectivePass4Tests();
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
        AssertTrue(hostText.Contains("SourceChanged", StringComparison.Ordinal), "SourceChanged must be subscribed before navigation.");
        AssertTrue(bridge.Contains("msg.documentSessionId !== sessionId", StringComparison.Ordinal), "renderer must ignore stale host sessions.");

        var bootstrap = File.ReadAllText(Path.Combine(root, "src", "LLMW.Writing.UI", "ProcessBootstrapper.cs"));
        AssertTrue(bootstrap.Contains("LLMW_UI_BOOTSTRAP_TOKEN", StringComparison.Ordinal), "ProcessBootstrapper must remain native-only.");
        var app = File.ReadAllText(Path.Combine(root, "src", "LLMW.Writing.UI", "App.xaml.cs"));
        AssertTrue(!app.Contains("ProcessBootstrapper", StringComparison.Ordinal), "WP15 must not start Core from App.");

        Console.WriteLine("WP15 security surface tests passed.");
        return 38;
    }

    private static int Wp15CorrectiveLifecycleTests()
    {
        var lifecycle = new NavigationSessionLifecycle();
        var processor = new BridgeMessageProcessor();
        var hello1 = processor.BeginDocumentSession();
        var session1 = JsonDocument.Parse(hello1).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session1, "ready-s1", "{}"))).Dispatched, "S1 ready must be accepted.");

        processor.InvalidateSession();
        lifecycle.NoteStarting(41, hostCancelled: true, isAllowedApplicationNavigation: false);
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
        failedLifecycle.NoteStarting(42, hostCancelled: false, isAllowedApplicationNavigation: true);
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
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.GpuProcessExited, 0) == WebViewProcessRecoveryAction.ObserveKeepSession, "GPU process exit must not force navigation/recreation.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.UtilityProcessExited, 0) == WebViewProcessRecoveryAction.ObserveKeepSession, "utility process exit must not force navigation/recreation.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.SandboxHelperProcessExited, 0) == WebViewProcessRecoveryAction.ObserveKeepSession, "sandbox helper exit must not force navigation/recreation.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.FrameRenderProcessExited, 0) == WebViewProcessRecoveryAction.FailClosedNoNavigate, "unexpected frame process failure must fail closed.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.RenderProcessUnresponsive, 0) == WebViewProcessRecoveryAction.ReloadApplicationDocument, "first unresponsive recovery may reload.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(WebViewProcessFailedKind.RenderProcessUnresponsive, 1) == WebViewProcessRecoveryAction.FailClosedNoNavigate, "repeated unresponsive recovery must not loop.");

        var hostPath = Path.Combine(FindRepoRoot(), "src", "LLMW.Writing.UI", "Hosting", "WebViewRuntimeHost.cs");
        var hostText = File.ReadAllText(hostPath);
        AssertTrue(hostText.Contains("ApplyPreNavigationHardeningAndNavigateAsync", StringComparison.Ordinal)
            && hostText.Contains("RecreateControlAsync", StringComparison.Ordinal), "recreate and hardening methods must exist.");
        AssertTrue(hostText.Contains("await ApplyPreNavigationHardeningAndNavigateAsync(_site.Renderer)", StringComparison.Ordinal)
            || hostText.Contains("await ApplyPreNavigationHardeningAndNavigateAsync(replacement)", StringComparison.Ordinal)
            || hostText.Contains("await ApplyPreNavigationHardeningAndNavigateAsync(replacement, allocated)", StringComparison.Ordinal), "recreated WebView must reuse the pre-navigation hardening sequence.");
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

    private static int Wp15CorrectivePass2Tests()
    {
        var overlap = new NavigationSessionLifecycle();
        AssertTrue(overlap.NoteStarting(1, hostCancelled: true, isAllowedApplicationNavigation: false) == NavigationTrackResult.Tracked, "N1 must be tracked.");
        AssertTrue(overlap.NoteStarting(2, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "N2 must not overwrite N1.");
        AssertTrue(overlap.TryGet(1, out var n1) && n1.HostCancelled, "N1 must remain host-cancelled.");
        AssertTrue(overlap.TryGet(2, out var n2) && n2.IsAllowedApplicationNavigation, "N2 must remain the application navigation.");
        AssertEqual(2, overlap.ActiveCount, "overlapping navigations must both remain active.");
        var n1Done = overlap.NoteCompleted(1, isSuccess: false, isOperationCanceled: true, currentSourceIsApplicationDocument: true);
        AssertTrue(n1Done == NavigationCompletionAction.None, "N1 cancel completion must not report NAVIGATION_FAILED.");
        AssertTrue(n1Done != NavigationCompletionAction.ShowNativeFailure, "N1 cancel completion must not destroy the overlapping load.");
        AssertTrue(overlap.TryGet(2, out _), "completing N1 must not clear N2.");
        AssertTrue(!overlap.TryGet(1, out _), "N1 completion must remove only N1.");
        var n2Done = overlap.NoteCompleted(2, isSuccess: true, isOperationCanceled: false, currentSourceIsApplicationDocument: true);
        AssertTrue(n2Done == NavigationCompletionAction.BeginNewSession, "N2 success must own the fresh session handshake.");

        var dualCancel = new NavigationSessionLifecycle();
        dualCancel.NoteStarting(11, hostCancelled: true, isAllowedApplicationNavigation: false);
        dualCancel.NoteStarting(12, hostCancelled: true, isAllowedApplicationNavigation: false);
        AssertTrue(dualCancel.TryGet(11, out var c1) && c1.HostCancelled, "N1 cancel identity must be retained.");
        AssertTrue(dualCancel.TryGet(12, out var c2) && c2.HostCancelled, "N2 cancel identity must be retained.");
        AssertTrue(dualCancel.NoteCompleted(11, false, true, true) == NavigationCompletionAction.None, "first cancelled completion must not handshake while N2 is active.");
        AssertTrue(dualCancel.TryGet(12, out var c2After) && c2After.HostCancelled, "N2 cancellation identity must survive N1 completion.");
        AssertTrue(dualCancel.NoteCompleted(12, false, true, true) == NavigationCompletionAction.BeginNewSession, "final host-cancelled completion restores a fresh session.");

        var redirect = new NavigationSessionLifecycle();
        AssertTrue(redirect.NoteStarting(7, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "redirect start must track once.");
        AssertTrue(redirect.NoteStarting(7, hostCancelled: true, isAllowedApplicationNavigation: false) == NavigationTrackResult.UpdatedRedirect, "redirect must update the same NavigationId.");
        AssertEqual(1, redirect.ActiveCount, "redirects must not create duplicate NavigationId records.");
        AssertTrue(redirect.TryGet(7, out var redirected) && redirected.HostCancelled && !redirected.IsAllowedApplicationNavigation, "a cancelled redirect hop must keep the navigation host-cancelled.");
        AssertTrue(redirect.NoteCompleted(7, false, true, true) == NavigationCompletionAction.BeginNewSession, "host-cancelled redirect that kept the app document must restore a session.");

        var unknown = new NavigationSessionLifecycle();
        unknown.NoteStarting(21, hostCancelled: true, isAllowedApplicationNavigation: false);
        unknown.NoteStarting(22, hostCancelled: false, isAllowedApplicationNavigation: true);
        AssertTrue(unknown.NoteCompleted(99, false, true, true) == NavigationCompletionAction.IgnoreUnknown, "unknown completion must not be treated as a tracked navigation.");
        AssertEqual(2, unknown.ActiveCount, "unknown completion must not clear another active navigation.");

        var bounded = new NavigationSessionLifecycle();
        for (ulong id = 1; id <= NavigationSessionLifecycle.MaximumActiveNavigations; id++)
        {
            AssertTrue(bounded.NoteStarting(id, hostCancelled: true, isAllowedApplicationNavigation: false) == NavigationTrackResult.Tracked, "active navigations within the bound must track.");
        }

        AssertTrue(bounded.NoteStarting(100, hostCancelled: true, isAllowedApplicationNavigation: false) == NavigationTrackResult.Overflow, "tracker overflow must fail closed.");
        AssertEqual(NavigationSessionLifecycle.MaximumActiveNavigations, bounded.ActiveCount, "overflow must not grow the tracker.");
        AssertTrue(bounded.TryGet(1, out _), "overflow must not overwrite the oldest NavigationId.");

        var processor = new BridgeMessageProcessor();
        var hello1 = processor.BeginDocumentSession();
        var session1 = JsonDocument.Parse(hello1).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session1, "ready-frag", "{}"))).Dispatched, "S1 ready before fragment must be accepted.");
        AssertTrue(SameDocumentSessionPolicy.Evaluate(isNewDocument: false, currentSourceIsApplicationDocument: false) == SameDocumentSourceChangeAction.InvalidateOnly, "fragment SourceChanged must invalidate without resurrecting S1.");
        processor.InvalidateSession();
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session1, "ping-frag", "{}"))).Error!.Code, "S1 must be stale after fragment SourceChanged.");
        AssertTrue(SameDocumentSessionPolicy.Evaluate(isNewDocument: false, currentSourceIsApplicationDocument: true) == SameDocumentSourceChangeAction.BeginNewSession, "return to the exact app document must mint S2.");
        var hello2 = processor.BeginDocumentSession();
        var session2 = JsonDocument.Parse(hello2).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(!string.Equals(session1, session2, StringComparison.Ordinal), "returning to the exact app Source must not resurrect S1.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session2, "ready-s2-frag", "{}"))).Dispatched, "S2 renderer.ready must be accepted.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session2, "ping-s2-frag", "{}"))).Dispatched, "S2 ping must be accepted.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session1, "ping-s1-after-frag", "{}"))).Error!.Code, "old S1 ping must remain rejected.");

        AssertTrue(SameDocumentSessionPolicy.Evaluate(isNewDocument: false, currentSourceIsApplicationDocument: true) == SameDocumentSourceChangeAction.BeginNewSession, "history-style same-document SourceChanged must be a session event.");
        AssertTrue(SameDocumentSessionPolicy.Evaluate(isNewDocument: true, currentSourceIsApplicationDocument: true) == SameDocumentSourceChangeAction.InvalidateOnly, "new-document SourceChanged must invalidate without handshake.");

        var pending = new PendingExternalLink
        {
            Source = ExternalLinkSource.BridgeRequest,
            DocumentSessionId = session1,
            RequestMessageId = "ext-late-s1",
            Uri = ValidateUri("https://example.com/stale")
        };
        var lateCoordinator = new ExternalLinkCoordinator();
        var lateLauncher = new RecordingExternalBrowserLauncher();
        AssertTrue(lateCoordinator.TryAdmit(pending) == ExternalLinkAdmitResult.Admitted, "S1 external confirmation must admit.");
        var late = lateCoordinator.Complete(pending, true, session2, true, true, lateLauncher);
        AssertTrue(late == ExternalLinkLaunchResult.StaleSession, "late S1 consent must not launch.");
        AssertEqual(0, lateLauncher.Opened.Count, "late S1 URI must never ProcessStart.");
        var lateJson = ExternalLinkBridgeReply.FromLaunch(pending, late);
        AssertTrue(!HostToRendererDelivery.ShouldPost(lateJson, session2), "late S1 replies must not be delivered to S2.");
        AssertTrue(HostToRendererDelivery.ShouldPost(hello2, session2), "host.hello for S2 must be delivered.");
        var s1Pong = BridgeOutboundJson.BridgePong(session1, "pong-late", "ping-late", "n");
        AssertTrue(!HostToRendererDelivery.ShouldPost(s1Pong, session2), "late S1 pong must not mutate S2.");
        var s1Ack = BridgeOutboundJson.BridgeAck(session1, "ack-late", "ext-late-s1", true);
        AssertTrue(!HostToRendererDelivery.ShouldPost(s1Ack, session2), "late S1 ack must not mutate S2.");
        var s1Err = BridgeOutboundJson.BridgeError(session1, "err-late", "ext-late-s1", new BridgeError(BridgeErrorCodes.StaleSession, "stale"));
        AssertTrue(!HostToRendererDelivery.ShouldPost(s1Err, session2), "late S1 error must not mutate S2.");
        var s2Status = BridgeOutboundJson.HostStatusReady(session2, "status-s2");
        AssertTrue(HostToRendererDelivery.ShouldPost(s2Status, session2), "S2 host.status must remain deliverable.");

        AssertTrue(!WebViewProcessRecoveryPolicy.LosesRendererDocument(WebViewProcessFailedKind.SandboxHelperProcessExited), "sandbox helper exit must not imply document loss.");
        MapAndKeepSession(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited, WebViewProcessFailedKind.SandboxHelperProcessExited);
        MapAndKeepSession(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited, WebViewProcessFailedKind.PpapiPluginProcessExited);
        MapAndKeepSession(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited, WebViewProcessFailedKind.PpapiBrokerProcessExited);
        MapAndKeepSession(CoreWebView2ProcessFailedKind.UnknownProcessExited, WebViewProcessFailedKind.UnknownProcessExited);
        MapAndKeepSession(CoreWebView2ProcessFailedKind.GpuProcessExited, WebViewProcessFailedKind.GpuProcessExited);
        MapAndKeepSession(CoreWebView2ProcessFailedKind.UtilityProcessExited, WebViewProcessFailedKind.UtilityProcessExited);

        var mappedUnknown = WebViewProcessFailedKindMapper.Map(CoreWebView2ProcessFailedKind.UnknownProcessExited);
        AssertTrue(mappedUnknown != WebViewProcessFailedKind.BrowserProcessExited, "UnknownProcessExited must not inherit BrowserProcessExited.");
        AssertTrue(mappedUnknown != WebViewProcessFailedKind.RenderProcessExited, "UnknownProcessExited must not inherit RenderProcessExited.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(mappedUnknown, 0) == WebViewProcessRecoveryAction.ObserveKeepSession, "UnknownProcessExited must observe without reload/recreate.");

        var live = new BridgeMessageProcessor();
        var liveHello = live.BeginDocumentSession();
        var liveSession = JsonDocument.Parse(liveHello).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(live.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, liveSession, "ready-live", "{}"))).Dispatched, "S1 must be READY before nonfatal process mapping.");
        var helperKind = WebViewProcessFailedKindMapper.Map(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited);
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(helperKind, 0) == WebViewProcessRecoveryAction.ObserveKeepSession, "nonfatal helper exit must not reload/recreate.");
        AssertTrue(!WebViewProcessRecoveryPolicy.LosesRendererDocument(helperKind), "nonfatal helper exit must keep the document session.");
        AssertTrue(live.IsReady, "S1 must remain valid across nonfatal process exit.");
        AssertTrue(live.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, liveSession, "ping-live", "{}"))).Dispatched, "S1 ping must still be accepted after nonfatal process exit.");

        var mapperText = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "LLMW.Writing.UI", "Hosting", "WebViewProcessFailedKindMapper.cs"));
        AssertTrue(mapperText.Contains("SandboxHelperProcessExited", StringComparison.Ordinal), "mapper must bind SandboxHelperProcessExited.");
        AssertTrue(!mapperText.Contains("SandboxIdleProcessExited", StringComparison.Ordinal), "mapper must not use a surrogate SandboxIdle kind.");
        AssertTrue(!File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "LLMW.Writing.UI", "WebView", "WebViewProcessRecoveryPolicy.cs")).Contains("SandboxIdleProcessExited", StringComparison.Ordinal), "policy must not test a surrogate process kind.");

        Console.WriteLine("WP15 corrective pass 2 tests passed.");
        return 48;
    }

    private static int Wp15CorrectivePass3Tests()
    {
        var processor = new BridgeMessageProcessor();
        var hello0 = processor.BeginDocumentSession();
        var session0 = JsonDocument.Parse(hello0).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session0, "ready-s0", "{}"))).Dispatched, "S0 ready must establish READY.");
        AssertTrue(processor.IsReady, "S0 must be READY before overlapping navigations.");

        var timeline = new NavigationSessionLifecycle();
        processor.InvalidateSession();
        AssertTrue(timeline.NoteStarting(1, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "N1 application NavigationStarting must track.");
        AssertTrue(timeline.TryGet(1, out var n1Start) && n1Start.StartSequence == 1 && n1Start.CanReplaceTopLevelDocument, "N1 must store startSequence 1.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session0, "ping-n1-start", "{}"))).Error!.Code, "N1 NavigationStarting must stale S0.");

        processor.InvalidateSession();
        AssertTrue(timeline.NoteStarting(2, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "N2 application NavigationStarting must track beside N1.");
        AssertTrue(timeline.TryGet(2, out var n2Start) && n2Start.StartSequence == 2 && n2Start.CanReplaceTopLevelDocument, "N2 must store startSequence 2.");
        AssertTrue(timeline.LatestStartSequence == 2, "latest startSequence must be the later-started navigation.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session0, "ping-n2-start", "{}"))).Error!.Code, "N2 NavigationStarting must keep S0 stale.");

        AssertTrue(timeline.NoteCompleted(1, isSuccess: true, isOperationCanceled: false, currentSourceIsApplicationDocument: true) == NavigationCompletionAction.None, "N1 success must not handshake while later N2 is active.");
        AssertTrue(processor.DocumentSessionId is null, "N1 success must not mint a usable session while N2 remains active.");
        AssertTrue(timeline.TryGet(2, out _), "N1 completion must leave N2 active.");

        AssertTrue(SameDocumentSessionPolicy.Evaluate(isNewDocument: true, currentSourceIsApplicationDocument: true) == SameDocumentSourceChangeAction.InvalidateOnly, "N2 SourceChanged(IsNewDocument=true) must not handshake.");
        processor.InvalidateSession();
        AssertTrue(processor.DocumentSessionId is null, "new-document SourceChanged must not create a replacement session.");
        AssertTrue(!processor.IsReady, "new-document SourceChanged must not mark READY.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session0, "ping-n2-sourcechanged", "{}"))).Error!.Code, "S0 must remain rejected after new-document SourceChanged.");

        AssertTrue(timeline.NoteCompleted(2, isSuccess: true, isOperationCanceled: false, currentSourceIsApplicationDocument: true) == NavigationCompletionAction.BeginNewSession, "owning N2 success must mint the next session.");
        var hello2 = processor.BeginDocumentSession();
        AssertTrue(hello2.Contains("\"semanticType\":\"host.hello\"", StringComparison.Ordinal), "owning NavigationCompleted must post host.hello.");
        var session2 = JsonDocument.Parse(hello2).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(!string.Equals(session0, session2, StringComparison.Ordinal), "S2 must not resurrect S0.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session2, "ready-s2-owning", "{}"))).Dispatched, "S2 renderer.ready must be required after owning completion.");
        AssertTrue(processor.IsReady, "S2 must be READY after renderer.ready.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session2, "ping-s2-owning", "{}"))).Dispatched, "S2 ping must be accepted.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session0, "ping-s0-after-s2", "{}"))).Error!.Code, "S0 must remain rejected after S2 READY.");

        var numeric = new NavigationSessionLifecycle();
        AssertTrue(numeric.NoteStarting(100, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "higher NavigationId may start first.");
        AssertTrue(numeric.NoteStarting(1, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "lower NavigationId may start later.");
        AssertTrue(numeric.TryGet(100, out var olderId) && numeric.TryGet(1, out var newerId) && olderId.StartSequence == 1 && newerId.StartSequence == 2, "startSequence, not NavigationId magnitude, is start order.");
        AssertTrue(numeric.NoteCompleted(100, true, false, true) == NavigationCompletionAction.None, "older completion of a larger NavigationId must not reauthorize.");
        AssertTrue(numeric.NoteCompleted(1, true, false, true) == NavigationCompletionAction.BeginNewSession, "later-started smaller NavigationId owns the handshake.");

        var timelineB = new NavigationSessionLifecycle();
        timelineB.NoteStarting(31, hostCancelled: false, isAllowedApplicationNavigation: true);
        timelineB.NoteStarting(32, hostCancelled: true, isAllowedApplicationNavigation: false);
        AssertTrue(timelineB.NoteCompleted(31, true, false, true) == NavigationCompletionAction.None, "N1 app success must not resurrect a session after later N2 NavigationStarting.");
        AssertTrue(timelineB.NoteCompleted(32, false, true, true) == NavigationCompletionAction.BeginNewSession, "latest host-cancelled completion still owns the cancelled-to-app handshake.");

        var timelineC = new NavigationSessionLifecycle();
        timelineC.NoteStarting(41, hostCancelled: true, isAllowedApplicationNavigation: false);
        timelineC.NoteStarting(42, hostCancelled: false, isAllowedApplicationNavigation: true);
        AssertTrue(timelineC.NoteCompleted(41, false, true, true) == NavigationCompletionAction.None, "cancelled N1 must not handshake while later N2 app is active.");
        AssertTrue(timelineC.NoteCompleted(42, true, false, true) == NavigationCompletionAction.BeginNewSession, "N2 app success must own the handshake after cancelled N1.");

        var timelineD = new NavigationSessionLifecycle();
        AssertTrue(timelineD.NoteStarting(7, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "redirect start must create one record.");
        AssertTrue(timelineD.TryGet(7, out var beforeRedirect) && beforeRedirect.StartSequence == 1, "redirect startSequence must be assigned once.");
        AssertTrue(timelineD.NoteStarting(7, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.UpdatedRedirect, "same NavigationId redirect must update the existing record.");
        AssertEqual(1, timelineD.ActiveCount, "redirect must remain one coherent navigation record.");
        AssertTrue(timelineD.TryGet(7, out var afterRedirect) && afterRedirect.StartSequence == 1, "redirect must keep the original startSequence.");
        AssertTrue(timelineD.NoteCompleted(7, true, false, true) == NavigationCompletionAction.BeginNewSession, "same-id redirect completion must remain one handshake.");

        var live = new BridgeMessageProcessor();
        var accidentalHello = live.BeginDocumentSession();
        var accidental = JsonDocument.Parse(accidentalHello).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(live.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, accidental, "ready-accidental", "{}"))).Dispatched, "accidentally live S1 must be READY before the new-document fence.");
        AssertTrue(SameDocumentSessionPolicy.Evaluate(isNewDocument: true, currentSourceIsApplicationDocument: true) == SameDocumentSourceChangeAction.InvalidateOnly, "new-document policy must invalidate only.");
        live.InvalidateSession();
        AssertTrue(live.DocumentSessionId is null, "new-document SourceChanged must drop S1 immediately.");
        AssertTrue(!live.IsReady, "new-document SourceChanged must not leave READY.");
        live.InvalidateSession();
        AssertTrue(live.DocumentSessionId is null, "new-document invalidate must be idempotent with a prior NavigationStarting fence.");
        AssertEqual(BridgeErrorCodes.StaleSession, live.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, accidental, "ping-accidental", "{}"))).Error!.Code, "S1 ping after new-document SourceChanged must be STALE.");

        var carried = live.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.ExternalLinkRequest, accidental, "ext-carried", "{\"uri\":\"https://example.com/carried\"}")));
        AssertEqual(BridgeErrorCodes.StaleSession, carried.Error!.Code, "compromised renderer carrying S1 into the new-document interval must be STALE.");
        AssertTrue(carried.ExternalUri is null, "carried S1 externalLink must not dispatch a URI.");
        AssertTrue(!carried.Dispatched, "carried S1 externalLink must not dispatch a native side effect.");
        AssertTrue(live.DocumentSessionId is null, "carried S1 must not mint a replacement session.");

        var owning = new NavigationSessionLifecycle();
        owning.NoteStarting(50, hostCancelled: false, isAllowedApplicationNavigation: true);
        AssertTrue(owning.NoteCompleted(50, true, false, true) == NavigationCompletionAction.BeginNewSession, "owning NavigationCompleted must create S2 after the new-document interval.");
        var replacementHello = live.BeginDocumentSession();
        var replacement = JsonDocument.Parse(replacementHello).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(!string.Equals(accidental, replacement, StringComparison.Ordinal), "S2 must not resurrect the carried S1.");
        AssertTrue(live.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, replacement, "ready-replacement", "{}"))).Dispatched, "S2 renderer.ready must complete the owning handshake.");
        AssertEqual(BridgeErrorCodes.StaleSession, live.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.ExternalLinkRequest, accidental, "ext-carried-after-s2", "{\"uri\":\"https://example.com/after\"}"))).Error!.Code, "carried S1 must remain STALE after S2 READY.");

        AssertTrue(ProcessRecoveryGate.ShouldApply(1, 1), "matching renderer generation must apply recovery.");
        AssertTrue(!ProcessRecoveryGate.ShouldApply(1, 2), "G1 recovery must not apply after G2 is current.");
        AssertTrue(!ProcessRecoveryGate.ShouldApply(0, 0), "unset renderer generation must not recover.");
        var staleRequest = new ProcessRecoveryRequest(1, WebViewProcessFailedKind.RenderProcessExited, WebViewProcessRecoveryAction.ReloadApplicationDocument);
        AssertEqual(1, staleRequest.RendererGeneration, "recovery request must freeze renderer generation.");
        AssertTrue(staleRequest.Kind == WebViewProcessFailedKind.RenderProcessExited, "recovery request must freeze failure kind.");
        AssertTrue(staleRequest.Action == WebViewProcessRecoveryAction.ReloadApplicationDocument, "recovery request must freeze recovery action.");
        AssertTrue(!ProcessRecoveryGate.ShouldApply(staleRequest.RendererGeneration, 2), "late G1 reload must be discarded when G2 is current.");
        var staleRecreate = new ProcessRecoveryRequest(1, WebViewProcessFailedKind.BrowserProcessExited, WebViewProcessRecoveryAction.RecreateControl);
        AssertTrue(!ProcessRecoveryGate.ShouldApply(staleRecreate.RendererGeneration, 2), "late G1 recreate must be discarded when G2 is current.");

        var hostText = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "LLMW.Writing.UI", "Hosting", "WebViewRuntimeHost.cs"));
        AssertTrue(hostText.Contains("AdoptRenderer", StringComparison.Ordinal), "WebView/Core recreate must allocate renderer generation at control ownership.");
        AssertTrue(hostText.Contains("new ProcessRecoveryRequest(_ownership.CurrentGeneration, kind, action)", StringComparison.Ordinal), "queued recovery must freeze the current renderer generation.");
        AssertTrue(hostText.Contains("ProcessRecoveryGate.ShouldApply", StringComparison.Ordinal), "queued recovery must re-check renderer generation before mutating.");
        AssertTrue(hostText.Contains("if (!IsCurrentCore(sender))", StringComparison.Ordinal)
            && hostText.Contains("OnProcessFailed", StringComparison.Ordinal), "ProcessFailed must ignore an obsolete CoreWebView2 sender.");
        var processFailed = hostText.IndexOf("private void OnProcessFailed", StringComparison.Ordinal);
        var processFailedBody = hostText.Substring(processFailed, hostText.IndexOf("private void OnWebMessageReceived", StringComparison.Ordinal) - processFailed);
        AssertTrue(processFailedBody.Contains("if (!IsCurrentCore(sender))", StringComparison.Ordinal), "OnProcessFailed must return when sender is not the current CoreWebView2.");
        var sourceChanged = hostText.IndexOf("private void OnSourceChanged", StringComparison.Ordinal);
        var sourceChangedBody = hostText.Substring(sourceChanged, hostText.IndexOf("private void OnNavigationCompleted", StringComparison.Ordinal) - sourceChanged);
        AssertTrue(sourceChangedBody.Contains("_processor.InvalidateSession()", StringComparison.Ordinal), "SourceChanged must invalidate any live DocumentSession.");
        AssertTrue(sourceChangedBody.Contains("BeginFreshDocumentSession", StringComparison.Ordinal)
            && sourceChangedBody.Contains("BeginNewSession", StringComparison.Ordinal), "SourceChanged handshake must remain gated on BeginNewSession.");
        AssertTrue(!sourceChangedBody.Contains("IgnoreNewDocument", StringComparison.Ordinal), "new-document SourceChanged must not ignore a live session.");
        AssertTrue(hostText.Contains("RecoverFromProcessFailureAsync(ProcessRecoveryRequest request)", StringComparison.Ordinal), "recovery callback must receive the frozen generation request.");
        AssertTrue(hostText.Contains("ReloadApplicationShellAsync(int expectedGeneration)", StringComparison.Ordinal), "reload recovery must bind to renderer generation.");
        AssertTrue(hostText.Contains("RecreateControlAsync(int expectedGeneration)", StringComparison.Ordinal), "recreate recovery must bind to renderer generation.");
        var reload = hostText.IndexOf("private async Task ReloadApplicationShellAsync", StringComparison.Ordinal);
        var reloadBody = hostText.Substring(reload, hostText.IndexOf("private void BeginFreshDocumentSession", StringComparison.Ordinal) - reload);
        AssertTrue(reloadBody.Contains("ProcessRecoveryGate.ShouldApply(expectedGeneration, _ownership.CurrentGeneration)", StringComparison.Ordinal), "late reload must discard work when generation changed.");
        AssertTrue(reloadBody.IndexOf("ProcessRecoveryGate.ShouldApply", StringComparison.Ordinal) < reloadBody.IndexOf("core.Navigate", StringComparison.Ordinal), "stale G1 recovery must not Navigate G2.");

        Console.WriteLine("WP15 corrective pass 3 tests passed.");
        return 64;
    }

    private static int Wp15CorrectivePass4Tests()
    {
        var processor = new BridgeMessageProcessor();
        var hello0 = processor.BeginDocumentSession();
        var session0 = JsonDocument.Parse(hello0).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session0, "ready-s0-p4", "{}"))).Dispatched, "S0 ready must establish READY.");

        var reverse = new NavigationSessionLifecycle();
        processor.InvalidateSession();
        AssertTrue(reverse.NoteStarting(1, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "N1 application start must track.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session0, "ping-n1-p4", "{}"))).Error!.Code, "N1 NavigationStarting must stale S0.");
        processor.InvalidateSession();
        AssertTrue(reverse.NoteStarting(2, hostCancelled: true, isAllowedApplicationNavigation: false) == NavigationTrackResult.Tracked, "N2 external start must track as host-cancelled.");
        AssertTrue(reverse.TryGet(1, out var n1Pending) && n1Pending.CanReplaceTopLevelDocument, "N1 must remain an active document replacer.");
        AssertTrue(reverse.TryGet(2, out var n2Cancelled) && !n2Cancelled.CanReplaceTopLevelDocument, "N2 host-cancelled must not replace the top-level document.");

        AssertTrue(reverse.NoteCompleted(2, isSuccess: false, isOperationCanceled: true, currentSourceIsApplicationDocument: true) == NavigationCompletionAction.None, "N2 OperationCanceled must not create a session while N1 can still replace the document.");
        AssertTrue(processor.DocumentSessionId is null, "no transient session may exist after N2 cancelled completion.");
        AssertTrue(!processor.IsReady, "bridge must remain stale after N2 cancelled completion.");
        AssertTrue(reverse.TryGet(1, out var n1Owner) && n1Owner.CanReplaceTopLevelDocument, "N1 must still own pending document replacement.");
        AssertTrue(reverse.LatestStartSequence == n1Owner.StartSequence, "ownership must transfer to the newest remaining active replacer.");
        AssertTrue(!reverse.TryGet(2, out _), "completed N2 must not remain the start-sequence owner.");

        AssertTrue(SameDocumentSessionPolicy.Evaluate(isNewDocument: true, currentSourceIsApplicationDocument: true) == SameDocumentSourceChangeAction.InvalidateOnly, "N1 SourceChanged(IsNewDocument=true) must not handshake.");
        processor.InvalidateSession();
        AssertTrue(processor.DocumentSessionId is null, "new-document SourceChanged during N1 load must not mint a session.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session0, "ping-n1-source-p4", "{}"))).Error!.Code, "S0 must remain rejected through the N1 new-document interval.");

        AssertTrue(reverse.NoteCompleted(1, isSuccess: true, isOperationCanceled: false, currentSourceIsApplicationDocument: true) == NavigationCompletionAction.BeginNewSession, "N1 success must mint exactly one fresh session.");
        var hello2 = processor.BeginDocumentSession();
        AssertTrue(hello2.Contains("\"semanticType\":\"host.hello\"", StringComparison.Ordinal), "owning N1 success must post host.hello.");
        var session2 = JsonDocument.Parse(hello2).RootElement.GetProperty("documentSessionId").GetString()!;
        AssertTrue(!string.Equals(session0, session2, StringComparison.Ordinal), "S2 must not resurrect S0.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.RendererReady, session2, "ready-s2-p4", "{}"))).Dispatched, "S2 renderer.ready must complete the handshake.");
        AssertTrue(processor.IsReady, "S2 must be READY.");
        AssertTrue(processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session2, "ping-s2-p4", "{}"))).Dispatched, "S2 ping must be accepted.");
        AssertEqual(BridgeErrorCodes.StaleSession, processor.ProcessIncoming(Msg(Envelope(BridgeSemanticTypes.BridgePing, session0, "ping-s0-after-p4", "{}"))).Error!.Code, "S0 must remain rejected after S2 READY.");

        var failedReplacer = new NavigationSessionLifecycle();
        failedReplacer.NoteStarting(11, hostCancelled: false, isAllowedApplicationNavigation: true);
        failedReplacer.NoteStarting(12, hostCancelled: true, isAllowedApplicationNavigation: false);
        AssertTrue(failedReplacer.NoteCompleted(12, false, true, true) == NavigationCompletionAction.None, "N2 cancelled completion must not reauthorize the old document.");
        AssertTrue(failedReplacer.NoteCompleted(11, isSuccess: false, isOperationCanceled: false, currentSourceIsApplicationDocument: true) == NavigationCompletionAction.ShowNativeFailure, "N1 later failure must follow fail-closed native-error policy.");
        AssertTrue(failedReplacer.NoteCompleted(12, false, true, true) == NavigationCompletionAction.IgnoreUnknown, "a finished cancelled navigation must not manufacture a later session.");

        var manyCancelled = new NavigationSessionLifecycle();
        manyCancelled.NoteStarting(21, hostCancelled: false, isAllowedApplicationNavigation: true);
        manyCancelled.NoteStarting(22, hostCancelled: true, isAllowedApplicationNavigation: false);
        manyCancelled.NoteStarting(23, hostCancelled: true, isAllowedApplicationNavigation: false);
        AssertTrue(manyCancelled.NoteCompleted(23, false, true, true) == NavigationCompletionAction.None, "a later cancelled navigation must not block because it cannot replace the document.");
        AssertTrue(manyCancelled.NoteCompleted(22, false, true, true) == NavigationCompletionAction.None, "multiple cancelled navigations must not handshake while a replacer is active.");
        AssertTrue(manyCancelled.TryGet(21, out var remainingReplacer) && remainingReplacer.CanReplaceTopLevelDocument, "the active document replacer must remain tracked.");
        AssertTrue(manyCancelled.LatestStartSequence == remainingReplacer.StartSequence, "ownership must settle on the remaining document-replacing navigation.");
        AssertTrue(manyCancelled.NoteCompleted(21, true, false, true) == NavigationCompletionAction.BeginNewSession, "the remaining replacer must still be able to resolve a session.");

        var onlyCancelled = new NavigationSessionLifecycle();
        onlyCancelled.NoteStarting(31, hostCancelled: true, isAllowedApplicationNavigation: false);
        onlyCancelled.NoteStarting(32, hostCancelled: true, isAllowedApplicationNavigation: false);
        AssertTrue(onlyCancelled.NoteCompleted(31, false, true, true) == NavigationCompletionAction.None, "an older cancelled completion must not handshake.");
        AssertTrue(onlyCancelled.NoteCompleted(32, false, true, true) == NavigationCompletionAction.BeginNewSession, "cancelled navigations must not block a safe session forever when none can replace the document.");

        var redirect = new NavigationSessionLifecycle();
        AssertTrue(redirect.NoteStarting(7, hostCancelled: false, isAllowedApplicationNavigation: true) == NavigationTrackResult.Tracked, "redirect start must track once.");
        AssertTrue(redirect.NoteStarting(7, hostCancelled: true, isAllowedApplicationNavigation: false) == NavigationTrackResult.UpdatedRedirect, "same NavigationId redirect must remain one record.");
        AssertEqual(1, redirect.ActiveCount, "bounded tracker must not duplicate redirect NavigationId records.");
        AssertTrue(redirect.NoteCompleted(7, false, true, true) == NavigationCompletionAction.BeginNewSession, "a cancelled same-id redirect without an active replacer may restore a session.");

        var ownership = new RendererOwnership();
        var g1Control = new object();
        var g1 = ownership.AdoptRenderer(g1Control);
        AssertEqual(1, g1, "first control ownership must allocate G1 immediately.");
        var r1 = new ProcessRecoveryRequest(g1, WebViewProcessFailedKind.BrowserProcessExited, WebViewProcessRecoveryAction.RecreateControl);
        var r2 = new ProcessRecoveryRequest(g1, WebViewProcessFailedKind.BrowserProcessExited, WebViewProcessRecoveryAction.RecreateControl);
        AssertTrue(ProcessRecoveryGate.ShouldApply(r1.RendererGeneration, ownership.CurrentGeneration), "R1(G1) must pass the gate while G1 is current.");
        var controlA = new object();
        var coreA = new object();
        var g2 = ownership.AdoptRenderer(controlA);
        AssertEqual(2, g2, "RecreateRenderer must allocate G2 before Ensure completes.");
        AssertTrue(ReferenceEquals(ownership.CurrentRenderer, controlA), "A must be the current renderer after R1 replacement.");
        AssertTrue(!ProcessRecoveryGate.ShouldApply(r2.RendererGeneration, ownership.CurrentGeneration), "R2(G1) must be stale after G2 is allocated.");
        AssertTrue(!ownership.IsCurrent(g1, g1Control), "G1 recovery must not still own the renderer.");
        AssertTrue(!ownership.ShouldRegisterHandlers(r2.RendererGeneration, controlA, coreA), "R2 must not mark handler state on A.");
        AssertTrue(!ownership.IsCurrent(g1, controlA), "R2 must not Navigate or replace A.");

        var mutated = false;
        if (ProcessRecoveryGate.ShouldApply(r2.RendererGeneration, ownership.CurrentGeneration)
            && ownership.IsCurrent(g1, controlA))
        {
            mutated = true;
            ownership.AdoptRenderer(new object());
        }

        AssertTrue(!mutated, "R2 must not replace A, Navigate, or mutate session/handler state.");
        AssertTrue(ownership.ShouldRegisterHandlers(g2, controlA, coreA), "A/G2 initialization must still be allowed to register handlers.");
        ownership.MarkHandlersRegistered(g2, controlA, coreA);
        AssertTrue(ownership.HasHandlersFor(g2, coreA), "G2 must have WP15 handlers after A initialization completes.");
        AssertTrue(ReferenceEquals(ownership.CurrentRenderer, controlA), "exactly one current renderer must survive concurrent G1 recovery.");
        AssertEqual(2, ownership.CurrentGeneration, "G2 must remain current after discarding R2.");

        var detached = new RendererOwnership();
        var first = new object();
        var firstGeneration = detached.AdoptRenderer(first);
        var replacementA = new object();
        var coreDetached = new object();
        var generationA = detached.AdoptRenderer(replacementA);
        var mapped = false;
        var navigated = false;
        var hello = false;
        var registeredDetached = false;
        var replacementB = new object();
        var coreB = new object();
        var generationB = detached.AdoptRenderer(replacementB);
        if (detached.IsCurrent(generationA, replacementA) && detached.ShouldRegisterHandlers(generationA, replacementA, coreDetached))
        {
            registeredDetached = true;
            detached.MarkHandlersRegistered(generationA, replacementA, coreDetached);
            mapped = true;
            navigated = true;
            hello = true;
        }

        AssertTrue(!detached.IsCurrent(generationA, replacementA), "A continuation must detect stale generation/control identity.");
        AssertTrue(!detached.ShouldRegisterHandlers(generationA, replacementA, coreDetached), "detached A must not register handlers.");
        AssertTrue(!registeredDetached && !mapped && !navigated && !hello, "detached A must not map, Navigate, or host.hello.");
        AssertTrue(!detached.HasHandlersFor(generationA, coreDetached), "detached Core registration must have zero authority over the current Core.");
        AssertTrue(detached.IsCurrent(generationB, replacementB), "B must remain the sole current renderer.");
        AssertTrue(detached.ShouldRegisterHandlers(generationB, replacementB, coreB), "current Core B must still install handlers exactly once.");
        detached.MarkHandlersRegistered(generationB, replacementB, coreB);
        AssertTrue(detached.HasHandlersFor(generationB, coreB), "B must own current handler registration.");
        AssertTrue(!detached.ShouldRegisterHandlers(generationB, replacementB, coreB), "current Core handlers must install exactly once.");
        AssertTrue(firstGeneration == 1 && generationA == 2 && generationB == 3, "each control ownership change must allocate a new generation immediately.");

        var hostText = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "LLMW.Writing.UI", "Hosting", "WebViewRuntimeHost.cs"));
        AssertTrue(!hostText.Contains("_handlersRegistered", StringComparison.Ordinal), "global handler-registered flag must not skip the current Core.");
        AssertTrue(!hostText.Contains("_rendererGeneration++", StringComparison.Ordinal), "generation must not wait for EnsureCoreWebView2Async.");
        var recreate = hostText.IndexOf("private async Task RecreateControlAsync", StringComparison.Ordinal);
        var recreateBody = hostText.Substring(recreate, hostText.IndexOf("private async Task ReloadApplicationShellAsync", StringComparison.Ordinal) - recreate);
        AssertTrue(recreateBody.IndexOf("RecreateRenderer()", StringComparison.Ordinal) < recreateBody.IndexOf("AdoptRenderer", StringComparison.Ordinal), "G2 must be allocated immediately when RecreateRenderer changes control ownership.");
        var apply = hostText.IndexOf("private async Task ApplyPreNavigationHardeningAndNavigateAsync", StringComparison.Ordinal);
        var applyBody = hostText.Substring(apply, hostText.IndexOf("private void RegisterHandlers", StringComparison.Ordinal) - apply);
        AssertTrue(applyBody.Contains("EnsureCoreWebView2Async", StringComparison.Ordinal)
            && applyBody.IndexOf("EnsureCoreWebView2Async", StringComparison.Ordinal) < applyBody.LastIndexOf("_ownership.IsCurrent", StringComparison.Ordinal), "initialization must re-check generation/control identity after Ensure.");
        AssertTrue(applyBody.Contains("SetVirtualHostNameToFolderMapping", StringComparison.Ordinal)
            && applyBody.Contains("_ownership.IsCurrent", StringComparison.Ordinal), "virtual-host mapping must stay generation-bound.");
        AssertTrue(applyBody.Contains("core.Navigate", StringComparison.Ordinal)
            && applyBody.LastIndexOf("_ownership.IsCurrent", StringComparison.Ordinal) < applyBody.IndexOf("core.Navigate", StringComparison.Ordinal), "Navigate must not run on a detached control.");
        AssertTrue(hostText.Contains("ShouldRegisterHandlers", StringComparison.Ordinal)
            && hostText.Contains("MarkHandlersRegistered", StringComparison.Ordinal), "handler registration must be per Core/generation.");

        Console.WriteLine("WP15 corrective pass 4 tests passed.");
        return 64;
    }

    private static void MapAndKeepSession(CoreWebView2ProcessFailedKind source, WebViewProcessFailedKind expected)
    {
        var mapped = WebViewProcessFailedKindMapper.Map(source);
        AssertTrue(mapped == expected, source + " must map to the matching policy kind.");
        AssertTrue(WebViewProcessRecoveryPolicy.Evaluate(mapped, 0) == WebViewProcessRecoveryAction.ObserveKeepSession, source + " must observe and keep the session.");
        AssertTrue(!WebViewProcessRecoveryPolicy.LosesRendererDocument(mapped), source + " must not imply renderer document loss.");
    }

    private static ValidatedExternalUri ValidateUri(string raw)
    {
        AssertTrue(ExternalUriPolicy.TryValidate(raw, out var validated, out _), "test URI must validate.");
        return validated!;
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
