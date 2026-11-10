# Episteme · 知书｜通用文档操作系统规格说明书

| 项 | 内容 |
|---|---|
| 产品名 | **Episteme**（ἐπιστήμη，希腊语「经论证的知识」） · 中文 **知书** |
| 文档状态 | **v0.1 · 平台化规格与高保真原型阶段**（本轮不改动现有可运行代码） |
| 日期 | 2026-11-09 |
| 上游基线 | 商业短篇创作工作台 `SPEC.md` v3.2（.NET 10 / Blazor / ASP.NET Core / Avalonia / PostgreSQL / Docker，39 项接口冒烟 + 22 项浏览器交互回归） |
| 需求来源 | 用户：「把这个软件更抽象一层，做成一个通用文档管理的空壳子」 |
| 已确认决策 | ① 演进式抽象（抽通用内核，Workbench 降为模板）② 本轮交付规格 + 高保真原型 ③ 对外提供**动态 MCP Server** ④ 内容寻址快照 DAG + Web 优先阅读器 |
| 交付物 | `episteme/SPEC.md`、`episteme/docs/01–06`、`episteme/design/`、`episteme/prototype/index.html` |

---

## 0. 一句话定位

> **知书不是又一个笔记软件，而是一个「没有预设业务语义、由用户定义目录与内容、可被 AI Agent 直接读写编辑、每次改动都可像 git 一样追溯回滚」的通用文档内核。**

现有「商业短篇创作工作台」不再是产品本身，而是安装在这个内核上的**第一套模板（Pack）**。

---

## 1. 为什么要抽象这一层

现有工作台把业务写死在代码里：11 个固定素材模块（M1–M11）、固定字段、固定的 12 个 Razor 页面、固定菜单。它能很好地服务「8000–12000 字流量短篇」，但换一个领域（论文库、法条库、代码知识库、竞品情报库）就要重写一遍页面和数据表。

抽象后，**变的只有数据（Schema + 目录 + 模板），不变的是内核**：

| 层 | 内容 | 变更频率 |
|---|---|---|
| **L2 模板层 Pack** | 目录树、内容类型、视图、提示词、示例数据；`workbench` 只是其中一个 | 用户随时安装 / 导出 / 自建 |
| **L1 媒介层 Viewer** | Markdown / Code / PDF / Web 四类一等公民阅读器与标记 | 内核稳定，可插件扩展 |
| **L0 通用内核 Core** | 空间、目录树、动态 Schema、文档与 Part、版本 DAG、动态 MCP、权限与审计 | 极少变 |

**空壳原则（Empty by default）**：首次启动零业务概念、零预置目录、零预置字段。界面上只有一个「创建你的第一个空间」。任何「素材」「热点」「报告」字样都不出现在内核里。

---

## 2. 六条设计原则

1. **Schema 即契约**：目录绑定「内容类型」，内容类型定义字段。Schema 一改，列表列、表单控件、检索索引、导出结构、MCP 工具签名**同步重建**，无需发版。
2. **文档是载体，不是格式**：`Document = 元数据 + 1..N 个 Part`。Markdown 正文、代码片段、PDF 原文、网页快照可以同时挂在**同一份文档**下。四类阅读器是 Part 的渲染器，不是四种互不相干的实体。
3. **版本是一等公民**：所有写操作——无论来自界面、REST 还是 MCP Agent——都生成同一个 Commit。AI 的每一次编辑都可追溯、可 diff、可 revert。
4. **MCP 动态生成**：`tools/list` 依据当前空间目录树与 Schema 实时生成；用户新建内容类型后，Agent 立刻获得对应的 `create_*` / `query_*` / `update_*` 工具，无需重启服务。
5. **Agent 改动默认可隔离**：MCP 写操作可指定 `branch`，在隔离分支上工作，人工审阅后合并；配合 `reason` / `agent_id` / `expectParent` 乐观锁，避免 AI 静默覆盖人类内容。
6. **复用而非重写**：JWT / RBAC / API Key + HMAC-SHA256 / Nonce / 幂等键 / 审计日志 / 定时调度 / 通知通道 / 三端 / Docker / `design/tokens.json` 全部沿用；新增的是抽象层，不是第二套系统。

---

## 3. 概念模型（速览）

```
Workspace 工作区（租户）
└── Space 空间（独立版本 DAG 根 / 独立权限 / 独立 MCP 端点）
    ├── Node 目录节点（物化路径树，可绑定 ContentType）
    │   └── Document 文档（title + fields[jsonb] + Parts[]）
    │       ├── Part(md | code | pdf | web | attachment)
    │       └── Annotation 标记（高亮 / 批注，带稳健锚点）
    ├── ContentType 内容类型（版本化 Schema：FieldDef[]）
    ├── Commit 提交（parents[] + treeHash + author + source + reason）
    │   └── Tree 树快照（path → docId/fieldsHash/partHashes）
    ├── Ref 引用（branch / tag → commit）
    └── Template / Pack（声明式 JSON：目录 + Schema + 视图 + 提示词）
```

完整实体定义、字段类型、约束与 DDL 见 `docs/01-概念模型与动态Schema.md`。

**关键抽象点**：`Node` 既可以是纯目录，也可以是「绑定了内容类型的集合」（类似 git 目录 + 数据库表的合体）。目录结构本身就是 Agent 可操作的资源树。

---

## 4. 四项核心能力

### 4.1 用户自定义目录与内容
- 可视化目录树：新建 / 重命名 / 移动 / 删除 / 折叠，路径物化、支持无限层级。
- Schema Builder：12 种字段类型（`text` `longtext` `markdown` `code` `number` `date` `boolean` `select` `multi_select` `tags` `url` `ref` `json` `file` `user`），逐字段配置必填、唯一、索引、MCP 暴露、校验规则、枚举选项、关联目标。
- Schema 版本化 + 迁移策略（新增字段填默认值、删除字段归档保留、类型变更须显式转换）。
- 列表 / 表单 / 筛选 / 排序全部由 Schema 驱动渲染，不再有手写页面。

### 4.2 动态 MCP Server
- 传输：Streamable HTTP `/mcp`（同容器暴露）+ 桌面端本地 stdio；沿用现有 API Key + HMAC / JWT 鉴权。
- 静态内核工具：`list_spaces` `tree` `read` `search` `create_node` `move_node` `create_document` `update_document` `append_part` `annotate` `commit` `log` `diff` `revert` `branch` `define_content_type` 等。
- 动态派生工具：按每个 `exposeToMcp` 的 ContentType 生成 `query_{type}` / `create_{type}` / `update_{type}`，其 JSON Schema 直接由 FieldDef 映射。
- Resources：`episteme://{space}/{path}/{docId}[/part/{kind}]`。
- `toolsVersion` 单调递增，Schema / 目录 / 权限变更时推送 `notifications/tools/list_changed`。
- 完整协议见 `docs/03-动态MCP-Server协议.md`，机器可读契约见 `design/mcp/`。

### 4.3 git 式版本管理（内容寻址快照 DAG）
- `Blob`（sha256，去重）→ `Tree`（路径→哈希快照）→ `Commit`（parents、message、author、source、reason）→ `Ref`（branch / tag）。
- 支持：log、diff（字段级 + Part 文本级 LCS + 二进制/快照差异）、blame、branch、tag、revert（生成反向提交，不改写历史）、三方合并（字段级自动 + Part 级冲突标记）、GC。
- 选择「git 语义 + PostgreSQL 内容寻址」而非真 git 仓库：保证事务一致性、行级权限、在线并发编辑与 MCP 写原子性；代价是不能直接用外部 git 客户端（提供导出为 git 仓库的补偿能力）。
- 完整模型与 DDL 见 `docs/02-git式版本模型.md`。

### 4.4 四类阅读器 / 编辑器
统一 Viewer 契约 `mount / read / edit / annotate / snapshot`，所有写回落到 Part + Commit：

| 媒介 | 阅读 | 编辑 | 标记 | 版本粒度 |
|---|---|---|---|---|
| Markdown | GFM / 表格 / Mermaid / 沉浸模式 | 双栏源码 + 预览，front-matter 映射 Schema | 行内批注 | 文本 diff |
| Code | Monaco，语言识别、大纲、行号 | 编辑、只读切换、可选 LSP | 行级批注、blame 联动 | 逐行 diff |
| PDF | pdf.js 文本层渲染 | 不可编辑 | 高亮 / 下划线 / 批注，`page+rect+textQuote` 稳健锚点 | 快照 + 标注集差异 |
| Web | URL 抓取 → Readability 抽取 → 阅读模式 / 原文模式 | 不可编辑，可重抓 | 划词高亮批注，DOM path + TextQuote | HTML / 文本双快照 |

SSRF 防护（承接上一轮开源调研结论）：Web / PDF 抓取走**独立网络隔离 worker**、域名白名单、禁内网地址与重定向穿透、大小与超时上限。

---

## 5. 与现有 Workbench 的关系（演进路径）

| 现有 | Episteme 中的形态 |
|---|---|
| 11 大素材模块 M1–M11 | Pack `workbench` 的 11 个 Node + 11 个 ContentType |
| 每日热点 50 条 | `Hotspot` 类型 + 定时导入任务写入 `每日热点/{date}` 节点 |
| 两大固定 AI 口令 | Pack 内的 MCP Prompts / 视图配置 |
| 拆文复盘、报告、订阅 | Pack 视图 + 现有调度 / 通知内核 |
| Agent `/api/v1` + HMAC | 保留并标记 deprecated；新增 MCP Server，二者共享同一 Application 层 |
| 12 个手写 Razor 页面 | Schema 驱动的通用列表 / 表单 / 阅读器页面 |
| PostgreSQL 现有 16 张表 | 原表保留；新增通用表；迁移脚本把旧数据导成 Node / Document / 首个 Commit「import from workbench v3.2」 |

**兼容红线**：现有 `/api/v1` 契约、`author/demo1234` 演示数据、39 项冒烟、22 项交互回归、Docker 部署方式在 P4 之前必须零回归。详细映射与分阶段迁移见 `docs/05-架构演进与Workbench模板化.md`。

---

## 6. 技术栈（沿用现有，不引入第二套体系）

| 层 | 选型 | 变化 |
|---|---|---|
| 运行时 | .NET 10 LTS / ASP.NET Core | 不变 |
| Web UI | Blazor Web App（Interactive Server） | 增加 Schema 驱动渲染 + 四类 Viewer |
| 桌面 / 移动 | Avalonia 12（共享 XAML） | 通用树 + Schema 表单 + 浏览/审阅；富阅读器 Web 优先 |
| 数据 | PostgreSQL 17/18 + `jsonb` + GIN + `zhparser` 中文分词 | 新增 Blob/Tree/Commit/Ref/Part/Annotation/Schema 表 |
| 缓存 / 幂等 / 广播 | Redis 7（现为可选 profile，平台化后转常用） | `toolsVersion` 广播、Nonce 跨实例 |
| 对象存储 | MinIO / COS（现为可选） | Blob 实体存储，平台化后转常用 |
| 版本 | 自研内容寻址 DAG | 新增；不引入 LibGit2Sharp |
| MCP | 官方 `ModelContextProtocol` C# SDK（实现阶段锁定版本） | 新增 Streamable HTTP 端点 |
| 前端阅读器 | CodeMirror 6 / Monaco、pdf.js、Readability | 新增，按需懒加载 |
| 部署 | Docker Compose（api + postgres + 可选 redis/minio + 抓取 worker） | 新增抓取 worker 与网络隔离 |
| 视觉 | `design/tokens.json` 四套浅色主题 | 不变，原型与实现同源 |

---

## 7. 交付物清单（本轮）

| 文件 | 作用 |
|---|---|
| `episteme/SPEC.md` | 本文：定位、原则、概念模型、决策、路线、验收 |
| `episteme/docs/01-概念模型与动态Schema.md` | 实体、字段类型、Schema 版本与迁移、动态渲染与索引、DDL |
| `episteme/docs/02-git式版本模型.md` | Blob/Tree/Commit/Ref、diff/merge/revert/blame/GC、DDL、与真 git 取舍 |
| `episteme/docs/03-动态MCP-Server协议.md` | 传输与鉴权、静态/动态工具、Resources、版本广播、安全与审计、错误码 |
| `episteme/docs/04-四类阅读器与统一Part契约.md` | Viewer 契约、四类规格、锚点模型、多 Part 组合、SSRF 隔离 |
| `episteme/docs/05-架构演进与Workbench模板化.md` | 分层改造、Pack 格式、数据迁移、兼容红线、P0–P5 路线 |
| `episteme/docs/06-API权限与验收标准.md` | REST 全表、权限矩阵、可测验收项、非功能要求、风险边界 |
| `episteme/design/schemas/*.json` | 通用文档库 Schema、Workbench Pack（11 类型）、Paper/Code/Web 示例 |
| `episteme/design/mcp/*.json` | `tools/list` 动态生成前后对照、Resources 样例、调用与回滚示例 |
| `episteme/prototype/index.html` | **高保真可交互原型**：单文件零依赖、四套主题、移动端适配 |
| `episteme/tests/ui_test.js` | 原型真实浏览器交互回归脚本 |

---

## 8. 分阶段路线

| 阶段 | 内容 | 退出条件 |
|---|---|---|
| **P0（本轮）** | 平台规格 + 高保真原型 + Schema/MCP 契约样例 | 原型全部控件可交互、回归全绿、方向经确认 |
| P1 通用内核 | Space / Node / ContentType / Document / Part + Schema 驱动 CRUD REST | 空启动零业务概念；Schema 改后列表与表单即时变化 |
| P2 版本 + MCP | 内容寻址 DAG + 动态 MCP Server + tools 版本广播 | 任意写产生 Commit；Agent 新建类型后工具自动出现；revert 一致 |
| P3 阅读器 | Markdown / Code 优先，PDF 标记 / Web 快照随后 | 四类均可读、可标注、标记进入版本 |
| P4 Pack 化 | `workbench` Pack + 旧数据迁移 + 旧 API deprecated | 安装 Pack 后出现 11 目录；39 项冒烟与 22 项交互零回归 |
| P5 三端与生态 | Avalonia 通用树 / 模板导入导出 / 模板目录 | 桌面与 Android 可浏览与审阅；Pack 可导入导出 |

---

## 9. 验收标准（可测，非描述性）

1. 全新实例启动后，界面**不出现任何业务专有名词**（素材 / 热点 / 拆文 / 报告），仅有「创建空间」。
2. 用户新增一个内容类型并加 3 个字段后，列表列、新建表单、筛选器、`tools/list` 中对应工具签名**在同一会话内**同步变化，无需重启。
3. 界面、REST、MCP 三条写入路径产生的 Commit 可互相 diff；对任一 Commit 执行 revert 后内容与该 Commit 父版本一致。
4. Agent 在 `agent/xxx` 分支的写入不影响 `main`；合并时字段级自动合并、Part 冲突显式标记。
5. Markdown / Code 可编辑并产生文本 diff；PDF / Web 可生成至少一条高亮与一条批注，刷新后锚点仍定位正确；四类标记均进入版本历史。
6. 安装 `workbench` Pack 后自动生成 11 个目录与对应 Schema；旧库迁移后原有演示数据零丢失；现有 39 项接口冒烟与 22 项浏览器交互回归不退化。
7. MCP 写操作的每次调用都有审计记录（agent_id、reason、commitId、幂等键），重复请求不产生第二个 Commit。
8. 抓取 worker 无法访问内网地址；越权工具调用被拒绝；Schema 中标记 `exposeToMcp=false` 的字段不出现在任何工具签名里。

---

## 10. 明确边界与风险

- **本轮不写实现代码**：现有容器与 Workbench 保持可运行状态，规格确认后再进入 P1。
- MCP SDK 具体版本、CodeMirror/Monaco 取舍、pdf.js 版本在实现阶段锁定，本轮只定义契约。
- 中文全文检索采用 PostgreSQL + `zhparser`，需真实语料压测；本轮未做性能实测。
- 三方合并的冲突处理、Blob GC 策略、大文件（>100 MB PDF）流式读取属于 P2/P3 细化项。
- 抓取外部网页 / PDF 的版权与合规由使用者负责；产品侧提供原文链接留存与来源标注。
- 上一轮开源调研结论仍然有效：不引入 AGPL/BSL 组件作为内核依赖；MCP 工具不原生支持 HMAC，将在 Episteme 网关层统一补齐。

---

## 11. 待确认项

| # | 问题 | 建议默认 |
|---|---|---|
| 1 | Space 是否支持跨空间引用（`ref` 字段跨 Space） | P1 先限制同 Space，P2 放开 |
| 2 | 是否需要多人实时协同编辑（CRDT/OT） | 暂不做，采用 `expectParent` 乐观锁 + 冲突提示 |
| 3 | 模板市场是否需要服务端目录 | P5 先做本地 JSON 导入导出 |
| 4 | 现有弱口令 `author/demo1234` 是否随平台化一并加固 | **建议加固**，但需同步登录页 / 冒烟脚本 / 客户端 |
| 5 | MCP 是否同时提供 stdio 供桌面端本地调用 | 建议提供，便于离线与低延迟场景 |
