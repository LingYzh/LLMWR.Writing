using LLMW.Writing.Application.Ipc;
using LLMW.Writing.UI.Editor;

namespace LLMW.Writing.UI;

internal sealed class CoreHostService : IAsyncDisposable
{
    private LaunchedProcessShells? _shells;
    private IpcClientSession? _session;

    public IpcClientSession? Session => _session;

    public string? WorkspaceInstanceId => _shells?.WorkspaceInstanceId;

    public async Task<IpcClientSession?> StartAsync(CancellationToken cancellationToken)
    {
        if (!HostProcessLocator.TryResolve(out var core, out var runtime))
        {
            return null;
        }

        var workspace = Guid.NewGuid().ToString("N");
        _shells = ProcessBootstrapper.Start(workspace, core, runtime);
        _session = await UiCoreConnection.ConnectAsync(workspace, _shells.UiBootstrapToken, cancellationToken)
            .ConfigureAwait(false);
        return _session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }

        _shells?.Dispose();
        _shells = null;
        GC.SuppressFinalize(this);
    }
}
