using Microsoft.UI.Xaml;

namespace LLMW.Writing.UI;

public partial class App : Microsoft.UI.Xaml.Application, IAsyncDisposable
{
    private Window? _window;
    private CoreHostService? _coreHost;

    public App()
    {
        InitializeComponent();
    }

    internal CoreHostService? CoreHost => _coreHost;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _coreHost = new CoreHostService();
        try
        {
            await _coreHost.StartAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Renderer still loads; native chrome reports CORE_UNAVAILABLE when a Draft is opened.
        }

        _window = new MainWindow(_coreHost);
        _window.Closed += OnMainWindowClosed;
        _window.Activate();
    }

    public async ValueTask DisposeAsync()
    {
        if (_coreHost is not null)
        {
            await _coreHost.DisposeAsync().ConfigureAwait(false);
            _coreHost = null;
        }

        GC.SuppressFinalize(this);
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        await DisposeAsync().ConfigureAwait(true);
    }
}
