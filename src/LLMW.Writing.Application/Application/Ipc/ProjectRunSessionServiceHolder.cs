using LLMW.Writing.Application.Security;

namespace LLMW.Writing.Application.Ipc;

/// <summary>
/// Core-owned late binding for the project RunSession authority. Runtime IPC options
/// reference this holder before OpenProject; the live current service is resolved per
/// request so a pre-OpenProject connection is not stuck with a null snapshot.
/// </summary>
public interface IRunSessionServiceAccessor
{
    RunSessionService? Current { get; }
}

public sealed class ProjectRunSessionServiceHolder : IRunSessionServiceAccessor
{
    private RunSessionService? current;

    public RunSessionService? Current => Volatile.Read(ref current);

    public void PublishOnce(RunSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref current, service, null) is not null)
        {
            throw new InvalidOperationException("Project RunSession authority is already published.");
        }
    }

    public bool TryAbandon(RunSessionService expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ReferenceEquals(Interlocked.CompareExchange(ref current, null, expected), expected);
    }
}
