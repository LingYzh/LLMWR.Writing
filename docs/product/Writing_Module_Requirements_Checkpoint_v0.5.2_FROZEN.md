# Writing Module Requirements Checkpoint — v0.5.2

**Checkpoint 日期**：2026-08-13  
**完整性**：Self-contained checkpoint  
**状态**：FROZEN / Product Requirements Final Freeze  
**阅读优先级**：**Part G > Part F > Part E > Part D > Part C > Part B > Part A**  
**用途**：Writing Product Requirements 最新正式冻结稿。完整保留 v0.5.1 FROZEN 决策链，并追加 v0.5.2 Content Mode / Built-in Agent Prompt Customization Addendum；后续工作继续进入 Technical Architecture / Implementation Design。

> 本文件完整保留 v0.5.1 FROZEN 的全部内容，并追加 **Part G — v0.5.2 Content Mode / Built-in Agent Prompt Customization Addendum**。  
> 若旧内容发生冲突，按 **Part G > Part F > Part E > Part D > Part C > Part B > Part A** 解释。  
> 本文件状态为 **FROZEN**；除非后续发现新的 Root Design Conflict，否则不再重新 Grilling 已 CLOSED 的产品根设计。  
> 曾短暂推导过的跨章节 Submission Queue 已明确废弃，不得作为实现依据。

---

# Embedded Previous Checkpoint（v0.4 Freeze Candidate 原文完整保留）

# Writing Module Requirements Checkpoint — v0.3

**Checkpoint 日期**：2026-08-12  
**完整性**：Self-contained checkpoint  
**阅读优先级**：Part C > Part B > Part A  
**用途**：在长会话上下文继续退化前固化 Writing Product Requirements 与 Agent Runtime 产品决策，供下一会话继续 Grilling。

> 本文件完整保留此前 v0.2 内容，并追加 Part C v0.3。旧章节中与 Part C 冲突的表述，以 Part C 为准。

---

# Embedded Previous Checkpoint（v0.2 原文完整保留）

# Writing 模块需求冻结稿 / 新会话交接文档 — v0.2 Checkpoint

**Checkpoint 日期**：2026-08-11  
**组成**：Part A = v0.1 原始冻结稿；Part B = 本轮新增冻结决策 / 覆盖规则  
**用途**：在长会话上下文开始退化前保存完整决策链，供下一会话直接继续 Grilling。  

> 阅读规则：**Part B 优先于 Part A**。Part B 未修改的 v0.1 决策继续有效。

---

# Part A — v0.1 Original Baseline（完整保留）

# Writing 模块需求冻结稿 / 新会话交接文档

**版本**：v0.1  
**状态**：已确认需求冻结稿（Product Requirements / UX Principles）  
**用途**：将本轮 Grilling 已经达成共识的内容沉淀下来，供后续新会话直接继续；避免重复讨论已经确认的决策。  
**当前讨论边界**：优先定义“需要什么功能、用户如何使用、产品如何表现”。Provider、数据库、缓存、OAuth、Gateway 等实现细节暂缓，后续进入技术设计阶段时再统一复核。

---

## 0. 产品方法论

本项目的 Writing 模块不是“一个带 AI 的 Markdown 编辑器”，也不是“一个靠聊天指挥 AI 写小说的 Agent 客户端”。

它的核心目标是：

> **通过明确的创作 Workflow、结构化表单、持续 Grilling、可直接编辑的作品内容和项目级 Agent 协作，让作者把模糊直觉逐步变成可验证、可执行、可追溯的叙事决策。**

应用不应假设：
- 用户天然知道自己到底想写什么；
- 用户填了内容就代表这个内容回答了正确的问题；
- LLM 能可靠地替作者补全所有未决策事项；
- “作者觉得每个角色都很重要”就等于所有角色都是 Main Cast；
- AI 写出来的内容天然符合角色卡、世界设定与前文连续性。

应用应当通过结构化 Workflow 与 Grilling 缩小：
- 作者水平差异；
- 作者表达能力差异；
- LLM 能力差异；
- 长篇创作过程中不断积累的设定漂移与隐含矛盾。

---

# 1. 已锁定的应用级边界

## 1.1 Writing 与 Roleplay 是两个独立业务模块

二者不是同一个 Project 模型下的两个 Mode。

```text
Application
├─ Writing
│  └─ 独立业务模型 / Workflow / UX
│
├─ Roleplay
│  └─ 独立业务模型 / Workflow / UX
│
└─ Shared
   └─ LLM Provider System
```

### 原则

- `WritingProject ≠ RoleplayProject`
- Writing 与 RP 不共享角色、世界观、会话等领域对象。
- 基础设施代码未来可以共享，但不能因为“代码复用”而强行制造共享领域模型。

---

## 1.2 Writing ↔ Roleplay 通过显式 Import / Export 交换

采用：

> **显式导入 / 导出；导入完成后彻底解耦。**

不采用跨模块实时引用或自动同步。

```text
Writing Object
      ↓ Export
Exchange Format
      ↓ Import
Roleplay Object

Import 完成后，两份数据各自独立。
```

---

## 1.3 Exchange Format 是正式开放格式

Exchange Format 不是仅存在于应用内部的 DTO。

它应当：
- 独立；
- versioned；
- extensible；
- 可被用户读取；
- 可被 Agent 读取 / 生成；
- 可被插件或第三方程序读写；
- 作为 Writing / RP / 外部生态之间的兼容层。

Writing 与 Roleplay **禁止直接依赖彼此内部 Schema**。

---

# 2. Writing 的核心产品模型

## 2.1 三个正交核心：Workflow + Editor + Agent

### Workflow

回答：

> **“当前作品进行到哪个创作阶段？”**

### Editor

回答：

> **“作者亲自编辑什么内容？”**

### Agent

回答：

> **“AI / Agent 帮作者完成什么任务？”**

三者不是互相替代关系。

```text
                   ┌─ Editor：作者亲自做
Current Workflow ──┤
                   └─ Agent：AI 协助 / 执行
```

Agent 不应成为正文编辑器的附属右键菜单；同时，用户也不应被迫通过 Chat 才能编辑自己的作品。

---

# 3. Writing 主 Workflow

已锁定的宏观流程：

```text
Raw Ideas / Idea
        ↓
Story Intent
        ↓
Master Outline
        ↓
┌──────────────────────────┐
│ Chapter N                │
│                          │
│ Chapter Outline          │
│        ↓                 │
│ Draft                    │
│        ↓                 │
│ Review                   │
│        ↓                 │
│ Chapter Accepted         │
└───────────┬──────────────┘
            ↓
         Next Chapter
            ↓
      First Draft Complete
            ↓
      全稿级审阅 / 修改
```

---

## 3.1 软必填与硬闭环

### 软必填

以下内容可以暂时欠账，但不会被系统视为完成：

- Idea / Story Intent 的部分内容；
- Master Outline 的部分前置内容；
- Ending 可以暂时未决定。

产品应做到：

> **允许跳过，但欠下的流程不会消失。**

UI 应持续显示：

```text
✓ 已完成
△ 待完善
● 正在梳理
○ 未开始
```

项目只有在必要内容最终补齐后，才能被认定为完整初稿。

---

## 3.2 `(章节大纲 → 正文 → Review)` 是硬闭环

对于新创作：

```text
Chapter Outline
      ↓
确认 / Ready
      ↓
Draft 解锁
      ↓
Review
      ↓
Chapter Accepted
```

规则：
- 每一个正式 Chapter 必须有 Chapter Outline；
- Chapter Outline 必须先完成并确认；
- 确认后才能开始该章 Draft；
- Draft 完成后必须执行 Review；
- Review 完成后才能正式验收该章；
- 然后进入下一章。

### 已有作品导入例外

已有正文允许先导入，但：

- 对应章节会被标记为“待补大纲”；
- 可以由作者手动补建；
- 也可以由 Agent 从正文反向提炼；
- 在完整性验收前仍必须补齐。

---

# 4. Grilling：Writing 的常驻 Narrative Decision Layer

Grilling 不只是 Idea 阶段的一次 Skill。

已确定：

> **Grilling 应全程参与 Writing Workflow。**

但它不能变成“作者每写一句话，AI 就跳出来质问”。

核心原则：

> **自由表达不 Grill；正式决策必须 Grill。**

---

## 4.1 内置 Skill

产品应内置：
- `grill-me`
- `grilling`

其中 `grilling` 是核心机制，`grill-me` 可以作为用户入口 / alias。

Grilling 核心行为：
- 沿 decision tree 逐层推进；
- 一次只解决一个问题；
- 上一个答案可以改变后续问题；
- 每个问题 Agent 都要提供自己的推荐答案；
- 能从已有内容 / 文件中自己找到答案的事实，不再反问用户；
- **事实可以由 Agent 查；创作决定必须交回用户决定；**
- 未形成 shared understanding 前，不应擅自进入下一个正式阶段。

---

## 4.2 Grilling 全流程职责

### Discovery Grill

主要发生在 Story Intent。

目的：

> **“你到底想写什么？”**

发现：
- 模糊概念；
- 未做决定；
- 自相矛盾；
- 隐含假设；
- 用户误把题材 / 风格 /设定当作主题、冲突或动机。

---

### Planning Grill

发生在：
- Master Outline；
- 更高层故事规划。

主要检查：
- 为什么事件发生；
- 谁推动；
- 为什么行动；
- 前置条件是否存在；
- 产生什么后果；
- 后果如何连接下一阶段。

---

### Pre-writing Grill

发生在 Chapter Outline 提交前。

目的：

> **“这一章真的已经准备好写了吗？”**

例如：
- 角色为什么现在行动；
- 掌握了什么信息；
- 当前冲突为什么发生；
- 转折为什么成立；
- 本章结束后改变了什么。

---

### Review Grill

发生在 Draft 之后。

检查：
- 大纲完成度；
- OOC；
- Canon 冲突；
- 世界规则；
- Continuity；
- Timeline；
- 因果断裂；
- 无铺垫转折；
- Theme 漂移；
- 人物弧异常。

---

## 4.3 Agent Drafting 也应受到 Grilling 原则约束

Agent 开始正式生成内容前应确保自己知道：
- 当前章节目标；
- 角色动机；
- 当前冲突；
- 预期结束状态；
- 相关 Canon。

如果存在真正需要作者决定的缺口：

> **Agent 不能偷偷补决策。**

必须将决策显式交回用户，并给出推荐答案。

---

# 5. Idea Workspace

Idea 不采用单一 Braindump。

采用两层：

```text
Raw Ideas
   ↓
Story Intent
```

---

## 5.1 Raw Ideas / 灵感池

完全自由。

可以保存：
- 一句话；
- 场景；
- 台词；
- 人物想法；
- 图片；
- 文档；
- 资料；
- 未确定脑洞；
- “也许如此”的方案；
- 互相矛盾的想法。

### 关键规则

> **Raw Ideas 不属于 Canon。**

例如：

> “也许中途让妹妹死？”

不能自动变成后续大纲中的既定事实。

Raw Ideas 默认不强制 Grill。

---

# 6. Story Intent

Story Intent 是：

> **作者已经确认的创作意图，是 Master Outline 的正式上游。**

Story Intent 不等于 Synopsis。

区别：

- Story Intent：**作者想写一个怎样的故事？**
- Master Outline：**这个故事具体如何发生？**

---

## 6.1 Story Intent 使用受控表单

Idea 阶段不应以自由 Chat 作为主要 UI。

核心体验：

```text
Story Intent Form
        ↓
用户填写
        ↓
AI 语义检查
        ↓
Inline Grilling
        ↓
用户确认
        ↓
Field Ready
```

Grilling 必须尽量停留在表单结构内部。

避免：

```text
自由聊天
   ↓
AI 自己总结
   ↓
自动填表
```

---

## 6.2 Story Intent 强制字段

已确定的硬必填：

1. **Opening / 故事起点**
2. **Primary / Core Conflict / 故事级核心冲突**
3. **Protagonist Motivation / 主角动机**
4. **Theme / 主题**
5. **Tone / 基调**
6. **Main Cast / 主要角色及其故事职责**

### Ending

Ending：
- 推荐填写；
- **允许暂时未知；**
- 不作为当前阶段绝对硬门槛。

---

## 6.3 Story Intent 字段不是简单大文本框

产品固定的是：

> **Intent Dimension / 要搞清楚的语义目标**

不是：

> 固定的一长串编剧考试题。

每个 Dimension 应包含概念上的：

```text
Intent Dimension
├─ Goal
│  最终需要搞清楚什么
│
├─ Entry Questions
│  无预设前提的问题
│
├─ Conditional Branches
│  根据回答继续 Grill
│
├─ Semantic Result
│  最终归纳出的标准结果
│
└─ Status
   未定 / 模糊 / 梳理中 / 用户确认
```

---

## 6.4 “先问是不是，再问为什么”

这是 Story Intent Grilling 的硬规则。

> **任何带前提假设的深层问题，都必须先验证前提是否成立。**

错误示例：

> “为什么主角无法退出？”

它已经假设主角无法退出。

正确方式：

```text
主角能够退出这场冲突吗？

○ 可以，而且代价很低
○ 可以，但需要付出代价
○ 理论上可以，但本人认为不能
○ 实际上无法退出
○ 尚未决定
```

之后再根据答案追问：

### 如果可以退出

> 为什么仍然主动留下？

### 如果有退出代价

> 退出的代价是什么？  
> 这个代价足以强迫他，还是只是影响选择？

### 如果无法退出

> 是什么真正阻止他退出？

这能区分：
- 自愿；
- 被迫；
- 半自愿；
- 内在义务；
- 外在压力；
- 现实约束。

---

# 7. Opening / 故事起点

Opening 必须 Grill 到足够明确。

不能只填“第一章发生什么”。

它需要回答的核心语义至少包括：
- 故事开始时人物 / 世界是什么状态；
- 主角是否已经处于主要冲突中；
- 故事从哪个叙事节点开始；
- 什么变化使故事真正开始；
- 主角当时知道多少；
- 当前事件把主角推向什么方向。

同样必须避免预设：

错误：

> “什么意外打破了主角的日常？”

因为有些故事开场时主角已经身处冲突。

先问：

```text
故事开始时，主角已经身处主要冲突中吗？

○ 尚未
○ 已经卷入，但不了解全貌
○ 已经主动参与
○ 从冲突高潮处直接开始
○ 其他
```

再进入不同分支。

---

# 8. Protagonist Motivation / 主角动机

这是硬必填。

核心目的：

> **理解主角为什么持续参与整个故事的核心行动。**

不能把：
- 身份；
- 职业；
- 性格标签；
- 一次性任务；
误认为完整动机。

建议通过条件式 Grilling 区分：
- 初始 Agency；
- 初始目标；
- 触发原因；
- 表层目标；
- 深层动机；
- Stakes；
- 是否能退出；
- 为什么留下；
- 是否从“被动”转为“主动”。

### 重要原则

“不能退出”和“可以退出但选择留下”是完全不同的人物写法，产品不能偷偷将所有主角导向“被迫卷入”。

---

# 9. Theme 与 Tone 必须分开

## Theme

回答：

> **这个故事真正想讨论什么问题 / 命题？**

不是题材标签。

错误：

```text
战争
爱情
成长
末日
```

更接近有效 Theme 的形式：

> “人在一个已经失去正常秩序的世界中，是否仍然有义务保持自己的道德底线？”

Theme 必须 Grill。

不要求作品最后提供唯一正确答案。

---

## Tone

回答：

> **作者希望读者整体以怎样的情绪体验这个故事？**

例如：
- 黑暗；
- 压迫；
- 神秘；
- 偶有温暖；
- 最终仍有希望。

### 语义检查

如果用户把：

> “黑暗王道”

填进 Theme，系统不能仅因“非空”而显示完成。

应该判断：

> 这更像基调，Theme 仍不明确。

然后触发 Inline Grill。

---

# 10. Core / Primary Conflict

Story Intent 必须明确一个 **Story-level Primary Conflict**。

但 Primary Conflict：
- 可以是复合冲突；
- 不等于“必须只有一个反派”；
- 多种 Conflict Dimensions 可以共同构成一个统一的故事级问题。

例如：

```text
Primary Conflict:
一群核战后幸存者能否找到真正适合重新生活的新家园？

Conflict Dimensions:
├─ 人 vs 末世环境
├─ 人 vs 资源
├─ 人 vs 其他幸存者
├─ 人 vs 社会 / 意识形态
└─ 人 vs 自身信念
```

关键是这些维度是否共同回答：

> **为什么故事的核心目标如此困难？**

---

## 10.1 冲突层级

已确定三级：

### L1 — Story / Primary Conflict

- Story Intent 阶段必须明确；
- 整部作品的统一脊柱；
- 可以是复合冲突。

### L2 — Arc Conflict

- 卷；
- 故事阶段；
- 某个主要人物弧；
- **主要在 Master Outline 中建立。**

### L3 — Local Conflict

- Chapter；
- Scene；
- Character；
- Relationship；
- **主要在 Chapter Outline 中建立。**

```text
Story Intent
    ↓
Primary Conflict

Master Outline
    ↓
Arc Conflict

Chapter Outline
    ↓
Local Conflict
```

群像剧同样适用，不需要另一套 Workflow。

---

# 11. Main Cast / Supporting Cast / Minor

角色主次描述的是：

> **角色承担的叙事职责。**

不是：
- 作者喜欢程度；
- OC 设定丰富程度；
- 单纯出场次数。

核心教育信息：

> **Supporting ≠ 不重要。**

---

## 11.1 三级角色层级

### Main Cast

- 承担故事级主要因果链；
- 直接参与 Primary Conflict；
- 必须完成 Main Cast Grilling。

### Supporting Cast

- 承担次级因果链；
- 重要人物关系；
- Arc；
- 关键剧情功能；
- 可以有自己的动机和人物弧；
- 不要求承担整个 Story-level Conflict。

### Minor / Functional

- 承担局部人物、场景、剧情功能；
- 仍然可以拥有完整 Character Card；
- Story Intent 不需要为其维护完整一级人物线。

角色可以随创作推进升级 / 降级。

---

# 12. Main Cast 必须进行角色级 Grilling

Main Cast 不能只是：

```text
Alice
Role = Main
```

它必须在 Story Intent 中明确：

1. **Narrative Role**  
   为什么这个角色存在于“这部故事”里？

2. **Motivation**  
   为什么行动？

3. **Relation to Primary Conflict**  
   主冲突为什么与这个角色有关？

4. **Personal Conflict**  
   是否拥有自己的个人问题？  
   允许明确回答“没有”。

5. **Agency**  
   是否主动推动事件，还是主要回应别人？

6. **Independent Causality**  
   是否存在自己的“目标 → 行动 → 结果 → 新问题”因果链？

7. **Potential Character Arc**  
   是否预计发生变化？  
   允许明确回答“基本不变化，而是影响其他人”。

---

## 12.1 人物弧不能被预设为“必须成长”

先问：

```text
角色是否预计发生明显变化？

○ 会发生明显变化
○ 主要是关系 / 立场变化
○ 基本保持稳定，并影响别人
○ 尚未决定
```

只有选“会变化”才继续追：
- 开始相信什么；
- 什么挑战它；
- 最终如何变化。

稳定型角色则应 Grill：
- 什么核心保持不变；
- 这种稳定性怎样作用于主冲突或其他角色。

---

# 13. Cast Role Audit：帮助作者真正分清角色主次

作者经常认为“每一个角色都很重要”。

产品不能简单让作者自己给所有人打 Main 标签。

应通过 **Cast Role Audit + Inline Grilling** 帮作者区分：

> “这个角色很重要”  
> 和  
> “这个角色是 Story-level Main Cast”

不是一回事。

---

## 13.1 Audit 关注功能，而不是作者感情

不问：

> “Alice 重要吗？”

因为答案通常永远是“重要”。

应检查：

### A. 与 Primary Conflict 的直接关系
- 主动推动解决；
- 主动阻止；
- 自己的目标直接改变主冲突；
- 主要被影响；
- 通过另一角色间接参与；
- 几乎没有直接关系。

### B. Agency
如果没有其他主要角色要求，这个角色是否仍然会：
- 形成自己的目标；
- 自己采取行动？

### C. Independent Causal Chain
是否拥有持续的：

```text
目标
 ↓
行动
 ↓
结果
 ↓
新问题
 ↓
再次行动
```

### D. Arc Ownership
是否真正“拥有”一部分剧情？

即使主角不在场：
- 角色仍有自己的目标；
- 自己做决定；
- 自己承担结果。

### E. Remove / Transfer Test
如果移除这个角色，并把必要功能转交给别人：

- 主冲突本身是否根本改变？
- 一个主要 Arc 是否消失？
- 只是某些功能需要重新分配？
- 主线几乎不变？

---

## 13.2 不做伪科学总分

不要：

```text
Alice = 93 分
Bob = 71 分
```

更适合输出 Narrative Role Profile：

```text
Primary Conflict       强
Agency                 强
Independent Causality  强
Arc Ownership          强
Narrative Connectivity 中 / 强

推荐：
MAIN CAST

原因：
……
```

或：

```text
推荐：
MAJOR SUPPORTING CAST

说明：
该角色非常重要，但目前主要服务于其他角色的故事，
尚未拥有独立的 Story-level 主要因果链。

Supporting ≠ 不重要。
```

最终分类仍由作者决定。

---

# 14. Custom Intent：允许无限扩展，但不能逃出表单语义体系

Story Intent 允许用户自定义任何创作内容。

例如：
- “黑暗但王道”；
- 爽点控制；
- 叙事禁区；
- 战斗比例；
- 恐怖感控制；
- 参考作品；
- 读者体验；
- 作者自己的独特规则。

但不能仅保存：

```text
字段名 = 自由文本
```

因为 LLM 可能不知道用户自己的术语代表什么。

---

## 14.1 Semantic Intent Unit

每个自定义项都必须拥有可理解的语义。

概念上至少包括：

```text
What is it?
它是什么？

What does it mean?
它代表什么？

What does it affect?
它影响哪些阶段 / 内容？

How strongly should it be followed?
约束程度？

Content
具体内容
```

用户可以自由表达。

LLM 负责提出语义解释。

用户负责确认。

```text
User Free-form
     ↓
LLM Interpretation
     ↓
Inline Grill
     ↓
User Confirmation
     ↓
Normalized Semantic Intent
```

### 重要原则

> **自定义不是跳出 Schema，而是动态扩展 Schema。**

---

# 15. 辅助知识 / Auxiliary Knowledge

Workflow 固定核心：

- Idea / Story Intent；
- Master Outline；
- Chapter；
  - Chapter Outline；
  - Draft；
  - Review。

此外，Writing 提供应用可理解的标准辅助资料类型。

建议包括：

- Character；
- World / Lore；
- Location；
- Organization；
- Item；
- Timeline / Event；
- Research；
- Note；
- Custom Type。

这些资料：
- 应用认识其语义；
- **但不强制所有作品都必须使用全部类型。**

---

# 16. 辅助资料采用统一规范

辅助资料不是任意格式的散乱笔记。

一旦正式进入 Writing Knowledge，应遵循标准 Schema。

目的：
- 方便 LLM 理解；
- 方便 Agent 检索；
- 方便一致性 Review；
- 方便跨模块 / 外部格式转换。

---

## 16.1 Core Schema + Project Custom Fields

采用：

> **固定核心字段 + 项目级自定义扩展字段。**

例如 Character Core 可以始终存在：

- Name；
- Aliases；
- Role；
- Description；
- Personality；
- Background；
- Goals；
- Relationships；
- Notes。

同时项目允许扩展：

修仙：
- 境界；
- 灵根；
- 宗门；
- 功法。

推理：
- 不在场证明；
- 秘密；
- 已知线索。

科幻：
- 义体；
- 权限等级；
- 舰船；
- 阵营。

这样：
- LLM 始终能依赖稳定核心语义；
- 作者仍可适应不同题材。

---

# 17. 标准资料的创建方式

## 17.1 手动表单

用户直接填写标准表单。

---

## 17.2 AI 解析导入

用户可以：
- 指定文件；
- 上传文件；
- 粘贴文本。

LLM 解析后：

```text
Source
  ↓
发现 Character / Location / Lore / Item / Event...
  ↓
生成标准表单
  ↓
Preview
  ↓
User Confirm
  ↓
写入 Writing Knowledge
```

### 必须有 Preview / Validation

AI 不允许：
- 解析完直接覆盖项目；
- 自动把歧义内容当事实。

---

## 17.3 保留来源

AI 提炼出的资料必须能追溯来源。

例如：

```text
Alice

Sources:
- 第一章.md
- 第三章.md
- 人物草稿.docx

[查看来源]
```

大型结构化导入时应尽量细化到具体 entry。

这样：
- 可以核查 AI 是否提炼错误；
- Review 可以展示设定冲突的证据来源。

---

# 18. SillyTavern 重型 World Info / Lorebook 导入

需要考虑：
- 数十 KB；
- 数百 KB；
- MB 级；
- 数百到数千 Entry 的世界书。

产品不能采用：

```text
整个世界书
   ↓
一次性丢给 LLM
   ↓
“帮我整理”
```

概念上应视为：

> **大型结构化知识库迁移。**

---

## 18.1 已知格式优先结构解析

对：
- SillyTavern Lorebook；
- Character Card + Lorebook；
- 自己的 Exchange Format；
- 未来其他明确支持的结构格式；

应先读取原始结构。

LLM 主要负责：
- 语义分类；
- 归一化；
- 实体识别；
- 合并；
- 关系理解；
- 歧义识别。

而不是让 LLM 自己猜文件格式。

---

## 18.2 大型导入分批处理

概念流程：

```text
Structured Lorebook
      ↓
Entries
      ↓
Batch Semantic Analysis
      ↓
Normalized Candidates
      ↓
Global Entity Resolution
      ↓
Conflict / Duplicate Check
      ↓
User Validation
      ↓
Writing Knowledge
```

规模增加意味着：
- 更多批次；
- 不意味着单次 Context 无限变大。

---

## 18.3 一个 Entry 不等于一张 Knowledge Card

例如：

```text
Entry #12  Alice background
Entry #38  Alice sword
Entry #79  Alice childhood
Entry #208 Alice & Bob relationship
```

应该能够归并为：

```text
Character: Alice
├─ Background
├─ Equipment
├─ Relationships
└─ Sources: #12 #38 #79 #208
```

必要时同一 Source Entry 也可以同时影响多个标准知识对象。

---

# 19. SillyTavern Trigger / Metadata 不能简单丢弃

已明确：

> **Trigger、关键词、Character Filter、Probability、Inclusion Group、Timed Effects 等 RP 元数据，可能包含非常重要的作者叙事意图。**

因此导入 Writing 时，不能简单归类为“技术垃圾”。

应通过 LLM 解释：

```text
RP Entry
├─ Content
├─ Keys / Trigger
├─ Character Filter
├─ Probability
├─ Inclusion Group
├─ Timed Effects
└─ ...
      ↓
Semantic Interpretation
      ↓
可能转换成：
- 世界事实
- 人物动机
- 行为倾向
- 情境条件
- 事件规则
- 随机事件
- 关系变化
- 叙事钩子
```

例如：

```text
Key: blood, wounded
Character Filter: Alice
Content:
Alice sees serious injury and becomes quiet...
```

可以转译为：
- Alice 的创伤；
- 触发该创伤的情境；
- 对行为与决策的影响。

---

## 19.1 三层导入模型

概念上保留：

### Layer 1 — Original Source
- 原始 ST Entry；
- 所有 metadata；
- 无损保存。

### Layer 2 — Writing Meaning
- LLM 对原 Entry 的写作语义解释；
- 行为条件；
- 动机；
- 世界规则；
- 事件规则；
- 情境约束。

### Layer 3 — Knowledge Cards
- Character；
- Lore；
- Event；
- Location；
- Item；
- Organization；
- Custom。

---

## 19.2 RP metadata 的语义判断

可按含义分为：

### Narrative
有明确世界 / 人物叙事含义  
→ 转换为 Writing Knowledge。

### Mixed
技术规则中包含叙事意图  
→ 提炼写作语义 + 保留原始字段。

### Operational
纯 RP 客户端运行逻辑  
→ 原样保存，不强行文学化。

最终判断仍需要用户可查看 / 修改 / 确认。

---

# 20. Canon：Agent 与作者采用不同约束

## 20.1 Agent 生成必须严格遵守 Canon

Agent 进行：
- 续写；
- 扩写；
- 重写；
- 正文生成；
- 大纲生成；

必须严格依据相关：

- Story Intent；
- Master Outline；
- Chapter Outline；
- Character Cards；
- World / Lore；
- Location；
- Organization；
- Item；
- Timeline / Event；
- 其他已确认 Knowledge。

原则：

> **相关 Canon 一旦存在，Agent 不得无理由违反。**

---

## 20.2 作者手写不被限制

作者在 Editor 中可以自由写任何内容。

应用不应：
- 阻止保存；
- 自动替作者修正文稿；
- 在输入过程中不断打断。

因为作者可能：
- 故意制造偏离；
- 正在写角色成长；
- 计划后续解释；
- 已经改变自己的创作方向。

---

# 21. Review 是强制步骤

正文完成后必须执行 Review。

但：

> **Review 的警告没有最终否决权。**

用户可以：
- 修复；
- 补充铺垫；
- 修改 Canon；
- 明确接受警告。

完成处理后仍可验收章节。

---

## 21.1 Review 类型

至少需要覆盖：

### Outline Completion
章节大纲目标是否实际兑现。

### Hard Contradiction
明确事实矛盾。

### OOC / Behavior Risk
行为与角色设定明显不符，且缺少合理因果。

### Continuity Gap
变化可能合理，但铺垫不足。

### World Rule Conflict
违反明确世界规则。

### Timeline
时间逻辑矛盾。

### Character Arc
角色变化是否存在足够铺垫。

### Theme Drift
章节是否明显偏离已确认的创作主题 / 基调。

---

# 22. OOC 的解决路径

发现潜在 OOC 时，不简单宣布“错误”。

应提供至少：

## A. 改成符合既有设定

Agent 建议调整正文。

用户查看后接受 / 拒绝。

---

## B. 保留当前行为，但补足合理性

例如：

```text
Canon:
Alice 极度恐高

正文：
Alice 爬上钟楼。
```

如果剧情上：
- 为救妹妹；
- 强烈恐惧仍然存在；
- 行动成本明确；

则可以通过新增：
- 生理恐惧；
- 内心冲突；
- 动机挣扎；
使表面偏离变成有效人物发展。

---

## C. 作者明确修改 Canon

用户可能真的改主意。

应用可以提示：

> 这项 Canon 修改可能影响哪些已有内容？

但不能自动偷偷修改整个项目。

---

# 23. Review 的最终原则

Reviewer 不检查：

> “角色有没有永远保持角色卡第一天的样子？”

而是检查：

> **“当前变化是否拥有足够的因果铺垫？”**

角色允许成长。

世界允许变化。

Canon 允许作者修改。

但重大变化不应凭空出现。

---

# 24. 当前阶段暂不展开的技术设计

以下内容此前有过较深入讨论，但已经明确暂停，等待产品需求整体 Grill 完成后再进入 Phase 3 技术设计：

- Provider 详细架构；
- Pricing Registry；
- Billing Profile；
- DeepSeek / OpenAI / Claude / Gemini 缓存经济；
- Adaptive Context Planner；
- RP 数据库具体设计；
- SQLite / FTS；
- OAuth；
- Usage Resolver；
- Coding Plan；
- Cursor / Kiro；
- Local Gateway；
- Relay；
- 缓存排列与成本策略。

### 保留但待技术阶段复核的方向

此前已达成较强共识，但后续仍应在技术阶段统一复核：

- Writing 面向普通文件 / 目录作为主要工作载体；
- RP 更适合结构化数据库管理；
- Lore / Memory / State 在 RP 中应彼此分离；
- Context Strategy 应考虑模型、成本、缓存与质量，而不应改变领域数据本身。

**这些不应在下一轮 Writing 产品 Grilling 中继续展开。**

---

# 25. 外部产品 / Skill 参考基线（仅作为参考，不替代本项目决策）

## Matt Pocock Skills

### `grilling`
核心规则：
- decision tree；
- one question at a time；
- 每题提供推荐答案；
- facts 自查；
- decisions 归用户；
- shared understanding 未确认前不执行。

Source:  
https://github.com/mattpocock/skills/blob/main/skills/productivity/grilling/SKILL.md

### `grill-with-docs`
值得借鉴：
- 决策不能只活在聊天里；
- settled vocabulary / decisions 应沉淀；
- 长会话需要持久交接。

Source:  
https://github.com/mattpocock/skills/blob/main/docs/engineering/grill-with-docs.md

---

## Sudowrite Story Bible

可参考但不照搬：
- Braindump；
- Synopsis；
- Characters；
- Worldbuilding；
- Outline；
- Scenes / Draft；
- Story Bible 作为 AI 写作时的持续上下文 / source of truth。

本项目的关键区别：
- 不允许从自由 Braindump 直接让 AI 随意补全 Story Intent；
- Story Intent 使用受控表单 + Grilling；
- 作者的未决策内容必须显式暴露；
- 章节 Outline → Draft → Review 形成更严格闭环。

Sources:  
https://docs.sudowrite.com/using-sudowrite/1ow1qkGqof9rtcyGnrWUBS/what-is-story-bible/jmWepHcQdJetNrE991fjJC  
https://docs.sudowrite.com/using-sudowrite/1ow1qkGqof9rtcyGnrWUBS/synopsis/r4GGUdR23VKcK2WrQVdheb  
https://docs.sudowrite.com/using-sudowrite/1ow1qkGqof9rtcyGnrWUBS/outline/3owKyHXUm1bCdp41b2Npjk

---

## SillyTavern World Info

值得支持：
- Lorebook / World Info；
- Keys / Triggers；
- Probability；
- Inclusion Group；
- Character Filter；
- Timed Effects；
- 动态激活；
- Token Budget。

本项目 Writing 导入的核心原则：

> 不把这些字段视为纯技术噪声，而是分析其中隐含的世界规则、人物动机、行为条件与事件逻辑。

Source:  
https://docs.sillytavern.app/usage/core-concepts/worldinfo/

---

# 26. 新会话继续 Grill 的起点

## 下一节：Master Outline / 总大纲

本轮已经决定：

```text
Story Intent
    ↓
Master Outline
    ↓
Chapter Outline
    ↓
Draft
    ↓
Review
```

同时已经明确冲突层级：

```text
Story Intent
→ L1 Primary / Story Conflict

Master Outline
→ L2 Arc Conflict

Chapter Outline
→ L3 Local Conflict
```

因此下一轮建议从：

> **Master Outline 的 Arc 层应该是什么？**

开始。

---

## 尚未敲定，只是上一轮提出的推荐

上一轮最后提出但**尚未由用户最终确认**：

> Master Outline 应先规划 Story Arc / Phase，再拆 Chapter，而不是直接面对几十章摘要。

建议新会话从这里继续 Grilling，不要视为已锁定。

---

# 27. 可直接复制到新会话的交接提示

```text
我们正在基于 Grill-me / grilling 方法梳理一个 AI 长篇写作 + Roleplay 桌面应用。

请先阅读我上传的《Writing 模块需求冻结稿 / 新会话交接文档》，不要重新询问其中已经标记为“已锁定”的决策。

继续遵循 grilling：
1. 沿决策树按依赖顺序推进；
2. 每次只问一个问题；
3. 每个问题给你的推荐答案；
4. 能从文档或公开资料得到的事实自己查，不要问我；
5. 创作/产品决策必须让我确认；
6. 当前继续只讨论产品功能与表现形式，暂不进入 Provider、数据库、OAuth、缓存等技术实现。

从 Writing → Master Outline / 总大纲继续。

上一轮最后尚未敲定的问题是：
“Master Outline 是否应该先规划 Arc / Story Phase，再拆 Chapter，而不是一开始直接规划章节？”

请从这里继续 Grill。
```

---

# 28. 当前冻结结论的一句话摘要

> **Writing 是一个 Workflow-driven、Form-guided、Grilling-controlled、Editor + Agent 协作的长篇创作系统：允许作者自由发散，但所有会影响后续创作的正式决策都必须被结构化、追问、确认并可追溯；Agent 必须严守 Canon，作者保有最终创作自由，而强制 Review 负责发现并处理大纲、人物、世界与连续性冲突。**


---


# Part B — v0.2 Checkpoint Addendum（2026-08-11）

> **状态**：本节记录 v0.1 之后本轮 Grilling 新增并已确认的产品决策。  
> **优先级规则**：如 Part B 与 Part A（v0.1）存在表述差异，以 **Part B 最新决策** 为准；未被 Part B 修改的 v0.1 内容继续有效。  
> **当前边界**：仍以 Writing 产品功能、Workflow、UX 和领域语义为主；Provider、数据库、缓存、OAuth、具体文件监听库、哈希算法、签名算法、模型路由实现等继续留到技术设计阶段。

---

# 29. 本轮新增的一句话总纲

Writing 的正式创作流程进一步收敛为：

```text
New Project
  ↓
Raw Ideas
  ↓
Story Intent
  ↓
Ending Direction
  ↓
Master Outline / Rough Arc Map
  ↓
┌──────────────────────────────────────┐
│ Arc N                                │
│                                      │
│ Arc Planning / Structural Grill      │
│      ↓                               │
│ Chapter N                            │
│   ├─ Chapter Contract                │
│   ├─ Scene(s)                        │
│   ├─ Draft                           │
│   ├─ Review                          │
│   └─ Chapter Accepted                │
│      × N                             │
│                                      │
│ Arc Closure                          │
│   ├─ Debt Settlement                 │
│   ├─ Canon / State Settlement        │
│   ├─ Warning Summary                 │
│   └─ Arc Accepted                    │
└──────────────────┬───────────────────┘
                   ↓
                Next Arc
                   ↓
          First Draft Complete
                   ↓
         Full Manuscript Review
                   ↓
             Revision Plan
                   ↓
          Revision Pass(es)
                   ↓
         Incremental Re-review
                   ↓
        Final Full Manuscript Review
                   ↓
            Final Acceptance
                   ↓
       Accepted Snapshot / Attestation
```

整体原则进一步明确为：

> **允许局部不确定性，但不允许未结算的不确定性跨越其所属的 Workflow Gate。**

以及：

> **系统负责暴露缺失、冲突、影响和风险；作者负责最终创作决策。**

---

# 30. Master Outline 与 Arc

## 30.1 Master Outline 必须显式包含 Arc / Story Phase

已锁定：

- Master Outline 不能直接从 Story Intent 跳到几十个 Chapter。
- 至少先建立一个 `Arc / Story Phase`。
- Chapter 必须挂在某个 Arc 下。
- `Arc` 是产品的中性领域概念，不等同于固定的三幕剧 `Act`。
- Story Intent 的 L1 Primary Conflict、Master Outline 的 L2 Arc Conflict、Chapter / Scene 的 L3 Local Conflict 层级继续有效。

概念层级：

```text
Story Intent
  ↓
Story-level Primary Conflict

Master Outline
  ↓
Arc / Phase
  ↓
Arc Conflict

Chapter / Scene
  ↓
Local Conflict
```

---

## 30.2 Arc 默认使用“起承转合”模板，但模板可替换

采用方案 B：

```text
创建 Arc
  ↓
默认：
起 → 承 → 转 → 合
```

但用户可以：

- 合并阶段；
- 增加阶段；
- 改名；
- 替换为其他结构；
- 完全自定义。

例如：

```text
Setup → Escalation → Crisis → Aftermath
```

或：

```text
探索 → 发现 → 灾难 → 逃亡
```

### 原则

> **结构层级强制；写作理论不强制。**

`起承转合` 是默认、最受产品理解和 Grilling 支持的 Arc Structure，但不是硬 Schema。

---

## 30.3 Arc Narrative Contract

Arc Structure 可以自由，但每个 Arc 必须在语义上覆盖足以形成叙事阶段的 Contract。

当前确定的核心语义包括：

```text
Arc Narrative Contract

Entry State
- Arc 开始时故事处于什么状态？

Arc Purpose
- 为什么整部故事需要这个 Arc？
- 它怎样服务 Story-level Primary Conflict？

Arc Conflict / Dramatic Problem
- 这一阶段主要处理什么问题？

Driving Forces
- 谁/什么试图改变当前状态？
- 谁/什么阻止这种改变？

Development
- 问题怎样真正发展，而不是原地踏步？

Meaningful Turn
- 局势 / 信息 / 关系 / 目标 / 信念发生了什么关键转向？

Exit State
- Arc 结束时故事处于什么新状态？

Consequence
- 本 Arc 的结果造成什么后果？

Transition
- 为什么故事因此进入下一个 Arc？
```

这些是 **Intent Dimensions**，不是九个固定大文本框。

用户自定义的 Arc 阶段只要能覆盖这些语义即可。

---

# 31. Arc 内允许探索性欠债，但禁止跨 Arc 带债

## 31.1 Soft Gate 的最终定义

Arc 内部允许：

- Structural Debt；
- 尚未完全确定的部分规划；
- 通过拆 Chapter / Scene 帮助作者探索当前 Arc。

但：

> **Soft Gate 只存在于当前 Arc 内部。**

---

## 31.2 Arc Closure 是 Hard Gate

一个 Arc 必须完整结算后才能开始下一个 Arc。

```text
Arc N
  ↓
Chapter Loop
  ↓
Arc Closure
  ↓
Debt Settlement
  ↓
Arc Accepted
──────────────────── HARD GATE
  ↓
Arc N+1
```

### 禁止

```text
Arc 1 △
  ↓
“先欠着”
  ↓
Arc 2 △
  ↓
Arc 3 △
```

因为这会让结构和 Canon Debt 利滚利。

---

## 31.3 Debt 必须有 Scope

不能因为项目还有未来未决事项就阻止创作。

需要区分：

```text
Project Debt
- 未来 Arc 的部分细节
- 尚未需要决定的后期内容
→ 可以继续存在

Arc Debt
- 当前 Arc Turn 未决定
- 当前 Arc Exit State 未结算
- 当前 Arc 产生的 Canon Debt
→ 禁止跨入下一 Arc

Chapter Debt
- Chapter Contract / Review / Acceptance 未完成
→ 由 Chapter Hard Gate 处理
```

---

# 32. Arc Closure / Arc Acceptance

Arc Closure 不只是检查“所有 Chapter 写完”。

至少需要确认：

```text
Arc Contract 已兑现
Structural Debt = 0
Arc-scoped Canon Debt = 0
当前 Arc 所有正式 Chapter Accepted
当前 Arc 的必要角色 / Timeline / World State 已结算
Active Storyline / Branch Canon 在当前范围完整
下一 Arc 的 Entry State 可以明确描述
```

Arc Closure 还必须重新汇总当前 Arc 的相关 Warning。

---

# 33. Narrative Diagnostics：Error / Warning / Info

本轮已正式锁定三级诊断体系。

## 33.1 Error — Workflow Incomplete

定义：

> **工作流必要内容缺失、未决定、未确认或某个强制步骤尚未完成。**

行为：

- 阻断当前 Gate；
- 不允许 Acknowledge 后强行通过；
- 必须补完 / 处理。

典型例：

- Arc Exit State 尚未决定；
- Arc Contract 必要语义缺失；
- Chapter 尚未 Review / Accepted；
- Enabled Story Branch / Storyline Canon 尚未补完；
- Required Narrative Obligation 到硬性 Target 仍未处理；
- Full Project Review Required 尚未执行；
- Stale / Needs Revalidation 仍未处理；
- Reconstruction Input / Workflow 尚未恢复完成。

核心规则：

> **Error = Workflow 内容缺失 / Decision Missing / Invariant 未满足。**

LLM 不得因为“觉得剧情质量差”而制造 Error。

---

## 33.2 Warning — Content Quality Issue

定义：

> **作者已经做出了决定，但当前内容存在明确的质量问题、叙事风险或已识别冲突。**

行为：

- 可以修改；
- 可以修改 Canon；
- 可以补充解释；
- 可以 Acknowledge；
- Acknowledge 后允许通过 Gate；
- Warning 本身仍持续存在，直到真正 Resolved。

典型例：

- Canon 冲突；
- OOC；
- 因果链断裂；
- 转折铺垫不足；
- Character Arc 跳变；
- Timeline 冲突；
- Theme Drift；
- 未兑现的 Narrative Obligation；
- Arc Conflict 解决过弱；
- 重复 Hook / Cliffhanger 模式。

---

## 33.3 Info / Note — Potential Quality Issue / Observation

定义：

> **当前证据不足以认定存在明确问题，但出现值得关注的趋势或潜在风险。**

行为：

- 不阻断；
- 不要求确认；
- 可以忽略；
- 后续证据增强时可升级为 Warning。

典型例：

- 某角色很久没进入主要因果链；
- Arc 中段开始拉长；
- 某个 Setup 暂未看到 Payoff；
- 某角色行为开始偏离以往模式；
- 支线占比持续上升。

---

## 33.4 Warning 生命周期

Warning 状态至少需要区分：

```text
OPEN
  ↓
ACKNOWLEDGED
  ↓
RESOLVED
```

另外增加：

```text
HISTORICAL
```

表示：

> 当时的问题仍存在于历史文本 / 决策记录中，但已不再对当前后续叙事造成持续风险。

### 重要

> **Acknowledged ≠ Resolved。**

Acknowledge 后：

- 不重复弹窗骚扰；
- 仍持续出现在 Diagnostics；
- Arc Closure 必须再次展示；
- Final Acceptance 必须再次汇总。

Arc Closure / Final Review 中应区分：

- Active Warnings；
- Previously Acknowledged；
- Historical Warnings。

高层 Review 可以把多个局部 Warning 聚合为新的 Arc / Manuscript-level Warning，但原始 Warning 仍可展开查看。

---

# 34. Story Branch / Storyline

## 34.1 Revision、Plotline、Local Variant、Story Branch 必须分开

### Revision

同一内容的历史版本。

### Plotline

同一 Canon 内并行存在的剧情线。

### Local Variant

不同写法 / 局部实现，但最终 Canon / 下游因果不分歧。

例如：

```text
A：Alice 当面质问 Bob
B：Alice 偷听 Bob
```

如果两者最终都只是：

```text
Alice 得知 Bob 在撒谎
```

则属于 Local Variant。

### Story Branch

从某个 Fork Point 开始产生下游 Canon / 因果分歧的互斥未来。

---

## 34.2 Story Branch 的成立门槛按“叙事后果”而不是文件层级

Fork 可以发生在：

- Arc；
- Chapter；
- Scene；
- Event；
- Character Decision；
- 其他稳定 Narrative Node。

但只有造成下游 Canon Divergence 的替代路线才升级为 Story Branch。

典型 Canon Divergence：

- 生死；
- 所在位置；
- Knowledge State；
- Relationship State；
- 目标 / Motivation；
- Event Outcome；
- Timeline；
- World State；
- Organization State；
- Item State；
- Arc Exit State；
- 后续必要前提。

---

## 34.3 Branch 生命周期

```text
Branch Idea
  ↓
Candidate Branch
  ↓
Structural Exploration / Comparison
  ↓
[Enable]
  ↓
Branch Canon Audit
  ↓
Canon Completion
  ↓
Enabled Branch / Storyline
```

Candidate Branch 可以很轻量。

但：

> **一旦要启用为可正式继续规划 / 写作的路线，就必须先完成该路线所需的 Canon。**

---

## 34.4 Branch Canon = 共享 Canon + Branch-specific Delta

不要求复制整个世界。

```text
Shared Canon
  ↓
Fork Point
  ├─ Storyline A
  │   └─ Local Canon A
  └─ Storyline B
      └─ Local Canon B
```

Fork 前共同 Canon 可被多条 Storyline 引用。

Fork 后只补齐：

- Changed Assumptions；
- Character State；
- Relationship State；
- Knowledge State；
- Timeline Delta；
- Arc / Motivation Delta；
- Obligation Delta；
- 其他受影响 Canon。

---

## 34.5 默认单 Active Canon，但可存在多条 Enabled Storyline

普通 Writing Project：

- 可以保留多个 Candidate；
- 可以有多个 Canon Complete / Enabled Storyline；
- 默认只有一条 `Active Canon Path / Active Storyline` 用于当前正式工作。

---

## 34.6 Story Branch 只分叉，不再汇流

本轮最终决定：

> **Fork 后的 Storyline 视为独立故事线，不支持 Reconvergence / Merge。**

即使后来剧情“碰巧回到相同地点 /事件”，仍然保持独立：

```text
Storyline A → Arc 5A
Storyline B → Arc 5B
```

如果用户想复用另一条线的规划：

```text
Copy / Derive / Adapt
```

生成独立对象，而不是共享 mutable downstream object。

### 明确不做

- Story Branch Merge；
- Reconvergence；
- Branch Rebase；
- Shared downstream Arc；
- 多父节点 Narrative Graph。

---

## 34.7 Shared Canon 修改不需要特殊 Branch 继承规则

不同 Storyline 可以引用同一 Shared Canon。

Shared Canon 改动时：

> **分别视为每条引用 Storyline 的普通 Canon Change。**

例如：

```text
Shared Alice Canon Changed
   ↓
Storyline A
→ Dependency Graph A
→ Incremental Review A

Storyline B
→ Dependency Graph B
→ Incremental Review B
```

共享的是上游数据，不共享 Review 结果和依赖图。

---

# 35. Ending Direction

## 35.1 Ending 不需要像后日谈一样详细

有效 Ending 可以非常简洁，甚至只是：

> “于是主角转身向山里走去。”

系统真正需要的是：

> **这个 Ending 的叙事含义是否足以指导 Arc 的总体发展方向。**

---

## 35.2 Ending 的阶段性硬约束

Story Intent 早期仍允许 Ending 暂时未知。

但：

> **在正式建立足以推进的 Master Outline / Arc Map 之前，必须拥有至少一个大致 Ending Direction。**

它要详细到足以回答：

- Primary Conflict 大致朝什么结果发展；
- 主角 / 故事总体状态大致走向哪里；
- 主要 Arc 为什么应该朝这个方向组织。

不要求：

- 每个 Main Cast 的后日谈；
- 每个角色的最终职业 / 婚姻 / 居所；
- 最后一句台词；
- 所有世界状态。

---

## 35.3 Ending Change 是项目级重大变更

Ending 可以随时修改。

但一旦 Ending 的叙事意义发生变化：

```text
Ending Changed
  ↓
Full Project Review Required
```

`Full Project Review Required` 本身属于 Workflow Error，必须先执行。

Review 完成后：

- 真正缺失的 Workflow 内容 → Error；
- 与新 Ending 产生的明确质量冲突 → Warning；
- 潜在影响 → Info。

重大上游变更触发 **重新验证**，不是默认把全部历史 Accepted 状态全部删除。

---

# 36. Chapter Contract

## 36.1 Chapter Outline = Narrative Contract

Chapter Outline 的职责：

> **定义“这一章必须完成什么”。**

目前锁定的四个硬语义：

```text
Purpose
- 为什么这一章存在？

Entry State
- 本章开始时相关人物 / 局势是什么状态？

Required Change / Exit State
- 本章结束以后，什么必须发生变化？

Narrative Contribution
- 这个变化怎样服务当前 Arc、人物弧、关系线、
  Setup / Payoff 或其他既有叙事任务？
```

这四项是语义目标，不要求表现为四个固定文本框。

---

## 36.2 其他要素采用 Conditional Grilling

不将以下内容强制成每章固定字段：

- Local Conflict；
- POV；
- Character Goal；
- Information Reveal；
- Setup / Payoff；
- Turning Point；
- Relationship Change；
- Action / Set Piece；
- Mystery / Clue；
- Tone / Pacing Requirement。

根据章节内容按需触发。

例如“喘息章 / 情绪章”不应被强迫创造一个反派或外部冲突。

---

# 37. Scene / Beat

## 37.1 Scene 是正式 Narrative Object，但没有独立硬闭环

正式 Chapter 进入 Draft 前至少包含一个 Scene。

多 Scene 章节必须明确 Scene 顺序。

但不采用：

```text
Scene Outline
→ Scene Ready
→ Scene Draft
→ Scene Review
→ Scene Accepted
```

这种重复的重型闭环。

硬 Gate 仍属于 Chapter。

---

## 37.2 Scene = Execution Plan

职责原则：

```text
Chapter Contract
= 本章必须完成什么

Scene
= 这些结果具体怎样发生
```

Scene 主要语义包括：

- Purpose；
- Entry State；
- Focus；
- Intent / Goal（如适用）；
- Development / Conflict（如适用）；
- Exit State；
- 对 Chapter Contract 的 Contribution。

Scene 不重复填写 Chapter 的 Arc-level Narrative Contribution。

---

## 37.3 Beat 是轻量可选结构

Beat：

> Scene 内部的顺序性事件 / 情绪 / 信息 / 行动节点。

- 可选；
- 不成为独立 Canon Object；
- 不成为独立 Grill / Review Gate；
- 主要用于帮助作者 / Agent 控制 Scene 展开顺序。

---

# 38. Author Input → Agent Structure → Author Confirm

Chapter / Scene 等正式规划统一采用：

```text
Author Free-form Input
  ↓
Agent Semantic Parse
  ↓
Structured Candidate
  ↓
Semantic Validation / Grill
  ↓
Author Edit / Confirm
  ↓
Confirmed Contract
```

原则：

> **作者先表达；Agent 负责结构化；作者最终确认。**

确认前：

- Author Input 是主要来源；
- Structured Candidate 只是 Agent 的解释。

确认后：

- Confirmed Contract 成为正式 Workflow Source of Truth；
- 原 Author Input 仍永久保留为 Source / Author Intent。

---

# 39. 根源性内容变更 → Dependency Invalidation

一旦已经确认的根源性内容发生语义变更：

```text
Root Content Changed
  ↓
Semantic Diff
  ↓
Dependency Impact Analysis
  ↓
Review Scope
  ↓
Revalidation
```

不能只标一个孤立 `Stale` 而不追踪影响。

---

# 40. Narrative Dependency Graph

## 40.1 系统自动维护为主

Narrative Dependency Graph 主要由系统根据：

- Confirmed Contract；
- Canon 引用；
- Arc / Chapter / Scene 关系；
- Character / Event；
- Setup / Payoff；
- Narrative Obligation；
- Agent Review；
- 语义关系；

自动生成。

作者：

- 可以查看；
- 可以校正；
- 不负责人工维护整张依赖图。

---

## 40.2 类似 `npm explain` 的影响解释

用户需要能够查看：

> “为什么 Chapter 18 因 Alice.Motivation 修改而需要重审？”

系统应展示依赖路径：

```text
Chapter 18
  → depends on Alice
  → depends on Alice.Motivation
```

---

## 40.3 Review Scope 默认增量计算

普通根源修改：

```text
Semantic Diff
  ↓
Dependency Graph
  ↓
Affected Set
  ↓
Incremental Review
```

只有：

- Ending 变更；
- Primary Conflict 等根级重大变更；
- 影响范围无法可靠收敛；
- 大规模外部改动；
- 用户主动要求；

才进入 Full Review。

Ending Change 已明确为无条件 Full Project Review。

---

# 41. Accepted / Confirmed 与 Validation Freshness 分离

所有经过 Confirm / Accept / Final Accept 的对象：

> **保留不可覆盖的历史验收事实。**

之后内容或依赖发生语义变更时，不删除历史 Accepted，而是让当前版本进入：

```text
STALE / NEEDS REVALIDATION
```

例如：

```text
Chapter 12

Last Acceptance:
Accepted Snapshot #12

Current Working State:
Changed Since Acceptance

Validation:
STALE
```

重审后当前版本重新成为 `CURRENT`。

---

## 41.1 避免超级 Status Enum

至少分成正交维度：

```text
Workflow State
- Not Started
- Planning
- Drafting
- Reviewing
- Accepted

Validation State
- Current
- Stale
- Review Pending
- Revalidation Required

Diagnostics
- Errors
- Warnings
- Info
```

因此合法状态例如：

```text
Workflow: Accepted
Validation: Stale
Warnings: 3
```

含义：

> 曾经完成验收，但当前版本已发生变化，旧验收不能覆盖当前内容。

---

# 42. Review 分层

正式锁定：

```text
Scene / Chapter Review
≈ 局部 / unit-level

Arc Closure Review
≈ 阶段 / integration-level

Full Manuscript Review
≈ 全书 / system-level
```

---

## 42.1 Chapter Review

检查局部成立性：

- Contract Completion；
- Local Continuity；
- Canon；
- OOC；
- Timeline；
- World Rule；
- 局部 Character Arc；
- Theme / Tone 风险；
- Scene Coverage；
- 本章状态变化。

---

## 42.2 Arc Closure Review

检查：

- Arc Contract 是否兑现；
- 多 Chapter 是否共同完成 Arc；
- Arc Conflict；
- Entry → Exit；
- Meaningful Turn；
- Arc-scoped Canon / Character / Timeline State；
- 当前 Arc Narrative Obligation；
- 跨章节节奏 / 人物变化；
- 累积 Warning 聚合。

---

## 42.3 Full Manuscript Review

不是简单 `forEach(chapter => review(chapter))`。

主要检查：

```text
Story Contract
- Opening → Ending 是否形成完整路径
- Primary Conflict 是否成立并得到收束
- Protagonist Motivation 是否支撑主线
- Ending 是否兑现 Story Direction

Arc Architecture
- 各 Arc 是否有必要
- Arc 间是否形成因果推进
- 是否存在重复功能 Arc
- 是否某 Arc 删除后主线基本不变

Character Arcs
- Main Cast 变化 / 稳定性是否完整
- 跨 Arc 转变是否有铺垫
- 角色是否中途失去 Narrative Role

Narrative Obligations
- Setup / Payoff
- Promise / Resolution
- Mystery / Answer
- Intentional Open

Global Continuity
- Timeline
- Canon
- Knowledge State
- Relationship State
- World State

Global Experience
- Pacing
- 重复
- Theme / Tone
- 高潮 / 低谷分布
```

高层 Review：

- 读取低层 Diagnostics；
- 发现 emergent pattern；
- 不机械重跑所有已经 Current 的局部检查；
- Stale / 受影响对象才重跑其局部 Review。

---

# 43. Narrative Obligation

## 43.1 正式一等 Narrative Object

定义：

> **作品已经建立、未来需要被处理或由作者明确决定开放 / 放弃的叙事承诺。**

类型可包括：

- Setup → Payoff；
- Question → Answer；
- Mystery → Reveal；
- Promise → Fulfillment / Subversion；
- Foreshadowing → Event / Meaning；
- Goal → Outcome；
- Threat → Resolution；
- Relationship Tension → Development；
- Intentional Open Thread。

---

## 43.2 Agent 自动发现 Candidate，作者确认

来源：

- Outline；
- Scene；
- Draft；
- Review；
- 作者主动创建。

流程：

```text
Narrative Content
  ↓
Agent Detection
  ↓
Candidate Obligation
  ↓
Author Confirmation
  ↓
Confirmed Obligation
```

Agent 不能自动把 Candidate 当正式 Obligation。

---

## 43.3 生命周期

至少：

```text
CANDIDATE
OPEN
PLANNED
PARTIAL
RESOLVED
INTENTIONAL OPEN
ABANDONED
```

`RESOLVED / PARTIAL / INTENTIONAL OPEN / ABANDONED` 等改变正式生命周期的状态：

> **必须由作者确认。**

Agent 只能发现 Potential Payoff / Candidate Resolution。

---

## 43.4 Setup / Payoff 关系不是一对一

允许：

```text
多个 Setup → 一个 Payoff
一个 Setup → 多个 Payoff
Macro Obligation → 多个 Child Obligations
```

因此是图状 Narrative Relation，而不是简单外键。

---

## 43.5 Salience 与 Resolution Horizon 正交

### Salience / Importance

例如：

```text
Core
Major
Minor
```

用途：

- 排序；
- 聚合；
- Warning 升级优先度；
- 风险解释。

**不能用于隐藏 Warning。**

---

### Resolution Horizon

```text
SCENE
CHAPTER
ARC
STORY
UNKNOWN
```

回答：

> 大致预计在哪个叙事范围内兑现。

Agent：

- 默认先推荐 Horizon；
- 提供理由。

作者：

- 最终确认；
- 可以修改；
- 可以 Unknown。

---

## 43.6 Expected Horizon ≠ Required Target

`Expected Horizon`：

- 预计范围；
- 越界 → Warning；
- 作者可调整。

`Required Target`：

- 作者明确承诺的硬性兑现 Gate；
- Agent 可以推荐；
- Agent 不能自行创建；
- 作者必须显式确认。

如果 Required Target 到期仍未处理：

```text
ERROR
```

因为这是作者已确认的 Workflow Contract 没完成。

---

## 43.7 Macro / Micro Obligation 可嵌套

例如：

```text
Macro:
谁杀了主角父亲？
Horizon: STORY

  ├─ 为什么父亲当晚去港口？
  │  Horizon: Arc 1
  │
  ├─ 戒指为什么在刺客手里？
  │  Horizon: Arc 2
  │
  └─ 真正凶手是谁？
     Horizon: STORY
```

长线 Obligation 持续开放不等于“拖太久”，系统应检查其 Child / Progress，而不是只按章数简单超时。

---

# 44. Narrative Hook

Hook 与 Obligation 分开。

## Hook

> **Reader Attention Device / 用于抓住读者继续阅读的叙事装置。**

可能包括：

- Question；
- Threat；
- Revelation；
- Cliffhanger；
- Emotional Hook；
- Mystery Hook；
- Other。

Hook 是一等但轻量的 Narrative Object。

---

## Obligation

> **Future Narrative Commitment / 未来叙事承诺。**

Hook 不一定产生 Obligation。

例如一个场景开头“谁在敲门”，下一页立即揭晓，可以只是 Hook。

如果 Hook 建立了需要未来处理的问题 / 承诺 /悬念：

```text
Hook
  → links to
Narrative Obligation
```

Review 可以检查 Hook 的重复、滥用、效果递减及由 Hook 产生的过量未回收 Obligation。

---

# 45. First Draft Complete → Revision Workflow

已锁定正式修订流程：

```text
First Draft Complete
  ↓
Full Manuscript Review
  ↓
Diagnostics / Findings
  ↓
Revision Plan
  ↓
Revision Pass 1
  ↓
Incremental Re-review
  ↓
Revision Pass 2
  ↓
...
  ↓
Final Full Manuscript Review
  ↓
Final Acceptance
```

---

## 45.1 Review 不自动改正文

Full Review 只产生：

- Diagnostics；
- Findings；
- Impact；
- Recommended Revision。

不能直接替用户全稿自动重写。

---

## 45.2 Formal Revision 必须先有 Revision Plan

Revision Plan 类似 change plan。

可以包含：

- Structural Pass；
- Character Pass；
- Continuity Pass；
- Prose Pass；
- 用户自定义 Pass。

不强制一种写作理论。

作者 Editor 仍可自由编辑，但：

> **自由修改 ≠ Formal Revision Workflow Complete。**

正式修订完成必须走：

```text
Plan
→ Execute
→ Review
```

---

# 46. Final Acceptance

Final Acceptance 验收对象是：

> **某一条 Storyline / Manuscript**

不是整个 Project。

因此：

- 其他 Storyline 仍在写；
- Candidate Branch 未完成；
- Raw Ideas 未整理；
- 废案仍存在；

都不阻止当前 Manuscript 完成。

---

## 46.1 Final Acceptance 硬条件

当前冻结条件：

```text
✓ Storyline Workflow Complete
✓ Final Arc Accepted
✓ Ending Confirmed
✓ Workflow Debt = 0
✓ Required Narrative Obligations overdue = 0
✓ Revision Workflow Pending = 0
✓ Final Full Manuscript Review Current
✓ Stale / Needs Revalidation = 0
✓ Unknown External Changes = 0
✓ File Conflicts = 0
```

Warnings：

```text
ANY NUMBER ALLOWED
```

Info：

```text
ANY NUMBER ALLOWED
```

---

## 46.2 Final Warning 可以一次性 Acknowledge

Final Acceptance 页面必须：

- 完整展示当前 Warning 总量；
- 分类；
- 展示重要 Warning；
- 允许查看全部。

但作者可以一次性：

```text
Acknowledge All Current Warnings
```

再：

```text
Accept Manuscript with N Active Warnings
```

不是要求逐条点 N 次。

### 仍然必须保留

- 每个 Warning 的历史记录；
- Accepted / Historical 状态；
- Final Snapshot 中验收时的 Warning Summary。

不能使用含义模糊的：

```text
Dismiss All
```

---

# 47. Accepted Snapshot、Export 与 Final Package

## 47.1 Export 与 Final Acceptance 完全解耦

任何阶段都允许：

- Export Manuscript；
- Export Current Chapter；
- Export Selected Chapters；
- Export Outline；
- Export Canon / Story Bible；
- Export Review Report。

未 Final Accepted 时只表明：

```text
WORK IN PROGRESS
```

但不阻止导出。

---

## 47.2 Final Acceptance 产生逻辑不可变 Accepted Snapshot

```text
Final Acceptance
  ↓
Accepted Snapshot v1.0
```

Snapshot 是应用内部的验收基线。

以后继续修改：

```text
v1.0 Snapshot
保持不变

Current Storyline
→ POST-ACCEPTANCE REVISION
```

再次 Review / Accept 后：

```text
v1.1 / v2.0
```

---

## 47.3 Final Package 本身不可能物理上锁

Final Package：

- 可以复制；
- 可以解压；
- 可以用 Word / 编辑器修改；
- 产品不尝试阻止用户操作文件。

目标改为：

> **允许修改，但修改后能够证明它已经不是原 Final Accepted Artifact 的原样副本。**

---

## 47.4 Final Package Integrity Manifest

Final Package 应支持 Manifest：

```text
Snapshot ID
Storyline ID
Accepted Version
Accepted At
Logical File List
Per-file Content Digest
Final Review ID
Warnings at Acceptance
Format / Manifest Version
```

整个 ZIP 的 digest 可以存在，但不能作为唯一逻辑身份，因为重新压缩可能改变 archive bytes 而逻辑内容未变。

---

## 47.5 Modified Final Package

如果 Final Package 文件发生修改：

```text
VERIFIED FINAL PACKAGE
  ↓
MODIFIED AFTER FINAL ACCEPTANCE
```

但内容仍可正常使用。

允许：

- 查看 Diff；
- 作为普通文件打开；
- 基于外部修改创建 Revision；
- 恢复 Accepted Snapshot；
- 另存 Derived Package。

外部编辑后的 Final Package 可以成为：

```text
v1.0 Final
  ↓
External Changes
  ↓
Working Revision v1.1
  ↓
Review
  ↓
Final Acceptance
```

---

# 48. Final Attestation / 签名

签名功能正式保留。

目标：

> **增强版本完整性、签署认可和创作过程证据链。**

不得宣称：

> “应用签名本身即可确定著作权归属。”

---

## 48.1 签名对象

优先签：

> **Canonical / Normalized Final Manifest + Content Digests**

而不是仅签整个 ZIP。

概念字段：

```text
Snapshot ID
Storyline ID
Accepted Version
Author / Pen Name
Signing Key ID
Accepted At
Manifest Digest
Manuscript Digest
Canon Digest
Outline Digest
Review Digest
Warning Summary
Signature
Optional Trusted Timestamp
```

---

## 48.2 验证层级

产品模型预留：

```text
Level 1
Local Accepted Snapshot Verification

Level 2
Portable Manifest + Content Digests

Level 3
Signed Final Attestation
+ Optional Trusted Timestamp
```

第三方可信时间戳 / TSA：

- 可后做；
- 不是 MVP 必须；
- 但数据结构从第一版预留。

换设备后的复杂独立验证、私钥恢复等不作为当前核心需求。

---

# 49. 项目文件应用外修改

## 49.1 产品行为不依赖“只有应用自己会改文件”

Writing 仍保留普通文件 / 目录作为主要工作载体的方向。

必须允许：

- VS Code；
- Typora；
- Obsidian；
- Agent；
- 脚本；
- Git checkout；
- 其他外部程序；

直接修改项目文件。

---

## 49.2 最终冻结为三层检测

```text
Runtime File Watcher
        +
Version Baseline / Git-style Diff
        +
Critical-Gate Reconciliation
```

### Watcher

负责：

> “这里可能变了。”

### Git-style Diff / Version Baseline

负责：

> “到底哪些逻辑内容变了。”

### Semantic Diff / Narrative Impact Analysis

负责：

> “这些变化意味着什么。”

---

## 49.3 Watcher 是报警器，不是事实来源

文件监听可能：

- 重复事件；
- 临时文件 rename；
- 丢事件；
- 网络 / 虚拟文件系统异常。

因此最终必须以磁盘最终状态 + Version Diff / Reconcile 为依据。

---

## 49.4 Critical Gate 前必须再次 Reconcile

例如：

- Accept Chapter；
- Close Arc；
- Switch Active Storyline；
- Final Acceptance；
- Export Final Package。

即使 Watcher 漏事件，也必须在 Gate 前兜底发现未知磁盘变化。

---

## 49.5 Local vs Disk Conflict

如果：

```text
Base
  ↓
App Local Unsaved Changes

同时

Disk External Changes
```

则进入：

```text
FILE CONFLICT
```

不能：

- 自动 reload 覆盖 Local；
- 自动 save 覆盖 Disk。

必须提供：

- Diff；
- Use Local；
- Use Disk；
- Merge。

File Conflict 与 Narrative Error / Warning / Info 分体系。

---

## 49.6 不直接污染用户自己的 Git Workflow

产品可以采用 Git-style snapshot / diff 思维，但：

- 不应假设用户项目没有 `.git`；
- 不应擅自 `git add / commit / checkout`；
- 不应把 App Acceptance 等同于用户 Git index。

底层未来可考虑：

- Shadow repository；
- Embedded Git；
- App-managed snapshot store；
- Git-compatible object store。

技术阶段再定。

---

## 49.7 Story Branch ≠ Git Branch

Narrative Layer：

- Story Branch；
- Local Variant；
- Canon；
- Arc。

Version Layer：

- Snapshot；
- Diff；
- Revision；
- History。

二者不得等同。

---

# 50. Reconstruction Mode / 已有作品重建

已有完整或半成品作品不允许：

```text
Import
→ 直接继续编辑
```

而必须进入独立：

```text
RECONSTRUCTION MODE
```

---

## 50.1 Reconstruction 是 Read-only Gate

在 Workflow 状态彻底梳理完成前：

正文允许：

- 阅读；
- 搜索；
- Diff；
- 查看来源；
- 引用讨论。

正文禁止：

- 修改；
- 删除；
- Agent 重写；
- Agent 续写；
- 新增正式正文。

锁的是：

> **Manuscript Mutation**

而不是整个应用。

---

## 50.2 Reconstruction 目的

不是“把所有旧内容都强行完成”。

而是：

> **准确确定所有已有内容当前属于哪个 Workflow State，并恢复 Current Workflow Frontier。**

例如：

```text
Arc 1      ✓ Retroactively Accepted
Arc 2      ✓ Retroactively Accepted

Arc 3      ● Current
  Ch.18    ✓ Accepted
  Ch.19    ✓ Accepted
  Ch.20    ● Draft In Progress
```

最终：

```text
CURRENT WORKFLOW FRONTIER
Arc 3 → Chapter 20 → Draft
```

然后才解除 Editor Lock。

---

## 50.3 已有正文不自动 Accepted

已有 Draft 必须补票：

```text
Existing Chapter
  ↓
Reverse Contract Reconstruction
  ↓
Scene Reconstruction
  ↓
Author Confirmation
  ↓
Retroactive Review
  ↓
Warnings Presented / Acknowledged
  ↓
Chapter Accepted
```

Arc 同理必须完成 Retroactive Arc Closure。

---

## 50.4 Reconstruction 严格遵循标准 Workflow 顺序

已锁定：

> **不能并行乱序确认。**

必须：

```text
Story Intent
  ↓
Ending Direction
  ↓
Master Outline / Arc Map
  ↓
Arc Contract
  ↓
Chapter Contract
  ↓
Scene
  ↓
Retroactive Review
  ↓
Arc Closure
  ↓
Determine Frontier
  ↓
Reconstruction Complete
```

Agent 可以提前扫描全文、缓存候选信息。

但：

> **上游尚未 Grill / Confirm，下游 Candidate 不允许正式进入 Workflow State。**

---

## 50.5 Reconstruction 仍然先由作者声明，再由 Agent Grill

对于：

- Story Intent；
- Arc Boundary；
- Current Stage；
- Main Cast；
- Ending；
- 伏笔；
- 当前写作位置；

采用：

```text
Author Declaration
  ↓
Agent Reads Existing Manuscript
  ↓
Evidence-based Grill
  ↓
Agent Recommendation
  ↓
Author Confirm
```

不是让 Agent 自动宣布：

> “这就是你的故事结构。”

---

## 50.6 Reconstruction Input 在重建期间被外部修改

Watcher / Diff 发现输入变化：

```text
RECONSTRUCTION INPUT CHANGED
```

受影响的已恢复结果必须：

```text
Invalidate Reconstruction Result
  ↓
Re-run affected reconstruction
```

不能基于旧文本继续完成 Reconstruction。

---

## 50.7 Reconstruction Complete Gate

至少需要：

```text
✓ 所有 Existing Content 已被 Workflow 分类
✓ Story Intent 已确认
✓ Ending Direction 已确认
✓ Rough Arc Map 已恢复
✓ 已完成 Arc 已追溯验收
✓ 已完成 Chapter 已追溯验收
✓ 当前未完成内容的 Workflow State 已明确
✓ Relevant Canon 已确认
✓ Required Narrative Obligations 已恢复 / 确认
✓ Reconstruction Errors = 0
✓ Warnings 已展示 / Acknowledge
✓ Stale Reconstruction = 0
✓ Disk State Reconciled
✓ Current Workflow Frontier 已确定
```

完成后生成：

```text
Reconstruction Baseline
```

然后解锁 Editor / Draft Agent。

---

# 51. Source Conflict 与 Narrative Conflict

任何存在互不兼容内容、且系统必须选择其一才能确定 Narrative Truth / Workflow State 的冲突：

> **必须询问作者。**

Agent 只能：

- 定位冲突；
- 提供证据；
- 说明影响；
- 提供推荐方案。

不能静默选择。

---

## 51.1 统一 Conflict Object

可包含：

```text
What Conflicts?
Evidence
  ├─ Source A
  ├─ Source B
  └─ ...

Why It Matters?
Affected Canon / Contract / Workflow

Agent Recommendation
Reason

Author Resolution
  ├─ Choose A
  ├─ Choose B
  ├─ Merge / Redefine
  ├─ Intentional Exception
  └─ Defer
```

如果选择 `Defer` 且当前 Gate 必须依赖该事实：

```text
ERROR
Unresolved Narrative Conflict
```

---

# 52. Root Conflict Analysis

大量表面冲突往往来自少量根冲突。

例如：

```text
Alice 父亲生死
  ↓
Motivation
  ↓
Arc Goal
  ↓
Timeline
  ↓
Ending
```

因此不能让用户机械处理 47 个症状。

---

## 52.1 Root Conflict 流程

```text
Raw Conflicts
  ↓
Normalize
  ↓
Conflict Dependency Graph
  ↓
Root Conflict Candidates
  ↓
逐个 Grilling
  ↓
Author Resolution
  ↓
Re-evaluate Graph
  ↓
Prune / Transform Derived Conflicts
  ↓
Remaining Conflict Queue
```

原则：

> **Grill root causes, not symptoms.**

---

## 52.2 Root Candidate 必须可解释

输出至少需要：

```text
Root Conflict Candidate

Claim
Evidence
Affected Conflicts
Propagation Explanation
Agent Recommendation
Reason
Uncertainty
```

作者必须能查看：

> “为什么你认为 C04 是 C01 的下游？”

类似 `npm explain` 的可解释依赖路径。

---

## 52.3 Root Conflict Analysis 是 High-Reasoning Task

不能假设所有模型都可靠。

尤其危险的是：

> **False Merge：把两个独立根因错误折叠成一个。**

False Merge 的风险高于“没合并、让作者多答几题”。

因此优化目标是：

> **在不隐藏独立创作决策的前提下尽可能减少重复问题。**

---

# 53. 模型能力退化机制

作为 Writing Agent 通用机制保留：

```text
ADAPTIVE
GUARDED
CONSERVATIVE
```

不绑定具体模型品牌。

---

## 53.1 CONSERVATIVE

弱模型强制。

允许：

- Conflict Extraction；
- Source / Evidence；
- Deterministic Grouping；
- 局部建议。

不允许：

- 高风险 Root Collapse；
- 将多个 Conflict 自动视为同一根因；
- 依赖一次推理结果修改 Conflict Graph。

原则：

> **宁可多问，不错误折叠。**

---

## 53.2 GUARDED

中间档主力模型默认候选。

允许 Root Analysis，但必须经过：

```text
Root Candidate
  ↓
Evidence Chain
  ↓
Dependency Validation
  ↓
Second-pass Challenge
  ↓
Safe to Collapse?
```

如果不能可靠通过，则保留为独立 Conflict。

---

## 53.3 ADAPTIVE

强模型默认。

可以进行完整：

- Conflict Extraction；
- Root Analysis；
- Propagation；
- Compression；
- Recommendation。

但仍必须：

- 展示证据；
- 展示传播链；
- 展示不确定性；
- 可以主动降级。

---

## 53.4 Task-specific Capability Certification

模型能力不按品牌 / Marketing 档位硬编码。

产品需要自己的 Eval，例如 Root Conflict Eval 至少检查：

```text
Root Recall
False Merge Rate
Evidence Fidelity
Propagation Accuracy
Recompute Accuracy
Abstention Quality
```

其中：

> **False Merge Rate 权重特别高。**

---

## 53.5 Case Complexity

同一个模型还需要根据当前任务复杂度动态选择模式。

```text
LOW
MEDIUM
HIGH
```

可参考：

- Conflict 数量；
- 实体数量；
- Source 数量；
- Dependency Depth；
- 跨 Chapter / Arc / Storyline 程度；
- Shared Canon；
- Timeline；
- Obligation；
- 上游是否仍未决策。

中档模型：

```text
Low / Medium
→ Guarded

High
→ Conservative
```

强模型：

```text
Adaptive
→ 可自主降级 Guarded / Conservative
```

---

## 53.6 运行时只能降级，不能自行升级

模型可以：

```text
Adaptive → Guarded
Guarded → Conservative
```

但不能：

```text
Conservative → Adaptive
```

超过离线 Capability Certification 的上限。

---

# 54. 当前完整 Workflow 的两种入口

## 新作品

```text
Raw Ideas
  ↓
Story Intent
  ↓
Ending Direction
  ↓
Master Outline / Rough Arc Map
  ↓
Arc Loop
  ↓
First Draft
  ↓
Revision
  ↓
Final Acceptance
```

## 已有作品

```text
Existing Manuscript / Project
  ↓
Read-only Reconstruction
  ↓
按标准 Workflow 自顶向下恢复
  ↓
Retroactive Review / Acceptance
  ↓
Current Workflow Frontier
  ↓
Unlock Editor
  ↓
继续普通 Writing Workflow
```

---

# 55. 本轮明确不做 / 不应误解

当前 Product Requirements 明确不采用：

- Story Branch Reconvergence / Merge；
- 把 Story Branch 等同于 Git Branch；
- 每 Scene 一个独立硬闭环；
- 每 Beat 一个 Canon / Review Object；
- 允许 Arc Debt 跨入下一 Arc；
- AI 因“质量差”抛 Workflow Error；
- Acknowledge 后把 Warning 隐藏 / 删除；
- Final Acceptance 前必须修完所有 Warning；
- Final Acceptance 才允许 Export；
- Final Package 物理只读；
- Reconstruction 导入后直接编辑；
- Reconstruction 中 AI 自动决定作品结构；
- Source / Canon 冲突由 AI 静默裁决；
- 弱模型高风险 Root Conflict Collapse；
- 把具体模型名字硬编码进业务 Workflow。

---

# 56. 当前尚未完整 Grill 的问题

以下问题 **尚未正式锁定**，下一会话不要把它们误认为已确认：

## 56.1 删除已验收对象的语义

尚未 Grill：

- 删除 Accepted Chapter；
- 删除 Accepted Arc；
- 删除 Confirmed Canon；
- 删除 Narrative Obligation；
- 删除 Shared Canon；
- 是否必须先 Impact Analysis；
- Delete / Deprecate / Archive 的差异。

这是当前状态机审计发现的下一项高优先级未决问题。

---

## 56.2 Source Role 的具体分类 UX

已经锁定：

> 所有真正 Narrative Conflict 必须作者裁决。

但以下仍未完全细化：

- 导入时 Source Role 是否必须显式存在；
- Agent 自动分类到何种粒度；
- 用户如何批量确认 Source Role；
- `Manuscript Evidence / Canon Candidate / Planning Source / Raw Ideas / Reference` 等是否作为正式枚举。

---

## 56.3 Final Attestation 的技术实现

需求语义已锁定，但以下属于后续技术设计：

- 签名算法；
- 密钥存储；
- Key ID；
- Manifest Canonicalization；
- Timestamp Provider / TSA；
- 是否接第三方存证；
- 跨设备验证 UX。

---

## 56.4 Version Baseline / Git-style Diff 的技术实现

产品行为已锁定，但以下尚未技术选型：

- Shadow Git；
- Embedded Git；
- App-managed object store；
- Fingerprint / hash；
- File watcher；
- Debounce；
- 3-way merge；
- Manifest 存储。

---

## 56.5 Model Capability / Eval 的具体评分线

机制已锁定，但技术阶段需确定：

- Eval Dataset；
- Threshold；
- Certification policy；
- Provider Mapping；
- Case Complexity 算法；
- 是否需要多模型复核；
- 成本 / 延迟策略。

---

# 57. 新会话建议继续 Grill 的起点

当前建议下一会话从：

> **“删除一个已经 Accepted / Confirmed 的 Narrative Object，究竟应该是普通 Delete、Archive / Deprecate，还是必须先执行 Impact Analysis？”**

开始。

原因：

- Accepted / Current / Stale 三维状态已经锁定；
- Dependency Graph 与 Impact Analysis 已锁定；
- 删除语义是目前状态机中明显剩余的断点；
- 这仍属于产品行为，而非底层技术实现。

继续遵循：

```text
1. 一次只 Grill 一个产品决策。
2. 每题 Agent 给推荐答案。
3. 事实 / 可查信息由 Agent 自查。
4. 正式创作 / 产品决定由用户确认。
5. 先解决上游根问题，再处理下游症状。
6. 当前仍优先完成 Writing Product Requirements，不进入 Provider / DB / OAuth / cache 等实现设计。
```

---

# 58. 可直接复制给新会话的交接提示

```text
我们正在继续梳理一个 AI 长篇 Writing + Roleplay 桌面应用。

请先完整阅读我上传的《Writing_Module_Requirements_Checkpoint_v0.2.md》。

该文件由：
- Part A：上一轮 v0.1 冻结稿原文
- Part B：本轮 v0.2 Checkpoint Addendum

组成。

如两部分存在表述差异，以 Part B 的最新决策为准。
不要重新询问已经标记为“已锁定”的产品决策。

继续使用 grilling 方法：
1. 沿依赖树按上游到下游推进；
2. 一次只问一个问题；
3. 每个问题都给你的推荐答案；
4. 能从项目、文件或公开资料得到的事实自己查；
5. 创作 / 产品决策必须让我确认；
6. 发现大量冲突时先找 Root Conflict，不要让我逐个处理症状；
7. 如果当前模型不足以可靠做高推理任务，使用 Conservative / Guarded 模式；
8. 继续先完成 Writing 产品需求，不进入 Provider、数据库、缓存、OAuth 等技术实现。

当前建议从以下尚未锁定的问题继续：

“删除一个已经 Accepted / Confirmed 的 Narrative Object，
究竟应该是普通 Delete、Archive / Deprecate，
还是必须先执行 Impact Analysis？”
```

---

# 59. v0.2 Checkpoint 一句话摘要

> **Writing 已经从“Workflow + Editor + Agent”进一步收敛成一个具备 Arc / Chapter / Scene 分层规划、Debt Hard Gate、持续 Diagnostics、独立 Storyline Branch、Narrative Dependency Graph、Narrative Obligation / Hook 追踪、分层 Review、Plan-driven Revision、Final Acceptance / Attestation、应用外变更重审以及 Read-only Reconstruction 的创作系统：AI 负责发现、结构化、解释、推荐和影响分析，作者始终拥有创作事实与冲突解决的最终决定权；允许探索，但任何属于当前作用域的 Workflow Debt 都不能跨过其 Hard Gate。**
---

# Part C — v0.3 Checkpoint Addendum（2026-08-12）

> **状态**：本节记录 v0.2 之后本轮 Grilling 新增并已确认的产品决策。  
> **优先级规则**：如 Part C 与 Part B / Part A 存在表述差异，以 **Part C 最新决策** 为准。  
> **当前边界**：仍然优先完成 Writing Product Requirements / Agent Runtime Product Behavior；Provider、数据库、具体存储、哈希、模型路由、具体线程/进程模型等技术实现继续后置。  
> **重要覆盖**：v0.2 中“作者始终亲自拥有所有正式 Narrative Decision 的最终确认”需要细化：作者仍拥有最高主权，但可以通过 Oversight Mode 将 Narrative Decision Authority 显式委托给 Agent；委托行为和 Agent 代为做出的决定必须完整留痕。

---

# 60. Narrative Change：Add / Modify / Remove / Reintroduce 统一抽象

## 60.1 Confirmed / Accepted Narrative Object 不采用普通 CRUD Delete

已锁定：

> **对已经进入 Confirmed / Accepted 状态的 Narrative Object 进行移除，本质上是一次 Narrative Change，而不是普通 Delete。**

其语义与开发中删除一个已经被依赖使用的依赖项更接近：Current Narrative Truth 发生变化，因此必须考虑下游引用、依赖和重新验证。

用户可以自由移除对象，但系统不得把它解释为“历史上从未存在过”。

历史 Acceptance / Snapshot / Change History 必须继续保留。

---

## 60.2 Archive 与 Narrative Removal 正交

`Archive` 仅表示 UI / Organization 层面的收纳，不改变对象是否属于 Current Narrative。

例如：

```text
Narrative State: CURRENT
Visibility: ARCHIVED
```

可以合法存在。

因此：

- Archive ≠ Remove from Current Narrative；
- Remove ≠ Hard Delete；
- History 永久可追溯。

---

## 60.3 不把 RETIRED / REINSTATED 身份问题过度上纲

本轮一度讨论 `CURRENT ↔ RETIRED ↔ REINSTATED`，随后进一步抽象并锁定：

> **真正的一等概念是 Narrative Change，而不是对象是否“还是原来那一个”的本体论问题。**

统一行为：

```text
Narrative Change
├─ ADD
├─ MODIFY
├─ REMOVE
└─ REINTRODUCE / RESTORE
```

Object Identity 主要服务于：

- History；
- Provenance；
- Dependency Explain；
- Accepted Snapshot；
- Version / Diff；

而不用于限制创作。

Reintroduce 可以与旧版本完全一致，也可以经过修改；真正重要的是 Before State → After State 的语义变化。

---

# 61. Narrative Change 的依赖判断与 Impact Analysis

## 61.1 先检查是否存在依赖，再决定是否进入完整 Impact Analysis

已锁定：

```text
Narrative Change
        ↓
Dependency Presence Assessment
        ↓
No relevant dependency
→ 留痕并完成变更

Has relevant dependency
→ Semantic Diff
→ Impact Analysis
→ Affected Set
→ Revalidation
```

因此：

> **Dependency Check 普遍存在；完整 Impact Analysis 有条件触发。**

没有任何相关引用时，不需要为了形式主义展示一套“影响对象 = 0”的重型流程。

---

## 61.2 显式依赖与语义依赖必须同时考虑

Dependency Presence Assessment 至少区分：

```text
Structural / Explicit Dependency
Semantic / Model-derived Dependency
```

显式引用、Contract 关系、Canon Reference、Arc / Chapter / Scene 层级等可由系统做确定性查询。

但长篇叙事存在大量隐式关系，例如人物恐惧、知识状态、隐藏动机、关系变化、因果铺垫等，不能因为没有结构化外键就宣称不存在依赖。

---

## 61.3 模型未发现语义依赖 ≠ 语义依赖不存在

已锁定：

```text
Semantic Dependency Assessment
├─ FOUND
├─ NO EVIDENCE FOUND
└─ UNCERTAIN
```

必须避免：

```text
LLM says no
= dependency does not exist
```

语义依赖检测的可靠度与：

- 当前模型能力；
- Context Window；
- 注意力 / 长上下文利用；
- Retrieval Coverage；
- Candidate Set；
- Task Complexity；

直接相关。

因此优先采用：

```text
Candidate Retrieval
→ Scoped Semantic Analysis
```

而不是默认把全书塞进 Context Window 后要求模型发现一切。

---

## 61.4 UNCERTAIN 不阻止 Narrative Change

已锁定：

> **Semantic Dependency = UNCERTAIN 时只产生显式 Warning，不把模型能力不足变成作者的创作硬锁。**

系统必须说明：

- 为什么不确定；
- 检查了什么；
- 哪些范围未能可靠覆盖；
- 当前没有证据 ≠ 已证明不存在。

作者 / Auto Agent 仍可继续完成 Narrative Change。

潜在风险最终通过后续：

- Chapter Review；
- Arc Closure Review；
- Full Manuscript Review；

继续收束。

---

# 62. Working Change 与显式提交

## 62.1 Confirmed Narrative Truth 不随每次输入即时变化

已锁定：

```text
Confirmed Narrative Object
        ↓
Author / Agent Edit
        ↓
WORKING CHANGE
        ↓
自由迭代
        ↓
Apply / Confirm
        ↓
正式 Narrative Change
```

不能把键盘输入过程中的每个中间状态都当作正式 Canon Change。

---

## 62.2 Narrative Change Set 是正式提交单位

一次创作决定可能同时影响多个 Narrative Objects，因此提交单位不强制为单对象。

```text
Working Change Set
├─ NO A changed
├─ NO B changed
├─ Arc Contract changed
└─ Timeline changed
        ↓
Apply
        ↓
ONE logical Narrative Change
```

这类似一个逻辑 commit，而不是要求用户逐个对象提交。

---

# 63. User / Agent 并发与 Execution Consistency

## 63.1 Agent Task 具有可追溯 Baseline 与隔离 Working Change

用户在 Agent 工作期间可以继续编辑。

不采用项目级硬锁。

```text
Current Narrative
      ├─ User Working State
      └─ Agent Task Change Set
```

Agent Task 必须知道其任务起始时基于哪个 Baseline，但不能把 Baseline 理解为“永远只允许相信旧状态”。

---

## 63.2 Tool Boundary 必须以 Current State 为事实来源

已锁定：

> **模型上下文可以包含旧知识，但真正执行 read / edit / mutation 时，工具层必须检查目标与关键推理输入的最新状态。**

不能只检查 Write Target。

概念上需要：

```text
Write Set Freshness
+
Relevant Reasoning Read Set Freshness
```

其中 Read Set 指：

> **对当前修改决策具有实质性依赖的输入，而不是 Agent 曾经看过的所有内容。**

如果关键输入变化：

```text
PRECONDITION CHANGED
→ 返回最新状态给 Agent
→ Re-read
→ Re-evaluate / Replan
→ Retry edit
```

---

## 63.3 最新状态变化后的自主 Replan 边界

已锁定：

```text
Current State changed
        ↓
原始用户意图在新状态下仍唯一明确？
├─ YES → Agent 可自行 Re-read / Replan / Continue
└─ NO  → 出现新的创作决策 → Grill / Ask Author
```

因此不能：

- 文件一变就凡事询问用户；
- 也不能以“自动 Replan”为名替用户偷偷新增创作目标。

---

## 63.4 Agent 可在 Tool Call / Checkpoint 获知 Current 变化

Baseline 保持可追溯，但系统可以在工具调用 / 执行检查点将与当前任务相关的新变化显式反馈给 Agent，使其重新规划。

不能静默混合：

```text
前半 reasoning based on old state
后半 reasoning silently based on new state
```

而必须有明确的 refresh / re-evaluation 过程。

---

# 64. Agent Task 中断、Retry 与 Resume

## 64.1 中断不自动丢弃 Working Changes

已锁定：

```text
Agent Task
├─ COMPLETED → Ready for Review
├─ INTERRUPTED → Incomplete Change Set retained
└─ FAILED → Incomplete Change Set retained
```

用户可：

- Resume；
- Retry；
- Inspect；
- Discard。

不得：

- 失败后自动写入 Current Narrative；
- 失败后自动清空全部工作成果。

---

## 64.2 Partial Work 不能被误当成完整 Proposal

Incomplete Change Set 默认不作为完整 Change Set 一键提交。

若用户希望保留其中部分成果，应重新抽取选中修改，组成新的 Working Change Set，再走正常：

```text
Semantic Diff
→ Dependency Check
→ Validation / Review
→ Apply
```

---

## 64.3 Retry / Resume 是一等能力

Retry 默认继续同一个 Top-level Task 与 Change Set，不因为一次超时制造新任务。

```text
Task #42
├─ Attempt 1: timeout
├─ Attempt 2: freshness changed → re-read
└─ Attempt 3: completed
```

技术性、瞬时、无需新 Narrative Decision 的失败允许有限 Auto Retry。

涉及：

- Canon Conflict；
- Task Assumption Invalidated；
- 重复无法完成；
- 需要新的创作决定；

则停止盲目自动重试，进入 Re-evaluate / Ask / Grill。

---

# 65. Specialized Agent System

## 65.1 不采用一套巨型 System Prompt 驱动所有 Agent

已锁定：

> **不同功能 Agent 使用独立 Agent Profile / Specialized System Prompt。**

例如：

- Grilling Agent；
- Story / Intent Agent；
- Outline / Arc Agent；
- Chapter Planning Agent；
- Drafting Agent；
- Character / Canon Agent；
- Timeline / Continuity Agent；
- Dependency / Impact Agent；
- Review Agent；
- Revision Agent；
- Reconstruction Agent；
- Research Agent；
- 后续 Custom Specialist。

最终 System Prompt 运行时拼装，而不是每个 Agent 复制一整套全局规则。

---

## 65.2 Runtime Kernel / Agent Constitution 保留为极薄不可覆盖层

已锁定：

```text
Runtime Kernel
+
Agent Profile
+
Project Instructions
+
Task Contract
+
Relevant Context / Canon
+
Runtime State
```

其中：

```text
Kernel
= HOW agents are allowed to act

Agent Profile
= HOW this specialist should think and work
```

Kernel 只保存系统级状态 / 工具 / 权限 / provenance / workflow invariants，不包含“怎么写小说、怎么审稿、怎么 Grill”等职责性提示。

因此：

> **共享的是协议，不共享角色。**

---

# 66. Agent Routing 与 Orchestrator

## 66.1 Workflow-first, Orchestrator-fallback

已锁定：

```text
Task type already known from Workflow / UI
→ deterministic route to Specialist

Free-form / cross-domain / ambiguous request
→ Orchestrator classify / decompose / route
```

例如：

```text
[Review Chapter]
→ Chapter Review Agent
```

无需再让 Router LLM 猜。

---

## 66.2 Orchestrator 默认组织 Specialist，而不是自己成为万能专家

已锁定：

Orchestrator 主要负责：

- 理解任务；
- 拆解；
- 路由；
- Task Dependency；
- 并发 / 顺序编排；
- 汇总 Result；
- 形成最终 Change Set / Findings。

只有满足以下条件的 Small Bounded Action 才允许顺手直接完成：

- 用户意图完全明确；
- 无新增创作决策；
- 不需要 Specialist 专业分析；
- Read / Write Scope 小且明确；
- 影响快速收敛；
- 不涉及复杂冲突 / 高推理判断。

> **Small ≠ 改动字数少；Small = 决策空间与影响空间都已收敛。**

---

## 66.3 Specialist 可在当前 Task Contract 内调用从属 sub-agent

已锁定：

Specialist 可以为完成自己当前任务而调用从属 Specialist / temporary sub-agent，例如：

```text
Arc Agent
├─ Character Analysis
├─ Timeline Check
└─ Dependency Check
```

但不能自行扩张 Top-level Task Scope。

如果发现必须：

- 推翻 Ending；
- 新增重大 Narrative Decision；
- 扩大用户任务范围；

则返回 Orchestrator / Author。

---

# 67. Agent 并发与调度硬限制

## 67.1 并发上限与最大委派深度属于 Runtime Hard Limit

已锁定：

不能只在 Prompt 中告诉 Agent“少开几个”。

Runtime 必须限制：

```text
Max concurrent agent runs
Max concurrent subagents per task tree
Max delegation depth
```

整个 Task Tree 共用并发预算，子节点不能各自获得新的独立预算。

达到上限的 READY Task 进入 Queue，而不是失败。

---

## 67.2 Dependency 优先于 Priority

调度顺序：

```text
1. Dependency satisfied?
2. READY?
3. Concurrency slot?
4. Priority / resource scheduling
5. Launch
```

Priority 只用于多个 Ready 节点之间的排序，不能越过 dependency。

---

# 68. Planning 与 Dynamic Execution Workflow

## 68.1 复杂任务先形成 Plan，再执行

已锁定：

复杂 Agent 请求优先进入 Planning：

```text
User Task
→ Explore / Analyze
→ Orchestrator Plan
→ User Decision
→ Execute
```

Plan 不只是报告，而是正式的执行入口。

---

## 68.2 Plan UI 采用成熟 Coding Agent 式结构

本轮根据 Claude Desktop 中实际 Plan 样式确定默认结构：

```text
Plan

Title
一句话说明准备完成什么

Context
- 当前背景 / 已确认事实
- 用户要求
- 重要前提

改动
- 主要修改范围
- 目标对象 / Arc / Chapter / Narrative Objects

说明
- 为什么这样做
- 关键约束 / 不做什么

验证
- Review / Validation 方法
- Contract / Canon / Timeline / Dependency 检查
- Completion Criteria
```

Plan 保持轻量，不把每一次 Tool Call、具体 Agent 数量和调度顺序写死。

---

## 68.3 Plan Decision Surface

Plan 提交后界面必须提供：

```text
[Reject]
[补充说明 / 修改计划]
[Accept & Execute ▼]
    ├─ Manual / Ask
    ├─ Accept Edits
    ├─ Auto
    └─ Bypass Permissions
```

`补充说明` → 回到 Planning，形成新 Plan 后重新展示。

`Accept & Execute` → 选择当前 Top-level Task 的 Oversight Override 并开始执行。

默认不因为一次 Task 选择 Auto 就永久改变整个项目的 Default Mode。

---

## 68.4 Approved Plan 是稳定 Contract；动态的是 Execution

本轮明确覆盖此前“执行中 Plan 可以由 Orchestrator 自主 version/replan”的建议。

已锁定：

> **Plan stable, execution adaptive.**

Approved Plan 在执行过程中不由 Orchestrator 自行修改。

可以动态改变的是：

- Specialist 启动时机；
- 并行 / 串行；
- Retry；
- Freshness Re-read；
- 从属 sub-agent；
- Tool Call 细节；
- 当前步骤的执行策略。

如果新证据证明 Approved Plan 的核心前提不成立，或完成目标必须超出 Approved Plan 的范围：

```text
PLAN BLOCKED
→ stop affected execution
→ return to Planning
→ produce replacement Plan
→ user approval again
```

不是让模型自行判断“Plan v2 继续执行”。

---

# 69. Task Completion Contract

## 69.1 Specialist 不能仅凭一句“完成了”把 Task 标记 Completed

正式 Task Node 必须包含 `Task Completion Contract`。

Specialist 只能：

```text
REQUEST COMPLETE
```

Runtime / Orchestrator 再进行 Completion Check。

---

## 69.2 Completion Condition 分 Deterministic 与 Semantic

```text
Deterministic
- Required outputs exist
- Required task nodes complete
- Required inputs read
- Schema complete
- Blocking Error = 0

Semantic
- Coverage sufficient
- Causal reasoning adequate
- Major omissions absent
- Evidence supports findings
```

能确定性验证的内容不交给 LLM 自证。

语义条件必要时由 Orchestrator / Reviewer 验证。

Completion Check 失败：

```text
→ return to Specialist
→ Continue / Retry
```

---

# 70. Task Result Artifact 与 Agent-to-Agent Handoff

## 70.1 Result Artifact 是默认交接接口

已锁定：

每个正式 Agent Task 完成后产出结构化 `Task Result Artifact`。

概念字段可包括：

```text
Task
Status
Conclusion
Findings
Evidence
Uncertainty
Diagnostics
Affected Narrative Objects
Recommended Follow-ups
Produced Against / Freshness Metadata
```

如果产生修改，则关联独立 Proposed Change Set。

---

## 70.2 Result ≠ Canon

Task Result Artifact 表示 Agent 的：

- Findings；
- Analysis；
- Evidence；
- Recommendation；

不自动成为 Narrative Truth。

---

## 70.3 默认传 Result，不传完整上游 Agent Transcript

```text
Default downstream context
→ Task Result Artifact

On demand
→ Evidence

Deep audit
→ Execution Trace / Full Agent Session
```

这样避免把大量工具日志、已推翻假设和中间过程污染下游 Context。

---

# 71. Task Result Freshness

## 71.1 Result 是“基于某一组输入得出的结论”，不是永久有效报告

已锁定：

Task Result 必须记录其关键输入 / provenance。

输入发生相关变化后：

```text
Result Validation
CURRENT
→ STALE / NEEDS REVALIDATION
```

原则与 Narrative Object 的 Accepted / Validation Freshness 分离一致。

---

## 71.2 Result Dependency 分级

下游 Task 对 Result 的依赖至少分为：

```text
REQUIRED
→ stale 时 BLOCK

ADVISORY
→ stale 时 Warning
→ 可继续 / 刷新

OPTIONAL
→ stale 不阻塞
```

目标：保持 Agent Context 新鲜，而不是任意一个旧分析变化就冻结整棵 Workflow。

---

## 71.3 分级责任

已锁定：

```text
Specialist Profile
→ 提供 Input Contract 默认值

Orchestrator
→ 根据本次 Task Contract 实例化实际 edge level

Running Specialist
→ 可提出 Upgrade / Downgrade + Reason

Runtime
→ 机械 Enforcement
```

Runtime 不自己做语义判断。

Orchestrator 可以覆盖 Profile 默认值，但若 Specialist 发现这样无法满足 Completion Contract，可以拒绝完成并要求升级依赖级别。

---

# 72. Oversight Mode：用户可以把创作权委托给 Agent

## 72.1 覆盖“所有 Narrative Decision 必须作者亲自点确认”的硬规则

已锁定：

> **作者拥有最高 Narrative Authority，但可以显式委托。**

本产品必须同时服务：

```text
Human writes, Agent assists
Human directs, Agent executes
Human gives an idea, Agent develops the entire work
```

用户完全可以只提供一个想法，然后把 Story Intent → Ending → Outline → Arc → Chapter → Draft → Review → Revision → Final Acceptance 全流程交给模型运行。

---

## 72.2 Auto 不绕过 Writing Workflow

最重要原则：

```text
AUTO
≠ idea → 一次性生成整本书

AUTO
= 完整 Workflow 仍运行
  只是 Human Confirmation
  → Delegated Agent Decision
```

因此 Auto 下仍然保留：

- Grilling；
- Contract；
- Dependency；
- Diagnostics；
- Review；
- Revision；
- Gate；
- Provenance；
- Freshness；
- Final Acceptance。

重型 Workflow 从“要求用户做流程”转变为“约束 Agent 把流程做完整”。

---

## 72.3 Oversight Mode 参考成熟 Coding Agent

产品层提供与 Claude Code / Coding Agent 接近的模式：

```text
PLAN
MANUAL / ASK
ACCEPT EDITS
AUTO
BYPASS PERMISSIONS
```

内部仍区分两个正交概念：

```text
Runtime Tool Permission
Narrative Decision Authority
```

但普通用户通过 Mode Preset 使用，不要求理解两个内部维度。

---

## 72.4 Auto 可以替作者做 Narrative Decision

Auto 下 Agent 可以：

- 完成 Grilling；
- 选择推荐答案；
- Confirm / Commit Narrative Decisions；
- Modify / Remove / Reintroduce Canon；
- Acknowledge Warning；
- Cross Workflow Gates；
- Revision；
- Final Acceptance。

但所有这些必须标记为 delegated provenance。

---

## 72.5 Bypass Permissions 进一步放宽 Runtime Permission

Bypass 属于更高风险的 Tool Permission 模式。

产品可以参考 coding agent 保留极少数系统级 circuit breaker；具体哪些操作即使 Bypass 仍拦截，留到技术 / 安全论证阶段。

Narrative delegation 与“把整个电脑完全交给 Agent”必须在内部区分。

---

# 73. Narrative Decision Provenance

## 73.1 Agent 代为决定必须显式标记

已锁定：

```text
Authority:
AUTHOR_CONFIRMED
AGENT_DELEGATED
```

并进一步区分：

```text
Proposed By: Agent
Confirmed By: User
```

与：

```text
Decided By: Agent
Authority: Delegated
Oversight Mode: Auto
```

不能混为一谈。

---

## 73.2 Provenance 跟 Narrative Decision / Object 走，不只存在 Session Log

至少能够反向追溯：

```text
Narrative Decision
→ Change Set
→ Task Result
→ Agent Task
→ Agent Run
→ Approved Plan
→ Original User Request
```

Session 未来即使被 compact / archive，作品本身仍能回答：

> “这个设定 / Ending / Canon 是谁、在什么权限模式下、基于什么原因决定的？”

---

# 74. Oversight Mode Scope 与运行中切换

## 74.1 层级继承

已锁定：

```text
Application Default
      ↓
Project Default
      ↓
Storyline / Workflow Override
      ↓
Task Override
```

最具体的 Scope 生效。

例如：

```text
Project = Accept Edits
Storyline B = Auto
Task #42 = Manual
```

合法。

---

## 74.2 Mode 可以运行中切换

Mode Change 是 forward-only runtime policy change。

不追溯改写已经完成的 Decision / Change / Acceptance provenance。

在下一个安全 execution checkpoint 生效。

```text
AUTO → MANUAL
→ 后续需要作者授权的操作暂停

MANUAL → AUTO
→ Agent 可接管尚未解决的决策
```

---

## 74.3 不强制生成“接管报告”

本轮明确拒绝：

```text
AUTO → MANUAL
→ 强制生成 takeover summary
→ 才允许用户继续
```

原因：用户通常正是通过查看会话 / 执行流发现方向跑偏或产生兴趣后主动接管。

如需要，可在设置中提供：

```text
Generate autonomous-run summary at task completion
```

作为 Top-level Task 尾部可选附加任务，而不是 Mode Change Gate。

---

# 75. Agent Activity / Transcript UX

## 75.1 信息密度参考 Claude Desktop / Claude Code Desktop 分级

已锁定：

提供多级 Transcript View，整体风格偏 Claude Desktop 的高透明度，而不是过度简化的结果式展示。

概念级：

```text
Summary
Normal
Thinking
Verbose
```

### Summary

只保留：

- 最终结果；
- 主要 Narrative Changes；
- 最终 Diagnostics；
- Task Completion。

### Normal

- 正常 Agent 对话；
- 折叠 Tool Calls；
- Specialist 启动 / 完成摘要；
- Execution 调整摘要；
- Change 摘要。

### Thinking

- Normal；
- 更详细的 Approach / 为什么；
- 为什么调用某 Specialist；
- 为什么改变执行策略；
- Recommendation reasoning（以 Provider /产品可展示能力为准）。

### Verbose

- Thinking；
- Tool Calls；
- Read / Search / Edit；
- Specialist Result；
- Retry / Freshness Check；
- Task Result Artifact；
- Dependency / Evidence；
- 可展开完整执行细节。

具体“Thinking”可展示到什么程度留到 Provider / UX 技术论证。

---

## 75.2 Background Tasks 是正式表现形式

已锁定：

类似 Claude Desktop / Claude Code 的 Background Tasks 面板，集中展示：

- Running sub-agents；
- Background shell / tool tasks；
- Workflow task status；
- Duration；
- Tool usage summary；
- Finished / Failed / Interrupted；
- View transcript；
- Stop。

不把所有并行任务都强塞进主对话。

---

# 76. 用户不能直接指挥 Specialist / Sub-agent

## 76.1 选择 Subagent 模型，而不是 Agent Team 模型

本轮明确锁定：

> **用户监督 Workflow，但不能越过 Orchestrator 成为第二个 Orchestrator。**

即：

```text
User
 ↓
Orchestrator
 ↓
Sub-agent / Specialist Run
 ↓
Result
 ↓
Orchestrator
 ↓
User
```

用户对正在运行的 Specialist：

```text
✓ View status
✓ View transcript
✓ View Result / Evidence
✓ Stop / Cancel

✗ Direct message
✗ Add task instruction directly
✗ Modify specialist Task Contract directly
✗ Force Complete
```

如果用户想纠偏：

```text
User → Orchestrator
→ Orchestrator updates execution / task instruction
→ Specialist retry / resume / replace / stop
```

原因：直接修改 Specialist 指令会使 Orchestrator 的 Task Contract、最终汇总和实际执行发生隐式分叉。

---

# 77. Specialist Catalog 与 Custom Specialist

## 77.1 Built-in Core Specialists + User-extensible Custom Specialists

已锁定。

内置一批产品真正理解其职责的 Core Specialist，同时允许用户扩展。

用户可以创建：

- 军事顾问；
- 推理诡计审查；
- 中文网文节奏 Reviewer；
- 特定世界观顾问；
- 其他项目 / 个人领域专家。

它们必须进入同一 Orchestrator / Permission / Task Result / Completion Contract 体系。

---

## 77.2 Custom Specialist 必须通过完整表单配置

不能只提供：

```text
Name + Prompt
```

建议表单至少包括：

```text
Identity
- Name
- Display Name
- Description

Routing
- Applicable Workflow Stages
- When to use
- When NOT to use
- Example Tasks
- 是否允许 Orchestrator 自动调用

System Prompt
- 完整 Specialist System Prompt 编辑器

Responsibility / Boundary
- Primary Responsibilities
- Out of Scope
- Change / Edit capability
- Delegation capability

Input Contract
- Required
- Advisory
- Optional

Output / Result Contract

Completion Contract

Capabilities / Tools

Runtime Constraints / Permission Ceiling

Preview
- Effective System Prompt
- Routing Preview
```

可以提供 Basic / Advanced View；技术实现时再决定具体 UI。

---

## 77.3 Applicable Stages 是多选

已锁定：

Custom Specialist 不硬绑定唯一 Workflow Stage。

例如 Psychology Specialist 可以适用于：

- Story Intent；
- Arc Planning；
- Chapter Planning；
- Review；
- Revision。

Routing 综合：

```text
Current Stage
+
Description
+
When to use
+
Task Contract
```

---

# 78. Specialist Scope、Built-in 定制与临时 Sub-agent

## 78.1 用户可管理的持久 Specialist Scope

最终收敛为：

```text
Built-in
User Library
Project
```

### User Library

跨 Writing Project 复用。

### Project Specialist

随作品保存 /迁移，为当前作品特化。

---

## 78.2 不把 Task / Session Specialist 当作产品级 Specialist

本轮明确修正：

> **所谓 Task / Session Specialist 本质上只是会话中由 Orchestrator 临时创建的 sub-agent，不应被单独上纲为用户可管理 Specialist Scope。**

因此：

- 用户不需要为一次任务手工创建一次性 Agent；
- Orchestrator 可以按需要临时构造 sub-agent instructions；
- 运行实例完成后返回 Result；
- 用户可查看 /停止；
- 默认不进入 Specialist Library。

概念上：

```text
Specialist Profile
= 可复用专家定义

Sub-agent Run
= 某次实际运行实例
```

---

## 78.3 Built-in Specialist 只读

**已锁定（本条在上一轮回答中提出，本轮用户特别提醒必须写入 Checkpoint）：**

> **Built-in Specialist 不允许直接修改原件。**

用户需要定制时：

```text
Duplicate
或
Explicit Override
```

生成 User / Project Specialist。

目的：

- 官方 Built-in 可持续升级；
- 用户修改不会和官方 Prompt / Contract 混在一起；
- 可以 Restore Built-in；
- Override 关系明确可追溯。

不采用仅凭同名 Agent 静默覆盖。

---

## 78.4 Custom Specialist Test Run 暂保留为候选能力

当前产品方向：

- Custom Specialist 保存时做 Profile Validation；
- 可尝试提供 `Test Run`；
- Test Run 是否保留为正式产品能力、测试深度、成本与模型要求，等待技术论证后决定。

**尚未锁定为 MVP 硬需求。**

---

# 79. Sub-agent Context 与 Resume

## 79.1 默认 fresh isolated context

已锁定：

普通 Sub-agent 不复制完整 Orchestrator conversation。

启动时由 Orchestrator 组装结构化 Task Packet：

```text
Runtime Kernel
+
Specialist Profile / temporary instructions
+
Project Instructions
+
Task Contract
+
Relevant Current Narrative Context
+
Required Upstream Result Artifacts
+
Current State / Freshness Metadata
```

目标：

- 避免主会话日志污染；
- 降低上下文成本；
- 让 Specialist 聚焦；
- 让 Current Project State 成为事实来源，而不是依赖主 Agent 的旧记忆。

---

## 79.2 Fork Context 仅作为特殊模式

若当前任务高度依赖尚未结构化的即时会话语境，例如刚刚经过大量 Grilling、许多信息尚未沉淀为正式对象，则 Orchestrator 可以选择：

```text
FORK CURRENT CONTEXT
```

但不是默认行为，也不要求用户手工选择。

默认：

```text
ISOLATED
```

特殊情况：

```text
FORK
```

---

## 79.3 Retry / Resume 优先恢复已有 Sub-agent Run

如果一次 Sub-agent 已经完成大量阅读 / Tool Calls 后因超时或中断停止，优先 Resume 原 Run，而不是重新生成一个空白 Agent 重做全部工作。

但 Resume 后依然必须执行：

```text
Freshness Check
→ relevant inputs changed?
→ re-read / re-evaluate if needed
```

> **Resume 保留工作记忆，不代表继续相信旧世界。**

---

# 80. 本轮对成熟 Coding Agent 的总体借鉴原则

已形成较强共识：

> **大部分通用 Agent Runtime 行为优先参考已经成熟的 Coding Agent / Agent SDK，不在没有必要时重新发明一套机制；只有 Writing 的领域语义、Workflow Gate、Narrative Authority、Canon / Dependency / Review 等部分做领域化扩展。**

重点参考：

- Claude Code / Claude Desktop；
- GitHub Copilot CLI / Custom Agents；
- OpenAI Agents SDK；
- 后续技术阶段可继续对比其他成熟 Agent Harness。

当前外部参考中的几个重要成熟模式：

- Plan → Approve → Execute；
- accept edits / auto / bypass；
- 独立 sub-agent context；
- User / Project scoped reusable agents；
- Main Agent / Lead 负责编排；
- Sub-agent 只回传 Result；
- Background Task；
- Task Dependency；
- Resume / RunState；
- Runtime Tool Permission；
- HITL；
- 专用 Agent Profile / Tool Set。

---

# 81. 当前尚未完整 Grill / 技术阶段待复核

## 81.1 Agent Context Compaction（下一轮建议起点）

**尚未 Grill。**

下一轮建议从：

> **长时间运行的 Orchestrator / Sub-agent 接近 Context 上限时，哪些信息可以 compact / summarize，哪些必须从 Source of Truth 重新注入，哪些内容绝不能只依赖摘要保存？**

开始。

应继续参考 Claude Code 等成熟 coding agent 的：

- auto compaction；
- context isolation；
- resume；
- plan accept 后是否清理 planning context；
- persistent project instructions；
- source re-read / freshness；
- subagent context management。

---

## 81.2 Source Role UX（v0.2 遗留）

仍未最终锁定：

- Source Role 是否必须显式存在；
- Agent 自动分类粒度；
- 用户批量确认；
- `Manuscript Evidence / Canon Candidate / Planning Source / Raw Ideas / Reference` 是否成为正式枚举。

这是当前 Writing Product Requirements 中仍需回头封口的老问题。

---

## 81.3 Specialist Catalog 最终内置清单

方向已锁定为 Built-in Core + Custom，但最终：

- 哪些角色必须是 Built-in Specialist；
- 哪些更适合 Skill / Tool / Prompt Module；
- Built-in Agent 之间的职责重叠如何消除；

仍应在 Product Gap Review 前统一审计一次。

---

## 81.4 Custom Specialist Test Run

仅作为候选能力保留，技术论证后决定是否正式进入产品。

---

## 81.5 Agent Context / Attempt History / Execution Trace 的具体保留策略

产品语义已存在，但具体：

- Trace 保留多久；
- Compaction 后保留什么；
- Attempt History 默认展示粒度；
- Result / Evidence / Raw Trace 存储；

继续后置到 Context Lifecycle / 技术设计阶段。

---

## 81.6 继续后置的技术设计

沿用 v0.2，不在下一轮 Product Grilling 中展开：

- Provider Architecture；
- Pricing Registry / Billing；
- SQLite / DB；
- OAuth；
- Cache Economy；
- Adaptive Context Planner 的底层实现；
- Shadow Git / Embedded Git / Object Store；
- Hash / File Watcher / Debounce / 3-way Merge；
- Attestation Signing Algorithm / TSA；
- Model Capability Eval Dataset / Threshold / Routing；
- 线程 / 进程 / IPC / Sandbox 的具体实现。

---

# 82. 当前总体完成度判断

截至 v0.3 Checkpoint：

```text
Writing Product Requirements overall
≈ 85%–90%

Core Writing Workflow
≈ 95%

Narrative State / Canon / Review / Dependency
≈ 90%–95%

Agent Runtime Product Behavior
≈ 80% 左右，已进入边界封口阶段
```

这不是开发进度，而是 **Product Requirement Decision Coverage** 的粗略估计。

当前已基本没有类似“Master Outline 到底是什么”这种可能推翻大面积设计的巨型未知项。

余下工作更接近：

- Context Lifecycle；
- Source Role；
- Specialist Catalog Audit；
- 少量 Agent Runtime 边界；
- 最终 Product Gap Review。

完成后即可宣布：

```text
Writing Product Requirements
→ FROZEN
```

再进入 Phase 3 Technical Architecture / Implementation Design。

---

# 83. v0.3 后建议 Grilling 顺序

建议继续：

```text
1. Agent Context Compaction / Context Lifecycle
2. Source Role UX
3. Built-in Specialist Catalog Audit
4. Agent Runtime Product Gap Review
5. Writing Product Requirements Final Gap Review
6. Freeze Writing Product Requirements
7. 进入 Technical Architecture
```

仍坚持：

```text
1. 一次只 Grill 一个决策。
2. 优先参考成熟 Coding Agent / Agent Harness 的既有行为。
3. 可查事实由 Agent 自查，不让用户回答事实题。
4. 产品 / 创作决策必须由用户确认，除非用户在实际产品中明确选择 Delegated / Auto Authority。
5. 先找 Root Conflict / Root Design Question，不逐个处理症状。
6. 当前不提前进入 Provider / DB / Cache / OAuth 等底层实现。
```

---

# 84. 外部参考基线（2026-08-12 复核）

以下用于后续继续对照成熟 Agent 行为，不替代本项目的领域决策。

## Claude Code / Claude Desktop

- Subagents：独立 context、自定义 system prompt / tools / permissions、user/project scope、resume、background 运行等。  
  https://code.claude.com/docs/en/sub-agents

- Permission Modes：default / acceptEdits / plan / auto / dontAsk / bypassPermissions；Plan 审批后选择执行模式。  
  https://code.claude.com/docs/en/permission-modes

- Desktop：Plan、Auto、Bypass 等桌面端行为。  
  https://code.claude.com/docs/en/desktop

- Agent Teams：Lead / teammate / shared task / dependency / completion hook；本项目只借鉴其中 Task / Dependency /并发等机制，不采用用户直接指挥 Specialist 的 team communication 模式。  
  https://code.claude.com/docs/en/agent-teams

- Parallel Agents / Background Agent View：background sessions、subagents、agent view、tasks。  
  https://code.claude.com/docs/en/agents

## GitHub Copilot CLI

- Custom Agents：Profile、Prompt、Tools、MCP、独立 subagent context。  
  https://docs.github.com/en/copilot/concepts/agents/copilot-cli/about-custom-agents

- Tool Permission：allow / deny / allow-all，deny 优先。  
  https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools

- Autopilot：Plan 后 Accept and build on autopilot，连续自主执行并可设置 continuation limit。  
  https://docs.github.com/en/copilot/concepts/agents/copilot-cli/autopilot

## OpenAI Agents SDK

- Agents / Handoffs：Specialist / handoff / input filter。  
  https://openai.github.io/openai-agents-python/agents/  
  https://openai.github.io/openai-agents-python/handoffs/

- Human-in-the-loop：tool approval / interruption / resume。  
  https://openai.github.io/openai-agents-python/human_in_the_loop/

- RunState：可序列化的 pause / resume boundary。  
  https://openai.github.io/openai-agents-python/ref/run_state/

---

# 85. 可直接复制给新会话的交接提示

```text
我们正在继续梳理一个 AI 长篇 Writing + Roleplay 桌面应用。

请先完整阅读我上传的《Writing_Module_Requirements_Checkpoint_v0.3.md》。

该文件是完整自包含 Checkpoint：
- Part A：v0.1 Original Baseline
- Part B：v0.2 Checkpoint Addendum
- Part C：v0.3 Checkpoint Addendum（最新）

阅读优先级：
Part C > Part B > Part A。

不要重新询问已经标记为“已锁定”的产品决策。

继续使用 grilling 方法：
1. 沿依赖树从上游到下游推进；
2. 一次只问一个问题；
3. 每题给出 Agent 的推荐答案；
4. 可以从项目 / 文件 / 公开资料得到的事实由 Agent 自查；
5. 产品 / 创作决策由我确认；
6. 发现大量问题时优先找 Root Conflict / Root Design Question；
7. 模型能力不足时使用 Conservative / Guarded；
8. 通用 Agent 行为优先参考成熟 Coding Agent（尤其 Claude Code / Claude Desktop、GitHub Copilot CLI、OpenAI Agents SDK），不要在没有必要时重新发明机制；
9. 当前继续完成 Writing Product Requirements，不提前进入 Provider、数据库、缓存、OAuth、具体存储等技术实现。

特别注意 Part C 最新覆盖：
- Confirmed / Accepted Narrative Object 的 Add / Modify / Remove / Reintroduce 统一为 Narrative Change；
- Dependency Presence Check 先于完整 Impact Analysis；
- Semantic Dependency 受模型注意力 / 检索覆盖影响，UNCERTAIN 只 Warning，最终 Review 收束；
- Working Change / Change Set 采用显式 Apply；
- Agent tool boundary 做 Current State / Read Set / Write Set freshness check；
- Agent 任务支持 Retry / Resume / Incomplete Change Set；
- Specialist Agent 使用独立 Profile，System Prompt 由 Runtime Kernel + Profile + Project + Task + Context 拼装；
- Workflow-first routing，Orchestrator 主要组织 Specialist；
- Agent Tree 有 Runtime 并发上限与最大深度；
- Plan 是稳定 Contract，动态的是 Execution；
- Task 必须有 Completion Contract；
- Task Result Artifact 是 Agent 间默认交接接口并具有 Freshness；
- Result Dependency 分 Required / Advisory / Optional；
- 用户可以通过 Manual / Accept Edits / Auto / Bypass 等 Oversight Mode 将 Narrative Authority 委托给 Agent；Auto 仍完整执行 Writing Workflow；
- 所有 Agent 代为做出的 Narrative Decision 都必须留下 Delegated Provenance；
- Oversight Mode 支持 Application / Project / Storyline-Workflow / Task 层级 override，并可运行中切换；
- Transcript 信息密度采用 Claude Desktop 式 Summary / Normal / Thinking / Verbose，并支持 Background Tasks；
- 用户不能直接给 Specialist / sub-agent 发指令，只能通过 Orchestrator；用户最多直接查看和 Stop；
- Plan 输出提供 Reject / 补充说明 / Accept & Execute，并可直接选择执行 Oversight Mode；
- Custom Specialist 使用完整表单、Applicable Stages 多选；
- Persistent Specialist Scope 只有 Built-in / User Library / Project；Task/Session 只是 Orchestrator 临时创建的 sub-agent，不是用户可配置 Specialist；
- Built-in Specialist 只读，用户通过 Duplicate / Explicit Override 定制；
- Custom Specialist Test Run 暂作为候选能力，待技术论证；
- Sub-agent 默认 isolated context，特殊情况下由 Orchestrator fork current context；Resume 后仍必须做 freshness check。

当前建议从以下尚未锁定的问题继续：

“长时间运行的 Orchestrator / Sub-agent 接近 Context 上限时，
哪些内容允许 compact / summarize，
哪些必须从 Source of Truth 重新注入，
哪些绝不能只依赖摘要保存？”

也就是 Agent Context Compaction / Context Lifecycle。

之后还需要回头封口 v0.2 遗留的 Source Role UX，
再做 Built-in Specialist Catalog Audit 和最终 Product Gap Review。
```

---

# 86. v0.3 Checkpoint 一句话摘要

> **Writing 已进一步从“具备 Agent 协作的重型创作 Workflow”收敛为一个可在人类主导与全自动 Agent 主导之间连续切换、拥有显式 Narrative Change / Working Change、Current-State Freshness、Specialized Agent Profiles、Orchestrator + Sub-agent 编排、稳定 Plan + 动态执行、Task Completion Contract、Result Artifact / Freshness、可委托 Narrative Authority、完整 Decision Provenance、Claude Desktop 式透明执行流以及 Custom Specialist 扩展能力的创作系统：重型 Workflow 不再意味着用户必须亲自完成每一步，而是成为约束 Agent 可靠地把一个想法发展成可追溯作品的执行框架。**


---

# Part D — v0.4 Freeze Candidate Addendum（2026-08-12）

> 本 Part D 记录 v0.3 之后围绕 Context Lifecycle、Workflow Session Relay、Source Classification、辅助资料、Agent 岗位、项目登记与最终 Gap Review 形成的最新业务决策。
>
> **阅读优先级：Part D > Part C > Part B > Part A。**
>
> 如 Part D 与旧部分存在冲突，以 Part D 为准。
>
> 当前状态：
>
> ```text
> Writing Product Requirements
> → FREEZE CANDIDATE
> ```
>
> 本部分仅冻结业务语义与产品行为；Provider、模型路由、数据库、缓存、具体 Git/Lock 实现、文件监听实现、权限沙箱、Context Budget 算法等仍属于后续 Technical Architecture / Implementation Design。

---

# 87. Context Lifecycle 的根原则

## 87.1 Agent Context 不是事实来源

正式锁定：

> **Agent Context 是可丢弃的执行视图，不是 Narrative / Workflow / Project State 的唯一事实来源。**

因此：

```text
Current Project State
Durable Workflow State
Narrative State
Task State
        ↓
Context Assembly
        ↓
Temporary LLM Working Context
```

不能反过来依赖某条 Conversation 保存项目的唯一真实状态。

任何在 Context 丢失后会改变：

- Narrative；
- Workflow；
- Task continuation；
- Authority；
- 用户已经明确表达的意图；

的信息，都必须在需要时进入可恢复的 Durable State；但“被持久化”不等于“自动成为 Canon”。

---

## 87.2 Context 信息的三种生命周期

仍保留三类逻辑：

### REHYDRATE

可以从 Current Source of Truth 可靠重建的内容。

恢复时：

```text
保存“需要重新读取什么”
而不是保存旧事实副本
```

例如：

- Current Canon；
- Current Character / World / Timeline；
- Current Arc / Chapter Contract；
- Project Instructions；
- Specialist Profile；
- Current Workflow State。

### PRESERVE

无法仅靠 Current Source 重建、但恢复运行必须精确保留的运行状态。

例如：

- 当前 Task Contract；
- Current Plan / Progress；
- Pending Decision；
- Pending Approval；
- Working Change Set；
- Incomplete Change Set；
- Oversight / Delegated Authority State；
- Required Result Dependencies。

### SUMMARIZE / DROP

可再生或低价值的执行历史。

例如：

- 大量 Tool Output；
- 已经提炼完成的大段文件读取；
- 重复讨论；
- 已推翻的探索过程；
- 已完成专项任务的中间过程。

Runtime 标记为必须 PRESERVE / REHYDRATE 的内容，模型无权自行丢弃。

---

# 88. Workflow-bound Fresh Session Relay

## 88.1 长篇连续性不依赖长寿命 Conversation

正式锁定：

> **Writing 的长期连续性由 Durable Workflow State / Narrative State 保证，而不是由长寿命 Conversation 保证。**

Arc 与 Chapter 都是 Workflow Object / Workflow Loop，不是必须持续存在的 LLM Conversation。

因此正常路径不再是：

```text
One Giant Session
→ Compact
→ Continue
→ Compact
→ Continue
→ ...
```

而是：

```text
Fresh Workflow Session
→ 完成当前 Workflow Unit
→ 固化 Current Project State
→ 生成下一阶段必要 Contract
→ End Session
→ Fresh Next Session
```

---

## 88.2 Chapter Loop 强制 Fresh Session

每个 Chapter iteration 默认开启 Fresh Session。

正常生命周期：

```text
Confirmed Chapter Outline / Contract
        ↓
Fresh Chapter Session
        ↓
Preflight Dependency Discovery
        ↓
Planning / Grill
        ↓
Draft
        ↓
Fresh Independent Review Agent
        ↓
Resolve / Revise
        ↓
Fresh Re-review（如需要）
        ↓
Chapter Accepted
        ↓
Current Project State Settlement
        ↓
Next Chapter Outline / Contract Ready
        ↓
END SESSION
```

下一章的新 Agent：

- 不继承上一章 Conversation；
- 不继承上一章的自然语言 Summary 作为主要事实来源；
- 只需要下一章已确认的 Chapter Outline / Narrative Contract；
- 其余信息自行从 Current Project State 按需重新读取。

正式原则：

> **Chapter-to-Chapter Relay 的唯一必要 Narrative Handoff 是下一章已确认的 Chapter Outline / Contract。**

---

## 88.3 Chapter Preflight：先判断需要什么，再按需读取

Fresh Chapter Agent 不应启动时全量读取 Story Bible / 全部角色 / 全部正文。

应先根据 Chapter Contract 推导：

```text
完成这一章需要知道什么？
```

再形成 Initial Relevant Reasoning Read Set，并从 Current Project State 按需读取。

Planning 前必须解析当前可以合理预见的关键依赖。

Execution 中如果出现新的真实依赖，可以增量扩展 Read Set。

---

## 88.4 Arc Loop 同样 Fresh，但默认使用 Sub-agent

Arc Session 与 Chapter Session 使用同一套：

- Dependency Discovery；
- Retrieval；
- Freshness；
- Current Source of Truth；

机制。

区别不是数据类型，而主要是：

- Relevant Object 数量；
- State Change 数量；
- Dependency Breadth；
- Cross-Chapter Evidence 数量；
- Reasoning Complexity。

因此：

```text
Chapter-level
→ Single-agent by default
→ Scope unusually large 时再升级 Sub-agent

Arc-level
→ Orchestrator + Sub-agents by default
→ Orchestrator 负责综合 Result
```

Arc Sub-agent 按 reasoning task / dependency cluster 动态拆分，不机械按照 Character / Timeline / Lore 文件类别切割。

---

## 88.5 Review 必须 Fresh Independent Context

正式锁定：

> **正式 Review 必须由独立 Reviewer Agent 在 Fresh Context 中执行。**

Reviewer 默认不继承：

- Drafting Conversation；
- Writer Agent 的自我解释；
- Writer Agent 的辩护；
- 失败过的探索方案；
- Previous Reviewer 的 Conversation。

Reviewer 应读取：

- Review Contract；
- Current Source of Truth；
- Target Artifact；
- Relevant Canon / Contract；
- Required Evidence；
- Diagnostics / Constraints。

Re-review 同样 Fresh。

Previous Review Result / Resolution Record 可以作为正式 Evidence 输入，但 Previous Reviewer Conversation 不继承。

---

## 88.6 Compaction 正式降级为兜底

正式锁定：

> **Compaction 只用于单个 Workflow Session 异常长、无法在 Context Budget 内自然到达 Session Boundary 时的 intra-session fallback。**

不允许因为“Context 还有空间”而跨越自然 Workflow Session Boundary 继续下一章 / 下一 Arc。

原则：

> **Compaction may extend a Workflow Run, but must not bypass a natural Workflow Session Boundary.**

---

# 89. Planning Grill 与 Runtime Grill

## 89.1 Planning 阶段承担主要 Decision Gap Detection

正式锁定：

> **已知的 Required Narrative Decision 必须优先在 Planning / Pre-execution 阶段暴露并 Grill，不允许故意拖到 Execution。**

Planning 应检查：

- Missing Narrative Decisions；
- Unresolved Intent；
- Ambiguous Canon；
- Conflicting Requirements；
- Required Dependency Gaps；
- Authority Boundary；
- Known Alternative Outcomes。

---

## 89.2 Runtime Grill 只作为 fallback

Execution-time Grill 仅用于 Planning 时无法合理预见的情况：

- 新 Source Information 出现；
- Current State 改变；
- 隐藏依赖暴露；
- 执行细化后才出现的真实歧义。

若原 Plan / Delegated Authority 足以唯一决定：

```text
Agent 自主 Replan
```

否则：

```text
Pause
→ Runtime Grill
→ Author / Delegated Decision
→ Update Plan / Working State
→ Resume
```

---

# 90. Agent 岗位与编排模型

## 90.1 正式 Workflow 的 Core 岗位

业务层按“岗位职责”定义核心专家，而不是按每个细分检查项都创建独立 Agent。

当前 Core Workflow 岗位：

### 项目经理

负责：

- 用户主交互；
- Workflow 编排；
- Plan；
- Task 拆分；
- Specialist / Sub-agent 调度；
- Gate；
- Result 聚合；
- Authority / Oversight 协调。

默认不亲自承担正文创作。

### 数据运维

负责：

- Source Classification；
- 项目结构化资料维护；
- Project Registry；
- Dependency / Retrieval 支持；
- Diff / Reconcile；
- 文件状态；
- 数据一致性。

### 故事策划

负责：

- Story Intent；
- Ending Direction；
- Master Outline；
- Arc Planning；
- Chapter Contract；
- Planning Grill。

### 写手

负责：

- Scene 展开；
- Draft；
- Rewrite；
- Revision。

### 审查编辑

负责：

- Chapter Review；
- Arc Review；
- Full Manuscript Review；
- Diagnostics；
- Revision 建议；
- Fresh Re-review。

---

## 90.2 研究员是 Workflow 外独立 Agent

研究员属于内置能力，但独立于正式 Writing Workflow。

用户可以直接使用研究员：

- 查资料；
- 发散想法；
- 验证现实信息；
- 推演设定；
- 整理研究结果。

权限边界：

```text
Structured Project Data
→ Read Only

Raw Area
→ Write Allowed

Structured Project Files
→ No Write

Narrative Change / Apply
→ No Permission
```

研究员产生的持久产物只能进入 Raw Area。

如果用户希望这些结果进入正式项目：

```text
Researcher Output
→ Raw
→ User-triggered Classification
→ Agent Suggestion
→ User Confirm
→ Structured Project File
```

研究员不能绕过 Classification 直接写 Character / Lore / Outline / Note / Canon。

---

## 90.3 后续按岗位分配 Tools

业务阶段只锁岗位与职责边界。

以下进入 Technical Architecture：

- 每个岗位具体 Tool Set；
- Read / Write Permission；
- Shell / Search / Web / Script 权限；
- 模型选择；
- 并发；
- Agent Tree 深度；
- 动态临时 Sub-agent 能力。

---

# 91. Raw Source 与 Structured Project Files 完全解耦

## 91.1 两层文件空间

正式锁定：

```text
Raw Source / Raw Area
≠
Structured Project Files
```

Raw Area 保存：

- 用户导入源文件；
- 粘贴输入；
- 研究员产物；
- 尚未分析 / 分类的待处理素材。

Structured Project Files 保存：

- 已经经过用户确认的项目内部数据；
- Narrative / Workflow Objects；
- Auxiliary Materials。

普通 Writing Agent 默认不从 Raw Area 做正常 Retrieval。

---

## 91.2 Classification 是用户主动触发行为

Classification 不允许静默自动发生。

### 项目初始化

如果用户在初始化阶段加入了 Source：

```text
Import Sources
→ Raw
→ User explicitly starts Classification
→ Agent Analysis
→ Inline Form
→ User Confirm
→ Structured Project Files
```

若用户不完成必要 Source Classification，则不能进入后续正式 Writing Workflow。

### 项目中途新增 Source

中途加入的新 Source 默认：

```text
UNANALYZED
→ 不属于正常 Agent 可用数据
```

只有用户主动触发 Analyze / Classify 后，才有资格进入结构化项目。

---

## 91.3 Agent 建议，用户确认

Classification UI 采用 Inline Form：

- Agent 提出结构化类型；
- Agent 提出内容解释；
- Agent 提出目标文件名；
- Agent 提出 Note Kind / Custom Kind（如适用）；
- 用户 Confirm / Edit / Reject。

Agent Suggestion 本身不等于正式项目状态。

---

## 91.4 Classification 一步到位

不再采用：

```text
Raw Source
→ Structured Source
→ Knowledge Object
```

而采用：

```text
Raw Source
→ Semantic Analysis
→ Candidate Project Objects
→ Inline User Review
→ Confirmed Internal Project Files
```

一个 Raw Source 可以生成多个 Project Files。

多个 Source 中的信息也可以被用户确认后合并进同一个已有 Project Object。

---

## 91.5 结构化项目文件不再绑定 Raw Source

正式覆盖旧规则：

> **结构化项目文件确认后，不保留 Raw Source 作为运行依赖，也不因 Raw Source 后续修改 / 移动 / 删除而自动变化或变为 Stale。**

Raw Source 采用 Snapshot / Ingestion 语义，不采用 Live Sync 语义。

如果用户以后希望吸收新的源文件或新版源文件：

```text
User explicitly Analyze New Source
→ New Candidate Change
→ Merge / Conflict Resolution
→ Review
```

不能因为外部 Source 改变而绕过 Narrative Change / Review。

---

# 92. Persistent Source Role（SR）正式删除

经最终审计，SR 原本试图表达的能力已经被更明确的机制完全覆盖：

```text
能否被 Agent 使用
→ Raw 隔离 + Project Registry / Retrieval Availability

它是什么
→ Object Type + Directory + Semantic Filename + Schema

它是不是 Canon
→ Narrative / Auxiliary Layer + Workflow / Validation State

它是猜测、问题、提醒还是观察
→ Note Kind

它是不是 Research / Raw Idea
→ Auxiliary Type

它有没有确认
→ Candidate / Confirmed / Accepted / Working Change 等状态
```

因此：

> **项目文件不再需要额外 Persistent Source Role 字段。**

Classification Agent 可以在分析过程中做临时语义判断，但一旦生成正式 Project Object，该临时分类不作为额外 SR 持久化。

旧 v0.2 / v0.3 的 Source Role UX 遗留项至此关闭。

---

# 93. Project File 的语义命名

项目文件必须具有真正可理解的文件名。

目录负责表达 Broad Class：

```text
notes/
research/
characters/
lore/
...
```

文件名负责表达 Specific Semantic Identity。

不接受以以下形式作为默认项目文件名：

```text
note-01.md
001.md
untitled.md
随机 UUID.md
```

Classification Inline Form 必须：

- 提供 Agent Suggested Filename；
- 显式提示用户文件名的实际作用；
- 允许用户修改；
- 对明显无语义名称进行警告 / 阻止。

文件名是 Agent 低成本候选筛选与人工文件管理的重要语义线索，但最终机器语义仍以结构化内容 / Schema 为准。

---

# 94. Non-Canon Auxiliary Documents

## 94.1 Auxiliary Material 也是正式项目文件，但不是 Narrative Truth

Project Structured Files 分为：

```text
Narrative / Workflow Truth
+
Auxiliary Materials
```

Auxiliary Materials：

- 可以被 Task-driven Retrieval 按需读取；
- 不自动成为 Canon；
- 不自动覆盖 Narrative Truth；
- 不因为被频繁引用而晋级。

---

## 94.2 Note Core Kinds

第一版 Core Kinds：

```text
观察
工作假说
开放问题
提醒
```

Core Kind 的解释权归系统。

其语义由 Runtime Kernel / System-owned Schema 固定，不允许项目重新解释。

目标：

> 抹平不同模型对简短 Kind 名称的理解能力差异。

### 观察

含义：

> 已经发现并值得长期记录的现象或模式。

用途：

> 后续规划、分析和审阅时作为观察依据，并结合 Current State 重新判断其是否仍成立。

### 工作假说

含义：

> 对人物、剧情、世界或其他项目内容的一种尚待作者确认的解释。

用途：

> 用于分析、寻找支持信息、比较解释方案，并在合适阶段推动作者确认。

### 开放问题

含义：

> 当前可以暂时没有答案，但未来可能值得解决的问题。

用途：

> 当问题进入相关 Workflow Scope 时重新呈现并判断是否已经变成 Required Decision。

### 提醒

含义：

> 未来某个对象、阶段或任务需要重新关注的事项。

用途：

> 在相关条件出现时重新提示作者或 Agent。

---

## 94.3 Core Kind 使用正向语义说明

系统不依赖大量“不要把它当作 X”的负面提示词约束模型。

Core Kind 的语义 Contract 应主要正向描述：

- 它是什么；
- 它什么时候被使用；
- Agent 应如何使用。

“Auxiliary 不是 Narrative Truth”等身份由数据层级 / Runtime 结构保证，不依赖模型理解否定提示。

---

# 95. Custom Kind

## 95.1 Agent 可主动提出，用户拥有最终决定权

当 Core Kind 无法准确表达某类高频项目笔记时：

```text
Agent detects recurring semantic category
→ Propose Custom Kind
→ Inline Form
→ User Confirm / Edit / Reject
→ Project Custom Kind Registry
```

Agent 只能提出，不能静默创建。

---

## 95.2 Custom Kind 的 Semantic Contract

表单至少包括：

```text
名称
含义
适用场景
使用方式
作用强度
```

Custom Kind 一旦被用户确认，其项目内解释固定使用。

不能只保存一个无法解释的任意字符串名称。

---

## 95.3 Scope

默认：

```text
Custom Kind
→ Project Scope
```

用户可以主动将其提升为：

```text
User-level Reusable Template
```

不能因为一个项目创建了 Custom Kind，就自动污染以后所有项目。

---

# 96. Auxiliary Promotion

## 96.1 作者定性后必须“晋级”，不能修改 Kind

例如：

```text
工作假说：
Alice 的疏离可能来自被抛弃恐惧
```

作者正式确认后：

```text
PROMOTE
→ Character / Alice
→ Narrative Change
```

不能把：

```text
Kind = Working Hypothesis
```

直接改成：

```text
Kind = Canon
```

Kind 描述 Auxiliary Object 是什么，不描述 Canon 权威状态。

---

## 96.2 Promotion 成功后原 Auxiliary Object 退出 Current Project State

正式锁定：

> **晋级是消费原 Auxiliary Object，而不是保留一个旧假说副本继续参与 Retrieval。**

Promotion 成功后：

```text
Target Narrative / Project Object updated
→ Original Auxiliary Object removed from Current Project State
```

不保留 Active / Archived Hypothesis 与新 Canon 同时进入正常 Retrieval。

历史过程仅通过：

- Version History；
- Change History；
- Narrative Change Record；

保留。

目标：

> 不人为制造“旧猜测 + 新事实”同时进入 LLM Context 的冲突提词环境。

---

# 97. Auxiliary 文件的废弃与 Kind 删除

## 97.1 普通 Note 不再需要

用户执行系统内“弃用 / 删除”操作时提供多选：

默认：

```text
移出 Retrieval 范围
```

可选：

```text
直接删除文件
```

---

## 97.2 删除 Custom Kind

Custom Kind 被删除时：

```text
Associated Files
→ INVALID / UNAVAILABLE
→ Remove from normal Retrieval
```

正文 / Manuscript 中已经依赖这些文件产生的内容：

- 不自动回滚；
- 不自动改写。

系统必须：

- 显式 Warning；
- 在必要的 Dependency / Review 阶段提示用户处理。

---

## 97.3 废弃工作假说

若用户通过系统正常删除 / 弃用：

```text
Dependency Presence Check
→ necessary impact handling
→ remove / disable
```

若用户直接从磁盘删除：

```text
Reconcile / Review
→ Missing dependency detection
→ Warning / Error as appropriate
```

---

# 98. 新 Source 更新已有 Project Object：Git-style Merge

当 Classification 产生的 Candidate 指向已有 Project Object 时：

```text
Incoming Candidate
+
Current Registered Object
+
Current Baseline
        ↓
Git-style / Three-way Merge Semantics
```

默认行为：

### 无文本 / 结构级冲突

```text
Auto Merge
→ Working Change
→ Semantic Diff
→ Dependency / Review
```

### 存在冲突

```text
FILE CONFLICT
→ Diff
→ User Manual Merge
→ Resolved Working Change
→ Review
```

正式原则：

> **文本无冲突不等于叙事无冲突。**

Git-style Merge 只解决文件 / 结构层合并。

合并成功后依然必须走已有：

- Semantic Diff；
- Dependency Check；
- Impact Analysis（如需要）；
- Review / Revalidation。

---

# 99. Project Registry Lock：项目登记锁

## 99.1 为什么需要登记锁

普通 Writing Agent 不应：

```text
scan project/**
→ 看到什么就读什么
```

而应只从正式登记的 Project Files 中发现 Retrieval 候选。

因此新增概念：

> **Project Registry / Project Registry Lock**

其职责不是 Narrative 语义，而是回答：

> **“磁盘上的哪些结构化文件当前被应用正式登记，可以参与正常 Agent Retrieval？”**

---

## 99.2 Registry 决定 Retrieval Surface

概念结构：

```text
project/
├─ characters/
├─ lore/
├─ notes/
├─ research/
├─ raw/
└─ .writing/
   └─ project.lock
```

Registry 至少逻辑记录：

- Registered Path / Object；
- Object Type；
- Schema / Version（概念层）；
- Current Content Digest / Baseline；
- Retrieval Availability。

Raw Area 不进入普通可检索 Registry。

---

## 99.3 Machine-managed

Project Registry Lock：

- 由应用维护；
- 用户可查看；
- 普通 UI 不提供自由编辑；
- 用户通过外部编辑器直接修改 Registry，不会自动获得系统信任。

如果 Registry 自身发生外部修改：

```text
REGISTRY NEEDS RECONCILIATION
```

用户不能通过手工编辑 Lock 文件直接把任意文件加入可信 Retrieval Surface。

“Last Trusted Registry Baseline”如何技术实现，留到 Technical Architecture。

---

# 100. 外部文件变化与 Registry Reconciliation

## 100.1 外部新增未知文件

磁盘出现结构化目录下的新文件，但 Registry 中不存在：

```text
UNREGISTERED
→ UNAVAILABLE
→ NOT RETRIEVABLE
```

UI 提示：

```text
Analyze / Reconcile
Inspect
Ignore
Delete
```

必须通过用户认可的 Reconcile / Classification 才能进入正式 Registry。

---

## 100.2 外部删除

Registry 记录对象存在，但磁盘文件消失：

```text
MISSING REGISTERED OBJECT
→ Remove from Retrieval
→ Dependency Check
→ Warning / Error as appropriate
```

不能解释为“历史上从未存在”。

---

## 100.3 外部移动 / 更名

默认：

```text
Old Path
→ Missing

New Path
→ Unregistered
```

系统可以根据内容摘要等提出：

```text
Possible Rename / Move
```

但不能静默认定。

用户确认后才更新 Registry。

---

## 100.4 外部仅修改内容

路径仍存在、Registry 仍登记，但内容发生变化：

```text
REGISTERED + MODIFIED
→ Diff
→ Semantic Diff
→ Dependency / Review
```

---

## 100.5 四层防线

正式业务防线：

```text
第一层：Project Registry
→ 决定文件有没有资格进入 Retrieval

第二层：File Watcher / Digest / Version Diff
→ 发现已登记文件是否发生变化

第三层：Dependency / Semantic Validation
→ 判断变化影响什么

第四层：Review
→ 判断当前作品语义是否仍成立
```

Critical Gate 前仍必须强制 Reconcile。

Review 是最终业务兜底，但不承担“未知文件为什么被读进 Context”这种低层隔离责任。

---

# 101. Agent Runtime Product Gap Review

本轮重新检查以下业务行为：

- Plan / Replan；
- Task Completion Contract；
- Result Artifact；
- Result Freshness；
- Retry；
- Resume；
- Incomplete Change Set；
- Partial Work；
- Current State / Read Set / Write Set Freshness；
- Manual / Accept Edits / Auto / Bypass；
- Delegated Narrative Authority；
- Decision Provenance；
- Background Tasks；
- Stop / Cancel；
- Specialist Isolation；
- Fresh Session Relay；
- Fresh Reviewer；
- Arc / Chapter Agent 默认策略；
- Runtime Grill fallback。

结论：

> **未发现新的阻塞级 Agent Runtime Product Gap。**

后续剩余主要属于技术设计：

- Agent 并发上限；
- 最大 Agent Tree 深度；
- Context Budget；
- Checkpoint 持久化实现；
- Provider / Model Routing；
- Tool Permission；
- Sandbox；
- Watcher；
- Registry Lock 信任基线；
- Shadow Git / Embedded Git / Snapshot Store；
- Custom Specialist Test Run。

---

# 102. Writing Full-flow Gap Review

## 102.1 新作品路径

当前可完整走通：

```text
New Project
→ Raw Source / Raw Ideas
→ User-triggered Classification
→ Structured Project State
→ Story Intent
→ Ending Direction
→ Master Outline
→ Arc Loop
    → Fresh Arc Session
    → Arc Planning
    → Chapter Loop
        → Fresh Chapter Session
        → Preflight Dependency Discovery
        → Planning / Grill
        → Draft
        → Fresh Review
        → Resolve / Revision
        → Accepted
        → Fresh Next Chapter Session
    → Arc Closure / Review
    → State Settlement
→ First Draft Complete
→ Full Review
→ Revision Plan
→ Revision
→ Re-review
→ Final Acceptance
```

---

## 102.2 已有作品路径

当前可完整走通：

```text
Existing Manuscript / Project
→ Raw / Existing Input
→ Read-only Reconstruction
→ Classification / Structure Recovery
→ Story Intent Recovery
→ Ending Direction Recovery
→ Arc / Chapter / Canon / Obligation Recovery
→ Retroactive Review / Acceptance
→ Current Workflow Frontier
→ Unlock Editor
→ Enter Normal Writing Workflow
```

---

## 102.3 项目中途变化

已覆盖：

- 新增 Raw Source；
- 新 Source Classification；
- Candidate 合并已有对象；
- 外部文件修改；
- 外部文件移动 / 删除 / 更名；
- 未登记文件；
- Auxiliary Notes；
- Custom Kind；
- Auxiliary Promotion；
- Narrative Change；
- Dependency Invalidation；
- Incremental / Full Review；
- Fresh Session Relay；
- Context fallback；
- Agent interruption / resume。

---

## 102.4 当前业务 Gap Review 结论

> **未发现新的主流程断点或需要重新设计主架构的业务缺口。**

Writing Product Requirements 已进入最终冻结候选状态。

---

# 103. 明确覆盖 / 废止的旧规则

以下旧规则在读取 Part A–C 时必须视为已被 Part D 覆盖。

## 103.1 “AI 提炼资料必须永久保留 Raw Source 引用”——废止

旧模型：

```text
Structured Knowledge
→ 长期 Sources: xxx.docx / chapter.md
```

新模型：

```text
Raw Source
→ Classification
→ User Confirm
→ Internal Project File
→ Runtime dependency on Raw Source ends
```

Raw Source 不再是结构化项目文件的持续运行依赖。

---

## 103.2 Persistent Source Role UX——废止

旧 v0.2 / v0.3 遗留的：

- Manuscript Evidence；
- Canon Candidate；
- Planning Source；
- Raw Ideas；
- Reference；

等统一 SR 枚举不再继续设计。

其职责由：

- Object Type；
- Auxiliary Type；
- Note Kind；
- Workflow / Validation State；
- Registry Availability；

分别承担。

---

## 103.3 “长期 Orchestrator 主要靠反复 Compact 续命”——废止

新规则：

```text
Workflow Session Relay
= Primary

Compaction
= intra-session fallback
```

Natural Workflow Boundary 优先结束旧 Session 并创建 Fresh Session。

---

## 103.4 Review Context 继承 Writer Context——禁止

正式 Review 强制 Fresh Independent Context。

---

# 104. 最新外部参考基线（用于技术阶段，不替代业务决策）

## Claude Code

- Subagents 使用独立 context window，可配置 system prompt、tools、permissions；适合隔离会产生大量搜索、日志、文件读取的专项任务。
- Fresh subagent 默认不继承父 Conversation 的完整历史与已读文件。
- Explore / Plan 等 Built-in subagent 体现了“岗位 / 职责 + Tool Restriction”的成熟模式。
- Compaction 会摘要会话，但部分持久规则从磁盘重新注入。

参考：
- https://code.claude.com/docs/en/sub-agents
- https://code.claude.com/docs/en/context-window

## OpenAI Agents SDK

- RunState 提供 durable pause / resume boundary；
- Human-in-the-loop 可在审批点中断并恢复；
- Agent / Handoff / context 与应用状态可分层处理。

参考：
- https://openai.github.io/openai-agents-python/ref/run_state/
- https://openai.github.io/openai-agents-python/human_in_the_loop/

## npm / Git

- npm hidden lockfile 只有在磁盘树仍符合登记状态时才可信，外部变更会使其失效；
- Git index / diff 提供“已登记状态 vs Working Tree”的成熟基线思想；
- Git merge-file 提供 Base / Current / Other 的三方合并及冲突人工解决语义。

参考：
- https://docs.npmjs.com/cli/v11/configuring-npm/package-lock-json/
- https://git-scm.com/docs/gitformat-index.html
- https://git-scm.com/docs/git-diff-index
- https://git-scm.com/docs/git-merge-file

---

# 105. Technical Architecture 阶段明确待办

业务需求冻结后，进入 Technical Architecture 时重点处理：

## 105.1 Agent / Tool

- 五个正式 Workflow 岗位的 Tool Set；
- 研究员 Tool Set；
- Tool Permission Matrix；
- Dynamic Sub-agent Tool Assignment；
- Provider / Model Routing；
- Capability Certification 落地；
- Agent 并发 / 深度。

## 105.2 Context / Runtime

- Fresh Session 创建与销毁；
- Checkpoint 数据结构；
- Durable Runtime State；
- Compaction fallback；
- Context Budget；
- Result Artifact Schema；
- Resume / Retry 实现。

## 105.3 File / Version / Registry

- Project Registry Lock 格式；
- Trusted Registry Baseline；
- Lock 防篡改 / Reconcile 机制；
- File Watcher；
- Content Digest；
- Shadow Git / Embedded Git / Snapshot Store 选型；
- Git-style Merge；
- 外部文件变化识别；
- Schema Versioning。

## 105.4 UI

- Source Classification Inline Form；
- Suggested Semantic Filename；
- Custom Kind Form；
- Conflict / Diff / Merge UI；
- Registry Reconciliation UI；
- Background Agent View；
- Fresh Review Result UI。

## 105.5 Later / Candidate

- Custom Specialist Test Run；
- Final Attestation 具体签名算法；
- Trusted Timestamp Provider；
- Provider-specific Thinking / Trace 展示能力。

---

# 106. v0.4 Freeze Candidate 结论

截至本 Checkpoint：

```text
Core Writing Workflow
→ CLOSED

Narrative State / Canon / Dependency / Review
→ CLOSED at Product Requirement level

Agent Orchestration
→ CLOSED at Product Requirement level

Context Lifecycle
→ CLOSED at Product Requirement level

Source Classification / Auxiliary Materials
→ CLOSED at Product Requirement level

Project File External Mutation Behavior
→ CLOSED at Product Requirement level

Agent Runtime Product Behavior
→ Gap Review Passed

Writing Full-flow Product Behavior
→ Gap Review Passed
```

当前建议状态：

```text
Writing Product Requirements
→ FREEZE CANDIDATE
```

除非最终通篇冲突检查发现新的 Root Design Conflict，否则下一阶段应转入：

```text
Phase 3
Technical Architecture / Implementation Design
```

---

# 107. 下一会话 / 技术阶段交接提示

```text
我们已经完成 Writing Product Requirements 的长期 Grilling，
当前最新文件为：

Writing_Module_Requirements_Checkpoint_v0.4_Freeze_Candidate.md

阅读优先级：

Part D > Part C > Part B > Part A。

Part D 已完成：
- Workflow-bound Fresh Session Relay；
- Chapter / Arc Context Lifecycle；
- Fresh Independent Review；
- Planning Decision Gap Detection；
- Chapter 单线 / Arc 多 Agent 默认编排；
- Core Workflow 岗位；
- 独立研究员 Agent；
- Raw Source / Structured Project File 解耦；
- 用户主动 Source Classification；
- Persistent Source Role 删除；
- Semantic Filename；
- Auxiliary Note Core / Custom Kind；
- Auxiliary Promotion；
- Project Registry Lock；
- 外部文件 Reconciliation；
- Git-style Merge；
- Agent Runtime Gap Review；
- Writing Full-flow Gap Review。

当前业务状态：

Writing Product Requirements
→ FREEZE CANDIDATE

下一步：
1. 只做最终文档冲突检查；
2. 若无 Root Design Conflict，则正式标记 FROZEN；
3. 进入 Technical Architecture / Implementation Design；
4. 优先从 Project File Layout / Registry / Schema / Agent Tool Permission Matrix / Runtime State 开始。
```

---

# 108. v0.4 一句话摘要

> **Writing 已从“一个长期依赖会话记忆的 AI 写作助手”进一步收敛为一个由结构化项目状态驱动、以 Arc / Chapter Fresh Session Relay 保持上下文新鲜、以独立 Fresh Reviewer 降低上下文惯性、以岗位化 Agent + 动态 Sub-agent 组织执行、以用户主动 Source Classification 将 Raw 素材一次性转化为解耦项目文件、以系统定义的 Auxiliary Note 语义管理非 Canon 信息、以 Project Registry Lock 控制真正可检索文件集合，并通过 Git-style Diff / Merge、Dependency Validation、Review 与 Critical-Gate Reconciliation 容纳用户直接编辑本地文件的重型长篇创作 Runtime；当前业务主流程与 Agent Runtime Gap Review 均已通过，进入 Product Requirements Freeze Candidate。**


---

# 109. 最终通篇冲突检查：语义澄清

本节用于消除 Part A–C 中仍可能因旧术语产生的误解，不新增新的业务主机制。

## 109.1 Workflow Author Input 不等于外部 Raw Source

旧规则中：

```text
Author Free-form Input
→ Agent Structure
→ User Confirm
→ Confirmed Contract
```

并允许保留原 Author Input 作为 Author Intent。

该行为不受“Raw Source 与 Structured Project Files 解耦”规则影响。

区别：

```text
External / Imported Raw Source
→ 一次性摄取素材
→ Classification 后不再成为结构化文件的运行依赖

Workflow Author Input
→ 应用内部产生的作者意图 / 决策上下文
→ 可以作为 Workflow Provenance / Author Intent 保留
```

因此：

> “结构化文件不保留 Raw Source 绑定”不等于“删除所有 Author Intent / Decision Provenance”。

---

## 109.2 导入时保留的 Operational / Original Fields 不等于 Source Binding

旧的 RP / Lorebook 导入设计允许：

```text
Mixed / Operational metadata
→ 提炼写作语义
→ 必要时保留可用原始字段
```

如果这些字段被用户确认后作为项目内部 Operational Data 保存，它们已经属于 Internal Project Data。

这不意味着：

- 保留对原始导入文件路径的依赖；
- 原文件变化后自动同步；
- Raw Source 删除导致该内部字段失效。

---

## 109.3 “Built-in Specialist 只读”仅指 Profile 配置不可直接修改

旧 Part C 的：

```text
Built-in Specialist 只读
→ Duplicate / Explicit Override 后定制
```

其含义是：

> **Built-in Specialist Profile / Definition 是系统内置模板，用户不能原地修改。**

不代表运行中的 Built-in Specialist 一律只有文件读取权限。

实际 Tool Read / Write Permission 由岗位职责决定，并在 Technical Architecture 阶段分配。

例如：

```text
写手
→ 必然需要允许创建 Working Draft / Working Change 的写能力

审查编辑
→ 正式 Review 默认以只读分析为主

数据运维
→ 需要受控结构化文件维护能力
```

---

## 109.4 “用户不能直接指挥 Specialist”与研究员不冲突

旧规则仍成立：

> 用户不能绕过 Orchestrator 直接给正式 Workflow 中正在运行的 Specialist / Sub-agent 改 Task Contract。

研究员属于：

```text
Workflow 外独立 Agent
```

不是：

```text
Orchestrator 下属 Specialist Run
```

因此用户可以直接与研究员对话，不构成对正式 Workflow Orchestration 规则的例外破坏。

---

## 109.5 Resume 只延续未完成的当前 Run，不跨自然 Workflow Boundary

旧的 Retry / Resume 规则继续成立，但适用范围明确为：

```text
Current unfinished Workflow Session / Task / Sub-agent Run
```

例如：

```text
Chapter 12 Session interrupted
→ Resume Chapter 12 Run
```

合法。

但：

```text
Chapter 12 Accepted
→ Resume Chapter 12 conversation to write Chapter 13
```

不再作为正常行为。

Chapter / Arc 自然 Gate 完成后应结束旧 Session，并由 Fresh Session Relay 进入下一 Workflow Unit。

---

## 109.6 Narrative Archive 与 Auxiliary Retrieval Disable 分离

旧 Narrative Object 的：

```text
Visibility: ARCHIVED
```

仍仅表达 UI / Organization 可见性，并不自动改变 Current Narrative Truth。

Auxiliary Note 的：

```text
移出 Retrieval
```

则表达：

> 当前普通 Agent 不再将该辅助资料作为候选上下文。

二者不是同一个状态枚举，不应因为都具有“收起来”的 UI 感受而混用。

---

# 110. 最终冲突检查结论

本次对旧规则重点检查了：

- Source / Author Input；
- Raw Source；
- Operational metadata；
- Source Role；
- Archive；
- Resume；
- Built-in Specialist read-only；
- 用户直接控制 Agent；
- Compaction / Fresh Session。

结论：

> **未发现需要重新 Grill 的 Root Design Conflict。**

发现的冲突均可由 Part D 的最新规则覆盖或由本节语义澄清消除。

因此 v0.4 仍维持：

```text
Writing Product Requirements
→ FREEZE CANDIDATE
```

---

# Part E — v0.5 Editor / Draft Workspace / Authority Addendum（2026-08-13）

## 110. Part E 总体结论

本轮补齐了 v0.4 中相对薄弱的 Editor / Authoring Workspace 产品语义，并进一步澄清正文与工作区的关系。

核心模型：

```text
Editor / Draft Workspace
≈ IDE / Source Workspace

Author / Writer Agent Editing
≈ Coding

Draft
≈ Source

Submit for Review
≈ Start Build / Validation

Review Candidate Snapshot
≈ Immutable Build Input

Chapter Review / Validation / Acceptance
≈ Build Pipeline

Manuscript Revision
≈ Built Authority Artifact
```

最终顶层原则：

1. **作者永远允许自由写作。**
2. **草稿永远只是草稿。**
3. **只有完整通过适用 Writing Workflow 的 Chapter 文本才能称为正文。**
4. **Editor 只编辑 Draft，不直接编辑 Manuscript Authority。**
5. **Chapter 是最小正文构建单元。**
6. **正常 Chapter Loop 始终只有一个 Current Workflow Frontier。**
7. **未来章节 Draft 可以提前存在，但在 Frontier 抵达前没有 Submission Eligibility。**
8. **每个 Project 同时最多只有一个 Active Authority Submission。**
9. **Writing 不支持 Authority Submission Queue。**
10. **历史正文 Revision Submit 后建立 Project-level Revision Barrier。**
11. **Authority World 串行；Draft Workspace 可自由并发。**
12. **正文、Narrative State、Dependency Graph 等 Authority Change 必须保持一致性与原子性。**

---

# 111. Draft / Manuscript 根语义

## 111.1 Draft / 草稿

任何尚未完整通过其所属 Writing Workflow 的 prose content 都属于 Draft。

Draft 来源可以包括：

- Author；
- Writer Agent；
- 外部编辑器；
- Import；
- Rewrite；
- Revision；
- Experimental Version；
- 未来章节提前写好的内容；
- 从既有正文复制出来的修改底稿。

无论 Draft：

- 已经写了多少；
- 质量有多高；
- 是否看起来已经完整；
- 是否由 Agent 认为“可以用了”；

只要尚未完成正式 Workflow，就仍然只是 Draft。

### Draft 可以

- 自由创建；
- 自由编辑；
- 自由删除；
- 自由重命名；
- 自由移动；
- 存在任意数量；
- 被用户或 Agent 显式参考；
- 作为未来工作素材；
- 包含错误；
- 不符合 Canon；
- 不符合 Chapter Contract；
- 提前涉及未来章节。

### Draft 不可以被动做到

- 成为正文；
- 改变 Current Manuscript；
- 改变 Current Narrative State；
- 改变正式 Canon；
- 改变 Narrative Dependency Graph；
- 推进 Workflow Frontier；
- 成为其他章节默认的正式 Narrative Dependency；
- 因为“保存了”或“写完了”而获得 Authority。

---

## 111.2 Draft Isolation Invariant

> **Draft 的存在和编辑不得被动改变任何已经建立的 Narrative Authority。**

因此：

```text
Reference
≠
Formal Dependency
```

Agent 可以在用户显式要求时读取 Draft，但必须将其视为：

```text
Authority: NON-AUTHORITATIVE
Dependency Eligible: NO
```

Draft 可以产生信息流，但不能产生正式 Authority 传播。

---

## 111.3 Manuscript Text / 正文

正文不是“小说文字”的泛称。

本产品中：

> **只有完成适用 Chapter Workflow、通过 Review / Validation，并完成 Acceptance / Materialization 的 Chapter Revision，才称为 Manuscript Text / 正文。**

正文具有：

- Chapter Identity；
- Revision Identity；
- Candidate Provenance；
- Review Result；
- Acceptance Record；
- Narrative Authority；
- Dependency Eligibility；
- Current / Superseded 等正式版本关系。

---

## 111.4 Manuscript Revision 逻辑不可变

Manuscript Revision 不提供原地修改语义。

禁止：

```text
editManuscriptInPlace()
```

已有正文修改必须走：

```text
Manuscript Rev N
↓
Create / Select Draft
↓
Edit Draft
↓
Submit
↓
Review
↓
Accept
↓
Materialize Manuscript Rev N+1
```

旧 Rev N 保持为历史事实，并可记录：

```text
Rev N
↓ superseded by
Rev N+1
```

---

# 112. Chapter 是最小正文构建单元

## 112.1 Build Unit

正式锁定：

> **Chapter 是最小 Manuscript Build Unit。**

```text
Scene / prose / edits
↓
Chapter Draft
↓
Chapter Workflow
↓
Chapter Manuscript Revision
```

Scene 仍然可以是正式 Narrative Object 和 Chapter 的执行结构，但：

- Scene 不独立正文化；
- Scene 不独立成为 Manuscript Artifact；
- Scene 与物理 Draft File 不要求一一对应。

Arc Review 更接近 Integration Review。

Full Manuscript Review 更接近 Global / Release Validation。

它们不是把一组 Draft 第一次“批量变成正文”的流程。

---

# 113. Chapter Draft Workspace

## 113.1 每章独立草稿区

每个 Chapter 拥有自己的 Draft Workspace。

示意：

```text
Chapter 07/
└─ Draft Workspace/
   ├─ rewrite-a.md
   ├─ writer-agent-v2.docx
   ├─ alternate-opening.txt
   ├─ scene-fragment.md
   └─ whatever-user-wants.md
```

Draft Workspace 中允许任意数量文件。

用户如何利用这些文件完全属于用户创作方式，不要求系统解释其语义。

用户可以：

- 一个文件写完整 Chapter；
- 一个文件写 Scene；
- 一个文件写片段；
- 一个文件存废稿；
- 一个文件存 Agent 试写；
- 保留多个完整 Chapter 候选稿；
- 自行拼接素材；
- 让 Writer Agent 整理。

---

## 113.2 不定义 Draft Composition

明确废弃：

```text
Chapter Draft Composition
Composition Manifest
Composition Source Ordering
```

系统不需要理解“多个文件共同组成一个 Chapter Candidate”。

正式规则：

> **One Submission = One Draft File Snapshot.**

用户最终 Submit 时必须选择一个单独 Draft File。

如果用户需要把多个片段组成完整稿，应在提交前自行整理成一个文件，或让 Writer Agent 协助。

---

## 113.3 Draft File 不需要声明“完整章节”类型

系统不要求：

```text
DraftFile.type = FULL_CHAPTER
```

用户点击 Submit 的行为本身即表示：

> “我认为这个文件当前可以作为本 Chapter 的完整 Review Candidate。”

如果文件实际上不完整：

```text
half-chapter.md
↓ Submit
```

由 Review 判断 Chapter Contract / Required Change / Exit State 等是否满足。

Editor / File System 不负责提前阻止。

---

## 113.4 Scene 与 Draft File 不建立强身份映射

不要求：

```text
1 Scene Object = 1 Draft File
```

可能出现：

```text
scene-01.md
scene-02.md
scene-03.md
```

也可能：

```text
chapter-07.docx
```

一次包含全部 Scene。

Formal Scene Object 属于 Narrative / Planning Layer。

Draft File 属于 Workspace Layer。

可以存在关联，但不建立 identity equality。

---

## 113.5 Draft 必须归属 Chapter

正式锁定：

> **Draft 必须属于某个 Chapter。**

没有 Chapter 归属的内容：

```text
→ Raw
```

不再设计独立的 project-level scratch draft 区。

---

## 113.6 Draft 跨 Chapter 移动

Draft 没有 Narrative Authority，因此：

```text
Ch.05/drafts/foo.md
↓ move
Ch.09/drafts/foo.md
```

之后它就是 Ch.09 Draft Workspace 中的普通 Draft。

不迁移 Workflow State，不产生 Narrative Change。

---

# 114. Draft 文件格式

## 114.1 第一阶段常见写作格式

产品层要求支持常见可编辑写作格式，至少包括：

```text
.txt
.md
.docx
```

这三类文件都可以：

- 作为 Chapter Draft；
- 在 Editor 中编辑；
- 被 Writer Agent 修改；
- 作为单文件 Review Input；
- 被冻结为 Review Candidate。

---

## 114.2 DOCX 是一等 Draft Format

DOCX 不只作为 Import / Raw Source。

从用户角度：

```text
chapter-07.docx
↓
Open
↓
Edit / Writer Agent Edit
↓
Save
↓
Submit
```

不要求用户执行：

```text
DOCX → Markdown → Edit → DOCX
```

这样的可见转换流程。

内部可使用 OOXML / WordprocessingML / Document Model / 确定性文档工具层实现，属于 Technical Architecture。

---

## 114.3 DOCX Candidate

DOCX Submit 时：

```text
Original DOCX
↓
Immutable Candidate Artifact
├─ original file snapshot
├─ content digest
└─ normalized review representation
```

Reviewer 可以使用规范化 prose / paragraph representation 以节约 Context，但 Candidate Identity 对应实际提交的原始 DOCX Artifact。

---

## 114.4 DOCX 保真范围仍需技术阶段确认

以下尚未在产品层最终锁死：

- 复杂 Word 排版的 round-trip fidelity；
- Track Changes；
- Comments；
- 复杂表格；
- 浮动对象；
- SmartArt；
- 宏；
- 复杂域；
- Word 特有高级排版。

当前产品要求是：

> TXT / MD / DOCX 均为可编辑、可提交 Draft Format。

复杂 DOCX feature fidelity 留 Technical Architecture / Editor Capability 评估。

---

# 115. Editor 打开章节与正文展示

## 115.1 打开 Chapter 默认进入 Draft Workspace

打开任意 Chapter：

```text
Open Chapter
↓
Enter Chapter Draft Workspace
```

如果该 Chapter 已存在 Current Manuscript Revision：

```text
Draft Workspace
+
Current Manuscript Read-only Reference
```

同时可访问。

具体：

- 左右并排；
- Tab；
- 上下布局；
- Split；
- Tool Window；

全部留 UI Design 阶段决定。

产品层只锁：

> **正文与草稿是两个独立对象，并且用户打开已有正文 Chapter 时可以同时访问二者。**

---

## 115.2 正文只读

正文即使显示在 Editor 中，也不因此成为可直接编辑对象。

```text
Displayed Manuscript
≠
Editable Draft
```

---

## 115.3 “基于正文修改”

提供便利入口：

```text
Current Manuscript Revision
↓ Copy
New ordinary Draft File
```

该功能本质只是复制正文内容到 Draft Workspace。

新文件：

```text
Authority: NONE
Dependency Eligible: NO
Editable: YES
```

可以保留：

```text
created_from_manuscript_revision
```

等 provenance，便于 Compare / Diff。

但它不是特殊 Draft 类型，也不是进入 Revision Workflow 的前提。

用户也可以直接使用自己长期保留的正文底稿。

---

# 116. Review Candidate Snapshot

## 116.1 Submit 固定一个文件

用户选择一个 Draft File：

```text
draft-a.md
↓ Submit for Review
```

提交时固定该文件当时内容：

```text
Review Candidate #17
= immutable snapshot
```

---

## 116.2 Candidate 与源 Draft 解耦

Candidate 创建后，源 Draft 仍可：

- 编辑；
- 重命名；
- 移动；
- 删除。

Candidate 不受影响。

Review 通过后，也不会自动：

- 删除源 Draft；
- 重命名源 Draft；
- 移动源 Draft；
- 用 Accepted 内容反写源 Draft。

---

## 116.3 Review 期间源 Draft 可继续编辑

例如：

```text
draft-a v10
↓ Submit
Candidate #17 = v10

source draft:
v10 → v11 → v12
```

Reviewer 仍然只审 Candidate #17 / v10。

即使 #17 PASS，也不能证明 v12 合格。

---

# 117. 正常 Chapter Loop 与 Submission Eligibility

## 117.1 Single Workflow Frontier

正常写作：

> **始终只有一个 Current Workflow Frontier。**

例如：

```text
Ch.01 ✓
Ch.02 ✓
Ch.03 ✓
Ch.04 ← Current Workflow Frontier
Ch.05 Drafts may exist
Ch.06 Drafts may exist
```

只有 Ch.04 具有 Submission Eligibility。

---

## 117.2 Future Draft 可存在，但 Future Review 不存在

作者可以提前写：

```text
Ch.08 Draft
Ch.12 Draft
```

但：

```text
Ch.08 Submit ✗
Ch.12 Submit ✗
```

直到：

```text
Ch.N Accepted
↓
Handoff
↓
Fresh Next Chapter Session
↓
Workflow Frontier → Ch.N+1
```

下一 Chapter 才获得正式 Workflow Eligibility。

---

## 117.3 下一章必须基于上一章 Handoff

Chapter Loop 仍然是：

```text
Chapter N
↓
Planning / Contract
↓
Drafting
↓
Submit
↓
Fresh Review
↓
Resolve / Revision
↓
Acceptance
↓
Materialize Manuscript
↓
Narrative / Dependency Update
↓
Handoff
↓
Fresh Chapter N+1
```

未来草稿的提前存在不能替代这个 Handoff。

---

## 117.4 非 Frontier Chapter 仍可自由打开与编辑 Draft

Workflow Gate 约束：

```text
Submission / Authority
```

而不是：

```text
Editor Access
```

因此任何 Chapter Draft Workspace 都允许用户打开和编辑。

只是非 Current Workflow Target 没有 Submit Eligibility。

---

# 118. 明确不支持 Submission Queue

本轮曾短暂推导：

```text
Per-Project FIFO Submission Queue
Cross-Chapter Candidate Queue
Queue Risk Detection
Force Continue
Failed Queue Head
```

全部废弃。

原因：

1. Chapter Loop 本身要求严格顺序；
2. 下一章必须基于上一章 Handoff；
3. 允许提前排队意味着提前赋予 Workflow Eligibility；
4. 会产生 Baseline Rebase；
5. 会产生连环 Dependency Review；
6. 会浪费 Token；
7. 会引入不必要的调度模型。

最终规则：

> **每个 Project 同一时间最多只有一个 Active Authority Submission。**

不存在第二个排队槽位。

---

# 119. Project Submission Lock

项目 Authority Submission 状态：

```text
IDLE
↓ Submit eligible Draft
ACTIVE AUTHORITY SUBMISSION
↓
Review / Validation / Acceptance / Dependency handling
↓
IDLE
```

当项目已经存在 Active Authority Submission：

> **任何其他正式 Authority Submission 都不可启动。**

包括：

- 同章其他 Draft；
- 前置章节 Revision；
- 后续章节 Revision；
- 新 Chapter 正式推进。

如果用户想换另一份同章草稿：

```text
Cancel current submission
↓
Return to eligible state
↓
Submit another Draft
```

不做 Candidate Queue。

---

# 120. Historical Revision 与 Revision Barrier

## 120.1 Revision Draft 在 Submit 前仍然只是 Draft

已有：

```text
Ch.06 Manuscript Rev2
```

用户创建并编辑：

```text
Ch.06 Revision Draft
```

此时：

- Rev2 仍是 Current Manuscript；
- 项目 Authority 不变；
- 下游正文不自动 stale；
- 不产生 Revision Barrier。

---

## 120.2 Revision Barrier 从 Submit 开始

只有：

```text
Submit Ch.06 Revision Draft
```

才进入：

```text
PROJECT REVISION BARRIER @ Ch.06
```

---

## 120.3 Revision Barrier 是 Project-level

Arc Boundary 不是 Dependency Boundary。

因此 Barrier 作用域为整个 Project。

如果 Ch.06 正在 Revision Submission：

```text
Submit Ch.03 Revision  ✗
Submit Ch.08 Revision  ✗
Submit Ch.19 Revision  ✗
Advance Ch.21 Workflow ✗
```

前置章节也不允许另行 Submit。

理由：

> 如果允许前置章节在当前 Review 过程中改变，会使当前 Baseline 失效，并重新引入 Rebase / Queue / 连环依赖检查。

---

# 121. Downstream Authority Lock

## 121.1 Conservative Barrier

若被修改正文已经被下游内容依赖：

在新的正文 Revision 与 Dependency Impact Analysis 完成之前，所有潜在下游 Authority Operation 先保守冻结。

Draft Workspace 不冻结。

---

## 121.2 Dependency Impact Analysis

新 Revision 成功 Materialize 后执行：

```text
Old Rev → New Rev Semantic Diff
+
Narrative Dependency Graph
+
Narrative State
↓
Affected Downstream Set
```

判断：

- unaffected；
- needs revalidation；
- stale；
- needs revision；
- transitively affected。

---

## 121.3 Dependency-aware Barrier

Impact Analysis 结束后：

```text
Unaffected
→ Unlock

Needs Revalidation
→ Keep controlled until revalidated

Needs Revision
→ Keep barrier / run revision

Transitively Affected
→ Keep barrier until upstream is clean
```

`Dependency Check Complete` 不等于 `Unlock Everything`。

---

## 121.4 Barrier 解除

必须直到：

```text
Revision Accepted
↓
New Manuscript Revision
↓
Dependency Impact Analysis
↓
Affected Revalidation / Revision
↓
Clean Trustworthy Authority Frontier
↓
Release Barrier
```

才能重新进入正常 Chapter Loop。

---

# 122. Revalidation

若某下游正文因上游变化进入：

```text
Needs Revalidation
```

但重新 Review 证明：

- 原文本仍然成立；
- 无需修改；
- 与新 Baseline 兼容；

则：

```text
refresh Validation Record
```

而不是制造内容完全相同的新 Manuscript Revision。

因此：

```text
Acceptance History
≠
Current Validation State
```

保持既有冻结规则。

---

# 123. Review FAIL / Cancel 语义

## 123.1 Review Result 永久绑定 Candidate

Review FAIL 只产生：

```text
Candidate
├─ submitted snapshot
├─ review result
├─ diagnostics
├─ requested changes
└─ status
```

Review 不自动修改 Draft Workspace。

---

## 123.2 FAIL 后用户自由选择下一步

用户可以：

- 继续修改当前 Draft；
- 换另一份 Draft；
- 从失败 Candidate 复制一个新 Draft；
- 让 Writer Agent 结合 Candidate + Review Result + Current Draft 修订。

系统不创建强制的“Revision Draft”。

---

## 123.3 Retry = New Submit

下一次无论选择同一物理 Draft File 还是另一文件：

```text
Draft
↓ Submit again
New immutable Candidate
↓
New Review
```

旧 Candidate 不可被修改后“继续审”。

---

## 123.4 Review Diagnostic 与当前 Draft

Diagnostic 绑定 Candidate。

当前 Draft 已发生变化时，UI 只做 best-effort 映射。

若无法可靠映射：

> 显示“此诊断针对旧 Candidate”。

不得伪造精确定位。

---

## 123.5 Failed / Cancelled Attempt History

当前冻结倾向：

- Failed / Cancelled Review Attempt 默认保留为 Project History；
- 不属于 Narrative Authority；
- Accepted Candidate / Manuscript Provenance 必须长期保留；
- Failed / Cancelled 历史允许用户显式清理。

具体保留周期 / 存储策略进入 Technical Architecture。

---

# 124. 外部直接修改正文

## 124.1 正文逻辑不可变，不要求文件系统物理只读

用户可以通过：

- Typora；
- Word；
- VS Code；
- 脚本；
- Git checkout；
- 其他 Agent；

直接改变项目目录内的正文物化文件。

系统不通过 OS 权限禁止。

---

## 124.2 Manuscript Materialization Dirty

若：

```text
Accepted Manuscript Rev digest
≠
Materialized manuscript file digest
```

则：

```text
MANUSCRIPT MATERIALIZATION DIRTY
→ RECONCILE REQUIRED
```

外部文件变化不会自动变成新的 Manuscript Authority。

---

## 124.3 Reconcile

系统必须允许用户：

- Compare；
- 恢复 Current Accepted Manuscript；
- 自行将外部修改内容保存/整理到 Draft Workspace。

不提供“直接接受外部文件为正文”。

也不新增专用 `Merge into Draft Wizard`；用户可自行处理，或使用 Writer Agent 协助。

---

## 124.4 Dirty 时暂停 Authority 操作

当 Manuscript Materialization 与 Authority Record 不一致时：

```text
Authority Operations → BLOCKED
Draft Editing → ALLOWED
```

先 Reconcile，再继续正式 Workflow。

---

# 125. Editor 保存 / Search / Statistics / Spellcheck

## 125.1 Autosave

默认采用 IDE 风格：

```text
Autosave
→ persist Draft file
```

Autosave：

- 不产生 Manuscript Revision；
- 不产生 Narrative Change；
- 不产生 Review Candidate；
- 不等于 VCS Commit。

具体 flush / debounce 策略进入技术阶段。

---

## 125.2 不自动创建空 Draft

Current Workflow Frontier 抵达某 Chapter 时，如果 Draft Workspace 为空：

> 不自动制造 `draft.md`。

用户第一次创建文件或 Writer Agent 第一次写作时再创建。

---

## 125.3 Search / Replace

Editor 基础能力包括：

- Current File Search；
- Current File Replace；
- Project-wide Search；
- Project-wide Replace；
- Regex；
- Scope / Filter。

这些是基础 Editor 能力，不依赖 Agent。

---

## 125.4 Writing Statistics

原生提供至少：

- Selection；
- Draft File；
- Chapter Manuscript；
- Current Manuscript；

的字数 / 字符数等基础统计。

具体统计口径与展示方式留 UI / Technical Design。

---

## 125.5 Spellcheck

提供 lightweight spellcheck。

要求：

- 可关闭；
- 不把剧情、风格、角色一致性等 AI 判断做成持续自动 lint；
- 深层检查继续交给 Agent / Review。

---

# 126. VCS / Git

## 126.1 默认无 Git 版本管理

Writing Project 默认：

```text
VCS = optional
```

提供 VCS 入口，首要支持 Git。

用户可以：

- 初始化 Project Root 为 Git Repository；
- 使用已有 Git Repository；
- 查看 diff / history / commit / branch 等熟悉流程。

具体 UI 后续参考 JetBrains 系列 IDE。

---

## 126.2 Git 是 Project-level

Git repository 作用域是 Project Root。

所有具有长期项目语义的持久项目内容都可以被用户纳入 Git。

原则：

```text
Narrative Authority
≠
Version-control Worthiness
```

Draft 即使没有 Narrative Authority，也完全可以被 Git 追踪。

---

## 126.3 Workflow 不自动操作 Git

Writing Workflow 默认不得：

- git add；
- git commit；
- git checkout；
- git branch；
- git tag；
- 自动 push。

Acceptance / Materialization / Final Acceptance 不自动映射为 Git operation。

---

## 126.4 用户可显式命令 Main Agent 操作 Git

例如：

```text
User:
“把当前变更 commit 一下”
↓
Main Agent
↓
Git operation
```

这属于普通显式 Agent Task。

仍然受 Agent Permission / Tool Permission / User Oversight 约束。

---

## 126.5 Git 与 Narrative Authority 正交

必须保持：

```text
Git Commit
≠
Workflow Acceptance

Git Branch
≠
Narrative Branch

Git HEAD
≠
Current Manuscript Authority
```

Git checkout 等操作若改变项目文件，则走既有 External Mutation / Reconcile。

---

# 127. Project Directory 与 VCS Tracking Boundary

## 127.1 Project Directory 是项目全部持久状态的物理边界

运行时数据也与 Project 绑定，因此仍放在对应 Project Directory 内。

概念示意：

```text
Project/
├─ manuscript/
├─ drafts/
├─ narrative/
├─ canon/
├─ outlines/
├─ auxiliary/
├─ raw/
├─ reviews/
├─ project settings / custom content
├─ agent extensions
│
├─ .runtime/
├─ .cache/
├─ .locks/
└─ ...
```

实际目录名进入 Technical Architecture。

---

## 127.2 默认进入 VCS 的内容

默认可追踪范围应覆盖：

### 所有可能的依赖项

例如：

- Manuscript；
- Canon；
- Narrative Objects；
- Narrative State；
- Chapter Contracts；
- Arc / Master Outline；
- Narrative Obligations；
- Structured Project Data；
- Auxiliary；
- Registry / Dependency durable state。

### 全部 Workspace Content

例如：

- Chapter Drafts；
- Notes；
- Alternate Drafts；
- User-created project files。

### Project-level Settings

仅限真正属于项目语义、应随项目迁移的配置。

### Project-level Custom Content

例如：

- Custom Kind；
- Custom Specialist；
- Project Agent Instructions；
- Project Skills / Plugins；
- Project Templates；
- Custom Rules / Schemas。

---

## 127.3 默认不进入 VCS 的 Project Runtime

例如：

- runtime task state；
- caches；
- locks；
- in-progress execution；
- temporary tool outputs；
- rebuildable indexes；
- local recovery state；
- Local History store。

它们：

```text
Project-scoped
but
default VCS tracking = false
```

通常由默认 `.gitignore` 排除。

用户仍然可以自行修改 Git ignore 规则。

---

# 128. Application-level Settings 与 Project-level Settings

## 128.1 Provider 全部是 Application-level

正式修正：

> **Provider 相关配置全部属于应用级配置，不属于 Project。**

包括：

- Provider；
- API credentials；
- API key / token；
- 模型列表；
- provider-specific runtime config；
- provider routing；
- machine-specific provider setup。

它们：

- 不跟 Project 走；
- 不进入 Project Git；
- clone Project 不会恢复 Provider Environment。

---

## 128.2 Project 可保存 Provider-independent Agent 语义

Project 可以保存：

- Agent Role Instructions；
- Project Agent Instructions；
- Skills / Plugin 声明；
- Custom Specialist；
- Workflow-specific project rules；
- Writing conventions。

但不保存具体 Provider Secret。

---

## 128.3 Window / Tool Window Layout

以下属于 Application / local workspace state：

- Tool Window placement；
- Docking；
- Split layout；
- Window geometry；
- Open tabs；
- Panel visibility。

应本地持久化，让用户下次启动无需重新调整。

但：

```text
not project semantic state
not VCS tracked
```

---

# 129. Local History

## 129.1 Local History 是正式产品能力

用户希望提供类似 JetBrains Local History 的能力。

它：

- 独立于 Git；
- 不要求用户 commit；
- 用于恢复误编辑 / Agent 大规模改动 / 外部修改；
- 应支持 Compare / Restore；
- 可支持恢复已删除文件。

---

## 129.2 Local History ≠ Narrative Version

Local History：

```text
Recovery / File History
```

不等于：

```text
Manuscript Revision
Workflow Acceptance
Narrative Change
Git Commit
```

---

## 129.3 技术细节未锁

以下留 Technical Architecture：

- full snapshot vs delta；
- content-addressed store；
- retention；
- storage quota；
- cleanup；
- performance；
- corruption recovery。

---

## 129.4 仍待最终确认的 Local History 产品细节

当前尚未单独确认：

1. Local History 是否覆盖所有 Project File Surface，还是只覆盖用户可编辑文件；
2. Writer / Agent 大批量修改前是否自动创建 `Before Agent Task` label；
3. 默认开启 / 可关闭的最终产品默认值；
4. 保留周期的产品级可配置范围。

这些属于小范围 Editor / Recovery 决策，不是 Root Architecture blocker。

---

# 130. Project Views / Navigation

## 130.1 文件视图与叙事/工作流视图分离

产品必须同时提供两个一等导航视图：

### Physical Project / File View

用于：

- Project File Tree；
- Draft Files；
- Raw；
- Project Settings；
- Custom Files；
- VCS；
- Runtime visibility（如有）。

### Narrative / Workflow View

用于：

- Story；
- Arc；
- Chapter；
- Scene；
- Character；
- Canon；
- Obligation；
- Review；
- Current Workflow Frontier；
- Workflow State。

不强迫其中一个承担另一套结构。

---

## 130.2 Tool Window 可重排

整体交互参考 JetBrains Tool Window 思路：

- 允许放置到不同边；
- 允许切换位置；
- 可隐藏 / 打开；
- 具体布局留 UI Design。

---

# 131. Writer Agent 编辑 Draft

## 131.1 Writer Product Scope

Writer Agent 正式写权限限定于 Draft Workspace 等被授权的非 Authority 工作区。

Writer 可以：

- create Draft；
- edit Draft；
- rewrite Draft；
- revise Draft；
- rename / move Draft（在角色能力允许范围内）。

Writer 不可直接：

- 修改 Manuscript Authority；
- 修改正式 Canon；
- 修改 Narrative State；
- 修改 Dependency Graph；
- Materialize Manuscript；
- 绕过 Review / Acceptance。

---

## 131.2 Permission Mode 不扩大 Product Scope

必须区分：

```text
Layer 1 — Product Capability Scope
Layer 2 — Agent Permission Mode
```

Manual / Accept Edits / Plan / Auto / Bypass 等只决定：

> 在已经允许的 Product Capability 内，需要多少用户确认。

因此：

> **Bypass Permissions ≠ Bypass Role ≠ Bypass Workflow ≠ Bypass Authority。**

---

## 131.3 Writer 的修改是否直接落盘

遵循统一 Agent Permission Mode。

例如：

```text
Manual
→ show / ask before edit

Accept Edits
→ allowed Draft edits may apply directly

Plan
→ no file mutation

Auto / Bypass
→ more autonomous within allowed Draft scope
```

不为 Writing 再造一套强制 Proposal / Diff 权限系统。

---

# 132. Selection 与 Editor Context

## 132.1 Selection 是 Context Signal，不是 Write Boundary

用户在 Editor 选中文字后调用 Agent：

```text
Selected Text
→ attach to Agent Context
```

Selection 不定义：

- hard write scope；
- permission boundary；
- Narrative Object；
- file lock。

Agent 可根据任务自行判断修改范围，但仍受 Role Scope 与 Permission Mode 约束。

---

## 132.2 Editor 自动提供轻量位置上下文

自动提供：

- Current Chapter identity；
- Current Draft File identity；
- Selection（若有）；
- Cursor / editor location 等轻量状态。

---

## 132.3 正式项目资料按需检索

不因为打开 Editor 就自动把以下全部塞进 Context：

- Chapter Contract；
- Canon；
- Character；
- Narrative State；
- Manuscript；
- Handoff；
- Dependency；
- 其他 Draft。

Agent 根据任务按需 Retrieval。

原则：

> **Editor 自动告诉 Agent“用户现在在哪里”；Agent 自己判断“为了完成任务还需要知道什么”。**

---

## 132.4 Manual Add to Context

产品倾向保留用户手动添加 Agent Context 的能力。

可优先支持轻量对象 / 文件级 pin：

- Draft File；
- Manuscript Chapter；
- Raw File；
- Auxiliary；
- Character；
- Canon Object；
- Chapter Contract；
- Selection。

具体第一版范围与实现难度仍需 Technical Architecture 评估。

不要求第一版实现复杂 dynamic context graph。

---

# 133. 同一 Draft File 并发写

本轮默认采用较保守的 Editor 写入模型：

```text
不同 Draft File
→ Agent / User 可并行工作

同一物理 Draft File
→ 同一时间只允许一个主动 Agent write task
```

用户自己的编辑与 Agent 修改仍走既有 Local vs Disk / Read Set / Write Set / Replan / Conflict 规则。

目的不是限制创作并发，而是避免同一文件发生无意义的自动写冲突。

若最终技术实现能安全提供更强协作编辑能力，可在 Technical Architecture 阶段重新评估，但不得破坏 Draft Isolation / User Oversight。

---

# 134. VCS / Local History / Save 三者正交

必须保持：

```text
Save / Autosave
≠
Local History Revision
≠
Git Commit
≠
Manuscript Revision
```

它们分别解决：

- Save：文件持久化；
- Local History：本地恢复；
- Git：用户选择的项目版本管理；
- Manuscript Revision：Writing Authority。

不得互相冒充。

---

# 135. Agent Extensions

## 135.1 不做通用 Application Plugin Platform

当前 Writing Product Requirements 不承诺 JetBrains 式通用 UI / App 插件平台。

---

## 135.2 Agent Skills 是正式能力

支持 Agent Skills。

可存在：

```text
Application-level Skills
Project-level Skills
```

Project-level Skills 属于 Project Custom Content，可进入 Git。

Skill 可以包含：

- Instructions；
- references；
- templates；
- scripts；
- resources。

具体格式与兼容标准进入 Technical Architecture。

---

## 135.3 Agent Plugins 是正式能力

支持更高层 Agent Plugin Packaging。

可以组合：

- Skills；
- Custom Agents / Specialists；
- Hooks；
- Agent Resources；
- Project Templates；
- Agent Extension Config。

不等同于 Application UI Plugin。

---

## 135.4 MCP 纳入正式 Agent Extension / Tool 能力

原“暂不考虑 MCP”被覆盖。

新规则：

> **既然产品已经有统一 Product Scope + Agent Permission + Tool Permission，那么 MCP 可以作为外部 Tool Provider 纳入。**

MCP Server 暴露的工具不能自行扩大 Agent 权限。

Effective Capability 可抽象为：

```text
Role Capability
∩
Agent Permission
∩
Tool / MCP Permission
```

---

## 135.5 MCP / Plugin / Skill 不得越权

无论 Skill / Plugin / MCP 自己声明多强的能力：

都不能突破：

- Agent Role Capability；
- Product Authority Boundary；
- Project Scope；
- User Permission Mode；
- Tool Security Policy。

例如 Writer 即使获得 filesystem MCP，也不能因此直接写 Manuscript Authority。

---

# 136. Project-level Agent Instructions

## 136.1 项目级 Agent Instructions 文件

项目应支持类似：

```text
AGENTS.md
CLAUDE.md
```

这种项目级 Agent 说明文件。

当前推荐以：

```text
AGENTS.md
```

作为 canonical 项目级说明入口，并可在技术阶段考虑兼容其他成熟 Agent instruction filename。

---

## 136.2 Project Agent Instructions 内容

可以用于：

- 项目整体写作原则；
- Agent 行为约定；
- 文件规则；
- 项目术语；
- 用户项目级写作偏好；
- 禁止事项；
- Skills 使用说明；
- 项目 Workflow 补充说明。

它属于：

```text
Project Content
Project Custom Content
VCS-trackable
```

---

## 136.3 Instructions 不能覆盖硬规则

项目说明文件不能覆盖：

- Authority Boundary；
- Role Capability；
- Workflow Gate；
- Product Security；
- Permission System。

它只是 Instructions，不是 Root Policy。

---

# 137. Export 语义

## 137.1 Manuscript Export 默认导出 Authority World

此前已经允许任意阶段：

- Export Manuscript；
- Export Current Chapter；
- Export Selected Chapters；
- Export Outline；
- Export Canon / Story Bible；
- Export Review Report。

新增澄清：

> **Export Manuscript / Export Chapter 默认指 Authority World 中的 Current Manuscript Text。**

存在未提交 Draft 时：

```text
Export Chapter
→ Accepted Current Manuscript
```

而不是 Editor 当前 Draft。

Draft 若要导出：

```text
Export Draft / Export File
```

显式执行。

---

## 137.2 第一阶段 Export 格式

产品层当前目标至少：

```text
Markdown
TXT
DOCX
```

Final Package 继续：

```text
ZIP + Integrity Manifest
```

具体格式能力、样式模板与 DOCX rendering 进入 Technical Architecture / Export Design。

---

## 137.3 不维护实时整本 manuscript.md

Current Manuscript 逻辑上是：

```text
ordered set of Current Chapter Manuscript Revisions
```

不要求实时维护一个巨大的：

```text
manuscript.md
```

派生副本。

整本单文件在需要时：

```text
Assemble / Export
```

动态生成。

---

## 137.4 每 Chapter 一个 Current Manuscript Materialization

项目正文物理层：

> **一个 Chapter 对应一个 Current Manuscript File / Materialization。**

历史 Manuscript Revision 由正式 Revision / Artifact History 保存，不要求在当前正文目录暴露：

```text
rev1.md
rev2.md
rev3.md
```

多个并列历史文件。

---

# 138. Final Package 与 Project Archive

## 138.1 Final Package

Final Package 是作品正式交付物。

默认只包含：

- Accepted Manuscript；
- Integrity Manifest；
- 用户选择的 Story Bible / Canon；
- 用户选择的 Review / Attestation 等正式资产。

不默认打包：

- Draft Workspace；
- Raw；
- Runtime；
- Cache；
- Local History。

---

## 138.2 Project Archive / Pack Project

单独提供：

```text
Archive Project / Pack Project
```

用于完整工作项目：

- 备份；
- 迁移；
- 搬到另一台机器；
- 保留长期 Project State；
- 可选择是否携带 runtime/recovery data。

它与 Final Package 完全不同：

```text
Final Package
= 作品交付

Project Archive
= 工作环境 / 项目迁移
```

---

# 139. Workflow 信息在 Editor 中始终可访问

打开 Chapter / Draft 时，必须能够随时访问：

- Current Workflow Frontier；
- Chapter Contract；
- Scene Plan；
- Current Manuscript；
- Review Result；
- Validation State；
- relevant workflow state。

具体展示在哪里：

```text
留 UI Design
```

产品层只锁：

> **可访问。**

---

# 140. UI 布局与 JetBrains 参考

UI / Workbench 总体倾向参考 JetBrains 系列 IDE：

- Tool Window；
- File Tree；
- Narrative View；
- Version Control View；
- Local History / Diff；
- movable panels；
- dock / hide / reposition。

但具体视觉和布局不在 Product Requirements 阶段锁死。

窗口布局属于 Application-level local state，不进入 Git。

---

# 141. Local / Disk Conflict 继续沿用旧规则

Part A–D 已确立：

如果：

```text
App Local Unsaved Changes
+
Disk External Changes
```

进入：

```text
FILE CONFLICT
```

必须提供至少：

- Diff；
- Use Local；
- Use Disk；
- Merge。

该一般文件冲突机制继续有效。

但针对 Manuscript Authority File：

即使使用 Disk 内容，也不能自动绕过 Writing Workflow 成为新正文。

---

# 142. Product Capability 与 Agent Tool Permission

本轮进一步明确：

> **权限模式决定 Agent 如何执行已经被允许的能力，不决定 Agent 本身拥有什么业务能力。**

因此技术阶段需要建立清晰：

```text
Role Capability Matrix
+
Tool Permission Matrix
+
Agent Permission Mode
+
Extension Permission / MCP Permission
```

最终 Effective Capability 必须是交集，不是并集。

---

# 143. 本轮明确废止 / 覆盖的旧或临时方案

以下不得继续实现：

## 143.1 Submission Queue

废止：

- FIFO Authority Queue；
- Queued Candidate；
- Cross-Chapter Review Queue；
- Queue reorder；
- Force Continue Queue；
- Queue dependency probability；
- failed queue head replacement。

---

## 143.2 Draft Composition

废止：

- Chapter Draft Composition；
- multi-file submission manifest；
- source ordering object。

正式提交只接受：

```text
one file snapshot
```

---

## 143.3 Editor Direct Manuscript Editing

废止任何：

```text
save manuscript directly
edit accepted manuscript in place
```

正文只能由 Workflow Materialize。

---

## 143.4 Selection Hard Write Scope

废止：

```text
selected text = hard Agent write boundary
```

Selection 只是 Context Signal。

---

## 143.5 Workflow 自动 Git

废止：

```text
Accept → git commit
Materialize → git add
Final Accept → git tag
```

Workflow 与 VCS 正交。

---

## 143.6 Project-level Provider Config

废止把：

- Provider；
- Model Routing；
- API Config；
- Credentials；

放进 Project / Git 的设计。

Provider 统一 Application-level。

---

# 144. v0.5 Freeze Checklist

## Draft / Manuscript

- [x] Draft 与 Manuscript 是不同实体。
- [x] 只有完整 Workflow 通过后才称正文。
- [x] Draft 不被动影响 Narrative Authority。
- [x] Manuscript Revision 不原地修改。
- [x] Chapter 是最小正文构建单元。
- [x] Scene 不独立正文化。

## Draft Workspace

- [x] 每 Chapter 有独立 Draft Workspace。
- [x] 每章允许任意数量 Draft File。
- [x] Draft 必须归属 Chapter；无归属内容为 Raw。
- [x] 不定义 Draft Composition。
- [x] One Submission = One Draft File Snapshot。
- [x] Draft File 不要求声明“完整章节”类型。
- [x] Scene 与 Draft File 不强绑定。
- [x] Draft 跨 Chapter 移动不产生 Authority Change。

## Editor

- [x] 打开 Chapter 默认进入 Draft Workspace。
- [x] 已有正文同时作为只读 reference 可访问。
- [x] “基于正文修改”只是复制正文到普通 Draft。
- [x] 非 Frontier Chapter Draft 仍可自由编辑。
- [x] Autosave 不产生 Authority。
- [x] 不自动制造空 Draft。
- [x] Project Search / Replace 属于基础能力。
- [x] Writing Statistics 属于基础能力。
- [x] Spellcheck 可提供且可关闭。

## Formats

- [x] TXT 支持。
- [x] Markdown 支持。
- [x] DOCX 支持。
- [x] DOCX 是可编辑 / 可提交一等 Draft Format。
- [x] DOCX Candidate 保留原 Artifact，可另生成 normalized review representation。
- [ ] 高级 DOCX round-trip fidelity 范围待 Technical Architecture。

## Workflow / Review

- [x] Single Workflow Frontier。
- [x] Future Draft 可以存在但不可提前 Submit。
- [x] 下一 Chapter 必须基于上一 Chapter Handoff。
- [x] 每 Project 同时最多一个 Active Authority Submission。
- [x] No Submission Queue。
- [x] Candidate 与源 Draft 解耦。
- [x] FAIL 不自动修改 Draft。
- [x] Retry 产生 New Candidate。
- [x] Diagnostic 绑定 Candidate。

## Revision Barrier

- [x] Historical Revision Draft 在 Submit 前仍只是 Draft。
- [x] Submit 后建立 Project-level Revision Barrier。
- [x] Barrier 期间前置 / 后置正式提交都禁止。
- [x] 被依赖正文修改时保守冻结潜在下游 Authority。
- [x] Dependency Impact 后精炼 Affected Set。
- [x] Unaffected 解锁。
- [x] Needs Revalidation 可仅刷新 Validation Record。
- [x] Affected / Transitive 继续阻塞直到收敛。

## External Mutation

- [x] 正文物理文件可被外部工具修改。
- [x] 外部修改不自动变成 Authority。
- [x] Dirty Manuscript 要 Reconcile。
- [x] Dirty 时暂停其他 Authority 操作。
- [x] 不提供直接接受外部正文修改的入口。
- [x] 不额外设计专用 Merge-into-Draft Wizard。

## VCS

- [x] VCS 默认可选。
- [x] Git 是 Project-level。
- [x] Project 内长期语义内容均可追踪。
- [x] Runtime 默认 ignore。
- [x] Workflow 不自动 Git。
- [x] 用户可显式命令 Main Agent 操作 Git。
- [x] Git 与 Narrative Authority 正交。

## Settings

- [x] Provider 全部 Application-level。
- [x] Provider / credentials 不进入 Project Git。
- [x] Tool Window / Layout 是本地 Application state。
- [x] Layout 本地持久保存。

## Local History

- [x] Local History 是希望保留的正式 Editor 能力。
- [x] 独立于 Git / Manuscript Revision。
- [ ] 覆盖范围待最终确认。
- [ ] Agent task 前自动 label 待最终确认。
- [ ] 默认开关与 retention UX 待最终确认。
- [ ] 存储实现进入 Technical Architecture。

## Agent / Context

- [x] Writer Agent 写权限限定 Draft Workspace。
- [x] Permission Mode 不扩大 Product Scope。
- [x] Selection 是 Context Signal，不是 hard write boundary。
- [x] Editor 自动提供轻量位置上下文。
- [x] Contract / Canon / Manuscript 等继续按需 Retrieval。
- [~] Manual Add to Context 保留为产品候选，v1 实现范围待技术评估。
- [x] 不同 Draft File 可并行。
- [x] 同一物理 Draft File 默认避免并行 Agent write task。

## Extensions

- [x] 不承诺通用 Application UI Plugin Platform。
- [x] Agent Skills 是正式能力。
- [x] Agent Plugins 是正式能力。
- [x] MCP 纳入正式 Tool / Extension 能力。
- [x] Skill / Plugin / MCP 不得扩大 Agent Authority。
- [x] 支持 Application-level / Project-level Agent Extensions。
- [x] 支持 Project-level Agent Instructions 文件。
- [x] 推荐 AGENTS.md 作为 canonical project instruction entry。

## Export / Archive

- [x] Export Manuscript / Chapter 默认导出 Authority World。
- [x] Draft 导出显式执行。
- [x] 至少支持 MD / TXT / DOCX export。
- [x] Final Package 继续 ZIP + Manifest。
- [x] 不实时维护整本 manuscript.md。
- [x] 一个 Chapter materialize 一个 Current Manuscript File。
- [x] Final Package 与 Project Archive 分离。
- [x] Project Archive / Pack Project 是独立产品能力。

---

# 145. v0.5 Remaining Product Questions

本轮结束后，真正尚未确认的产品层问题已很少。

## 145.1 DOCX Fidelity Boundary

需要最终决定：

> 第一版对复杂 Word 格式承诺到什么程度？

可选方向：

- common prose formatting best-effort；
- high-fidelity Word round-trip；
- 分级支持。

该问题会显著影响 Editor Technology Selection。

---

## 145.2 Local History Scope

需要决定：

- 所有 Project Files；
- 仅用户可编辑文件；
- 是否包含 machine-managed structured project files；
- 是否包含 Manuscript materialization；
- 是否排除 runtime/cache（当前倾向排除）。

---

## 145.3 Agent Task Local History Label

需要决定：

> Writer / Main Agent 在大批量文件修改前，是否自动创建 Local History Label / Recovery Point？

这不是 Authority Gate，只是 recovery UX。

---

## 145.4 Local History Default / Retention UX

需要决定：

- default on/off；
- configurable retention；
- storage budget；
- user cleanup。

底层存储实现属于 Technical Architecture。

---

## 145.5 Manual Add to Context v1 Scope

产品能力倾向保留，但需要确认：

- 是否 v1 必做；
- 支持哪些对象类型；
- 只是 retrieval pin 还是内容快照；
- UI 入口。

---

# 146. Technical Architecture 阶段新增重点

结合 Part D 原待办，本轮额外增加：

## Editor / Document

- TXT / MD / DOCX Editor Technology；
- DOCX OOXML / document-model layer；
- DOCX round-trip fidelity；
- Candidate normalization；
- Autosave；
- same-file write coordination；
- Search / Replace；
- word statistics；
- spellcheck。

## Authority / Files

- Manuscript Materialization Store；
- Manuscript digest；
- Dirty / Reconcile；
- immutable Candidate Artifact；
- Manuscript Revision history；
- atomic Authority Commit；
- Revision Barrier representation。

## VCS / History

- Git integration boundary；
- default `.gitignore`；
- Local History store；
- Local History retention；
- restore deleted file；
- Agent task labels。

## Agent Extensions

- Skill format；
- Plugin packaging；
- MCP client；
- Tool permission intersection；
- Application vs Project extension scope；
- AGENTS.md / compatible instruction-file precedence；
- project extension security。

## UI

- File View；
- Narrative / Workflow View；
- movable Tool Windows；
- Manuscript read-only reference；
- Draft Workspace；
- Review Candidate / Diagnostic view；
- Dirty Manuscript Reconcile；
- VCS Tool Window；
- Local History UI；
- DOCX editor UX。

---

# 147. 外部成熟实践复核（2026-08-13）

本轮通过公开资料再次确认以下成熟实践可作为后续实现参考，但它们不是本项目产品规则的来源：

- `AGENTS.md` 已存在公开、可预测的 Agent 项目说明约定；
- MCP 官方规范提供 Tools、Authorization 与安全最佳实践，可作为外部 Tool Provider；
- DOCX / WordprocessingML 是基于 Office Open XML parts 的结构化文档格式，可通过确定性文档工具层操作；
- JetBrains Local History 独立于 VCS，自动记录项目 meaningful changes，可作为本项目 Local History UX 的参考；
- JetBrains VCS 以 Project / Directory Mapping 为边界，可作为项目级 Git UX 参考。

本项目只借鉴成熟交互与边界，不自动继承其全部实现。

---

# 148. v0.5 Freeze Candidate 结论

截至本 Checkpoint：

```text
Core Writing Workflow
→ CLOSED

Draft / Manuscript Authority Model
→ CLOSED

Chapter Draft Workspace
→ CLOSED

Single Workflow Frontier / No Submission Queue
→ CLOSED

Historical Revision / Revision Barrier
→ CLOSED

Review Candidate / Retry / Failure Semantics
→ CLOSED

External Manuscript Mutation / Reconcile
→ CLOSED at Product Requirement level

VCS / Project Storage Boundary
→ CLOSED at Product Requirement level

Editor Navigation / Agent Editing / Context
→ CLOSED with minor implementation candidates

Agent Skills / Plugins / MCP / Project Instructions
→ CLOSED at Product Requirement level

Local History
→ REQUIRED, minor product defaults still open

DOCX
→ REQUIRED, fidelity boundary still open
```

当前建议状态：

```text
Writing Product Requirements
→ FINAL FREEZE CANDIDATE
```

下一会话优先执行：

```text
1. 阅读 Part E（本轮最高优先级）
2. 检查 Part E 与 Part A–D 的术语 / 状态机冲突
3. 只处理 145 节列出的少量未决项
4. 做最终遗漏检查
5. 若无 Root Design Conflict：
   Writing Product Requirements → FROZEN
6. 进入 Technical Architecture / Implementation Design
```

---

# 149. 下一会话交接提示

```text
当前最新自包含 Checkpoint：

Writing_Module_Requirements_Checkpoint_v0.5_Editor_Authority_Freeze_Candidate.md

阅读优先级：
Part E > Part D > Part C > Part B > Part A

Part E 新增 / 覆盖的关键决策：
- Draft 与 Manuscript 严格分离；
- Editor = Chapter Draft Workspace；
- Chapter 是最小正文 Build Unit；
- 每章任意 Draft File，但 Submit 只能选单个文件；
- TXT / MD / DOCX 是一等 Draft Format；
- Current Manuscript 是只读 Authority Reference；
- Future Draft 可以存在，但只有 Current Workflow Frontier 可 Submit；
- 明确废弃 Submission Queue；
- 每 Project 最多一个 Active Authority Submission；
- Historical Revision Submit 建立 Project-level Revision Barrier；
- 被依赖正文修改时阻塞潜在下游 Authority，直到 Revalidation / Revision 收敛；
- External Manuscript Edit 进入 Dirty / Reconcile，不直接改变 Authority；
- Review FAIL 不自动改 Draft，Retry 产生 New Candidate；
- Git 是 Project-level，可由用户显式命令 Main Agent 操作，但 Workflow 不自动 Git；
- Project Runtime 仍存 Project Directory，但默认不进 Git；
- Provider 全部 Application-level；
- Project 同时提供 File View 与 Narrative / Workflow View；
- Writer Agent 只能在 Product Scope 内改 Draft，Permission Mode 不扩大 Authority；
- Selection 只作为 Context Signal；
- Editor 自动提供 Current Chapter / Draft / Selection / Cursor，其他资料按需 Retrieval；
- Local History 正式需要，但少量默认策略待定；
- Agent Skills / Plugins / MCP 正式支持；
- Project-level Agent Instructions 正式支持，推荐 AGENTS.md；
- Final Package 与 Project Archive 分离。

不要重新打开已经 CLOSED 的 Submission Queue / Draft Composition / Direct Manuscript Editing 等方案。

剩余产品问题只看 Part E §145。
```

---

# 150. v0.5 一句话摘要

> **Writing 模块现已从“Workflow + Agent + 可编辑项目文件”的框架进一步收敛为一个以 Chapter Draft Workspace 作为自由 Source World、以单文件 immutable Candidate 作为正式审查输入、以 Chapter 为最小 Manuscript Build Unit、以 Single Workflow Frontier 与 Project-level Revision Barrier 保证 Authority 串行一致、以只读 Manuscript Revision 作为可依赖构建产物、同时提供 JetBrains 式文件/叙事双视图、可选 Git、独立 Local History、TXT/MD/DOCX 一等 Draft、Skills/Plugins/MCP 与 Project Agent Instructions 的完整重型写作 IDE；当前只剩 DOCX Fidelity、Local History 少量默认策略与 Manual Add-to-Context v1 范围需要最终封口。**
---

# Part F — v0.5.1 Final Freeze Patch（2026-08-13）

> **本 Part 是 Writing Product Requirements 的最终冻结补丁。**  
> 阅读优先级：**Part F > Part E > Part D > Part C > Part B > Part A**。  
> Part F 不重新打开已经 CLOSED 的产品方向，只消除 v0.5 最终审计发现的规范歧义与状态机漏边。

# 151. v0.5.1 Freeze Patch 范围

最终冲突与遗漏检查结论：

```text
New Root Design Conflict
→ NONE

Root Architecture Re-grill
→ NOT REQUIRED
```

因此本补丁只固化以下五项：

1. Historical Revision Submission Eligibility；
2. Revision Barrier 的 remediation / FAIL / Cancel 出口；
3. Reconstruction 中旧 `Editor Lock` 术语；
4. Project-wide Replace 的 writable scope；
5. Project Extension 的 trust / activation 不变量。

以下根设计保持不变：

- Draft / Manuscript 严格分离；
- Editor 只直接编辑非 Authority 工作区；
- Chapter 是最小 Manuscript Build Unit；
- Normal Chapter Loop 采用 Single Workflow Frontier；
- Authority World 串行；
- 每 Project 同时最多一个 Active Authority Submission；
- 不存在 Authority Submission Queue；
- Historical Revision 使用 Project-level Revision Barrier；
- Git / Local History / filesystem state 与 Narrative Authority 正交；
- Provider 全部 Application-level；
- Agent / Skill / Plugin / MCP 不能突破 Product Authority Boundary。

---

# 152. Submission Eligibility 正式拆分

v0.5 §117 中：

> “只有 Current Workflow Frontier 具有 Submission Eligibility”

正式限定为：

> **Normal Chapter Submission Eligibility。**

它不禁止 Historical Revision 使用独立的 Revision Submission Eligibility。

## 152.1 Normal Chapter Submission Eligibility

正常向前写作仍严格遵循：

```text
Current Workflow Frontier
→ only Normal Chapter Submission target
```

Future Chapter：

- Draft 可以提前存在；
- Draft 可以自由编辑；
- 不可提前进入正式 Review / Acceptance；
- 不具有 Normal Chapter Submission Eligibility。

该规则继续保证正常 Chapter Loop 单线向前推进。

## 152.2 Historical Revision Submission Eligibility

已经存在 Current Manuscript Revision 的 Historical Chapter 可以创建 Revision Draft。

当项目不存在阻止该操作的 Authority 状态时，Historical Chapter 可以获得：

```text
Historical Revision Submission Eligibility
```

它：

- 不要求该 Chapter 成为 Current Workflow Frontier；
- 不改变 Normal Workflow Frontier；
- 不创建第二条 Narrative Branch；
- 不进入 Submission Queue；
- 仍受 Project Submission Lock、Dirty / Reconcile、Revision Barrier 与其他既有 Authority Gate 约束。

因此：

```text
Normal Chapter Submit
→ governed by Current Workflow Frontier

Historical Revision Submit
→ governed by Historical Revision Eligibility
```

二者共享同一个 Project-level Authority serialization rule：

> **每个 Project 同时最多只有一个 Active Authority Submission。**

---

# 153. Revision Barrier 状态机补全

## 153.1 Revision Barrier 两阶段语义

Historical Revision Submission 建立的 Project Revision Barrier 正式区分为两个阶段。

### Phase A — Conservative Global Barrier

从 Historical Revision Candidate 正式 Submit 开始：

```text
Historical Revision Submit
↓
ACTIVE AUTHORITY SUBMISSION
+
PROJECT REVISION BARRIER
```

在 Candidate 尚未完成 Review / Acceptance / Materialization 之前：

```text
Other Authority Submission
→ BLOCKED

Draft Editing
→ ALLOWED
```

此时阻塞范围仍是整个 Project。

### Phase B — Dependency Resolution Barrier

若 Historical Revision 被 Accepted 并 Materialize 为新的 Manuscript Revision：

```text
New Manuscript Revision
↓
Dependency Impact Analysis
↓
Affected Downstream Set
```

随后 Barrier 从“未知影响的全局保守锁”进入：

```text
DEPENDENCY RESOLUTION BARRIER
```

此阶段：

- Unaffected 内容恢复正常 trustworthy / dependency-valid 状态；
- `Unaffected → Unlock` **不等于恢复无关的 Normal Authority Submission**；
- Normal Chapter Loop 仍暂停；
- 无关 Historical Revision 仍不可启动；
- 只允许为收敛当前 Barrier 所必需的 Revalidation / Remediation Authority Operation。

这样保留 Project-level Barrier，同时避免把 `Unaffected → Unlock` 误实现成恢复任意提交资格。

---

## 153.2 Barrier Remediation Submission 例外

v0.5 中“Barrier 期间前置 / 后置正式提交都禁止”正式补充以下限定：

> **禁止的是与当前 Barrier 无关的 Authority Submission；属于当前 Affected Set、用于消除该 Barrier 的 Remediation Submission 是允许的。**

当某个下游 Chapter 被判定为：

```text
Needs Revision
```

可以：

```text
Affected Chapter
↓
Create / Select Revision Draft
↓
Edit freely
↓
Barrier-controlled Remediation Submit
↓
Fresh Review
↓
Accept / Materialize
↓
Re-run affected Dependency / Revalidation
```

Remediation Submission：

- 不是 Submission Queue；
- 不预先排队多个 Candidate；
- 不允许并行 Authority mutation；
- 仍然一次只允许一个 Active Authority Submission；
- 只允许服务于当前 Barrier 的收敛；
- 完成后重新计算 / 更新仍未收敛的 Affected Set。

因此不存在：

```text
must revise to release barrier
+
revision submit blocked by barrier
```

这一自锁状态。

---

## 153.3 Revalidation 不强制制造 Manuscript Revision

既有规则继续有效：

```text
Needs Revalidation
↓
Fresh Review against new baseline
↓
still valid
↓
Refresh Validation Record
```

若文本无需修改：

- 不创建内容相同的新 Manuscript Revision；
- 只更新 Current Validation State；
- 该对象从 Affected Set 中收敛退出。

---

## 153.4 Triggering Revision FAIL / Cancel 的 Barrier 出口

必须区分“触发 Barrier 的初始 Historical Revision Submission”与“Barrier 已经由 Authority Change 建立后的 remediation attempt”。

### A. 初始 Historical Revision Candidate 在 Materialization 前 FAIL / Cancel

若：

```text
Old Manuscript Authority
→ unchanged
```

且本次 Submission 是当前 Barrier 的触发源，则：

```text
Active Submission ends
↓
No new Authority baseline exists
↓
Release this Revision Barrier
↓
Return to previous Authority state
```

Retry：

```text
Draft
↓
New Submit
↓
New immutable Candidate
↓
New Revision Barrier attempt
```

旧 Candidate / Review Result 继续保留其历史语义。

### B. Barrier 已因 Accepted Revision 进入 Dependency Resolution 后，Remediation Candidate FAIL / Cancel

此时上游 Authority 已经改变，因此：

```text
Remediation attempt ends
but
Revision Barrier remains
```

用户可以：

- 修改当前 Draft；
- 换另一份 Draft；
- 从失败 Candidate 创建新 Draft；
- 重新 Submit New Candidate。

直到 Affected Set 完全收敛才释放 Barrier。

---

## 153.5 Barrier 最终释放条件

最终保持：

```text
Accepted Upstream Revision
↓
Dependency Impact Analysis
↓
Affected Revalidation / Remediation
↓
Affected Set = EMPTY / CLEAN
↓
Clean Trustworthy Authority Frontier
↓
Release Project Revision Barrier
↓
Resume Normal Chapter Loop
```

若 Impact Analysis 直接得出：

```text
Affected Set = EMPTY
```

则可直接释放 Barrier。

---

# 154. Reconstruction Lock 术语正式覆盖

旧 Part 中出现的：

```text
Editor Lock
unlock Editor / Draft Agent
```

不再按字面解释为“禁止打开或编辑 Draft Workspace”。

正式替换语义为：

```text
RECONSTRUCTION AUTHORITY / PROGRESSION LOCK
```

## 154.1 Reconstruction 真正锁定的内容

在 Reconstruction Complete Gate 之前禁止：

- 把 Imported Existing Manuscript 当作可原地修改的 Authority；
- 对尚未完成重建的 Existing Content 做正式 Manuscript Mutation；
- 绕过 Reconstruction 直接 Accept Existing Content；
- 在未恢复 Current Workflow Frontier 前推进正式 Normal Chapter Authority；
- 让 Agent 对 Existing Manuscript 执行绕过 Workflow 的 rewrite / continuation。

## 154.2 Reconstruction 不锁 Draft 自由创作

用户仍可以：

- 创建 Chapter Draft；
- 编辑已有 Draft；
- 写未来章节 Draft；
- 保存实验文本；
- 使用 Raw Area；
- 在非 Authority Workspace 中自由写作。

但这些内容：

```text
remain Draft / Raw
→ no Authority
→ no Submission Eligibility unless corresponding workflow gate permits
```

因此统一为：

> **Reconstruction 锁 Authority Progression，不锁自由 Draft Editing。**

任何旧文本中的 `Editor Lock` 均以本节解释为准。

---

# 155. Project-wide Search / Replace 的 Writable Scope

## 155.1 Search 与 Replace 权限语义不同

正式明确：

```text
Search Scope
≠
Write Scope
```

Project-wide Search 可以在产品允许用户检索的 Project Surface 上工作。

Project-wide Replace 属于 mutation，因此：

> **只能作用于当前 Product Capability 与目标对象 Write Policy 允许修改的 Writable Surface。**

## 155.2 Replace 不提供隐式越权

Project-wide Replace 不能因为名字中存在 `Project-wide` 就：

- 修改 Manuscript Authority；
- 绕过 Draft / Manuscript Boundary；
- 修改 machine-managed Registry / Lock；
- 修改 runtime/cache/lock 等非用户内容；
- 绕过 Structured Project Object 的既有 validation / reconciliation 语义；
- 绕过任何 Authority / Workflow Gate。

对一个搜索结果而言：

```text
Search Match
+
Target Writable under current product rules
→ Replace eligible

Search Match
+
Target not writable
→ Read-only result / excluded from Replace
```

具体哪些文件类型与对象进入 Replace UI、批量替换预览和事务策略，进入 UI / Technical Architecture。

---

# 156. Project Extension Trust / Activation 不变量

Project-level Skills / Plugins / Agent Instructions / MCP configuration 继续属于正式能力。

但正式增加以下 Product Security Invariant：

> **Opening / Importing / Cloning / Unpacking a Project MUST NOT by itself execute project-provided executable content.**

这里的 executable content 至少包括：

- Skill scripts；
- Plugin hooks；
- Project-provided commands；
- executable templates / helpers；
- locally declared external tool bootstrap；
- MCP server launch command；
- 其他能够产生系统副作用的 Project Extension 内容。

## 156.1 Data / Instructions 与 Execution 分离

项目被打开时允许：

- 发现 Extension metadata；
- 读取安全的声明信息；
- 展示 Skills / Plugins / MCP availability；
- 读取 Project Agent Instructions；
- 标记 extension trust / activation state。

但：

```text
Project Present
≠
Project Extension Trusted
≠
Executable Permission Granted
≠
Execution
```

## 156.2 执行仍受权限交集

任何真正执行仍必须满足既有：

```text
Product Capability
∩
Role Capability
∩
Agent Permission Mode
∩
Tool / MCP Permission
∩
Extension Trust / Activation
∩
Tool Security Policy
```

具体 trust UI、首次启用确认、签名、allowlist、workspace trust、MCP lifecycle 等进入 Technical Architecture / Security Design。

本节只冻结不可违反的产品原则：

> **项目内容的存在本身不能成为代码执行授权。**

---

# 157. Part E §145 未决项的冻结处置

Part E §145 列出的少量问题继续存在，但正式分类为：

```text
NON-ROOT PRODUCT DEFAULT / V1 SCOPE DECISION
```

它们不再阻塞 Writing Product Requirements Freeze。

## 157.1 DOCX Fidelity Boundary

冻结不变量：

- TXT / MD / DOCX 都是一等 Draft Format；
- DOCX 可编辑、可提交；
- Candidate 保留原始 Artifact；
- Review 可使用 normalized representation；
- 不在 Product Requirements 层承诺任意复杂 Word 文档的无损 round-trip。

第一版具体采用：

- common prose formatting best-effort；
- 分级 fidelity；
- 或更高 fidelity；

由 Editor Technology Selection / Technical Architecture 决定。

## 157.2 Local History

冻结不变量：

- Local History 是正式产品能力；
- 独立于 Git；
- 独立于 Manuscript Revision / Narrative Authority；
- 支持 Compare / Restore；
- 应覆盖足够的用户编辑与 Agent 修改恢复场景。

以下转 Technical Architecture / UX Default：

- precise scope；
- default on/off；
- retention；
- storage budget；
- cleanup；
- Agent task recovery label。

## 157.3 Manual Add to Context

保留为：

```text
Product Candidate / v1 Scope Decision
```

不作为核心 Workflow、Authority、Editor 或 Agent Runtime 的冻结 blocker。

---

# 158. Final Root Conflict Verdict

最终检查确认：

```text
Draft / Manuscript Authority Model
→ CONSISTENT

Normal Frontier / Historical Revision Eligibility
→ CONSISTENT after §152 clarification

Project Submission Lock / Revision Barrier
→ CONSISTENT after §153 state-machine completion

Reconstruction / Draft Freedom
→ CONSISTENT after §154 terminology override

Editor Search / Replace / Authority Boundary
→ CONSISTENT after §155 scope rule

Agent Extensions / Permission / Project Trust
→ CONSISTENT after §156 security invariant

VCS / Local History / Narrative Revision
→ ORTHOGONAL BY DESIGN

DOCX / Editor Representation
→ IMPLEMENTATION BOUNDARY, NOT ROOT CONFLICT
```

因此：

```text
New Root Design Conflict
→ NONE

Writing Product Requirements
→ FROZEN
```

从本节开始，不再因为实现层取舍重新打开以下 CLOSED 决策：

- Draft / Manuscript separation；
- Chapter as minimum Manuscript Build Unit；
- Single Normal Workflow Frontier；
- No Submission Queue；
- One Active Authority Submission per Project；
- Historical Revision + Project Revision Barrier；
- No Direct Manuscript Editing；
- Draft Workspace freedom；
- Authority / Git / Local History separation；
- Application-level Provider configuration；
- Product Scope cannot be bypassed by Agent permissions or extensions。

只有发现真正导致上述根规则无法同时成立的新事实时，才允许将 Writing Product Requirements 从 `FROZEN` 重新升级为 Design Review。

---

# 159. Phase 3 — Technical Architecture / Implementation Design Handoff

下一阶段不再 Grill “产品应该是什么”，而开始设计“怎样可靠实现已经冻结的产品”。

优先顺序建议：

```text
1. Project File Layout / Surface Classification
2. Authority State Machine / Revision Barrier Representation
3. Registry / Schema / Durable Narrative State
4. Candidate / Manuscript Revision / Atomic Authority Commit
5. Agent Role Capability + Tool Permission Matrix
6. Runtime State / Fresh Session / Checkpoint / Resume
7. Editor Document Model（TXT / MD / DOCX）
8. External Mutation / Reconcile / File Watcher
9. Local History Store
10. Git Integration Boundary
11. Extension Trust / Skill / Plugin / MCP Runtime
12. UI State / File View / Narrative View / Tool Windows
```

技术设计允许改变：

- 数据结构；
- 文件名 / 目录名；
- 数据库存储方式；
- snapshot / delta 实现；
- queue-less serialization 的内部机制；
- locking primitive；
- DOCX document model；
- Local History retention implementation；
- MCP client implementation；
- UI component technology。

技术设计不得悄悄改变：

- 用户看到的 Authority 语义；
- Workflow Gate；
- Draft 自由度；
- Manuscript immutability 逻辑；
- Submission / Barrier 规则；
- Agent Role Capability；
- Extension 不可越权；
- Project 内容存在不等于执行授权。

---

# 160. v0.5.1 Final Freeze Summary

> **Writing Module Product Requirements 已正式 FROZEN：产品以自由可并发的 Chapter Draft Workspace 作为 Source World，以单文件 immutable Candidate 驱动 Review，以 Chapter 为最小 Manuscript Build Unit，以 Normal Single Workflow Frontier 管理向前创作、以独立 Historical Revision Eligibility 支持旧章修订，并由 Project Submission Lock 与可收敛的 Revision Barrier 串行维护 Narrative Authority；Manuscript、Narrative State 与 Dependency State 作为一致的 Authority World，不被 Editor 保存、Git、Local History、外部文件修改或 Agent Permission 隐式改变；TXT / MD / DOCX、双视图 Editor、Agent Skills / Plugins / MCP、Project Instructions 与 Project-level Git 均保留，同时项目扩展的存在不构成执行授权。剩余 DOCX fidelity、Local History 默认值与 Manual Add-to-Context 范围均降级为 Technical Architecture / v1 Scope 决策，不再阻塞产品冻结。**
---

# Part G — v0.5.2 Content Mode / Built-in Agent Prompt Customization Addendum（2026-08-13）

> 本 Part G 为 **Additive Product Requirement**。它不重新打开 v0.5.1 已 FROZEN 的 Writing 根设计。
>
> **阅读优先级：Part G > Part F > Part E > Part D > Part C > Part B > Part A。**
>
> 若旧内容与本 Part G 冲突，以 Part G 为准；未被本 Part G 修改的 v0.5.1 FROZEN 决策继续有效。

# 161. Addendum 总结

本轮新增两项正式产品能力：

1. **Application-level Content Mode：SFW / NSFW。**
2. **Built-in Agent 默认系统提示词允许用户进行 Application-level 自定义。**

同时继续冻结：

> **Prompt Freedom 可以很宽，但 Prompt 不得成为 Capability / Authority / Workflow 的安全边界。**

因此：

```text
User-editable Prompt Behavior
→ HIGHLY CUSTOMIZABLE

Workflow / Authority / Dependency / Tool Capability
→ HARD RUNTIME ENFORCEMENT
```

---

# 162. Application-level Content Mode

## 162.1 正式提供 SFW / NSFW 开关

Writing 应在 Application Settings 中提供：

```text
Content Mode
├─ SFW   [default]
└─ NSFW
```

默认：

```text
Content Mode = SFW
```

该设置属于：

```text
Application-level User Preference
```

默认不属于 Project Content，不进入 Project Git，也不改变 Narrative Authority。

---

## 162.2 Content Mode 的职责

Content Mode 主要决定应用对 LLM Agent 注入的默认 **Narrative Content Behavior / Content Policy Overlay**。

它影响：

- Writer 的创作行为；
- Planner / Reviewer 等在分析成人向内容时的默认回避程度；
- 其他需要读取、讨论或生成 Narrative Content 的 LLM Agent。

它不直接改变：

- Project Workflow；
- Draft / Manuscript Authority；
- Review / Acceptance；
- Dependency Graph；
- Agent Tool Permission；
- Extension Trust；
- Provider credentials / routing。

---

## 162.3 SFW Mode

SFW 是应用默认内容模式。

SFW Mode 应采用应用默认的 SFW-oriented Narrative Content Behavior。

较低优先级的 User Prompt Override / Project Instructions 不应在 SFW Mode 下将应用的有效内容模式偷偷扩大为 NSFW。

若用户希望允许应用层面的 NSFW 创作，应显式切换 Content Mode。

---

## 162.4 NSFW Mode

NSFW Mode 的产品语义是：

> **应用不再因为成人向虚构创作内容本身额外施加 SFW-only 的写作回避，并向适用 LLM Agent 注入 NSFW-aware Narrative Behavior。**

它可以用于成人向虚构创作、讨论、编辑与审阅。

但：

```text
NSFW Mode
≠ Provider Safety / Policy Bypass
≠ Model Capability Override
≠ Jailbreak Mechanism
```

不同 Provider / Model 仍可能拥有自身不可由应用 Prompt 改写的能力与政策边界。

因此产品不能承诺：

> “NSFW = 所有模型一定生成所有成人内容。”

NSFW 只代表应用自己的 Prompt / Content Behavior 不再主动限定为 SFW-only。

---

# 163. Built-in Agent Prompt Customization

## 163.1 覆盖旧的“Built-in Profile 不可原地修改”解释

旧冻结规则中：

```text
Built-in Specialist Profile / Definition
→ System-owned Template
→ User cannot edit in place
```

继续保留其真正需要保护的部分：

> **Built-in Base Definition / Role Identity / Capability Contract 仍由系统拥有，作为稳定基线，不允许通过 Prompt 编辑改变。**

但新增：

> **Built-in Agent 的默认 Behavioral System Prompt 允许用户建立 Application-level Prompt Override。**

因此新的正式语义是：

```text
Built-in Agent Base Definition
→ immutable system baseline

Built-in Behavioral Prompt
→ shipped default
→ user may customize / override
→ resettable to shipped default
```

用户无需 Duplicate 整个 Specialist 才能调整默认 Prompt 行为。

---

## 163.2 Prompt 自定义应尽量开放

用户可以修改的内容原则上包括：

- 写作风格；
- 语气；
- Persona；
- Narrative approach；
- 成人内容态度；
- 暴力 / 黑暗主题的表达方式；
- Reviewer 的评价风格；
- Planner 的分析方法；
- Writer 的主动程度；
- Grilling 表达方式；
- 输出组织与格式偏好；
- 其他不改变 Runtime Capability 的 Behavioral Instructions。

产品不应仅提供一个极小的“附加一句 Custom Instruction”文本框作为唯一自定义方式。

第一版至少应允许用户看到并修改该 Built-in Agent 的有效 Behavioral Prompt Override，并提供恢复默认值的能力。

具体采用：

- replace；
- append；
- structured sections；
- diff-based customization；

中的哪一种或哪些组合，进入 Technical Architecture / UX Design。

---

# 164. Prompt Freedom 与 Runtime Safety 正式分层

## 164.1 Narrative / Behavioral Layer：用户高度可控

以下属于 Prompt / Behavior Layer：

- SFW / NSFW 行为；
- 文风；
- Persona；
- 语言风格；
- 创作方法；
- Review 表达方式；
- Grilling 表达方式；
- 输出格式偏好；
- 成人 / 暴力 / 黑暗题材的 Narrative Treatment。

这些应尽量允许用户控制。

---

## 164.2 Workflow / Operational Safety：Prompt 不可覆盖

以下继续属于不可被 Prompt 改写的 Runtime/Product Invariant：

- Draft / Manuscript Authority Boundary；
- Workflow Gate；
- Review / Acceptance；
- Historical Revision / Revision Barrier；
- Dependency integrity；
- Agent Role Capability；
- Tool Permission；
- filesystem scope；
- MCP / Extension Permission；
- Project Trust；
- secret access；
- destructive operation controls；
- Project Submission Lock；
- No Submission Queue；
- Manuscript immutability。

例如用户自定义 Prompt 中出现：

```text
“直接改 Manuscript”
“跳过 Review”
“不要管 Revision Barrier”
“把 Draft 自动当 Canon”
“给自己所有 filesystem 权限”
```

均不能因此产生对应 Runtime Capability。

正式原则：

> **Prompt Instruction ≠ Capability Grant。**

---

# 165. Effective Prompt / Policy Semantic Layers

产品层冻结的是语义分层，不锁定具体 Provider message-role 实现。

概念上：

```text
Provider / Model Non-overridable Boundary
                ↓
Runtime Product Capability / Authority Policy
        [hard enforcement, not prompt-only]
                ↓
Built-in Agent Base Role / Behavioral Baseline
                ↓
Application-level User Prompt Override
                ↓
Application Content Mode Policy
          SFW / NSFW
                ↓
Project Instructions / AGENTS.md
                ↓
Workflow / Task Context
                ↓
User Request
```

其中：

- Runtime Capability / Authority Policy 不应只依赖自然语言 Prompt；
- SFW Mode 作为 Application Content Policy，不允许更低层 Prompt 静默扩大为 NSFW；
- NSFW Mode 是开放应用层面的成人创作行为，不强制作品必须包含 NSFW；
- Project Instructions 可以进一步收窄某个项目的写作风格 / 内容要求；
- Provider-specific role / instruction API 由 Provider Adapter 负责映射。

---

# 166. Built-in Prompt 与 Project Instructions 继续区分

Application-level Built-in Prompt Override：

```text
回答：
“这个 Agent 默认应该怎样工作？”
```

Project Instructions / AGENTS.md：

```text
回答：
“在这个 Project 中，Agent 应遵守哪些项目约定？”
```

二者不得合并成同一持久化对象。

因此：

- Built-in Prompt Override 默认不随 Project Git；
- Project Instructions 继续属于 Project Custom Content，可进入 Git；
- Provider / credentials 继续是 Application-level；
- Prompt Override 与 Provider Configuration 逻辑分离。

---

# 167. Prompt Customization 的版本与恢复要求

Built-in Agent Prompt 会随应用版本演进，因此至少需要保证：

1. 系统始终知道当前 shipped default；
2. 用户 override 不覆盖 / 删除系统基线本身；
3. 用户可以 Reset to Default；
4. 应能够判断当前使用的是 Default 还是 Customized；
5. 应为后续提供 Default vs Override Diff 留出能力；
6. 应能够记录 Effective Prompt / Prompt Configuration 的版本身份，用于 Run / Candidate provenance 或 Debug。

具体存储格式、merge 策略与升级 UX 进入 Technical Architecture。

---

# 168. Provider Compatibility Boundary

不同模型/Provider 对：

- system / developer instructions；
- prompt role hierarchy；
- content policy；
- prompt caching；
- tool guidance；
- reasoning behavior；

支持方式并不完全一致。

因此需要 Provider Adapter：

```text
Canonical Prompt Layers
↓
Provider-specific Prompt Compiler / Adapter
↓
Actual Provider Request
```

产品不能把某一家 Provider 的 message-role 结构直接写死为 Writing Domain Model。

同时：

> **Content Mode 表达应用意图；Provider Adapter 负责尽可能正确映射，但不能伪装成能够绕过 Provider 自身不可覆盖的约束。**

---

# 169. Phase 3 新增 Technical Architecture Grilling 项

在 v0.5.1 §159 的 Technical Architecture Backlog 上正式新增：

## Prompt Architecture / Content Mode

- Canonical Prompt Layer Model；
- Built-in Base Definition vs Behavioral Prompt；
- Application-level Prompt Override storage；
- Effective Prompt composition；
- override / append / replace UX；
- Content Mode overlay representation；
- SFW mode lower-layer conflict handling；
- NSFW provider compatibility behavior；
- Project Instructions precedence；
- Provider-specific Prompt Compiler / Adapter；
- prompt version / provenance；
- prompt upgrade / reset / diff；
- prompt caching boundary；
- prompt customization eval / regression tests。

同时必须在下列既有技术项中加入 Prompt Layer 考虑：

- Agent Role Capability + Tool Permission Matrix；
- Provider / Model Routing；
- Context Lifecycle；
- Project Instructions；
- UI Settings；
- Trace / Provenance；
- Testing / Capability Certification。

---

# 170. v0.5.2 Freeze Verdict

本 Addendum 不改变任何 v0.5.1 Root Architecture：

```text
Draft / Manuscript Separation
→ unchanged

Workflow / Authority / Dependency Safety
→ unchanged

Agent Capability Boundary
→ unchanged

Provider Configuration Scope
→ unchanged

Project Extension Trust
→ unchanged
```

新增内容属于：

```text
Application Content Behavior
+
Agent Behavioral Prompt Customization
```

因此：

```text
New Root Design Conflict
→ NONE

Writing Product Requirements
→ REMAINS FROZEN

Latest Product Baseline
→ v0.5.2 FROZEN
```

---

# 171. v0.5.2 一句话摘要

> **Writing 在 v0.5.1 的 Authority / Workflow 冻结基础上新增 Application-level SFW / NSFW Content Mode，并允许用户对 Built-in Agent 的 Behavioral System Prompt 进行高度开放的 Application-level 自定义；系统仍保留不可修改的 Built-in Base Definition / Role Capability 基线，所有 Workflow、Authority、Dependency、Tool Permission 与 Extension Trust 均由 Runtime 硬约束而非 Prompt 保证，从而实现“Prompt Freedom broadly open, Capability / Authority safety hard-enforced”。**

