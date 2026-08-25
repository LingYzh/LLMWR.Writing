# WP22 Design Proof — Recovery / Reconstruction / Project Health

**Status:** WP22 EXECUTION PROOF
**Accepted base:** `f56d1e9` (`Merge pull request #9 from LingYzh/wp21-activation-security`)
**Branch:** `wp22-recovery-reconstruction`

Precedence: Product FROZEN > Architecture FROZEN > Implementation Design > Test/Fault Plan > this proof > local detail.

No Frozen conflict requiring Architecture Review was found. WP22 completes CF-001 by
reconstructing the durable Chapter submission aggregate after WP03 transaction recovery. It
does not change the Authority commit point, transaction protocol, process ownership, or schema.

## Source-of-truth map

| Fact | Truth owner |
|---|---|
| Candidate and Review history | SQLite `candidates` / `review_attempts` |
| Chapter workflow and current pointer | SQLite `chapters` |
| transaction commit/recovery state | SQLite `authority_transactions` |
| Acceptance and Manuscript Revision | SQLite Authority rows created only inside WP03 commit |
| Current Manuscript | recoverable materialization; never recovery inference input |
| legal recovery transition | pure Domain recovery policy |
| startup orchestration / decision routing | Application `ProjectRecoveryCoordinator` |
| durable reads and normalization | Infrastructure SQLite recovery store |

No previous process memory, Agent memory, renderer/UI state, or filesystem manuscript bytes are
used to infer Authority.

## Recovery model

```text
durable Candidate / Review / Chapter / transaction / pointer
  -> WP03 RecoverIncomplete
  -> pure recovery classification and invariant checks
  -> durable workflow rehydrate
  -> resume Review / require Acceptance decision / Cancel / roll forward
```

Pre-commit Candidate + no Review rehydrates to `reviewing` and retains the one-submission lock.
Pre-commit Candidate + durable PASS rehydrates to `resolving`, exposes
`USER_ACTION_REQUIRED`, and permits only resume Acceptance or Cancel. A legal Cancel keeps
Candidate/Review history, marks the Candidate cancelled, returns the Chapter to Draft, and
releases the lock. A transaction with no Candidate is `AUTO_RECOVERABLE` and is released.

Committed Authority is never cancelled or rolled back. `COMMITTED_BUT_DIRTY` remains WP03
roll-forward and surfaces as `AUTHORITY_COMMITTED_ROLL_FORWARD`; exhausted or inconsistent
recovery surfaces as `RECOVERY_REQUIRED` and retains a blocking lock/read-only health result.
After WP03 verifies roll-forward materialization, WP22 normalizes the durable project submission
state to `idle` under committed-row guards, so another restart does not rediscover a live workflow.

## Project Health classifications

- `AUTO_RECOVERABLE`
- `USER_ACTION_REQUIRED`
- `RECOVERY_REQUIRED`
- `AUTHORITY_COMMITTED_ROLL_FORWARD`

Core project-open composition runs the Application coordinator and publishes bounded recovery
health notices. The untrusted renderer receives no recovery store, filesystem rollback,
Authority mutation, path, or generic Core capability. Resume/Cancel decision routing is also
gated by the existing Core authorization service (`Authority.Accept` or `Authority.Submit`);
missing or untrusted principals fail before durable state access.

## Idempotency and crash safety

WP03 may first change a pre-commit active transaction to terminal `failed`. WP22 then uses the
preserved Candidate/Review/Chapter state to normalize that same transaction identity back to a
legal `reviewing` or `resolving` workflow. Repeated startup repeats this cleanup/normalization on
the same rows: it creates no Candidate, Review, transaction, event, or second active submission.
An injected crash between transaction cleanup and workflow rehydrate is recoverable on the next
startup because `project_submission_state` remains durable and non-idle.

## Migration

Schema unchanged. Existing v1 columns and constraints are sufficient. Verification remains:

```sql
PRAGMA user_version;                         -- 1
SELECT COUNT(*) FROM schema_migrations;      -- 1
```

No dependency was added.

## Verification obligations

- Domain: illegal recovery transition, resume, Cancel, failed submission, pre-commit invariant.
- Application: restart orchestration, all health classifications, duplicate recovery, lock state.
- Integration: real file-backed SQLite restart/recomposition for CF-001 A–F.
- Fault injection: after transaction creation, before SQLite COMMIT, repeated restart, and
  interruption after transaction cleanup but before workflow rehydrate.
- Regression: full Domain/Application/Infrastructure/UI/Contract and Integration suites.

## Architecture and security proof

SQLite COMMIT remains the only logical Authority commit point. No Acceptance, Revision, pointer,
event, or Current Manuscript exists in any tested pre-commit interruption. Recovery never deletes
history and never treats filesystem state as Authority. Domain and Application retain no SQLite,
filesystem, IPC, UI, provider, Git, OpenXML, or Windows-sandbox implementation dependency.
