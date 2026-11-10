# 02 · git 式版本模型（内容寻址快照 DAG）

> 决策已确认：**git 语义 + PostgreSQL 内容寻址 DAG**，不用 LibGit2Sharp / 真 git 仓库。
> 目标：让界面、REST、MCP Agent 三条写入路径共享同一条可追溯、可 diff、可回滚的历史。

---

## 1. 为什么不是真 git

| 维度 | 真 git 仓库（LibGit2Sharp） | 内容寻址 DAG in PostgreSQL（本方案） |
|---|---|---|
| 事务一致性 | 文件锁 + 工作树，DB 与仓库两步提交易撕裂 | **单事务**：写 Document / Part / Commit 原子完成 |
| 行级权限 | 需自己把目录权限映射到仓库路径 | 直接复用现有 RBAC / scope / Space 隔离 |
| 并发在线编辑 | 工作树竞争、需要显式锁 | 行级锁 + `expectParent` 乐观锁 |
| MCP 写原子性 | Agent 写文件 → 再 commit，中间态可见 | 一次工具调用 = 一个 Commit，无中间态 |
| 细粒度 diff / blame | 需解析 blob 内容 | 字段级直接比 `jsonb`，Part 级走文本 LCS |
| 部分还原 / 大库性能 | checkout 整棵树代价高 | 按路径取 Tree 条目，仅还原目标文档 |
| 外部 git 工具互通 | 原生 | 需导出（提供 `export_as_git` 补偿） |
| 运维 | 需管理磁盘仓库、备份两套 | 一套 PostgreSQL 备份即可 |

**结论**：Episteme 的价值在于「AI 与人共写一份知识库且每次改动可审计可撤销」，不是与 git 生态互通；选 DB 内 DAG 更贴合。代价是接受导出能力作为补偿。

---

## 2. 四层数据结构

```
Blob    sha256(bytes) → 去重存储（Part 内容、PDF/HTML 快照、Schema 定义）
  ↓
TreeEntry  { path, documentId, fieldsHash, partHashes[], annotationHash, kind }
  ↓
Tree    sha256(规范化序列化的 TreeEntry[]) → 某一时刻全库快照
  ↓
Commit  { id, parents[], treeHash, message, author, source, reason, ts, signature }
  ↓
Ref     refs/heads/{branch} | refs/tags/{tag} → commitId
```

### 2.1 Blob

- 主键 `hash = sha256(bytes)`，全局去重；大内容放对象存储，PG 只存 `{hash, size, storageUri, mime}`。
- 短文本（< 64 KB，如 Markdown 正文、代码片段）**同时**冗余进 `parts.content`，便于 `LIKE` / FTS 与快速 diff，Blob 作为版本真相。
- PDF / HTML 快照只存 Blob，不做文本 diff，改为「快照替换 + 标注集 diff」。

### 2.2 Tree / TreeEntry

- Tree 是**全量快照**而非增量，但 TreeEntry 按 path 存哈希，未变文档复用同一 `Tree` 行引用，写入量小。
- 排序规则：path 按 UTF-8 字节序，序列化时去掉空白与无关字段，保证 `sha256` 稳定（同内容必得同 hash）。
- Tree 不可变；永不 UPDATE。

### 2.3 Commit

```json
{
  "id": "cm_01J8Z...",
  "spaceId": "sp_01J...",
  "parents": ["cm_01J8Y..."],
  "treeHash": "b3K9...",
  "message": "补充第 3 节方法论",
  "author": { "type": "user", "id": "u_02", "name": "作者" },
  "source": "ui | api | mcp | import | schema | system",
  "reason": "Agent: 按每日热点补全文献综述",
  "agentId": "claude-desktop",
  "idempotencyKey": "idem_9f2...",
  "signature": "hmac-sha256:...",
  "createdAt": "2026-11-09T01:23:45Z",
  "stats": { "added": 1, "modified": 2, "deleted": 0, "parts": 3 }
}
```

- `parents` 允许 1（普通提交）或 2（合并）；禁止 >2，简化遍历。
- `source` + `agentId` + `reason` 是审计三元组，回答「谁 / 通过什么 / 为什么改」。
- `signature` 对 MCP 调用复用现有 HMAC 签名结果，防止事后抵赖。
- `idempotencyKey` 唯一索引：同一 Agent 重放请求直接返回原 Commit，不产生第二条历史（验收项 7）。

### 2.4 Ref

- `refs/heads/main` 默认分支，Space 创建时生成空树 Commit。
- 分支是**轻量指针**，创建零成本；Agent 常用 `refs/heads/agent/{agentId}/{date}`。
- Tag 不可变，指向固定 Commit，用于「发布版 / 归档快照」。

---

## 3. 写入路径（统一）

```
UI 编辑 / REST PUT / MCP tools.call
        │
        ▼
[1] 鉴权 + scope 校验 + expectParent 乐观锁
[2] 计算新 Blob（去重）/ 更新 Document + Part + Annotation
[3] 构造 Tree（复用未变条目）→ treeHash
[4] INSERT Commit（含审计三元组、幂等键）
[5] 移动 Ref（仅当目标分支 HEAD 仍等于 expectParent，否则 409 Conflict）
[6] 写 AuditLog + 广播变更（SignalR / Redis pub-sub）
        │
        └─ 全部在同一 PostgreSQL 事务；任一步失败整体 ROLLBACK
```

**并发冲突语义**：返回 `409 CONFLICT { head, expected, hint }`。MCP 客户端约定收到 409 后先 `episteme.log` 重读再重试，不得盲目覆盖。

---

## 4. diff

### 4.1 树级 diff

对两棵 Tree 的 `path → entry` 做有序归并，输出五类变更：
`added` / `modified` / `deleted` / `moved`（同 `documentId` 不同 path）/ `type_changed`。
复杂度 O(n log n)，大库按 path 前缀做下推过滤（`diff --path /论文库/2026`）。

### 4.2 文档级 diff

| 对象 | 算法 | 展示 |
|---|---|---|
| `fields` jsonb | 按 FieldDef 逐键比较（不做原始 JSON 文本 diff） | 「发表年份 2025 → 2026」字段行 |
| `Part(md)` / `Part(code)` | 行级 LCS（Myers 简化版），> 200 KB 降级为段落级 | 并排 / 内联双栏，语法着色 |
| `Part(pdf)` | 二进制哈希比较 + 页数 / 文本层指纹 | 「快照已替换」+ 标注集 diff |
| `Part(web)` | HTML 快照哈希 + 抽取正文文本 diff + 原文 URL 变化 | 「正文新增 2 段」 |
| `Annotation` | 按 anchor + text 集合比较 | 新增 / 删除 / 修改批注列表 |
| Schema（source=schema） | 旧版 definition 与新版做字段级对比 | 「新增字段 year（必填）」 |

### 4.3 blame

对目标 Part 的每一行，沿 `parents` 反向遍历 Commit，首次引入该行的 Commit 即归属；结果与 `source` / `agentId` 一起展示，可直接看出「这段是 Agent 写的还是人写的」。为避免全历史扫描，对最近 N 个 Commit 做缓存索引。

---

## 5. branch / merge / revert

### 5.1 分支

- 从任意 Commit 创建分支，成本 = 插入一行 Ref。
- 分支上的 Space 视图是**只读快照 + 可继续提交**；`main` 不受影响。
- MCP 工具默认写 `main`，显式传 `branch` 时写隔离分支（验收项 4）。

### 5.2 合并（三方）

```
base = mergeBase(ours, theirs)
字段级：对 FieldDef 逐键三方比较
  - 仅一方改 → 自动采用
  - 双方改且值相同 → 自动采用
  - 双方改且不同 → 冲突，保留双方值进入 conflict 面板由人选择
Part 级：
  - 仅一方改 → 自动采用
  - 双方改 → 冲突，生成 <<<<<<< ours / ======= / >>>>>>> theirs 标记版本，
              **不得静默丢弃任一侧内容**
目录结构：按 path 合并，moved + modified 组合标记为冲突
```
合并成功生成双 parent Commit；存在未解决冲突时禁止生成 Commit，返回冲突清单。

### 5.3 revert

- **生成反向提交**，不改写历史、不删除原 Commit。
- 支持 revert 单个 Commit、单个路径、或「某个 `agentId` 在某时间窗内的全部 Commit」（AI 误操作一键撤销，产品级刚需）。
- revert 本身也是 Commit，`source=system`，`reason` 记录原 Commit。

### 5.4 tag / 归档

Tag 指向 Commit，用于「2026 Q4 归档版」。归档时同时冻结该 Commit 可达的全部 Blob（GC 白名单）。

---

## 6. GC 与容量

- 可达性：从所有 Ref（含 tag）沿 `parents` 遍历，标记可达 Commit / Tree / Blob。
- 保留策略：默认保留全部 Commit（廉价，只有差异 TreeEntry），Blob 保留所有被可达 Commit 引用的；不可达 Blob 延迟 30 天后清理。
- 大文件：> 100 MB 的 PDF / 视频走对象存储分片，PG 只存引用；diff 时只比元数据。
- 归档导出：`export_as_git` 把 DAG 线性化为真实 git 仓库（每个 Commit 一次 commit），供外部工具消费。

---

## 7. DDL（P2 落地基线）

```sql
CREATE TABLE blobs (
  hash        text PRIMARY KEY,               -- sha256 hex
  size        bigint NOT NULL,
  mime        text,
  storage_uri text,                           -- NULL 表示内容在 content 字段
  content     bytea,
  ref_count   integer NOT NULL DEFAULT 0,
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tree_entries (
  tree_hash      text NOT NULL,
  path           text NOT NULL,
  document_id    text,
  fields_hash    text,
  part_hashes    jsonb NOT NULL DEFAULT '[]'::jsonb,
  annotation_hash text,
  kind           text,
  PRIMARY KEY (tree_hash, path)
);

CREATE TABLE trees (
  hash        text PRIMARY KEY,
  space_id    text NOT NULL,
  entry_count integer NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE commits (
  id               text PRIMARY KEY,
  space_id         text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  parents          text[] NOT NULL DEFAULT '{}',
  tree_hash        text NOT NULL REFERENCES trees(hash),
  message          text NOT NULL,
  author_type      text NOT NULL,             -- user | agent | system
  author_id        text NOT NULL,
  author_name      text NOT NULL,
  source           text NOT NULL,             -- ui | api | mcp | import | schema | system
  reason           text NOT NULL DEFAULT '',
  agent_id         text,
  idempotency_key  text,
  signature        text,
  stats            jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX commits_idem_idx ON commits (space_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX commits_space_time_idx ON commits (space_id, created_at DESC);
CREATE INDEX commits_agent_idx      ON commits (space_id, agent_id, created_at DESC) WHERE agent_id IS NOT NULL;

CREATE TABLE refs (
  space_id    text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  name        text NOT NULL,                  -- refs/heads/main / refs/tags/v1
  commit_id   text NOT NULL REFERENCES commits(id),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (space_id, name)
);
```

> `timestamptz` 一律 UTC；`DateTimeOffset` 写入前 `ToUniversalTime()`（承接 v3.1 容器版 Npgsql 修复，勿回退）。

---

## 8. 与现有调度 / 订阅的衔接

现有 `Subscription` + 30s `SchedulerHostedService` 保持不变，报告生成时**读取指定 Ref 的快照**而非实时表，保证「同一份报告任何时候重跑结果一致」。报告实例记录 `commitId`，可跳转到当时的版本视图。

---

## 9. 明确不做

- 不做 rebase / cherry-pick / 交互式历史改写（保持历史不可变，简化审计）。
- 不做子模块 / 稀疏检出。
- 不做跨 Space 的 DAG 合并。
- 不承诺与外部 git 客户端直接互通（仅提供导出）。
