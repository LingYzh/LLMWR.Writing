# WebView Bridge Contract v1

**Status:** `WP15 EXECUTION CONTRACT`  
**Protocol:** `llmw-web-bridge`  
**Version:** `1`  
**Owner:** Trusted Native UI Host  
**Non-owner:** Untrusted WebView2 renderer  

> Baseline precedence: Product FROZEN > Architecture FROZEN > Implementation Design > this contract > local implementation detail.  
> This protocol is **not** `IPC_Contract_v1`. The renderer never speaks Core Named Pipe IPC.

## 1. Trust model

```text
Untrusted Renderer  (https://app.llmw.invalid/)
        ↓ typed WebMessage (PostWebMessageAsJson / WebMessageReceived)
Trusted Native UI Host
        ↓ existing typed process IPC (not this contract)
Authority Core / Agent Runtime
```

Application-owned origin is a **gate**, not authority. A compromised renderer may mint arbitrary WebMessages. Native capability requires every applicable fact below:

1. this process's trusted WebView instance
2. exact application origin + current top-level application document
3. current `documentSessionId`
4. protocol `llmw-web-bridge` and version `1`
5. known inbound discriminator
6. valid schema
7. non-replayed `messageId`
8. operation-specific policy (handshake state, external URI rules)

## 2. Non-equivalence

| Not this | Is this |
|---|---|
| Application origin | Trusted renderer |
| `Source` URI | Authorization |
| Same origin | Same document session |
| WebMessage bytes | Validated command |
| Known `semanticType` | Allowed current-state operation |
| Virtual host mapping | Filesystem / Project capability |
| External URL | WebView navigation permission |
| `AdditionalObjects` | Allowed file access |
| Renderer storage | Project Authority |
| This bridge | Core IPC / RunSession |

## 3. Envelope

Inbound and outbound messages use the same envelope. Unknown envelope keys are rejected.

```json
{
  "protocol": "llmw-web-bridge",
  "version": 1,
  "documentSessionId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "messageId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "semanticType": "renderer.ready",
  "replyTo": "optional-message-id",
  "payload": {}
}
```

| Field | Rule |
|---|---|
| `protocol` | Exact string `llmw-web-bridge` |
| `version` | JSON integer `1` only (not `0`, `2`, `1.0`, `1e0`) |
| `documentSessionId` | Non-empty string, max 64 chars |
| `messageId` | Non-empty string, max 64 chars, unique per session |
| `semanticType` | Closed discriminator, max 64 chars |
| `replyTo` | Optional string, max 64 chars |
| `payload` | JSON object; shape depends on `semanticType` |

Limits: envelope UTF-8 size ≤ 1 MiB; JSON depth ≤ 8. Size is checked before deserialization.

No generic `{ "method", "args" }` RPC. No CLR `$type` polymorphism.

## 4. Message set (WP15)

Renderer → host (inbound closed set):

| `semanticType` | When | Payload |
|---|---|---|
| `renderer.ready` | After `host.hello`, handshake only | optional `shell` string ≤ 64 |
| `bridge.ping` | Bridge READY | optional `nonce` string ≤ 64 |
| `externalLink.request` | Bridge READY | required `uri` string ≤ 2048 |

Host → renderer:

| `semanticType` | Payload |
|---|---|
| `host.hello` | `appName`, `shell` (UI-safe metadata only) |
| `bridge.pong` | optional echoed `nonce` |
| `bridge.ack` | `accepted` boolean |
| `bridge.error` | `code`, `message` (safe text only) |
| `host.status` | `bridge` = `ready` |

Inbound host-only types are treated as unknown commands.

Forbidden in WP15 (must remain unknown): `readFile`, `writeFile`, `listDirectory`, `runCommand`, `shell`, `git`, `provider`, `credential`, `mcp`, `core.invoke`, `agent.invoke`.

## 4.1 WP16 editor extension (closed additive set)

Renderer → host:

| `semanticType` | Payload |
|---|---|
| `editor.bind.ack` | `editorSessionId`, `transferId` |
| `editor.change` | `editorSessionId`, `sequence`, `expectedSequence`, `from`, `to`, `text` |
| `editor.shadow.resync.begin` | `editorSessionId`, `transferId`, `totalBytes`, `sha256` |
| `editor.shadow.resync.chunk` | `editorSessionId`, `transferId`, `index`, `count`, `data` |
| `editor.shadow.resync.commit` | `editorSessionId`, `transferId` |
| `editor.save.request` | `editorSessionId`, `explicit` |
| `editor.recovery.response` | `editorSessionId`, `action` = `restore` \| `discard` |
| `editor.selection.changed` | `editorSessionId`, `from`, `to`, `head` |
| `editor.close.request` | `editorSessionId` |

Host → renderer:

| `semanticType` | Payload |
|---|---|
| `editor.bind` | session/format/title/writable/saveState/recovery/digest. **No filesystem path.** |
| `editor.document.begin` / `chunk` / `commit` | bounded UTF-8 transfer |
| `editor.state` | saveState, dirty, digest, revision |
| `editor.save.result` | saveOperationId, succeeded, digest |
| `editor.lease.state` | writable, leaseOwnerKind |
| `editor.recovery.offer` / `editor.recovery.conflict` | editorSessionId |
| `editor.error` | typed `code` + safe `message` |

Every editor message remains bound to the current WP15 `documentSessionId`. The renderer cannot switch `EditorSessionId`. There is no `editor.openPath`, `readFile`, or `writeFile`. WebMessage max remains 1 MiB.

`host.hello.shell` is `wp16-editor`. CodeMirror styles use CSP nonce `llmw-editor` (`style-src 'self' 'nonce-llmw-editor'`). `unsafe-inline` / `unsafe-eval` remain forbidden.

## 5. Handshake and session

1. Security handlers and virtual-host mapping are installed **before** navigation.
2. Successful top-level application document load generates a fresh `documentSessionId`.
3. Host posts `host.hello`.
4. Renderer posts `renderer.ready` with that session.
5. Host posts `bridge.ack` and `host.status`. Bridge is READY.
6. `NavigationStarting`, `SourceChanged` (including `IsNewDocument = true`), same-document `SourceChanged` (`IsNewDocument = false`), reload, renderer process failure that loses the document, WebView recreation, and navigation failure **immediately** invalidate the previous session.
7. Navigation tracking is a bounded per-`NavigationId` table. Each new `NavigationId` records a host-owned `startSequence` (monotonic epoch; `NavigationId` numeric order is not authority), `hostCancelled`, and `allowedApplication`. Overlapping navigations keep independent records. Redirects reuse the same `NavigationId` and update that record without assigning a new `startSequence`. If any hop is host-cancelled, the navigation stays host-cancelled. Completing N1 does not clear N2. An unknown completion id is ignored. Overflow fail-closes without growing the table. WebView recreation resets tracking.
8. A host-cancelled navigation (`Cancel = true`) that completes with `WebErrorStatus.OperationCanceled` while `CoreWebView2.Source` is still the exact application document, and that is the latest-started tracked navigation, **does not** surface `NAVIGATION_FAILED`. If any remaining active navigation `CanReplaceTopLevelDocument`, the cancelled completion does **not** mint a session, does not post `host.hello`, and transfers `_latestStartSequence` to the newest remaining active document-replacing navigation. Only when no active document-replacing navigation remains may a host-cancelled completion restore a fresh session. The previous session remains permanently invalid and is never resurrected. An older navigation's completion, including a successful application completion, must not `BeginNewSession` while a later-started **document-replacing** navigation is still the ownership candidate.
9. A genuine failed **application** navigation does not mint a replacement session; the native `NAVIGATION_FAILED` error remains.
10. Same-document `SourceChanged` (fragment / History API, `IsNewDocument = false`) is a session event. Leaving the exact application document invalidates the session immediately. Returning to the exact application document mints a **new** session; it does not resurrect the previous one. `SourceChanged(IsNewDocument = true)` invalidates any live `DocumentSession` immediately and does not post `host.hello` or mark READY. It is not a new handshake. The owning successful `NavigationCompleted` remains the singular successful-document handshake. The invalidate is idempotent if `NavigationStarting` already cleared the session.
11. The renderer accepts `host.hello` as the session-establishing message. Every other host message whose `documentSessionId` is not the renderer's current session is ignored.

Stale session → `BRIDGE_STALE_SESSION`. Duplicate `messageId` in the same session → `BRIDGE_REPLAY`. Replay cache is per session and bounded; overflow fail-closes the session.

Request/response operations (`bridge.ack`, `bridge.error` for `externalLink.request`, including `EXTERNAL_LINK_BUSY`) set `replyTo` to the original request `messageId`. They do not emit `replyTo: null`.

## 6. Error codes

| Code | Meaning |
|---|---|
| `BRIDGE_WRONG_ORIGIN` | Source or current document is not the application origin |
| `BRIDGE_STALE_SESSION` | `documentSessionId` is not the live session |
| `BRIDGE_NOT_READY` | Handshake incomplete for this command |
| `BRIDGE_PROTOCOL_UNSUPPORTED` | Protocol string or version mismatch |
| `BRIDGE_UNKNOWN_MESSAGE_TYPE` | Discriminator not in the inbound closed set |
| `BRIDGE_INVALID_SCHEMA` | Missing/wrong types, extra keys, oversize strings |
| `BRIDGE_MESSAGE_TOO_LARGE` | UTF-8 envelope exceeds 1 MiB |
| `BRIDGE_JSON_TOO_DEEP` | Nesting exceeds depth 8 |
| `BRIDGE_MALFORMED_JSON` | Not JSON / not a JSON object |
| `BRIDGE_REPLAY` | Duplicate `messageId` in this session |
| `BRIDGE_ADDITIONAL_OBJECTS_DENIED` | `AdditionalObjects.Count > 0` |
| `NAVIGATION_BLOCKED` | Top-level/frame/new-window navigation denied |
| `EXTERNAL_URL_DENIED` | External URI failed policy |
| `EXTERNAL_LINK_BUSY` | An external-link confirmation is already pending for this window |

Renderer errors contain only `code` + safe `message` + `replyTo`. No stack traces, native paths, LocalAppData, Project paths, or exception text.

## 7. Origin and navigation

Single application origin: `https://app.llmw.invalid/` (RFC 2606 `.invalid`).

Allowed top-level documents: `/`, `/index.html`.  
Allowed application resources: `/`, `/index.html`, `/bridge.js`, `/app.css`, `/editor.bundle.js`.

Comparison uses parsed scheme/host/port, empty user-info, and exact host `app.llmw.invalid`. Prefix/wildcard matching is not authority.

External http(s) URLs are never loaded in WebView. Native host always cancels top-level external navigation. Native consent is offered only for the controlled user-intent flow (trusted WebView `IsUserInitiated` plus `CancelAndOfferExternal`). Script-created external navigation is cancelled and must not open native dialogs.

The host keeps at most one pending external confirmation per window. A second `externalLink.request` while a confirmation is open receives `EXTERNAL_LINK_BUSY` and does not create another `ContentDialog`, queue entry, or `ProcessStart`.

An accepted bridge `externalLink.request` freezes `documentSessionId`, request `messageId`, and the validated URI. Immediately before `ProcessStart` the host re-checks that the frozen session is still current and READY and that the top-level Source is still the application document. If navigation, reload, or process failure invalidated that session while the dialog was open, the host does not launch the browser and returns a typed stale/cancelled result.

Native host validates then opens the system browser via `ProcessStartInfo { FileName = absoluteUri, UseShellExecute = true }` after native consent. This is not `cmd.exe` / PowerShell / `Shell.Execute` string interpolation.

Ordinary logs record only safe origin descriptors (`scheme`, `host`, non-default `port`) plus event category and bridge error code. Path, query, fragment, userinfo, and full project/user-controlled URLs are not logged.

## 8. Source-of-truth / trust-surface map

| Surface | Truth owner | Renderer-controllable inputs | Privileged operation? | Validation |
|---|---|---|---|---|
| Native WinUI host | UI process | None (trusted) | Yes (host only) | Process identity |
| WebView renderer | Untrusted | DOM, script, WebMessages | No | Origin + session + schema |
| Virtual host mapping | Native host | None | Asset read of shipped files only | Mapping folder = app output assets |
| Renderer asset directory | Build/output | None | No Project read | Exists before navigate |
| WebView user-data folder | Native host | Renderer storage APIs | No Authority | `%LOCALAPPDATA%\LLMW.Writing\WebView2` |
| WebMessage bridge | Native processor | JSON bytes, Source, AdditionalObjects | Only closed WP15 ops | See §5–§6 |
| External navigation | Native policy | URL strings | System browser for valid http(s) | `ExternalUriPolicy` |
| Resource requests | Native policy | Request URI | None if blocked | Canonical origin compare |
| NewWindow | Native host | URL | Same as external flow | Cancel WebView window |
| Permissions / downloads / frames | Native host | Event args | No | Deny/cancel |
| Host objects | Disabled | N/A | No | `AreHostObjectsAllowed = false` |
| DevTools | Build config | N/A | Debug only | Release disabled |
| ProcessBootstrapper | Native host | None | Child process secrets | Never posted to renderer |
| Core pipe / credentials / Project FS | Core / later WPs | None in WP15 | Not via this bridge | No bridge types |

WP15 must not widen renderer→native privilege beyond ping and validated external http(s) open.

## 9. ProcessFailed recovery

`CoreWebView2.ProcessFailed` is classified by `ProcessFailedKind`. The host does not `Navigate` the existing control for every kind.

| Kind | Recovery |
|---|---|
| `BrowserProcessExited` | Recreate the WebView2 control, rebind the runtime host, create/attach the environment, apply every secure setting, register every handler, map the virtual host, navigate the application origin, then start a fresh document-session handshake |
| `RenderProcessExited` | Reload/navigate the application document on the existing control when it is still usable; otherwise recreate. Fresh session |
| `RenderProcessUnresponsive` | One bounded reload, then fail closed; no dialog/reload loop |
| `FrameRenderProcessExited` | WP15 allows no frames; fail closed without a navigation loop. Does not by itself prove top-level document loss |
| `GpuProcessExited` / `UtilityProcessExited` / `SandboxHelperProcessExited` / `PpapiPluginProcessExited` / `PpapiBrokerProcessExited` | Nonfatal. Observe; do not invalidate the document session, reload, or recreate |
| `UnknownProcessExited` | Observe / safe diagnostic. Must not inherit BrowserProcessExited or RenderProcessExited recovery |

The document session is invalidated whenever the renderer document is lost. Recreated controls reuse the same pre-navigation hardening sequence used at first initialization.

`ProcessFailed` from a `CoreWebView2` that is not the current control is ignored. Native host allocates a new renderer generation **synchronously** when it changes which WebView2 control is current (`RecreateRenderer` / adopt), not after `EnsureCoreWebView2Async` returns. Initialization, handler registration, virtual-host mapping, and navigation are one generation-owned operation: before and after every await the expected generation and control identity must still match. Handler registration is bound to the current Core/generation; a detached Core cannot skip or own registration for the current Core. Queued recovery freezes that generation with the failure kind and recovery action. If the current generation has changed when the callback runs, recovery is discarded: it must not `Navigate`, recreate, or invalidate the newer renderer. Renderer generation is Native Host lifecycle identity and is not `DocumentSessionId`.
