# Writing Technical Architecture Spec v0.1 — FROZEN

**日期**：2026-08-13  
**状态**：`TECHNICAL ARCHITECTURE FROZEN`  
**产品基线**：`Writing_Module_Requirements_Checkpoint_v0.5.2_FROZEN.md`  
**技术决策基线**：`Phase3_Technical_Architecture_Grilling_Decisions_v0.2.md`（Q1–Q43）  
**规格冻结决策基线**：`Phase 3B — Architecture Specification Freeze Grilling Decisions v0.1`（Q44–Q122）  
**冻结来源**：`Writing_Technical_Architecture_Spec_v0.1.1_Final_Freeze_Candidate.md`；Q123–Q128 + Final Audit P0/P1 Freeze Patch 已全部吸收  
**冲突优先级**：`Product v0.5.2 FROZEN > 本 Architecture Spec > ADR > Implementation Detail`

> 本文件将已确认的 Product Requirements、Q1–Q128 技术决策与最终审计修补编译为唯一 Technical Architecture 实现基线。  
> 本文件不重新打开已冻结的 Draft / Manuscript Authority、Workflow Gate、Single Frontier、Revision Barrier、Agent Role、Prompt Freedom / Capability Boundary 等产品根语义。  
> Final Conflict / Gap / Failure-mode 审计与机械一致性扫描已完成：未发现新的 Root Architecture Conflict。本文件自此升级为 `TECHNICAL ARCHITECTURE FROZEN`，作为 Phase 4 Implementation Design 的唯一技术架构基线。

---

# 0. Architecture Invariants

以下是任何实现、重构、库替换、性能优化都不得破坏的架构不变量。

1. **Authority Core 是每个 Project 的唯一 Authority Writer。**
2. **UI 与 Agent Runtime 不直接写 `project.db`、`.llmw/` Authority state 或 Manuscript materialization。**
3. **SQLite + immutable artifact store 构成 Runtime Authority Source of Truth；用户可读结构化文件是 deterministic Git-trackable projection，不是无条件直接 Authority。**
4. **DB Commit 是 Authority Change 的唯一逻辑提交点。**
5. **Current Manuscript File 是 Authority 的派生物化，不是 Authority Source。**
6. **Authority World 串行；Draft Workspace 可并发，但同一物理 Draft File 采用单写 lease。**
7. **Prompt Instruction ≠ Capability Grant。Prompt 不得扩大 Runtime Capability。**
8. **Core 必须独立复核调用者的 Run Identity / Capability；不得相信 IPC payload 自报 Role。**
9. **Project 内容存在 ≠ 获得执行授权；Project Trust 与 Extension Activation 分离。**
10. **Git ≠ Narrative Authority；Local History ≠ Narrative Revision；Autosave ≠ Acceptance。**
11. **Event Stream 是 Authority logical audit / reconstruction source，但不是与 DB 物理独立的灾难恢复副本。**
12. **Project UUID 表示逻辑项目身份；Workspace Instance ID 表示本机物理工作副本身份。**
13. **任何 Archive / Backup 必须通过一致快照与 reachability closure 构建，不能简单复制正在运行的混合状态。**
14. **Secrets 不进入 Project、Git、Prompt provenance 明文、Checkpoint、Trace 或 Debug Export。**
15. **v1 正式支持 Windows；其他平台不进入 v1 架构验收。**
16. **Narrative Oversight / Delegated Authority 与 Tool Permission 是正交状态；`AUTO` 不等于绕过 Workflow，`Full Accept/Bypass` 不等于取得 Narrative Authority。**
17. **Registry 是 Normal Agent Retrieval Surface 的强制门；未登记/不可用对象不得仅因磁盘存在或 FTS 命中而进入普通 Retrieval。**
18. **Core 授权必须绑定已认证 channel/principal 与 Core-issued Run Session identity；仅提供一个合法 `runId` 不足以证明调用者有权代表该 Run。**
19. **WebView2 Renderer 视为不可信 presentation/editor domain；不得直接持有 Core pipe、credential、filesystem/shell/MCP capability。**
20. **Shell / arbitrary executable 必须位于 OS-enforced sandbox boundary 内；Job Object 只承担资源/生命周期约束，不能单独充当 filesystem/network security boundary。**
21. **Model/Provider 的高风险推理模式不得超过离线 Task-specific Capability Certification ceiling；运行时只允许自主降级，不允许自行升级。**
22. **Authority Domain 不只包含 Chapter：Narrative Change、Arc/Storyline Review/Acceptance、Final Acceptance 与 Accepted Snapshot 必须具有可持久化、可审计的一等表示。**
23. **Backup / Archive 构建期间必须 pin 其 snapshot-reachable blobs；GC 不得删除正在被一致快照引用的 artifact。**

---

# 1. Technology Baseline

## 1.1 Desktop Runtime

```text
Windows v1
├─ .NET / C# backend
├─ WinUI 3 host
└─ WebView2 UI/editor surface
```

UI 采用 Web 技术栈主要用于 Editor 生态与复杂前端交互；所有 privileged local operations 由 .NET backend 承担。

## 1.2 Process Topology

```text
┌──────────────────────────────────────────────┐
│ UI Process                                   │
│ WinUI 3 + WebView2                           │
│ Views / Editor / Diff / Diagnostics / UX     │
└───────────────────┬──────────────────────────┘
                    │ Named Pipes
                    ▼
┌──────────────────────────────────────────────┐
│ Authority Core                               │
│ Project lifecycle / SQLite / Registry        │
│ Authority FSM / transaction / projection     │
│ File API / watcher / reconcile / history     │
└───────────────────┬──────────────────────────┘
                    │ versioned IPC
                    ▼
┌──────────────────────────────────────────────┐
│ Agent Runtime                                │
│ Orchestrator / RunState / Prompt Compiler    │
│ Context / Retrieval / Provider / MCP         │
└───────────────────┬──────────────────────────┘
                    │ spawn
                    ▼
            ┌──────────────────┐
            │ Worker Process   │  1 per Run
            │ restricted       │
            └──────────────────┘
```

## 1.3 Process Ownership

| Resource / Operation | UI | Authority Core | Agent Runtime | Worker |
|---|---:|---:|---:|---:|
| `project.db` read/write | No direct | **Owner / only writer** | No direct | No |
| `.llmw/` authority files | No write | **Owner** | No direct | No |
| Draft read | Native UI host direct trusted read; WebView via typed host bridge | Yes | via API/tool | via tool |
| Draft write | via Core API | **serialized owner** | via Core API | via tool |
| Manuscript materialization | Read/reference | **only writer** | Read via API | Read via tool |
| Registry mutation | No | **owner** | request only | request only |
| Provider calls | No | No | **owner** | delegated/run-scoped |
| Shell / MCP | No | No | broker | restricted worker |

### Core crash

UI 保持打开并显示 disconnected；Core 可重启并自动 reconnect。Project Authority 操作在 Core 不可用期间停止。

### Agent Runtime crash

Draft 编辑继续可用。若崩溃 Run 正持有 Active Authority Submission，则 **Project Submission Lock 必须保留**，直到 Resume / Cancel / Recovery；其他情况下 Authority 操作不受影响。

## 1.4 WebView2 Trust Boundary

Native WinUI/.NET host 与 WebView2 renderer 不属于同一授权域：

```text
Trusted Native UI Host
        ↓ typed narrow bridge
Untrusted WebView Renderer
```

强制规则：

- WebView Renderer 不得直接连接 Authority Core Named Pipe；
- 不向 WebView 暴露 generic native proxy / unrestricted host object；
- WebMessage / host-object 参数必须 schema validate；
- 每次 native bridge 调用校验当前 document origin/source；
- navigation 默认限制为 application-owned origin/resource；外链由系统浏览器或显式受控流程打开；
- Project Markdown/DOCX/HTML 派生内容一律视为不可信内容，不因被 Editor 展示而获得 native capability；
- Renderer compromised 时，最多能请求已定义的 UI operation，不能直接获得 filesystem / shell / MCP / credential / Authority capability。

---

# 2. Instance Identity / Project Identity

## 2.1 Project UUID

`Project UUID` 是逻辑项目身份：

- 创建 Project 时生成；
- 整目录移动保持不变；
- Project Archive round-trip 默认保持不变；
- Narrative Object / Authority provenance 依赖该身份。

## 2.2 Workspace Instance ID

为避免同一 Project 的合法副本产生本机实例冲突，新增：

```text
Workspace Instance ID
= local physical workspace identity
= Project UUID + canonical physical location 的本地稳定映射
```

它用于：

- Pipe name；
- 当前打开实例 lock；
- 本机 workspace state；
- second-instance detection。

它**不进入 Narrative Authority identity**。

Pipe 命名：

```text
llmw-<appid>-<workspace-instance-id>
```

同一物理 Workspace 同时只允许一个 Core owner；第二 App Instance 请求打开时通知并聚焦已有实例。

## 2.3 Trust Identity

Project Trust 的逻辑主体以 Project UUID 为主；canonical path 是本地 workspace observation，不作为唯一逻辑身份。Executable extension / MCP / Skill script activation 额外绑定内容 digest；其内容变化会使 activation 失效。

---

# 3. Project Physical Layout

Canonical layout：

```text
Project/
├─ project.llmw.json
├─ AGENTS.md
│
├─ Manuscript/
│  └─ current/
│     └─ <chapter-id>.<ext>
│
├─ Draft/
│  └─ <chapter-uuid>/
│     └─ ...
│
├─ Raw/
├─ Narrative/
├─ Reviews/
├─ Auxiliary/
│
└─ .llmw/
   ├─ project.db
   ├─ objects/
   ├─ candidates/
   ├─ authority/
   ├─ transaction/
   ├─ backup/
   └─ history/
```

## 3.1 Directory Semantics

### `Manuscript/current/`

用户可见 Current Manuscript materialization。只允许 Authority Core 写。

### `Draft/`

自由工作区。目录按 Chapter UUID 建立，不因标题 rename 改 identity。

### `Narrative/`

**Git-trackable deterministic structured projection surface**。它承载 Canon / Outline / Contract / durable Narrative State 等长期项目语义的用户可读、可 diff 表示。

SQLite 是 transactional Authority Source of Truth；`Narrative/` projection 是：

```text
SQLite Authority
→ deterministic projection
→ Git / external tools / human-readable project surface
```

外部修改 projection 后：

```text
External Structured Mutation
→ Dirty / Reconcile
→ Semantic Diff
→ Dependency / Workflow Validation
→ Authority update only through formal transition
```

不得通过直接写 projection 绕过 Authority。

### `.llmw/authority/`

仅用于 authority staging / recovery manifest / materialization coordination。**Current Manuscript 不存放在此目录。**

### `.llmw/candidates/`

候选相关 logical references / manifest surface；实际 artifact bytes 与 Manuscript historical artifact **共享 `.llmw/objects/` 内容寻址库**，避免重复存储。

### `.llmw/backup/`

仅用于 backup staging / local recovery manifest / snapshot lease metadata；**默认真实自动备份仍位于 Project 外用户可配置位置**，不得把该目录理解成唯一灾备副本。

## 3.2 Application-level State

普通安装：`%LOCALAPPDATA%` 存：

- application settings；
- Provider profile；
- Prompt Override；
- Prompt shipped-version cache/history；
- logs / cache；
- credential handles；
- workspace-instance mapping。

Portable 版统一重定向到可执行文件旁 `data/`。

## 3.3 Symlink / Junction

Project surface 不允许应用主动创建 junction/symlink。已有 reparse point 可只读识别；写操作授权前必须 resolve final path 并拒绝穿透 Project 安全边界。

---

# 4. Project Descriptor / Versioning

`project.llmw.json` 最低字段：

```json
{
  "projectId": "<uuid-v7>",
  "formatVersion": 1,
  "schemaVersion": 1,
  "createdByAppVersion": "...",
  "lastOpenedByAppVersion": "..."
}
```

其中：

- `formatVersion`：项目物理布局 / Archive/Project container format；
- `schemaVersion`：Git-trackable structured projection / project structured schema version；
- SQLite `PRAGMA user_version`：DB schema version；
- 三者独立演进，并由 migration coordinator 显式映射兼容区间。

未来版本 Project 被旧 App 打开时：

```text
REFUSE WITHOUT MUTATION
```

不提供旧版 read-only 自动打开，避免旧版本产生错误派生状态。

Migration：

1. open preflight；
2. forced backup；
3. Core-owned migration transactional run；
4. schema + descriptor update；
5. health check；
6. open Project。

Core-owned declarative migrations 可自动执行。**Project Extension executable migration hook 不得在 Trust + Activation 前自动运行。**

Historical Candidate / Manuscript blob 永不 migration。

---

# 5. Persistence Architecture

## 5.1 SQLite

每 Project 一个 `project.db`。

```text
journal_mode = WAL
synchronous = FULL
busy_timeout = 5000ms
```

Authority Core 是唯一 writer；其他进程不直接连接为写者。

FTS、RunState、History metadata 可以与 Authority 表同库，但必须按 table-family 分类 authoritative / derived / runtime。

## 5.2 Table Families

### Core Object Identity

`objects`

- `object_id TEXT(36) PK`
- `object_type`
- `created_at_ms`
- `updated_at_ms`
- `revision_no`
- `schema_version`
- `status`
- `deleted_at_ms NULL`

Authority / Narrative 对象删除使用 tombstone，不 hard delete。

### Registry Entry

`registry_entries`

- `registry_entry_id`
- `object_id`
- `object_type`
- `schema_version`
- `registration_state` (`REGISTERED` / `UNREGISTERED` / `MISSING` / `IGNORED`)
- `retrieval_availability` (`AVAILABLE` / `UNAVAILABLE` / `STALE`)
- `trusted_physical_digest NULL`
- `trusted_semantic_digest NULL`
- `reconcile_state`
- `registered_at_ms`
- `last_verified_at_ms NULL`

**Registry controls Normal Retrieval Surface.** FTS/Search 结果必须先经过 Registry availability filter；磁盘存在、projection 存在或 FTS 命中均不能绕过该门。

`object_paths`

- `path_id`
- `object_id`
- `relative_path`
- `kind`
- `is_canonical`
- `physical_digest`
- `semantic_digest`
- `updated_at_ms`

Registry 支持一个 Object 多 Artifact/Path。

### Manuscript Revision

`manuscript_revisions`

- `revision_id`
- `chapter_id`
- `candidate_id`
- `artifact_digest`
- `normalized_digest`
- `transaction_id`
- `supersedes_revision_id NULL`
- `accepted_at_ms`
- `materialization_status`
- `created_at_ms`

Current pointer 单独维护，不依赖文件路径推断。

### Candidate

`candidates`

- `candidate_id`
- `chapter_id`
- `submission_kind` (`NORMAL` / `HISTORICAL_REVISION` / `REMEDIATION`)
- `source_draft_path`
- `artifact_digest`
- `normalized_digest`
- `status`
- `parent_candidate_id NULL`
- `barrier_id NULL`
- `prompt_config_id NULL`
- `effective_prompt_digest NULL`
- `content_mode NULL`
- `provider_id NULL`
- `model_id NULL`
- `created_at_ms`
- `updated_at_ms`

### Review Attempt

`review_attempts`

- `review_attempt_id`
- `review_scope_type` (`CHAPTER_CANDIDATE` / `ARC` / `STORYLINE_MANUSCRIPT` / `NARRATIVE_CHANGE_SET`)
- `review_scope_id`
- `review_kind`
- `candidate_id NULL`
- `attempt_no`
- `reviewer_run_id`
- `status`
- `result`
- `diagnostics_ref`
- `requested_changes_ref NULL`
- `started_at_ms`
- `completed_at_ms NULL`

Review 不只属于 Chapter Candidate；Arc Closure、Final Full Manuscript Review、Narrative Change Impact/Review 均必须使用同一可审计 review aggregate。

### Acceptance Record

`acceptance_records`

- `acceptance_id`
- `acceptance_scope_type` (`CHAPTER` / `ARC` / `STORYLINE_MANUSCRIPT` / `NARRATIVE_CHANGE_SET`)
- `acceptance_scope_id`
- `candidate_id NULL`
- `chapter_id NULL`
- `manuscript_revision_id NULL`
- `review_attempt_id NULL`
- `accepted_snapshot_id NULL`
- `accepted_by_kind` (`AUTHOR_CONFIRMED` / `AGENT_DELEGATED`)
- `accepted_by_id NULL`
- `oversight_mode`
- `warnings_ack_digest NULL`
- `transaction_id`
- `accepted_at_ms`

Reviewer 只产出 Review Result；Acceptance 必须经过独立 Authority transition，并记录 Human / Delegated provenance。

### Accepted Snapshot

`accepted_snapshots`

- `accepted_snapshot_id`
- `storyline_id`
- `accepted_version`
- `accepted_at_ms`
- `final_review_attempt_id`
- `manifest_digest`
- `warnings_digest`
- `authority_root_digest`
- `supersedes_snapshot_id NULL`
- `transaction_id`

Final Acceptance 产生逻辑不可变 Accepted Snapshot；后续修改进入 Post-Acceptance Revision，不能改写旧 Snapshot。

### Narrative Change Set / Change

`narrative_change_sets`

- `change_set_id`
- `scope_storyline_id`
- `status`
- `proposed_by_kind`
- `proposed_by_id NULL`
- `decision_authority_kind`
- `oversight_mode`
- `transaction_id NULL`
- `created_at_ms`
- `applied_at_ms NULL`

`narrative_changes`

- `change_id`
- `change_set_id`
- `object_id`
- `change_kind` (`ADD` / `MODIFY` / `REMOVE` / `REINTRODUCE`)
- `before_revision_ref NULL`
- `after_payload_ref NULL`
- `dependency_assessment_status`
- `impact_analysis_ref NULL`
- `created_at_ms`

Confirmed/Accepted Narrative Object 的移除/修改不是普通 CRUD；正式提交单位允许一次 Change Set 涉及多个对象。

### Dependency Edge

`dependency_edges`

- `edge_id`
- `from_object_id`
- `to_object_id`
- `edge_type`
- `validation_status`
- `confidence NULL`
- `provenance_ref`
- `source_revision_id NULL`
- `last_validated_at_ms NULL`
- `created_at_ms`
- `updated_at_ms`

`Needs Revalidation` 是 edge-level validation status；对象 / Chapter 层只做聚合显示。

### Narrative State Revision

`narrative_state_revisions`

- `state_revision_id`
- `scope_object_id`
- `transaction_id`
- `snapshot_digest` 或 deterministic payload reference
- `supersedes_state_revision_id NULL`
- `created_at_ms`

### Authority Transaction

`authority_transactions`

- `transaction_id`
- `transaction_kind`
- `idempotency_key UNIQUE`
- `project_submission_state`
- `barrier_id NULL`
- `initiating_run_id NULL`
- `status`
- `started_at_ms`
- `committed_at_ms NULL`
- `completed_at_ms NULL`
- `recovery_state`
- `failure_code NULL`

### Authority Event

`authority_events`

- `event_id`
- `event_seq INTEGER UNIQUE`
- `transaction_id`
- `aggregate_type`
- `aggregate_id`
- `event_type`
- `event_payload_json`
- `created_at_ms`

事件必须足以在**数据库逻辑仍可读取**时重建 Current State tables / Registry / Dependency State。

> Event Stream 不是与 SQLite 物理隔离的 Disaster Recovery 副本。整库物理丢失/不可恢复损坏必须依赖 Backup。

### Runtime / Derived Families

- `workflow_runs`
- `runs`
- `tasks`
- `attempts`
- `completion_contracts`
- `result_artifacts`
- `result_dependencies`
- `background_tasks`
- `checkpoints`
- `tool_calls`
- `approvals`
- `oversight_overrides`
- `decision_provenance`
- `evidence`
- `history_entries`
- FTS virtual tables

其中 FTS 为 rebuildable derived state；Run / Trace retention 可清理，不承担 Authority 灾备责任。

---

# 6. Git-trackable Structured Projection

这是 SQLite Authority 与 Product VCS 语义之间的正式桥接层。

## 6.1 Principle

```text
Runtime Transactional Truth
= SQLite + immutable artifacts

VCS / Human-readable Durable Projection
= Project structured files
```

长期项目语义必须能够以 deterministic projection 形式进入 Git，例如：

- Canon；
- Narrative Objects；
- Narrative State；
- Arc / Chapter Contracts；
- Outline；
- Narrative Obligations；
- dependency durable view；
- Registry durable view（只投影长期登记/类型/schema/retrieval baseline；runtime lock/session state 不投影）；
- Project-level custom content / schemas。

Projection format 的具体 schema 文件名可在 Implementation Design 细化，但必须满足：

1. deterministic serialization；
2. stable object identity；
3. semantic filename；
4. diff-friendly text；
5. external mutation detectable；
6. projection rebuildable from Authority；
7. projection mutation不能直接覆盖 Authority。

## 6.2 Projection Commit

Authority DB commit 完成后，projection 可异步/紧随其后 materialize。Projection materialization failure 与 Manuscript materialization failure一样进入 Dirty/Repair，不反向撤销已 commit 的 Authority。

下一次 Authority Operation 前必须保证 required Authority materializations/projections 已恢复到可验证状态。

---

# 7. Immutable Object Store

## 7.1 Addressing

SHA-256 digest 作为 artifact address：

```text
.llmw/objects/ab/cdef...
```

存储流程：

```text
write temp
→ flush
→ verify digest
→ atomic rename
```

相同 bytes 自动去重。

## 7.2 Representations

TXT/MD Candidate：

- raw bytes blob；
- normalized representation blob/digest。

DOCX Candidate：

- original `.docx` artifact blob；
- normalized review/search representation blob；
- mapping anchors metadata。

## 7.3 Retention

- Accepted Candidate：永久；
- Manuscript historical revision：永久；
- Failed Candidate：默认 30 天，可配；
- Cancelled Candidate：默认 7 天；
- Local History-only blob：可 GC。

GC 采用 DB reference count + periodic mark/orphan sweep；**Authority-reachable blob 永不自动删除。**

---

# 8. Authority State Machines

## 8.1 Project Submission State

```text
IDLE
  ↓ Submit
SUBMITTING
  ↓ Candidate persisted
REVIEWING
  ├─ Review FAIL / Cancel before Accept → IDLE
  ↓ review result accepted for resolution
RESOLVING
  ├─ Cancel before Accept              → IDLE
  ↓ acceptance begins
ACCEPTING               (cancel no longer allowed)
  ↓ commit protocol
COMMITTING
  ↓ dependency / materialization validation
REVALIDATING
  ↓ clean
IDLE
```

## 8.2 Candidate State

```text
CREATED
→ UNDER_REVIEW
→ FAILED | CANCELLED | ACCEPTED
→ SUPERSEDED (when replaced by later accepted lineage where applicable)
```

Retry 总是 New Candidate，并通过 `parent_candidate_id` / provenance link 指向上一 Candidate。

## 8.3 Chapter State

```text
OUTLINE_CONTRACT
→ READY
→ DRAFT
→ SUBMITTED
→ UNDER_REVIEW
├─ FAILED → DRAFT / NEW CANDIDATE lineage
└─ ACCEPTED → MATERIALIZED
```

`FAILED` 绝不流向 `MATERIALIZED`。Retry 必须创建 New Candidate。

## 8.3.1 Higher-scope Authority Aggregates

Architecture 必须同时表示：

```text
Arc
→ Arc Review / Closure
→ Arc Acceptance

Storyline / Manuscript
→ Final Full Manuscript Review
→ Final Acceptance
→ immutable Accepted Snapshot
→ optional Post-Acceptance Revision lineage
```

这些 higher-scope Gate 与 Chapter Acceptance 共用 Authority Core / audit / transaction framework，但不能被压缩成 Chapter-specific candidate state。Final Acceptance 的 scope 是 Storyline/Manuscript，不是整个 Project。

## 8.4 Revision Barrier

```text
INACTIVE
→ ACTIVE_INITIAL
→ RESOLVING
→ INACTIVE
```

Historical Revision Submit 建立保守 Project-level Barrier。若 Review FAIL / Cancel 且尚未改变 Authority，则释放该次 Barrier；一旦历史 Authority 已 commit，Barrier 只能在 Affected Set 全部 revalidation / remediation 收敛后释放。

Remediation Candidate 必须带 `barrier_id` / originating transaction identity。

## 8.5 Eligibility

Normal Submission Eligibility 与 Historical Revision Eligibility 为两个资格源，但共用同一 submission pipeline 和 Project Submission Lock。

---

# 9. Atomic Authority Commit Protocol

## 9.1 Logical Commit Point

**SQLite Commit 是唯一 Authority linearization point。**

Canonical protocol：

```text
1. Validate eligibility + active lock
2. Prepare immutable artifact blob
3. Verify artifact digest
4. Begin SQLite transaction
5. Persist Candidate/Review/Acceptance/AcceptedSnapshot/NarrativeChange/Revision/State/Dependency mutations as applicable
6. Append Authority Event(s)
7. Update current pointers / transaction record
8. COMMIT SQLite                ← Authority becomes committed here
9. Materialize Current Manuscript
10. Materialize structured projection
11. Verify materialization digests
12. Mark transaction COMPLETE
```

## 9.2 Crash Semantics

### Before DB commit

Rollback / no Authority change. Prepared orphan blob 可后续 GC。

### After DB commit, before materialization

Authority 已改变；Project enters `MATERIALIZATION DIRTY / NEEDS_REPAIR`。Recovery roll-forward 重建 Manuscript / projection。

### During materialization

使用 same-volume temp + flush/fsync + atomic replace；重跑 materialization 必须幂等。

### Recovery failure

进入：

```text
RECOVERY REQUIRED
→ read-only Authority
→ salvage/export allowed
```

Draft 编辑是否可用取决于 Project filesystem health；不得允许新的 Authority operation。

## 9.3 Idempotency

每次 Authority transaction：

- `transaction_id` = UUID；
- client-generated `idempotency_key`；
- event 带 transaction ID；
- resume/retry 必须查重。

---

# 10. Event Stream / Disaster Recovery Boundary

Authority Event Stream 是：

- immutable logical audit；
- state table reconstruction source；
- provenance source。

它**不是**物理独立于 `project.db` 的灾备日志。

因此：

```text
Logical inconsistency / state tables damaged
→ rebuild from readable event stream

Whole project.db lost/unrecoverably corrupted
→ restore consistent Backup
→ replay post-backup readable events only if available
```

不得声称在 DB 与 Backup 均丢失时只凭同库 event table 恢复完整 Authority。

---

# 11. Backup Architecture

Backup unit 必须包含：

```text
SQLite consistent snapshot
+
all Authority-reachable immutable blobs
+
project.llmw.json
+
required durable project/projection files
```

不能只保存 DB 中的 blob reference 而不保存对应 Authority artifact。

默认：

- 每次正常会话关闭；
- 每日；
- 保留 5 份；
- 默认放 Project 外；
- 用户可配置位置。

SQLite 使用 consistent backup snapshot；Blob 根据 snapshot reachability closure 复制/校验。创建 snapshot 后必须建立 `snapshot_blob_lease`（或等价 pin 集），在 closure copy/verify 完成前 GC 不得删除 snapshot-reachable blob；完成/失败清理 lease。

Project Open health check：

- DB integrity；
- pending transaction；
- missing Authority blob；
- Registry digest；
- Manuscript materialization digest；
- projection materialization digest。

---

# 12. Project Archive / Final Package

## 12.1 Archive Projection

Project Archive **不得直接复制 live `project.db`** 后又声称排除某些 table-family。

Archive 流程：

```text
Live DB
→ consistent snapshot
→ Archive DB Projection
→ include/exclude requested table families
→ calculate reachable blob closure
→ pack user project files + projected DB + reachable blobs
```

默认包含：

- Manuscript / Draft / Raw / Narrative / Reviews / Auxiliary / AGENTS.md；
- project descriptor；
- Authority DB projection；
- Candidate / Manuscript Authority blobs。

默认排除：

- Local History records/blobs；
- Agent Run History；
- cache/log/temp/transaction scratch。

用户选择 Include History 时才把对应 table-family 与 history-only blob 加入 reachability closure。即使默认排除 Agent Run History，也必须保留被 Authority record 引用的最小 immutable provenance stub（run_id / role / provider-model identity / prompt config digest / timestamps），避免 `reviewer_run_id` 等 Authority 引用变成语义悬空。Archive snapshot 同样建立 blob lease，打包完成前 GC 不得回收 snapshot-reachable artifact。

## 12.2 Final Package

仅包含成稿 + deterministic integrity manifest。Manifest 最少包含：

- Snapshot ID；
- Storyline ID；
- Accepted Version；
- Accepted At；
- Logical File List；
- per-file Content Digest；
- Final Review ID；
- Warnings at Acceptance；
- Format / Manifest Version。

整个 ZIP digest 可以附加，但不能作为唯一逻辑身份。修改后的 Package 仍可使用，但 verification 状态必须变为 `MODIFIED AFTER FINAL ACCEPTANCE`。数字签名 / TSA 后置，不要求物理不可修改；v1 manifest schema 仍预留 `signature_algorithm / key_id / signature / trusted_timestamp` 可选字段，未启用时为空。

---

# 13. IPC Contract

## 13.1 Transport

Named Pipes，当前 Windows user ACL；推荐 `CurrentUserOnly` / `PipeSecurity` 约束同用户连接。

Pipe 不允许 child process 继承 Core bootstrap credential。

## 13.2 Authentication

Pipe ACL 只证明 OS user；还需要 process/bootstrap authentication。

- UI bootstrapper 为 Core / Agent Runtime 分别生成一次性随机 token；
- token 不通过可观察 command line 传递；
- 不同 channel 使用不同 token；
- Worker 不继承 Core bootstrap secret。

## 13.3 Message Format

Length-prefixed UTF-8 JSON：

```json
{
  "protocolVersion": 1,
  "messageType": "...",
  "requestId": "...",
  "correlationId": "...",
  "projectId": "...",
  "workspaceInstanceId": "...",
  "runId": "...",
  "timestamp": 0,
  "payload": {}
}
```

限制：

- max message：1 MB；
- large content 使用 blob/artifact reference；
- default request timeout：30s；
- cancellation 通过 correlationId；
- reconnect = snapshot + monotonic event sequence；
- slow consumer 不得阻塞 Core；gap 必须显式标记并触发 snapshot refresh。

## 13.4 Protocol Version

每进程声明 `[min,max]`；选择最高公共版本；无交集 refuse。新字段向后兼容；未知 message type 返回 structured protocol error。

---

# 14. Core-owned Caller Identity / Capability Verification

这是 Q128 的强制安全修补。

Agent Runtime / Worker **不得通过 IPC 自己声明 Role 或 Capability 并获得信任**。

Core 维护 authoritative identity mapping：

```text
RunId
→ WorkflowRun
→ Agent Role
→ Parent Run
→ Permission Mode
→ Role Capability
→ Tool/Extension Grants
→ Project Scope
```

Core 定义三类 caller principal：

```text
USER_INTERACTIVE
AGENT_RUN
CORE_INTERNAL
```

Agent Runtime 建立 Run 时，由 Core 创建不可伪造的 `RunSessionHandle`，绑定：

```text
authenticated channel/session
+ runId
+ worker/process instance
+ project/workspace scope
+ expiry/generation
```

IPC 请求可以携带 `runId / workerId / requested operation` 作为 routing/provenance，但 Core 必须先验证当前 channel/session 的 RunSession binding；**知道另一个合法 runId 不构成代表该 Run 的权限。**

Core 从 durable records 计算：

```text
Effective Capability =
Product Capability
∩ Role Capability
∩ Permission Mode
∩ Tool Permission
∩ Extension Permission
```

任何 caller-supplied `role=...` / `capability=...` 都不得作为授权依据。UI 发起 Author-confirmed / Trust / Reconcile 等操作时使用 `USER_INTERACTIVE` principal，并记录交互 provenance；不能伪装成 Agent Run。

---

# 15. Agent Runtime Object Model

正式对象：

- WorkflowRun；
- Run；
- Task；
- Attempt；
- AgentInstance；
- WorkerProcess；
- Checkpoint；
- ResultArtifact；
- Evidence；
- ToolCall；
- Approval。

关系：

```text
WorkflowRun
└─ Run
   ├─ Task
   │  └─ Attempt
   ├─ AgentInstance
   ├─ WorkerProcess
   └─ Checkpoint
```

每 Run 一个独立 worker；sub-agent 对应独立 WorkerProcess。Orchestrator 位于 Agent Runtime 主进程。

### Task Completion Contract / Result Artifact

每个正式 Task 必须有结构化 Completion Contract；完成后默认产出 Task Result Artifact，而不是把完整 transcript 作为下游交接接口。

Result Artifact 至少包含：

- Task / Status / Conclusion；
- Findings / Diagnostics；
- Evidence refs；
- Uncertainty；
- Affected Narrative Objects；
- Recommended Follow-ups；
- Produced Against / freshness metadata；
- Proposed Change Set ref（若有）。

Result Artifact **不自动成为 Canon/Authority**。Result Dependency 区分 Required / Advisory / Optional；Required 结果 stale/缺失时下游 Task 必须阻塞或 replan。

### Background Tasks

Background Task 是正式 Runtime 对象，必须持久化 owner Run、status、duration、tool/sub-agent identity、checkpoint/recovery state，并可被用户查看/Stop。Background execution 不改变 Workflow Gate。

### Specialist Profile Registry

Persistent Specialist Profile 仅有三种 scope：

```text
BUILT_IN
USER_LIBRARY
PROJECT
```

Task/Session 临时 sub-agent 是 Runtime instance，**不是**用户可配置的 persistent Specialist。Profile 至少保存：

- stable profile id / name / version；
- base role / purpose；
- applicable stages；
- behavioral prompt reference/override；
- requested tool/capability envelope（仍只能收窄或请求，不直接授权）；
- scope / provenance / enabled state。

Built-in Base Definition 保持 system-owned immutable；用户可通过 application-level Behavioral Prompt Override、Duplicate 或 Explicit Override 定制，不原地改写 Base Definition。Custom Specialist Test Run v1 提供，但仅用于可选验证，不阻断使用。

### Agent Tree Limits / Runtime Grill

Agent tree 必须有有限的 concurrency 与 maximum depth enforcement；默认并发由 Performance Baseline 给出，具体 depth 默认值可在 Implementation Design 调整，但不得允许无限递归 delegation。

当 Approved Plan / Delegated Authority 已不足以唯一决定下一步时：

```text
Pause
→ Pending Decision / Approval
→ Runtime Grill
→ Author or valid Delegated Decision
→ update execution state
→ Resume
```

如果现有 Plan / Delegated Authority 已足以唯一决定，则允许 execution-level replan；不得借 replan 改写已批准的产品/创作决策 provenance。

Checkpoint：

- Plan；
- Task DAG；
- Agent state；
- compacted messages；
- recent critical messages；
- truncated Tool Results；
- approvals；
- context pointer；
- artifact/evidence refs；
- source digests；
- prompt config / provider identity。

Secrets 不入 checkpoint。

Side-effect tool call 必须有 idempotency record；`UNKNOWN side effect` 禁止自动 retry。

---

# 16. Freshness / Resume / Context

Task Input Digest Set：

- Authority revision；
- relevant Object digests；
- Prompt Config ID；
- Effective Prompt digest；
- AGENTS digest；
- Skill versions/digests；
- Provider/model identity。

Resume classification：

```text
unchanged                 → CONTINUE
inputs changed, plan valid → REPLAN
plan invalid               → RESTART TASK
structural invalidation    → RESTART RUN
```

非 Task input 的 Draft 变化不自动 stale。

Evidence：`source digest + locator + object/artifact identity`；Source digest 变化自动标 stale。

Compaction summary 固定包含：

- task goal；
- confirmed decisions；
- progress；
- unresolved items；
- evidence refs。

不得只依赖 summary 保存：

- user decisions；
- Authority facts；
- unresolved decision records；
- tool provenance；
- permission/approval records。

Fresh Reviewer 不继承 Writer conversation history。

---

# 17. Retrieval / FTS5

v1 仅词法检索，不引入 vector/rerank。Normal Retrieval 的第一步永远是 `Registry Availability Filter`；只有 `REGISTERED + AVAILABLE`（或明确允许的 special retrieval mode）对象才能进入后续 FTS/Search ranking。

Tokenizer：

- 中文：FTS5 `trigram`；
- 英文：FTS5 `porter` over `unicode61`；
- 日文：best-effort，后续单独优化。

> 不使用不存在的内置 `unigram` tokenizer 名称。

Index unit：section / paragraph chunk。

Stable chunk identity 由 Object UUID + source/artifact identity + deterministic locator 派生；不能仅靠每次变化的 digest 作为唯一长期定位标识。

Index row 至少带：

- object UUID；
- artifact digest；
- revision/current status；
- chunk locator。

检索面：

- Current Manuscript：默认；
- Superseded：默认隐藏/降权；
- Draft：显式 Draft Retrieval；
- Raw：独立 index，不进普通 retrieval；
- Manual Add to Context：pin artifact + digest，变化即 stale。

Authority current index update 与 Authority transaction 协调，但 **FTS 写入/重建失败不能回滚已经合法 commit 的 Narrative Authority**；失败时 index 标 DIRTY 并异步/启动时重建。FTS 本身仍是 rebuildable derived state。

---

# 18. Role Capability Matrix

标记：`A` = role 最大能力允许；`S` = scoped / 只能通过专用 API、仍受 Workflow/Permission Gate；`-` = role 不拥有。

| Capability | PM / Main Orchestrator | Data Ops | Story Planner | Writer | Reviewer | Researcher |
|---|---:|---:|---:|---:|---:|---:|
| ProjectFile.Read | A | A | A | A | A | A |
| Draft.Write | S | - | - | **A via Draft API** | - | - |
| Raw.Write | S | A | - | - | - | S |
| Structured.Write | S | **A scoped** | **A planning scope** | - | - | - |
| Authority.Submit | **S orchestration** | - | - | **S eligible Draft only** | - | - |
| Authority.Review | - | - | - | - | **A review scope** | - |
| Authority.Accept | **S only when Author-confirmed or Delegated Authority permits** | - | - | - | - | - |
| Registry.Query | A | A | A | A | A | A |
| Registry.Mutate | - | **A via Core** | - | - | - | - |
| Web.Search | A | A | A | A | A | A |
| Network.Request | S | S | S | S | S | S |
| Shell.Execute | S | S | S | S | S | S |
| Script.Execute | S | S | S | S | S | S |
| Git.Execute | **S explicit user task only** | - | - | - | - | - |
| MCP.Call | S | S | S | S | S | S |
| Agent.Spawn | A | S | S | S | S | - |

说明：

1. 上表是 role maximum，不代表默认自动批准；最终能力仍取交集。
2. Writer 不获得 generic filesystem write；`Draft.Write` 必须经 Draft API。
3. Registry mutation 只允许 Data Ops / Core formal operations。
4. PM 的 `Authority.Submit` 只能编排合法 eligibility，不可创造 eligibility。
5. Reviewer 的 `Authority.Review` 产生 Review Result，不等同于无条件 Acceptance Authority。
6. `Authority.Accept` 是 formal gate transition；PM/Main Orchestrator 只有在 `AUTHOR_CONFIRMED` 或有效 `AGENT_DELEGATED` Narrative Authority 下才能请求，Core 仍复核 gate conditions。
7. `Script.Execute` 与 `Shell.Execute` 分列；Python/Node 等脚本运行不能借 generic script path 绕过 shell/sandbox policy。
8. Full Accept 仅减少 approval friction，不改变 Role/Product hard deny。
9. 人类用户直接执行 Accept / Trust / Reconcile 等操作使用 `USER_INTERACTIVE` principal，不属于任何 Agent Role，因此不需要塞进上表的 Agent capability maximum。

---

# 19. Narrative Oversight / Delegated Authority

Product user-facing Oversight Mode 保留：

```text
MANUAL / ASK
ACCEPT_EDITS
AUTO
BYPASS_PERMISSIONS
```

Architecture 内部必须将其拆成两个正交轴：

```text
NarrativeDecisionAuthority
├─ AUTHOR_CONFIRMED_REQUIRED
└─ AGENT_DELEGATED

RuntimePermissionMode
├─ ASK
├─ ACCEPT_EDITS
├─ AUTO_APPROVE_SCOPED
└─ BYPASS_PERMISSIONS
```

关键规则：

- `AUTO` 仍运行完整 Grilling / Contract / Dependency / Review / Revision / Gate / Provenance / Final Acceptance；只是在允许范围内把 Human Confirmation 替换为 Delegated Agent Decision；
- `BYPASS_PERMISSIONS` 主要扩大工具审批便利度，不能自行建立 Narrative Authority，也不能突破 Project Trust / Role hard deny / Authority gate；
- Oversight override 支持 Application → Project → Storyline/Workflow → Task 层级，近层显式 override 生效；
- Mode Change 为 forward-only runtime policy change，在下一个 safe execution checkpoint 生效，不追溯改写历史 Decision/Acceptance provenance；
- 每个正式 Narrative Decision / Acceptance 必须记录 `ProposedBy / ConfirmedBy or DecidedBy / AuthorityKind / OversightMode / scope / timestamp`；
- `AUTHOR_CONFIRMED` 与 `AGENT_DELEGATED` 不得混为同一种 accepted_by provenance。

---

# 20. Full Accept / Hard Deny

Full Accept 可自动批准：

- allowed tool use；
- normal scoped paths；
- Shell within configured advanced permission scope。

永不被 Full Accept 解除：

- Project Trust；
- extension activation；
- secrets plaintext access；
- Authority/Workflow bypass；
- Role Capability hard deny；
- Project 外删除；
- registry/system directory write；
- Windows service control 等系统级破坏性操作的一次确认。

---

# 21. Sandbox / Shell

Worker：

- Windows Job Object：memory / CPU / child process / kill-on-close（资源与生命周期约束）；
- Restricted Token：去除不需要的 OS privilege；
- **AppContainer / LowBox 或等价稳定 Windows OS-enforced isolation：Shell / arbitrary executable 的 filesystem/network/process 安全边界，v1 不得省略；**
- 可 feature-detect 新 Windows sandbox API，但实验性 API 不作为唯一实现依赖；Implementation Design 可选择具体稳定 primitive 组合，只要满足本节硬边界。

Filesystem：

- authorize after resolving canonical final path；
- reparse/junction 防穿越；
- write path open 时拒绝 unexpected reparse redirection。

Shell：

- default PowerShell for script scenario；
- tool-specific executable preferred；
- arbitrary executable == Shell permission；
- python/node 为独立 Script capability；
- default timeout 60s；
- stdout/stderr 256KB head+tail；
- detached background child 禁止；
- worker kill 时整个 process tree kill。

Network：

- Web.Search 独立 capability；
- generic network 走 domain allowlist + mediated tool；
- shell 不默认获得 generic network；domain allowlist 不能只靠应用层约定，Shell/Script 进程的网络隔离必须由 OS sandbox capability/ACL/broker enforcement 兜底。

---

# 22. Prompt Architecture / Content Mode

## 22.1 Canonical Prompt IR

```text
Runtime Policy       [hard, not user-editable prompt]
Base Role Definition [structured role contract]
Behavioral Prompt    [shipped + user override]
Content Overlay      [SFW / NSFW]
Project Context      [AGENTS / compatible instructions]
Skill Instructions
Workflow Context
Task Context
User Request
```

Runtime Policy 不作为用户可编辑文本，也不通过 Prompt 实现授权。

## 22.2 User Override

每 Built-in Agent application-level 独立配置：

- Replace：只替换 Behavioral 层；
- Append：拼接 Behavioral 层之后；
- Reset；
- Default vs Override Diff；
- Effective Prompt Preview。

Replace 不触碰 Base Role Definition / Capability Contract。

## 22.3 Content Mode

SFW/NSFW 是 Content Behavior Overlay，不是 Provider safety bypass。

Narrative-relevant agents：Writer、Planner、Reviewer 对相关调用注入；Data Ops 默认不注入。

SFW 下低层 Prompt 尝试扩大为 NSFW：diagnostic + ignore conflicting lower-layer content expansion，不允许静默覆盖 application-level mode。

NSFW 表示应用不额外施加自身 SFW creative restriction；仍受 Provider/Model 能力与规则影响。

## 22.4 Prompt Config ID

定义两个摘要：

### `PromptConfigId`

仅描述可复用 static behavioral configuration：

- compiler schema version；
- Base Role Definition version/digest；
- shipped Behavioral Prompt version/digest；
- user Override mode + digest；
- Content Mode；
- applicable static Skill layer digests。

### `EffectivePromptDigest`

描述一次 Run 实际编译的 effective static prompt context，额外纳入：

- Project Instructions digest；
- resolved Skill versions；
- provider compiler version。

动态 Task/User正文不进入 `PromptConfigId`，但进入 Run input provenance。

Canonical serialization：

1. UTF-8；
2. normalized LF；
3. Unicode NFC；
4. deterministic key ordering；
5. array order 按实际 precedence 固定；
6. no secrets；
7. prefix domain separator：`llmw-prompt-config-v1\n`；
8. SHA-256 得到 ID/digest。

## 22.5 Shipped Prompt Upgrade

三方 merge baseline：

```text
old shipped
user override
new shipped
```

因此只要存在基于某 shipped version 的 user override，该 old shipped prompt snapshot/content-addressed resource 不得 GC。

Replace override 不自动 merge 新 default；标 stale，用户处理后更新 baseline。

Override：8k chars warning，16k chars hard cap（可配置）；adaptive trimming 只裁 dynamic context，绝不裁 Runtime Policy / Base Definition。

---

# 23. Provider Adapter

统一能力接口：

- streaming；
- tool calls；
- structured output；
- reasoning capability metadata；
- file/image input；
- prompt-cache metadata；
- usage normalization；
- error taxonomy。

Model identity：

- provider；
- model；
- reported revision；
- endpoint/profile。

OpenAI-compatible custom endpoint 使用 capability manifest；local model 以用户声明 + probe 验证。

Provider policy refusal：

```text
PROVIDER_POLICY_REFUSAL
```

与 application Content Mode 独立归因。

Timeout retry 2 次后按 role-configured fallback；**fallback 不得跨越当前 Content Mode / task capability 明确不兼容的 Provider Profile**；fallback 后 provenance 记录实际 provider/model。

Raw provider response 默认不完整持久化；只存必要摘要、usage、error classification 与 artifact refs。
## 23.1 Model Capability Certification

Application-level `Model Capability Registry` 独立于 Project Git/Authority，按 `(provider, model, reported revision/endpoint, shipped prompt baseline)` 记录 task-specific certification。

至少支持 Root Conflict 类任务的指标：

- Root Recall；
- False Merge Rate（高权重）；
- Evidence Fidelity；
- Propagation Accuracy；
- Recompute Accuracy；
- Abstention Quality。

认证输出包括：

```text
CertificationProfile
├─ dataset/version
├─ score set / threshold result
├─ certified task classes
├─ max reasoning mode: CONSERVATIVE | GUARDED | ADAPTIVE
├─ provider/model identity
├─ prompt baseline digest
└─ VALID | STALE | UNCERTIFIED
```

Case Complexity 至少支持 `LOW / MEDIUM / HIGH`，参考 Conflict/Entity/Source 数量、dependency depth、跨 Chapter/Arc/Storyline、Shared Canon/Timeline/Obligation 与上游 unresolved state。

运行时规则：

```text
certified ceiling
→ may downgrade
→ MUST NOT self-upgrade above ceiling
```

用户自定义/未知模型默认 `UNCERTIFIED → CONSERVATIVE`，可通过可选 Test/Certification Run 获得更高 ceiling；用户 Prompt Override 后原 shipped certification 标记“不再保证”，但不自动提升能力。

---


# 24. Editor / DOCX

TXT/MD：CodeMirror 6。  
DOCX：Open XML SDK + internal paragraph/run AST。

UI ↔ Core document edits 使用 structured operations；TXT/MD 使用 transaction-like replace range + patches。

## 24.1 Same-file Lease

每物理 Draft File 同时只有一个 writer lease：

- User 编辑中 → Agent 等待/挂起；
- Agent 写中 → UI 提示等待或取消 Agent task；
- lease 恢复后先做 freshness check。

## 24.2 DOCX

v1 支持 prose formatting：paragraph、heading、bold/italic、list、basic style。

高级 feature：未触及则 opaque preserve；触及则即时 warning + submit 前确认。

Comments / Track Changes v1 = preserve-only。

Normalized review representation 必须保留可回映原 paragraph/run 的 anchors。

Fidelity corpus 最低 20 case，Word / LibreOffice 各半；后续可扩增，不构成根架构变更。

---

# 25. Local History

Project-local：`.llmw/history/` + shared blob store。

Surface：

- Draft；
- Manuscript materialization；
- Narrative user-facing files；
- AGENTS.md。

Prompt Override 是 Application-level，不进入 Project Local History；单独有 setting/prompt history。

Trigger：

- meaningful autosave after debounce；
- content changed；
- periodic 10 min；
- Agent task recovery label before first write。

Retention：

- 200 versions **per file**；
- 30 days；
- 任一限制达到可淘汰；
- **2GB cap per Project**，不是跨所有 Project 全局 cap。

Restore Manuscript materialization 后必须进入 reconcile；Local History 永不直接改变 Authority。

---

# 26. Git Integration

libgit2 只存在 infrastructure adapter，libgit2 types 不得泄漏 domain/Authority layer，以隔离未来 major-version API/ABI 变化。依赖版本策略使用 **current patched 1.9.x or later compatible adapter baseline**；不得固定到已知缺少安全修复的旧 patch 版本。

支持：

- Project Root == Repo Root；
- Project 位于 monorepo 子目录。

v1 不支持：

- nested repo；
- worktree advanced UX；
- submodule workflow integration。

checkout / merge：

```text
Watcher batch mode
→ Git operation
→ full reconciliation snapshot
→ FILE CONFLICT / Dirty / Reconcile as needed
```

Workflow 不自动 Git；Agent Git 只允许 PM/Main Agent 在用户显式 task 下通过统一 Git service 执行。Git hooks 默认禁用；高级设置开启 hook 时必须视为 per-project executable extension activation：要求 Project Trust + 显式激活，hook 内容/hash 变化使激活失效，Full Accept/Bypass 不能自动启用 hooks。

## 26.1 Default `.gitignore`

```gitignore
# LLMW machine-managed runtime
.llmw/

# Application/runtime caches and logs
*.tmp
*.lock
*.log

# Common editor/OS noise
Thumbs.db
Desktop.ini
.vscode/
.idea/

# Generated transient exports (project may override)
~$*.docx
```

注意：`.llmw/` 被 ignore **不再导致 durable Narrative State 缺失 Git tracking**，因为长期项目语义通过 `Narrative/` 等 deterministic projection surface 进入 Git。

用户可自行修改 `.gitignore`。

---

# 27. AGENTS / Skills / Plugins

## AGENTS.md

- canonical project instruction entry；
- root → child inheritance；
- 近层追加，冲突显式 diagnostic；
- 兼容 `CLAUDE.md`；同 scope 时 `AGENTS.md > CLAUDE.md`；
- 引用不得逃出 Project Root；
- 改变后 Active Run stale，下一个 Fresh Session 生效。

## Skills / Plugins

Discovery：Application / User / Project。  
同一 persistent Skill ID/name 的**覆盖解析 precedence**：`Project > User > Application`（近 scope 覆盖远 scope）。
当多个不同 Skill instructions 需要共同进入 Prompt 时，**deterministic composition order** 为 `Application → User → Project`，因此 Project 层最后应用；这表示聚合顺序，不与“同名覆盖 precedence”矛盾。多 Skill 同层按稳定 name + version/id 排序并做冲突 diagnostic。

Manifest 最少：

- name；
- version；
- description；
- instructions；
- scripts；
- requested permissions；
- dependencies。

Extension script/hash 改变 → activation 失效。

### Migration Security

Core declarative migration 可以自动；Project extension executable migration hook 必须在 extension trust + activation 后运行。Migration 不构成自动执行 Project Code 的后门。

---

# 28. MCP Runtime

v1：

```text
stdio
+
current MCP HTTP transport
+
legacy HTTP/SSE compatibility only
```

Native target protocol：2026-07-28；compatibility floor 通过 adapter 兼容受支持的 2025-era server。不得把“native target”误写成拒绝旧 server 的最低协议门槛。

以下不作为新架构依赖：

- Roots；
- Sampling；
- Logging；
- legacy HTTP+SSE。

Prompts / Resources 支持。Tasks extension、MRTR v1 不作为必需能力，可后补。

stdio server 由 Agent Runtime spawn；crash 自动 restart 2 次，之后停用并提示。

MCP config / executable content 改变 → activation 失效。

Remote OAuth token 存 Credential Manager；OAuth metadata/issuer binding 交兼容 SDK/adapter。

Tool result provenance：

- server identity；
- tool name；
- arguments digest；
- timestamp；
- Run/Task identity。

MCP capability 最终仍受 Core-side Effective Capability 验证。

---

# 29. Project Trust / Secrets

Project Trust 与：

- Skill activation；
- Plugin activation；
- MCP server activation；
- executable migration；

全部分离。

Full Accept 永远不能自动建立 Project Trust。

Secrets：

- Windows Credential Manager / DPAPI-backed handle；
- Agent 不获得 plaintext API key；
- Provider/MCP/Git 通过 credential host/callback；
- redaction = known-secret exact match + key-name heuristic；
- Prompt editor 疑似 secret → warn only；
- clipboard 不进入持久 history/trace。

---

# 30. Observability

Audit 与 Debug Trace 分离。

## Authority Audit

- 永久；
- 与 transaction/event stream 对应；
- 必须保存 Narrative Decision/Acceptance 的 `AUTHOR_CONFIRMED` / `AGENT_DELEGATED` provenance 与 Oversight scope；
- 不可因普通 cleanup 删除。

## Debug Trace

- 默认 30 天，可配；
- tool args redacted；
- Provider request 默认不存完整 prompt；
- 只保存 Prompt Config ID / digest /必要 metadata；
- verbose logging 仍强制 secret redaction。

Correlation ID 贯穿：

```text
UI request
→ Core operation
→ Run / Task
→ Tool Call
→ Authority Transaction/Event
```

Debug export 默认排除 Manuscript/Draft 正文。

---

# 31. Projection / Watcher / External Mutation

Watcher：OS-native + polling fallback；支持：

- self-write suppression；
- debounce；
- Git event storm batching；
- startup full scan；
- move/rename heuristic via Object ID/digest；
- overflow → full rescan。

External mutation 分类：

```text
Registered + modified       → DIRTY / reconcile
Registered + missing        → MISSING / dependency check
Unregistered new            → UNREGISTERED / UNAVAILABLE / not retrievable
Move/rename suspected       → user-confirmed reconcile
Manuscript materialization  → MATERIALIZATION DIRTY
Projection modified         → structured reconcile
```

Dirty Authority surface 阻塞新的 Authority operations，但不阻塞 Draft creative editing。

---

# 32. Performance Baseline

v1 baseline：

- cold startup ≤ 5s：点击启动到主界面可交互；
- target hardware：mainstream Windows laptop，i5-class / 16GB / SSD；
- recommended Project scale：≤200 Chapters / 50MB prose；
- 超出：warning + best effort；
- search ≤500ms P95 warm index；
- Agent default concurrency = 4，CPU/RAM adaptive；
- worker memory max = 2GB；
- shell output = 256KB；
- DOCX recommended ≤20MB；
- Local History cap = 2GB per Project。

数值优化允许在 Implementation Review 中调整，但不得降低 Authority / Recovery correctness。

---

# 33. Packaging / Update

v1：MSIX + Portable。

- portable self-contained .NET；
- portable 的 Project/Application data 可移动，但 Windows Credential Manager / DPAPI secrets **不承诺跨机器可携带**；移动到另一台机器后 Provider/MCP/Git 凭据需要重新认证/绑定；
- WebView2 Evergreen 为默认；可提供 Fixed Version fallback/bundle strategy；
- Project format 两种发行形态完全相同；
- app update 完成后，Project open 时 migration；
- active Agent Run 时禁止更新；
- Core/Runtime binaries + shipped Prompt resources 同版本原子更新；
- rollback 旧 App 遇已 migration Project → refuse。

---

# 34. Architecture Verification Matrix

以下为 Architecture Freeze 后必须转入工程验收的最小测试矩阵。

| Area | Required Verification |
|---|---|
| Authority FSM | model-based transitions + all illegal transitions |
| Atomic Commit | crash injection at every persistent step |
| SQLite | WAL corruption / integrity failure / transaction rollback |
| Storage | disk full / missing blob / orphan blob / GC reachability |
| Materialization | crash after DB commit / atomic replace / DIRTY repair |
| Projection | external edit / Git checkout / deterministic rebuild |
| Watcher | lost event / overflow / 1000-file Git storm / offline changes |
| IPC | auth failure / protocol mismatch / reconnect gap / slow consumer |
| Agent | worker crash / cancel cascade / checkpoint resume / UNKNOWN side effect |
| Provider | timeout / rate limit / malformed / fallback / policy refusal / content-policy-incompatible fallback blocked |
| Capability | caller role spoof / runId cross-run spoof / channel-session binding / child escalation / Full Accept hard deny |
| Sandbox | path traversal / junction escape / AppContainer-equivalent filesystem/network isolation / project-outside write/delete |
| Prompt | Override cannot grant capability / precedence / corrupted override |
| Content Mode | SFW lower-layer widening attempt / NSFW provider refusal attribution |
| Prompt Upgrade | old shipped + override + new shipped deterministic merge/stale behavior |
| Model Certification | certified ceiling / runtime downgrade-only / stale model revision / uncertified custom model conservative fallback |
| AGENTS | injection / hierarchy / stale detection |
| WebView2 | origin validation / navigation escape / hostile project HTML / malformed WebMessage / generic native proxy unavailable |
| Extensions | script digest change / trust reactivation / migration hook gating |
| MCP | malicious schema / crash / auth mismatch / legacy transport compatibility |
| DOCX | untouched unsupported round-trip / touched warning / anchor mapping |
| Local History | restore Draft / restore Manuscript → reconcile / retention GC |
| Migration | backup / rollback / interrupted migration / future-version refuse |
| Recovery | corrupted DB backup restore / missing blob partial recovery / salvage |
| Reconstruction | full Reconstruction E2E |
| Archive | pack/unpack Project identity preserved + excluded-history semantics + Authority provenance stubs + snapshot blob lease |
| Final Package | manifest hash round-trip / modified file mismatch |

特别新增 Freeze 修补测试：

1. 同 Project UUID 两个不同 Workspace 副本不会 pipe collision；
2. IPC payload 伪造 Role/Capability 不能扩权；
3. executable extension migration 在 untrusted/unactivated Project 上绝不运行；
4. Archive 默认不携带 Run/History table-family 与 history-only blobs；
5. `.llmw/` 全部 ignore 时 Git 仍能追踪 deterministic Narrative projection；
6. DB 全损时系统不会错误声称可用同库 event table 独立恢复；
7. 仅持有另一个合法 runId 的 caller 不能跨 Run 冒用权限；
8. `AUTO` Delegated Narrative Authority 与 `BYPASS_PERMISSIONS` Tool Permission 分离测试；
9. 未登记/UNAVAILABLE 对象即使存在 FTS row 也不能进入 Normal Retrieval；
10. Arc/Final Acceptance 与 Accepted Snapshot 可独立于 Chapter Candidate 持久化；
11. Shell/Script 在 sandbox 内无法越过 project filesystem/network policy；
12. backup/archive closure 构建期间 GC 无法删除 leased blob。

---

# 35. ADR Requirements

以下架构决策必须形成 ADR：

- ADR-001 `.NET + WinUI 3 + WebView2`；
- ADR-002 Three-process topology；
- ADR-003 SQLite single-writer Authority；
- ADR-004 SQLite Authority + Git-trackable deterministic projection；
- ADR-005 Immutable SHA-256 artifact store；
- ADR-006 DB Commit as Authority linearization point；
- ADR-007 Event Stream disaster-recovery boundary；
- ADR-008 Project UUID vs Workspace Instance ID；
- ADR-009 Core-owned Capability verification；
- ADR-010 Prompt Instruction ≠ Capability Grant；
- ADR-011 Project Trust / Extension Activation separation；
- ADR-012 libgit2 adapter isolation；
- ADR-013 MCP 2026-07-28 compatibility boundary。

---

# 36. Architecture Change Control

Architecture FROZEN 后，以下变化必须重新进入 Architecture Grill / ADR Review：

- Authority semantics；
- Authority commit linearization point；
- process ownership；
- persistence Source of Truth；
- Project identity model；
- capability calculation model；
- Project Trust boundary；
- Prompt/Capability separation；
- SQLite ↔ projection authority relationship。

以下通常属于 Implementation Decision，不重新打开 Architecture：

- class names；
- internal helper APIs；
- UI component library；
- SQL index tuning；
- debounce exact value；
- library minor version；
- logging wording；
- individual table secondary indexes；
- non-semantic directory helper filenames。

---

# 37. External Technical Verification Baseline (2026-08-13)

本 Architecture Spec 的产品语义来自本项目冻结需求；下列外部资料仅用于验证实现可行性和当前生态事实：

- Microsoft WebView2 / WinUI 3 documentation：WinUI 3 officially supports WebView2 integration；WebView2 security guidance requires origin/source validation, parameter validation, navigation restriction, and avoiding generic proxies.
- Microsoft .NET Named Pipes documentation：Named Pipes support duplex IPC and Windows ACL / PipeSecurity; current-user restrictions are available.
- SQLite documentation：WAL + `synchronous=FULL` provides stronger durability; SQLite atomic transaction guarantee does not make external files part of the same DB transaction; backup API provides consistent DB snapshots.
- SQLite FTS5 documentation：built-in tokenizer families include `unicode61`, `porter`, and `trigram`; English baseline uses porter/unicode61, Chinese v1 uses trigram.
- MCP 2026-07-28 specification/blog：stateless core, current Streamable HTTP changes, Roots/Sampling/Logging and legacy HTTP+SSE deprecated for new implementations.
- Microsoft AppContainer documentation：AppContainer provides OS-enforced file/network/process isolation and least-privilege boundary; Job Object remains resource/lifecycle control.
- libgit2 release baseline：current patched v1.9.x is required; v1.9.5 fixed multiple security issues and v1.9.6 is the current release at audit time. v2 is expected to introduce API/ABI changes, therefore adapter isolation is mandatory.

参考：

- https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winui
- https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication
- https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security
- https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation
- https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipesecurity
- https://sqlite.org/atomiccommit.html
- https://sqlite.org/backup.html
- https://sqlite.org/fts5.html
- https://blog.modelcontextprotocol.io/posts/2026-07-28/
- https://github.com/libgit2/libgit2/releases
- https://github.com/libgit2/libgit2/blob/main/docs/changelog.md

---

# 38. Final Audit Freeze Patch Summary

本 v0.1.1 仅将 Product v0.5.2 已冻结语义与最终安全审计要求补编入 Architecture，不新增产品 Root Design：

1. Domain Authority Aggregate：Narrative Change Set / Arc / Storyline / Final Acceptance / Accepted Snapshot；
2. Oversight / Delegated Narrative Authority 与 Runtime Tool Permission 正交；
3. Registry Retrieval Availability / Trusted Baseline 成为 Normal Retrieval 强制门；
4. Shell/Script 使用 OS-enforced sandbox boundary；
5. Core caller authorization 绑定 authenticated channel + RunSessionHandle，而非只信 runId；
6. WebView2 Renderer 降为 untrusted presentation/editor domain；
7. Model Capability Certification / Case Complexity / downgrade-only ceiling 落地；
8. P1 修补：Script capability、FSM failure branch、descriptor schemaVersion、MCP target wording、provider fallback compatibility、FTS derived-state failure、Archive provenance stub、snapshot blob lease、portable credential caveat、Final Package manifest/attestation reserve、patched libgit2 + Git hook trust、Skill precedence/composition disambiguation、Specialist Profile/Runtime Grill 补编。

这些修补不改变 `SQLite Authority + immutable artifacts`、DB commit linearization、三进程 topology、Draft/Manuscript 分层、Single Frontier / Revision Barrier 等根架构。

Final mechanical scan 已验证：章节编号/引用闭合、Authority failure branches、Oversight/Permission 正交、Registry Retrieval gate、RunSession caller binding、WebView trust boundary、sandbox hard boundary、Model Certification ceiling、Archive/Backup reachability 与 Git/MCP/Provider 补丁之间无新的 Root-level 自相矛盾。

---

# 39. Architecture Freeze Verdict

本 Spec 已吸收：

```text
Q1–Q43     → Technical Architecture Decisions
Q44–Q122   → Architecture Specification Decisions
Q123–Q128  → Final Cross-decision Freeze Patches
Final Audit → P0/P1 Specification & Security Freeze Patch
```

同时机械修正：

- Agent Runtime crash 不释放其持有的 Active Authority Submission Lock；
- FTS English tokenizer 明确为 porter/unicode61；
- future-version Project 为 refuse-without-mutation；
- `.llmw/authority/` 仅 staging/recovery，不与 `Manuscript/current/` 重复；
- old shipped prompt version 在存在 dependent Override 时不得 GC；
- Local History 2GB cap 定义为 per Project；
- Domain Authority 泛化到 Narrative Change / Arc / Storyline / Final Acceptance / Accepted Snapshot；
- Oversight/Delegated Narrative Authority 与 Tool Permission 正交建模；
- Registry Retrieval Gate、RunSession caller binding、WebView2 trust boundary、OS-enforced shell sandbox、Model Capability Certification 全部补齐；
- Final Package manifest、Archive provenance stub、snapshot blob lease、portable credential caveat、Git hook trust 与 patched libgit2 policy 补齐。

最终状态：

```text
Technical Architecture Specification
→ FROZEN

Root Architecture Conflict
→ NONE

Final Gap / Failure-mode Blocker
→ NONE

Mechanical Consistency Scan
→ PASSED
```

Architecture Freeze 后进入：

```text
Phase 4 — Implementation Design
→ Repository Scaffold
→ Module / Assembly Boundaries
→ Database Migration v1
→ IPC Contracts
→ Coding Agent Execution Plan
→ Implementation
```

---

# 40. One-line Summary

> **Writing Technical Architecture v0.1 FROZEN 以 Windows/.NET + WinUI 3/WebView2 为宿主、Authority Core 为唯一持久化写者、SQLite(WAL/FULL)+不可变内容寻址对象库为运行时 Authority Source of Truth，并通过 deterministic Git-trackable structured projection 保留普通文件/VCS 项目语义；Authority 以 DB Commit 为唯一逻辑提交点，Manuscript/Projection 作为可恢复派生物化；Agent Runtime 采用独立 worker、durable RunState、Task Result Artifact、freshness-aware Context 和 Core-side authenticated RunSession Capability 再验证；Narrative Oversight/Delegated Authority 与 Tool Permission 正交；Prompt 自由与 Runtime 权限硬分离，SFW/NSFW 作为 Content Behavior Overlay；Project Trust、Extension Activation、MCP、Shell、Git、DOCX、Local History、Backup/Archive 均受统一事务、provenance、reconcile 与 fault-injection 验收约束。**
