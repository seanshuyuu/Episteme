# 05 · 架构演进与 Workbench 模板化

> 决策已确认：**演进式抽象**。现有产品不推倒重来，Workbench 降级为 Episteme 上的第一套 Pack。
> 兼容红线：P4 之前，现有 `/api/v1`、演示数据、39 项接口冒烟、22 项浏览器交互回归、Docker 部署方式**零回归**。

---

## 1. 分层改造

### 1.1 现在（`v3.2`）

```
Workbench.Core        Domain(Entities/ModuleCatalog) + Data + Security + Services
Workbench.Server      Program / Minimal API / 12 个手写 Razor 页面 / Scheduler
Workbench.UI.Avalonia ViewModel + XAML（写死 11 模块的菜单与表单）
Workbench.Desktop / Android
PostgreSQL 16 张表，字段写死在实体里
```

### 1.2 目标（`Episteme`）

```
Episteme.Core             ← 新增：通用内核
  Domain/                  Space / Node / ContentType / FieldDef / Document / Part
                           / Annotation / Blob / Tree / Commit / Ref / Pack
  Schema/                  Schema 校验、迁移策略、动态渲染描述、索引 DDL 生成
  Versioning/              Blob/Tree/Commit 服务、diff、merge、revert、blame、GC
  Mcp/                     工具注册、动态派生、toolsVersion、Resources、Prompts
  Pack/                    Pack 解析、安装、卸载、导出、示例数据导入
  Security/               沿用 JwtService / HmacSigner / PasswordHasher（下沉共享）
  Data/                   WorkbenchDbContext → 拆 EpistemeDbContext + 旧 Context 并存

Episteme.Server           ← 由 Workbench.Server 演进而来
  /api/v1/**              现有端点原样保留（deprecated + Sunset 头）
  /api/v1/episteme/**     通用 REST（见 06）
  /mcp                    Streamable HTTP MCP Server
  Components/             通用渲染器（列表 / 表单 / 阅读器 / 版本 / MCP 面板）
                          + 兼容旧路由的 Pack 视图

Packs/Workbench           ← 由现有业务代码降级而来
  pack.json                11 个 Node + 11 个 ContentType + 视图 + Prompts + 图标
  Jobs/                    每日热点导入（原 Hotspot 逻辑迁移为定时导入任务）
  Views/                   拆文对话、报告、订阅等复合视图
  Migration/               旧表 → 通用表的数据迁移脚本

Episteme.UI.Avalonia     ← 通用树 + Schema 驱动表单 + 浏览 / 标注审阅
Episteme.Desktop / Android
FetchWorker              ← 新增：独立网络隔离容器，处理 web/pdf 抓取
Docker Compose           api + postgres + redis（常用）+ minio（常用）+ fetch-worker
```

**关键变化**：`ModuleCatalog`（写死 11 模块）被 `pack.json` 取代；12 个手写 Razor 页面被 Schema 驱动渲染器取代；Avalonia 里写死的菜单变成动态目录树。

---

## 2. Pack 格式（声明式 JSON）

```json
{
  "id": "workbench",
  "name": "商业短篇创作工作台",
  "version": "1.0.0",
  "episteme": ">=0.1.0",
  "description": "8000–12000 字流量短篇创作全流程",
  "author": "内置",
  "license": "internal",
  "nodes": [
    { "path": "/M1-作品入库档案库",       "contentType": "work_archive",    "icon": "archive",     "group": "创作资产库" },
    { "path": "/M2-导语开篇钩子库",       "contentType": "opening_hook",    "icon": "zap",         "group": "创作资产库" },
    { "path": "/M3-赛道选题库",           "contentType": "track_topic",     "icon": "compass",     "group": "创作资产库" },
    { "path": "/M4-人设素材库",           "contentType": "character",       "icon": "users",       "group": "创作资产库" },
    { "path": "/M5-剧情结构节奏库",       "contentType": "plot_structure",  "icon": "git-branch",  "group": "创作资产库" },
    { "path": "/M6-拆文复盘库",           "contentType": "teardown",        "icon": "scissors",    "group": "创作资产库" },
    { "path": "/M7-金句对话库",           "contentType": "quote",           "icon": "message",     "group": "创作资产库" },
    { "path": "/M8-结局闭环模板库",       "contentType": "ending_template", "icon": "flag",        "group": "创作资产库" },
    { "path": "/M9-平台过稿合规自查库",   "contentType": "compliance",      "icon": "shield",      "group": "合规与素材" },
    { "path": "/M10-原创冲突素材储备库",  "contentType": "conflict_material","icon": "layers",     "group": "合规与素材" },
    { "path": "/M11-每日全网热点库",      "contentType": "hotspot",         "icon": "flame",       "group": "每日热点" },
    { "path": "/M11-每日全网热点库/每日", "contentType": "hotspot", "defaultPartKinds": ["web"] }
  ],
  "contentTypes": [ /* 见 episteme/design/schemas/workbench-pack.json */ ],
  "views": [
    { "name": "今日热点 Top50", "node": "/M11-每日全网热点库/每日", "sort": [{"field":"rank","desc":false}], "limit": 50 },
    { "name": "拆文复盘对话",   "type": "chat", "prompts": ["teardown_full"] }
  ],
  "prompts": [
    { "id": "teardown_full", "name": "全维度拆文", "template": "…" },
    { "id": "fact_blur",     "name": "事实虚化改编", "template": "…" }
  ],
  "jobs": [
    { "id": "daily_hotspots", "cron": "0 0 7 * * ?", "type": "import", "target": "/M11-每日全网热点库/每日", "limit": 50 }
  ],
  "sampleData": false
}
```

**Pack 是纯数据**：安装 = 在一个事务里创建 Node / ContentType + 生成一条 `source=import` 的 Commit + 更新 `toolsVersion`。卸载 = 反向操作（可选择保留用户文档）。Pack 可导出为 JSON 供团队复用，这是「用户自己定义目录和内容」的最高层抽象。

---

## 3. 原业务概念 → Pack 映射表

| 现有概念 | Pack 中的形态 | 迁移方式 |
|---|---|---|
| M1–M11 固定模块 | 11 个 Node，各绑定一个 ContentType | `pack.json` 声明 |
| 模块固定字段（如热点 rank / 原文链接 / 6 项转化） | ContentType 的 FieldDef[] | 逐字段映射，保留原值 |
| 每日热点 50 条 + 原文 / 原视频链接 | `Hotspot` 类型 + `daily_hotspots` 定时导入任务 | `hotspots` 表 → Document + `Part(web)` |
| 事实虚化改编 / 3 个标题模板 | Pack `prompts` + `views` | 模板文本迁到 Prompts，改动进版本 |
| M6 拆文复盘库对话窗口 | Pack 的 `chat` 复合视图 | 前端组件保留，数据源换通用 Document |
| 全维度拆文报告 / 一键归档 M1–M10 | Pack 视图 + 现有 `ReportEngine` / `Subscription` | 报告读指定 `ref` 快照（见 02 §8） |
| 两大固定 AI 口令 | Pack `prompts` | 数据化，不再写死在代码 |
| `/api/v1` Agent 接口 + HMAC | 保留 deprecated；MCP Server 为首选 | 共享 Application 层 |
| 12 个手写 Razor 页面 | 通用列表 / 表单 / 阅读器 + 少量 Pack 复合视图 | 逐步替换，旧路由 301 到 Pack 视图 |
| 现有 16 张 PG 表 | 只读保留一个发布周期，新表承接写入 | 迁移脚本 + 双读校验 |

---

## 4. 数据迁移（P4）

```
[1] 备份：pg_dump 全库 → 对象存储，记录校验和
[2] 建新表（DDL 见 01/02），不动旧表
[3] 创建 Space「创作工作台」，安装 workbench Pack
[4] 逐表迁移：
      library_items  → Document(node=/0X-…, fields=原字段, parts=[md])
      hotspots       → Document(node=/M11-每日全网热点库/每日/YYYY-MM-DD, fields, parts=[web(sourceUri)])
      report_instances / subscriptions / delivery_logs
                       → 沿用现有表（它们本就是通用调度能力，不迁）
      agent_tasks / agent_runs / api_keys / audit_logs
                       → 沿用现有表（内核能力，不迁）
[5] 生成首个 Commit：message = "import from workbench v3.2", source = import
[6] 双读校验：随机抽样 100 条，旧表与新表逐字段比对，差异为 0 才切流
[7] 切换：Pack 视图指向通用表；旧表置只读；旧 API 返回 deprecated 头但数据一致
[8] 观察一个发布周期后，旧表归档为备份，不物理删除
```

**回滚预案**：迁移在独立事务 / 独立 Space 中进行，任何一步失败只丢弃新 Space，旧系统完全不受影响。

---

## 5. 兼容策略

| 对象 | 策略 |
|---|---|
| `GET/POST /api/v1/**` | 原样保留，响应加 `Deprecation: true` + `Sunset` + `Link: </api/v1/episteme/…>` |
| 登录 `author / demo1234` | P4 前不变（**但平台化会放大弱口令风险，建议在 P1 一并加固**） |
| `tests/smoke_test.py` 39 项 | 每次改造后必须全绿，作为回归闸门 |
| `tests/ui_test.js` 22 项 | 同上；Pack 视图替换页面时逐项迁移断言 |
| Docker Compose | 现有 `api + postgres` 组合继续可用；新增组件走 profile，不破坏 `up -d --build` 一键启动 |
| `design/tokens.json` | 四套主题值不变；新增组件只读令牌，不引入第二套配色 |
| 桌面 / Android | 现有 Avalonia 工程渐进改造；`WORKBENCH_API` 环境变量保持兼容，新增 `EPISTEME_API` 别名 |

---

## 6. 分阶段实施与退出条件

| 阶段 | 主要工作 | 退出条件 | 回归闸门 |
|---|---|---|---|
| **P0** | 规格 + 原型 + Schema/MCP 契约样例（本轮） | 原型全交互回归通过、方向确认 | 现有容器不受影响 |
| **P1** | `Episteme.Core` 通用实体 + Schema 引擎 + 通用 REST + 通用渲染器 | 空实例零业务概念；加字段后列表/表单即时变化 | 39 + 22 全绿 |
| **P2** | 版本 DAG + 动态 MCP Server + toolsVersion 广播 + Redis | 三条写路径同一 Commit；revert 一致；工具自动派生 | 39 + 22 + 新增 MCP 契约测试 |
| **P3** | Markdown / Code → PDF 标记 → Web 快照 + fetch-worker | 四类可读可标注；标记进版本；SSRF 用例被拦截 | 同上 + 阅读器交互回归 |
| **P4** | `workbench` Pack + 数据迁移 + 旧 API deprecated | 安装后出现 11 目录；抽样双读差异 0 | 39 + 22 零退化 |
| **P5** | Avalonia 通用树 / Pack 导入导出 / 模板目录 / stdio MCP | 桌面与 Android 可浏览审阅；Pack 可导出复用 | 三端冒烟 |

---

## 7. 新增部署拓扑

```yaml
# deploy/docker-compose.yml（演进后，节选）
services:
  api:            # 现有容器，新增 /mcp 与 /api/v1/episteme
    ports: ["${WORKBENCH_BIND_IP:-127.0.0.1}:${WORKBENCH_HTTP_PORT:-5080}:8080"]
    depends_on: [postgres, redis]
  postgres:       # 不变，新增通用表
  redis:          # 由 profile 可选 → 常用（Nonce / 幂等 / toolsVersion 广播 / SignalR 背板）
  minio:          # 由 profile 可选 → 常用（Blob）
  fetch-worker:   # 新增，独立网络，仅出网，不与 postgres/api 同网段
    networks: [egress-only]
```

`fetch-worker` 与 `api` 之间只通过受控队列传递 URL 与结果，不共享数据库凭据（§03 §6.4）。

---

## 8. 风险与取舍

| 风险 | 影响 | 缓解 |
|---|---|---|
| 通用渲染器能力不足以表达拆文对话等复合视图 | Pack 特例变成硬编码回潮 | 明确允许 Pack 注册自定义视图组件，但**数据仍走通用 Document** |
| 旧表与新表双写期数据漂移 | 报告 / Agent 读到不一致 | 迁移期只写通用表，旧表只读 + 双读校验后立即切流 |
| Avalonia 富 PDF/Web 渲染成本高 | 桌面端体验落后 Web | 桌面端用 WebView 承载 / 跳 Web 端；标注审阅做原生 |
| `jsonb` 动态字段查询性能 | 大库列表变慢 | 按 `FieldDef.indexed` 自动建表达式索引；超大规模再引入 Meilisearch（主体 MIT，禁用 EE，承接上一轮调研结论） |
| 平台化放大弱口令风险 | 局域网可无门槛登录 admin | **建议 P1 同步加固**，见 SPEC §11 待确认项 4 |
| 引入 MCP SDK / 前端大包 | 构建与首屏变慢 | SDK 版本实现阶段锁定；阅读器按 kind 懒加载 |

---

## 9. 明确不做

- 不在 P0–P4 重写现有 Avalonia 全部界面。
- 不删除现有 16 张表 / 旧 API（至少一个发布周期内）。
- 不把 Pack 做成可执行插件（Pack 是纯数据，不含服务端代码，避免供应链风险）。
- 不引入 AGPL / BSL 组件作为内核依赖（承接 `docs/07` 许可证红线结论）。
