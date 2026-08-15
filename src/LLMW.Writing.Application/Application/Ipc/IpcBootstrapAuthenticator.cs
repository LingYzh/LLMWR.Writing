using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Ipc;

public sealed class IpcBootstrapAuthenticator
{
    private readonly object gate = new();
    private string current;
    private int activeConnections;

    public IpcBootstrapAuthenticator(string initialToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialToken);
        current = initialToken;
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

            var tokenAccepted = IpcBootstrapToken.FixedTimeEquals(current, supplied);
            var kindAccepted = clientKind == expectedKind;
            if (!tokenAccepted || !kindAccepted)
            {
                return BootstrapAuthResult.Rejected();
            }

            var rotated = IpcBootstrapToken.Create();
            current = rotated;
            activeConnections = 1;
            return BootstrapAuthResult.Succeeded(rotated);
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
