# Agent.md · Episteme 知书（AI 协作者工作规范）

> 本文件面向在本仓库中工作的 AI Agent / 编码助手：先读这里，再按「索引」定位具体文件。
> 它是**协作规范 + 导航索引**，不替代事实来源；规格以 `SPEC.md` / `docs/01–10` 为准，接口以 `src/Episteme.Server/Endpoints.cs` 为准。
> 人类读者请从 `README.md` 开始。

---

## 0. 一句话定位

Episteme 是一个**没有预设业务语义的通用文档内核**：目录与内容由用户定义，界面 / REST / AI Agent 共用同一份 Schema，四类媒介（md/code/pdf/web）挂在一份文档下，每次改动像 git 一样可追溯、可 diff、可回滚。旧「商业短篇创作工作台」已降为一套内置模板（Pack），本仓库只保留内核与其模板。

当前版本：`v0.5（0.5.0-polish）`，P1–P5 全部落地。仓库根即 `Episteme/`（**没有** `episteme/` 子目录），所有命令在仓库根执行。

**项目范围（2026-11-16 调整）：仅开发 Web 应用**——只交付浏览器 Web SPA 与 ASP.NET Core 服务端；不维护 Android / 桌面 / Avalonia 跨平台客户端（相关工程已删除）；MCP 仅 Streamable HTTP（`POST /mcp`），无 SSE / stdio 传输。

---

## 1. 必须遵守的规则（Rules）

### R1. 空壳原则（Empty by default）
内核里不得出现「素材 / 热点 / 报告 / 论文」等业务专有名词，不得预置目录、字段或业务实体。新增业务概念只能进 Pack 模板（数据），不能进 Core/Server 代码。新增能力前先问：这是通用的，还是某个 Pack 的？

### R2. 三层抽象边界
| 层 | 落点 | 规则 |
|---|---|---|
| L0 通用内核 Core | `src/Episteme.Core/`、`src/Episteme.Server/` | 极少变；零业务语义；保持零 NuGet 依赖（Core） |
| L1 媒介 Viewer | Viewer 契约 + `wwwroot/` 阅读器 | 内核稳定，渐进增强；引擎本地 vendored，零 CDN |
| L2 模板 Pack | `wwwroot/packs/*.json` | 纯数据，安装时不执行用户代码；一次事务建对象并产生 `source=import` 的 Commit |

跨层依赖只允许上层引用下层：Server → Core，Pack/UI → Server 暴露的能力。禁止在 Core 里引用持久化、HTTP 或 UI 框架。

### R3. Schema 即契约
`ContentTypeDefinition`（`src/Episteme.Core/SchemaEngine.cs`）是校验、动态渲染、MCP 工具签名的**唯一事实来源**。改字段类型/规则时，必须同步：校验逻辑、渲染描述、MCP 派生签名、`docs/01` 字段类型表、相关测试。禁止在别处复制一套字段规则。

### R4. 版本是一等公民
界面 / REST / MCP 三条写路径都走同一个 `WriteCommitAsync`：一次写入 = 一条 Commit + 一条审计。新增写接口必须支持 `source` / `reason` / `actor` / `agentId` / `idempotencyKey` / `expectParent` / `branch` 等写参数（适用时），并保证写响应回写 `X-Tools-Version`。所有 `DateTime` 用统一 UTC 转换器，保证 SQLite 与 Npgsql 行为一致。

### R5. MCP 动态生成
`tools/list` 依据当前目录树与 Schema **实时重建**，靠 `toolsVersion` 单调递增 + 客户端重新拉取；不要引入重启依赖或假设工具清单固定。新增/变更静态工具需同步 `src/Episteme.Core/McpTools.cs`、`docs/03`、`design/mcp/` 契约样例与 MCP 调试测试。空库时只返回 5 个引导工具（含 `episteme.create_space`）。

### R6. Agent 改动默认可隔离
MCP 写操作用 `branch` 隔离、`reason`/`agentId`/`idempotencyKey` 留痕、`expectParent` 做乐观锁。不要让 Agent 静默覆盖人类内容；不改写历史（revert 生成反向提交）。

### R7. 鉴权三模式，默认最小
`EPISTEME_AUTH_MODE` ∈ `legacy`（默认，角色头可伪造，仅本机）/ `secure`（JWT + HMAC + Nonce + 限流）/ `users`（账号 + 服务端 JWT + 按账号授权 + 每账号 MCP token）。
新增端点必须声明在三种模式下的行为；users 模式遵循**按账号授权**（空间 `ep_space_owners` 唯一归属，他人 `403 ACCOUNT_FORBIDDEN`）。客户端伪造的 `X-Episteme-Role` 会被服务端 principal 覆盖。不要把未开鉴权的 5080 暴露公网。

### R8. 持久化边界
EF Core + SQLite（默认）/ PostgreSQL 17（可选 profile）。启动用 `EnsureCreatedAsync()`，**没有迁移脚本**；任何 Schema/表结构变更需手工规划升级数据，并在 `README.md §9` 的边界说明里登记。身份表 `ep_users`/`ep_space_owners`/`ep_mcp_tokens` 由 `IdentityStore` 用可移植 raw SQL 幂等创建（不是 EF Migration）。

### R9. 响应与错误契约
统一 JSON（camelCase、忽略 null）；业务错误为 `{ "code", "message", "traceId" }`。常见码：`401 UNAUTHORIZED`、`403 FORBIDDEN_SCOPE / ACCOUNT_FORBIDDEN`、`409 CONFLICT_PARENT / PACK_CONFLICT`、`422 PACK_INVALID / ANCHOR_INVALID`、`429 RATE_LIMITED`、`503 FETCH_WORKER_UNAVAILABLE`。不要新造平行错误体系。

### R10. 离线与本地优先
pdf.js / Monaco / Readability 全部 vendored 在 `src/Episteme.Server/wwwroot/vendor/`，零 CDN；加载失败要降级而不是白屏。网页抓取走独立 worker，须保留八类 SSRF 防护（协议/端口/DNS/私网/元数据/重定向/白名单/大小超时），单次 `allowedHosts` 只能更严。

### R11. 不提交密钥与本地数据
`deploy/.env`、`.local-data/`、`bin/`、`obj/`、`node_modules` 均不提交。改配置用 `deploy/.env.example` / `deploy/users.env.example` 模板。密钥、管理员密码、MCP token 明文不得写入代码或文档。

### R12. 环境细节
Windows PowerShell 中 `curl` 是 `Invoke-WebRequest` 别名，要用 `curl.exe`；bash 风格 `\` 续行在 PowerShell 改成单行或反引号。仓库只保留 `src/Episteme.slnx`（Core + Server），本机编译服务端即可。

### R13. 改 UI / 组件规则必须同步用户文档
界面或系统组件逻辑规则一旦发生变化，改动**不算完成**——必须主动同步两份面向用户的文档，把它们当作同一个变更单元：

- **`README.md`**：更新受影响的功能清单（§3）、使用方法（§5）、接口/协议速查（§6）、配置与命令（§4）、边界（§9）等对应章节；若涉及版本或本轮增量，也一并更新表格里的说明。
- **`src/Episteme.Server/wwwroot/help-content.js`**：这是**应用内页内帮助手册的唯一数据源**（纯数据，由 `wwwroot/app.js` 渲染，17 个主题分「入门 / 核心操作 / AI 与接口 / 注意事项」四组）。新增、改名、删除界面元素（按钮、菜单、Tab、字段、快捷键、操作路径）或调整组件行为规则时，必须新增或修订对应主题，禁止只改界面不改手册，也禁止让手册描述过时行为。
- 若改动同时影响高保真原型 `prototype/index.html`，保持原型与实现一致。

文档更新与代码改动视为同一次提交的一部分；未同步这两份文档的自检不通过（见第 4 节检查清单）。注意 `README.md` 与 `help-content.js` 的描述须彼此一致，且都以实际代码行为为准。

### R14. 仅 Web 范围（2026-11-16 起）
产品只交付浏览器 Web SPA 与 ASP.NET Core 服务端。不得新建 Avalonia / 桌面 / Android / 跨平台客户端工程，也不得恢复 MCP 的 SSE / stdio 传输；所有新接口只走 REST 与 `POST /mcp`（Streamable HTTP）。任何涉及多端客户端的需求先按 Web 方案实现。

---

## 2. 仓库索引（Index）

### 2.1 顶层
| 路径 | 内容 |
|---|---|
| `README.md` | 人类入口：11 节的完整使用/运维/验收/边界说明 |
| `SPEC.md` | v0.1 平台化规格：定位、六原则、概念模型、四项能力、路线、待拍板项 |
| `Agent.md` | 本文件：AI 协作规范 + 索引 |
| `docs/01–10` | 设计规格 + 分阶段实现与验收报告（详见 2.4） |
| `design/` | `tokens.json`（四套主题）、`schemas/`（Pack 样例）、`mcp/`（工具契约样例） |
| `prototype/index.html` | 单文件零依赖高保真交互原型（与实现同源主题） |
| `deploy/` | Dockerfile、docker-compose.yml、`.env.example`/`users.env.example`、Caddyfile.example、post-receive.example |
| `worker/` | Node 22 fetch-worker：`/fetch` 在线抓取、`/extract` 离线快照、Readability + 白名单清洗 |
| `tools/git-sync.py` | 旁路 git 同步：head/hook/range、legacy/HMAC/JWT/bearer/login/mcp、`--app-type` 归档、幂等 |
| `tests/` | 十二套回归脚本 + `ui_report.md/.json`（详见 2.5） |

### 2.2 服务端 `src/Episteme.Server/`
| 文件 | 职责 |
|---|---|
| `Program.cs` | 组合根：DbProvider 选择（SQLite/PG）、DI、CORS、JSON 选项、`EnsureCreatedAsync`、身份表初始化、中间件与静态 SPA |
| `Endpoints.cs` | REST 全表（`/api/v1/episteme` 前缀，53 条路由）——**接口事实来源** |
| `McpEndpoint.cs` / `SseMcpEndpoint.cs` | MCP JSON-RPC 主端点、SSE + messages 会话 |
| `EpistemeDbContext.cs` | EF Core 11 个 DbSet 与模型映射 |
| `EpistemeService*.cs` | 业务服务的 partial 拆分：`EpistemeService`（核心）、`.Version`（版本 DAG）、`.Parts`、`.Media`、`.Packs`、`.Viewer` |
| `IdentityEndpoints.cs` | `/api/v1/auth`、`/api/v1/admin` 账号与角色端点 |
| `Security/` | 鉴权/签名/限流/空间归属中间件（legacy / secure / users） |
| `Capabilities.cs` | `/health`、`/capabilities`、`/fetch-worker/health` 自检 |
| `wwwroot/` | SPA：`app.js`、`auth.js`、`help-content.js`（**页内帮助手册唯一数据源**，由 `app.js` 渲染）、`tokens.json`、`packs/`（4 套模板）、`vendor/`（pdf.js/Monaco） |

### 2.3 领域内核 `src/Episteme.Core/`（零 NuGet 依赖）
| 文件 | 职责 |
|---|---|
| `Entities.cs` | 实体定义（Space/Node/Document/Part/Annotation/Blob/Tree/Commit/Ref/AuditLog…） |
| `SchemaEngine.cs` | 字段类型、校验、动态渲染契约、MCP 签名派生——**Schema 事实来源** |
| `Versioning.cs` | 内容寻址（SHA-256 Blob）→ Tree → Commit → Ref，diff / merge / revert |
| `PackEngine.cs` | 声明式模板解析、dryRun/overwrite/includeSamples、幂等安装与导出 |
| `McpTools.cs` | 30+ 静态工具定义与 tools/list 动态生成 |
| `ViewerContracts.cs` | 统一 Viewer 契约（mount/read/edit/annotate/snapshot）与锚点模型 |

### 2.4 文档索引
| 文档 | 内容 |
|---|---|
| `docs/01` | 概念模型与动态 Schema：实体、字段类型表、版本迁移、渲染契约、DDL |
| `docs/02` | git 式版本模型：Blob/Tree/Commit/Ref、写入事务、diff/merge/revert/GC |
| `docs/03` | 动态 MCP Server 协议：传输与鉴权、静态/动态工具、Resources、安全与错误码 |
| `docs/04` | 四类阅读器与统一 Part 契约：锚点模型、多 Part 组合、SSRF 隔离 |
| `docs/05` | 架构演进与 Workbench 模板化：Pack 格式、迁移映射、P0–P5 路线、部署拓扑 |
| `docs/06` | REST 全表、权限矩阵、可测验收项、回归闸门 |
| `docs/07`–`docs/09` | P1+P2、P3、P4+P5 的实现范围、验收对照、修复清单 |
| `docs/10` | v0.5 完善：真实媒介、生产化、桌面补全、回归与诚实边界 |

> `SPEC.md` 与 `docs/05`、`docs/06` 中「复用旧 Workbench 代码 / `Workbench.slnx` / `/api/v1` 零回归」是 v0.1 设计阶段的历史依据，不对应本仓库可执行产物；P1 起按规格从零落地内核。

### 2.5 Web 前端与测试索引
| 路径 | 说明 |
|---|---|
| `src/Episteme.slnx` | Core + Server（唯一解决方案，Docker 构建用） |
| `src/Episteme.Server/wwwroot/` | 唯一客户端形态：Web SPA（`app.js` / `auth.js` / `help-content.js`）+ vendored 阅读器 |
| `prototype/index.html` | 单文件零依赖高保真交互原型 |
| `tests/api_smoke.py` | REST/MCP 主回归（104，Python 标准库） |
| `tests/ui_smoke.js` / `tests/ui_test.js` | Web UI jsdom 交互（47）/ 原型 Playwright 浏览器（28） |
| `tests/help_smoke.js` | 应用内帮助手册（35） |
| `tests/git_sync_smoke.py` | git 旁路同步（22，含 secure HMAC 3 项） |
| `tests/secure_smoke.py` | secure 鉴权/限流（12，默认 5081） |
| `tests/postgres_smoke.py` | PostgreSQL provider（13，默认 5082） |
| `tests/auth_smoke.py` / `tests/auth_ui_smoke.js` | users 账号与工作空间（86）/ 登录墙与后台 DOM（29） |
| `tests/mcp_token_smoke.py` / `tests/isolation_smoke.py` | MCP token 生命周期（42）/ 跨账号越权矩阵（36） |

---

## 3. 常用命令（Cheat Sheet）

```bash
# 一键运行（仓库根）
docker compose -p episteme -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
curl http://127.0.0.1:5080/health          # PowerShell 用 curl.exe

# 本地源码运行
dotnet build src/Episteme.slnx -c Release
$env:ASPNETCORE_URLS = "http://localhost:5080"; $env:EPISTEME_DATA_DIR = "F:\Code\Episteme\.local-data"
dotnet run --project src/Episteme.Server/Episteme.Server.csproj

# 测试（前置见 README §7；users 专项需空库实例 + 管理员密码）
python3 tests/api_smoke.py
NODE_PATH=<node_modules> node tests/ui_smoke.js
```

完整环境变量表、各模式启用步骤与测试复跑注意见 `README.md §4 / §7 / §11`。

---

## 4. 修改检查清单（Before you finish）

- [ ] 新业务概念是否只落在 Pack，没有污染 Core/Server（R1/R2）？
- [ ] Schema 改动是否同步校验、渲染、MCP 签名、文档与测试（R3）？
- [ ] 写路径是否产生 Commit + 审计、支持幂等/乐观锁、回写 `X-Tools-Version`（R4）？
- [ ] MCP 工具清单是否靠 `toolsVersion` 动态重建、空库引导未被破坏（R5）？
- [ ] 三种鉴权模式下的行为与 users 空间归属是否声明并校验（R7）？
- [ ] 涉及表结构变更是否登记为无迁移的手工升级边界（R8）？
- [ ] 错误码/响应格式符合统一契约（R9）；前端资源零 CDN 且有降级（R10）？
- [ ] 未提交任何密钥/本地数据（R11）；相关回归套件已复跑或说明未跑原因？
- [ ] 改了 UI 界面或系统组件逻辑规则，是否已主动同步 `README.md` 与页内帮助 `wwwroot/help-content.js`（必要时含 `prototype/index.html`），且两者描述一致（R13）？
- [ ] 新增能力是否仍符合「仅 Web + 服务端」范围，没有引入多端客户端或 MCP SSE / stdio 传输（R14）？
- [ ] 若新增/调整文件，回头更新本文件的索引（第 2 节）与 `README.md §8`。

---

## 5. 已知边界（不要越界承诺）

仅 Web SPA（无多端客户端）· MCP 仅 Streamable HTTP（无 SSE / stdio）· `/fetch` 真实出网需在受控环境复验 · secure/users 为单机最小实现（无刷新令牌/SSO/审计归档）· MCP 为自实现最小 JSON-RPC（`resources/read` 仅提示文本，`list_changed` 未实现）· 无迁移脚本 · Episteme 不是 git remote（旁路同步靠外部 hook/CI 调 `tools/git-sync.py`）。完整清单见 `README.md §9`。
