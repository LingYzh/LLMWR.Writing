using System.IO.Pipes;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Core.Ipc;

internal sealed class CorePipeServer
{
    private readonly string _pipeName;
    private readonly IpcServerOptions _options;

    public CorePipeServer(string pipeName, IpcServerOptions options)
    {
        _pipeName = pipeName;
        _options = options;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await IpcServerSession.ServeAsync(server, _options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }
}
