# 03 · 动态 MCP Server 协议

> 决策已确认：Episteme **对外提供 MCP Server**，且工具与资源随用户自定义目录 / Schema **动态生成**。
> 机器可读样例见 `episteme/design/mcp/`。

---

## 1. 设计目标

1. Agent 能像操作文件系统一样操作知识库：`tree` → `read` → 编辑 → `commit`。
2. **用户改 Schema，工具立刻跟着变**，不需要改代码、不需要重启服务、不需要重新配置客户端。
3. 每次 AI 写入都落在版本 DAG 上，可审计、可 diff、可一键撤销。
4. 复用现有安全体系（API Key + HMAC-SHA256 / JWT / RBAC / 幂等键 / 审计），不发明第二套鉴权。

---

## 2. 传输与端点

| 项 | 值 |
|---|---|
| 远程 | Streamable HTTP，`POST/GET https://{host}/mcp`；SSE 兼容回退 |
| 本地 | stdio（桌面端 Avalonia 内置，供离线 / 低延迟场景） |
| 协议版本 | 实现阶段锁定 `ModelContextProtocol` C# SDK 版本，头部带 `MCP-Protocol-Version` |
| 会话 | `Mcp-Session-Id`；服务端无状态，会话状态可由 JWT / 幂等键重建 |
| 空间路由 | `/mcp?space={slug}` 或工具参数 `spaceId`；**一次连接只绑定一个 Space**，避免工具描述爆炸 |
| OpenAPI | 现有 `/openapi.json` 保留；MCP 工具清单另提供 `GET /api/v1/episteme/mcp/tools` 便于调试 |

### 2.1 鉴权（沿用现有，不重复造轮子）

- **API Key + HMAC-SHA256**：`X-Key-Id`、`X-Timestamp`、`X-Nonce`、`X-Signature`，签名串顺序固定（时间戳 → Nonce → Key → Body），承接 v3.2 已修好的中间件顺序。
- **JWT Bearer**：交互式 Agent / 桌面端登录后使用。
- `tools/call` 每次都独立校验 scope；连接建立时的身份不等于调用授权。
- Nonce 由进程内缓存升级为 Redis（平台化后多实例必需）。
- 新增 MCP 专用 scope：`episteme:read` / `episteme:write` / `episteme:schema` / `episteme:version` / `episteme:annotate`。

---

## 3. 工具分两层

### 3.1 静态内核工具（恒定，共 20 个）

| 工具 | 作用 | 最小 scope | 产生 Commit |
|---|---|---|---|
| `episteme.list_spaces` | 列出可访问空间 | read | – |
| `episteme.tree` | 列出目录树（含节点绑定的类型 / 是否暴露） | read | – |
| `episteme.read` | 读文档：fields + Parts + annotations | read | – |
| `episteme.search` | 全文 / 字段 / 标签检索 | read | – |
| `episteme.list_content_types` | 列出内容类型与版本 | read | – |
| `episteme.define_content_type` | 新建 / 更新 Schema | schema | ✔(`source=schema`) |
| `episteme.create_node` | 新建目录（可绑定类型） | write | ✔ |
| `episteme.move_node` | 移动 / 重命名目录 | write | ✔ |
| `episteme.delete_node` | 删除目录（需 `recursive` 显式确认） | write | ✔ |
| `episteme.create_document` | 通用新建文档 | write | ✔ |
| `episteme.update_document` | 通用更新 fields / title / tags | write | ✔ |
| `episteme.append_part` | 追加 md / code / pdf / web Part | write | ✔ |
| `episteme.annotate` | 新增 / 删除标记 | annotate | ✔ |
| `episteme.commit` | 显式提交（把暂存改动打包） | write | ✔ |
| `episteme.log` | 提交历史（可按 path / agentId / 时间过滤） | read | – |
| `episteme.diff` | 两个 Commit / Ref 之间的差异 | read | – |
| `episteme.branch` | 创建 / 列出分支 | version | – |
| `episteme.merge` | 合并分支（返回冲突清单） | version | ✔ |
| `episteme.revert` | 反向提交（支持按 agentId 批量） | version | ✔ |
| `episteme.export_as_git` | 导出为真实 git 仓库 | read | – |

### 3.2 动态派生工具（随 Schema 变化）

对**每个** `exposeToMcp = true` 且绑定了目录节点的 ContentType，生成三个工具：

```
episteme.query_{slug}    ← 可检索字段（indexed && exposeToMcp）
episteme.create_{slug}   ← 必填字段 + 可写可选字段
episteme.update_{slug}   ← 主键 + 可写字段（全部 optional）
```

**FieldDef → JSON Schema 映射**

| FieldDef.type | JSON Schema |
|---|---|
| `text` / `longtext` / `markdown` | `{"type":"string"}` + `maxLength` / `pattern` |
| `code` | `{"type":"object","properties":{"lang":{"type":"string"},"code":{"type":"string"}}}` |
| `number` | `{"type":"integer 或 number","minimum":…,"maximum":…}` |
| `date` | `{"type":"string","format":"date-time"}` |
| `boolean` | `{"type":"boolean"}` |
| `select` | `{"type":"string","enum":[…]}` |
| `multi_select` / `tags` | `{"type":"array","items":{"type":"string"}}` |
| `url` | `{"type":"string","format":"uri"}` |
| `ref` | `{"type":"string","description":"目标文档 ID"}` |
| `json` / `file` | `{"type":"object"}` + 子 schema |

`required` 数组来自 `FieldDef.required`，`description` 由 `label + hint` 拼成（让 Agent 看懂字段语义）。`exposeToMcp=false` 的字段**完全不出现**；`mcpWritable=false` 的字段只出现在 `query_*` 的返回描述里，不出现在 `create_* / update_*` 参数里。

**工具名冲突策略**：slug 只接受 `[a-z0-9_]`；与静态工具同名时自动加 `_type` 后缀并在描述里标注；跨类型重名时追加内容类型 `version`。

**目录级过滤**：`Node.exposeToMcp = false` 的节点及其子树不生成资源、不出现在 `tree` 返回中（但仍可通过显式 docId 读取，若有权限）。

### 3.3 工具描述模板（示例）

```json
{
  "name": "episteme.create_paper",
  "description": "在节点 /论文库 下新建「论文」文档。必填：标题、发表年份。写入会产生一条 Commit，可用 episteme.log 查看、episteme.revert 撤销。建议在 agent/ 分支上试写。",
  "inputSchema": {
    "type": "object",
    "properties": {
      "nodePath": { "type": "string", "default": "/论文库" },
      "title":    { "type": "string", "description": "标题" },
      "year":     { "type": "integer", "minimum": 1900, "maximum": 2100, "description": "发表年份" },
      "venue":    { "type": "string", "description": "发表 venue" },
      "tags":     { "type": "array", "items": { "type": "string" }, "description": "标签" },
      "parts":    { "type": "array", "description": "可选：md/code/pdf/web 内容" },
      "branch":   { "type": "string", "description": "目标分支，默认 main" },
      "reason":   { "type": "string", "description": "本次改动理由，写入 Commit 审计" },
      "expectParent": { "type": "string", "description": "乐观锁：期望的当前 HEAD commitId" }
    },
    "required": ["title", "year"]
  }
}
```

---

## 4. Resources 与 Prompts

| 类型 | URI 模板 | 说明 |
|---|---|---|
| 目录 | `episteme://{space}/{nodePath}` | 返回该节点下文档清单（含 id / title / updatedAt） |
| 文档 | `episteme://{space}/{nodePath}/{docId}` | 返回 fields + Part 摘要 |
| Part | `episteme://{space}/.../{docId}/part/{kind}` | 返回原文；MIME 按 kind：`text/markdown`、`text/x-{lang}`、`application/pdf`、`text/html` |
| 版本 | `episteme://{space}/.../{docId}?ref={commitId 或 branch 或 tag}` | 读历史快照 |
| 标注 | `episteme://{space}/.../{docId}/annotations` | 高亮与批注列表 |

`resources/list` 只列暴露的节点模板（不枚举全部文档，避免大库爆炸）；`resources/read` 按需拉取。
`notifications/resources/list_changed` 在目录结构变化时推送。

**Prompts**：由 Pack 提供（如 `workbench` 的「拆文复盘」「事实虚化改编」），内核不内置任何业务提示词。用户可在界面编辑 Prompts，改动同样进版本历史。

---

## 5. tools/list 动态重建与版本广播

```
触发源：ContentType 增改 / Node 增删或 expos 开关 / RBAC scope 变更 / Pack 安装卸载
   │
   ▼
事务内：重算工具清单 → toolsVersion += 1 → 写 Redis `episteme:{space}:tools`
   │
   ▼
广播：notifications/tools/list_changed  { spaceId, toolsVersion, added[], removed[], changed[] }
   │
   ▼
客户端：重新 tools/list；缓存以 toolsVersion 为 ETag
```

- `toolsVersion` 出现在每个工具调用的响应头与 `tools/list` 返回值；服务端检测到客户端携带过期版本时返回 `TOOLS_STALE`，提示重取而非静默用旧签名。
- 重建耗时目标 < 50 ms（100 个内容类型 / 1000 节点量级）；工具清单按 Space 缓存。
- 权限变更只影响该连接可见的工具子集，不全局重建。

**验收锚点**：用户新建一个「竞品情报」类型（3 个字段）后，同一会话内 `tools/list` 立刻多出 `episteme.create_competitor_intel` 等 3 个工具，且参数签名与 Schema 一致（SPEC §9 验收项 2）。

---

## 6. 写入语义与安全

### 6.1 通用参数（所有写工具）

| 参数 | 必填 | 说明 |
|---|---|---|
| `branch` | 否 | 默认 `refs/heads/main`；建议 Agent 用隔离分支 |
| `reason` | 建议 | 写入 Commit.reason，缺省填工具名 |
| `expectParent` | 建议 | 乐观锁；不匹配返回 `409 CONFLICT` |
| `idempotencyKey` | 建议 | 复用现有幂等键；重复调用返回原 Commit |
| `dryRun` | 否 | 返回将产生的 diff 预览，不落 Commit |

### 6.2 原子性

一次 `tools/call` = 一个事务 = 最多一个 Commit（§02 §3）。批量操作由 Agent 拼在一个调用里或用显式 `episteme.commit` 打包，避免半完成状态被他人读到。

### 6.3 权限矩阵（工具级）

| 工具组 | owner | editor | viewer | agent |
|---|---|---|---|---|
| `read/search/tree/log/diff/resources` | ✔ | ✔ | ✔ | ✔（限本 Space） |
| `create/update/append/annotate` | ✔ | ✔ | ✖ | ✔（限授权节点 + 建议分支） |
| `define_content_type` / `create_node` / `delete_node` | ✔ | ✖ | ✖ | ✖（默认） |
| `branch/merge/revert` | ✔ | ✔（仅自己分支） | ✖ | ✔（仅自己分支） |

`agent` 角色默认**不能改 Schema**、不能删目录——防止 Agent 自我扩权。需要时由 owner 显式授予 `episteme:schema`。

### 6.4 SSRF 与内容安全

承接上一轮开源调研结论（Karakeep 官方自述 SSRF 仅部分缓解）：

- `url` 字段 / `Part(web)` / `Part(pdf)` 的抓取**不在 API 进程内执行**，统一投递到独立 `fetch-worker` 容器。
- 抓取 worker 与 PostgreSQL / API **不同 Docker 网络**，仅有出网权限；禁止 `10./172.16-31./192.168./127./169.254.` 及 IPv6 等价段；DNS 解析后二次校验；重定向逐跳校验；禁 `file://` / `gopher://`。
- 域名白名单、单响应上限（默认 20 MB）、超时（10 s）、内容类型白名单、重抓频率限制。
- 抓取结果只保留：原文 URL、抽取正文、HTML 快照、抓取时间、HTTP 状态；不执行任何脚本。
- 外部内容一律标记 `origin=external`，在 UI 与 MCP 返回值里显式提示「非本库原创内容」。

### 6.5 审计

每次 `tools/call` 记一条 AuditLog：`sessionId / agentId / tool / 参数摘要 / scope / 结果 / commitId / idempotencyKey / latency`。
提供 `episteme.revert --agentId X --since T` 的批量撤销入口（§02 §5.3）。

---

## 7. 错误码

| code | HTTP | 含义 | Agent 建议动作 |
|---|---|---|---|
| `UNAUTHORIZED` | 401 | 签名 / 令牌无效 | 重新鉴权 |
| `FORBIDDEN_SCOPE` | 403 | scope 不足或节点不暴露 | 换节点 / 申请授权 |
| `TOOLS_STALE` | 409 | toolsVersion 过期 | 重新 `tools/list` |
| `CONFLICT_PARENT` | 409 | `expectParent` 不匹配 | `log` 重读后重试 |
| `SCHEMA_VALIDATION` | 422 | 字段校验失败 | 按 `details[]` 修参数 |
| `SCHEMA_VERSION_MISMATCH` | 409 | 提交的 schemaVersion 过旧 | 重新拉 Schema |
| `SSRF_BLOCKED` | 422 | URL 命中禁网段 / 非白名单 | 换公开 URL |
| `ANNOTATION_ANCHOR_LOST` | 422 | 标记锚点无法定位 | 重新选取文本 |
| `IDEMPOTENT_REPLAY` | 200 | 幂等重放 | 直接用返回的原 Commit |

---

## 8. 一次完整交互示例

```jsonc
// 1) Agent 连上后先看结构
{"method":"tools/call","params":{"name":"episteme.tree","arguments":{"path":"/"}}}
// → { nodes:[{path:"/论文库", contentType:"Paper", exposeToMcp:true}, ...], toolsVersion: 12 }

// 2) 用户刚新建了「竞品情报」类型，收到 tools/list_changed → 重新 tools/list
// → 新增 episteme.create_competitor_intel / query_competitor_intel / update_competitor_intel

// 3) 在隔离分支写入
{"method":"tools/call","params":{"name":"episteme.create_competitor_intel","arguments":{
  "nodePath":"/竞品","title":"某产品 2026 Q3 更新","releaseDate":"2026-09-30",
  "tags":["定价","AI"],
  "parts":[{"kind":"web","sourceUri":"https://example.com/changelog"}],
  "branch":"refs/heads/agent/claude/20261109",
  "reason":"每日情报采集任务补齐 Q3 更新",
  "expectParent":"cm_01J8Y...",
  "idempotencyKey":"task-8891-20261109"
}}}
// → { commitId:"cm_01J8Z...", docId:"doc_...", branch:"refs/heads/agent/claude/20261109",
//     stats:{added:1}, toolsVersion:12, auditId:"al_..." }

// 4) 人工审阅
{"method":"tools/call","params":{"name":"episteme.diff","arguments":{
  "base":"refs/heads/main","target":"refs/heads/agent/claude/20261109"}}}
// 5) 合并或整批撤销
{"method":"tools/call","params":{"name":"episteme.merge","arguments":{"from":"refs/heads/agent/claude/20261109"}}}
// 或：{"name":"episteme.revert","arguments":{"agentId":"claude-desktop","since":"2026-11-09T00:00:00Z"}}
```

---

## 9. 与现有 `/api/v1` 的关系

- 现有 REST 全部保留并标记 deprecated（`Sunset` 头），P4 前不得破坏 39 项冒烟。
- MCP 与 REST **共享同一 Application 层服务**，不是两套逻辑：MCP 工具是 REST 端点的一层协议适配。
- API Key / HMAC / 幂等 / 审计 / 调度 / 通知均为共用组件。

---

## 10. 明确不做

- 不实现 MCP sampling / roots 的服务端主动调用（P2 仅 tools + resources + prompts）。
- 不在一次连接里暴露多 Space 的全部工具（工具上限与描述膨胀）。
- 不让 Agent 默认获得 Schema 修改权。
- 不在 API 进程内抓取外部 URL。
