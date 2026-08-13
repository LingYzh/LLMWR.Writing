# Editor Implementation Spec

**Status**: `EXECUTION BASELINE`

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## Trust model

Native WinUI host trusted; WebView2 renderer untrusted. Renderer never connects Core pipes and never gets generic filesystem/native API.

## Local origin / bridge

Use one virtual local application origin. Block unexpected navigation/frame navigation; external links validated then opened by native host. No generic host object. All Native↔Web messages are versioned JSON DTOs via PostMessage; validate origin, protocol, document/session ID, discriminator, size, schema.

## Editor session

Track EditorSessionId, DocumentIdentity, base disk revision/digest, LeaseOwner, Dirty, LastPersistedRevision, selection/cursor, format kind. Renderer owns unsaved editing state; Core owns durable persisted state.

## Same-file lease

User editing => user lease. Agent write request waits/pauses. Agent write => UI write disabled with cancel-agent option. Any lease transfer runs freshness first; stale write never applies blindly.

## TXT/MD

CodeMirror 6 state in renderer. v1 save full text. Autosave debounce 500ms. Undo/redo is renderer state; Local History is separate durable recovery. Persist UTF-8 no BOM and LF; read tolerates BOM/CRLF; unsupported encoding diagnoses/imports rather than corrupts.

Renderer crash: reopen persisted document, compare crash buffer base digest, restore only if safe; changed base => merge/recovery flow.

Search can span registered surfaces; replace is restricted to writable surfaces and multi-file replace uses preview/change-set semantics.

## DOCX

Open XML SDK behind `IDocxDocumentAdapter`. Internal AST = paragraph/run hierarchy with stable anchors/style refs. Normalized review representation maps back to paragraph/run.

Editable v1: paragraphs/headings, bold/italic/basic run formatting, lists/basic styles. Preserve-only if untouched: comments, Track Changes, images/relationships, complex tables, headers/footers/sections/fields/equations/other unsupported parts. Adapter clones package and writes touched supported parts only. Touching unsupported content emits fidelity warnings.

Corrupt DOCX => refuse edit and preserve source. Password/encrypted DOCX => v1 refuse; no decryption.

Minimum warning codes: unsupported present/touched, style fidelity risk, anchor mapping lost, relationship risk, corrupt, encrypted unsupported.

## Corpus

20 fixtures minimum: 10 Word + 10 LibreOffice, covering prose styles, lists, comments/track changes, images, tables, sections/header/footer and reopen round-trip. Each fixture defines untouched/touched expectations and anchor mapping.

## Release hardening

DevTools off, host objects off, default context menus off unless required, strict CSP, unexpected navigation blocked, WebView user-data under LocalAppData not Project.

## Tests

Malicious WebMessage/origin, external navigation, lease race, renderer crash dirty state, BOM/CRLF import, stale save, DOCX preserve-only round-trip, touched warning, anchor mapping.
