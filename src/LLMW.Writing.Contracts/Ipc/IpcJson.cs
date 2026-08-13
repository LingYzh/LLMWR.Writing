using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LLMW.Writing.Contracts.Ipc;

public static class IpcJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Serialize<TPayload>(IpcEnvelope<TPayload> envelope, JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);
        IpcFrameHeader.ValidateLength(payload.Length);
        return payload;
    }

    public static IpcEnvelope<TPayload> Deserialize<TPayload>(
        ReadOnlySpan<byte> payload,
        JsonTypeInfo<IpcEnvelope<TPayload>> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        IpcFrameHeader.ValidateLength(payload.Length);

        var json = StrictUtf8.GetString(payload);
        return JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new JsonException("IPC envelope JSON must not be null.");
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(IpcMessageTypeJsonConverter), typeof(IpcClientKindJsonConverter)])]
[JsonSerializable(typeof(IpcEnvelope<HelloRequest>), TypeInfoPropertyName = "HelloRequestEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<HelloAck>), TypeInfoPropertyName = "HelloAckEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<Heartbeat>), TypeInfoPropertyName = "HeartbeatEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<HeartbeatAck>), TypeInfoPropertyName = "HeartbeatAckEnvelope")]
[JsonSerializable(typeof(IpcEnvelope<IpcError>), TypeInfoPropertyName = "ErrorEnvelope")]
public sealed partial class IpcJsonContext : JsonSerializerContext;

public sealed class IpcMessageTypeJsonConverter : JsonStringEnumConverter<IpcMessageType>
{
    public IpcMessageTypeJsonConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

public sealed class IpcClientKindJsonConverter : JsonStringEnumConverter<IpcClientKind>
{
    public IpcClientKindJsonConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}
