# LLMW.Writing — Implementation Carry-Forward Register

**Status:** ACTIVE ENGINEERING REGISTER  
**Purpose:** 记录已经确认、但明确延期到后续 Work Package 处理的实现风险 / 验收债务，避免跨会话、跨 Agent 或长周期开发过程中遗失。  
**Scope:** 这里只登记已经被外部审查确认的 carry-forward；当前 Work Package 内必须修复的问题不得用本文件降级为延期项。  

---

## 使用规则

1. 每个新 Work Package 开始前，Coding Agent 应按目标 WP 检查本文件中是否存在对应的 `Target WP` / `Must be resolved by` 条目。
2. Carry-forward 不自动改变 Frozen Product / Architecture；如修复要求改变冻结语义，必须重新进入 Architecture Review。
3. 到达目标 WP 时，相关条目必须进入该 WP 的 Mandatory Tests / Completion Contract，而不是仅作为 Known Limitation 保留。
4. 只有在真实代码 + 测试验收通过后，条目才可标记为 `RESOLVED`。
5. 解决条目时保留原记录和最终 commit / test evidence，不删除历史。

---

# CF-001 — Pre-Commit Crash 后 Chapter Submission Workflow Rehydrate

| 字段 | 内容 |
|---|---|
| ID | `CF-001` |
| Severity | `P1 Carry-Forward` |
| Status | `OPEN` |
| Origin | `WP05 — Chapter Authority Vertical Slice` 外部真实代码审查 |
| Detected after | `feat(wp05): implement chapter authority vertical slice` |
| Target WP | `WP22 — Recovery / Reconstruction / Project Health` |
| Must be resolved by | WP22 Completion Contract |
| Architecture impact | 无；属于既有 Recovery invariant 的实现/验收补全 |
| Current blocker | 否；不阻塞 WP06–WP21，只要后续实现不假定该场景已解决 |

## 1. 风险场景

WP05 当前纵向切片已经验证：

- Candidate / Review 等 workflow history 可以在正式 Authority Acceptance COMMIT 前持久化；
- `BeforeSqliteCommit` 故障在**同一进程仍存活**时可以安全 retry；
- DB COMMIT 后 crash 使用 WP03 的 roll-forward recovery。

但尚未完整验证以下跨进程场景：

```text
Draft
→ Candidate 已持久化
→ Review 已持久化
→ Project Submission 已进入 RESOLVING / 准备 Accept
→ Authority Acceptance SQLite COMMIT 尚未发生
→ Core / process 崩溃或被终止
→ Application restart
→ startup RecoverIncomplete / workflow rehydrate
```

当前风险点在于：

```text
startup recovery
→ 识别未完成 Authority transaction
→ transaction 被清理 / 标记失败

但：
Candidate / Review / Chapter / ProjectSubmission durable workflow state
可能仍然表示“正在等待 Acceptance”
```

如果只恢复 transaction，而没有重新构建 workflow aggregate，就可能出现：

- Candidate 已存在且 Review PASS；
- Chapter / ProjectSubmission 仍处于非 IDLE 中间状态；
- 没有已提交 Acceptance / Manuscript Revision；
- 用户无法继续 Accept；
- 新 Submit 又可能被 active-submission 状态阻塞；
- 或系统错误释放 submission lock，导致两个逻辑 submission 并存。

这属于 **workflow rehydrate / recovery completeness** 问题，而不是 WP03 SQLite atomicity 问题。

---

## 2. 必须保持的不变量

修复 CF-001 时不得破坏：

1. **SQLite COMMIT 仍是唯一 Authority logical commit point。**
2. Pre-commit process death **不得产生 Acceptance / Manuscript Revision / Current Manuscript Authority**。
3. 已持久化的 Candidate / Review history 不得因 recovery 被伪装成“从未发生”。
4. Failed / Cancelled Candidate 不得被复活成 Accepted Candidate。
5. Retry 若需要重新提交 Candidate，必须建立 New Candidate lineage。
6. 不得通过 filesystem rollback 伪造 Authority rollback。
7. 恢复必须 deterministic + idempotent。
8. 重复 restart / repeated recovery 不得创建第二个 logical submission 或 Authority transaction。

---

## 3. WP22 Mandatory Recovery Cases

WP22 必须新增真实 file-backed / process-restart 级测试，至少覆盖：

### CF-001-A — Crash after Review PASS, before Acceptance transaction COMMIT

```text
Candidate persisted
→ Review PASS persisted
→ ProjectSubmission = RESOLVING
→ process terminates
→ restart
```

期望：

- Candidate 仍存在；
- Review 仍存在；
- Acceptance 不存在；
- Manuscript Revision 不存在；
- Current Manuscript Authority 不改变；
- workflow 能被重新构建为明确、合法、可继续处理的状态；
- 系统不会把此状态误认为已 Accept。

### CF-001-B — Crash after Acceptance transaction created/PENDING, before SQLite COMMIT

期望：

- pre-commit Authority mutation 不存在；
- PENDING transaction 按 WP03 recovery semantics 安全收敛；
- Candidate / Review durable history 保留；
- workflow aggregate 与 transaction cleanup 后状态一致；
- 用户可明确继续 Accept、重新尝试或执行合法 Cancel；
- 不留下永久 active-submission deadlock。

### CF-001-C — Repeated restart / recovery idempotency

连续多次：

```text
restart
→ RecoverIncomplete
→ crash again
→ restart
→ RecoverIncomplete
```

必须：

- 状态不漂移；
- 不创建重复 Candidate / Review / transaction / event；
- 不形成两个 active submissions；
- recovery result deterministic。

### CF-001-D — Submission lock correctness after recovery

恢复后必须证明：

- 若原 submission 应继续存在，则第二 Submit 被正确阻止；
- 若 recovery 已合法释放原 submission，则新 Submit 可以开始；
- 不得出现“旧 workflow 仍活着但 lock 已释放”的 split-brain 状态。

### CF-001-E — Recovery UX / Project Health surface

WP22 的 Project Health / Recovery state 必须能够区分至少：

- 可自动恢复；
- 需要用户决定继续 / Cancel；
- `RECOVERY_REQUIRED`；
- 已提交 Authority 的 roll-forward recovery。

CF-001 的 pre-commit workflow interruption 不得被错误展示成“Authority 已提交但 materialization dirty”。

---

## 4. 建议实现边界

WP22 到达时，优先考虑：

```text
durable Candidate / Review / Chapter / ProjectSubmission state
+
Authority transaction durable state
+
current pointers
↓
startup rehydrate
↓
derive legal workflow aggregate
↓
recover / resume / require decision
```

不要依赖：

- 前一进程的内存 FSM state；
- previous chat / Agent memory；
- filesystem Current Manuscript 来猜 Authority state。

如需要新增 recovery coordinator / rehydrator，应保持：

- Domain FSM 仍为 pure transition model；
- Application 负责 workflow rehydrate / orchestration；
- Infrastructure 负责 SQLite / durable state reads；
- WP03 Transaction Coordinator 的 commit/recovery semantics 不被重新定义。

---

## 5. Completion Evidence Required

CF-001 只有在以下证据齐全后才可标记 `RESOLVED`：

- [ ] 真实 restart / rehydrate integration test；
- [ ] crash before Acceptance COMMIT；
- [ ] crash with PENDING transaction；
- [ ] repeated recovery idempotency；
- [ ] active-submission lock correctness；
- [ ] Candidate / Review history preservation；
- [ ] no Acceptance / Manuscript Authority before COMMIT；
- [ ] no duplicate Authority transaction / event；
- [ ] Project Health / recovery classification；
- [ ] WP01–WP21 relevant regression tests remain green；
- [ ] 外部代码审查通过；
- [ ] 记录最终 resolving commit SHA。

---

## 6. Resolution Record

**Status:** `OPEN`

最终解决时填写：

- Resolving WP:
- Resolving branch:
- Resolving commit:
- Tests:
- External review:
- Notes:

---

# Register Summary

| ID | Severity | Status | Origin | Target WP | Short description |
|---|---|---|---|---|---|
| CF-001 | P1 | OPEN | WP05 | WP22 | Pre-commit process death 后必须 rehydrate Chapter submission workflow，并保持 transaction / FSM / submission lock 一致 |
