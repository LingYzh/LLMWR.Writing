using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LLMW.Writing.UI.Hosting;

namespace LLMW.Writing.UI;

public sealed partial class MainWindow : Window, IWebViewRendererSite
{
    private WebViewRuntimeHost? _host;
    private bool _started;

    public MainWindow()
    {
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
        _host = new WebViewRuntimeHost(this);
        await _host.InitializeAsync();
    }

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
