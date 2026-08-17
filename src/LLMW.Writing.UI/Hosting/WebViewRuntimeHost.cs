using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using LLMW.Writing.UI.WebView;

namespace LLMW.Writing.UI.Hosting;

internal sealed class WebViewRuntimeHost
{
    private readonly WebView2 _webView;
    private readonly TextBlock _nativeStatus;
    private readonly XamlRoot _xamlRoot;
    private readonly BridgeMessageProcessor _processor;
    private readonly IExternalBrowserLauncher _launcher;
    private readonly IBridgeLog _log;
    private CoreWebView2Environment? _environment;
    private bool _initialized;
    private bool _handlersRegistered;

    public WebViewRuntimeHost(
        WebView2 webView,
        TextBlock nativeStatus,
        XamlRoot xamlRoot,
        IExternalBrowserLauncher? launcher = null,
        IBridgeLog? log = null)
    {
        _webView = webView;
        _nativeStatus = nativeStatus;
        _xamlRoot = xamlRoot;
        _launcher = launcher ?? new ShellExecuteExternalBrowserLauncher();
        _log = log ?? NullBridgeLog.Instance;
        _processor = new BridgeMessageProcessor(_log);
    }

    public BridgeMessageProcessor Processor => _processor;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var assets = RendererAssetLayout.FromApplicationBase(AppContext.BaseDirectory);
        if (!assets.Exists)
        {
            ShowNativeError("RENDERER_ASSETS_MISSING", "Application renderer assets are missing.");
            return;
        }

        var userData = WebViewUserDataFolder.Resolve();
        Directory.CreateDirectory(userData);

        try
        {
            var options = new CoreWebView2EnvironmentOptions();
            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
            await _webView.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception)
        {
            ShowNativeError("WEBVIEW_INIT_FAILED", "The renderer host could not start.");
            return;
        }

        var core = _webView.CoreWebView2;
        ApplySettings(core.Settings, WebViewSecuritySettings.ForCurrentBuild);
        RegisterHandlers(core);
        core.SetVirtualHostNameToFolderMapping(
            AppOrigin.Host,
            assets.DirectoryPath,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate(AppOrigin.IndexHtmlAbsoluteUri);
    }

    internal static void ApplySettings(CoreWebView2Settings settings, WebViewSecuritySettings requested)
    {
        settings.AreHostObjectsAllowed = requested.AreHostObjectsAllowed;
        settings.AreDevToolsEnabled = requested.AreDevToolsEnabled;
        settings.AreDefaultContextMenusEnabled = requested.AreDefaultContextMenusEnabled;
        settings.IsGeneralAutofillEnabled = requested.IsGeneralAutofillEnabled;
        settings.IsPasswordAutosaveEnabled = requested.IsPasswordAutosaveEnabled;
        settings.IsWebMessageEnabled = requested.IsWebMessageEnabled;
        settings.AreDefaultScriptDialogsEnabled = requested.AreDefaultScriptDialogsEnabled;
    }

    private void RegisterHandlers(CoreWebView2 core)
    {
        if (_handlersRegistered)
        {
            return;
        }

        _handlersRegistered = true;
        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (WebResourcePolicy.IsAllowed(args.Request.Uri))
        {
            return;
        }

        _log.Write(BridgeErrorCodes.NavigationBlocked, "resource", null, _processor.DocumentSessionId, args.Request.Uri);
        args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _processor.InvalidateSession();
        var decision = NavigationPolicy.EvaluateTopLevel(args.Uri);
        if (decision == NavigationDecision.AllowApplication)
        {
            return;
        }

        args.Cancel = true;
        _log.Write(BridgeErrorCodes.NavigationBlocked, "navigation", null, null, args.Uri);
        if (decision == NavigationDecision.CancelAndOfferExternal)
        {
            _ = OpenExternalAsync(args.Uri, replyTo: null);
        }
    }

    private static void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = true;
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess || !AppOriginPolicy.IsApplicationDocument(sender.Source))
        {
            _processor.InvalidateSession();
            if (!args.IsSuccess)
            {
                ShowNativeError("NAVIGATION_FAILED", "The application renderer failed to load.");
            }

            return;
        }

        var hello = _processor.BeginDocumentSession();
        sender.PostWebMessageAsJson(hello);
        ShowNativeStatus("HOST_HELLO", "Application origin loaded.");
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        var decision = NavigationPolicy.EvaluateNewWindow(args.Uri);
        _log.Write(BridgeErrorCodes.NavigationBlocked, "new-window", null, _processor.DocumentSessionId, args.Uri);
        if (decision == NavigationDecision.CancelAndOfferExternal)
        {
            _ = OpenExternalAsync(args.Uri, replyTo: null);
        }
    }

    private static void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private static void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        _processor.InvalidateSession();
        ShowNativeError("RENDERER_PROCESS_FAILED", "The renderer process failed.");
        _ = ReloadApplicationShellAsync();
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var additional = args.AdditionalObjects is null ? 0 : args.AdditionalObjects.Count;
        var result = _processor.ProcessIncoming(new IncomingWebMessage
        {
            Source = args.Source,
            CurrentDocument = sender.Source,
            Json = args.WebMessageAsJson,
            AdditionalObjectCount = additional
        });

        if (result.ExternalUri is not null)
        {
            _ = CompleteExternalFromBridgeAsync(sender, result);
            return;
        }

        foreach (var outbound in result.OutboundJson)
        {
            sender.PostWebMessageAsJson(outbound);
        }
    }

    private async Task CompleteExternalFromBridgeAsync(CoreWebView2 sender, BridgeProcessResult result)
    {
        var sessionId = _processor.DocumentSessionId ?? "none";
        var opened = await OpenValidatedAsync(result.ExternalUri!).ConfigureAwait(true);
        sender.PostWebMessageAsJson(BridgeMessageProcessor.CompleteExternalLink(sessionId, replyTo: null, opened));
    }

    private async Task OpenExternalAsync(string? raw, string? replyTo)
    {
        _ = replyTo;
        if (!ExternalUriPolicy.TryValidate(raw, out var validated, out _))
        {
            return;
        }

        await OpenValidatedAsync(validated).ConfigureAwait(true);
    }

    private async Task<bool> OpenValidatedAsync(ValidatedExternalUri validated)
    {
        var dialog = new ContentDialog
        {
            Title = "Open external link",
            Content = validated.DisplayHost + Environment.NewLine + validated.AbsoluteUri,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            XamlRoot = _xamlRoot
        };

        var consent = await dialog.ShowAsync();
        if (consent != ContentDialogResult.Primary)
        {
            return false;
        }

        try
        {
            _launcher.Open(validated);
            return true;
        }
        catch (Exception)
        {
            ShowNativeError("EXTERNAL_URL_DENIED", "The external link could not be opened.");
            return false;
        }
    }

    private async Task ReloadApplicationShellAsync()
    {
        try
        {
            await Task.Delay(250).ConfigureAwait(true);
            if (_webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.Navigate(AppOrigin.IndexHtmlAbsoluteUri);
            }
        }
        catch (Exception)
        {
            ShowNativeError("RENDERER_RELOAD_FAILED", "The renderer could not be reloaded.");
        }
    }

    private void ShowNativeError(string code, string message)
    {
        _nativeStatus.Text = code + ": " + message;
        _nativeStatus.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void ShowNativeStatus(string code, string message)
    {
        _nativeStatus.Text = code + ": " + message;
        _nativeStatus.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }
}
