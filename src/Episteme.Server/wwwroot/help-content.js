/* Episteme · 知书 — 内置帮助手册内容（纯数据，零依赖）。
 * 由 app.js 渲染为页内帮助面板；index.html 只保留入口与容器，便于保持空壳首页。
 * 结构：sections[] -> { id, group, title, lead, blocks[] }
 * block.type: steps | list | table | note | warn | code
 *   steps/list items: 字符串或 { t: 小标题, d: 说明 }
 *   table: { head: [], rows: [][] }
 *   note/warn: { title, items: [] }
 *   code: { text }
 */
(function () {
  'use strict';

  window.EpistemeHelp = {
    version: '0.5.0-polish',
    sections: [
      {
        id: 'start', group: '入门', title: '开始之前 · 必读',
        lead: 'Episteme 是一个「空壳」：首次启动没有预置目录、字段或业务概念。结构由你定义；界面、REST 与 AI Agent 共用同一份 Schema；每次改动都生成可追溯、可 diff、可回滚的 Commit。',
        blocks: [
          { type: 'steps', items: [
            { t: '先创建一个空间', d: '首页点「创建空间」。空间是独立的版本历史、权限与 MCP 上下文，顶栏可随时新增或切换。' },
            { t: '定义内容类型，或先装模板', d: '中栏「+ 定义类型」建字段并绑定目录；也可以到「模板」Tab 安装通用阅读台 / 工作台模板，一次生成整套目录与 Schema。' },
            { t: '开始读写与追溯', d: '在动态表单创建文档 →「阅读器」Tab 挂接 Markdown / 代码 / PDF / 网页快照与标注 →「版本」Tab 看 diff 或回滚。' }
          ] },
          { type: 'warn', title: '第一次使用请特别留意', items: [
            '默认是「无鉴权 + 监听 0.0.0.0」：能访问 5080 端口的人都可读写。对公网或不可信网络开放前，务必启用 secure 模式、改绑定地址并加 TLS 终止。',
            '数据都在 Docker 具名卷里。执行 docker compose ... down -v 会清空全部空间、文档与提交历史，请先备份。',
            'Schema 字段类型变更需要显式转换规则；新增必填字段必须给 defaultValue，否则迁移会被拒绝。',
            'PDF 原件最多 20 MiB 且不可编辑；网页抓取由独立 worker 完成，短 HTML 片段会走 Readability 回退，不是错误。',
            'Android 客户端是未编译验证的骨架；桌面 GUI 只验证过编译与客户端逻辑，未在容器内实际开窗。'
          ] },
          { type: 'list', items: [
            '任何时候点顶栏「帮助」都能打开本手册；左侧按分组切换主题，搜索框过滤关键词，「阅读全部」可一次浏览完整内容。',
            '按 Esc、点遮罩或点右上角「关闭」退出帮助。帮助内容完全本地，不请求任何外部网络资源。'
          ] }
        ]
      },
      {
        id: 'quickstart', group: '入门', title: '十分钟走通全流程',
        lead: '按下面顺序点一遍，就能理解系统的主要能力。每一步的写入都会产生一条 Commit，可在「版本」Tab 看到。',
        blocks: [
          { type: 'steps', items: [
            { t: '1 · 创建空间', d: '空状态卡片填写名称与标识（slug 可留空），点「创建空间」；也可用顶栏「+ 空间」随时新建。' },
            { t: '2 · 定义类型', d: '中栏「+ 定义类型」：填 slug（小写字母开头，仅 a-z0-9_）、显示名、绑定目录（如 /论文库），逐行添加字段并配置必填 / 索引 / 枚举 / MCP 暴露。' },
            { t: '3 · 观察联动', d: '保存后目录树自动出现绑定节点，下方动态表单立刻按 Schema 生成控件；「MCP 工具」Tab 的 tools vN 递增并多出 query_/create_/update_ 三件工具。' },
            { t: '4 · 创建文档', d: '在动态表单填字段点「创建文档」。文档 = 元数据 + 1..N 个 Part，不是某一种固定格式。' },
            { t: '5 · 挂接多媒介', d: '「文档」Tab 点开文档 →「阅读器」Tab →「+ Part」可添加 Markdown、代码、PDF 文本层、网页快照；同一份文档可同时拥有四类 Part。' },
            { t: '6 · 导入真实媒介', d: '粘贴 URL 点「抓取正文」（独立 worker + Readability），或粘贴 HTML 快照离线提取；选择本地 PDF 上传，用 pdf.js 翻页阅读。' },
            { t: '7 · 标注', d: '在「标记 · 锚点」区选高亮 / 下划线 / 批注，填引用原文、行号、页码或 DOM 路径后添加。锚点丢失时用「重新定位到当前选中」修复，「导出为笔记」汇总为新的 Markdown Part。' },
            { t: '8 · 版本与回滚', d: '「版本」Tab 看提交时间线、选中两次提交看并排 diff、点「撤销选中提交」生成反向提交（不改写历史）。' },
            { t: '9 · 用模板加速', d: '「模板」Tab 预览 / 安装内置模板；「导出为 Pack JSON」分享当前空间结构，或粘贴自定义 Pack 后「导入到当前空间」。' },
            { t: '10 · 外观与移动端', d: '顶栏切换四套浅色主题（浅绿 / 浅蓝 / 浅紫 / 浅粉）；窄屏自动切换为抽屉与底部导航。' }
          ] },
          { type: 'note', title: '想跳过手工建模？', items: [
            '直接安装「通用阅读台」模板：5 个目录 / 4 个内容类型并带示例文档，演示一份文档同时挂 PDF 原文、Markdown 笔记、网页快照与复现代码。',
            '安装「商业短篇创作工作台」模板会得到 15 个目录 / 12 个内容类型，用来观察旧工作台如何降为一份纯数据模板。'
          ] }
        ]
      },
      {
        id: 'ui', group: '入门', title: '界面导览',
        lead: '整个应用是单页：左侧管结构，中间管 Schema 与录入，右侧五个 Tab 管内容、阅读、版本、Agent 与模板。',
        blocks: [
          { type: 'table', head: ['区域', '作用'], rows: [
            ['顶栏', '空间切换 / 新建空间、toolsVersion 与能力芯片、四套主题、帮助入口'],
            ['左栏「目录」', '无限层级物化路径树；「+ 新建目录」可建纯目录或绑定内容类型的集合'],
            ['中栏「内容类型」', 'Schema Builder 定义字段；下方动态表单由 /render 契约生成，直接创建文档'],
            ['Tab「文档」', '当前空间的文档列表，点卡片打开阅读器'],
            ['Tab「阅读器」', '四类 Part 切换与编辑、真实媒介导入、标注与锚点重定位、导出笔记'],
            ['Tab「版本」', '提交时间线、并排 diff、撤销选中提交'],
            ['Tab「MCP 工具」', '实时工具清单与 toolsVersion，Schema 改动后可观察到新工具'],
            ['Tab「模板」', '内置模板预览 / 安装 / 导出 / 导入自定义 Pack JSON']
          ] },
          { type: 'list', items: [
            '顶栏「能力检测中…」芯片来自 GET /capabilities，会显示当前实例的鉴权、数据库、渲染器与抓取能力。',
            '窄屏（≤768px）下三栏折叠为单列，阅读器工具条改为纵向排列。'
          ] }
        ]
      },
      {
        id: 'space', group: '核心操作', title: '空间与目录',
        lead: '空间是最外层隔离单位；目录是物化路径树，节点既可以只是目录，也可以绑定内容类型成为「集合」。',
        blocks: [
          { type: 'steps', items: [
            { t: '新建 / 切换空间', d: '空状态卡片或顶栏「+ 空间」创建；下拉框切换。切换会清空阅读器与版本面板的选中状态，避免跨空间串数据。' },
            { t: '新建目录', d: '左栏「+ 新建目录」填 /路径，第二栏可填要绑定的内容类型 slug（留空即纯目录）。' },
            { t: '用 Schema 自动建目录', d: '定义内容类型时填「绑定目录」，保存后会自动创建节点并绑定类型，通常不需要再手工建一次。' }
          ] },
          { type: 'note', title: '目录也是 Agent 的资源树', items: [
            '目录结构会通过 MCP 的 tree / resources/list 暴露给 Agent；节点是否暴露由 Schema 的 exposeToMcp 决定。',
            '每个空间有独立的一条 Commit 链，跨空间不共享版本历史。'
          ] }
        ]
      },
      {
        id: 'schema', group: '核心操作', title: '内容类型与动态 Schema',
        lead: 'ContentTypeDefinition 是校验、动态渲染、检索索引与 MCP 工具签名的唯一事实来源。改一次，四处同时生效，无需重启或发版。',
        blocks: [
          { type: 'table', head: ['字段类型', '适用内容'], rows: [
            ['text / longtext / markdown', '短文本、长文本、Markdown 正文'],
            ['code / number / date / boolean', '代码、数值（支持 min/max/integer）、日期、布尔'],
            ['select / multi_select / tags / url', '单选、多选、标签、链接（枚举必须提供 options）'],
            ['ref / json / file / user', '关联引用、结构化 JSON、附件、人员']
          ] },
          { type: 'list', items: [
            '每个字段可设置：必填、唯一、索引、默认值、校验规则、枚举选项、是否暴露给 MCP、是否允许 MCP 写入、排序。',
            'exposeToMcp=false 的字段完全不出现在工具签名里；mcpWritable=false 的字段只出现在查询工具，不出现在写工具。',
            '类型定义带 version：升级后新增字段回填默认值、删除字段归档到 _archived_fields、类型变更必须显式 cast，否则返回 422 SCHEMA_MIGRATION。',
            '列表列、表单控件、筛选与校验都由 GET /spaces/{id}/nodes/{nodeId}/render 驱动，界面没有为某个业务写死页面。'
          ] },
          { type: 'warn', title: '建模注意', items: [
            'slug 必须匹配小写字母开头的 a-z0-9_（最长 40 字符）；字段 key 同样有命名约束，且不能重复。',
            'select / multi_select 必须提供 options；新增必填字段必须提供 defaultValue 才能迁移已有文档。'
          ] }
        ]
      },
      {
        id: 'doc', group: '核心操作', title: '文档与四类阅读器',
        lead: '文档是载体，不是格式：同一份文档可同时挂 Markdown 笔记、代码片段、PDF 原文和网页快照。所有写回落到 Part + Commit。',
        blocks: [
          { type: 'table', head: ['媒介', '阅读 / 编辑', '标记锚点'], rows: [
            ['Markdown', '源码 + 预览双模式，可编辑并产生行级 diff', 'textQuote / line'],
            ['Code', 'Monaco 0.52.2（语言识别、行号），可编辑；失败时降级 textarea', 'line + textQuote'],
            ['PDF', 'pdf.js 4.10.38 canvas + 文本层翻页，原件只读', 'page + rect + textQuote'],
            ['Web', 'Readability 正文 / 纯文本 / 原文快照三模式，可重抓', 'domPath + textQuote']
          ] },
          { type: 'steps', items: [
            { t: '添加 Part', d: '阅读器工具条「+ Part」选类型、填内容与原文 URL；PDF 文本层用字面 \\f 分页或每行一段。' },
            { t: '编辑可写 Part', d: 'Markdown / Code 在源码模式修改后点「保存」；PDF 与 Web 原件不可编辑，改动以标注或重抓方式记录。' },
            { t: '切换模式', d: '工具条按媒介出现「源码 / 预览」「编辑 / 阅读」「正文 / 纯文本 / 原文快照」等按钮，PDF 另有上/下页。' },
            { t: '按历史版本阅读', d: '阅读器头部下拉可选某个 Commit 作为阅读快照，配合「版本」Tab 做前后对照。' }
          ] },
          { type: 'note', title: '零 CDN', items: [
            'pdf.js 与 Monaco 全部本地打包在 /vendor/，离线环境可加载；加载失败会降级为文本层 / textarea，不会白屏。'
          ] }
        ]
      },
      {
        id: 'media', group: '核心操作', title: '真实媒介：网页抓取与 PDF 上传',
        lead: '网页抓取走独立的 fetch-worker（Node 22 + Readability + linkedom），PDF 原件存内容寻址 blob，前端只保留哈希引用。',
        blocks: [
          { type: 'steps', items: [
            { t: '在线抓取', d: '「阅读器」→「+ Part」→ 填 https:// 地址点「抓取正文」。服务端调用 worker 的 /fetch，抽取正文并做白名单清洗，新增或重抓一个 web Part。' },
            { t: '离线快照', d: '把完整 HTML 粘进下方文本框再抓取，走 /extract，不依赖公网；回归测试就是用这条路径。' },
            { t: '上传 PDF', d: '选择本地 .pdf 后点「上传 PDF」。服务端校验 %PDF- 文件头与大小，写入 blobs/<sha256>.pdf，Part 只存 blob:sha256:<hash>。' },
            { t: '读回原件', d: '新上传的 PDF 由 pdf.js canvas 渲染；老式 \\f 文本层 Part 仍以兼容模式显示。' }
          ] },
          { type: 'warn', title: '抓取与上传限制', items: [
            'worker 有八类 SSRF 防护：协议、端口、DNS 解析、私网 IP、云元数据、逐跳重定向、域名白名单、大小 + 超时；单次 allowedHosts 只能比全局更严。',
            'worker 单次响应上限 2 MiB、超时 9 秒；PDF 原件上限 20 MiB。',
            '短 HTML 片段或缺少完整文档时 Readability 会走 readability-fallback，属于诚实回退而非缺陷。',
            '不配置 EPISTEME_FETCH_WORKER_URL 时抓取返回 503 FETCH_WORKER_UNAVAILABLE，其余功能不受影响。'
          ] }
        ]
      },
      {
        id: 'annotation', group: '核心操作', title: '标注与稳健锚点',
        lead: '标注是一等公民：进入不可变版本快照，可被 diff 与 revert 恢复；内容变化后锚点自动重定位，绝不静默丢弃。',
        blocks: [
          { type: 'steps', items: [
            { t: '选中文本', d: '在阅读器里划选文字会自动填入「选中文本 / 引用原文」；也可手工填写。' },
            { t: '选类型与位置', d: '类型支持高亮 / 下划线 / 批注 / 区域 / 书签；按媒介填行号（md、code）、页码（pdf）或 DOM 路径（web）。' },
            { t: '添加与审阅', d: '点「添加标记」后在下方列表查看状态：anchored / relocated / lost。非法锚点（越界行号、不存在的引文、缺定位信息）返回 422 ANCHOR_INVALID。' },
            { t: '重定位与导出', d: 'lost 标注点「重新定位到当前选中」写新锚点并产生 Commit；「导出为笔记」把跨 Part 标注汇总成新的 md Part。' }
          ] },
          { type: 'note', title: '锚点为什么稳健', items: [
            '每个锚点写入时归一化，带 fingerprint / textPosition / fallback，按 DOM path → textQuote → 行号 → 指纹四级回退重定位。',
            '标注归属产生它的那条 Commit，因此历史快照里也包含完整标注集。'
          ] }
        ]
      },
      {
        id: 'version', group: '核心操作', title: '版本、diff 与回滚',
        lead: '界面、REST、MCP 三条写入路径共享同一条 Commit 链：AI 的每次编辑同样可追溯、可比较、可撤销。',
        blocks: [
          { type: 'steps', items: [
            { t: '看历史', d: '「版本」Tab 显示提交时间线，含来源（ui / api / mcp / import）、作者、理由与统计。' },
            { t: '比较', d: '选中提交查看字段级、Part 逐行、标注级 diff；也可在 API 侧用 GET /spaces/{id}/diff?base=...&target=...。' },
            { t: '回滚', d: '点「撤销选中提交」生成一条反向提交，历史不被改写，内容回到该提交的父版本。' },
            { t: '分支与合并', d: 'REST/MCP 可用 branches / merges 建隔离分支再合并；Agent 写入建议放在 agent/xxx 分支，人工审阅后再合入 main。' }
          ] },
          { type: 'list', items: [
            '写请求可带 idempotencyKey（重放返回原 Commit，不产生第二条）与 expectParent（HEAD 已变返回 409 CONFLICT_PARENT）。',
            '还可带 source / reason / actor / agentId，全部写入审计日志，可在 GET /spaces/{id}/audit 查询。',
            '实现是「git 语义 + 数据库内容寻址 DAG」（Blob → Tree → Commit → Ref），不能直接把数据目录当 git 仓库用；提供导出为线性提交清单的补偿能力。'
          ] }
        ]
      },
      {
        id: 'pack', group: '核心操作', title: '模板 Pack：安装、导出与导入',
        lead: 'Pack 是纯数据（目录 + 内容类型 + 视图 + 可选示例数据），安装不执行任何用户代码，一次事务内完成并产生一条 source=import 的 Commit。',
        blocks: [
          { type: 'table', head: ['内置模板', '内容'], rows: [
            ['通用阅读台（pack_reading-desk）', '5 个目录 / 4 个内容类型 + 示例文档，演示多 Part 组合'],
            ['商业短篇创作工作台（workbench）', '15 个目录 / 12 个内容类型，旧工作台降为模板']
          ] },
          { type: 'steps', items: [
            { t: '预览', d: '「模板」Tab 点「预览」查看目录数、类型数与校验结果，不写入任何数据。' },
            { t: '安装', d: '点「安装」；重复安装同一模板会返回 409 PACK_CONFLICT，除非显式覆盖。' },
            { t: '导出', d: '勾选「包含文档作为示例数据」后点「导出为 Pack JSON」，结果出现在下方文本框。' },
            { t: '导入', d: '把自定义或他人分享的 Pack JSON 粘进文本框，点「导入到当前空间」；非法模板（ID 非法 / 引用未声明类型）返回 422 PACK_INVALID。' }
          ] },
          { type: 'note', title: '进阶参数（REST / MCP）', items: [
            'dryRun 只预览不落库；overwrite 允许覆盖同名对象；includeSamples 连带导入示例文档。',
            '安装支持幂等键：同一键重放返回原 Commit，不会重复创建目录。'
          ] }
        ]
      },
      {
        id: 'mcp', group: 'AI 与接口', title: 'MCP 与 AI Agent 协作',
        lead: 'MCP 工具按当前目录树与 Schema 实时生成：你新建一个内容类型，Agent 立刻获得对应的查询 / 新建 / 更新工具，无需重启服务。',
        blocks: [
          { type: 'table', head: ['传输', '接入方式'], rows: [
            ['Streamable HTTP（主）', 'POST /mcp?space=<slug>，JSON-RPC 2.0，协议版本 2025-06-18'],
            ['SSE', 'GET /mcp/sse 拿到 /mcp/messages?sessionId=...，再 POST 请求'],
            ['stdio', '编译 src/Episteme.McpStdio，本地 Agent 用它把 stdin 请求转发到 HTTP MCP']
          ] },
          { type: 'steps', items: [
            { t: '先看工具清单', d: '「MCP 工具」Tab 或 tools/list：30 个静态工具 + 每个 exposeToMcp 类型派生的 3 个动态工具。Schema 改过要重新拉一次。' },
            { t: '在隔离分支试写', d: '调用时带 branch=agent/xxx、reason、agentId、idempotencyKey，必要时带 expectParent 做乐观锁。' },
            { t: '人工审阅后合并', d: '在「版本」Tab 看 diff，确认后用 merges 合入；不满意用 revert 撤销。' },
            { t: '控制可见性', d: '字段级 exposeToMcp / mcpWritable 决定 Agent 能看见和能改什么；agent 角色默认无权修改 Schema（FORBIDDEN_SCOPE）。' }
          ] },
          { type: 'code', text: 'POST /mcp?space=my-lib\n{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}\n\nPOST /mcp?space=my-lib\n{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"episteme.create_paper","arguments":{"title":"草稿","branch":"agent/draft","reason":"自动整理"}}}' },
          { type: 'warn', title: 'MCP 当前实现边界', items: [
            'toolsVersion 单调递增并通过 X-Tools-Version 回写，但 notifications/tools/list_changed 主动推送尚未实现，请重新调用 tools/list。',
            'resources/list 只列暴露的节点（episteme://{spaceId}{nodePath}）；resources/read 目前返回提示文本，实际读取请用 tree / read 工具。',
            'MCP 是自实现的最小 JSON-RPC，未使用官方 SDK；SSE 会话只在单进程内存，多实例部署需共享背板。'
          ] }
        ]
      },
      {
        id: 'api', group: 'AI 与接口', title: 'REST API 速查',
        lead: '所有 REST 路径以 /api/v1/episteme 为前缀；响应统一 JSON，业务错误为 { code, message, traceId }。',
        blocks: [
          { type: 'table', head: ['分组', '代表路径'], rows: [
            ['空间 / 目录', 'GET|POST /spaces、GET /spaces/{id}/tree、POST /spaces/{id}/nodes'],
            ['Schema', 'GET|POST /spaces/{id}/content-types、.../{slug}/validate、/nodes/{nodeId}/render'],
            ['文档 / Part', '/spaces/{id}/documents、/nodes/{nodeId}/documents、/documents/{docId}/parts'],
            ['标注 / 检索', '/documents/{docId}/annotations、/exports/notes、/spaces/{id}/search'],
            ['版本', '/commits、/diff、/blame、/refs、/branches、/merges、/reverts、/tags'],
            ['模板 / 调试', '/packs、/spaces/{id}/packs/install|import|export、/mcp/tools、/audit、/capabilities']
          ] },
          { type: 'code', text: "curl -H 'Content-Type: application/json' -H 'X-Episteme-Role: owner' \\\n  -d '{\"name\":\"我的资料库\",\"slug\":\"my-lib\"}' \\\n  http://127.0.0.1:5080/api/v1/episteme/spaces" },
          { type: 'list', items: [
            'legacy 模式用 X-Episteme-Role 声明角色（owner / editor / agent / viewer），缺省 owner；secure 模式改 JWT 或 HMAC 签名头。',
            '常见状态码：401 UNAUTHORIZED、403 FORBIDDEN_SCOPE、409 CONFLICT_PARENT / PACK_CONFLICT / PART_NOT_EDITABLE、422 PACK_INVALID / ANCHOR_INVALID、429 RATE_LIMITED、503 FETCH_WORKER_UNAVAILABLE。',
            '探活：GET /health、GET /api/v1/episteme/capabilities、GET /api/v1/episteme/fetch-worker/health。'
          ] }
        ]
      },
      {
        id: 'safety', group: '注意事项', title: '安全与部署注意事项',
        lead: '生产化能力全部是环境变量开关，默认 legacy + SQLite + 不鉴权，这样默认体验最简单，也意味着默认不适合直接暴露到公网。',
        blocks: [
          { type: 'warn', title: '上线前必须处理', items: [
            'deploy/.env 默认 EPISTEME_BIND_IP=0.0.0.0 且 EPISTEME_AUTH_MODE=legacy。至少改成 127.0.0.1 或前置反向代理，并把 AUTH_MODE 设为 secure。',
            'secure 模式必须配置足够长的 EPISTEME_AUTH_SECRET；可选 EPISTEME_API_KEYS 登记 keyId:role。JWT 只做 HS256 签名 / 过期 / 角色校验，没有 issuer、audience、刷新与撤销列表。',
            '服务端不终止 TLS，也没有 WAF；请在网关层做 HTTPS、证书与访问控制。限流是全局固定窗口（默认每分钟 600 次），不是按用户分区。',
            '抓取 worker 只在 compose 内网可达（expose 8090，不发布宿主端口）；上线前用 EPISTEME_FETCH_ALLOWLIST 与受控 DNS / 出网策略复验 /fetch 全链路。',
            '抓取外部网页或 PDF 的版权与合规由使用者负责；系统保留原文链接与来源标注。'
          ] },
          { type: 'list', items: [
            '数据目录 EPISTEME_DATA_DIR（容器 /app/data）含 episteme.db 与 blobs/；备份请复制整个目录，不要只导出单个表。',
            '数据库启动执行 EnsureCreatedAsync()，没有迁移脚本；跨版本升级前先备份并自行迁移。',
            '切换 PostgreSQL 需同时设置 EPISTEME_DB_PROVIDER=postgres 与 EPISTEME_PG_CONNECTION；PG 容器默认不发布宿主端口。',
            '.env 文件被纳入版本跟踪，切勿把真实密钥提交进仓库。'
          ] }
        ]
      },
      {
        id: 'limits', group: '注意事项', title: '功能边界与已知限制',
        lead: '这些是明确未做到或有意简化的部分，避免把原型能力误当成生产保证。',
        blocks: [
          { type: 'table', head: ['项目', '现状'], rows: [
            ['Android', '骨架源码，未编译 APK、未真机验证；富阅读器计划由 WebView 承载'],
            ['桌面 GUI', '容器内无 X server，只验证编译、ViewModel 与 REST/MCP 客户端，未实际开窗'],
            ['多人协同', '无 CRDT / OT 实时协同，靠 expectParent 乐观锁与冲突提示'],
            ['SSE / 多实例', '会话只在单进程内存；未接 Redis 背板、对象存储、密钥托管与审计归档'],
            ['阅读器', 'Monaco / pdf.js 加载失败自动降级；老式 \\f PDF 文本层保持兼容显示'],
            ['secure 模式', '最小实现：无 JWT 刷新 / 撤销、无 TLS 终止、无限流按用户分区'],
            ['MCP', '自实现最小 JSON-RPC；resources/read 仅提示；无 tools/list_changed 主动推送'],
            ['在线抓取', '回归基于离线 /extract 快照，公网 /fetch 需在受控环境复验']
          ] },
          { type: 'note', title: '设计取舍', items: [
            '选择「git 语义 + 数据库内容寻址」而非真 git 仓库，换取事务一致性、行级权限与 MCP 写原子性；代价是不能直接用外部 git 客户端。',
            '所有生产化开关默认关闭，保证既有 REST / MCP / UI 契约零回归；开启后路径前缀 /api/ 与 /mcp 会受保护。'
          ] }
        ]
      },
      {
        id: 'troubleshoot', group: '注意事项', title: '常见问题排查',
        lead: '先看 /health 与 /capabilities，再按现象定位。',
        blocks: [
          { type: 'table', head: ['现象', '检查'], rows: [
            ['首页 404 / 空白', '镜像是否完成 dotnet publish；wwwroot/index.html 是否随发布输出；看容器日志'],
            ['抓取 503 FETCH_WORKER_UNAVAILABLE', 'EPISTEME_FETCH_WORKER_URL 是否配置；fetch-worker 是否健康（/fetch-worker/health）'],
            ['抓取被拒或内容为空', '全局与单次域名白名单、私网 / 重定向 / 端口限制；短 HTML 走 Readability 回退属正常'],
            ['secure 模式 401 / 403', 'AUTH_SECRET 是否配置；JWT 的 role claim；HMAC 头 X-Episteme-Key/Timestamp/Nonce/Signature 与 API_KEYS 角色是否一致'],
            ['PDF 上传失败', '文件头必须是 %PDF-，大小 1 字节 ~ 20 MiB，base64 必须合法'],
            ['Schema 保存报 422', 'slug / 字段 key 命名、枚举 options、必填字段 defaultValue、字段类型变更的 cast 规则'],
            ['端口被占用', '改 deploy/.env 的 EPISTEME_HTTP_PORT；本地运行改 ASPNETCORE_URLS'],
            ['想恢复空壳', 'docker compose ... down -v 后重新 up -d --build（会删除全部数据）']
          ] }
        ]
      },
      {
        id: 'glossary', group: '注意事项', title: '术语表与更多资料',
        lead: '理解这几个词，就能读懂界面与接口里的所有提示。',
        blocks: [
          { type: 'table', head: ['术语', '含义'], rows: [
            ['Space 空间', '独立的版本 DAG 根、权限边界与 MCP 上下文'],
            ['ContentType 内容类型', '版本化 Schema，定义字段、视图与 MCP 暴露策略'],
            ['Node 节点', '物化路径目录，可绑定内容类型成为集合'],
            ['Document / Part', '文档 = 元数据 + 1..N 个媒介部件；Part 才是真正被阅读的内容'],
            ['Annotation 标注', '高亮 / 批注等标记，带稳健锚点并进入版本快照'],
            ['Commit / Ref', '内容寻址提交与分支 / 标签引用；revert 生成反向提交'],
            ['Pack 模板', '声明式 JSON，一次事务安装目录与 Schema'],
            ['toolsVersion', '工具清单版本号，Schema / 目录 / Pack 变更后递增']
          ] },
          { type: 'list', items: [
            '设计规格：SPEC.md、docs/01–06；阶段实现与验收：docs/07–10。',
            '完整构建、配置、API 示例与回归命令见仓库根目录 README.md。',
            '单文件交互原型：prototype/index.html（可直接用浏览器打开，零依赖）。'
          ] }
        ]
      }
    ]
  };
})();
