namespace LLMW.Writing.Application.Editor;

public static class EditorSaveFaultEnvironment
{
    public static IEditorSaveFaultInjector FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("LLMW_EDITOR_SAVE_FAULT");
        if (string.IsNullOrWhiteSpace(raw)
            || !Enum.TryParse<EditorSaveFaultPoint>(raw, ignoreCase: true, out var point)
            || point == EditorSaveFaultPoint.None)
        {
            return NoEditorSaveFaultInjector.Instance;
        }

        return new MutableEditorSaveFaultInjector { Fault = point };
    }
}
