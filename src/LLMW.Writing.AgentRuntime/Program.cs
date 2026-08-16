using LLMW.Writing.Application.Ipc;
using LLMW.Writing.Application.Provider;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.AgentRuntime;

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
        var bootstrapToken = Environment.GetEnvironmentVariable("LLMW_RUNTIME_BOOTSTRAP_TOKEN");
        Environment.SetEnvironmentVariable("LLMW_RUNTIME_BOOTSTRAP_TOKEN", null);
        if (string.IsNullOrWhiteSpace(bootstrapToken))
        {
            throw new InvalidOperationException("Agent Runtime requires a launcher-provided runtime bootstrap token.");
        }

        var heartbeatInterval = GetHeartbeatInterval();
        var client = new Ipc.RuntimePipeClient(workspaceInstanceId, bootstrapToken, heartbeatInterval);
        await client.RunWithReconnectAsync(shutdown.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// WP14 composition seam: authenticated WP11 session + AgentRuntime-owned credentials.
    /// Does not take IRuntimePersistence or SqliteRuntimeStore.
    /// </summary>
    internal static ProviderInvocationCoordinator CreateProviderCoordinator(
        IpcClientSession session,
        RunSessionProof proof,
        IProviderDefinitionStore definitions,
        IProviderCredentialResolver credentials,
        IModelCertificationStore protocolProfiles,
        IPriceSnapshotStore prices,
        IProviderAdapterResolver adapters,
        IModelCatalogStore? catalog = null,
        ITaskCertificationStore? taskCertifications = null) =>
        ProviderInvocationRuntimeSeam.Create(
            session,
            proof,
            definitions,
            credentials,
            protocolProfiles,
            prices,
            adapters,
            catalog,
            taskCertifications);

    private static TimeSpan GetHeartbeatInterval()
    {
        const int defaultIntervalMilliseconds = 5000;
        var configured = Environment.GetEnvironmentVariable("LLMW_HEARTBEAT_INTERVAL_MS");
        return int.TryParse(configured, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.FromMilliseconds(defaultIntervalMilliseconds);
    }
}
