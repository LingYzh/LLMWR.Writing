namespace LLMW.Writing.UI.WebView;

internal static class BridgeProtocol
{
    public const string Name = "llmw-web-bridge";
    public const int Version = 1;
    public const int MaximumEnvelopeBytes = 1024 * 1024;
    public const int MaximumJsonDepth = 8;
    public const int MaximumIdChars = 64;
    public const int MaximumSemanticTypeChars = 64;
    public const int MaximumMetadataChars = 64;
    public const int MaximumSafeErrorChars = 256;
    public const int MaximumReplayCache = 4096;
    public const string AppName = "LLMW.Writing";
    public const string ShellName = "wp16-editor";
    public const int MaximumEditorInsertChars = 256 * 1024;
}

internal static class BridgeSemanticTypes
{
    public const string RendererReady = "renderer.ready";
    public const string BridgePing = "bridge.ping";
    public const string ExternalLinkRequest = "externalLink.request";
    public const string EditorBindAck = "editor.bind.ack";
    public const string EditorChange = "editor.change";
    public const string EditorShadowResyncBegin = "editor.shadow.resync.begin";
    public const string EditorShadowResyncChunk = "editor.shadow.resync.chunk";
    public const string EditorShadowResyncCommit = "editor.shadow.resync.commit";
    public const string EditorSaveRequest = "editor.save.request";
    public const string EditorRecoveryResponse = "editor.recovery.response";
    public const string EditorSelectionChanged = "editor.selection.changed";
    public const string EditorCloseRequest = "editor.close.request";
    public const string HostHello = "host.hello";
    public const string BridgePong = "bridge.pong";
    public const string BridgeAck = "bridge.ack";
    public const string BridgeError = "bridge.error";
    public const string HostStatus = "host.status";
    public const string EditorBind = "editor.bind";
    public const string EditorDocumentBegin = "editor.document.begin";
    public const string EditorDocumentChunk = "editor.document.chunk";
    public const string EditorDocumentCommit = "editor.document.commit";
    public const string EditorState = "editor.state";
    public const string EditorSaveResult = "editor.save.result";
    public const string EditorLeaseState = "editor.lease.state";
    public const string EditorRecoveryOffer = "editor.recovery.offer";
    public const string EditorRecoveryConflict = "editor.recovery.conflict";
    public const string EditorError = "editor.error";

    public static bool IsInbound(string semanticType)
        => semanticType is RendererReady or BridgePing or ExternalLinkRequest
            or EditorBindAck or EditorChange or EditorShadowResyncBegin or EditorShadowResyncChunk
            or EditorShadowResyncCommit or EditorSaveRequest or EditorRecoveryResponse
            or EditorSelectionChanged or EditorCloseRequest;
}

internal static class BridgeErrorCodes
{
    public const string WrongOrigin = "BRIDGE_WRONG_ORIGIN";
    public const string StaleSession = "BRIDGE_STALE_SESSION";
    public const string NotReady = "BRIDGE_NOT_READY";
    public const string ProtocolUnsupported = "BRIDGE_PROTOCOL_UNSUPPORTED";
    public const string UnknownMessageType = "BRIDGE_UNKNOWN_MESSAGE_TYPE";
    public const string InvalidSchema = "BRIDGE_INVALID_SCHEMA";
    public const string MessageTooLarge = "BRIDGE_MESSAGE_TOO_LARGE";
    public const string JsonTooDeep = "BRIDGE_JSON_TOO_DEEP";
    public const string MalformedJson = "BRIDGE_MALFORMED_JSON";
    public const string Replay = "BRIDGE_REPLAY";
    public const string AdditionalObjectsDenied = "BRIDGE_ADDITIONAL_OBJECTS_DENIED";
    public const string NavigationBlocked = "NAVIGATION_BLOCKED";
    public const string ExternalUrlDenied = "EXTERNAL_URL_DENIED";
    public const string ExternalLinkBusy = "EXTERNAL_LINK_BUSY";
    public const string EditorPatchInvalid = "EDITOR_PATCH_INVALID";
    public const string EditorPatchSequence = "EDITOR_PATCH_SEQUENCE";
}

internal sealed class BridgeError
{
    public BridgeError(string code, string safeMessage)
    {
        Code = code;
        SafeMessage = SafeText.Truncate(safeMessage, BridgeProtocol.MaximumSafeErrorChars);
    }

    public string Code { get; }
    public string SafeMessage { get; }
}

internal static class SafeText
{
    public static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars];
    }
}

internal sealed record WebViewSecuritySettings(
    bool AreHostObjectsAllowed,
    bool AreDevToolsEnabled,
    bool AreDefaultContextMenusEnabled,
    bool IsGeneralAutofillEnabled,
    bool IsPasswordAutosaveEnabled,
    bool IsWebMessageEnabled,
    bool AreDefaultScriptDialogsEnabled)
{
    public static WebViewSecuritySettings Release { get; } = new(
        AreHostObjectsAllowed: false,
        AreDevToolsEnabled: false,
        AreDefaultContextMenusEnabled: false,
        IsGeneralAutofillEnabled: false,
        IsPasswordAutosaveEnabled: false,
        IsWebMessageEnabled: true,
        AreDefaultScriptDialogsEnabled: false);

    public static WebViewSecuritySettings DebugDevelopment { get; } =
        Release with { AreDevToolsEnabled = true };

    public static WebViewSecuritySettings ForCurrentBuild =>
#if DEBUG
        DebugDevelopment;
#else
        Release;
#endif
}

internal static class WebViewUserDataFolder
{
    public const string CompanyFolder = "LLMW.Writing";
    public const string WebViewFolder = "WebView2";

    public static string Resolve()
        => Path.Combine(DistributionLayout.ApplicationDataRoot, WebViewFolder);
}

internal sealed class RendererAssetLayout
{
    public RendererAssetLayout(string directoryPath)
    {
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public string DirectoryPath { get; }
    public string IndexHtmlPath => Path.Combine(DirectoryPath, "index.html");
    public string BridgeJsPath => Path.Combine(DirectoryPath, "bridge.js");
    public string AppCssPath => Path.Combine(DirectoryPath, "app.css");
    public string EditorBundlePath => Path.Combine(DirectoryPath, "editor.bundle.js");

    public bool Exists =>
        File.Exists(IndexHtmlPath)
        && File.Exists(BridgeJsPath)
        && File.Exists(AppCssPath)
        && File.Exists(EditorBundlePath);

    public static RendererAssetLayout FromApplicationBase(string applicationBaseDirectory)
        => new(Path.Combine(applicationBaseDirectory, "web-editor", "app"));
}

internal static class SafeOriginLog
{
    public static string Describe(string? uri)
    {
        if (!AppOriginPolicy.TryParseAbsolute(uri, out var parsed))
        {
            return "invalid";
        }

        var host = string.IsNullOrWhiteSpace(parsed.IdnHost) ? parsed.Host : parsed.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return parsed.Scheme;
        }

        var defaultPort = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443
            : string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ? 80
            : parsed.Port;
        var port = parsed.IsDefaultPort || parsed.Port == defaultPort
            ? string.Empty
            : ":" + parsed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return parsed.Scheme + "://" + host + port;
    }
}

internal interface IBridgeLog
{
    void Write(string code, string? semanticType, string? messageId, string? sessionId, string? origin);
}

internal sealed class NullBridgeLog : IBridgeLog
{
    public static NullBridgeLog Instance { get; } = new();

    public void Write(string code, string? semanticType, string? messageId, string? sessionId, string? origin)
    {
        _ = SafeOriginLog.Describe(origin);
    }
}

internal sealed class RecordingBridgeLog : IBridgeLog
{
    public List<string> Entries { get; } = [];

    public void Write(string code, string? semanticType, string? messageId, string? sessionId, string? origin)
    {
        Entries.Add($"{code}|{semanticType}|{messageId}|{sessionId}|{SafeOriginLog.Describe(origin)}");
    }
}
