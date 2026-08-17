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

## 5. Handshake and session

1. Security handlers and virtual-host mapping are installed **before** navigation.
2. Successful top-level application document load generates a fresh `documentSessionId`.
3. Host posts `host.hello`.
4. Renderer posts `renderer.ready` with that session.
5. Host posts `bridge.ack` and `host.status`. Bridge is READY.
6. `NavigationStarting`, reload, renderer process failure, WebView recreation, and navigation failure **immediately** invalidate the previous session.

Stale session → `BRIDGE_STALE_SESSION`. Duplicate `messageId` in the same session → `BRIDGE_REPLAY`. Replay cache is per session and bounded; overflow fail-closes the session.

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

Renderer errors contain only `code` + safe `message` + `replyTo`. No stack traces, native paths, LocalAppData, Project paths, or exception text.

## 7. Origin and navigation

Single application origin: `https://app.llmw.invalid/` (RFC 2606 `.invalid`).

Allowed top-level documents: `/`, `/index.html`.  
Allowed application resources: `/`, `/index.html`, `/bridge.js`, `/app.css`.

Comparison uses parsed scheme/host/port, empty user-info, and exact host `app.llmw.invalid`. Prefix/wildcard matching is not authority.

External http(s) URLs are never loaded in WebView. Native host validates then opens the system browser via `ProcessStartInfo { FileName = absoluteUri, UseShellExecute = true }` after optional native consent. This is not `cmd.exe` / PowerShell / `Shell.Execute` string interpolation.

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
