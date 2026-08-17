import { EditorState } from "@codemirror/state";
import { EditorView, keymap } from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { markdown } from "@codemirror/lang-markdown";
import {
  Inbound,
  Outbound,
  MAX_PATCH_CHARS,
  changeTouchesDocument,
  displayStatus
} from "./protocol.js";

var editorView = null;
var editorSessionId = null;
var sequence = 0;
var applyingHost = false;
var pendingTransfer = null;
var formatKind = "txt";

function setText(id, text) {
  var el = document.getElementById(id);
  if (el) {
    el.textContent = text;
  }
}

function post(semanticType, payload) {
  if (!window.llmwBridge) {
    return;
  }
  window.llmwBridge.post(semanticType, payload);
}

function hexSha256(buffer) {
  return crypto.subtle.digest("SHA-256", buffer).then(function (hash) {
    var bytes = new Uint8Array(hash);
    var hex = "";
    for (var i = 0; i < bytes.length; i++) {
      hex += bytes[i].toString(16).padStart(2, "0");
    }
    return hex;
  });
}

function destroyEditor() {
  if (editorView) {
    editorView.destroy();
    editorView = null;
  }
}

function createEditor(text, format) {
  destroyEditor();
  var host = document.getElementById("editor-host");
  if (!host) {
    return;
  }
  var extensions = [
    EditorView.cspNonce.of("llmw-editor"),
    history(),
    keymap.of([{ key: "Mod-s", preventDefault: true, run: onExplicitSave }].concat(defaultKeymap, historyKeymap, [indentWithTab])),
    EditorView.lineWrapping,
    EditorView.updateListener.of(onViewUpdate)
  ];
  if (format === "md") {
    extensions.push(markdown());
  }
  editorView = new EditorView({
    state: EditorState.create({
      doc: text,
      extensions: extensions
    }),
    parent: host
  });
}

function onExplicitSave() {
  if (!editorSessionId) {
    return true;
  }
  post(Outbound.saveRequest, { editorSessionId: editorSessionId, explicit: true });
  return true;
}

function onViewUpdate(update) {
  if (!editorSessionId || applyingHost) {
    return;
  }
  if (update.selectionSet && !update.docChanged) {
    var sel = update.state.selection.main;
    post(Outbound.selectionChanged, {
      editorSessionId: editorSessionId,
      from: sel.from,
      to: sel.to,
      head: sel.head
    });
    return;
  }
  if (!update.docChanged) {
    return;
  }
  update.changes.iterChanges(function (fromA, toA, _fromB, _toB, inserted) {
    var text = inserted.toString();
    if (!changeTouchesDocument(fromA, toA, text)) {
      return;
    }
    if (text.length > MAX_PATCH_CHARS) {
      void resyncShadow();
      return;
    }
    var expected = sequence;
    sequence += 1;
    post(Outbound.change, {
      editorSessionId: editorSessionId,
      sequence: sequence,
      expectedSequence: expected,
      from: fromA,
      to: toA,
      text: text
    });
  });
}

function resyncShadow() {
  if (!editorView || !editorSessionId || !window.llmwBridge) {
    return Promise.resolve();
  }
  var bytes = new TextEncoder().encode(editorView.state.doc.toString());
  return hexSha256(bytes).then(function (sha) {
    var transferId = window.llmwBridge.uuid();
    var chunkSize = 256 * 1024;
    var count = bytes.length === 0 ? 0 : Math.ceil(bytes.length / chunkSize);
    post(Outbound.resyncBegin, {
      editorSessionId: editorSessionId,
      transferId: transferId,
      totalBytes: bytes.length,
      sha256: sha
    });
    for (var i = 0; i < count; i++) {
      var slice = bytes.subarray(i * chunkSize, Math.min(bytes.length, (i + 1) * chunkSize));
      var binary = "";
      for (var n = 0; n < slice.length; n++) {
        binary += String.fromCharCode(slice[n]);
      }
      post(Outbound.resyncChunk, {
        editorSessionId: editorSessionId,
        transferId: transferId,
        index: i,
        count: count,
        data: btoa(binary)
      });
    }
    post(Outbound.resyncCommit, { editorSessionId: editorSessionId, transferId: transferId });
    sequence += 1;
  });
}

function startTransfer(payload) {
  pendingTransfer = {
    transferId: payload.transferId,
    totalBytes: payload.totalBytes,
    sha256: payload.sha256,
    count: payload.count,
    chunks: []
  };
}

function acceptChunk(payload) {
  if (!pendingTransfer || payload.transferId !== pendingTransfer.transferId) {
    return;
  }
  pendingTransfer.chunks[payload.index] = payload.data;
}

function commitTransfer(payload) {
  if (!pendingTransfer || payload.transferId !== pendingTransfer.transferId) {
    return;
  }
  var binary = "";
  for (var i = 0; i < pendingTransfer.count; i++) {
    if (typeof pendingTransfer.chunks[i] !== "string") {
      pendingTransfer = null;
      return;
    }
    binary += atob(pendingTransfer.chunks[i]);
  }
  var bytes = new Uint8Array(binary.length);
  for (var n = 0; n < binary.length; n++) {
    bytes[n] = binary.charCodeAt(n);
  }
  var text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  applyingHost = true;
  try {
    createEditor(text, formatKind);
  } finally {
    applyingHost = false;
  }
  sequence = 0;
  pendingTransfer = null;
  post(Outbound.bindAck, { editorSessionId: editorSessionId, transferId: payload.transferId });
}

function onHostMessage(msg) {
  if (!msg || !msg.semanticType) {
    return;
  }
  var payload = msg.payload || {};
  if (msg.semanticType === Inbound.bind) {
    editorSessionId = payload.editorSessionId;
    formatKind = payload.format === "md" ? "md" : "txt";
    setText("editor-status", displayStatus(payload.saveState));
    setText("editor-title", payload.title || "");
    return;
  }
  if (!editorSessionId || payload.editorSessionId !== editorSessionId) {
    if (msg.semanticType.indexOf("editor.") === 0) {
      return;
    }
  }
  switch (msg.semanticType) {
    case Inbound.documentBegin:
      startTransfer(payload);
      break;
    case Inbound.documentChunk:
      acceptChunk(payload);
      break;
    case Inbound.documentCommit:
      try {
        commitTransfer(payload);
      } catch (e) {
        pendingTransfer = null;
      }
      break;
    case Inbound.state:
      setText("editor-status", displayStatus(payload.saveState));
      break;
    case Inbound.saveResult:
      setText("editor-status", payload.succeeded ? "Saved" : "Save failed");
      break;
    case Inbound.leaseState:
      if (!payload.writable) {
        setText("editor-status", displayStatus("read-only"));
      }
      break;
    case Inbound.recoveryOffer:
      setText("editor-status", displayStatus("recovery-available"));
      break;
    case Inbound.recoveryConflict:
      setText("editor-status", displayStatus("recovery-conflict"));
      break;
    case Inbound.error:
      setText("error", payload.code || "editor error");
      break;
    default:
      break;
  }
}

if (window.llmwBridge) {
  window.llmwBridge.onEditorMessage = onHostMessage;
}
