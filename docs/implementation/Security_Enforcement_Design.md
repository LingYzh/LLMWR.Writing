# Security Enforcement Design

**Status**: `SECURITY IMPLEMENTATION BASELINE`

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## Trust zones

```text
Trusted Native UI Host
  ↓ typed WebMessage
Untrusted WebView Renderer

Trusted Authority Core
  ↔ authenticated Named Pipe
Agent Runtime broker
  ↔ run-scoped session
Restricted Worker Sandbox
  ↔ mediated tools
External Provider/MCP/Git remote
```

No lower-trust zone receives a generic proxy into a higher-trust zone.

## CallerPrincipal and RunSession

Principals: `UserInteractive`, `AgentRun`, `CoreInternal`. Core never trusts caller-supplied role/capability. RunSessionHandle is 256-bit opaque; DB stores hash only; bind channel/process/worker/run/project/expiry. Restart/cancel revokes and reissues as needed.

## Capability formula

```text
Product ∩ Role ∩ RuntimePermission ∩ ToolGrant ∩ ExtensionGrant ∩ Trust/PathScope − HardDeny
```

Evaluator returns structured deny explanation. Sensitive commands use declarative entry policy plus handler-side recheck. Cache invalidates on mode/trust/extension/path changes.

Narrative delegation is independent; BYPASS_PERMISSIONS cannot accept narrative facts without delegated Narrative Authority.

## Project Trust / executable activation

Project Trust, Skill script, Plugin, MCP server, executable migration, and Git hook activation are separate. Open/import/clone never executes project code. Executable content digest change invalidates activation.

## Worker sandbox

Mandatory stack: Restricted Token + AppContainer/LowBox + Job Object + broker.

Project path access: create/use per-project AppContainer identity and grant its SID minimum NTFS ACL only to designated sandbox surfaces. Run-level broker policy narrows further. Normalize/resolve final path before mediated access and reject reparse/junction escape.

Network: generic shell network denied by default. Allowed network requires both OS AppContainer network permission and broker/domain policy. Web.Search is separate.

Credentials: never plaintext in worker environment/files; trusted broker/callback owns secrets.

Job Object: memory/process/lifecycle limits and kill-on-close for entire process tree.

Sandbox init failure => Shell/Script unavailable; no unsandboxed fallback.

## WebView2

Renderer untrusted even for bundled assets. One virtual local origin; block unexpected top/frame navigation; external links go through validated native browser flow. Generic host objects disabled. Typed versioned WebMessage only; validate origin/schema. Do not interpolate project/user content into ExecuteScript. Release DevTools off; strict CSP; no remote executable editor assets.

## IPC

Current-user pipe restriction + bootstrap secret. Worker never inherits Core bootstrap credentials. A compromised Runtime still cannot choose another valid run identity without the Core-bound RunSession.

## Git hooks

Disabled by default. Enable requires Project Trust + advanced setting + explicit activation for current hook digest. Full Accept does not activate hooks. Hash change => re-approval.

## MCP

MCP remains behind Capability Engine. MCP roots are not a filesystem sandbox. Remote OAuth token remains brokered. Config/executable changes invalidate activation.

## Portable credentials

Portable project/settings are movable; Credential Manager/DPAPI secrets are not promised portable across machine/user and require re-authentication.

## Mandatory tests

Role/run spoof, stolen/revoked handle, pipe auth, WebView origin/message attack, path traversal/reparse escape, shell network denial, process escape, fail-closed sandbox, extension/migration/hook without trust, Full Accept hard-deny/trust bypass attempt.
