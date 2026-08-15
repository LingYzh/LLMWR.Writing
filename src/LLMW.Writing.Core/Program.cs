using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Contracts.Ipc;

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

        var eventRing = new IpcEventRing(Guid.NewGuid().ToString("D"));
        var bindings = new TrustedIpcBindingRegistry();
        var commands = new MutableIpcCommandHandler();
        var nativeUi = new TrustedNativePrincipalSource("core-native-ui");
        var runtimeWorkerInstanceId = "runtime-" + Guid.NewGuid().ToString("N");
        var runtimeChannelInstanceId = "runtime-channel-" + Guid.NewGuid().ToString("N");
        var runSessions = new ProjectRunSessionServiceHolder();
        commands.Inner = new CoreOpenProjectHandler(
            commands,
            bindings,
            eventRing,
            workspaceInstanceId,
            runtimeWorkerInstanceId,
            runtimeChannelInstanceId,
            nativeUi,
            runSessions);

        var uiOptions = new IpcServerOptions
        {
            WorkspaceInstanceId = workspaceInstanceId,
            ExpectedClientKind = IpcClientKind.Ui,
            Bootstrap = new IpcBootstrapAuthenticator(uiBootstrapToken),
            EventRing = eventRing,
            Bindings = bindings,
            NativeUi = nativeUi,
            Commands = commands
        };
        var runtimeOptions = new IpcServerOptions
        {
            WorkspaceInstanceId = workspaceInstanceId,
            ExpectedClientKind = IpcClientKind.AgentRuntime,
            Bootstrap = new IpcBootstrapAuthenticator(runtimeBootstrapToken),
            EventRing = eventRing,
            Bindings = bindings,
            Commands = commands,
            RunSessionAccessor = runSessions
        };

        var uiServer = new Ipc.CorePipeServer(
            IpcPipeNames.Core(workspaceInstanceId),
            uiOptions);
        var runtimeServer = new Ipc.CorePipeServer(
            IpcPipeNames.Runtime(workspaceInstanceId),
            runtimeOptions);
        await Task.WhenAll(
                uiServer.RunAsync(shutdown.Token),
                runtimeServer.RunAsync(shutdown.Token))
            .ConfigureAwait(false);
    }
}
