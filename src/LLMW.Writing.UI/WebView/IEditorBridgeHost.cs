namespace LLMW.Writing.UI.WebView;

internal interface IEditorBridgeHost
{
    void OnDocumentSessionReady(string documentSessionId);

    void HandleEditorMessage(EditorInboundMessage message, string documentSessionId, string messageId);
}
