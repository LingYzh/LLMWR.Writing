using System.Diagnostics;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.UI;

/// <summary>
/// Trusted-host launch skeleton. The future WinUI application owns this bootstrapper;
/// no secret is placed in child command-line arguments or exposed to WebView content.
/// </summary>
internal sealed class ProcessBootstrapper
{
    public static LaunchedProcessShells Start(string workspaceInstanceId, string coreAssemblyPath, string runtimeAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(coreAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeAssemblyPath);

        var uiBootstrapToken = IpcBootstrapToken.Create();
        var runtimeBootstrapToken = IpcBootstrapToken.Create();

        var core = StartChild(
            coreAssemblyPath,
            workspaceInstanceId,
            uiBootstrapToken,
            runtimeBootstrapToken);
        try
        {
            var runtime = StartChild(runtimeAssemblyPath, workspaceInstanceId, runtimeBootstrapToken, null);
            return new LaunchedProcessShells(core, runtime);
        }
        catch
        {
            if (!core.HasExited)
            {
                core.Kill(entireProcessTree: true);
            }

            core.Dispose();
            throw;
        }
    }

    private static Process StartChild(
        string assemblyPath,
        string workspaceInstanceId,
        string primaryBootstrapToken,
        string? secondaryBootstrapToken)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{assemblyPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["LLMW_WORKSPACE_INSTANCE_ID"] = workspaceInstanceId;
        startInfo.Environment["LLMW_RUNTIME_BOOTSTRAP_TOKEN"] = primaryBootstrapToken;
        if (secondaryBootstrapToken is not null)
        {
            startInfo.Environment["LLMW_UI_BOOTSTRAP_TOKEN"] = primaryBootstrapToken;
            startInfo.Environment["LLMW_RUNTIME_BOOTSTRAP_TOKEN"] = secondaryBootstrapToken;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("A process shell did not start.");
    }
}

internal sealed class LaunchedProcessShells : IDisposable
{
    private readonly Process _core;
    private readonly Process _runtime;

    public LaunchedProcessShells(Process core, Process runtime)
    {
        _core = core;
        _runtime = runtime;
    }

    public void Dispose()
    {
        Stop(_runtime);
        Stop(_core);
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }

        process.Dispose();
    }
}
