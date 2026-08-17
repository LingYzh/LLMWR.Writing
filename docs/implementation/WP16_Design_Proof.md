# WP16 Design Proof — TXT/MD Editor

**Status:** WP16 EXECUTION PROOF  
**Accepted base:** `f72381e45043ca079dfe351e16fe15502027413e`  
**Branch:** `wp16-txt-md-editor`  
**Does not own:** DOCX, Local History, Git, Authority, Project Trust, CF-001

Precedence: Product FROZEN > Architecture FROZEN > Implementation Design > Editor Spec > this proof > local detail.

No frozen conflict requiring Architecture Review was found. Architecture §24 “transaction-like replace range + patches” is the renderer→Native crash-shadow protocol. Core Draft persistence remains Editor Spec / Implementation Design **full-text save v1**. Local History debounce (Architecture §25) remains WP18.

---

## 1. Source-of-Truth map

| Fact | Truth owner |
|---|---|
| Project identity | Core (`project.llmw.json` ProjectId, never path) |
| Draft document eligibility | Core + frozen Draft path/document rules |
| Draft persisted bytes | physical Draft file |
| persisted digest | Core observation of physical Draft bytes (SHA-256) |
| EditorSessionId | Core (UUIDv7); Native binds; renderer does not mint |
| DocumentSessionId | WP15 Native WebView Host |
| renderer document state | renderer only for unsaved edit buffer |
| native crash shadow | Trusted Native Host runtime memory |
| writer lease | Core in-memory lease coordinator |
| Dirty | trusted host, from validated patches vs LastPersistedDigest |
| LastPersistedDigest | Core save result |
| save completion | Core after atomic materialization |
| Canon / Accepted chapter | existing Authority workflow — **Draft save MUST NOT mutate** |

Draft Save does not create Candidate, accept, change Current Manuscript, write provenance, or release RevisionBarrier.

---

## 2. Non-equivalence (hard-coded into tests)

Draft ≠ Candidate ≠ Current Manuscript. Draft save ≠ Authority commit. Autosave ≠ user acceptance. Dirty=false ≠ Canon current. EditorSessionId ≠ DocumentSessionId ≠ RunSession. DocumentSession READY ≠ lease ownership. Renderer-claimed EditorSessionId ≠ authority. Renderer text ≠ trusted file content. Renderer path is inexpressible. project-relative selector ≠ canonical physical target. same lexical path ≠ same trusted identity. user lease ≠ Project Trust ≠ Authority.Accept. agent write permission ≠ editor lease. lease ≠ freshness ≠ permission. save request ≠ save succeeded. IPC delivered ≠ disk write. timeout ≠ failed-before-write. retry ≠ duplicate-mutation authority. native crash shadow ≠ durable persistence. renderer localStorage ≠ recovery truth. CodeMirror history ≠ Local History. BlobRef ≠ Draft identity. blob upload ≠ path capability.

---

## 3. Forbidden proxies (rejected by construction)

- Renderer never supplies a filesystem path or ProjectId-as-auth.
- No generic `readFile` / `writeFile` / `editor.invoke` / host objects / AdditionalObjects.
- EditorSessionId alone is not authorization (UI channel + project + document + lease).
- DocumentSessionId is not Core authentication.
- mtime/size are not freshness. Digest is.
- Renderer Dirty flag is untrusted. Native Dirty is derived.
- Autosave timer is not lease authority.
- OpenProject is not Project Trust.
- Successful save is not Canon.
- Reconnect does not auto-replay save (`IsSafeToReplayAfterReconnect` excludes every editor command).
- IPC/WebMessage 1 MiB limits are unchanged.

---

## 4. EditorSession

Core-issued, ephemeral, in-memory. Tracks: EditorSessionId (UUIDv7), ProjectId, DocumentIdentity (chapterId + Draft filename), FormatKind txt\|md, Base/LastPersisted digest+revision, LeaseOwner, ReadOnly, UI ConnectionId, lifecycle.

Core restart invalidates every EditorSession and lease. Persisted Draft bytes remain truth. **No project.db EditorSession/lease rows. No schema v2.**

A Core EditorSession MAY survive WebView renderer recreation. The new renderer does **not** inherit it: new DocumentSession → `renderer.ready` → Native explicitly rebinds → document transfer → `editor.bind.ack`. Stale DocumentSession messages are rejected by WP15 fencing.

---

## 5. Draft identity and path safety

Request data only: `chapterId` + `draftFileName`. Core independently validates:

1. authenticated USER_INTERACTIVE UI channel and current project binding
2. chapterId is canonical UUIDv7
3. filename is a single segment, `.txt` or `.md`, no ADS/device/escape
4. relative path is exactly `Draft/{chapterId}/{fileName}`
5. `ProjectPathResolver` canonical containment + ancestor reparse rejection (not `StartsWith` alone as authority; resolver also `GetFullPath` + walk)
6. target is not Manuscript/current, `.llmw/`, or any non-Draft root

Lease key = canonical resolved physical path after that validation.

---

## 6. Lease

Owner kinds: `USER_EDITOR` | `AGENT_WRITE` (enum, not free string). Same physical Draft: one writer. Different Drafts: concurrent. No silent steal. Transfer re-checks current digest. Renderer cannot mint AGENT_WRITE. Tests use a fake AGENT_WRITE owner. No new generic agent filesystem tool. No manufactured agent-cancel path; typed read-only when agent owns the lease.

Release (idempotent): editor close, project close, UI connection loss, session invalidation, Core shutdown. Renderer navigation alone does not release while Native is rebinding the same EditorSession (bounded rebind).

---

## 7. UI ↔ Core IPC and assembly

Production path: Renderer → WP15 bridge → Native Host → authenticated UI Named Pipe → Core `Wp16IpcCommandHandler` → Application editor services → Infrastructure Draft adapter → file.

**UI → Application reference:** Application is a **class library**, not an executable. Frozen rule: executables must not reference other **executable** implementations. AgentRuntime already references Application. UI uses `IpcClientSession` transport only; EditorRuntime is composed **only in Core**. Bootstrap token stays native-only.

Commands (UI-owned, not replay-safe):

| semanticType | Role |
|---|---|
| `openDraftEditorSession` | validate + lease + session |
| `getDraftEditorSessionState` | query digest/lease/session |
| `releaseDraftEditorSession` | idempotent release |
| `beginEditorContentUpload` | bind upload to session/lease/save op |
| `editorContentUploadChunk` | ≤256 KiB raw |
| `commitEditorContentUpload` | hash verify → BlobRef |
| `saveDraftEditorSession` | BlobRef + expected digest → atomic Draft write |

BlobRef = `{digest,size,locator}`. Locator is a Core handle (`blob:<digest>`), never a path.

---

## 8. Large content / bounds

- IPC frame 1 MiB unchanged. WebMessage 1 MiB unchanged.
- Editor document resource bound: **32 MiB UTF-8**. Typed `EDITOR_DOCUMENT_TOO_LARGE`. No silent truncate.
- Host→renderer open uses `editor.document.begin|chunk|commit`.
- Renderer→host large paste uses `editor.shadow.resync.*` (atomic; partial never replaces shadow).
- Upload stages in Core memory (pre-sized declared length) then `IImmutableBlobStore.Stage`. Interrupted upload does not touch Draft.

---

## 9. Codec

Read: UTF-8 (BOM stripped from logical text), CRLF→LF. Invalid UTF-8 → `EDITOR_ENCODING_UNSUPPORTED`, original bytes preserved, no save of garbage. Write: UTF-8 **no BOM**, **LF**. No NFC of user prose.

---

## 10. Save linearization

Under the per-document lease lock: session + UI binding + project + identity + lease + format + Draft containment + expected digest == physical bytes + BlobRef hash. Then same-volume temp, flush, verify, freshness recheck, atomic replace, recompute digest. Self-write tracker cooperates with WP08. Pre-publish failure → old Draft intact. Post-publish → new bytes are truth. No Candidate/Canon.

SaveOperationId is Native-minted. Same id + same content/base → idempotent. Same id + different identity → `EDITOR_SAVE_IDENTITY_CONFLICT`.

Stale save responses must not roll back a newer Native revision (operation ordering on the host).

---

## 11. Native crash shadow, autosave, recovery

Patches: monotonic sequence, bounded ranges, session+editor binding. Invalid → `EDITOR_PATCH_INVALID`, shadow unchanged, read-only/reload.

Autosave: Native 500ms debounce after **validated document** changes only. One in-flight save per EditorSession. Edits during save keep Dirty and schedule the next save from the latest shadow. Explicit Save flushes debounce. Failure: Dirty remains; no retry spin.

Crash shadow survives renderer crash/recreation in the same UI process. It is **not** durable across UI-process crash (WP18).

Recovery: clean+matching digest → silent reopen. Dirty+same base → restore/discard offer; restore stays Dirty. Dirty+changed base → `RECOVERY_CONFLICT`; no overwrite, no autosave of the shadow.

---

## 12. CodeMirror / CSP / build

CodeMirror 6 (`@codemirror/state` 6.7.1, `view` 6.43.7, `commands` 6.10.4, `lang-markdown` 6.5.1). TXT = no markdown language. MD = syntax highlighting only, no HTML preview/execution. History is renderer EditorState, not Local History. No innerHTML.

CSP: no `unsafe-eval`, no `unsafe-inline`, no remote script/style, `connect-src 'none'`. CodeMirror StyleModule requires `EditorView.cspNonce`; `style-src` gains a **static nonce** `llmw-editor` only. That is not `unsafe-inline` and does not add network or eval.

esbuild 0.28.1 bundles to `src/web-editor/app/editor.bundle.js` (generated, not committed). `build.ps1` restores pnpm lockfile, runs web test+build, then .NET.

---

## 13. Trust-surface delta (summary)

| Surface | Owner | Renderer inputs | Side effect | Gate | Bound | Widen? |
|---|---|---|---|---|---|---|
| CodeMirror | renderer | keystrokes | unsaved buffer only | CSP + no HTML | 32 MiB logical | unchanged (untrusted) |
| Editor WebMessages | Native | JSON | shadow/save request | WP15 + editor schema + sessions | 1 MiB + patch limits | **narrow add** |
| EditorSession | Core | none to mint | lease/save | UI principal + project | ephemeral RAM | **narrow add** |
| Crash shadow | Native | patches | RAM snapshot | sequence/bind | 32 MiB | **narrow add** |
| Editor IPC | Core | DTOs | Draft file | UI channel + lease + digest | 1 MiB frames / 32 MiB doc | **narrow add** |
| Draft resolver | Core | chapterId+name | none until save | canonical Draft root | path policy | **narrow add** |
| Lease | Core | none | writer exclusion | owner kind + connection | in-memory | **narrow add** |
| Upload/BlobRef | Core | chunks | temp blob only | session+lease+hash | 256 KiB / 32 MiB | **narrow add** |
| Atomic Draft write | Infra | BlobRef | Draft bytes | lock+digest | file | **narrow add** |
| Autosave | Native | none (timer) | save IPC | Dirty+lease | 500ms coalesce | **narrow add** |
| Node/esbuild | build | n/a | static assets | lockfile | approved pkgs | **tooling** |

No renderer→filesystem or renderer→Core pipe capability is introduced. BLOCKER condition is not met.
