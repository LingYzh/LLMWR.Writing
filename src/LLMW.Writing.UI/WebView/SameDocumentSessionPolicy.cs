namespace LLMW.Writing.UI.WebView;

internal enum SameDocumentSourceChangeAction
{
    InvalidateOnly = 0,
    BeginNewSession = 1
}

internal static class SameDocumentSessionPolicy
{
    public static SameDocumentSourceChangeAction Evaluate(bool isNewDocument, bool currentSourceIsApplicationDocument)
    {
        if (isNewDocument)
        {
            return SameDocumentSourceChangeAction.InvalidateOnly;
        }

        return currentSourceIsApplicationDocument
            ? SameDocumentSourceChangeAction.BeginNewSession
            : SameDocumentSourceChangeAction.InvalidateOnly;
    }
}
