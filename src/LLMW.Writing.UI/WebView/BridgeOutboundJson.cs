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

    public static string EditorBind(
        string documentSessionId,
        string messageId,
        string editorSessionId,
        string transferId,
        string format,
        string title,
        bool writable,
        string saveState,
        string? leaseOwnerKind,
        string recovery,
        string lastPersistedDigest)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorBind, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
            writer.WriteString("transferId", transferId);
            writer.WriteString("format", format);
            writer.WriteString("title", title);
            writer.WriteBoolean("writable", writable);
            writer.WriteString("saveState", saveState);
            if (!string.IsNullOrEmpty(leaseOwnerKind))
            {
                writer.WriteString("leaseOwnerKind", leaseOwnerKind);
            }

            writer.WriteString("recovery", recovery);
            writer.WriteString("lastPersistedDigest", lastPersistedDigest);
        });

    public static string EditorDocumentBegin(
        string documentSessionId,
        string messageId,
        string editorSessionId,
        string transferId,
        int totalBytes,
        string sha256,
        int chunkCount)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorDocumentBegin, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
            writer.WriteString("transferId", transferId);
            writer.WriteNumber("totalBytes", totalBytes);
            writer.WriteString("sha256", sha256);
            writer.WriteNumber("count", chunkCount);
        });

    public static string EditorDocumentChunk(
        string documentSessionId,
        string messageId,
        string editorSessionId,
        string transferId,
        int index,
        int count,
        string data)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorDocumentChunk, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
            writer.WriteString("transferId", transferId);
            writer.WriteNumber("index", index);
            writer.WriteNumber("count", count);
            writer.WriteString("data", data);
        });

    public static string EditorDocumentCommit(
        string documentSessionId,
        string messageId,
        string editorSessionId,
        string transferId)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorDocumentCommit, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
            writer.WriteString("transferId", transferId);
        });

    public static string EditorState(
        string documentSessionId,
        string messageId,
        string editorSessionId,
        string saveState,
        bool dirty,
        string lastPersistedDigest,
        long revision)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorState, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
            writer.WriteString("saveState", saveState);
            writer.WriteBoolean("dirty", dirty);
            writer.WriteString("lastPersistedDigest", lastPersistedDigest);
            writer.WriteNumber("revision", revision);
        });

    public static string EditorSaveResult(
        string documentSessionId,
        string messageId,
        string editorSessionId,
        string saveOperationId,
        bool succeeded,
        string? persistedDigest,
        long revision,
        string? code)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorSaveResult, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
            writer.WriteString("saveOperationId", saveOperationId);
            writer.WriteBoolean("succeeded", succeeded);
            if (!string.IsNullOrEmpty(persistedDigest))
            {
                writer.WriteString("persistedDigest", persistedDigest);
            }

            writer.WriteNumber("revision", revision);
            if (!string.IsNullOrEmpty(code))
            {
                writer.WriteString("code", code);
            }
        });

    public static string EditorLeaseState(
        string documentSessionId,
        string messageId,
        string editorSessionId,
        bool writable,
        string? leaseOwnerKind)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorLeaseState, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
            writer.WriteBoolean("writable", writable);
            if (!string.IsNullOrEmpty(leaseOwnerKind))
            {
                writer.WriteString("leaseOwnerKind", leaseOwnerKind);
            }
        });

    public static string EditorRecoveryOffer(string documentSessionId, string messageId, string editorSessionId)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorRecoveryOffer, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
        });

    public static string EditorRecoveryConflict(string documentSessionId, string messageId, string editorSessionId)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorRecoveryConflict, writer =>
        {
            writer.WriteString("editorSessionId", editorSessionId);
        });

    public static string EditorError(
        string documentSessionId,
        string messageId,
        string? replyTo,
        string code,
        string safeMessage)
        => Write(documentSessionId, messageId, BridgeSemanticTypes.EditorError, writer =>
        {
            writer.WriteString("code", code);
            writer.WriteString("message", SafeText.Truncate(safeMessage, BridgeProtocol.MaximumSafeErrorChars));
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
