using System.Security.Cryptography;
using System.Text.Json;

namespace LLMW.Writing.Contracts.Ipc;

/// <summary>
/// Stable protocol constants for the v1 Core IPC boundary.
/// </summary>
public static class IpcProtocol
{
    public const string Name = "llmw-writing-ipc/1";
    public const int Version1 = 1;
    public const int MinimumSupportedVersion = Version1;
    public const int MaximumSupportedVersion = Version1;
    public const int MaximumFrameBytes = 1024 * 1024;
    public const int BootstrapTokenMinimumBits = 256;

    public static bool TryNegotiate(int clientMinimum, int clientMaximum, out int negotiatedVersion)
    {
        negotiatedVersion = 0;

        if (clientMinimum <= 0 || clientMaximum < clientMinimum)
        {
            return false;
        }

        var minimum = Math.Max(clientMinimum, MinimumSupportedVersion);
        var maximum = Math.Min(clientMaximum, MaximumSupportedVersion);
        if (minimum > maximum)
        {
            return false;
        }

        negotiatedVersion = maximum;
        return true;
    }
}

/// <summary>
/// Names for the two Core-owned named-pipe endpoints in a workspace instance.
/// </summary>
public static class IpcPipeNames
{
    private const string AppId = "writing";

    public static string Core(string workspaceInstanceId) => Create(workspaceInstanceId, "core");

    public static string Runtime(string workspaceInstanceId) => Create(workspaceInstanceId, "runtime");

    private static string Create(string workspaceInstanceId, string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);

        if (workspaceInstanceId.Length > 96 ||
            workspaceInstanceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException(
                "Workspace instance IDs used in pipe names may contain only ASCII letters, digits, and hyphens.",
                nameof(workspaceInstanceId));
        }

        return $"llmw-{AppId}-{workspaceInstanceId}-{endpoint}";
    }
}

/// <summary>
/// A v1 message envelope. Payloads are transport contracts, never Domain entities.
/// </summary>
public sealed record IpcEnvelope<TPayload>(
    int ProtocolVersion,
    IpcMessageType MessageType,
    Guid RequestId,
    Guid CorrelationId,
    Guid? ProjectId,
    string WorkspaceInstanceId,
    Guid? RunId,
    long TimestampMs,
    TPayload Payload);

public enum IpcMessageType
{
    Request,
    Response,
    Event,
    Control
}

public enum IpcClientKind
{
    Ui,
    AgentRuntime,
    Worker
}

public sealed record HelloRequest(
    int ProtocolMin,
    int ProtocolMax,
    string BootstrapToken,
    IpcClientKind ClientKind,
    Guid ProcessInstanceId);

public sealed record HelloAck(int NegotiatedProtocol, string[] ServerCapabilities);

public sealed record Heartbeat(long Sequence);

public sealed record HeartbeatAck(long Sequence);

public sealed record IpcError(
    string Code,
    string Message,
    JsonElement? Details,
    bool Retryable);

public static class IpcErrorCodes
{
    public const string AuthBootstrapRejected = "AUTH_BOOTSTRAP_REJECTED";
    public const string InvalidFrame = "IPC_INVALID_FRAME";
    public const string MalformedFrame = "IPC_MALFORMED_FRAME";
    public const string ProtocolNoCommonVersion = "IPC_PROTOCOL_NO_COMMON_VERSION";
    public const string UnexpectedMessage = "IPC_UNEXPECTED_MESSAGE";
}

public static class IpcEnvelopeFactory
{
    public static IpcEnvelope<TPayload> Create<TPayload>(
        IpcMessageType messageType,
        string workspaceInstanceId,
        TPayload payload,
        Guid? projectId = null,
        Guid? runId = null,
        Guid? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);

        var requestId = IpcMessageIds.Create();
        return new IpcEnvelope<TPayload>(
            IpcProtocol.Version1,
            messageType,
            requestId,
            correlationId ?? requestId,
            projectId,
            workspaceInstanceId,
            runId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload);
    }
}

/// <summary>
/// UUIDv7 identifiers make IPC traces time-sortable without coupling contracts to a persistence implementation.
/// </summary>
public static class IpcMessageIds
{
    public static Guid Create()
    {
        Span<byte> guidBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(guidBytes);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = 5; index >= 0; index--)
        {
            guidBytes[index] = (byte)timestamp;
            timestamp >>= 8;
        }

        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x70);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes, bigEndian: true);
    }
}

/// <summary>
/// Prefix encoding is a pure contract helper; stream transport stays outside Contracts.
/// </summary>
public static class IpcFrameHeader
{
    public static byte[] Create(int payloadLength)
    {
        ValidateLength(payloadLength);
        return BitConverter.GetBytes(payloadLength);
    }

    public static int Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length != sizeof(int))
        {
            throw new ArgumentException("An IPC frame header must contain exactly four bytes.", nameof(header));
        }

        var payloadLength = BitConverter.ToInt32(header);
        ValidateLength(payloadLength);
        return payloadLength;
    }

    public static void ValidateLength(int payloadLength)
    {
        if (payloadLength <= 0 || payloadLength > IpcProtocol.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                payloadLength,
                $"IPC frames must be between 1 and {IpcProtocol.MaximumFrameBytes} bytes.");
        }
    }
}
