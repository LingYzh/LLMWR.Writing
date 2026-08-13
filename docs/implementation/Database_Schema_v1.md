# Database Schema v1

**Status**: `MIGRATION-READY DESIGN`

> Baseline precedence: `Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md` > `Writing_Technical_Architecture_Spec_v0.1_FROZEN.md` > this Implementation Design > ADR > local implementation detail.
>
> This document compiles confirmed Phase 4 decisions Q123–Q154. It does not reopen frozen Product or Architecture decisions.

## General rules

One project = one `project.db`; Core only writer. UUIDv7 = canonical TEXT(36), enums = stable TEXT. Foreign keys are explicit. Authority/Narrative ownership uses RESTRICT + tombstone; CASCADE is limited to disposable runtime children. JSON canonicalization is applied before persistence. WAL + synchronous FULL + 5000ms busy timeout are required.

## Migration protocol

1. acquire project migration ownership;
2. force backup;
3. verify `PRAGMA user_version` + migration checksums;
4. execute migration transaction where SQLite allows;
5. record `schema_migrations` + set `PRAGMA user_version=1`;
6. integrity/schema check;
7. only then open project.

## v1 DDL

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA busy_timeout = 5000;

CREATE TABLE schema_migrations (
  migration_id TEXT PRIMARY KEY,
  applied_at_ms INTEGER NOT NULL,
  app_version TEXT NOT NULL,
  checksum TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE objects (
  object_id TEXT PRIMARY KEY CHECK(length(object_id)=36),
  object_type TEXT NOT NULL,
  schema_version INTEGER NOT NULL,
  revision_no INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  deleted_at_ms INTEGER
) STRICT;

CREATE TABLE object_paths (
  path_id TEXT PRIMARY KEY,
  object_id TEXT NOT NULL REFERENCES objects(object_id) ON DELETE RESTRICT,
  relative_path TEXT NOT NULL UNIQUE,
  path_kind TEXT NOT NULL,
  is_canonical INTEGER NOT NULL CHECK(is_canonical IN(0,1)),
  physical_digest TEXT,
  semantic_digest TEXT,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_object_one_canonical_path ON object_paths(object_id) WHERE is_canonical=1;

CREATE TABLE registry_entries (
  registry_entry_id TEXT PRIMARY KEY,
  object_id TEXT REFERENCES objects(object_id) ON DELETE RESTRICT,
  path_id TEXT REFERENCES object_paths(path_id) ON DELETE RESTRICT UNIQUE,
  object_type TEXT NOT NULL,
  schema_version INTEGER NOT NULL,
  registration_state TEXT NOT NULL CHECK(registration_state IN('registered','unregistered','ignored','missing')),
  retrieval_availability TEXT NOT NULL CHECK(retrieval_availability IN('available','unavailable','stale')),
  trusted_physical_digest TEXT,
  trusted_semantic_digest TEXT,
  reconcile_state TEXT NOT NULL CHECK(reconcile_state IN('clean','dirty','pending_confirm','reconciling','needs_attention')),
  registered_at_ms INTEGER,
  last_verified_at_ms INTEGER,
  updated_at_ms INTEGER NOT NULL
) STRICT;

CREATE TABLE storylines (
  storyline_id TEXT PRIMARY KEY REFERENCES objects(object_id) ON DELETE RESTRICT,
  workflow_state TEXT NOT NULL,
  current_arc_id TEXT,
  accepted_snapshot_id TEXT,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE arcs (
  arc_id TEXT PRIMARY KEY REFERENCES objects(object_id) ON DELETE RESTRICT,
  storyline_id TEXT NOT NULL REFERENCES storylines(storyline_id) ON DELETE RESTRICT,
  ordinal INTEGER NOT NULL,
  workflow_state TEXT NOT NULL,
  contract_digest TEXT,
  updated_at_ms INTEGER NOT NULL,
  UNIQUE(storyline_id,ordinal)
) STRICT;
CREATE TABLE chapters (
  chapter_id TEXT PRIMARY KEY REFERENCES objects(object_id) ON DELETE RESTRICT,
  storyline_id TEXT NOT NULL REFERENCES storylines(storyline_id) ON DELETE RESTRICT,
  arc_id TEXT REFERENCES arcs(arc_id) ON DELETE RESTRICT,
  ordinal INTEGER NOT NULL,
  workflow_state TEXT NOT NULL,
  current_manuscript_revision_id TEXT,
  current_draft_path TEXT,
  updated_at_ms INTEGER NOT NULL,
  UNIQUE(storyline_id,ordinal)
) STRICT;

CREATE TABLE workflow_runs (
  workflow_run_id TEXT PRIMARY KEY,
  storyline_id TEXT REFERENCES storylines(storyline_id) ON DELETE RESTRICT,
  status TEXT NOT NULL,
  oversight_scope_json TEXT,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE runs (
  run_id TEXT PRIMARY KEY,
  workflow_run_id TEXT REFERENCES workflow_runs(workflow_run_id) ON DELETE CASCADE,
  parent_run_id TEXT REFERENCES runs(run_id) ON DELETE CASCADE,
  role TEXT NOT NULL,
  status TEXT NOT NULL,
  depth INTEGER NOT NULL CHECK(depth>=0),
  provider_id TEXT,
  model_id TEXT,
  prompt_config_id TEXT,
  effective_prompt_digest TEXT,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE tasks (
  task_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
  parent_task_id TEXT REFERENCES tasks(task_id) ON DELETE CASCADE,
  task_kind TEXT NOT NULL,
  status TEXT NOT NULL,
  priority INTEGER NOT NULL DEFAULT 0,
  completion_contract_json TEXT,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE attempts (
  attempt_id TEXT PRIMARY KEY,
  task_id TEXT NOT NULL REFERENCES tasks(task_id) ON DELETE CASCADE,
  attempt_no INTEGER NOT NULL,
  status TEXT NOT NULL,
  started_at_ms INTEGER NOT NULL,
  completed_at_ms INTEGER,
  UNIQUE(task_id,attempt_no)
) STRICT;
CREATE TABLE result_artifacts (
  result_artifact_id TEXT PRIMARY KEY,
  task_id TEXT NOT NULL REFERENCES tasks(task_id) ON DELETE CASCADE,
  status TEXT NOT NULL,
  conclusion_json TEXT,
  findings_json TEXT,
  evidence_json TEXT,
  uncertainty_json TEXT,
  diagnostics_json TEXT,
  freshness_json TEXT NOT NULL,
  produced_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE result_dependencies (
  dependency_id TEXT PRIMARY KEY,
  consumer_task_id TEXT NOT NULL REFERENCES tasks(task_id) ON DELETE CASCADE,
  producer_task_id TEXT NOT NULL REFERENCES tasks(task_id) ON DELETE CASCADE,
  result_artifact_id TEXT REFERENCES result_artifacts(result_artifact_id) ON DELETE SET NULL,
  dependency_kind TEXT NOT NULL CHECK(dependency_kind IN('required','advisory','optional')),
  status TEXT NOT NULL,
  UNIQUE(consumer_task_id,producer_task_id,dependency_kind)
) STRICT;
CREATE TABLE checkpoints (
  checkpoint_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
  task_id TEXT REFERENCES tasks(task_id) ON DELETE CASCADE,
  schema_version INTEGER NOT NULL,
  payload_json TEXT NOT NULL,
  input_digest_set_json TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE approvals (
  approval_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
  task_id TEXT REFERENCES tasks(task_id) ON DELETE CASCADE,
  approval_kind TEXT NOT NULL,
  status TEXT NOT NULL,
  payload_digest TEXT,
  decided_by TEXT,
  decided_at_ms INTEGER,
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE tool_calls (
  tool_call_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
  task_id TEXT REFERENCES tasks(task_id) ON DELETE CASCADE,
  tool_name TEXT NOT NULL,
  arguments_digest TEXT,
  status TEXT NOT NULL,
  side_effect_state TEXT NOT NULL,
  idempotency_key TEXT UNIQUE,
  started_at_ms INTEGER NOT NULL,
  completed_at_ms INTEGER
) STRICT;
CREATE TABLE evidence (
  evidence_id TEXT PRIMARY KEY,
  run_id TEXT REFERENCES runs(run_id) ON DELETE CASCADE,
  task_id TEXT REFERENCES tasks(task_id) ON DELETE CASCADE,
  source_kind TEXT NOT NULL,
  source_id TEXT NOT NULL,
  source_digest TEXT NOT NULL,
  locator_json TEXT NOT NULL,
  stale INTEGER NOT NULL DEFAULT 0 CHECK(stale IN(0,1)),
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE background_tasks (
  background_task_id TEXT PRIMARY KEY,
  owner_run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
  owner_task_id TEXT REFERENCES tasks(task_id) ON DELETE CASCADE,
  kind TEXT NOT NULL,
  status TEXT NOT NULL,
  checkpoint_id TEXT REFERENCES checkpoints(checkpoint_id) ON DELETE SET NULL,
  started_at_ms INTEGER NOT NULL,
  completed_at_ms INTEGER
) STRICT;
CREATE TABLE specialist_profiles (
  specialist_profile_id TEXT PRIMARY KEY,
  scope_kind TEXT NOT NULL CHECK(scope_kind IN('builtin','user','project')),
  project_id TEXT,
  name TEXT NOT NULL,
  version INTEGER NOT NULL,
  definition_json TEXT NOT NULL,
  base_definition_digest TEXT,
  enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_specialist_profile_name ON specialist_profiles(scope_kind,COALESCE(project_id,''),name);

CREATE TABLE run_session_handles (
  handle_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
  worker_instance_id TEXT NOT NULL,
  channel_instance_id TEXT NOT NULL,
  project_scope TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE,
  expires_at_ms INTEGER NOT NULL,
  revoked_at_ms INTEGER,
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE oversight_overrides (
  override_id TEXT PRIMARY KEY,
  scope_kind TEXT NOT NULL CHECK(scope_kind IN('application','project','storyline','task')),
  scope_id TEXT,
  narrative_authority TEXT NOT NULL CHECK(narrative_authority IN('author_confirmed_required','agent_delegated')),
  runtime_permission_mode TEXT NOT NULL CHECK(runtime_permission_mode IN('ask','accept_edits','auto_approve_scoped','bypass_permissions')),
  effective_after_checkpoint_id TEXT,
  created_by TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE delegated_decisions (
  delegated_decision_id TEXT PRIMARY KEY,
  transaction_id TEXT,
  scope_kind TEXT NOT NULL,
  scope_id TEXT NOT NULL,
  proposed_by TEXT,
  confirmed_by TEXT,
  decided_by TEXT NOT NULL,
  authority_kind TEXT NOT NULL,
  oversight_mode TEXT NOT NULL,
  payload_digest TEXT,
  decided_at_ms INTEGER NOT NULL
) STRICT;

CREATE TABLE candidates (
  candidate_id TEXT PRIMARY KEY,
  chapter_id TEXT NOT NULL REFERENCES chapters(chapter_id) ON DELETE RESTRICT,
  submission_kind TEXT NOT NULL,
  source_draft_path TEXT NOT NULL,
  artifact_digest TEXT NOT NULL,
  normalized_digest TEXT,
  status TEXT NOT NULL,
  parent_candidate_id TEXT REFERENCES candidates(candidate_id) ON DELETE RESTRICT,
  barrier_id TEXT,
  prompt_config_id TEXT,
  effective_prompt_digest TEXT,
  content_mode TEXT,
  provider_id TEXT,
  model_id TEXT,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE review_attempts (
  review_attempt_id TEXT PRIMARY KEY,
  scope_kind TEXT NOT NULL CHECK(scope_kind IN('candidate','chapter','arc','storyline','manuscript')),
  scope_id TEXT NOT NULL,
  review_kind TEXT NOT NULL,
  candidate_id TEXT REFERENCES candidates(candidate_id) ON DELETE RESTRICT,
  attempt_no INTEGER NOT NULL,
  reviewer_run_id TEXT REFERENCES runs(run_id) ON DELETE SET NULL,
  reviewer_provenance_stub_id TEXT,
  status TEXT NOT NULL,
  result_json TEXT,
  diagnostics_ref TEXT,
  requested_changes_ref TEXT,
  started_at_ms INTEGER NOT NULL,
  completed_at_ms INTEGER,
  UNIQUE(scope_kind,scope_id,review_kind,attempt_no)
) STRICT;
CREATE TABLE manuscript_revisions (
  revision_id TEXT PRIMARY KEY,
  chapter_id TEXT NOT NULL REFERENCES chapters(chapter_id) ON DELETE RESTRICT,
  candidate_id TEXT NOT NULL REFERENCES candidates(candidate_id) ON DELETE RESTRICT,
  artifact_digest TEXT NOT NULL,
  normalized_digest TEXT,
  transaction_id TEXT NOT NULL,
  supersedes_revision_id TEXT REFERENCES manuscript_revisions(revision_id) ON DELETE RESTRICT,
  materialization_status TEXT NOT NULL,
  accepted_at_ms INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE accepted_snapshots (
  accepted_snapshot_id TEXT PRIMARY KEY,
  storyline_id TEXT NOT NULL REFERENCES storylines(storyline_id) ON DELETE RESTRICT,
  accepted_version TEXT NOT NULL,
  final_review_id TEXT REFERENCES review_attempts(review_attempt_id) ON DELETE RESTRICT,
  manifest_digest TEXT NOT NULL,
  warnings_digest TEXT,
  transaction_id TEXT NOT NULL,
  accepted_at_ms INTEGER NOT NULL,
  UNIQUE(storyline_id,accepted_version)
) STRICT;
CREATE TABLE acceptance_records (
  acceptance_id TEXT PRIMARY KEY,
  scope_kind TEXT NOT NULL CHECK(scope_kind IN('chapter','arc','storyline','final')),
  scope_id TEXT NOT NULL,
  candidate_id TEXT REFERENCES candidates(candidate_id) ON DELETE RESTRICT,
  manuscript_revision_id TEXT REFERENCES manuscript_revisions(revision_id) ON DELETE RESTRICT,
  review_attempt_id TEXT REFERENCES review_attempts(review_attempt_id) ON DELETE RESTRICT,
  accepted_snapshot_id TEXT REFERENCES accepted_snapshots(accepted_snapshot_id) ON DELETE RESTRICT,
  accepted_by_kind TEXT NOT NULL,
  accepted_by_id TEXT,
  warnings_ack_digest TEXT,
  transaction_id TEXT NOT NULL,
  accepted_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE revision_barriers (
  barrier_id TEXT PRIMARY KEY,
  originating_transaction_id TEXT NOT NULL,
  state TEXT NOT NULL,
  affected_set_json TEXT,
  created_at_ms INTEGER NOT NULL,
  resolved_at_ms INTEGER
) STRICT;
CREATE TABLE authority_transactions (
  transaction_id TEXT PRIMARY KEY,
  transaction_kind TEXT NOT NULL,
  idempotency_key TEXT NOT NULL UNIQUE,
  project_submission_state TEXT NOT NULL,
  barrier_id TEXT REFERENCES revision_barriers(barrier_id) ON DELETE RESTRICT,
  initiating_run_id TEXT REFERENCES runs(run_id) ON DELETE SET NULL,
  status TEXT NOT NULL,
  recovery_state TEXT NOT NULL,
  failure_code TEXT,
  started_at_ms INTEGER NOT NULL,
  committed_at_ms INTEGER,
  completed_at_ms INTEGER
) STRICT;
CREATE UNIQUE INDEX ux_single_active_authority_transaction ON authority_transactions((1))
WHERE status IN('submitting','reviewing','resolving','accepting','committing','revalidating');
CREATE TABLE authority_events (
  event_id TEXT PRIMARY KEY,
  event_seq INTEGER NOT NULL UNIQUE,
  transaction_id TEXT NOT NULL REFERENCES authority_transactions(transaction_id) ON DELETE RESTRICT,
  aggregate_type TEXT NOT NULL,
  aggregate_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  event_payload_json TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL
) STRICT;

CREATE TABLE narrative_change_sets (
  change_set_id TEXT PRIMARY KEY,
  scope_kind TEXT NOT NULL,
  scope_id TEXT NOT NULL,
  status TEXT NOT NULL,
  impact_analysis_id TEXT,
  proposer_kind TEXT NOT NULL,
  proposer_id TEXT,
  decider_kind TEXT,
  decider_id TEXT,
  transaction_id TEXT REFERENCES authority_transactions(transaction_id) ON DELETE RESTRICT,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE narrative_changes (
  narrative_change_id TEXT PRIMARY KEY,
  change_set_id TEXT NOT NULL REFERENCES narrative_change_sets(change_set_id) ON DELETE RESTRICT,
  object_id TEXT NOT NULL REFERENCES objects(object_id) ON DELETE RESTRICT,
  change_kind TEXT NOT NULL CHECK(change_kind IN('add','modify','remove','reintroduce')),
  before_revision_ref TEXT,
  before_digest TEXT,
  after_payload_digest TEXT,
  ordinal INTEGER NOT NULL,
  UNIQUE(change_set_id,ordinal)
) STRICT;
CREATE TABLE impact_analyses (
  impact_analysis_id TEXT PRIMARY KEY,
  change_set_id TEXT NOT NULL REFERENCES narrative_change_sets(change_set_id) ON DELETE RESTRICT,
  status TEXT NOT NULL CHECK(status IN('no_relevant_dependency','affected','uncertain','failed')),
  affected_set_json TEXT,
  evidence_json TEXT,
  warnings_json TEXT,
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE dependency_edges (
  edge_id TEXT PRIMARY KEY,
  from_object_id TEXT NOT NULL REFERENCES objects(object_id) ON DELETE RESTRICT,
  to_object_id TEXT NOT NULL REFERENCES objects(object_id) ON DELETE RESTRICT,
  edge_type TEXT NOT NULL,
  validation_status TEXT NOT NULL,
  confidence REAL,
  provenance_ref TEXT,
  source_revision_id TEXT,
  last_validated_at_ms INTEGER,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL
) STRICT;
CREATE INDEX ix_dependency_from ON dependency_edges(from_object_id);
CREATE INDEX ix_dependency_to ON dependency_edges(to_object_id);
CREATE TABLE narrative_state_revisions (
  state_revision_id TEXT PRIMARY KEY,
  scope_object_id TEXT NOT NULL REFERENCES objects(object_id) ON DELETE RESTRICT,
  transaction_id TEXT NOT NULL REFERENCES authority_transactions(transaction_id) ON DELETE RESTRICT,
  snapshot_digest TEXT NOT NULL,
  supersedes_state_revision_id TEXT REFERENCES narrative_state_revisions(state_revision_id) ON DELETE RESTRICT,
  created_at_ms INTEGER NOT NULL
) STRICT;

CREATE TABLE history_entries (
  history_entry_id TEXT PRIMARY KEY,
  relative_path TEXT NOT NULL,
  artifact_digest TEXT NOT NULL,
  label TEXT,
  source_kind TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL
) STRICT;
CREATE INDEX ix_history_path_time ON history_entries(relative_path,created_at_ms DESC);
CREATE TABLE snapshot_blob_leases (
  lease_id TEXT PRIMARY KEY,
  snapshot_kind TEXT NOT NULL,
  snapshot_id TEXT NOT NULL,
  blob_digest TEXT NOT NULL,
  expires_at_ms INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL,
  UNIQUE(snapshot_kind,snapshot_id,blob_digest)
) STRICT;
CREATE TABLE authority_provenance_stubs (
  provenance_stub_id TEXT PRIMARY KEY,
  run_id TEXT,
  role TEXT,
  provider_id TEXT,
  model_id TEXT,
  prompt_config_id TEXT,
  effective_prompt_digest TEXT,
  content_mode TEXT,
  created_at_ms INTEGER NOT NULL
) STRICT;

CREATE TABLE search_documents (
  search_rowid INTEGER PRIMARY KEY,
  object_id TEXT NOT NULL,
  artifact_digest TEXT NOT NULL,
  section_key TEXT NOT NULL,
  title TEXT,
  body TEXT NOT NULL,
  current_status TEXT NOT NULL,
  UNIQUE(object_id,artifact_digest,section_key)
) STRICT;
CREATE VIRTUAL TABLE search_fts USING fts5(
  title, body,
  content='search_documents',
  content_rowid='search_rowid',
  tokenize='unicode61'
);
```

## Notes

- `scope_kind + scope_id` references are deliberately polymorphic; handlers validate kind/target before mutation.
- current pointers (`chapters.current_manuscript_revision_id`, `storylines.accepted_snapshot_id`) are mandatory logical invariants; migration may enforce them by table-recreate FK once ordering is convenient.
- FTS is derived. Base schema uses `unicode61`; English profile may rebuild with `porter unicode61`, Chinese with `trigram`, Japanese remains v1 best effort. Multiple language profiles may use separate derived FTS tables.

## Required DB tests

Partial unique active Authority transaction; idempotency duplicate; RESTRICT ownership; monotonic event sequence; tombstone views; runtime cascade isolation; file-backed WAL/FULL crash; migration checksum mismatch; FTS drop/rebuild without Authority loss.
