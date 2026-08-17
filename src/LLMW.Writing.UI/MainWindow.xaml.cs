using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.UI.Editor;
using LLMW.Writing.UI.Hosting;

namespace LLMW.Writing.UI;

public sealed partial class MainWindow : Window, IWebViewRendererSite
{
    private readonly CoreHostService? _coreHost;
    private WebViewRuntimeHost? _host;
    private EditorSessionController? _editor;
    private bool _started;

    public MainWindow()
        : this(null)
    {
    }

    internal MainWindow(CoreHostService? coreHost)
    {
        _coreHost = coreHost;
        InitializeComponent();
        Title = "LLMW.Writing";
        if (Content is FrameworkElement root)
        {
            root.Loaded += OnRootLoaded;
        }
    }

    public WebView2 Renderer => RendererView;

    XamlRoot IWebViewRendererSite.XamlRoot => RequireRoot().XamlRoot;

    DispatcherQueue IWebViewRendererSite.DispatcherQueue => RequireRoot().DispatcherQueue;

    public WebView2 RecreateRenderer()
    {
        RendererHost.Children.Clear();
        var next = new WebView2();
        RendererHost.Children.Add(next);
        RendererView = next;
        return next;
    }

    public void ShowNativeStatus(string code, string message)
        => SetNativeStatus(code, message);

    public void ShowNativeError(string code, string message)
        => SetNativeStatus(code, message);

    private async void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _editor = new EditorSessionController(this, () => _host);
        _host = new WebViewRuntimeHost(this, editor: _editor);
        await _host.InitializeAsync();
    }

    private async void OnOpenProjectClick(object sender, RoutedEventArgs args)
    {
        if (_coreHost?.Session is null)
        {
            ShowNativeError("CORE_UNAVAILABLE", "The Core editor host is not connected.");
            return;
        }

        var path = ProjectPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowNativeError("EDITOR_SESSION_INVALID", "Choose a project folder.");
            return;
        }

        try
        {
            var projectId = await UiCoreConnection.OpenProjectAsync(_coreHost.Session, path, CancellationToken.None);
            _editor?.AttachCore(new IpcEditorCoreClient(_coreHost.Session, Guid.Parse(projectId), path));
            ShowNativeStatus("PROJECT_OPEN", "Project bound.");
        }
        catch (IpcProtocolException exception)
        {
            ShowNativeError(exception.ErrorCode, "Project could not be opened.");
        }
        catch (Exception)
        {
            ShowNativeError("CORE_UNAVAILABLE", "Project could not be opened.");
        }
    }

    private async void OnOpenDraftClick(object sender, RoutedEventArgs args)
    {
        if (_editor is null)
        {
            return;
        }

        try
        {
            await _editor.OpenDraftAsync(ChapterIdBox.Text?.Trim() ?? "", DraftFileBox.Text?.Trim() ?? "", CancellationToken.None);
        }
        catch (IpcProtocolException exception)
        {
            ShowNativeError(exception.ErrorCode, "Draft could not be opened.");
        }
        catch (Exception)
        {
            ShowNativeError("EDITOR_SESSION_INVALID", "Draft could not be opened.");
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        if (_editor is not null)
        {
            await _editor.FlushSaveAsync();
        }
    }

    private void OnRestoreClick(object sender, RoutedEventArgs args) => _editor?.RestoreRecovery();

    private void OnDiscardClick(object sender, RoutedEventArgs args) => _editor?.DiscardRecovery();

    private FrameworkElement RequireRoot()
    {
        if (Content is FrameworkElement root)
        {
            return root;
        }

        throw new InvalidOperationException("The window root is not available.");
    }

    private void SetNativeStatus(string code, string message)
    {
        NativeStatus.Text = code + ": " + message;
        NativeStatus.Visibility = Visibility.Visible;
    }
}
