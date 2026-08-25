# WP20 Design Proof — Backup, Archive, and Final Package

**Status:** WP20 EXECUTION PROOF
**Accepted base:** `1fab451a0d9c263f09c48221a30de785e12e7161` (`merge(wp19): integrate Git adapter watcher`)
**Branch:** `wp20-project-git-service`
**Does not own:** Final Acceptance FSM, Authority mutation semantics, schema v2, restore/import UX, migration orchestration, UI file-picker integration, WP21 activation/security, WP22 recovery/reconstruction.

Precedence: Product FROZEN > Architecture FROZEN > Implementation Design > Test/Fault Plan > this proof > local detail.

No frozen conflict requiring Architecture Review was found.  Existing v1 tables
`snapshot_blob_leases` and `authority_provenance_stubs` are deliberately consumed as
specified; no migration or dependency change is required.

## 1. Source-of-truth map

| Fact | Truth owner |
|---|---|
| Project identity and canonical project root | Core open-project preflight |
| Authority state and accepted-snapshot facts | `project.db` written by Core-owned Authority workflows |
| Immutable artifact bytes | `.llmw/objects`, addressed by SHA-256 digest |
| Backup/Archive consistency boundary | SQLite online backup snapshot plus closure derived from that copied DB |
| GC protection during packaging | `snapshot_blob_leases` rows owned by the Core-composed package store |
| Archive contents | filtered projection of the consistent DB snapshot plus its reachable blobs |
| Minimal provenance when runtime history is absent | `authority_provenance_stubs` in the archive projection |
| Final Package identity | accepted snapshot identity plus deterministic logical-file manifest |
| ZIP byte stream | a transport container only, never the sole logical identity |

Draft, Candidate, Canon, Manuscript/current, and Final Package remain non-equivalent.
Creating any WP20 package is observational: it never accepts a Candidate, creates an
Accepted Snapshot, mutates Canon, writes Current Manuscript, or changes Authority state.

## 2. Package kinds and closure

| Kind | Input | Required contents | Explicit exclusions |
|---|---|---|---|
| Backup | live project | consistent SQLite copy, descriptor, durable project files, all Authority-reachable blobs | cache, logs, temp scratch |
| Archive | live project + include-history choice | filtered DB projection, project files, reachable Authority blobs, required provenance stubs | local history and runtime history by default |
| Final Package | existing Accepted Snapshot + selected logical assets | accepted manuscript assets and deterministic integrity manifest | Draft, Raw, runtime, cache, Local History by default |

The copied DB is the sole input for the blob query.  Its authority closure includes
candidate/manuscript artifacts, accepted snapshot manifests and warnings, narrative state
revision payloads, and before/after payload digests from Narrative Change Sets.  Archive
history is included only if selected.  A missing, malformed, or hash-mismatched reachable
blob aborts publication.

## 3. Snapshot publication protocol

```text
authenticated native-UI IPC request
  -> Core validates client/principal/project binding
  -> Application validates request identity and operation intent
  -> Infrastructure creates SQLite online backup in a private stage directory
  -> read closure from copied database
  -> insert snapshot_blob_lease rows in live database
  -> copy + SHA-256 verify every closure blob and required file
  -> build package/manifest in stage
  -> atomically publish temp -> final in Core-configured project-external package root
  -> delete leases (also on failure/finally)
```

Core additionally invokes the same Application service with its composition-only
`CoreInternal` principal for the frozen daily and normal-session-close **Backup** trigger.
That identity can create Backup only: it cannot create Archive or Final Package, cannot cross
the open Project binding, and is not constructible from IPC payloads or by the renderer.

Leases use an expiry only as crash hygiene; the executing process explicitly removes all
leases on success or failure.  The snapshot builder never invokes GC.  If lease creation,
copying, validation, manifest serialization, or publish fails, no final package is exposed.

Backup retention is deterministic: keep the five newest successfully published backups for
the project in the Core-configured external root; never delete a staging directory or an
unrelated archive/final package.

## 4. Archive projection and provenance

Archive starts from the copied SQLite database; it never edits or labels a copied live DB as
filtered.  The projection deletes default-excluded runtime and local-history table families.
It retains `authority_provenance_stubs`, and retains only stubs referenced by archive
Authority records when reference data exists.  Runtime/live project state remains unchanged.

## 5. Final Package manifest and verification

The pure Domain model validates manifest version, IDs, accepted version/time, logical file
names, SHA-256 content digests, and deterministic ordinal ordering.  Infrastructure is the
only layer that reads ZIP/filesystem bytes.  Verification compares each logical file in the
package with the manifest: a mismatch produces `MODIFIED_AFTER_FINAL_ACCEPTANCE`; it does not
make the package unusable or attempt repair.  Optional signature fields remain absent/empty;
WP20 does not implement signing or TSA.

## 6. Trust surface and IPC

Production path:

```text
Renderer (untrusted)
  -> existing typed bridge / Native UI
  -> authenticated UI pipe
  -> WP20 IPC handler
  -> Application ProjectPackageService
  -> Infrastructure ProjectPackageStore
```

WP20 adds no renderer bridge message, generic file API, project path, output path,
credential, Core-pipe access, arbitrary archive entry name, or agent-runtime command.  The
Core chooses the project-external package destination from trusted application configuration.
Every mutation requires the authenticated `USER_INTERACTIVE` principal, an open matching
Project binding, and an explicit typed request.  Runtime/renderer, wrong project, malformed
payload, and unavailable service are rejected before Infrastructure.

## 7. Layer allocation

| Layer | Responsibility |
|---|---|
| Contracts | typed path-free WP20 IPC requests/responses and semantic names |
| Domain | pure final-package manifest validation and deterministic ordering |
| Application | principal/request validation and narrow package-store port |
| Infrastructure | SQLite online backup/projection, lease persistence, digest copy, ZIP publication/verification |
| Core | project-scoped composition and authenticated command binding |
| UI/renderer | unchanged; security regression proves no renderer capability is introduced |

No Infrastructure type appears in Application or Contracts; no executable references another
executable implementation.

## 8. Test proof obligations

- Domain: malformed/duplicate logical entries denied; canonical manifest ordering and digest
  validation retained.
- Application/security: explicit trusted user succeeds; missing/wrong identity, wrong project,
  runtime/renderer request, malformed request, and unavailable service stop before storage.
- Infrastructure: SQLite online snapshot; Authority blob closure and copy/hash verification;
  snapshot lease insertion/cleanup; archive default history exclusion + provenance stub;
  five-backup rotation; final manifest verification detects modification without deletion.
- Contracts/UI security: source-generated contract round-trip and the renderer's absence from
  the WP20 filesystem/IPC surface.

## 9. Explicit non-goals

- No automatic Final Acceptance, Candidate creation, Canon mutation, restore, import, or
  physical package lock.
- No schema migration, dependency change, signature/TSA, custom backup-location UI, or
  WP21/WP22 work.
