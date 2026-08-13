namespace LLMW.Writing.Core;

internal static class Program
{
    private static async Task Main()
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        var workspaceInstanceId = Environment.GetEnvironmentVariable("LLMW_WORKSPACE_INSTANCE_ID") ?? "development";
        var uiBootstrapToken = Environment.GetEnvironmentVariable("LLMW_UI_BOOTSTRAP_TOKEN");
        var runtimeBootstrapToken = Environment.GetEnvironmentVariable("LLMW_RUNTIME_BOOTSTRAP_TOKEN");
        Environment.SetEnvironmentVariable("LLMW_UI_BOOTSTRAP_TOKEN", null);
        Environment.SetEnvironmentVariable("LLMW_RUNTIME_BOOTSTRAP_TOKEN", null);
        if (string.IsNullOrWhiteSpace(uiBootstrapToken) || string.IsNullOrWhiteSpace(runtimeBootstrapToken))
        {
            throw new InvalidOperationException("Core requires separate launcher-provided UI and Agent Runtime bootstrap tokens.");
        }

        var uiServer = new Ipc.CorePipeServer(
            LLMW.Writing.Contracts.Ipc.IpcPipeNames.Core(workspaceInstanceId),
            LLMW.Writing.Contracts.Ipc.IpcClientKind.Ui,
            uiBootstrapToken);
        var runtimeServer = new Ipc.CorePipeServer(
            LLMW.Writing.Contracts.Ipc.IpcPipeNames.Runtime(workspaceInstanceId),
            LLMW.Writing.Contracts.Ipc.IpcClientKind.AgentRuntime,
            runtimeBootstrapToken);
        await Task.WhenAll(
                uiServer.RunAsync(shutdown.Token),
                runtimeServer.RunAsync(shutdown.Token))
            .ConfigureAwait(false);
    }
}
