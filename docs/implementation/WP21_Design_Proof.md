# WP21 Design Proof — AGENTS, Skills, Plugins, MCP Activation Security

**Status:** WP21 EXECUTION PROOF
**Accepted base:** `1e19a8f` (`Merge pull request #8 from LingYzh/wp20-project-git-service`)
**Branch:** `codex/wp21-activation-security`

Precedence: Product FROZEN > Architecture FROZEN > Implementation Design > ADR > local implementation detail.

## Frozen requirement solved

WP21 implements the frozen extension-security requirements in Product §§135–136 and §156,
and Architecture §§27–29:

- project `AGENTS.md` is canonical, inherits root → child, accepts `CLAUDE.md` only as a
  same-scope fallback, emits an explicit conflict diagnostic, and never resolves outside the
  Project Root;
- Skills, Plugins, and MCP descriptors are discovered at Application, User, and Project scopes;
  same-ID precedence is `Project > User > Application`, while distinct prompt Skills compose
  deterministically `Application → User → Project`;
- Project Trust, extension activation, executable migration, and MCP activation stay separate;
  opening/importing/cloning a project executes none of its content;
- a changed extension script/config/content digest invalidates its activation;
- AGENTS and active Skill digests contribute to runtime freshness;
- MCP/Plugin/Skill declarations remain requests, not capability grants.

## Minimal implementation delta

- Pure Domain activation transitions (`Inactive`, `Active`, `Invalidated`) and deterministic
  extension resolution.
- Path-free typed UI IPC for list, trust/revoke-trust, activate, and deactivate. Every mutation
  carries a UUID operation identity and is replay-safe at the Application service.
- Core-composed Application service that checks authenticated `USER_INTERACTIVE` principal and
  open-project binding before any store access.
- Infrastructure read-only discovery of `extension.llmw.json` manifests in trusted roots; manifest
  files are strict and include only frozen minimum metadata (`name`, `version`, `description`,
  `instructions`, `scripts`, requested permissions, dependencies) plus the required `kind`.
  Project extensions reside at `Extensions/<extension>/extension.llmw.json`; the project
  instruction root remains the project root.
- File-tree SHA-256 activation digest covers manifest, script, config, and resource content;
  reparse points and unsafe relative script references are rejected.
- Per-user activation/trust state is atomically persisted beneath LocalAppData, keyed by both
  Project UUID and canonical root. It is not project data, Authority SQLite data, an audit event,
  or a portable trust grant.

## Security and boundary proof

```text
Native UI typed IPC
  → Wp21IpcCommandHandler (principal + project binding)
  → ExtensionActivationService (operation identity + pure transition)
  → file catalog / per-user security state
```

The renderer receives no filesystem, script, credential, MCP transport, or Core-pipe interface.
The handler neither accepts a destination/path/command nor starts an executable or server. A
future actual MCP call still reaches the existing Core capability path (`MCP.Call`), where Role,
Permission, Tool/Extension grant, Project Trust, scope, and Hard Deny remain intersected.

No API key, token, secret, project root, script path, or process argument is stored in the
activation record, exposed by IPC, or logged by WP21. A corrupted local security state fails
closed to untrusted/inactive.

## Migration

No schema change. `project.db`, `schema_migrations`, and `PRAGMA user_version` remain v1. WP21
does not modify Authority state, migrations, or the WP20 package workflow.

## Explicit non-goals

- no launch of Skill scripts, Plugin hooks, executable migrations, or MCP servers;
- no MCP transport/client implementation, OAuth flow, credential storage, or secret handling;
- no renderer bridge expansion, UI filesystem API, Authority mutation, or recovery/reconstruction;
- no WP20 behavior change.

## Required evidence

- Domain: trust-gated/invalid activation transition and deterministic scope precedence.
- Contracts: source-generated path-free contracts and no replay-safe mutation semantic.
- Application: principal/binding gate, explicit trust separation, idempotent replay/conflict, and
  changed-digest removal from prompt freshness.
- Infrastructure: safe manifest/script discovery, reparse/path escape rejection, AGENTS inheritance,
  atomic location-bound local trust persistence.
- Integration: typed IPC → Application → Domain → Infrastructure trust/activate/invalidate path.
