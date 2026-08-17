namespace LLMW.Writing.UI.WebView;

internal enum EditorSaveUiState
{
    Clean = 0,
    Dirty = 1,
    SavePending = 2,
    Saving = 3,
    SaveFailed = 4,
    StaleBase = 5,
    ReadOnlyLeased = 6,
    RecoveryAvailable = 7,
    RecoveryConflict = 8
}

internal sealed class EditorSaveStateMachine
{
    public EditorSaveUiState State { get; private set; } = EditorSaveUiState.Clean;

    public string WireName => ToWire(State);

    public bool TryTransition(EditorSaveUiState next)
    {
        if (!IsLegal(State, next))
        {
            return false;
        }

        State = next;
        return true;
    }

    public void Force(EditorSaveUiState next) => State = next;

    public static bool IsLegal(EditorSaveUiState from, EditorSaveUiState to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            EditorSaveUiState.Clean => to is EditorSaveUiState.Dirty
                or EditorSaveUiState.StaleBase
                or EditorSaveUiState.ReadOnlyLeased
                or EditorSaveUiState.RecoveryAvailable
                or EditorSaveUiState.RecoveryConflict,
            EditorSaveUiState.Dirty => to is EditorSaveUiState.SavePending
                or EditorSaveUiState.Saving
                or EditorSaveUiState.StaleBase
                or EditorSaveUiState.ReadOnlyLeased
                or EditorSaveUiState.SaveFailed
                or EditorSaveUiState.RecoveryAvailable
                or EditorSaveUiState.RecoveryConflict,
            EditorSaveUiState.SavePending => to is EditorSaveUiState.Saving
                or EditorSaveUiState.Dirty
                or EditorSaveUiState.StaleBase
                or EditorSaveUiState.ReadOnlyLeased
                or EditorSaveUiState.RecoveryConflict,
            EditorSaveUiState.Saving => to is EditorSaveUiState.Clean
                or EditorSaveUiState.Dirty
                or EditorSaveUiState.SaveFailed
                or EditorSaveUiState.StaleBase
                or EditorSaveUiState.ReadOnlyLeased
                or EditorSaveUiState.RecoveryConflict,
            EditorSaveUiState.SaveFailed => to is EditorSaveUiState.Dirty
                or EditorSaveUiState.Saving
                or EditorSaveUiState.StaleBase
                or EditorSaveUiState.ReadOnlyLeased
                or EditorSaveUiState.RecoveryAvailable
                or EditorSaveUiState.RecoveryConflict,
            EditorSaveUiState.StaleBase => to is EditorSaveUiState.Clean
                or EditorSaveUiState.RecoveryConflict
                or EditorSaveUiState.ReadOnlyLeased,
            EditorSaveUiState.ReadOnlyLeased => to is EditorSaveUiState.Clean
                or EditorSaveUiState.Dirty
                or EditorSaveUiState.StaleBase,
            EditorSaveUiState.RecoveryAvailable => to is EditorSaveUiState.Dirty
                or EditorSaveUiState.Clean
                or EditorSaveUiState.RecoveryConflict
                or EditorSaveUiState.ReadOnlyLeased,
            EditorSaveUiState.RecoveryConflict => to is EditorSaveUiState.Clean
                or EditorSaveUiState.ReadOnlyLeased,
            _ => false
        };
    }

    public static string ToWire(EditorSaveUiState state) => state switch
    {
        EditorSaveUiState.Clean => "saved",
        EditorSaveUiState.Dirty => "unsaved",
        EditorSaveUiState.SavePending => "unsaved",
        EditorSaveUiState.Saving => "saving",
        EditorSaveUiState.SaveFailed => "save-failed",
        EditorSaveUiState.StaleBase => "external-change",
        EditorSaveUiState.ReadOnlyLeased => "read-only",
        EditorSaveUiState.RecoveryAvailable => "recovery-available",
        EditorSaveUiState.RecoveryConflict => "recovery-conflict",
        _ => "unsaved"
    };
}
