export const PROTOCOL = "llmw-web-bridge";
export const VERSION = 1;
export const AUTOSAVE_MS = 500;
export const MAX_PATCH_CHARS = 256 * 1024;

export const Inbound = {
  bind: "editor.bind",
  documentBegin: "editor.document.begin",
  documentChunk: "editor.document.chunk",
  documentCommit: "editor.document.commit",
  state: "editor.state",
  saveResult: "editor.save.result",
  leaseState: "editor.lease.state",
  recoveryOffer: "editor.recovery.offer",
  recoveryConflict: "editor.recovery.conflict",
  error: "editor.error"
};

export const Outbound = {
  bindAck: "editor.bind.ack",
  change: "editor.change",
  resyncBegin: "editor.shadow.resync.begin",
  resyncChunk: "editor.shadow.resync.chunk",
  resyncCommit: "editor.shadow.resync.commit",
  saveRequest: "editor.save.request",
  recoveryResponse: "editor.recovery.response",
  selectionChanged: "editor.selection.changed",
  closeRequest: "editor.close.request"
};

export function changeTouchesDocument(from, to, text) {
  return from !== to || (text != null && text.length > 0);
}

export function serializeChange(sequence, expectedSequence, from, to, text) {
  return {
    editorSessionId: "",
    sequence,
    expectedSequence,
    from,
    to,
    text
  };
}

export function createDebounce(ms) {
  let due = null;
  let saves = 0;
  return {
    noteDocumentChange(now) {
      due = now + ms;
    },
    noteSelectionOnly() {},
    tick(now) {
      if (due != null && now >= due) {
        due = null;
        saves += 1;
        return true;
      }
      return false;
    },
    get saves() {
      return saves;
    },
    get due() {
      return due;
    }
  };
}

export const SaveStates = {
  saved: "saved",
  unsaved: "unsaved",
  saving: "saving",
  saveFailed: "save-failed",
  externalChange: "external-change",
  readOnly: "read-only",
  recoveryAvailable: "recovery-available",
  recoveryConflict: "recovery-conflict"
};

export function displayStatus(state) {
  switch (state) {
    case SaveStates.saved:
      return "Saved";
    case SaveStates.unsaved:
      return "Unsaved";
    case SaveStates.saving:
      return "Saving…";
    case SaveStates.saveFailed:
      return "Save failed";
    case SaveStates.externalChange:
      return "External change detected";
    case SaveStates.readOnly:
      return "Read-only / another writer";
    case SaveStates.recoveryAvailable:
      return "Recovery available";
    case SaveStates.recoveryConflict:
      return "Recovery conflict";
    default:
      return "Unsaved";
  }
}

export function expectedChunkCount(totalBytes, chunkBytes) {
  if (totalBytes === 0) {
    return 0;
  }
  return Math.ceil(totalBytes / chunkBytes);
}
