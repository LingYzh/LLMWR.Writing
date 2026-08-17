using Microsoft.UI.Xaml;
using LLMW.Writing.UI.Hosting;

namespace LLMW.Writing.UI;

public sealed partial class MainWindow : Window
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

    private async void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        if (_started || Content is not FrameworkElement root)
        {
            return;
        }

        _started = true;
        _host = new WebViewRuntimeHost(RendererView, NativeStatus, root.XamlRoot);
        await _host.InitializeAsync();
    }
}
