# 06 · REST API、权限矩阵与验收标准

> 本文是 `SPEC.md §9` 的可执行展开。P1 起按此落地；P0 仅定义契约，不改现有 `/api/v1`。

---

## 1. 通用约定

| 项 | 值 |
|---|---|
| 前缀 | `/api/v1/episteme`（现有 `/api/v1` 保持不变，见 `05` §5） |
| 风格 | REST + JSON；`camelCase`；时间统一 ISO 8601 UTC（`timestamptz` 写 UTC，承接 v3.1 修复） |
| 分页 | `?page=1&pageSize=20`，返回 `Paged<T>{items,total,page,pageSize}` |
| 幂等 | 写操作支持 `Idempotency-Key`（沿用现有实现，存储升级为 Redis） |
| 乐观锁 | 写操作支持 `If-Match: {headCommitId}` / body `expectParent`，不匹配 `409 CONFLICT_PARENT` |
| 鉴权 | JWT Bearer（交互）+ API Key + HMAC-SHA256（Agent），沿用现有中间件与签名顺序 |
| 错误 | `{code,message,details[],traceId}`，错误码表见 `03` §7 |
| 版本 | 所有响应头带 `X-Episteme-Ref`（当前 HEAD）与 `X-Tools-Version` |

---

## 2. REST 全表

### 2.1 空间 / 目录

| 方法 | 路径 | 作用 | scope |
|---|---|---|---|
| GET | `/spaces` | 列出可访问空间 | read |
| POST | `/spaces` | 创建空间（生成空树 Commit） | owner |
| GET | `/spaces/{id}` | 空间详情 + head ref + toolsVersion | read |
| PATCH | `/spaces/{id}` | 改名 / 设置 | owner |
| GET | `/spaces/{id}/tree` | 目录树（可 `?depth=&includeTypes=`） | read |
| POST | `/spaces/{id}/nodes` | 新建目录（可绑定 contentTypeId） | write |
| PATCH | `/spaces/{id}/nodes/{nodeId}` | 重命名 / 移动 / 换绑定 / `exposeToMcp` | write |
| DELETE | `/spaces/{id}/nodes/{nodeId}` | 删除（须 `?recursive=true` 显式确认） | owner |

### 2.2 Schema

| 方法 | 路径 | 作用 | scope |
|---|---|---|---|
| GET | `/spaces/{id}/content-types` | 类型清单（含 version） | read |
| POST | `/spaces/{id}/content-types` | 新建内容类型 | schema |
| GET | `/spaces/{id}/content-types/{slug}?version=` | 取指定版本定义 | read |
| PUT | `/spaces/{id}/content-types/{slug}` | 更新（触发迁移 + toolsVersion 广播） | schema |
| POST | `/spaces/{id}/content-types/{slug}/validate` | 校验一份 fields（不落库） | write |
| GET | `/spaces/{id}/nodes/{nodeId}/render` | 动态渲染描述（列 / 表单 / 筛选 / 权限） | read |

### 2.3 文档 / Part / 标注

| 方法 | 路径 | 作用 | scope |
|---|---|---|---|
| GET | `/spaces/{id}/nodes/{nodeId}/documents` | 列表（按视图 / 筛选 / 排序 / 搜索） | read |
| POST | `/spaces/{id}/nodes/{nodeId}/documents` | 新建文档（fields + parts 一次提交） | write |
| GET | `/spaces/{id}/documents/{docId}?ref=` | 读文档（可指定历史 ref） | read |
| PATCH | `/spaces/{id}/documents/{docId}` | 更新 title / fields / tags | write |
| DELETE | `/spaces/{id}/documents/{docId}` | 删除（生成 Commit，可 revert） | write |
| POST | `/spaces/{id}/documents/{docId}/parts` | 追加 Part | write |
| PUT | `/spaces/{id}/documents/{docId}/parts/{partId}` | 更新 md / code 内容 | write |
| GET | `/spaces/{id}/documents/{docId}/parts/{partId}/content` | 取原文 / 快照（按 MIME） | read |
| GET | `/spaces/{id}/documents/{docId}/annotations` | 标注列表 | read |
| POST | `/spaces/{id}/documents/{docId}/annotations` | 新增标注 | annotate |
| DELETE | `/spaces/{id}/annotations/{annId}` | 删除标注 | annotate |
| GET | `/spaces/{id}/search?q=&kind=&node=` | 全文 / 字段 / 标签检索 | read |

### 2.4 版本

| 方法 | 路径 | 作用 | scope |
|---|---|---|---|
| GET | `/spaces/{id}/commits?path=&agentId=&since=` | 提交历史 | read |
| GET | `/spaces/{id}/commits/{commitId}` | 提交详情 + stats | read |
| GET | `/spaces/{id}/diff?base=&target=&path=` | 差异（字段级 + Part 级） | read |
| GET | `/spaces/{id}/blame?docId=&partId=` | 行级归属 | read |
| GET | `/spaces/{id}/refs` | 分支 / 标签清单 | read |
| POST | `/spaces/{id}/branches` | 创建分支 | version |
| POST | `/spaces/{id}/merges` | 合并（返回冲突清单） | version |
| POST | `/spaces/{id}/reverts` | 反向提交（支持 `agentId` 批量） | version |
| POST | `/spaces/{id}/tags` | 打标签 | version |
| POST | `/spaces/{id}/exports/git` | 导出真实 git 仓库 | read |

### 2.5 MCP / Pack / 抓取

| 方法 | 路径 | 作用 | scope |
|---|---|---|---|
| GET | `/spaces/{id}/mcp/tools` | 当前动态工具清单（调试用） | read |
| POST | `/mcp` | Streamable HTTP MCP 端点 | 见 `03` |
| GET | `/packs` | 已安装 Pack 清单 | read |
| POST | `/packs/install` | 安装 Pack（JSON 上传 / 内置 id） | owner |
| POST | `/packs/{id}/export` | 导出 Pack 定义 + 数据 | owner |
| DELETE | `/packs/{id}` | 卸载（可选保留文档） | owner |
| POST | `/fetch` | 提交 URL 抓取任务（异步，结果回写 Part） | write |

---

## 3. 权限矩阵

| 能力 | owner | editor | viewer | agent |
|---|---|---|---|---|
| 读目录 / 文档 / 搜索 / 历史 / diff | ✔ | ✔ | ✔ | ✔（限本 Space） |
| 新建 / 编辑 / 删除文档 | ✔ | ✔ | ✖ | ✔（限授权节点） |
| 追加 Part / 标注 | ✔ | ✔ | ✖（标注可单独授予） | ✔ |
| 新建 / 改 / 删目录 | ✔ | ✖ | ✖ | ✖ |
| 新建 / 改 Schema | ✔ | ✖ | ✖ | ✖（默认，需显式授 `schema`） |
| 创建分支 / 合并自己的分支 | ✔ | ✔（仅自己） | ✖ | ✔（仅自己） |
| 合并到 `main` / revert 他人提交 | ✔ | ✖ | ✖ | ✖ |
| 安装 / 卸载 Pack | ✔ | ✖ | ✖ | ✖ |
| 管理工具调用 scope / API Key | ✔ | ✖ | ✖ | ✖ |

**字段级**：`FieldDef.exposeToMcp=false` 的字段对所有 Agent 不可见；`mcpWritable=false` 对 Agent 只读。界面按权限**禁用**而非隐藏控件，避免用户与 Agent 对可用能力产生不同理解。

**目录级**：`Node.exposeToMcp=false` 的子树不出现在 `tree` / `resources/list` / 动态工具中；显式 docId 读取仍受 RBAC 约束。

---

## 4. 可测验收项（对应 SPEC §9）

| # | 验收项 | 判定方法 | 阶段 |
|---|---|---|---|
| 1 | 空启动零业务概念 | 全新 DB 启动，抓取首页与所有导航文本，断言不含「素材/热点/拆文/报告/选题」等词 | P1 |
| 2 | Schema 改后四处同步 | 新建「竞品情报」类型 + 3 字段；同会话断言列表列、表单控件、`render` 响应、`tools/list` 签名四者一致 | P1/P2 |
| 3 | 三条写路径同一 Commit 链 | UI / REST / MCP 各写一次；`GET /commits` 验证 3 条且 source 分别为 ui/api/mcp；revert 第 3 条后内容与第 2 条一致 | P2 |
| 4 | Agent 分支隔离与合并 | Agent 写 `agent/xxx`；断言 `main` HEAD 未变；diff 有内容；合并后字段级自动合并、Part 冲突显式标记 | P2 |
| 5 | 四类阅读器可标注 | md/code 编辑产生行级 diff；pdf/web 各生成 1 高亮 + 1 批注；刷新后锚点定位成功；四类标记均出现在版本历史 | P3 |
| 6 | Pack 安装与零回归 | 安装 `workbench` 后出现 11 个目录与 11 个 ContentType；抽样 100 条迁移数据双读差异 0；39 项冒烟 + 22 项交互全绿 | P4 |
| 7 | 幂等与审计 | 同一 `idempotencyKey` 重放 2 次只产生 1 个 Commit；每次 MCP 调用有 AuditLog（agentId/reason/commitId） | P2 |
| 8 | 安全边界 | `exposeToMcp=false` 字段不出现在工具签名；无 schema scope 调 `define_content_type` 返回 403；抓取 worker 访问 `http://127.0.0.1:5080` 被拦截 | P2/P3 |

### 4.1 回归闸门

任何阶段合入前必须全绿：
- `dotnet build Workbench.slnx -c Release` → 0 警告 0 错误
- `python3 tests/smoke_test.py` → **39/39**
- `node tests/ui_test.js` → **22/22**（连续两次）
- `python3 tools/check_tokens.py` → 主题令牌一致
- `docker compose config -q`
- 新增：`episteme/tests/ui_test.js`（原型交互）与 P2 起的 MCP 契约测试

---

## 5. 非功能要求

| 项 | 目标 |
|---|---|
| 列表查询（10 万文档 / 常用筛选） | P95 < 300 ms（表达式索引命中） |
| 中文全文检索 | `zhparser` + GIN，P95 < 500 ms；未装扩展时降级 `simple` 并提示 |
| `tools/list` 重建 | 100 类型 / 1000 节点 < 50 ms |
| 一次 Commit（含 1 文档 + 2 Part） | P95 < 150 ms |
| diff（1000 行 md） | < 200 ms |
| 并发写冲突 | 全部以 `409 CONFLICT_PARENT` 显式返回，无静默覆盖 |
| 容器 | 非 root、`TZ=Asia/Shanghai`、健康检查、凭据外置、端口绑定可配（承接 v3.1/v3.2） |
| 备份 | `pg_dump` + Blob 对象存储版本化；恢复演练每季度一次 |
| 可观测 | 每次 MCP 调用审计日志 + 结构化日志（traceId / spaceId / tool / latency） |

---

## 6. 风险与未决边界

- **本轮不实现任何代码**，以上为契约与验收基线；SDK 版本、前端库选型在 P1 锁定。
- 中文分词效果需真实语料压测；本轮未做性能实测。
- 三方合并冲突策略、Blob GC 保留期、大文件流式读取在 P2/P3 细化。
- 外部网页 / PDF 的版权与合规由使用者负责；产品保留原文链接与 `origin=external` 标识。
- 现有弱口令 `author/demo1234` 在平台化与局域网开放下风险放大，SPEC §11 建议 P1 加固（需同步登录页、冒烟脚本、Avalonia 客户端与文档）。
- 上一轮调研结论继续有效：内核不引入 AGPL/BSL 组件；MCP 原生不支持 HMAC，由 Episteme 网关层统一补齐。
