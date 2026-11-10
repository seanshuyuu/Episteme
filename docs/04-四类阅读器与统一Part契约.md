# 04 · 四类阅读器与统一 Part 契约

> 核心抽象：**文档不是格式，格式是文档的 Part**。四种媒介是一等公民渲染器，共享同一套读取、编辑、标记、版本接口。

---

## 1. 统一 Viewer 契约

每个 Viewer 实现同一组能力，界面与 MCP 都只面向这个契约，不关心底层是 MD 还是 PDF：

```ts
interface Viewer {
  kind: 'md' | 'code' | 'pdf' | 'web' | 'attachment';
  mount(part: Part, ctx: ViewerContext): void;      // 渲染
  read(): PartContent;                              // 取当前内容（用于保存 / diff）
  edit?(patch: PartPatch): void;                    // 仅可编辑媒介实现
  annotate(a: AnnotationDraft): Annotation;         // 四种都支持
  snapshot(): PartSnapshot;                         // 交给 Commit 的哈希输入
  capabilities(): { editable, annotatable, searchable, exportable };
  destroy(): void;
}
```

`ViewerContext` 带：`docId / partId / ref(commitId|branch) / permissions / theme / onChange / onAnnotate / onError`。

**写入闭环**：Viewer 的每次 `edit` / `annotate` → 生成 Patch → 服务端计算新 Blob / Annotation → 生成 Commit → 广播 → Viewer 按 `ref` 重绘。
只读 `?ref=` 快照视图下 `edit` 被禁用，但 `annotate` 可进入「对历史版本的批注」模式（标注归属该 commit，不改动历史内容）。

---

## 2. Part 数据模型

```json
{
  "id": "pt_01J...",
  "documentId": "doc_01J...",
  "kind": "pdf",
  "ord": 0,
  "blobHash": "sha256:...",
  "content": null,
  "mime": "application/pdf",
  "language": null,
  "sourceUri": "https://example.com/paper.pdf",
  "meta": {
    "pages": 12,
    "textLayer": true,
    "fetchedAt": "2026-11-09T01:00:00Z",
    "httpStatus": 200,
    "title": "原文标题",
    "author": "...",
    "origin": "external"
  }
}
```

| kind | `content` | `blobHash` | 典型 `meta` |
|---|---|---|---|
| `md` | 短文本直存 | ✔（版本真相） | wordCount、frontMatter |
| `code` | 代码直存 | ✔ | language、lineCount、encoding |
| `pdf` | – | ✔ | pages、textLayer、encrypted |
| `web` | 抽取后的纯文本可直存 | ✔（HTML 快照） | sourceUri、fetchedAt、httpStatus、extractor |
| `attachment` | – | ✔ | fileName、size、mime |

**一份文档多个 Part**：例如「论文」文档 = `pdf`（原文）+ `md`（阅读笔记）+ `web`（作者博客快照）+ `code`（复现脚本）。Tab 切换，右侧统一显示版本时间线与标注列表。这是比「四类文档各自独立」更贴合真实知识工作的抽象。

---

## 3. Markdown 阅读 / 编辑

| 项 | 规格 |
|---|---|
| 渲染 | GFM（表格、任务列表、删除线）、代码块高亮、Mermaid 图、脚注、数学公式（KaTeX，按需加载） |
| 编辑 | 双栏（源码 / 预览）、纯源码、沉浸三种模式；CodeMirror 6 |
| front-matter | YAML front-matter 与 Schema 字段**双向映射**：改标题字段同步 front-matter，反之亦然；冲突时以 Schema 为准并提示 |
| 内部链接 | `[[docId → 显示名]]` 解析为 `ref` 字段，形成对称引用与关系图 |
| 标记 | 行内批注（选中文本 → 批注），锚点见 §5 |
| 版本 | 行级 LCS diff；支持并排 / 内联；blame 显示每段由谁写入 |
| 导出 | `.md` / HTML / PDF（打印样式） |

---

## 4. Code 阅读 / 编辑

| 项 | 规格 |
|---|---|
| 编辑器 | Monaco Editor（与 VS Code 同源），按 `language` 或扩展名自动识别 60+ 语言 |
| 阅读 | 语法高亮、行号、折叠、小地图、大纲符号、括号匹配 |
| 编辑 | 可切换只读 / 编辑；编辑态提供保存、格式化（可选）、查找替换 |
| 智能 | P3 可选接 LSP（跳转定义 / 悬停文档 / 诊断）；无 LSP 时降级为纯高亮 |
| 标记 | 行级批注（点行号加注）、行范围高亮；与 §02 blame 联动，直接看出该行来自哪次 Commit、是 Agent 还是人写的 |
| 版本 | 逐行 diff，语法着色保留；大文件（> 200 KB）降级段落级 |
| 边界 | **不做在线运行 / 编译执行**（安全），仅阅读与编辑；仓库同步走外部工具 |

---

## 5. PDF 阅读与标记

| 项 | 规格 |
|---|---|
| 渲染 | pdf.js 文本层 + canvas，缩放 / 翻页 / 缩略图 / 双页 / 搜索 |
| 不可编辑 | PDF 原件只读；所有「修改」都以标注形式独立存储 |
| 标记类型 | 高亮、下划线、便签批注、矩形区域标记、页码书签 |
| 标注侧栏 | 按页 / 颜色 / 作者 / 关键词筛选，点击定位，可导出为 Markdown 笔记 |
| 文本层缺失 | 扫描件提示「无可选取文本」，降级为区域标记 + OCR 占位（OCR 不在 P3 范围） |

### 5.1 稳健锚点（关键设计）

PDF 重排版 / 换版本后标记不能飘。锚点存四层冗余，定位时逐级回退：

```json
{
  "version": 1,
  "kind": "pdf",
  "page": 4,
  "rect": { "x": 72.5, "y": 310.2, "w": 180.4, "h": 14.2 },
  "textQuote": {
    "exact": "内容寻址快照 DAG",
    "prefix": "第二章 ",
    "suffix": " 保证事务一致性"
  },
  "textPosition": { "start": 1284, "end": 1293 },
  "pdfFingerprint": "sha256:...",
  "fallback": "page-rect"
}
```

定位顺序：`pdfFingerprint + textPosition` → `textQuote（W3C Text Quote Selector 思路，容模糊匹配）` → `page + rect` → 标记 `ANNOTATION_ANCHOR_LOST` 并高亮为「需重新定位」，**绝不静默丢弃标注**。
Web 标注把 `page/rect` 换成 `domPath + textQuote + textPosition`，其余一致。

---

## 6. Web 网页阅读

| 项 | 规格 |
|---|---|
| 入库 | URL → 独立抓取 worker → Readability 抽取正文 → 保存 **HTML 快照 + 纯文本 + 元数据 + 原文 URL** |
| 阅读模式 | 抽取正文（统一排版、去掉导航广告）/ 原文快照（沙箱 iframe、禁脚本）/ 纯文本，三态切换 |
| 标记 | 划词高亮 / 批注，锚点同 §5.1 的 Web 变体；快照更新后自动重定位 |
| 失效检测 | 定时 HEAD 检查原文可达性，失效时在文档上显示「原文已失效（保留快照于 …）」 |
| 重抓 | 手动重抓生成新 Commit；diff 展示正文新增 / 删除段落与 URL 变化 |
| 边界 | 不执行页面脚本、不跟随登录态、不抓取需认证内容；版权与合规由使用者负责，UI 常驻「外部内容 / 原文链接」标识 |

---

## 7. 附件与其他媒介

未识别类型降级为 `attachment`：展示元数据、预览（图片 / 文本 / 视频按浏览器能力）、下载、来源标注。扩展新 Viewer 只需注册 `kind → 渲染器` 并实现 §1 契约，不动内核。

---

## 8. 通用能力（四类共享）

- **版本时间线**：右侧抽屉列出该文档所有 Commit，点击任一条进入只读 diff；`revert` 直接在这里触发。
- **标注汇总**：跨 Part 汇总一本书 / 一篇论文的全部高亮与批注，一键导出为 Markdown 笔记（新建 `md` Part 并生成 Commit）。
- **全局搜索**：PostgreSQL FTS + `zhparser` 中文分词；PDF / Web 抽取文本也进索引；结果按 Part 类型分组，命中处高亮。
- **命令面板**：`Ctrl/Cmd+K` 搜索文档 / 跳转版本 / 执行 MCP 工具预览。
- **主题**：沿用 `design/tokens.json` 四套浅色主题；PDF / 代码高亮有独立暗色可选（阅读器内部），不污染全局主题真源。
- **三端差异**：Web 提供完整编辑与四类加载；Avalonia 桌面 / Android 提供目录浏览、Schema 表单、Markdown/Code 轻量编辑与**标注审阅**，PDF/Web 富渲染通过内置 WebView 或跳转 Web 端完成（承接方案 A 的两套 UI 边界）。

---

## 9. 性能与非功能

| 项 | 目标 |
|---|---|
| Markdown / Code 首屏 | < 300 ms（内容直存，按需加载编辑器） |
| PDF 首屏 | < 1.5 s（首屏页优先渲染，其余懒加载） |
| 大文件 | > 100 MB 分片 / 对象存储，PG 只存引用 |
| 编辑器包体 | CodeMirror / Monaco / pdf.js / Readability 全部**按 kind 懒加载**，未用到的不下载 |
| 标注定位 | 1000 条标注内 < 200 ms |
| 自动保存 | 编辑停止 2 s 后生成草稿（不产生 Commit），显式保存或 MCP 写才提交，避免历史被每敲一个字污染 |

---

## 10. 明确不做

- 不做 PDF 原件编辑 / 表单填写 / 数字签名。
- 不做浏览器内执行任意网页脚本、不做登录态抓取。
- 不做在线代码运行 / 编译。
- 不做 CRDT 多人同屏协同（采用乐观锁 + 冲突提示）。
- 不在 P3 引入 OCR（留作扩展点）。
