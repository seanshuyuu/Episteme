# Episteme · 知书｜自底向上实施规格

| 项 | 内容 |
|---|---|
| 产品名 | **Episteme**（ἐπιστήμη，希腊语「经论证的知识」）· 中文 **知书** |
| 文档版本 | **v1.0 · 重写版（从零实施）** |
| 日期 | 2026-11-17 |
| 本版性质 | **推翻 v0.x 的渐进演进路线，按「数据 → CRUD → API/MCP → 页面」自底向上重建** |
| 技术决策 | .NET 10 + ASP.NET Core + PostgreSQL + **EF Core Code First**（用户确认） |
| 首版范围 | **极简打底：实体模型 + 数据库 CRUD（详细设计）；API/MCP 与页面给出路标与契约边界** |
| 旧版去向 | v0.x 规格与实现保留在 Git 历史（提交 `0ed63fb` 及之前），仅作参考，不再是实施基线 |

---

## 0. 本版为什么重写

v0.x 的规格是「在既有 Workbench 上逐层抽象」，实际落地后累积了三类问题：

1. **范围膨胀**：一个月内从「通用空壳」扩到客户端、四类阅读器、三方合并、GC、git 导出、三套鉴权模式，其中多项只停留在规格层（`MergeAsync` 实为单亲快进，GC 与真 git 导出未实现）。
2. **边界含混**：实体、服务、MCP 工具、版本对象互相纠缠，改一处要动多处；`EnsureCreated` 无迁移导致加字段靠手写升级脚本。
3. **地基不实**：先写了页面与 MCP，再回头补数据模型，导致版本、权限、Schema 校验都成了事后补丁。

本版 reversing 顺序：**先把实体与关系、数据流设计到可建库，再写 CRUD，再往上长 API/MCP，最后才是页面**。每一层只依赖下一层，不反向依赖。

**本版的三条硬约束**

- H1 **每一层只向下依赖**：CRUD 不认识 HTTP，API 不认识 DOM，页面不直接碰数据库。
- H2 **先有真实的 Migration**：用 EF Core Code First 的 `dotnet ef migrations`，不再用 `EnsureCreated` 打补丁。
- H3 **极简打底**：能用一个字段解决的不用一张表，能推迟的能力推迟，但**字段占位要一次留够**（见 §3.6 前瞻字段）。

---

## 1. 业务目标（其余一切的唯一来源）

下面六条是**可判定的目标**，后续每个实体、每个接口都必须能回答「我服务于哪一条」。答不上来的，不建。

| 编号 | 业务目标 | 判定方式 |
|---|---|---|
| **G1 空壳** | 首次启动零业务概念、零预置目录、零预置字段 | 新库里除了「创建第一个空间」没有任何业务字样 |
| **G2 自定义结构** | 用户自己定义目录树，自己定义每类文档的字段 | 不改代码即可建出「论文库」「需求库」两套完全不同结构 |
| **G3 文档即载体** | 一份文档 = 元数据 + 1..N 个媒介部件（正文/代码/PDF/网页） | 同一文档可同时挂 md 与 code，互不覆盖 |
| **G4 变动可追溯** | 每次写入生成一个不可变快照，可列出、比对、回滚 | 任意两次写入可 diff，回滚后内容与目标快照一致 |
| **G5 AI 可读写** | Agent 通过 MCP 读写，工具随 Schema 自动派生 | 新建一个内容类型后，Agent 无需重启即获得对应工具 |
| **G6 账号私有** | 空间归属唯一账号，他人不可见不可写 | 用 B 的凭据访问 A 的空间一律拒绝 |

**目标之间的依赖顺序**

```
G1 空壳 ─┬─> G2 自定义结构 ──> G3 文档即载体 ──> G4 变动可追溯
         │                                          │
         └──────────────> G5 AI 可读写 <────────────┘
                                │
                          G6 账号私有（横切，贯穿 G2–G5）
```

**分阶段承载**

| 阶段 | 承载目标 | 本 SPEC 的详细程度 |
|---|---|---|
| 阶段一 数据 + CRUD | G1、G2、G3（结构层）+ G6 的归属占位 | **逐字段详细设计** |
| 阶段二 API + MCP | G4、G5 + G6 完整鉴权 | 契约与边界，接口清单级 |
| 阶段三 页面 | G1、G2 的可视化，四类阅读器 UI | 路线图与验收项级 |

---

## 2. 设计原则（精简版）

沿用 v0.x 中被证明正确、且已经写进实现的部分，只保留五条：

1. **Schema 即契约**：目录绑定内容类型，类型定义字段；字段一改，列表/表单/校验/MCP 工具签名同源派生。（服务 G2、G5）
2. **文档是载体不是格式**：`Document = 元数据 + 1..N 个 Part`；四类媒介是 Part 的 `kind`，不是四种实体。（服务 G3）
3. **写入唯一收口**：所有写操作（页面、REST、MCP）走同一个 `WriteAsync` 服务，版本快照在那里统一生成。（服务 G4，杜绝 v0.x 三条写路径分叉）
4. **权限下沉到空间**：Space 是版本根、权限根、MCP 端点根；归属按账号，不按角色矩阵。（服务 G6）
5. **无业务名词内核**：内核实体与表结构里不出现「素材/热点/报告/论文」等任何领域词。（服务 G1）

> v0.x 的「复用而非重写」原则本版**不采用**——本版就是从零重建，只沿用业务语义与技术栈，不沿用代码。

---

## 3. 实体与关系模型（核心设计）

### 3.1 实体全景与分期

```
                         ┌──────────── 阶段一（本次落地，6 张表）────────────┐

  Space 空间 ──1:N──> Node 目录节点 ──1:N──> Document 文档 ──1:N──> Part 部件
     │                   │  ▲                   │                      │
     │                   │  └──self(parent_id)   │                      │
     │                   │                       │                      │
     │                   └─0..1─> ContentType ───┘                      │
     │                              类型/Schema                          │
     │                                                                    │
     └──────────────────────────────1:N────────────────────────────────────┘
                                                                           │
                                                                    Part ──1:N──> Annotation 标记

                         ┌──────────── 阶段二（本版定模型，后续落地）───────┐

  Space ──1:N──> Commit ──N:1──> Tree ──1:N──> TreeEntry ──N:1──> Blob
     │              │
     └──1:N──> Ref(branch/tag) ──> Commit

  Space ──1:N──> AuditLog（写操作审计）

                         ┌──────────── 阶段三（本版仅占位）──────────────┐

  User 账号 ──1:N──> Space(owner_id)      McpToken ──N:1──> User
```

**为什么这样分期**：阶段一的 6 张表已经能完整表达「用户自定义目录 + 自定义字段 + 多媒介文档」，能独立建库、独立 CRUD、独立验收。版本对象（Blob/Tree/Commit/Ref）是**对阶段一数据的快照**，必须先有稳定的被快照对象才能设计得对，所以放阶段二。账号表放阶段三，但 `Space.OwnerId` 字段阶段一就留（见 §3.6）。

### 3.2 阶段一实体逐项定义

#### 3.2.1 Space 空间

> 服务 G1（空壳的容器）、G6（权限与版本根）。一个 Space = 一棵独立版本树 + 一个独立 MCP 端点。

| 字段 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `Id` | string(32) | PK | `sp_` + 20 位十六进制，应用层生成 |
| `Slug` | string(64) | UNIQUE(owner_id, slug) | URL 友好标识，小写字母/数字/连字符 |
| `Name` | string(200) | NOT NULL | 显示名 |
| `Description` | string(2000) | DEFAULT '' | 简介 |
| `OwnerId` | string(64) | NOT NULL, DEFAULT 'local' | **账号归属占位**；阶段三接 User 表 |
| `CreatedAt` | timestamptz | DEFAULT now() | 一律 UTC |
| `UpdatedAt` | timestamptz | DEFAULT now() | 乐观锁比对用 |

**设计说明**
- **为什么 Space 是根**：把版本、权限、MCP 工具、目录树的作用域全部收敛到一个 ID 上，查询时永远先带 `space_id`，从数据结构上杜绝跨空间串数据（G6）。
- **为什么用字符串 ID 不用自增**：应用层生成、无需数据库往返、跨环境导入导出不冲突；`sp_`/`nd_` 等前缀让日志与审计可肉眼分辨实体类型。
- **为什么 OwnerId 默认值 'local'**：阶段一没有账号体系，但不能让字段为空——否则阶段三加 User 表时要回填全部历史数据。默认 `local` 表示「本机/单用户」，阶段三把真实账号写进来即可，表结构不动。

#### 3.2.2 Node 目录节点

> 服务 G2（用户自定义树）、G5（MCP 资源路径）。**目录即集合**：既能纯组织层级，也能绑定一个 ContentType 成为「表」。

| 字段 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `Id` | string(32) | PK | `nd_` 前缀 |
| `SpaceId` | string(32) | FK→Space, CASCADE, NOT NULL | 所属空间 |
| `ParentId` | string(32)? | FK→Node, **RESTRICT** | 父节点；NULL 表示根 |
| `Path` | string(1000) | UNIQUE(space_id, path), NOT NULL | 物化路径 `/a/b/c` |
| `Name` | string(200) | NOT NULL | 显示名 |
| `ContentTypeId` | string(32)? | FK→ContentType, **RESTRICT** | 绑定的类型；NULL = 纯目录 |
| `SortOrder` | int | DEFAULT 0 | 同级排序 |
| `Icon` | string(64) | DEFAULT 'folder' | 前端图标名，内核不解释 |
| `ExposeToMcp` | bool | DEFAULT true | 是否进入 MCP 资源树（G5 前瞻，阶段二生效） |

**设计说明**
- **为什么物化路径（同时存 parent_id 和 path）**：`parent_id` 维护树的拓扑与级联，`path` 让「查某节点下全部后代」变成一次前缀查询（`path LIKE '/a/b/%'`），也让阶段二的 MCP 资源 URI `episteme://{space}/a/b` 直接可用。冗余换来的是查询简单，代价是移动节点要批量改后代 path——这个代价在 §4.4 显式处理。
- **为什么删除父节点用 RESTRICT 而非 CASCADE**：让服务层决定「有子节点时是拒绝还是递归删除」，避免一次误删抹掉整棵树。数据库只保证「不允许留下孤儿」这条底线。
- **为什么 ContentTypeId 用 RESTRICT**：正在被文档使用的类型不允许删除，防止出现「文档引用了不存在的 Schema」。

#### 3.2.3 ContentType 内容类型（Schema）

> 服务 G2（自定义字段）、G5（工具派生）。版本化：每次改字段 `Version + 1`，旧版本保留。

| 字段 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `Id` | string(32) | PK | `ct_` 前缀 |
| `SpaceId` | string(32) | FK→Space, CASCADE, NOT NULL | 所属空间 |
| `Slug` | string(64) | NOT NULL | 类型标识，如 `paper` |
| `Label` | string(100) | NOT NULL | 显示名，如「论文」 |
| `Version` | int | NOT NULL, DEFAULT 1 | 单调递增 |
| `DefinitionJson` | jsonb | NOT NULL | 完整定义（字段数组 + 视图 + 暴露开关） |
| `ExposeToMcp` | bool | DEFAULT true | 类型级 MCP 开关 |
| `CreatedAt` / `UpdatedAt` | timestamptz | DEFAULT now() | |

UNIQUE(`space_id`, `slug`, `version`) —— 同一类型的不同版本共存。

**`DefinitionJson` 的结构**（内核只解释这几个键，其余透传给前端）

```json
{
  "fields": [
    {
      "key": "year",
      "label": "发表年份",
      "type": "number",
      "required": true,
      "indexed": false,
      "exposeToMcp": true,
      "mcpWritable": true,
      "defaultValue": null,
      "validation": { "min": 1900, "max": 2100 },
      "options": []
    }
  ],
  "primaryField": "title",
  "defaultPartKinds": ["md"],
  "views": []
}
```

字段类型（阶段一支持 8 种，够用即可）：`text` `longtext` `number` `date` `boolean` `select` `tags` `ref`。
阶段二再补：`markdown` `code` `multi_select` `url` `json` `file` `user`。

**设计说明**
- **为什么 Schema 存 jsonb 而不是每类型建一张表**：后者等于把「用户自定义」变成「用户让运维建表」，是 DDL 地狱；前者让加字段变成一次 UPDATE（G2）。代价是字段级查询要建表达式索引，本版接受。
- **为什么版本化**：文档里存的是 `schema_version`，改了字段定义后旧文档仍按旧版本解释，不会因为删了字段就读出脏数据。阶段二 diff/回滚也要靠它。
- **双层暴露开关**（类型级 + 字段级）是「AI 可写范围」的安全边界：`exposeToMcp=false` 的字段不出现在任何工具签名里（G5 + G6）。

#### 3.2.4 Document 文档

> 服务 G3（载体）、G2（字段按 Schema 存）。文档本身不存正文，正文在 Part。

| 字段 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `Id` | string(32) | PK | `doc_` 前缀 |
| `SpaceId` | string(32) | FK→Space, CASCADE, NOT NULL | 冗余但必带，便于强制空间隔离 |
| `NodeId` | string(32) | FK→Node, CASCADE, NOT NULL | 所在目录 |
| `ContentTypeId` | string(32) | FK→ContentType, RESTRICT, NOT NULL | 决定字段校验规则 |
| `SchemaVersion` | int | NOT NULL | 创建时锁定，不随类型升级自动变 |
| `Title` | string(500) | NOT NULL | 标题 |
| `FieldsJson` | jsonb | NOT NULL DEFAULT '{}' | 用户自定义字段值 |
| `TagsJson` | jsonb | NOT NULL DEFAULT '[]' | 标签数组 |
| `CreatedAt` / `UpdatedAt` | timestamptz | DEFAULT now() | |

索引：`(space_id, node_id)`、`(space_id, content_type_id)`、`(space_id, updated_at DESC)`。

**设计说明**
- **为什么 FieldsJson 与 Title 分开**：标题是全局高频检索字段，提到独立列避免每次都解析 jsonb；其余字段保持灵活。
- **为什么 SchemaVersion 锁在创建时**：如果自动跟随类型升级，一次 Schema 改动会让所有历史文档「字段含义悄悄变了」，diff 与回滚都会失真。锁版本号后，迁移是显式的、可审计的。
- **为什么冗余 SpaceId**（Node 已经归属 Space）：让「查某空间全部文档」不必 JOIN Node；更重要的是所有 CRUD 都能强制带 `space_id` 过滤，是 G6 的数据层护栏。

#### 3.2.5 Part 媒介部件

> 服务 G3（一份文档多种媒介）。四种媒介是 `kind` 的取值，不是四张表。

| 字段 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `Id` | string(32) | PK | `pt_` 前缀 |
| `DocumentId` | string(32) | FK→Document, CASCADE, NOT NULL | 所属文档 |
| `Kind` | string(16) | NOT NULL, CHECK IN ('md','code','pdf','web') | 媒介类型 |
| `Ord` | int | NOT NULL | 同一文档内排序 |
| `Content` | text? | 可空 | 短内容直接内联 |
| `BlobRef` | string(64)? | 可空 | 大内容指向 Blob（阶段二生效） |
| `Mime` / `Language` / `SourceUri` | string? | 可空 | 媒介元数据 |
| `MetaJson` | jsonb | NOT NULL DEFAULT '{}' | 扩展元数据（如 pdf 页数、代码行号映射） |

UNIQUE(`document_id`, `ord`)。

**设计说明**
- **为什么 Part 而不是四张表**：需求是「同一份文档可同时挂 md 与 code」（G3）。四张表会变成四种实体、四套 CRUD、四个 MCP 工具；一个 `kind` 字段 + 一个统一 Viewer 契约就够了。
- **为什么同时留 Content 与 BlobRef**：阶段一没有 Blob 表，短文本直接内联最省事；阶段二接入内容寻址时，把大内容搬到 Blob、`Content` 置空即可，Part 表结构不变。这是 §3.6 的前瞻字段。
- **为什么 Web/PDF 不可编辑但仍建 Part**：它们的原文与标记要进版本（G4），标记挂 Part 最自然。

#### 3.2.6 Annotation 标记

> 服务 G3 + G4：高亮/批注是内容的一部分，必须进快照，不能是外挂表。

| 字段 | 类型 | 约束 | 说明 |
|---|---|---|---|
| `Id` | string(32) | PK | `an_` 前缀 |
| `PartId` | string(32) | FK→Part, CASCADE, NOT NULL | 所属部件 |
| `Kind` | string(16) | CHECK IN ('highlight','underline','note') | 标记类型 |
| `AnchorJson` | jsonb | NOT NULL | 稳健锚点（走 §3.5 的锚点契约） |
| `Text` | text | DEFAULT '' | 批注文字 |
| `Color` | string(16)? | 可空 | 高亮色 |
| `Author` | string(64) | DEFAULT 'user' | 作者；Agent 写 `agent:{id}` |
| `CreatedAt` | timestamptz | DEFAULT now() | |

**设计说明**
- **为什么锚点是 jsonb 而不是三个标量列**：PDF 用 `{page, rect, textQuote}`，Web 用 `{domPath, textQuote}`，md/code 用 `{startLine, endLine, startOffset, endOffset}`——结构不同但都进同一个字段，Viewer 按 `Part.Kind` 解释。这样加新媒介不用加列。
- **为什么标记独立成实体而不是塞进 Part.Content**：标记要单独增删改、单独归属作者、单独进 diff（阶段二），塞进正文无法表达。

### 3.3 阶段二实体（本版定模型，后续落地）

| 实体 | 关键字段 | 服务目标 | 落地时机 |
|---|---|---|---|
| `Blob` | `Hash`(PK, SHA-256), `Size`, `Mime`, `Content`, `CreatedAt` | G4 内容寻址去重 | 阶段二 |
| `Tree` | `Hash`(PK), `SpaceId`, `EntriesJson`, `EntryCount` | G4 不可变快照 | 阶段二 |
| `TreeEntry` | `Path`, `Kind`, `DocumentId`, `BlobHash`（存在 `EntriesJson` 内，非独立表） | G4 | 阶段二 |
| `Commit` | `Id`, `SpaceId`, `ParentsJson`, `TreeHash`, `Message`, `AuthorType`, `Source`, `Reason`, `IdempotencyKey`, `CreatedAt` | G4 + G5 审计 | 阶段二 |
| `Ref` | `(SpaceId, Name)` PK, `CommitId` | G4 分支/标签 | 阶段二 |
| `AuditLog` | `Id`, `SpaceId`, `Actor`, `Action`, `CommitId`, `DetailJson` | G6 审计 | 阶段二 |

**相对 v0.x 的两处收敛**（吸取昨天 DAG 分析的教训）：

1. **合并只做快进覆盖**：`MergeAsync` 保持「哈希比对 → 无冲突则整批迁移 → 单亲 Commit」，**不承诺字段级三方合并与冲突标记**。规格与实现必须一致，这是 v0.x 最大的教训。
2. **导出只做清单**：`ExportGitAsync` 返回线性化提交清单，**不承诺生成真 git 仓库**。真导出列为远期。

### 3.4 关系与基数总表

| 关系 | 基数 | 删除行为 | 理由 |
|---|---|---|---|
| Space → Node | 1:N | CASCADE | 空间删除，目录全部作废 |
| Node → Node | 1:N（自引用） | RESTRICT | 由服务层决定递归/拒绝，防误删 |
| Node → ContentType | N:1 | RESTRICT | 类型在用不可删 |
| ContentType → Document | 1:N | RESTRICT | 同上 |
| Document → Node | N:1 | CASCADE | 目录删则文档删 |
| Document → Part | 1:N | CASCADE | 载体删则部件删 |
| Part → Annotation | 1:N | CASCADE | 部件删则标记删 |
| Space → Document | 1:N | CASCADE | 冗余关联，保证空间隔离 |

> PostgreSQL 对多重 CASCADE 路径无 SQL Server 的限制，但本版仍刻意把「类型/父节点」设为 RESTRICT——这是业务策略，不是数据库妥协。

### 3.5 数据表 DDL 草案（EF Code First 的目标形态）

> 这份 DDL **不是**实施手段，只是 Migration 生成结果的可读描述。实施时以实体类 + `OnModelCreating` 配置为准，用 `dotnet ef migrations add InitialCreate` 生成（H2）。

```sql
CREATE TABLE spaces (
  id           text PRIMARY KEY,
  slug         text NOT NULL,
  name         text NOT NULL,
  description  text NOT NULL DEFAULT '',
  owner_id     text NOT NULL DEFAULT 'local',
  created_at   timestamptz NOT NULL DEFAULT now(),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (owner_id, slug)
);

CREATE TABLE content_types (
  id             text PRIMARY KEY,
  space_id       text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  slug           text NOT NULL,
  label          text NOT NULL,
  version        integer NOT NULL DEFAULT 1,
  definition     jsonb NOT NULL,
  expose_to_mcp  boolean NOT NULL DEFAULT true,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (space_id, slug, version)
);

CREATE TABLE nodes (
  id              text PRIMARY KEY,
  space_id        text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  parent_id       text REFERENCES nodes(id) ON DELETE RESTRICT,
  path            text NOT NULL,
  name            text NOT NULL,
  content_type_id text REFERENCES content_types(id) ON DELETE RESTRICT,
  sort_order      integer NOT NULL DEFAULT 0,
  icon            text NOT NULL DEFAULT 'folder',
  expose_to_mcp   boolean NOT NULL DEFAULT true,
  UNIQUE (space_id, path)
);
CREATE INDEX nodes_parent_idx ON nodes (space_id, parent_id);

CREATE TABLE documents (
  id              text PRIMARY KEY,
  space_id        text NOT NULL REFERENCES spaces(id) ON DELETE CASCADE,
  node_id         text NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
  content_type_id text NOT NULL REFERENCES content_types(id) ON DELETE RESTRICT,
  schema_version  integer NOT NULL,
  title           text NOT NULL,
  fields          jsonb NOT NULL DEFAULT '{}'::jsonb,
  tags            jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX documents_node_idx ON documents (space_id, node_id);
CREATE INDEX documents_type_idx ON documents (space_id, content_type_id);

CREATE TABLE parts (
  id         text PRIMARY KEY,
  document_id text NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  kind       text NOT NULL CHECK (kind IN ('md','code','pdf','web')),
  ord        integer NOT NULL,
  content    text,
  blob_ref   text,
  mime       text,
  language   text,
  source_uri text,
  meta       jsonb NOT NULL DEFAULT '{}'::jsonb,
  UNIQUE (document_id, ord)
);

CREATE TABLE annotations (
  id         text PRIMARY KEY,
  part_id    text NOT NULL REFERENCES parts(id) ON DELETE CASCADE,
  kind       text NOT NULL CHECK (kind IN ('highlight','underline','note')),
  anchor     jsonb NOT NULL,
  text       text NOT NULL DEFAULT '',
  color      text,
  author     text NOT NULL DEFAULT 'user',
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX annotations_part_idx ON annotations (part_id);
```

**中文检索**：`zhparser` 作为**可选**扩展——阶段一不建全文索引，先用 `fields` 的精确/前缀查询；阶段二需要 FTS 时再补 `to_tsvector('zhcfg', ...)` 并给出降级方案（未装扩展时用 `simple`）。这避免首版被环境问题卡住（v0.x 的教训）。

### 3.6 前瞻字段（一次留够，避免未来改表）

这些字段阶段一不参与业务逻辑，但**现在就建列**，否则阶段二/三要回填历史数据：

| 字段 | 所在实体 | 为哪个阶段预留 | 阶段一的取值 |
|---|---|---|---|
| `Space.OwnerId` | Space | 阶段三 账号 | `'local'` |
| `Node.ExposeToMcp` | Node | 阶段二 MCP | `true` |
| `ContentType.ExposeToMcp` | ContentType | 阶段二 MCP | `true` |
| `Part.BlobRef` | Part | 阶段二 版本 | `NULL` |
| `Document.SchemaVersion` | Document | 阶段二 版本/diff | 创建时写入 |
| `ContentType.Version` | ContentType | 阶段二 版本/diff | `1` |

---

## 4. 数据流

### 4.1 创建空间（G1 的起点）

```
用户点击「创建空间」
  → API: POST /spaces {slug,name}
  → 校验 slug 格式 + owner 内唯一
  → Space {id, owner_id='local'} 落库
  → 自动建根 Node {path='/', parent_id=NULL, content_type_id=NULL}
  → 返回 Space + 根 Node
  → 空壳达成：此时库里无任何业务概念
```

### 4.2 定义内容类型（G2）

```
用户提交字段定义
  → 校验：key 合法标识符、类型在白名单、required 字段有默认值
  → ContentType.Version = 同一 slug 的 max(version) + 1
  → DefinitionJson 落库（旧版本行保留）
  → 返回新版本
```

### 4.3 写入文档（G2 + G3，唯一的写入收口）

这是全系统最重要的一条流，**阶段二的版本快照、阶段三的 MCP 写都复用它**。

```
调用方（页面 / REST / MCP）
        │
        ▼
WriteAsync(spaceId, command)
        │
        ├─ 1. 空间归属检查（G6）：owner_id 是否匹配
        ├─ 2. 实体存在性：Node / ContentType 是否属于该 space
        ├─ 3. Schema 校验（G2）：
        │      required → 类型 → 枚举/范围 → ref 存在性
        │      顺序固定，任一步失败即整体回滚
        ├─ 4. 乐观锁：command.ExpectedUpdatedAt 与库内比对
        ├─ 5. 在一个事务内：
        │      INSERT/UPDATE Document
        │      UPSERT Parts（按 ord）
        │      UPSERT Annotations（按 id）
        │      [阶段二] BuildTree → WriteCommit → 更新 Ref
        └─ 6. 提交事务 → 返回 DTO
```

**关键决策**
- **唯一收口**：阶段二的 MCP 和阶段三的页面都调 `WriteAsync`，不另写逻辑。这样 G4 的快照不可能是「三条路径三套实现」（v0.x 的痛点）。
- **校验顺序固定**：错误码可预测，Agent 能自动重试。
- **事务边界覆盖版本**：阶段二接入 Commit 时，Commit 与实体变更在同一事务——要么都成功，要么都不留（避免孤儿 Commit）。

### 4.4 移动目录（物化路径的代价）

```
MOVE node /a/b → /x/y
  → 校验：目标不在自身子树内（防环）
  → 计算新 path 前缀
  → 一条 UPDATE：
       SET path = new_prefix || substring(path from len(old_prefix)+1)
       WHERE space_id = ? AND (path = old OR path LIKE old || '/%')
  → 批量更新所有后代的 path（单条 SQL，不是 N 次）
  → 父节点 parent_id 单独更新
```

### 4.5 读取流

```
GET /spaces/{id}/tree        → 一次查全部 Node，内存组装嵌套 JSON（节点量 < 10k 足够）
GET /nodes/{id}/documents    → 按 space_id + node_id 过滤，Schema 字段原样返回
GET /documents/{id}          → Document + Parts + Annotations 一次 JOIN 加载
```

**规则**：所有查询强制带 `space_id`，由仓储层统一注入，不靠调用方记得传（G6 护栏）。

### 4.6 删除与级联

```
DELETE /spaces/{id}      → DB CASCADE 全清（需二次确认）
DELETE /nodes/{id}       → 服务层判断：有子节点/文档？
                            ├─ 有且无 force → 409 NODE_NOT_EMPTY
                            └─ 有且 force   → 递归删除后代后删自身
DELETE /content-types/{id} → 有文档引用？→ 409 TYPE_IN_USE
DELETE /documents/{id}   → CASCADE Parts + Annotations
```

---

## 5. 阶段一：底层数据库 CRUD（本次详细实施范围）

### 5.1 技术栈与项目结构

```
src/
  Episteme.Core/          纯领域层，零 NuGet 依赖（实体、Schema 校验逻辑、ID 生成）
    Entities.cs           6 个阶段一实体
    SchemaValidator.cs    DefinitionJson 的字段校验
    Ids.cs                sp_/nd_/ct_/doc_/pt_/an_ 前缀生成
  Episteme.Infrastructure/ EF Core 持久化层（依赖 Core，不依赖 Web）
    EpistemeDbContext.cs  DbSet + OnModelCreating
    Configurations/       每个实体一个 IEntityTypeConfiguration<T>
    Repositories/         ISpaceRepository / INodeRepository / ...
    Migrations/           dotnet ef 生成，纳入版本控制
  Episteme.Api/           阶段二接入；阶段一仅提供最小宿主用于跑迁移与冒烟
```

**依赖方向**：`Api → Infrastructure → Core`，箭头不反向（H1）。Core 里不会出现 `Microsoft.EntityFrameworkCore` 命名空间。

### 5.2 EF Core Code First 配置要点

```csharp
// EpistemeDbContext.cs（节选，示意配置意图）
protected override void OnModelCreating(ModelBuilder b)
{
    b.ApplyConfigurationsFromAssembly(typeof(EpistemeDbContext).Assembly);

    // 所有 jsonb 字段统一映射；字符串存 JSON，避免引入额外 POCO
    b.Entity<ContentType>().Property(x => x.DefinitionJson).HasColumnType("jsonb");
    b.Entity<Document>().Property(x => x.FieldsJson).HasColumnType("jsonb");
    b.Entity<Document>().Property(x => x.TagsJson).HasColumnType("jsonb");
    b.Entity<Part>().Property(x => x.MetaJson).HasColumnType("jsonb");
    b.Entity<Annotation>().Property(x => x.AnchorJson).HasColumnType("jsonb");

    // 所有 UTC 时间统一转换器（规避 Npgsql 对 Kind 的严格要求）
    foreach (var entity in b.Model.GetEntityTypes())
        foreach (var p in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
            p.SetValueConverter(UtcConverter);
}
```

**关键配置约定**

| 项 | 约定 |
|---|---|
| ID 生成 | 应用层 `Ids.New(prefix)`，EF 不负责 |
| 表名 | 复数小写（`spaces` / `nodes` / ...），在 Configuration 里显式指定 |
| jsonb | 统一 `HasColumnType("jsonb")`，读写用 `Jsonx` 帮助类 |
| 删除行为 | 按 §3.4，全部显式声明，不依赖 EF 默认 |
| 并发 | 阶段一用 `ExpectedUpdatedAt` 应用层比对；阶段二可切 Npgsql `xmin` 令牌 |
| 迁移 | `dotnet ef migrations add X --project .../Infrastructure --startup-project .../Api`，产物入库 |

### 5.3 仓储接口（CRUD 契约）

```csharp
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<T>> ListAsync(string spaceId, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task RemoveAsync(T entity, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

// 特定仓储只加领域语义，不重复通用 CRUD
public interface ISpaceRepository : IRepository<Space>
{
    Task<Space?> GetBySlugAsync(string ownerId, string slug, CancellationToken ct = default);
}
public interface INodeRepository : IRepository<Node>
{
    Task<List<Node>> GetDescendantsAsync(string spaceId, string pathPrefix, CancellationToken ct = default);
    Task<bool> HasChildrenAsync(string nodeId, CancellationToken ct = default);
}
```

### 5.4 CRUD 接口清单（阶段一 REST）

| 方法 | 路径 | 说明 | 关键校验 |
|---|---|---|---|
| POST | `/api/v1/spaces` | 创建空间 + 根节点 | slug 格式、owner 内唯一 |
| GET | `/api/v1/spaces` | 列出本人空间 | owner_id 过滤 |
| GET | `/api/v1/spaces/{id}` | 空间详情 | 归属检查 |
| DELETE | `/api/v1/spaces/{id}` | 删除空间（二次确认） | 归属检查 |
| POST | `/api/v1/spaces/{id}/types` | 新建内容类型 | key 白名单、required 默认值 |
| GET | `/api/v1/spaces/{id}/types` | 类型列表（含版本） | |
| PUT | `/api/v1/types/{id}` | 更新类型（版本 +1） | 旧版本保留 |
| DELETE | `/api/v1/types/{id}` | 删除类型 | TYPE_IN_USE 检查 |
| POST | `/api/v1/spaces/{id}/nodes` | 新建目录 | path 唯一、父存在 |
| PUT | `/api/v1/nodes/{id}/move` | 移动目录 | 防环、批量改 path |
| DELETE | `/api/v1/nodes/{id}` | 删除目录 | NODE_NOT_EMPTY / force |
| GET | `/api/v1/spaces/{id}/tree` | 目录树 | |
| POST | `/api/v1/nodes/{id}/documents` | 创建文档 | Schema 校验全通过 |
| PUT | `/api/v1/documents/{id}` | 更新文档 | Schema + 乐观锁 |
| GET | `/api/v1/documents/{id}` | 读取文档（含 Parts/Annotations） | |
| DELETE | `/api/v1/documents/{id}` | 删除文档 | |
| POST | `/api/v1/documents/{id}/parts` | 新增部件 | kind 白名单、ord 唯一 |
| PUT | `/api/v1/parts/{id}` | 更新部件 | |
| POST | `/api/v1/parts/{id}/annotations` | 新增标记 | 锚点结构合法 |
| DELETE | `/api/v1/annotations/{id}` | 删除标记 | |

**统一错误码**：`BAD_REQUEST` / `NOT_FOUND` / `FORBIDDEN` / `CONFLICT` / `SLUG_EXISTS` / `PATH_EXISTS` / `NODE_NOT_EMPTY` / `TYPE_IN_USE` / `SCHEMA_INVALID` / `VERSION_CONFLICT`。

### 5.5 阶段一验收标准（可测）

1. `dotnet ef migrations add InitialCreate` 成功生成迁移，`dotnet ef database update` 在空 PostgreSQL 上建出 6 张表。
2. 全新库启动后，除「创建空间」外无任何业务数据（G1）。
3. 创建一个 `paper` 类型（3 字段）和两个不同结构的类型，写入文档时字段按各自 Schema 校验，错误字段被拒（G2）。
4. 一份文档挂 `md` + `code` 两个 Part，读取时两个都在，互不覆盖（G3）。
5. 移动 `/a/b` 到 `/x/y` 后，其所有后代 path 前缀同步更新（§4.4）。
6. 有子节点的目录删除返回 409；有文档引用的类型删除返回 409。
7. 所有查询带 `space_id`，用另一 owner 的 ID 访问返回 FORBIDDEN（G6 占位）。
8. `ExpectedUpdatedAt` 不匹配时返回 `VERSION_CONFLICT`，且不产生部分写入。
9. Core 项目编译时不引用任何 EF Core / ASP.NET 程序集（H1）。
10. 重复执行同一 Migration 不报错；`database update` 幂等。

---

## 6. 阶段二：API 与 MCP（路标 + 契约边界）

> 本阶段**不在首版详细设计范围**，只锁定契约边界，防止阶段一的字段设计把路堵死。

### 6.1 REST 层约定

- 阶段一的 CRUD 接口原样升级为正式 API，加统一错误契约 `{ code, message, traceId }`。
- 所有写请求必带 `Idempotency-Key`，服务端在 Commit 上做唯一约束（复用 v0.x 已验证的设计）。

### 6.2 MCP 层契约

| 项 | 契约 |
|---|---|
| 传输 | 仅 Streamable HTTP `POST /mcp`，无 SSE / stdio |
| 凭据 | 登录 JWT 仅用于 `/api/*`；`epm_` 长期令牌仅用于 `/mcp`；混用 `403 CREDENTIAL_SCOPE_MISMATCH` |
| 静态工具 | `tree` `read` `search` `create_node` `create_document` `update_document` `append_part` `annotate` `log` `diff` `revert` |
| 动态工具 | 按 `ContentType.ExposeToMcp` + `FieldDef.exposeToMcp/mcpWritable` 派生 `query_{slug}` / `create_{slug}` / `update_{slug}` |
| 工具版本 | `Space.ToolsVersion` 单调递增，变更推 `notifications/tools/list_changed`（阶段一字段已留） |
| 写入收口 | MCP 写**必须**调阶段一的 `WriteAsync`，不得另起写路径 |

### 6.3 版本落点（G4）

- 阶段一 `Space.OwnerId`、`Part.BlobRef`、`Document.SchemaVersion`、`ContentType.Version` 已留，阶段二直接接 Blob/Tree/Commit/Ref。
- 合并只做快进覆盖，不承诺三方合并；导出只做线性清单，不承诺真 git 仓库（§3.3）。

### 6.4 阶段二退出条件

- 任意写（REST 或 MCP）产生且仅产生一个 Commit；重复幂等键不产生第二个 Commit。
- 新建内容类型后，Agent 无需重启即看到新工具。
- 对任一 Commit 回滚后，文档内容与目标快照一致。

---

## 7. 阶段三：页面（路标）

- **渲染方式**：零手写业务页面；通用渲染器读 `ContentType.DefinitionJson` 生成列表/表单/筛选。
- **四类阅读器**：`md`/`code` 可编辑，`pdf`/`web` 只读 + 标记；统一 `mount/read/edit/annotate/snapshot` 契约。
- **抓取安全**：Web/PDF 走独立 worker + SSRF 白名单（禁内网、禁非 80/443 端口、禁重定向穿透）——v0.x 已把端口校验移到 DNS 之前，这个修复要带上。
- **视觉**：沿用 `design/tokens.json` 四套浅色主题，原型与实现同源。
- **阶段三退出条件**：G1/G2 在浏览器可操作；四类媒介至少各生成一条标记且刷新后锚点仍在。

---

## 8. 实施顺序总表

| 序 | 步骤 | 产出 | 依赖 |
|---|---|---|---|
| 1 | 建 `Episteme.Core` 实体 + ID + SchemaValidator | 可编译的领域层 | 无 |
| 2 | 建 `Episteme.Infrastructure` + DbContext + 6 个 Configuration | 可编译的持久化层 | 1 |
| 3 | `migrations add InitialCreate` → `database update` | 6 张真实表 | 2 |
| 4 | 6 个仓储 + `WriteAsync` 服务 | 可单测的 CRUD | 3 |
| 5 | 最小 API 宿主 + §5.4 接口 | 可冒烟的 REST | 4 |
| 6 | 跑 §5.5 十条验收 | 阶段一完成 | 5 |
| 7 | 阶段二：鉴权 + MCP + 版本对象 | | 6 |
| 8 | 阶段三：通用渲染器 + 阅读器 | | 7 |

---

## 9. 明确不做（首版）

- 不做客户端：无 Avalonia / Android / 桌面，MCP 只有 Streamable HTTP。
- 不做三方合并、GC、真 git 仓库导出——规格不承诺，代码不实现（v0.x 教训）。
- 不做运行时动态建表；字段一律 jsonb。
- 不做 CRDT 实时协同；阶段一用 `ExpectedUpdatedAt` 乐观锁。
- 不内置任何业务枚举；「素材/热点/报告/论文」只能出现在 Pack 或用户数据里。
- 不做跨 Space 的 JOIN 查询。
- 不引入 AGPL/BSL 许可的组件作为内核依赖。

---

## 10. 待确认项

| # | 问题 | 本版默认 |
|---|---|---|
| 1 | 阶段一是否需要登录，还是先单用户（`owner_id='local'`） | **先单用户**，阶段三接账号 |
| 2 | `ref` 字段是否允许跨 Space | 阶段一限制同 Space |
| 3 | Schema 字段类型首版 8 种是否够 | 够；不够再加，走 Migration |
| 4 | 中文全文检索何时启用 | 阶段二；未装 zhparser 时降级 simple |
| 5 | Migration 产物是否入库 | **是**（H2） |
