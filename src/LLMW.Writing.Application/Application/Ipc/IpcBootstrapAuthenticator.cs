using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public sealed class IpcBootstrapAuthenticator
{
    private readonly object gate = new();
    private string current;
    private string? pending;
    private int activeConnections;

    public IpcBootstrapAuthenticator(string initialToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialToken);
        current = initialToken;
    }

    public bool HasUnconfirmedRotation
    {
        get
        {
            lock (gate)
            {
                return pending is not null;
            }
        }
    }

    public bool HasActiveConnection
    {
        get
        {
            lock (gate)
            {
                return activeConnections > 0;
            }
        }
    }

    public BootstrapAuthResult Authenticate(string supplied, IpcClientKind clientKind, IpcClientKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        lock (gate)
        {
            if (activeConnections > 0)
            {
                return BootstrapAuthResult.ReplayDetected();
            }

            var kindAccepted = clientKind == expectedKind;
            var currentAccepted = IpcBootstrapToken.FixedTimeEquals(current, supplied);
            var pendingAccepted = pending is not null && IpcBootstrapToken.FixedTimeEquals(pending, supplied);
            if (!kindAccepted || (!currentAccepted && !pendingAccepted))
            {
                return BootstrapAuthResult.Rejected();
            }

            pending = IpcBootstrapToken.Create();
            activeConnections = 1;
            return BootstrapAuthResult.Succeeded(pending);
        }
    }

    public void Confirm()
    {
        lock (gate)
        {
            if (pending is null)
            {
                return;
            }

            current = pending;
            pending = null;
        }
    }

    public void Release()
    {
        lock (gate)
        {
            activeConnections = 0;
        }
    }
}

public sealed record BootstrapAuthResult(bool Accepted, bool Replay, string? RotatedToken)
{
    public static BootstrapAuthResult Succeeded(string rotatedToken) => new(true, false, rotatedToken);

    public static BootstrapAuthResult Rejected() => new(false, false, null);

    public static BootstrapAuthResult ReplayDetected() => new(false, true, null);
}
