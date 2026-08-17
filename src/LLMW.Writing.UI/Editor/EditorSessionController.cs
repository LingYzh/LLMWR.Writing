using LLMW.Writing.Contracts.Editor;
using LLMW.Writing.Contracts.Ipc;
using LLMW.Writing.Application.Ipc;
using LLMW.Writing.UI.Hosting;
using LLMW.Writing.UI.WebView;
using Microsoft.UI.Dispatching;

namespace LLMW.Writing.UI.Editor;

internal sealed class EditorSessionController : IEditorBridgeHost
{
    private readonly IWebViewRendererSite _site;
    private readonly Func<WebViewRuntimeHost?> _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly EditorAutosaveScheduler _autosave = new();
    private readonly EditorSaveStateMachine _fsm = new();
    private IEditorCoreClient? _core;
    private OpenDraftEditorSessionResponse? _open;
    private EditorCrashShadow? _shadow;
    private string? _readyDocumentSessionId;
    private string? _pendingRecoveryKind;
    private DispatcherQueueTimer? _timer;

    public EditorSessionController(IWebViewRendererSite site, Func<WebViewRuntimeHost?> host)
    {
        _site = site;
        _host = host;
        _dispatcher = site.DispatcherQueue;
    }

    public void AttachCore(IEditorCoreClient core)
    {
        lock (_gate)
        {
            _core = core;
        }
    }

    public async Task OpenDraftAsync(string chapterId, string draftFileName, CancellationToken cancellationToken)
    {
        IEditorCoreClient core;
        lock (_gate)
        {
            core = _core ?? throw new InvalidOperationException("Core editor client is unavailable.");
        }

        var opened = await core.OpenAsync(chapterId, draftFileName, requestWritable: true, cancellationToken)
            .ConfigureAwait(true);
        var read = NativeDraftReader.Read(core.ProjectRoot, opened.RelativeDraftPath, opened.BaseDiskDigest);
        if (!read.Succeeded)
        {
            _site.ShowNativeError(read.ErrorCode ?? IpcErrorCodes.EditorSessionInvalid, "Draft could not be opened.");
            return;
        }

        lock (_gate)
        {
            _open = opened;
            _shadow = new EditorCrashShadow(
                opened.EditorSessionId,
                opened.BaseDiskDigest,
                read.Value!.EncodingSupported ? read.Value.LogicalText : "",
                dirty: false);
            _fsm.Force(opened.Writable
                ? (read.Value.EncodingSupported ? EditorSaveUiState.Clean : EditorSaveUiState.ReadOnlyLeased)
                : EditorSaveUiState.ReadOnlyLeased);
            _pendingRecoveryKind = "none";
            if (!read.Value.EncodingSupported)
            {
                _pendingRecoveryKind = "encoding";
            }
        }

        EnsureTimer();
        if (!string.IsNullOrEmpty(VolatileReadySession()))
        {
            RebindLocked();
        }

        PublishStatus();
        if (!read.Value!.EncodingSupported)
        {
            _site.ShowNativeError(IpcErrorCodes.EditorEncodingUnsupported, "Draft encoding is not supported UTF-8.");
        }
    }

    public void OnDocumentSessionReady(string documentSessionId)
    {
        lock (_gate)
        {
            _readyDocumentSessionId = documentSessionId;
            _shadow?.BindRenderer(documentSessionId, _host()?.CurrentRendererGeneration ?? 0);
        }

        RebindLocked();
    }

    public void HandleEditorMessage(EditorInboundMessage message, string documentSessionId, string messageId)
    {
        EditorCrashShadow? shadow;
        OpenDraftEditorSessionResponse? open;
        lock (_gate)
        {
            shadow = _shadow;
            open = _open;
            if (shadow is null
                || open is null
                || !shadow.MatchesBinding(message.EditorSessionId ?? "", documentSessionId))
            {
                Post(BridgeOutboundJson.EditorError(
                    documentSessionId,
                    Guid.NewGuid().ToString("D"),
                    messageId,
                    IpcErrorCodes.EditorSessionInvalid,
                    "Editor session is not bound."));
                return;
            }
        }

        switch (message.SemanticType)
        {
            case BridgeSemanticTypes.EditorChange:
                ApplyPatch(shadow, message, documentSessionId, messageId);
                break;
            case BridgeSemanticTypes.EditorShadowResyncBegin:
                Reply(shadow.BeginResync(message.TransferId ?? "", message.TotalBytes ?? -1, message.Sha256 ?? ""), documentSessionId, messageId);
                break;
            case BridgeSemanticTypes.EditorShadowResyncChunk:
                Reply(shadow.AcceptResyncChunk(message.TransferId ?? "", message.Index ?? -1, message.Count ?? -1, message.Data ?? ""), documentSessionId, messageId);
                break;
            case BridgeSemanticTypes.EditorShadowResyncCommit:
                var committed = shadow.CommitResync(message.TransferId ?? "");
                Reply(committed, documentSessionId, messageId);
                if (committed.Succeeded && shadow.Dirty)
                {
                    NoteDirty();
                }

                break;
            case BridgeSemanticTypes.EditorSaveRequest:
                _ = SaveAsync(explicitSave: message.ExplicitSave == true);
                break;
            case BridgeSemanticTypes.EditorRecoveryResponse:
                ApplyRecovery(message.Action ?? "");
                break;
            case BridgeSemanticTypes.EditorSelectionChanged:
                shadow.ApplySelection(message.From ?? 0, message.To ?? 0, message.Head ?? 0);
                break;
            case BridgeSemanticTypes.EditorBindAck:
                break;
            case BridgeSemanticTypes.EditorCloseRequest:
                _ = CloseAsync();
                break;
        }
    }

    public Task FlushSaveAsync() => SaveAsync(explicitSave: true);

    public void RestoreRecovery() => ApplyRecovery("restore");

    public void DiscardRecovery() => ApplyRecovery("discard");

    private void ApplyPatch(EditorCrashShadow shadow, EditorInboundMessage message, string documentSessionId, string messageId)
    {
        var result = shadow.ApplyChange(
            message.Sequence ?? -1,
            message.ExpectedSequence ?? -1,
            message.From ?? -1,
            message.To ?? -1,
            message.Text ?? "");
        Reply(result, documentSessionId, messageId);
        if (result.Succeeded && shadow.Dirty)
        {
            NoteDirty();
        }
        else if (!result.Succeeded)
        {
            lock (_gate)
            {
                _fsm.TryTransition(EditorSaveUiState.ReadOnlyLeased);
            }

            PublishStatus();
        }
    }

    private void NoteDirty()
    {
        lock (_gate)
        {
            if (_fsm.State is EditorSaveUiState.ReadOnlyLeased
                or EditorSaveUiState.RecoveryConflict
                or EditorSaveUiState.StaleBase)
            {
                return;
            }

            _fsm.TryTransition(EditorSaveUiState.Dirty);
            _fsm.TryTransition(EditorSaveUiState.SavePending);
            _autosave.NoteValidatedDocumentChange(DateTimeOffset.UtcNow);
        }

        PublishStatus();
    }

    private void ApplyRecovery(string action)
    {
        OpenDraftEditorSessionResponse? open;
        EditorCrashShadow? shadow;
        IEditorCoreClient? core;
        lock (_gate)
        {
            open = _open;
            shadow = _shadow;
            core = _core;
        }

        if (open is null || shadow is null || core is null)
        {
            return;
        }

        if (string.Equals(action, "discard", StringComparison.Ordinal))
        {
            var read = NativeDraftReader.Read(core.ProjectRoot, open.RelativeDraftPath, open.LastPersistedDigest);
            if (!read.Succeeded || !read.Value!.EncodingSupported)
            {
                _site.ShowNativeError(read.ErrorCode ?? IpcErrorCodes.EditorStaleBase, "Persisted Draft could not be reloaded.");
                return;
            }

            shadow.LoadCleanDisk(read.Value.LogicalText, read.Value.Digest, open.LastPersistedRevision);
            lock (_gate)
            {
                _fsm.Force(open.Writable ? EditorSaveUiState.Clean : EditorSaveUiState.ReadOnlyLeased);
                _pendingRecoveryKind = "none";
            }

            RebindLocked();
            PublishStatus();
            return;
        }

        if (string.Equals(action, "restore", StringComparison.Ordinal))
        {
            shadow.LoadRestoredBuffer(shadow.LogicalText);
            lock (_gate)
            {
                _fsm.Force(EditorSaveUiState.Dirty);
                _pendingRecoveryKind = "none";
                _autosave.NoteValidatedDocumentChange(DateTimeOffset.UtcNow);
            }

            RebindLocked();
            PublishStatus();
        }
    }

    private async Task CloseAsync()
    {
        IEditorCoreClient? core;
        string? sessionId;
        lock (_gate)
        {
            core = _core;
            sessionId = _open?.EditorSessionId;
            _shadow = null;
            _open = null;
            _autosave.Cancel();
            _fsm.Force(EditorSaveUiState.Clean);
        }

        if (core is not null && sessionId is not null)
        {
            try
            {
                await core.ReleaseAsync(sessionId, CancellationToken.None).ConfigureAwait(true);
            }
            catch (IpcProtocolException)
            {
            }
        }

        PublishStatus();
    }

    private async Task SaveAsync(bool explicitSave)
    {
        EditorCrashShadow? shadow;
        OpenDraftEditorSessionResponse? open;
        IEditorCoreClient? core;
        lock (_gate)
        {
            shadow = _shadow;
            open = _open;
            core = _core;
            if (shadow is null || open is null || core is null || !open.Writable)
            {
                return;
            }

            if (!_autosave.TryBeginSave(DateTimeOffset.UtcNow, explicitSave))
            {
                return;
            }

            _fsm.TryTransition(EditorSaveUiState.Saving);
        }

        PublishStatus();
        var logical = shadow.LogicalText;
        var bytes = shadow.EncodeUtf8NoBomLf();
        if (bytes.Length > EditorTransportLimits.MaximumDocumentUtf8Bytes)
        {
            CompleteSave(false, IpcErrorCodes.EditorDocumentTooLarge, logical);
            return;
        }

        var saveOp = Guid.NewGuid().ToString("D");
        try
        {
            var blob = await core.UploadAsync(open.EditorSessionId, saveOp, bytes, CancellationToken.None)
                .ConfigureAwait(true);
            var saved = await core.SaveAsync(
                    open.EditorSessionId,
                    saveOp,
                    shadow.LastPersistedDigest,
                    blob,
                    CancellationToken.None)
                .ConfigureAwait(true);

            var stillNewer = !shadow.CoversLatestShadow(logical);
            shadow.MarkSaved(saved.SaveOperationId, saved.PersistedDigest, saved.PersistedRevision, logical);
            lock (_gate)
            {
                _open = open with
                {
                    LastPersistedDigest = saved.PersistedDigest,
                    LastPersistedRevision = saved.PersistedRevision,
                    BaseDiskDigest = saved.PersistedDigest
                };
                _autosave.CompleteSave(true, stillNewer, DateTimeOffset.UtcNow, scheduleRetryOnFailure: false);
                _fsm.Force(stillNewer || shadow.Dirty ? EditorSaveUiState.Dirty : EditorSaveUiState.Clean);
                if (stillNewer || shadow.Dirty)
                {
                    _fsm.TryTransition(EditorSaveUiState.SavePending);
                }
            }
        }
        catch (IpcProtocolException exception)
        {
            CompleteSave(false, exception.ErrorCode, logical, exception.ErrorCode == IpcErrorCodes.EditorStaleBase);
            return;
        }
        catch (Exception)
        {
            CompleteSave(false, IpcErrorCodes.EditorSaveOutcomeUnknown, logical);
            return;
        }

        PublishStatus();
    }

    private void CompleteSave(bool succeeded, string code, string logical, bool stale = false)
    {
        lock (_gate)
        {
            _autosave.CompleteSave(succeeded, !_shadow?.CoversLatestShadow(logical) ?? true, DateTimeOffset.UtcNow, scheduleRetryOnFailure: false);
            if (stale)
            {
                _fsm.Force(_shadow?.Dirty == true ? EditorSaveUiState.RecoveryConflict : EditorSaveUiState.StaleBase);
            }
            else
            {
                _fsm.Force(EditorSaveUiState.SaveFailed);
            }
        }

        _site.ShowNativeError(code, "Draft save did not complete.");
        PublishStatus();
    }

    private void RebindLocked()
    {
        string? session;
        OpenDraftEditorSessionResponse? open;
        EditorCrashShadow? shadow;
        EditorSaveUiState state;
        string recovery;
        IEditorCoreClient? core;
        lock (_gate)
        {
            session = _readyDocumentSessionId;
            open = _open;
            shadow = _shadow;
            state = _fsm.State;
            recovery = _pendingRecoveryKind ?? "none";
            core = _core;
        }

        if (session is null || open is null || shadow is null)
        {
            return;
        }

        if (core is not null)
        {
            try
            {
                var current = NativeDraftReader.Read(core.ProjectRoot, open.RelativeDraftPath, open.LastPersistedDigest);
                if (!current.Succeeded && current.ErrorCode == IpcErrorCodes.EditorStaleBase)
                {
                    lock (_gate)
                    {
                        _fsm.Force(shadow.Dirty ? EditorSaveUiState.RecoveryConflict : EditorSaveUiState.StaleBase);
                        state = _fsm.State;
                        recovery = shadow.Dirty ? "conflict" : "reload";
                        _pendingRecoveryKind = recovery;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        if (state == EditorSaveUiState.RecoveryAvailable || (shadow.Dirty && recovery == "none" && state != EditorSaveUiState.Clean))
        {
            var diskMatches = string.Equals(shadow.BasePersistedDigest, open.LastPersistedDigest, StringComparison.Ordinal);
            if (shadow.Dirty && diskMatches)
            {
                recovery = "offer";
                lock (_gate)
                {
                    _fsm.Force(EditorSaveUiState.RecoveryAvailable);
                    state = EditorSaveUiState.RecoveryAvailable;
                }
            }
        }

        var transfer = EditorDocumentChunker.Split(shadow.LogicalText);
        var format = open.FormatKind == EditorFormatKind.Md ? "md" : "txt";
        var leaseWire = open.LeaseOwnerKind == EditorLeaseOwnerKind.AgentWrite
            ? "agentWrite"
            : open.LeaseOwnerKind == EditorLeaseOwnerKind.UserEditor ? "userEditor" : null;

        Post(BridgeOutboundJson.EditorBind(
            session,
            Guid.NewGuid().ToString("D"),
            open.EditorSessionId,
            transfer.TransferId,
            format,
            open.LogicalTitle,
            open.Writable && state is not EditorSaveUiState.ReadOnlyLeased and not EditorSaveUiState.RecoveryConflict,
            EditorSaveStateMachine.ToWire(state),
            leaseWire,
            recovery,
            open.LastPersistedDigest));

        if (recovery == "offer")
        {
            Post(BridgeOutboundJson.EditorRecoveryOffer(session, Guid.NewGuid().ToString("D"), open.EditorSessionId));
        }
        else if (recovery == "conflict")
        {
            Post(BridgeOutboundJson.EditorRecoveryConflict(session, Guid.NewGuid().ToString("D"), open.EditorSessionId));
        }

        foreach (var message in EditorDocumentChunker.ToOutbound(session, open.EditorSessionId, transfer))
        {
            Post(message);
        }

        Post(BridgeOutboundJson.EditorState(
            session,
            Guid.NewGuid().ToString("D"),
            open.EditorSessionId,
            EditorSaveStateMachine.ToWire(state),
            shadow.Dirty,
            open.LastPersistedDigest,
            open.LastPersistedRevision));
        Post(BridgeOutboundJson.EditorLeaseState(
            session,
            Guid.NewGuid().ToString("D"),
            open.EditorSessionId,
            open.Writable,
            leaseWire));
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.IsRepeating = true;
        _timer.Tick += OnAutosaveTick;
        _timer.Start();
    }

    private void OnAutosaveTick(DispatcherQueueTimer sender, object args)
    {
        bool due;
        lock (_gate)
        {
            due = _autosave.DueAt is not null && DateTimeOffset.UtcNow >= _autosave.DueAt.Value && !_autosave.InFlight;
        }

        if (due)
        {
            _ = SaveAsync(explicitSave: false);
        }
    }

    private void Reply(EditorShadowResult result, string documentSessionId, string messageId)
    {
        if (result.Succeeded)
        {
            return;
        }

        Post(BridgeOutboundJson.EditorError(
            documentSessionId,
            Guid.NewGuid().ToString("D"),
            messageId,
            result.ErrorCode ?? IpcErrorCodes.EditorPatchInvalid,
            result.SafeMessage ?? "Editor patch was rejected."));
    }

    private void Post(string json) => _host()?.PostEditorJson(json);

    private string? VolatileReadySession()
    {
        lock (_gate)
        {
            return _readyDocumentSessionId;
        }
    }

    private void PublishStatus()
    {
        string text;
        lock (_gate)
        {
            text = _fsm.WireName;
        }

        _site.ShowNativeStatus("EDITOR", text);
    }
}
