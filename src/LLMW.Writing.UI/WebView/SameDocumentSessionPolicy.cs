namespace LLMW.Writing.UI.WebView;

internal enum SameDocumentSourceChangeAction
{
    IgnoreNewDocument = 0,
    InvalidateOnly = 1,
    BeginNewSession = 2
}

internal static class SameDocumentSessionPolicy
{
    public static SameDocumentSourceChangeAction Evaluate(bool isNewDocument, bool currentSourceIsApplicationDocument)
    {
        if (isNewDocument)
        {
            return SameDocumentSourceChangeAction.IgnoreNewDocument;
        }

        return currentSourceIsApplicationDocument
            ? SameDocumentSourceChangeAction.BeginNewSession
            : SameDocumentSourceChangeAction.InvalidateOnly;
    }
}
