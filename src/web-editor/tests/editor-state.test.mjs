import test from "node:test";
import assert from "node:assert/strict";
import { EditorState } from "@codemirror/state";
import { history, undo, redo } from "@codemirror/commands";
import { markdown } from "@codemirror/lang-markdown";
import {
  AUTOSAVE_MS,
  changeTouchesDocument,
  createDebounce,
  displayStatus,
  expectedChunkCount,
  serializeChange
} from "../src/protocol.js";

test("TXT EditorState initializes with history", () => {
  const state = EditorState.create({ doc: "hello", extensions: [history()] });
  assert.equal(state.doc.toString(), "hello");
});

test("MD mode builds with lang-markdown", () => {
  const state = EditorState.create({
    doc: "# Title",
    extensions: [history(), markdown()]
  });
  assert.equal(state.doc.toString(), "# Title");
});

test("undo/redo semantics", () => {
  const fake = {
    state: EditorState.create({ doc: "ab", extensions: [history()] }),
    dispatch(tr) {
      fake.state = tr.state;
    }
  };
  fake.dispatch(fake.state.update({ changes: { from: 2, insert: "c" } }));
  assert.equal(fake.state.doc.toString(), "abc");
  undo(fake);
  assert.equal(fake.state.doc.toString(), "ab");
  redo(fake);
  assert.equal(fake.state.doc.toString(), "abc");
});

test("doc change vs selection-only", () => {
  assert.equal(changeTouchesDocument(0, 0, "x"), true);
  assert.equal(changeTouchesDocument(1, 3, ""), true);
  assert.equal(changeTouchesDocument(4, 4, ""), false);
});

test("change serialization is deterministic", () => {
  const first = serializeChange(1, 0, 0, 1, "A");
  const second = serializeChange(1, 0, 0, 1, "A");
  assert.deepEqual(first, second);
});

test("status labels distinguish save states", () => {
  assert.equal(displayStatus("saved"), "Saved");
  assert.equal(displayStatus("unsaved"), "Unsaved");
  assert.equal(displayStatus("saving"), "Saving…");
  assert.equal(displayStatus("save-failed"), "Save failed");
  assert.equal(displayStatus("external-change"), "External change detected");
  assert.equal(displayStatus("read-only"), "Read-only / another writer");
  assert.equal(displayStatus("recovery-available"), "Recovery available");
  assert.equal(displayStatus("recovery-conflict"), "Recovery conflict");
});

test("500ms debounce coalesces keystrokes", () => {
  const debounce = createDebounce(AUTOSAVE_MS);
  debounce.noteDocumentChange(0);
  debounce.noteSelectionOnly();
  debounce.noteDocumentChange(100);
  debounce.noteDocumentChange(250);
  debounce.noteDocumentChange(499);
  assert.equal(debounce.tick(499), false);
  assert.equal(debounce.tick(998), false);
  assert.equal(debounce.tick(999), true);
  assert.equal(debounce.saves, 1);
});

test("chunk count for large documents", () => {
  assert.equal(expectedChunkCount(0, 256 * 1024), 0);
  assert.equal(expectedChunkCount(256 * 1024 + 1, 256 * 1024), 2);
});
