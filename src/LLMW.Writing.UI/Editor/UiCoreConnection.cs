using System.IO.Pipes;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.UI.Editor;

internal static class HostProcessLocator
{
    public static bool TryResolve(out string coreAssembly, out string runtimeAssembly)
    {
        coreAssembly = "";
        runtimeAssembly = "";
        var envCore = Environment.GetEnvironmentVariable("LLMW_CORE_ASSEMBLY");
        var envRuntime = Environment.GetEnvironmentVariable("LLMW_RUNTIME_ASSEMBLY");
        if (!string.IsNullOrWhiteSpace(envCore)
            && !string.IsNullOrWhiteSpace(envRuntime)
            && File.Exists(envCore)
            && File.Exists(envRuntime))
        {
            coreAssembly = envCore;
            runtimeAssembly = envRuntime;
            return true;
        }

        var configuration = InferConfiguration(AppContext.BaseDirectory);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var core = Path.Combine(directory.FullName, "src", "LLMW.Writing.Core", "bin", configuration, "net8.0", "LLMW.Writing.Core.dll");
            var runtime = Path.Combine(directory.FullName, "src", "LLMW.Writing.AgentRuntime", "bin", configuration, "net8.0", "LLMW.Writing.AgentRuntime.dll");
            if (File.Exists(core) && File.Exists(runtime))
            {
                coreAssembly = core;
                runtimeAssembly = runtime;
                return true;
            }
        }

        return false;
    }

    private static string InferConfiguration(string baseDirectory)
    {
        if (baseDirectory.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || baseDirectory.EndsWith(Path.DirectorySeparatorChar + "Debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        return "Release";
    }
}

internal static class UiCoreConnection
{
    public static async Task<IpcClientSession> ConnectAsync(
        string workspaceInstanceId,
        string bootstrapToken,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new NamedPipeClientStream(
                ".",
                IpcPipeNames.Core(workspaceInstanceId),
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(400), cancellationToken)
                    .ConfigureAwait(false);
                return await IpcClientSession.HandshakeAsync(
                        client,
                        workspaceInstanceId,
                        bootstrapToken,
                        IpcClientKind.Ui,
                        TimeSpan.FromSeconds(5),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or IpcProtocolException)
            {
                last = exception;
                await client.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Timed out connecting to Core: " + last);
    }

    public static async Task<string> OpenProjectAsync(
        IpcClientSession session,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var opened = await session.RequestAsync(
                IpcSemanticTypes.OpenProject,
                new OpenProjectRequest(projectRoot),
                IpcJsonContext.Default.OpenProjectRequestEnvelope,
                IpcJsonContext.Default.OpenProjectResponseEnvelope,
                cancellationToken)
            .ConfigureAwait(false);
        return opened.Payload.ProjectId;
    }
}
