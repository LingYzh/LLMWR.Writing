using System.Text;
using System.Text.Json;

namespace LLMW.Writing.UI.WebView;

internal static class BridgeOutboundJson
{
    public static string HostHello(string documentSessionId, string messageId)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.HostHello, writer =>
        {
            writer.WriteString("appName", BridgeProtocol.AppName);
            writer.WriteString("shell", BridgeProtocol.ShellName);
        });

    public static string BridgePong(string documentSessionId, string messageId, string? replyTo, string? nonce)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.BridgePong, writer =>
        {
            if (!string.IsNullOrEmpty(nonce))
            {
                writer.WriteString("nonce", nonce);
            }
        }, replyTo);

    public static string BridgeAck(string documentSessionId, string messageId, string? replyTo, bool accepted)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.BridgeAck, writer =>
        {
            writer.WriteBoolean("accepted", accepted);
        }, replyTo);

    public static string HostStatusReady(string documentSessionId, string messageId)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.HostStatus, writer =>
        {
            writer.WriteString("bridge", "ready");
        });

    public static string BridgeError(string documentSessionId, string messageId, string? replyTo, BridgeError error)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.BridgeError, writer =>
        {
            writer.WriteString("code", error.Code);
            writer.WriteString("message", error.SafeMessage);
        }, replyTo);

    private static string Write(
        string documentSessionId,
        string messageId,
        string semanticType,
        Action<Utf8JsonWriter> writePayload,
        string? replyTo = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", BridgeProtocol.Name);
            writer.WriteNumber("version", BridgeProtocol.Version);
            writer.WriteString("documentSessionId", documentSessionId);
            writer.WriteString("messageId", messageId);
            writer.WriteString("semanticType", semanticType);
            if (!string.IsNullOrEmpty(replyTo))
            {
                writer.WriteString("replyTo", replyTo);
            }

            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writePayload(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
