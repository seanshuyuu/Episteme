# 01 · 概念模型与动态 Schema

> 本文定义 Episteme 的静态结构：有哪些实体、字段怎么描述、Schema 变更后哪些东西必须跟着变、PostgreSQL 怎么落。
> 版本与提交链见 `02`，MCP 工具派生规则见 `03`。

---

## 1. 实体总览

| 实体 | 一句话 | 关键约束 |
|---|---|---|
| `Workspace` | 租户 / 组织边界 | 全局唯一 slug；所有查询强制带 workspace_id |
| `Space` | 一个独立知识库，版本 DAG 的根 | 一个 Space 一棵 DAG；独立权限与独立 MCP 端点 |
| `Node` | 目录节点，物化路径树 | `path` 唯一；可绑定 0..1 个 ContentType；删除需级联策略 |
| `ContentType` | 内容类型（版本化 Schema） | `name` 在 Space 内唯一；`version` 单调递增 |
| `FieldDef` | 字段定义 | 属于 ContentType；`key` 在类型内唯一且为合法标识符 |
| `Document` | 文档：元数据 + Parts | 归属 Node；`fields` 为 jsonb，按 Schema 校验 |
| `Part` | 媒介部件（md/code/pdf/web/attachment） | 一个 Document 至少 1 个 Part；按 `ord` 排序 |
| `Annotation` | 标记（高亮 / 下划线 / 批注） | 指向 Part；带稳健锚点；独立版本化 |
| `Blob` | 内容寻址字节 | sha256 主键，跨 Space 去重（可选按 Space 隔离） |
| `Tree` / `TreeEntry` | 目录快照 | 见 `02` |
| `Commit` / `Ref` | 提交与引用 | 见 `02` |
| `Template` / `Pack` | 声明式模板 | JSON，含目录 + Schema + 视图 + 提示词 |
| `AuditLog` / `ApiKey` / `Subscription` | 沿用现有内核 | 不重复定义 |

### 1.1 关系图

```
Workspace ─1:N─> Space ─1:N─> Node (自引用 parent_id, 物化 path)
                    │              │
                    │              └─0..1─> ContentType ─1:N─> FieldDef
                    │              └─1:N─> Document ─1:N─> Part ─1:N─> Annotation
                    │                            │
                    │                            └─ fields:jsonb (按 ContentType 校验)
                    ├─1:N─> Commit ─N:1─> Tree ─1:N─> TreeEntry ─N:1─> Blob
                    ├─1:N─> Ref (branch / tag)
                    └─0:N─> Pack (已安装模板)
```

---

## 2. 「目录」的语义：目录即集合

传统文档系统里「目录」和「数据表」是两件事。Episteme 把它们合并：

- **纯目录节点**：`content_type_id = NULL`，仅用于组织层级（如 `2026/11`）。
- **集合节点**：绑定一个 ContentType，其下的 Document 必须满足该 Schema（如 `论文库` 绑定 `Paper` 类型）。
- 子目录可以**继承或覆盖**父节点的类型绑定；覆盖时显式记录，MCP 工具描述里会注明。
- 节点还有 `expose_to_mcp: bool` 与 `default_part_kind`，决定它是否出现在资源树、以及新建文档默认用哪种阅读器。

这样 Agent 看到的 `episteme://{space}/{path}` 既是路径，也是「可查询的集合」，`tree` 工具一次调用就能拿到结构与类型信息。

---

## 3. ContentType / FieldDef 规格

### 3.1 ContentType

```json
{
  "id": "ct_01J...",
  "spaceId": "sp_01J...",
  "name": "Paper",
  "slug": "paper",
  "label": "论文",
  "icon": "file-text",
  "version": 3,
  "description": "学术论文与阅读笔记",
  "exposeToMcp": true,
  "mcpToolPrefix": "paper",
  "primaryField": "title",
  "titleTemplate": "{{title}}",
  "defaultPartKinds": ["pdf", "md"],
  "fields": [ /* FieldDef[] */ ],
  "indexes": [ { "name": "by_year", "fields": ["year", "venue"] } ],
  "views": [ { "name": "全部", "filter": {}, "sort": [{"field":"updatedAt","desc":true}] } ],
  "createdAt": "2026-11-09T01:00:00Z",
  "updatedAt": "2026-11-09T01:00:00Z"
}
```

### 3.2 字段类型表

| `type` | 存储 | 控件 | 可索引 | MCP JSON Schema | 备注 |
|---|---|---|---|---|---|
| `text` | jsonb string | 单行输入 | ✔ | `string` | 支持 `maxLength` / `pattern` |
| `longtext` | jsonb string | 多行文本 | ✔(FTS) | `string` | 不做富文本 |
| `markdown` | jsonb string | MD 编辑器 | ✔(FTS) | `string` | 与 `Part(md)` 区别：字段是短段落 |
| `code` | jsonb string | 代码块（带 language） | ✔ | `string` | 存 `{lang, code}` |
| `number` | jsonb number | 数字输入 | ✔ | `number` / `integer` | `min` `max` `precision` |
| `date` | jsonb string(ISO) | 日期选择 | ✔ | `string(format=date-time)` | 统一 UTC 存储 |
| `boolean` | jsonb bool | 开关 | ✔ | `boolean` | |
| `select` | jsonb string | 下拉 | ✔ | `string(enum)` | `options[]` |
| `multi_select` | jsonb array | 多选 | ✔(GIN) | `array<enum>` | |
| `tags` | jsonb array | 标签输入 | ✔(GIN) | `array<string>` | 自动补全来自已有值 |
| `url` | jsonb string | URL 输入 | ✔ | `string(format=uri)` | 抓取前做 SSRF 校验 |
| `ref` | jsonb string(docId) | 文档选择器 | ✔ | `string` | `targetNode` / `targetType`；对称引用见 §6 |
| `json` | jsonb any | JSON 编辑器 | ✖(可指定路径) | `object` | 需 `schema` 子定义 |
| `file` | jsonb `{blobHash,name,mime,size}` | 上传 | ✖ | `object` | 走 Blob 存储 |
| `user` | jsonb userId | 成员选择 | ✔ | `string` | |

### 3.3 FieldDef 结构

```json
{
  "key": "year",
  "label": "发表年份",
  "type": "number",
  "required": true,
  "unique": false,
  "indexed": true,
  "exposeToMcp": true,
  "mcpWritable": true,
  "defaultValue": 2026,
  "hint": "用于按年份筛选",
  "validation": { "min": 1900, "max": 2100, "integer": true },
  "options": [],
  "ref": null,
  "order": 4
}
```

**双层暴露开关**：
- `ContentType.exposeToMcp = false` → 该类型完全不生成动态工具。
- `FieldDef.exposeToMcp = false` → 字段不出现在任何工具参数 / 返回值（用于存放内部状态、密钥、成本字段等）。
- `mcpWritable = false` → 只读字段（如自动计算的时间、人工审核结论），Agent 可看不可改。

这两层是「AI 可写范围」的安全边界，验收项 8 会直接测它。

### 3.4 校验顺序

写入 `Document.fields` 时固定顺序：
`身份与权限 → ContentType 版本匹配 → required → 类型/枚举/range/pattern → unique（Space 内） → ref 存在性 → 自定义 validator → 写 Part → 生成 Commit → 审计`。
任一步失败整体回滚，不产生半成品 Commit。

---

## 4. Schema 变更与迁移

Schema 是**版本化**的：每次修改 `version + 1`，旧版本保留以支持历史文档的还原与 diff。

| 变更 | 策略 | 是否需人工确认 |
|---|---|---|
| 新增字段（非必填 / 有默认值） | 回填默认值，历史文档不动 | 否 |
| 新增必填字段 | 必须提供 `defaultValue` 或 `backfill` 表达式 | **是** |
| 删除字段 | 值移入 `_archived_fields`，不物理删除 | 否 |
| 字段重命名 | 解释为「删除 + 新增 + `backfill` 映射」 | **是** |
| 类型变更 | 默认拒绝；提供显式 `cast` 规则才允许 | **是** |
| 枚举缩减 | 已有值保留但标记 `deprecated` | 是 |
| `exposeToMcp` 关闭 | 立即从工具签名移除并广播 `tools/list_changed` | 否 |

**变更后的同步重建清单**（事务内完成，失败全回滚）：

1. 列表列 / 表单控件 / 筛选器 / 排序选项（前端按 Schema 渲染，无需发布）。
2. PostgreSQL 表达式索引（`fields->>'key'`）与 GIN 索引。
3. 动态 MCP 工具 JSON Schema + `toolsVersion + 1` + 广播。
4. 导出模板与视图定义。
5. 一条 `source=schema` 的 Commit，记录谁在何时改了什么（Schema 本身也进版本历史）。

---

## 5. 动态渲染契约

前端不再有「素材库」「热点」这类手写页面，只有一个通用渲染器：

```
GET /api/v1/episteme/spaces/{id}/nodes/{nodeId}/schema
  → { contentType, fields[], views[], permissions }

渲染器：
  fields[].type  → 表单控件映射表
  views[]        → 列表列 / 筛选 / 排序 / 分组
  permissions    → 控件可用状态（禁用而非隐藏，避免 Agent 误判）
  partKinds      → 右侧阅读器 Tab
```

新增字段类型只需在控件映射表注册一项；新增内容类型零代码。

---

## 6. 关联与溯源（对称引用）

`ref` 字段支持 `symmetric: true`：A 引用 B 时自动在 B 的 `_backrefs` 中登记。
典型用途：文档库里「素材原文 ↔ 改编稿」「论文 PDF ↔ 阅读笔记」「需求 ↔ 代码文件」。溯源面板由 `_backrefs` 自动生成，不需要额外维护关系表。

`ref` 不做数据库外键强约束（跨分支 / 跨版本还原时目标可能暂不存在），改由应用层做存在性校验并在 diff 里显示「悬挂引用」。

---

## 7. PostgreSQL DDL（P1 落地基线）

```sql
CREATE TABLE spaces (
  id            text PRIMARY KEY,
  workspace_id  text NOT NULL,
  slug          text NOT NULL,
  name          text NOT NULL,
  description   text NOT NULL DEFAULT '',
  head_ref      text NOT NULL DEFAULT 'refs/heads/main',
  settings      jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (workspace_id, slug)
);

CREATE TABLE nodes (
  id                text PRIMARY KEY,
  space_id          text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  parent_id         text REFERENCES nodes(id) ON DELETE CASCADE,
  path              text NOT NULL,               -- 物化路径 /a/b/c
  name              text NOT NULL,
  content_type_id   text,
  expose_to_mcp     boolean NOT NULL DEFAULT true,
  default_part_kind text,
  meta              jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (space_id, path)
);
CREATE INDEX nodes_path_idx ON nodes (space_id, path text_pattern_ops);

CREATE TABLE content_types (
  id            text PRIMARY KEY,
  space_id      text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  slug          text NOT NULL,
  label         text NOT NULL,
  version       integer NOT NULL DEFAULT 1,
  definition    jsonb NOT NULL,                 -- 完整 ContentType JSON
  expose_to_mcp boolean NOT NULL DEFAULT true,
  created_at    timestamptz NOT NULL DEFAULT now(),
  UNIQUE (space_id, slug, version)
);
CREATE INDEX content_types_def_idx ON content_types USING gin (definition);

CREATE TABLE documents (
  id               text PRIMARY KEY,
  space_id         text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  node_id          text NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
  content_type_id  text NOT NULL,
  schema_version   integer NOT NULL,
  title            text NOT NULL,
  fields           jsonb NOT NULL DEFAULT '{}'::jsonb,
  tags             text[] NOT NULL DEFAULT '{}',
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX documents_fields_idx ON documents USING gin (fields);
CREATE INDEX documents_fts_idx    ON documents USING gin (to_tsvector('zhcfg', title || ' ' || coalesce(fields::text,'')));
-- 动态表达式索引由 Schema 变更事务按 FieldDef.indexed 自动 CREATE/DROP

CREATE TABLE parts (
  id           text PRIMARY KEY,
  document_id  text NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  kind         text NOT NULL CHECK (kind IN ('md','code','pdf','web','attachment')),
  ord          integer NOT NULL DEFAULT 0,
  blob_hash    text,                            -- 大内容指向 Blob；短文本可直接存 content
  content      text,
  mime         text,
  language     text,                            -- code 用
  source_uri   text,                            -- web / pdf 原文链接
  meta         jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (document_id, ord)
);

CREATE TABLE annotations (
  id          text PRIMARY KEY,
  part_id     text NOT NULL REFERENCES parts(id) ON DELETE CASCADE,
  kind        text NOT NULL CHECK (kind IN ('highlight','underline','note')),
  anchor      jsonb NOT NULL,                   -- 见 04 §5 稳健锚点
  text        text NOT NULL DEFAULT '',
  color       text,
  author      text NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX annotations_part_idx ON annotations (part_id);
```

> `zhcfg` 需 `zhparser` 扩展；若部署环境未装，降级为 `'simple'` 配置并在设置页提示中文检索精度下降。所有 `timestamptz` 一律写 UTC（承接 v3.1 的 Npgsql UTC 修复）。

---

## 8. 与现有表的共存

现有 `library_items` / `hotspots` / `report_instances` / `subscriptions` 等 16 张表**保持不动**，P4 通过迁移脚本写入新的通用表，旧表只读保留一个发布周期。两套读路径由 Pack 视图层做路由，避免一次性大爆炸迁移。迁移产生的首个 Commit 固定 message：`import from workbench v3.2`，`source=import`，作者为迁移任务。

---

## 9. 明确不做

- 不做运行时动态建表（每类型一张表）——Schema 变更会变成 DDL 地狱，统一用 `jsonb` + 表达式索引。
- 不做跨 Space 的 SQL JOIN 查询，跨空间走 API 聚合。
- 不在内核内置任何业务枚举（「素材」「热点」「报告」等一律属于 Pack）。
- 不做 CRDT 实时协同（P1 用乐观锁，见 `06` 待确认项 2）。
