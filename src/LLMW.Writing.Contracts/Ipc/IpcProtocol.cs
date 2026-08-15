using System.Buffers.Binary;
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
    public const int SubscriberRingCapacity = 256;
    public const int CriticalOutboundCapacity = 64;
    public const int SnapshotOutboundCapacity = 8;
    public const int MaximumInFlightRequests = 32;
    public const int DefaultHeartbeatIntervalMs = 5000;
    public const int MissedHeartbeatsBeforeEvict = 3;
    public const int DefaultRequestTimeoutMs = 30_000;
    public const int ReconnectInitialBackoffMs = 100;
    public const int ReconnectMaximumBackoffMs = 5000;
    public const int FirstEventSequence = 1;
    public const long EmptySnapshotSequence = 0;
    public const int ClientEventBufferCapacity = 32;
    public const int WriteTimeoutMs = 2000;
    public const int DrainTimeoutMs = 2000;

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
/// Names for Core-owned named-pipe endpoints in a workspace instance.
/// Worker endpoints are per Core-owned launch binding and admit one connection each.
/// </summary>
public static class IpcPipeNames
{
    private const string AppId = "writing";

    public static string Core(string workspaceInstanceId) => Create(workspaceInstanceId, "core");

    public static string Runtime(string workspaceInstanceId) => Create(workspaceInstanceId, "runtime");

    public static string Worker(string workspaceInstanceId, string launchBindingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchBindingId);
        if (launchBindingId.Length != 16 ||
            launchBindingId.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "Worker launch binding IDs used in pipe names must be 16 ASCII hex characters.",
                nameof(launchBindingId));
        }

        return Create(workspaceInstanceId, "w-" + launchBindingId.ToLowerInvariant());
    }

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
/// <paramref name="SemanticType"/> is the stable operation/event discriminator.
/// </summary>
public sealed record IpcEnvelope<TPayload>(
    int ProtocolVersion,
    IpcMessageType MessageType,
    string SemanticType,
    Guid RequestId,
    Guid CorrelationId,
    Guid? ProjectId,
    string WorkspaceInstanceId,
    Guid? RunId,
    long TimestampMs,
    TPayload Payload);

/// <summary>
/// Wire envelope used to read the discriminator before selecting a typed payload.
/// </summary>
public sealed record IpcWireEnvelope(
    int ProtocolVersion,
    IpcMessageType MessageType,
    string SemanticType,
    Guid RequestId,
    Guid CorrelationId,
    Guid? ProjectId,
    string WorkspaceInstanceId,
    Guid? RunId,
    long TimestampMs,
    JsonElement Payload);

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
    Guid ProcessInstanceId)
{
    public override string ToString() =>
        $"HelloRequest {{ ProtocolMin = {ProtocolMin}, ProtocolMax = {ProtocolMax}, BootstrapToken = [REDACTED], ClientKind = {ClientKind}, ProcessInstanceId = {ProcessInstanceId} }}";
}

public sealed record HelloAck(
    int NegotiatedProtocol,
    string[] ServerCapabilities,
    string EventStreamId,
    string ConnectionId,
    string? RotatedBootstrapToken)
{
    public override string ToString() =>
        $"HelloAck {{ NegotiatedProtocol = {NegotiatedProtocol}, ServerCapabilities = {ServerCapabilities.Length}, EventStreamId = {EventStreamId}, ConnectionId = {ConnectionId}, RotatedBootstrapToken = [REDACTED] }}";
}

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
    public const string AuthBootstrapReplay = "AUTH_BOOTSTRAP_REPLAY";
    public const string InvalidFrame = "IPC_INVALID_FRAME";
    public const string MalformedFrame = "IPC_MALFORMED_FRAME";
    public const string ProtocolNoCommonVersion = "IPC_PROTOCOL_NO_COMMON_VERSION";
    public const string UnexpectedMessage = "IPC_UNEXPECTED_MESSAGE";
    public const string UnsupportedSemanticType = "IPC_UNSUPPORTED_SEMANTIC_TYPE";
    public const string DuplicateRequest = "IPC_DUPLICATE_REQUEST";
    public const string UnknownCorrelation = "IPC_UNKNOWN_CORRELATION";
    public const string QueueOverload = "IPC_QUEUE_OVERLOAD";
    public const string ResyncRequired = "IPC_RESYNC_REQUIRED";
    public const string CommandUnavailable = "IPC_COMMAND_UNAVAILABLE";
    public const string ProtocolViolation = "IPC_PROTOCOL_VIOLATION";
    public const string Cancelled = "IPC_CANCELLED";
    public const string InvalidSession = "SECURITY_INVALID_SESSION";
    public const string SessionExpired = "SECURITY_SESSION_EXPIRED";
    public const string SessionRevoked = "SECURITY_SESSION_REVOKED";
    public const string BindingMismatch = "SECURITY_BINDING_MISMATCH";
    public const string TrustedBindingUnavailable = "SECURITY_TRUSTED_BINDING_UNAVAILABLE";
    public const string RuntimeManagementDenied = "SECURITY_RUNTIME_MANAGEMENT_DENIED";
    public const string AgentSpawnDenied = "AGENT_SPAWN_DENIED";
    public const string AgentDepthLimit = "AGENT_DEPTH_LIMIT";
    public const string AgentDepthSpoof = "AGENT_DEPTH_SPOOF";
    public const string AgentUnknownSideEffect = "AGENT_UNKNOWN_SIDE_EFFECT";
    public const string AgentCheckpointUnsupported = "AGENT_CHECKPOINT_UNSUPPORTED";
    public const string AgentIllegalTransition = "AGENT_ILLEGAL_TRANSITION";
}

public static class IpcServerCapabilities
{
    public static readonly string[] V1 = ["heartbeat", "multiplex", "snapshot", "cancel", "events"];
}

public static class IpcEnvelopeFactory
{
    public static IpcEnvelope<TPayload> Create<TPayload>(
        IpcMessageType messageType,
        string semanticType,
        string workspaceInstanceId,
        TPayload payload,
        Guid? projectId = null,
        Guid? runId = null,
        Guid? correlationId = null,
        Guid? requestId = null,
        long? timestampMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticType);

        var id = requestId ?? IpcMessageIds.Create();
        return new IpcEnvelope<TPayload>(
            IpcProtocol.Version1,
            messageType,
            semanticType,
            id,
            correlationId ?? id,
            projectId,
            workspaceInstanceId,
            runId,
            timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
/// Length is an unsigned 32-bit little-endian integer and is rejected before a body is allocated.
/// </summary>
public static class IpcFrameHeader
{
    public const int Size = sizeof(uint);

    public static byte[] Create(int payloadLength)
    {
        ValidateLength(payloadLength);
        var header = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        return header;
    }

    public static int Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length != Size)
        {
            throw new ArgumentException("An IPC frame header must contain exactly four bytes.", nameof(header));
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (payloadLength == 0 || payloadLength > IpcProtocol.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(header),
                payloadLength,
                $"IPC frames must be between 1 and {IpcProtocol.MaximumFrameBytes} bytes.");
        }

        return (int)payloadLength;
    }

    public static bool TryParse(ReadOnlySpan<byte> header, out int payloadLength, out string? errorCode)
    {
        payloadLength = 0;
        errorCode = null;
        if (header.Length != Size)
        {
            errorCode = IpcErrorCodes.MalformedFrame;
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > IpcProtocol.MaximumFrameBytes)
        {
            errorCode = IpcErrorCodes.InvalidFrame;
            return false;
        }

        payloadLength = (int)length;
        return true;
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
