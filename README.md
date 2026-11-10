# Episteme · 知书

> 通用文档操作系统空壳：用户自定义目录与内容，动态 MCP 读写编辑，git 式版本管理，Markdown / Code / PDF / Web 四类阅读器。
> 由「商业短篇创作工作台」抽象一层而来；工作台降级为本平台的**第一套模板（Pack）**。

**当前阶段：v0.2 · P1 通用内核 + P2 版本 DAG / 动态 MCP 已落地为可运行实现（Docker 部署）**；v0.1 规格与高保真原型保留为设计基线。实现范围、回归结果与明确边界见 `docs/07-P1P2实现与验收报告.md`。

> **工作区清理（2026-11-10）**：按用户要求，旧「商业短篇创作工作台」的源码、文档、原型、部署、测试，以及 Docker 数据卷与镜像已全部清除；本工作区现仅保留 `episteme/` 与 `.workbuddy/`。完整备份与恢复步骤见 `/mnt/work/backups/workbench-cleanup-20261110/RESTORE.md`。
>
> 因此 `SPEC.md`、`docs/05`、`docs/06` 中关于「复用现有 Workbench 代码 / `Workbench.slnx` / 39 项接口冒烟 / 22 项交互回归 / `/api/v1` 零回归」的表述，现为**设计阶段的历史依据**，不再对应工作区内可执行的产物；P1 起按规格从零落地内核。四套主题令牌已从原 `design/tokens.json` 内置为 `episteme/design/tokens.json`，原型仍可独立打开、无需外部依赖。
>
> 清理后验证：jsdom 独立加载冒烟 **6/6 PASS**（0 外部资源引用、状态接口可用、真实点击可改变状态）、原型 JS 语法通过。完整 Playwright 28 项回归本轮因宿主 shell 的 4 GB 虚拟内存硬限制无法启动 Chromium（环境问题，非原型缺陷）；`tests/ui_report.md` 保留 11-09 的 28/28 历史结果。

---

## 运行实现版本（Docker）

```bash
cd episteme
docker compose --project-name episteme -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

- 浏览器打开 `http://127.0.0.1:5080/`；首次启动是零业务概念的空壳，只有「创建你的第一个空间」。
- `deploy/.env` 已设 `EPISTEME_BIND_IP=0.0.0.0`，局域网可直接访问；`restart: "no"` 不开机自启。
- REST 前缀 `/api/v1/episteme`，MCP 为 `POST /mcp`，调试用 `GET /mcp/tools?space={slug}`。
- 数据在具名卷 `episteme_episteme-data`；`docker compose ... down -v` 可回到全新空库。

回归（连续两次全绿）：

```bash
/home/lishawn/.workbuddy/binaries/python/versions/3.13.12/bin/python3 ./tests/api_smoke.py          # 37/37
NODE_PATH=/home/lishawn/.workbuddy/binaries/node/workspace/node_modules \
  /home/lishawn/.workbuddy/binaries/node/versions/22.22.2/bin/node ./tests/ui_smoke.js            # 16/16
```

---

## 快速看原型

```bash
# 直接用浏览器打开（单文件、零依赖、无需服务器）
./prototype/index.html
```

原型是**可点击的真实交互演示**，不是静态图。建议按这个顺序体验：

1. **空壳启动**：首次打开是零业务概念的空状态，点「创建你的第一个空间」。
2. **装模板**：左侧「模板」→ 安装「通用阅读台」或「商业短篇创作工作台」，观察目录树与内容类型自动生成（后者演示 11 个模块变成 11 个可改目录）。
3. **自定义目录 / Schema**：目录树右键或顶部「新建目录」；「Schema」页新增内容类型、增删字段、切换字段类型与「暴露给 MCP」开关。
4. **文档与四类阅读器**：点开一篇示例文档，切换 Markdown / Code / PDF / Web 四个 Tab；MD 与 Code 可编辑并自动产生草稿，PDF 可选中文字高亮加批注，Web 可切阅读模式 / 原文模式。
5. **版本管理**：右侧「版本」抽屉看提交时间线、并排 diff、切分支、回滚任意提交。
6. **MCP 面板**：右侧「MCP」抽屉看 `tools/list` 实时清单；改一个 Schema 字段后观察工具签名与 `toolsVersion` 同步变化；点「模拟 Agent 写入」会在隔离分支产生一条 Commit，可在版本面板审阅 / 合并 / 撤销。
7. **主题与移动端**：顶栏四个圆点切四套浅色主题；窄屏下自动变抽屉 + 底部导航。

---

## 文档结构

| 文件 | 内容 |
|---|---|
| `SPEC.md` | 总入口：定位、六条设计原则、概念模型、四项能力、演进关系、技术栈、路线、验收标准、边界与待确认项 |
| `docs/01-概念模型与动态Schema.md` | 实体、字段类型表、Schema 版本与迁移、动态渲染契约、对称引用、PostgreSQL DDL |
| `docs/02-git式版本模型.md` | Blob/Tree/Commit/Ref、写入事务、diff/blame/branch/merge/revert/GC、与真 git 取舍、DDL |
| `docs/03-动态MCP-Server协议.md` | 传输与鉴权、20 个静态工具、动态派生规则、Resources、toolsVersion 广播、SSRF 与审计、错误码 |
| `docs/04-四类阅读器与统一Part契约.md` | 统一 Viewer 契约、四类规格、稳健锚点、多 Part 组合、性能目标 |
| `docs/05-架构演进与Workbench模板化.md` | 分层改造、Pack 格式、11 模块真实映射表、数据迁移与回滚、P0–P5 路线、部署拓扑 |
| `docs/06-API权限与验收标准.md` | REST 全表、权限矩阵、8 项可测验收、回归闸门、非功能要求 |
| `design/schemas/generic-library.json` | 通用阅读台 Pack：4 个内容类型 + 多 Part 示例文档 + 标注示例 |
| `design/schemas/workbench-pack.json` | 工作台 Pack：真实 11 个模块 → 11 个目录 / 12 个内容类型 / 两大口令 / 迁移映射 |
| `design/mcp/tools-before.json` | 新建内容类型前的 `tools/list` 快照 |
| `design/mcp/tools-after.json` | 新建后的快照 + 广播通知 + 三个新工具签名 + 字段可见性演示 |
| `design/mcp/call-examples.json` | 完整 Agent 协作流程、Resources 示例、错误码示例 |
| `prototype/index.html` | 高保真单文件交互原型 |
| `tests/ui_test.js` | 原型真实浏览器交互回归脚本 |
| `docs/07-P1P2实现与验收报告.md` | **本轮实现**：范围、架构、验收对照、修复缺陷、明确边界 |
| `src/Episteme.Core/` | 领域实体、Schema 引擎、内容寻址 diff、MCP 工具派生（零 NuGet 依赖） |
| `src/Episteme.Server/` | ASP.NET Core REST + MCP JSON-RPC + 动态 SPA + EF Core 持久化 |
| `deploy/` | Dockerfile、compose（restart: no）、端口绑定 .env |
| `tests/api_smoke.py` / `tests/ui_smoke.js` | 37 项 REST/MCP 冒烟、16 项 jsdom 真实点击回归 |

---

## 关键设计一句话

- **空壳**：内核不内置任何业务名词，首次启动只有「创建空间」。
- **Schema 即契约**：字段一改，列表 / 表单 / 索引 / MCP 工具签名在同一事务内重建。
- **文档是载体不是格式**：一份文档可同时挂 PDF 原文、Markdown 笔记、网页快照和代码。
- **版本是一等公民**：界面 / REST / MCP 三条写入路径共享同一条 Commit 链，AI 的每次编辑都能 diff 与 revert。
- **MCP 动态生成**：Agent 不需要改配置就能操作用户刚定义的目录与类型；写操作默认建议在隔离分支。
- **演进而非重写**：现有鉴权 / 幂等 / 审计 / 调度 / 三端 / Docker / 主题令牌全部复用。

---

## 复跑原型回归

```bash
# 环境：playwright + chromium 已装在本机 node workspace
NODE_PATH=/home/lishawn/.workbuddy/binaries/node/workspace/node_modules \
  /home/lishawn/.workbuddy/binaries/node/versions/22.22.2/bin/node \
  episteme/tests/ui_test.js
```

脚本会真实点击、填表、切换阅读器、改 Schema、触发 MCP 模拟写入，并捕获 console error / pageerror / 失败请求；输出 `episteme/tests/ui_report.md`。

---

## 下一步（P3 及以后）

P1 / P2 本轮已落地；接下来是 P3 四类阅读器与隔离抓取 worker → P4 Workbench Pack 化与数据迁移 → P5 三端与模板生态。生产化补齐项（PostgreSQL、Redis、JWT/HMAC、Blazor 渲染器）与本轮全部简化边界见 `docs/07-P1P2实现与验收报告.md §7`。

待拍板项仍见 `SPEC.md §11`。
