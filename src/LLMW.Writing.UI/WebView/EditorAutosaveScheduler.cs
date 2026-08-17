using LLMW.Writing.Contracts.Editor;

namespace LLMW.Writing.UI.WebView;

internal sealed class EditorAutosaveScheduler
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(EditorTransportLimits.AutosaveDebounceMilliseconds);

    private DateTimeOffset? _dueAt;
    private bool _inFlight;
    private bool _dirtyAfterSave;

    public bool InFlight => _inFlight;

    public DateTimeOffset? DueAt => _dueAt;

    public bool DirtyAfterSave => _dirtyAfterSave;

    public void NoteValidatedDocumentChange(DateTimeOffset now)
    {
        _dueAt = now + Debounce;
        if (_inFlight)
        {
            _dirtyAfterSave = true;
        }
    }

    public bool TryBeginSave(DateTimeOffset now, bool explicitSave)
    {
        if (_inFlight)
        {
            if (explicitSave)
            {
                _dirtyAfterSave = true;
            }

            return false;
        }

        if (!explicitSave && (_dueAt is null || now < _dueAt.Value))
        {
            return false;
        }

        _inFlight = true;
        _dueAt = null;
        return true;
    }

    public void CompleteSave(bool succeeded, bool shadowStillNewer, DateTimeOffset now, bool scheduleRetryOnFailure)
    {
        _inFlight = false;
        if (!succeeded)
        {
            _dirtyAfterSave = false;
            _dueAt = scheduleRetryOnFailure ? now + Debounce : null;
            return;
        }

        if (shadowStillNewer || _dirtyAfterSave)
        {
            _dirtyAfterSave = false;
            _dueAt = now + Debounce;
            return;
        }

        _dirtyAfterSave = false;
        _dueAt = null;
    }

    public void Cancel()
    {
        _dueAt = null;
        _inFlight = false;
        _dirtyAfterSave = false;
    }
}
