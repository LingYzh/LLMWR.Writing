using System.IO.Pipes;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Worker;

internal static class Program
{
    private static async Task Main()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMW_UI_BOOTSTRAP_TOKEN")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMW_RUNTIME_BOOTSTRAP_TOKEN")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LLMW_CORE_BOOTSTRAP_TOKEN")))
        {
            throw new InvalidOperationException("A Worker must never inherit a Core bootstrap credential.");
        }

        var workerToken = Environment.GetEnvironmentVariable("LLMW_WORKER_BOOTSTRAP_TOKEN");
        Environment.SetEnvironmentVariable("LLMW_WORKER_BOOTSTRAP_TOKEN", null);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        if (string.IsNullOrWhiteSpace(workerToken))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
            return;
        }

        var workspaceInstanceId = Environment.GetEnvironmentVariable("LLMW_WORKSPACE_INSTANCE_ID")
            ?? throw new InvalidOperationException("Worker requires LLMW_WORKSPACE_INSTANCE_ID.");
        var pipeName = Environment.GetEnvironmentVariable("LLMW_WORKER_PIPE_NAME")
            ?? throw new InvalidOperationException("Worker requires LLMW_WORKER_PIPE_NAME.");
        var runId = Environment.GetEnvironmentVariable("LLMW_RUN_ID");

        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
        await using var session = await IpcClientSession.HandshakeAsync(
                client,
                workspaceInstanceId,
                workerToken,
                IpcClientKind.Worker,
                TimeSpan.FromSeconds(5),
                shutdown.Token)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(runId))
        {
            try
            {
                await session.RequestAsync(
                        IpcSemanticTypes.CreateRunSession,
                        new CreateRunSessionRequest(runId, null),
                        IpcJsonContext.Default.CreateRunSessionRequestEnvelope,
                        IpcJsonContext.Default.CreateRunSessionResponseEnvelope,
                        shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (IpcProtocolException)
            {
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
    }
}
