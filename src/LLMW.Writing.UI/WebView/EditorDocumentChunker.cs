using System.Security.Cryptography;
using System.Text;
using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.UI.WebView;

internal sealed record EditorDocumentChunks(
    string TransferId,
    int TotalBytes,
    string Sha256,
    int ChunkCount,
    IReadOnlyList<string> Base64Chunks);

internal static class EditorDocumentChunker
{
    public static EditorDocumentChunks Split(string logicalText, string? transferId = null)
    {
        ArgumentNullException.ThrowIfNull(logicalText);
        var utf8 = Encoding.UTF8.GetBytes(logicalText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal));
        if (utf8.Length > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            throw new InvalidOperationException(IpcErrorCodes.EditorDocumentTooLarge);
        }

        var digest = Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
        var chunks = new List<string>();
        var offset = 0;
        while (offset < utf8.Length)
        {
            var length = Math.Min(EditorTransportLimits.MaximumChunkUtf8Bytes, utf8.Length - offset);
            chunks.Add(Convert.ToBase64String(utf8, offset, length));
            offset += length;
        }

        return new EditorDocumentChunks(
            transferId ?? Guid.NewGuid().ToString("D"),
            utf8.Length,
            digest,
            chunks.Count,
            chunks);
    }

    public static IReadOnlyList<string> ToOutbound(
        string documentSessionId,
        string editorSessionId,
        EditorDocumentChunks chunks)
    {
        var messages = new List<string>(chunks.ChunkCount + 2)
        {
            BridgeOutboundJson.EditorDocumentBegin(
                documentSessionId,
                Guid.NewGuid().ToString("D"),
                editorSessionId,
                chunks.TransferId,
                chunks.TotalBytes,
                chunks.Sha256,
                chunks.ChunkCount)
        };

        for (var index = 0; index < chunks.Base64Chunks.Count; index++)
        {
            messages.Add(BridgeOutboundJson.EditorDocumentChunk(
                documentSessionId,
                Guid.NewGuid().ToString("D"),
                editorSessionId,
                chunks.TransferId,
                index,
                chunks.ChunkCount,
                chunks.Base64Chunks[index]));
        }

        messages.Add(BridgeOutboundJson.EditorDocumentCommit(
            documentSessionId,
            Guid.NewGuid().ToString("D"),
            editorSessionId,
            chunks.TransferId));
        return messages;
    }
}
