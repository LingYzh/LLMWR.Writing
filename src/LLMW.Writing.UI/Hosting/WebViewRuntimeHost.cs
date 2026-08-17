using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using LLMW.Writing.UI.WebView;

namespace LLMW.Writing.UI.Hosting;

internal sealed class WebViewRuntimeHost
{
    private readonly IWebViewRendererSite _site;
    private readonly BridgeMessageProcessor _processor;
    private readonly IExternalBrowserLauncher _launcher;
    private readonly IBridgeLog _log;
    private readonly NavigationSessionLifecycle _navigationLifecycle = new();
    private readonly ExternalLinkCoordinator _externalLinks = new();
    private CoreWebView2Environment? _environment;
    private bool _initialized;
    private bool _handlersRegistered;
    private int _rendererGeneration;
    private int _unresponsiveRecoveryCount;

    public WebViewRuntimeHost(
        IWebViewRendererSite site,
        IExternalBrowserLauncher? launcher = null,
        IBridgeLog? log = null)
    {
        _site = site;
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
            _site.ShowNativeError("RENDERER_ASSETS_MISSING", "Application renderer assets are missing.");
            return;
        }

        try
        {
            await CreateEnvironmentAsync().ConfigureAwait(true);
            await ApplyPreNavigationHardeningAndNavigateAsync(_site.Renderer).ConfigureAwait(true);
        }
        catch (Exception)
        {
            _site.ShowNativeError("WEBVIEW_INIT_FAILED", "The renderer host could not start.");
        }
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

    private async Task CreateEnvironmentAsync()
    {
        var userData = WebViewUserDataFolder.Resolve();
        Directory.CreateDirectory(userData);
        var options = new CoreWebView2EnvironmentOptions();
        _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
    }

    private async Task ApplyPreNavigationHardeningAndNavigateAsync(WebView2 webView)
    {
        await webView.EnsureCoreWebView2Async(_environment);
        _rendererGeneration++;
        var core = webView.CoreWebView2;
        var assets = RendererAssetLayout.FromApplicationBase(AppContext.BaseDirectory);
        ApplySettings(core.Settings, WebViewSecuritySettings.ForCurrentBuild);
        RegisterHandlers(core);
        core.SetVirtualHostNameToFolderMapping(
            AppOrigin.Host,
            assets.DirectoryPath,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate(AppOrigin.IndexHtmlAbsoluteUri);
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
        core.SourceChanged += OnSourceChanged;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

        if (WebResourcePolicy.IsAllowed(args.Request.Uri))
        {
            return;
        }

        _log.Write(BridgeErrorCodes.NavigationBlocked, "resource", null, _processor.DocumentSessionId, args.Request.Uri);
        args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

        _processor.InvalidateSession();
        var decision = NavigationPolicy.EvaluateTopLevel(args.Uri);
        var isAllowedApplication = decision == NavigationDecision.AllowApplication;
        if (!isAllowedApplication)
        {
            args.Cancel = true;
        }

        var tracked = _navigationLifecycle.NoteStarting(
            args.NavigationId,
            hostCancelled: !isAllowedApplication,
            isAllowedApplicationNavigation: isAllowedApplication);
        if (tracked == NavigationTrackResult.Overflow)
        {
            args.Cancel = true;
            _navigationLifecycle.Reset();
            _processor.InvalidateSession();
            _site.ShowNativeError("NAVIGATION_FAILED", "The renderer navigation tracker is exhausted.");
            return;
        }

        if (isAllowedApplication)
        {
            return;
        }

        _log.Write(BridgeErrorCodes.NavigationBlocked, "navigation", null, null, args.Uri);
        if (ExternalNavigationIntent.MayOfferNativeDialog(decision, args.IsUserInitiated))
        {
            _ = OpenExternalFromNavigationAsync(args.Uri);
        }
    }

    private static void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = true;
    }

    private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

        var action = SameDocumentSessionPolicy.Evaluate(
            args.IsNewDocument,
            AppOriginPolicy.IsApplicationDocument(sender.Source));
        _processor.InvalidateSession();
        if (action == SameDocumentSourceChangeAction.BeginNewSession)
        {
            BeginFreshDocumentSession(sender);
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

        var sourceIsApp = AppOriginPolicy.IsApplicationDocument(sender.Source);
        var action = _navigationLifecycle.NoteCompleted(
            args.NavigationId,
            args.IsSuccess,
            args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled,
            sourceIsApp);

        if (action == NavigationCompletionAction.BeginNewSession)
        {
            BeginFreshDocumentSession(sender);
            return;
        }

        if (action == NavigationCompletionAction.ShowNativeFailure)
        {
            _processor.InvalidateSession();
            _site.ShowNativeError("NAVIGATION_FAILED", "The application renderer failed to load.");
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!IsCurrentCore(sender))
        {
            return;
        }

        var decision = NavigationPolicy.EvaluateNewWindow(args.Uri);
        _log.Write(BridgeErrorCodes.NavigationBlocked, "new-window", null, _processor.DocumentSessionId, args.Uri);
        if (ExternalNavigationIntent.MayOfferNativeDialog(decision, args.IsUserInitiated))
        {
            _ = OpenExternalFromNavigationAsync(args.Uri);
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
        if (!IsCurrentCore(sender))
        {
            return;
        }

        var kind = WebViewProcessFailedKindMapper.Map(args.ProcessFailedKind);
        var action = WebViewProcessRecoveryPolicy.Evaluate(kind, _unresponsiveRecoveryCount);
        if (kind == WebViewProcessFailedKind.RenderProcessUnresponsive
            && action == WebViewProcessRecoveryAction.ReloadApplicationDocument)
        {
            _unresponsiveRecoveryCount++;
        }

        var request = new ProcessRecoveryRequest(_rendererGeneration, kind, action);
        if (WebViewProcessRecoveryPolicy.LosesRendererDocument(kind))
        {
            _processor.InvalidateSession();
            _navigationLifecycle.Reset();
        }

        _ = _site.DispatcherQueue.TryEnqueue(() => _ = RecoverFromProcessFailureAsync(request));
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsCurrentCore(sender))
        {
            return;
        }

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
            _ = CompleteExternalFromBridgeAsync(result);
            return;
        }

        foreach (var outbound in result.OutboundJson)
        {
            PostToRenderer(outbound);
        }
    }

    private async Task CompleteExternalFromBridgeAsync(BridgeProcessResult result)
    {
        if (result.ExternalUri is null
            || string.IsNullOrEmpty(result.RequestMessageId)
            || string.IsNullOrEmpty(result.RequestDocumentSessionId))
        {
            return;
        }

        var request = new PendingExternalLink
        {
            Source = ExternalLinkSource.BridgeRequest,
            DocumentSessionId = result.RequestDocumentSessionId,
            RequestMessageId = result.RequestMessageId,
            Uri = result.ExternalUri
        };

        if (_externalLinks.TryAdmit(request) == ExternalLinkAdmitResult.Busy)
        {
            PostToRenderer(ExternalLinkBridgeReply.Busy(request.DocumentSessionId, request.RequestMessageId));
            return;
        }

        var accepted = false;
        try
        {
            accepted = await ConfirmExternalAsync(request.Uri).ConfigureAwait(true);
        }
        catch (Exception)
        {
            _externalLinks.Clear();
            PostToRenderer(ExternalLinkBridgeReply.FromLaunch(request, ExternalLinkLaunchResult.Cancelled));
            _site.ShowNativeError("EXTERNAL_URL_DENIED", "The external link could not be confirmed.");
            return;
        }

        ExternalLinkLaunchResult launch;
        try
        {
            launch = _externalLinks.Complete(
                request,
                accepted,
                _processor.DocumentSessionId,
                _processor.IsReady,
                CurrentSourceIsApplicationDocument(),
                _launcher);
        }
        catch (Exception)
        {
            _externalLinks.Clear();
            PostToRenderer(ExternalLinkBridgeReply.FromLaunch(request, ExternalLinkLaunchResult.Cancelled));
            _site.ShowNativeError("EXTERNAL_URL_DENIED", "The external link could not be opened.");
            return;
        }

        PostToRenderer(ExternalLinkBridgeReply.FromLaunch(request, launch));
    }

    private async Task OpenExternalFromNavigationAsync(string? raw)
    {
        if (!ExternalUriPolicy.TryValidate(raw, out var validated, out _))
        {
            return;
        }

        var request = new PendingExternalLink
        {
            Source = ExternalLinkSource.UserInitiatedNavigation,
            DocumentSessionId = _processor.DocumentSessionId ?? "none",
            RequestMessageId = Guid.NewGuid().ToString("D"),
            Uri = validated
        };

        if (_externalLinks.TryAdmit(request) == ExternalLinkAdmitResult.Busy)
        {
            return;
        }

        var accepted = false;
        try
        {
            accepted = await ConfirmExternalAsync(validated).ConfigureAwait(true);
        }
        catch (Exception)
        {
            _externalLinks.Clear();
            return;
        }

        try
        {
            _ = _externalLinks.Complete(
                request,
                accepted,
                _processor.DocumentSessionId,
                _processor.IsReady,
                CurrentSourceIsApplicationDocument(),
                _launcher);
        }
        catch (Exception)
        {
            _externalLinks.Clear();
            _site.ShowNativeError("EXTERNAL_URL_DENIED", "The external link could not be opened.");
        }
    }

    private async Task<bool> ConfirmExternalAsync(ValidatedExternalUri validated)
    {
        var dialog = new ContentDialog
        {
            Title = "Open external link",
            Content = validated.DisplayHost + Environment.NewLine + validated.AbsoluteUri,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            XamlRoot = _site.XamlRoot
        };

        var consent = await dialog.ShowAsync();
        return consent == ContentDialogResult.Primary;
    }

    private async Task RecoverFromProcessFailureAsync(ProcessRecoveryRequest request)
    {
        if (!ProcessRecoveryGate.ShouldApply(request.RendererGeneration, _rendererGeneration))
        {
            return;
        }

        switch (request.Action)
        {
            case WebViewProcessRecoveryAction.RecreateControl:
                _site.ShowNativeError("RENDERER_PROCESS_FAILED", "The browser process failed and the renderer is being recreated.");
                await RecreateControlAsync(request.RendererGeneration).ConfigureAwait(true);
                break;
            case WebViewProcessRecoveryAction.ReloadApplicationDocument:
                _site.ShowNativeError("RENDERER_PROCESS_FAILED", "The renderer process failed.");
                await ReloadApplicationShellAsync(request.RendererGeneration).ConfigureAwait(true);
                break;
            case WebViewProcessRecoveryAction.FailClosedNoNavigate:
                if (!ProcessRecoveryGate.ShouldApply(request.RendererGeneration, _rendererGeneration))
                {
                    return;
                }

                _site.ShowNativeError("RENDERER_PROCESS_FAILED", "The renderer failed and was not automatically recovered.");
                break;
            case WebViewProcessRecoveryAction.ObserveKeepSession:
                _log.Write("RENDERER_PROCESS_OBSERVED", "process", null, _processor.DocumentSessionId, AppOrigin.Origin);
                break;
        }
    }

    private async Task RecreateControlAsync(int expectedGeneration)
    {
        if (!ProcessRecoveryGate.ShouldApply(expectedGeneration, _rendererGeneration))
        {
            return;
        }

        _handlersRegistered = false;
        _navigationLifecycle.Reset();
        _processor.InvalidateSession();
        try
        {
            var replacement = _site.RecreateRenderer();
            if (_environment is null)
            {
                await CreateEnvironmentAsync().ConfigureAwait(true);
            }

            if (!ProcessRecoveryGate.ShouldApply(expectedGeneration, _rendererGeneration))
            {
                return;
            }

            await ApplyPreNavigationHardeningAndNavigateAsync(replacement).ConfigureAwait(true);
        }
        catch (Exception)
        {
            try
            {
                if (!ProcessRecoveryGate.ShouldApply(expectedGeneration, _rendererGeneration))
                {
                    return;
                }

                await CreateEnvironmentAsync().ConfigureAwait(true);
                if (!ProcessRecoveryGate.ShouldApply(expectedGeneration, _rendererGeneration))
                {
                    return;
                }

                _handlersRegistered = false;
                await ApplyPreNavigationHardeningAndNavigateAsync(_site.Renderer).ConfigureAwait(true);
            }
            catch (Exception)
            {
                _site.ShowNativeError("WEBVIEW_RECREATE_FAILED", "The renderer host could not be recreated.");
            }
        }
    }

    private async Task ReloadApplicationShellAsync(int expectedGeneration)
    {
        try
        {
            await Task.Delay(250).ConfigureAwait(true);
            if (!ProcessRecoveryGate.ShouldApply(expectedGeneration, _rendererGeneration))
            {
                return;
            }

            var core = _site.Renderer.CoreWebView2;
            if (core is null)
            {
                await RecreateControlAsync(expectedGeneration).ConfigureAwait(true);
                return;
            }

            core.Navigate(AppOrigin.IndexHtmlAbsoluteUri);
        }
        catch (Exception)
        {
            _site.ShowNativeError("RENDERER_RELOAD_FAILED", "The renderer could not be reloaded.");
        }
    }

    private void BeginFreshDocumentSession(CoreWebView2 sender)
    {
        _unresponsiveRecoveryCount = 0;
        var hello = _processor.BeginDocumentSession();
        sender.PostWebMessageAsJson(hello);
        _site.ShowNativeStatus("HOST_HELLO", "Application origin loaded.");
    }

    private bool IsCurrentCore(CoreWebView2 sender)
        => _site.Renderer.CoreWebView2 is CoreWebView2 current && ReferenceEquals(sender, current);

    private bool CurrentSourceIsApplicationDocument()
        => _site.Renderer.CoreWebView2 is CoreWebView2 core
           && AppOriginPolicy.IsApplicationDocument(core.Source);

    private void PostToRenderer(string json)
    {
        if (!HostToRendererDelivery.ShouldPost(json, _processor.DocumentSessionId))
        {
            return;
        }

        _site.Renderer.CoreWebView2?.PostWebMessageAsJson(json);
    }
}
