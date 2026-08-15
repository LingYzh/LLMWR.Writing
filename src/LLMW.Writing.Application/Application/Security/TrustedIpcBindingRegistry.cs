using LLMW.Writing.Application.Security;

namespace LLMW.Writing.Application.Security;

public sealed record TrustedIpcLaunchRecord(
    AuthenticatedClientKind ClientKind,
    string WorkerInstanceId,
    string ChannelInstanceId,
    ProjectScope ProjectScope,
    string? LaunchBindingId = null,
    string? BoundRunId = null)
{
    public string BindingId => string.IsNullOrWhiteSpace(LaunchBindingId) ? ChannelInstanceId : LaunchBindingId;
}

public interface ITrustedIpcBindingRegistry
{
    void Register(TrustedIpcLaunchRecord record);

    bool TryBind(AuthenticatedClientKind clientKind, out AuthenticatedChannelContext context);

    bool TryBind(string launchBindingId, AuthenticatedClientKind expectedKind, out AuthenticatedChannelContext context);

    void Unregister(AuthenticatedClientKind clientKind);

    void Unregister(string launchBindingId);
}

public sealed class TrustedIpcBindingRegistry : ITrustedIpcBindingRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, TrustedIpcLaunchRecord> records = new(StringComparer.Ordinal);
    private string? runtimeBindingId;

    public void Register(TrustedIpcLaunchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.WorkerInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ChannelInstanceId);
        ArgumentNullException.ThrowIfNull(record.ProjectScope);
        if (record.ClientKind is not (AuthenticatedClientKind.AgentRuntime or AuthenticatedClientKind.Worker))
        {
            throw new ArgumentException("Trusted IPC launch records are only defined for Runtime/Worker channels.");
        }

        lock (gate)
        {
            records[record.BindingId] = record;
            if (record.ClientKind == AuthenticatedClientKind.AgentRuntime)
            {
                runtimeBindingId = record.BindingId;
            }
        }
    }

    public bool TryBind(AuthenticatedClientKind clientKind, out AuthenticatedChannelContext context)
    {
        lock (gate)
        {
            if (clientKind == AuthenticatedClientKind.AgentRuntime &&
                runtimeBindingId is not null &&
                records.TryGetValue(runtimeBindingId, out var runtime))
            {
                context = ToContext(runtime);
                return true;
            }

            context = null!;
            return false;
        }
    }

    public bool TryBind(string launchBindingId, AuthenticatedClientKind expectedKind, out AuthenticatedChannelContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchBindingId);
        lock (gate)
        {
            if (!records.TryGetValue(launchBindingId, out var record) || record.ClientKind != expectedKind)
            {
                context = null!;
                return false;
            }

            context = ToContext(record);
            return true;
        }
    }

    public void Unregister(AuthenticatedClientKind clientKind)
    {
        lock (gate)
        {
            if (clientKind != AuthenticatedClientKind.AgentRuntime || runtimeBindingId is null)
            {
                return;
            }

            records.Remove(runtimeBindingId);
            runtimeBindingId = null;
        }
    }

    public void Unregister(string launchBindingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchBindingId);
        lock (gate)
        {
            if (records.Remove(launchBindingId) && StringComparer.Ordinal.Equals(runtimeBindingId, launchBindingId))
            {
                runtimeBindingId = null;
            }
        }
    }

    private static AuthenticatedChannelContext ToContext(TrustedIpcLaunchRecord record) =>
        new(record.ChannelInstanceId, record.ClientKind, record.WorkerInstanceId, record.ProjectScope, record.BoundRunId);
}
