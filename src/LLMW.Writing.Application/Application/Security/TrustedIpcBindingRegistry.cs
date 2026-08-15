namespace LLMW.Writing.Application.Security;

/// <summary>
/// Core-owned launch/composition record. This is not an IPC payload and is not caller-writable.
/// </summary>
public sealed record TrustedIpcLaunchRecord(
    AuthenticatedClientKind ClientKind,
    string WorkerInstanceId,
    string ChannelInstanceId,
    ProjectScope ProjectScope);

public interface ITrustedIpcBindingRegistry
{
    void Register(TrustedIpcLaunchRecord record);

    bool TryBind(AuthenticatedClientKind clientKind, out AuthenticatedChannelContext context);

    void Unregister(AuthenticatedClientKind clientKind);
}

public sealed class TrustedIpcBindingRegistry : ITrustedIpcBindingRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<AuthenticatedClientKind, TrustedIpcLaunchRecord> records = [];

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
            records[record.ClientKind] = record;
        }
    }

    public bool TryBind(AuthenticatedClientKind clientKind, out AuthenticatedChannelContext context)
    {
        lock (gate)
        {
            if (!records.TryGetValue(clientKind, out var record))
            {
                context = null!;
                return false;
            }

            context = new AuthenticatedChannelContext(
                record.ChannelInstanceId,
                record.ClientKind,
                record.WorkerInstanceId,
                record.ProjectScope);
            return true;
        }
    }

    public void Unregister(AuthenticatedClientKind clientKind)
    {
        lock (gate)
        {
            records.Remove(clientKind);
        }
    }
}
