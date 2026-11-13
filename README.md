# Episteme · 知书

> 通用文档操作系统空壳：用户自定义目录与内容，动态 MCP 读写编辑，git 式版本管理，Markdown / Code / PDF / Web 四类**真实**阅读器，声明式 Pack 模板，Web / 桌面 / Android 三端；生产化能力以环境变量渐进开启。
> 由「商业短篇创作工作台」抽象一层而来；工作台已落地为本平台的**第一套内置模板（Pack）**。

**当前阶段：v0.5 · P1–P5 全部落地，并完成真实阅读器、生产化加固、桌面端补全、功能打磨排错，以及 git 旁路同步（公网入口 + post-receive hook）**（安卓端按用户要求一行未动）；服务端为 Docker 部署，客户端为可编译源码。v0.1 规格与高保真原型保留为设计基线。实现范围、回归结果与明确边界见 `docs/07`–`docs/10`，本轮验收见 `docs/10-v0.5完善与全量验收报告.md`。

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
- Compose 现在是 **api + fetch-worker** 两容器：worker 仅内网 `expose: 8090`，不发布宿主端口；可选 `postgres` profile。
- REST 前缀 `/api/v1/episteme`；MCP 支持 `POST /mcp`（主）、`GET /mcp/sse` + `POST /mcp/messages`、`stdio` bridge 三种传输。
- 数据在具名卷 `episteme_episteme-data`；`docker compose ... down -v` 可回到全新空库。
- 能力自检：`GET /api/v1/episteme/capabilities`、`GET /health`、`GET /api/v1/episteme/worker/health`。
- **真实阅读器**：本地 vendored pdf.js 4.10.38 / Monaco 0.52.2 / Mozilla Readability 0.6.0，零 CDN；PDF 原件存内容寻址 blob、Web 快照走独立 worker 的 SSRF 防护 + 白名单清洗；丢失锚点可交互式重定位。
- **生产化开关**（默认全部关闭、不影响 legacy 契约）：`EPISTEME_AUTH_MODE=secure`（JWT HS256 / HMAC-SHA256 / Nonce / 限流）、`EPISTEME_DB_PROVIDER=postgres` + `EPISTEME_PG_CONNECTION`、`EPISTEME_RATE_LIMIT`、`EPISTEME_SIGNATURE_SKEW`、`EPISTEME_FETCH_TOKEN`、`EPISTEME_FETCH_ALLOWLIST`。
- P4 入口：`GET /api/v1/episteme/packs` 列出 3 套内置模板（workbench / reading-desk / git-ingest）；Web UI「模板」Tab 可预览 / 安装 / 导出 / 导入。
- P5 客户端：`src/Episteme.Client.slnx`（Avalonia 12.1.5 共享壳 + Desktop + McpStdio + 无 GUI Probe）；`src/Episteme.Android/` 为独立 head（需 Android SDK，本轮按用户要求未改动、未编译）。

回归（7 套全绿，合计 **235 项 / 轮**）：

| 套件 | 命令 | 结果 |
|---|---|---|
| REST / MCP 主回归 | `python3 tests/api_smoke.py` | **104/104** |
| Web UI jsdom 交互 | `NODE_PATH=<workspace>/node_modules node tests/ui_smoke.js` | **47/47** |
| 桌面客户端无 GUI | 容器内 `dotnet src/Episteme.Client.Probe/bin/Release/net10.0/Episteme.Client.Probe.dll` | **30/30** |
| secure 鉴权 / 限流 | 另起 5081 实例 → `EPISTEME_TEST_BASE=… python3 tests/secure_smoke.py` | **12/12** |
| MCP stdio 传输 | `EPISTEME_DOTNET_CMD=… python3 tests/stdio_smoke.py` | **7/7** |
| PostgreSQL provider | 另起 PG17 + 5082 实例 → `EPISTEME_TEST_BASE=… python3 tests/postgres_smoke.py` | **13/13** |
| git 旁路同步 | `python3 tests/git_sync_smoke.py`（可配 `EPISTEME_SECURE_BASE` / `EPISTEME_SECURE_SECRET` 跑 HMAC） | **22/22** |

客户端层（容器内编译 + 无 GUI 冒烟；首次 NuGet 下载较慢，用缓存卷加速）：

```bash
docker run --rm --network episteme_default -e EPISTEME_API=http://api:8080 \
  -v "$PWD":/src -v episteme-nuget-cache:/root/.nuget/packages -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -lc 'set -o pipefail; \
    dotnet build src/Episteme.Client.slnx -c Release --nologo && \
    dotnet src/Episteme.Client.Probe/bin/Release/net10.0/Episteme.Client.Probe.dll'
```

服务端 publish 与客户端 build 均为 **0 警告 0 错误**。Android head 需 `dotnet workload install android` 与 Android SDK，本轮未编译验证；桌面 GUI 因容器无 X server 未实际开窗。

---

## 从任意位置同步 git 提交（旁路方案）

**Episteme 不是 git remote**：它没有实现 `git-upload-pack` / `git-receive-pack`，也不能 `git clone`。标准的 `git push` 落在你自己的 Git 服务（Gitea / GitHub / bare 仓库），由其 `post-receive` hook 或 CI 调用 `tools/git-sync.py`，把提交写进 Episteme。

### 1. 让服务可从公网访问

- `deploy/Caddyfile.example`：域名 + 自动 TLS 反代到 `127.0.0.1:5080`；`flush_interval -1` 保证 SSE 不被缓冲。
- `deploy/secure.env.example`：把 `deploy/.env` 切到 `EPISTEME_AUTH_MODE=secure`，配置 `EPISTEME_AUTH_SECRET` 与 `EPISTEME_API_KEYS=git-sync:owner`。**不要**把未开 secure 的 5080 直接暴露公网——legacy 的角色头可伪造。
- 生效：`docker compose -f deploy/docker-compose.yml -p episteme up -d --force-recreate`。

### 2. 安装 git-ingest 模板

`GET /api/v1/episteme/packs` 会列出 `pack_git-ingest`（共 3 套）。也可让脚本自动安装；`--dry-run` 只采集、不写文档：

```bash
python3 tools/git-sync.py --mode head --api https://episteme.example.com \
  --space <spaceId> --repo /path/to/repo --install-pack --dry-run
```

它创建 `/Git 提交` 目录与 `git_commit` 内容类型（17 个字段），并把 `gitcommit` 前缀的动态 MCP 工具一并派生出来。

### 3. 挂到 git push

- `deploy/post-receive.example`：复制到 bare 仓库 `hooks/post-receive` 并 `chmod +x`；脚本从 stdin 读 `<old> <new> <ref>`，按旧→新顺序导入。
- CI 用 `--mode range --range <base>..<head>`；本地试用用 `--mode head`。

```bash
# 开发流程不变，push 正常走 git 服务
git remote add archive ssh://git@your-server/srv/git/project.git
git push archive main
# 服务器 hook 自动把每个提交同步进 Episteme
```

认证：`--auth legacy`（仅内网）/ `--auth hmac`（secure，HMAC-SHA256 + Nonce）/ `--auth jwt`（HS256）。HMAC 的 canonical 串为 ``method\npath\nquery\ntimestamp\nnonce\nrole\nbodySha256``，与 `Security/SecurityMiddleware.cs` 一致；服务端配置 `EPISTEME_API_KEYS` 后，脚本的 `--key-id` 与 `--role` 必须与之匹配。

### 记录什么

| 组 | 字段 | 文件内容 Part |
|---|---|---|
| 提交标识 | `gitSha` / `gitShortSha` / `gitRepo` / `gitBranch` / `gitTag` | — |
| 作者与时间 | `gitAuthorName` / `gitAuthorEmail` / `gitAuthorDate` / `gitCommitter` | — |
| 说明与变更 | `gitMessage` / `gitBody` / `gitChangedFiles` / `gitDiff` | — |
| 文件内容 | `gitFileContents` / `gitPartCount` | `.md`→md、代码→code、`.html`→web、其它文本→attachment；二进制 / 超大 / 非 UTF-8 文件跳过并记原因 |

每个提交用 `idempotencyKey=git-sync:<sha>:<repoHash>`，hook 重跑或 CI 重试不会产生重复文档；`actor` 写 git 作者、`reason` 写 `git:<sha>`，版本日志与审计里也能看到 git 来源。默认上限：20 文件 / 每文件 256 KB / diff 64 KB / 每次 push 20 提交，均可用 `--max-*` 调整。

**边界**：这是旁路同步链，不是原生 git 协议；`POST /exports/git` 仍只返回线性化提交清单，不生成可 clone 的真实仓库。完整参数见 `python3 tools/git-sync.py --help`。

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
| `docs/07-P1P2实现与验收报告.md` | P1+P2 实现：范围、架构、验收对照、修复缺陷、明确边界 |
| `docs/08-P3四类阅读器实现与验收报告.md` | **P3 实现**：统一 Viewer 契约、稳健锚点四级回退、标注进版本、验收与简化边界 |
| `docs/09-P4P5实现与验收报告.md` | **P4/P5 实现**：Pack 契约、内置模板、三端骨架、验收对照、修复清单、诚实边界 |
| `docs/10-v0.5完善与全量验收报告.md` | **v0.5 完善**：真实媒介、生产化、桌面补全、修复清单、六套回归与诚实边界 |
| `src/Episteme.Core/` | 领域实体、Schema 引擎、内容寻址 diff、Pack 校验、Viewer 契约/锚点引擎、MCP 工具派生（零 NuGet 依赖） |
| `src/Episteme.Server/` | ASP.NET Core REST + MCP JSON-RPC + SSE + 四类阅读器 SPA + EF Core 持久化 + secure 中间件 |
| `src/Episteme.Server/wwwroot/vendor/` | 本地 vendored pdf.js / Monaco（零 CDN） |
| `src/Episteme.Client.slnx` + `Episteme.UI.Avalonia` / `Desktop` / `Android` / `Client.Probe` / `McpStdio` | P5 三端、桌面读写补全、stdio bridge 与无 GUI 客户端层冒烟 |
| `worker/` | 独立 fetch-worker：Node 22 + Readability + linkedom，SSRF 八类防护、/fetch、/extract、白名单清洗 |
| `src/Episteme.Server/wwwroot/packs/` | 3 套内置声明式 Pack JSON（workbench / reading-desk / git-ingest） |
| `deploy/` | Dockerfile、compose、.env，以及公网入口样例 `Caddyfile.example` / `secure.env.example` / `post-receive.example` |
| `.dockerignore` | 排除客户端 bin/obj 与 .git，构建上下文 595 MB → 3.59 kB |
| `tools/git-sync.py` | 旁路 git 同步脚本：hook / head / range 三种模式、四组 git 元数据、文件内容导入、HMAC / JWT 鉴权、幂等 |
| `tests/` | 104 项 REST/MCP、47 项 jsdom、30 项客户端 Probe、12 项 secure、7 项 stdio、13 项 PostgreSQL、22 项 git 同步回归 |

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

## 下一步（v0.5 之后）

P1–P5 已落地；v0.5 补齐了真实媒介、生产化开关、桌面读写与功能打磨。仍建议按序推进：

1. 多实例生产化：SSE 会话目前只在单进程内存，需 Redis 背板；补齐 Redis / MinIO 与对象存储、密钥托管与审计归档。
2. Android：按用户要求本轮未动；需 Android SDK 编译 APK、真机验证，并把共享壳升级到与桌面同等的 WebView 富阅读器。
3. 真实在线抓取：当前回归用离线 `/extract` 快照避免依赖公网；上线前应在受控 DNS / 出网策略下验证 `/fetch` 全链路与白名单。
4. 模板生态：服务端模板目录、版本签名与安装来源校验。

全部简化边界见 `docs/07 §7`、`docs/08 §8`、`docs/09 §9`、`docs/10 §7`。待拍板项仍见 `SPEC.md §11`。
