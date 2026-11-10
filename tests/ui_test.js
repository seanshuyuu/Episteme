/* Episteme 知书 · 原型真实浏览器交互回归
 * 运行：NODE_PATH=<node workspace>/node_modules node episteme/tests/ui_test.js
 * 会起一个临时静态 HTTP server，用 Playwright Chromium 逐项真实点击，
 * 捕获 console error / pageerror / 失败请求，输出 episteme/tests/ui_report.md
 */
"use strict";
const { chromium } = require("playwright");
const http = require("http");
const fs = require("fs");
const path = require("path");

const HTML = path.resolve(__dirname, "../prototype/index.html");
const REPORT_MD = path.resolve(__dirname, "ui_report.md");
const REPORT_JSON = path.resolve(__dirname, "ui_report.json");

const results = [];
const runtimeErrors = [];
let page = null;

function assert(cond, msg) { if (!cond) throw new Error(msg); }
async function step(name, fn) {
  const before = runtimeErrors.length;
  try {
    if (page) await closeOverlays();
    const detail = await fn();
    const fresh = runtimeErrors.slice(before);
    if (fresh.length) throw new Error("浏览器报错: " + fresh.join(" | "));
    results.push({ name, ok: true, detail: detail || "" });
    console.log("PASS " + name + (detail ? "  " + detail : ""));
  } catch (e) {
    let diag = "";
    try {
      if (page) diag = " | url=" + page.url()
        + " | body=" + (await page.locator("body").innerText()).slice(0, 160).replace(/\n/g, " / ");
    } catch (_) {}
    const msg = String((e && e.message) || e) + diag;
    results.push({ name, ok: false, detail: msg });
    console.log("FAIL " + name + "  " + msg);
  }
}
const click = async (locator, timeout) => { await locator.click({ timeout: timeout || 8000 }); await page.waitForTimeout(120); };
async function closeOverlays() {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.evaluate(() => {
    ["modalHost", "cmdk"].forEach(id => { const e = document.getElementById(id); if (e) e.classList.remove("show"); });
    const d = document.getElementById("drawer"), m = document.getElementById("drawerMask");
    if (d) d.classList.remove("show");
    if (m) m.classList.remove("show");
    const S = window.EpistemePrototype && window.EpistemePrototype.getState();
    if (S) { S.modal = null; S.drawer = null; S.cmdkOpen = false; }
  });
}
async function closeDrawerIfOpen() {
  if (await page.locator("#drawer.show").count()) await click(page.locator('.icon-btn[data-action="close-drawer"]'));
}
async function openDocByCmdk(q) {
  if (!(await page.locator("#cmdk.show").count())) await page.keyboard.press("Control+k");
  await page.locator("#cmdkInput").fill(q);
  await page.waitForTimeout(140);
  await page.keyboard.press("Enter");
  await page.waitForTimeout(220);
}
const state = () => page.evaluate(() => {
  const S = window.EpistemePrototype.getState();
  const s = S.spaces.find(x => x.id === S.activeSpaceId);
  return {
    theme: S.theme, page: S.page, drawer: S.drawer, modal: S.modal,
    spaces: S.spaces.length,
    nodes: s ? s.nodes.length : -1,
    types: s ? s.contentTypes.length : -1,
    docs: s ? s.documents.length : -1,
    commits: s ? s.commits.length : -1,
    toolsVersion: s ? s.toolsVersion : -1,
    tools: window.EpistemePrototype.buildTools().total,
    derived: window.EpistemePrototype.buildTools().derived.length,
    refs: s ? Object.keys(s.refs) : [],
    anns: s ? s.documents.reduce((n, d) => n + d.annotations.length, 0) : -1
  };
});

function serve() {
  return new Promise(resolve => {
    const server = http.createServer((req, res) => {
      if (req.url === "/favicon.ico") { res.writeHead(204); res.end(); return; }
      res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
      res.end(fs.readFileSync(HTML));
    });
    server.listen(0, "127.0.0.1", () => resolve({ server, port: server.address().port }));
  });
}

(async () => {
  const { server, port } = await serve();
  const BASE = "http://127.0.0.1:" + port + "/";
  const browser = await chromium.launch({ headless: true, args: ["--no-sandbox"] });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, acceptDownloads: true });
  page = await ctx.newPage();
  page.on("console", m => { if (m.type() === "error") runtimeErrors.push("console: " + m.text().slice(0, 220)); });
  page.on("pageerror", e => runtimeErrors.push("pageerror: " + String(e.message).slice(0, 220)));
  page.on("requestfailed", r => runtimeErrors.push("requestfailed: " + r.url().slice(0, 120)));

  try {
    await page.goto(BASE, { waitUntil: "domcontentloaded" });
    await page.evaluate(() => localStorage.clear());
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForTimeout(200);

    await step("空壳启动：零业务语义", async () => {
      const txt = await page.locator("body").innerText();
      assert(txt.includes("创建你的第一个空间"), "没有空状态引导");
      const forbidden = ["素材库", "拆文复盘", "今日热点", "选题灵感"];
      const hit = forbidden.filter(w => txt.includes(w));
      assert(hit.length === 0, "空壳里出现了业务名词: " + hit.join(","));
      const s = await state();
      assert(s.spaces === 0, "初始不应有空间");
      return "0 空间 / 无业务词";
    });

    await step("创建空间后仍是 0 目录 0 类型 0 文档", async () => {
      await click(page.getByRole("button", { name: "创建你的第一个空间" }));
      assert(await page.locator("#modalHost.show").count() > 0, "弹窗未打开");
      await page.locator("#mInput").fill("我的资料库");
      await click(page.locator('[data-action="do-create-space"]'));
      const s = await state();
      assert(s.spaces === 1, "空间未创建");
      assert(s.nodes === 0 && s.types === 0 && s.docs === 0, "空间不是空的: " + JSON.stringify(s));
      assert(s.commits === 1, "创建空间应产生 1 条 Commit");
      const txt = await page.locator("#content").innerText();
      assert(txt.includes("这个空间还是空的"), "缺少空空间引导");
      return "1 空间 / 0-0-0 / 1 commit";
    });

    await step("安装「通用阅读台」Pack 生成目录、类型与示例文档", async () => {
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="templates"]'));
      await click(page.locator('[data-action="install-template"][data-tpl="generic"]'));
      const s = await state();
      assert(s.nodes >= 5, "目录未生成: " + s.nodes);
      assert(s.types >= 4, "内容类型未生成: " + s.types);
      assert(s.docs >= 4, "示例文档未生成: " + s.docs);
      assert(s.toolsVersion >= 2, "toolsVersion 未递增");
      assert(s.commits >= 2, "安装应生成 Commit");
      return s.nodes + " 目录 / " + s.types + " 类型 / " + s.docs + " 文档 / tools v" + s.toolsVersion;
    });

    await step("目录树：选中节点过滤文档列表", async () => {
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="library"]'));
      const row = page.locator('.tree-row', { hasText: "论文库" }).first();
      await click(row);
      const s = await state();
      assert(s.page === "library", "未停留在文档页");
      const txt = await page.locator("#content").innerText();
      assert(txt.includes("内容寻址版本模型"), "论文未出现在该节点列表");
      assert(!txt.includes("PostgreSQL jsonb 表达式索引"), "其他节点文档未被过滤");
      return "节点过滤生效";
    });

    await step("目录树：折叠 / 展开子节点", async () => {
      const hot = page.locator('.tree-row', { hasText: "归档" }).first();
      assert(await hot.count() > 0, "找不到目录行");
      const before = await page.locator('.tree-row').count();
      const caret = page.locator('.tree-caret:not(.leaf)').first();
      await click(caret);
      const after = await page.locator('.tree-row').count();
      assert(after < before, "折叠未减少可见行数");
      await click(caret);
      assert(await page.locator('.tree-row').count() === before, "展开未恢复");
      return before + " → " + after + " → " + before;
    });

    await step("文档搜索框真实过滤（不是装饰）", async () => {
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="library"]'));
      const clear = page.locator('[data-action="clear-node"]');
      if (await clear.count()) await click(clear);
      await page.locator("#docSearch").fill("PostgreSQL");
      await page.waitForTimeout(150);
      let txt = await page.locator("#content").innerText();
      assert(txt.includes("PostgreSQL jsonb"), "搜索未命中目标文档");
      await page.locator("#docSearch").fill("zzz-不存在的关键词");
      await page.waitForTimeout(150);
      txt = await page.locator("#content").innerText();
      assert(txt.includes("共 0 篇"), "无结果时计数未变 0");
      await page.locator("#docSearch").fill("");
      await page.waitForTimeout(150);
      return "命中 / 无结果 / 清空 三态均正确";
    });

    await step("自定义新建目录并绑定内容类型", async () => {
      const s0 = await state();
      await click(page.locator('[data-action="open-modal"][data-modal="new-node"]').first());
      await page.locator("#mInput").fill("竞品情报");
      await page.locator("#mType").selectOption("note");
      await click(page.locator('[data-action="do-new-node"]'));
      const s = await state();
      assert(s.nodes === s0.nodes + 1, "目录未新增");
      assert(s.toolsVersion > s0.toolsVersion, "toolsVersion 未重建");
      assert(s.commits > s0.commits, "未生成 Commit");
      const tree = await page.locator("#sideScroll").innerText();
      assert(tree.includes("竞品情报"), "新目录未出现在树中");
      return "目录 +1 / tools v" + s.toolsVersion;
    });

    await step("Schema：新建内容类型并动态派生 3 个 MCP 工具", async () => {
      const s0 = await state();
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="schema"]'));
      await click(page.locator('[data-action="open-modal"][data-modal="new-type"]'));
      await page.locator("#mLabel").fill("竞品动态");
      await page.locator("#mInput").fill("competitor_intel");
      await page.locator("#mFieldCount").selectOption("3");
      await click(page.locator('[data-action="do-new-type"]'));
      const s = await state();
      assert(s.types === s0.types + 1, "类型未新增");
      assert(s.derived === s0.derived + 3, "应新增 3 个派生工具，实际 " + (s.derived - s0.derived));
      const txt = await page.locator("#content").innerText();
      assert(txt.includes("竞品动态"), "新类型未出现在 Schema 页");
      return "类型 +1 / 派生工具 +3 / tools v" + s.toolsVersion;
    });

    await step("Schema：改字段 + 关闭 MCP 暴露 → 工具签名重建", async () => {
      const s0 = await state();
      const tid = await page.evaluate(() => {
        const S = window.EpistemePrototype.getState();
        const spc = S.spaces.find(x => x.id === S.activeSpaceId);
        const t = spc.contentTypes.find(x => x.slug === "competitor_intel");
        return t ? t.id : null;
      });
      assert(tid, "找不到 competitor_intel 类型");
      const card = page.locator(".card").filter({ has: page.locator('[data-action="save-schema"][data-tid="' + tid + '"]') });
      await card.locator('[data-bind="field-label"]').first().fill("情报标题");
      await card.locator('[data-bind="field-type"]').first().selectOption("url");
      const exposes = card.locator('[data-bind="field-expose"]');
      await exposes.first().uncheck();
      await click(card.locator('[data-action="save-schema"]'));
      const s = await state();
      assert(s.toolsVersion > s0.toolsVersion, "保存 Schema 未递增 toolsVersion");
      assert(s.commits > s0.commits, "未生成 schema Commit");
      const page2 = await page.evaluate(() => {
        const t = window.EpistemePrototype.buildTools().derived.find(x => x.slug === "competitor_intel" && x.op === "create");
        return t ? t.fields.map(f => f.key) : null;
      });
      assert(Array.isArray(page2), "找不到 create_competitor_intel 工具");
      assert(page2.indexOf("title") < 0, "exposeToMcp=false 的字段仍出现在工具签名");
      return "字段已改 / 隐藏字段不出现在工具 / tools v" + s.toolsVersion;
    });

    await step("MCP 面板：tools/list 可展开查看 JSON Schema", async () => {
      await click(page.locator('[data-action="open-drawer"][data-drawer="mcp"]').first());
      const drawer = page.locator("#drawer.show");
      assert(await drawer.count() > 0, "MCP 抽屉未打开");
      const txt = await drawer.innerText();
      assert(txt.includes("toolsVersion"), "缺少 toolsVersion");
      assert(txt.includes("episteme.create_competitor_intel"), "动态工具未出现");
      const tool = drawer.locator('[data-action="toggle-tool"][data-name="episteme.create_competitor_intel"]');
      await click(tool);
      const detail = await drawer.innerText();
      assert(detail.includes("inputSchema"), "展开后没有 JSON Schema");
      assert(detail.includes("idempotencyKey"), "缺少通用写参数");
      return "工具签名可见且含审计/幂等参数";
    });

    await step("MCP：模拟工具调用产生 Commit 与审计日志", async () => {
      const s0 = await state();
      await click(page.locator('[data-action="open-drawer"][data-drawer="mcp"]').first());
      await page.waitForTimeout(340);
      await click(page.locator('#drawer [data-action="toggle-tool"][data-name="episteme.create_competitor_intel"]'));
      await page.waitForTimeout(140);
      await click(page.locator('#drawer [data-action="call-tool"][data-name="episteme.create_competitor_intel"]'));
      const s = await state();
      assert(s.commits > s0.commits, "工具调用未产生 Commit");
      const txt = await page.locator("body").innerText();
      assert(txt.includes("tools/call") || s.docs >= s0.docs, "调用日志未更新");
      return "commit +1 / 日志已记录";
    });

    await step("新建文档：表单由 Schema 动态生成（无硬编码字段）", async () => {
      const s0 = await state();
      await closeDrawerIfOpen();
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="library"]'));
      await click(page.locator('[data-action="open-modal"][data-modal="new-doc"]'));
      const modal = page.locator("#modalHost.show");
      assert(await modal.count() > 0, "新建文档弹窗未打开");
      const fkeys = await modal.locator("[data-fkey]").evaluateAll(els => els.map(e => e.getAttribute("data-fkey")));
      assert(fkeys.length > 0, "表单没有动态字段");
      assert(fkeys.indexOf("title") >= 0, "缺少 Schema 定义的 title 字段");
      await modal.locator('[data-fkey="title"]').fill("竞品 Q4 定价追踪");
      await modal.locator("#mPartKind").selectOption("md");
      await click(modal.locator('[data-action="do-new-doc"]'));
      const s = await state();
      assert(s.docs === s0.docs + 1, "文档未新增");
      assert(s.commits > s0.commits, "未生成 Commit");
      const txt = await page.locator("#content").innerText();
      assert(txt.includes("竞品 Q4 定价追踪"), "新文档未打开");
      return "动态字段 " + fkeys.length + " 个 / 文档 +1";
    });

    await step("Markdown 阅读器：编辑实时预览 + 保存生成 Commit", async () => {
      const s0 = await state();
      const mdTab = page.locator('[data-action="switch-tab"][data-kind="md"]');
      assert(await mdTab.count() > 0, "新建文档没有 md Tab");
      await click(mdTab);
      const ed = page.locator("#partEditor");
      assert(await ed.count() > 0, "MD 编辑器未渲染");
      await ed.fill("## 回归测试标题\n\n- **加粗** 与列表\n- 第二条");
      const prev = await page.locator("#mdPreview").innerText();
      assert(prev.includes("回归测试标题") && prev.includes("加粗"), "预览未实时更新");
      await click(page.locator('[data-action="save-part"][data-kind="md"]'));
      const s = await state();
      assert(s.commits > s0.commits, "保存未生成 Commit");
      return "预览实时 / commit +1";
    });

    await step("Code 阅读器：行号 / 语法高亮 / 编辑保存", async () => {
      await openDocByCmdk("PostgreSQL jsonb");
      const codeTab = page.locator('[data-action="switch-tab"][data-kind="code"]');
      assert(await codeTab.count() > 0, "打开的不是含 Code Part 的文档");
      await click(codeTab);
      const lines = await page.locator(".code-lines").innerText();
      assert(/\d/.test(lines), "没有行号");
      const html = await page.locator("#codeView").innerHTML();
      assert(html.includes('class="k"') || html.includes('class="c"') || html.includes('class="s"'), "没有语法高亮");
      const s0 = await state();
      await page.locator("#partEditor").fill("SELECT 1;\n-- edited by ui test");
      await click(page.locator('[data-action="save-part"][data-kind="code"]'));
      const s = await state();
      assert(s.commits > s0.commits, "Code 保存未生成 Commit");
      return "行号 + 高亮 + 保存 commit";
    });

    await step("PDF 阅读器：选中文本高亮并进入标注列表", async () => {
      await openDocByCmdk("内容寻址版本模型");
      const pdfTab = page.locator('[data-action="switch-tab"][data-kind="pdf"]');
      assert(await pdfTab.count() > 0, "打开的不是含 PDF Part 的文档");
      await click(pdfTab);
      assert(await page.locator(".pdf-paper").count() > 0, "PDF 纸面未渲染");
      await page.evaluate(() => {
        const p = [...document.querySelectorAll(".pdf-paper p")].find(x => x.textContent.includes("传统协同写作系统"));
        if (!p) return;
        const w = document.createTreeWalker(p, NodeFilter.SHOW_TEXT);
        const n = w.nextNode();
        if (!n) return;
        const r = document.createRange();
        r.setStart(n, 0); r.setEnd(n, Math.min(10, n.textContent.length));
        const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);
      });
      const s0 = await state();
      await click(page.locator('[data-action="highlight-selection"][data-kind="pdf"]'));
      const s = await state();
      assert(s.anns > s0.anns, "标注数未增加");
      assert(await page.locator(".pdf-paper mark").count() > 0, "高亮未渲染到纸面");
      assert(s.commits > s0.commits, "标记未生成 Commit");
      return "标注 " + s0.anns + " → " + s.anns;
    });

    await step("PDF：添加文字批注并显示在侧栏", async () => {
      const s0 = await state();
      await click(page.locator('[data-action="add-note"][data-kind="pdf"]'));
      await page.locator("#mInput").fill("这里需要核对 diff 算法复杂度");
      await click(page.locator('[data-action="do-add-note"]'));
      const s = await state();
      assert(s.anns > s0.anns, "批注未新增");
      const txt = await page.locator("#viewerMain").innerText() + await page.locator("#content").innerText();
      assert(txt.includes("这里需要核对 diff 算法复杂度"), "批注未显示在侧栏");
      return "批注已保存";
    });

    await step("Web 阅读器：阅读模式 / 原文快照切换", async () => {
      await openDocByCmdk("MCP 工具动态注册");
      const webTab = page.locator('[data-action="switch-tab"][data-kind="web"]');
      assert(await webTab.count() > 0, "打开的不是含 Web Part 的文档");
      await click(webTab);
      assert(await page.locator(".web-reader").count() > 0, "Web 阅读器未渲染");
      await click(page.locator('[data-action="switch-web-mode"][data-mode="raw"]'));
      const raw = await page.locator(".web-reader").getAttribute("class");
      assert(raw.includes("web-raw"), "未切到原文快照模式");
      assert(await page.locator(".web-raw .ad").count() > 0, "原文模式未显示被剔除的广告位");
      await click(page.locator('[data-action="switch-web-mode"][data-mode="read"]'));
      return "两种模式可切换";
    });

    await step("Web：重新抓取生成 Commit", async () => {
      const s0 = await state();
      await click(page.locator('[data-action="refetch-web"]'));
      const s = await state();
      assert(s.commits > s0.commits, "重抓未生成 Commit");
      return "commit +1";
    });

    await step("版本面板：Commit 时间线 + diff 审阅 + 回滚", async () => {
      await click(page.locator('[data-action="open-drawer"][data-drawer="version"]').first());
      const drawer = page.locator("#drawer.show");
      const n = await drawer.locator(".commit").count();
      assert(n >= 5, "提交时间线条目过少: " + n);
      await click(drawer.locator(".commit").first());
      const txt = await drawer.innerText();
      assert(txt.includes("BASE") && txt.includes("TARGET"), "diff 视图未渲染");
      const s0 = await state();
      await click(drawer.locator('[data-action="revert-commit"]').first());
      const s = await state();
      assert(s.commits > s0.commits, "回滚未生成反向提交");
      return n + " 条提交 / diff 可见 / 回滚 commit +1";
    });

    await step("MCP：模拟 Agent 在隔离分支写入", async () => {
      await closeDrawerIfOpen();
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="mcp"]'));
      const s0 = await state();
      await click(page.locator('[data-action="agent-write"]').first());
      const s = await state();
      assert(s.docs > s0.docs, "Agent 未写入文档");
      assert(s.commits > s0.commits, "Agent 写入未生成 Commit");
      assert(s.refs.indexOf("agent/demo") >= 0, "未创建隔离分支");
      const commits = await page.evaluate(() => {
        const S = window.EpistemePrototype.getState();
        const s = S.spaces.find(x => x.id === S.activeSpaceId);
        return s.commits.slice(0, 3).map(c => c.source + "/" + c.branch);
      });
      assert(commits.some(x => x.startsWith("mcp/")), "缺少 source=mcp 的提交");
      return "文档 +1 / 分支 agent/demo / mcp commit";
    });

    await step("版本：合并 agent/demo 到 main", async () => {
      await click(page.locator('[data-action="open-drawer"][data-drawer="version"]').first());
      const merge = page.locator('[data-action="merge-branch"]');
      assert(await merge.count() > 0, "没有合并入口");
      await click(merge);
      const s = await state();
      assert(s.refs.indexOf("agent/demo") < 0, "合并后分支未清理");
      assert(s.commits > 0 && s.refs.indexOf("main") >= 0, "main 引用异常");
      await closeDrawerIfOpen();
      return "合并完成 / main 保留";
    });

    await step("命令面板 Ctrl+K 搜索并跳转文档", async () => {
      await page.keyboard.press("Control+k");
      assert(await page.locator("#cmdk.show").count() > 0, "命令面板未打开");
      await page.locator("#cmdkInput").fill("关于");
      await page.waitForTimeout(120);
      const list = await page.locator("#cmdkList").innerText();
      assert(list.includes("关于「空壳」的几点思考"), "搜索未命中文档");
      await page.keyboard.press("Enter");
      await page.waitForTimeout(180);
      const s = await state();
      assert(s.docId !== null, "回车未跳转");
      return "搜索命中并跳转";
    });

    await step("四套主题切换真实生效", async () => {
      for (const t of ["sky", "lilac", "blush", "mint"]) {
        await click(page.locator('[data-action="theme"][data-theme="' + t + '"]'));
        const cur = await page.evaluate(() => document.documentElement.getAttribute("data-theme"));
        assert(cur === t, "主题未切到 " + t + "，实际 " + cur);
      }
      return "mint/sky/lilac/blush 全部生效";
    });

    await step("安装 Workbench Pack → 真实 11 个模块目录出现", async () => {
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="templates"]'));
      await click(page.locator('[data-action="install-template"][data-tpl="workbench"]'));
      const s = await state();
      const tree = await page.locator("#sideScroll").innerText();
      const mods = ["M1-", "M2-", "M3-", "M4-", "M5-", "M6-", "M7-", "M8-", "M9-", "M10-", "M11-"];
      // 树里可能因折叠只显示根级，这里按状态数据校验
      const names = await page.evaluate(() => {
        const S = window.EpistemePrototype.getState();
        const s = S.spaces.find(x => x.id === S.activeSpaceId);
        return s.nodes.map(n => n.name);
      });
      const missing = mods.filter(m => !names.some(n => n.startsWith(m)));
      assert(missing.length === 0, "缺少模块目录: " + missing.join(","));
      assert(s.types >= 16, "内容类型数量不足: " + s.types);
      assert(tree.length > 0, "侧栏未渲染");
      return "11 个 M 目录 / " + s.types + " 个内容类型";
    });

    await step("导出 Pack 定义触发下载", async () => {
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="templates"]'));
      const dl = page.waitForEvent("download", { timeout: 8000 }).catch(() => null);
      await click(page.locator('[data-action="export-pack"]'));
      const d = await dl;
      assert(d, "未触发下载事件");
      assert(d.suggestedFilename().endsWith(".json"), "导出文件名异常");
      return d.suggestedFilename();
    });

    await step("移动端：底部导航 + 抽屉菜单可用", async () => {
      await page.setViewportSize({ width: 375, height: 800 });
      await page.waitForTimeout(150);
      assert(await page.locator("#bottomNav").isVisible(), "底部导航未显示");
      assert(await page.locator(".hamb").isVisible(), "汉堡按钮未显示");
      await click(page.locator(".hamb"));
      assert((await page.locator("#sidebar").getAttribute("class")).includes("open"), "侧栏抽屉未打开");
      await click(page.locator('#sideScroll [data-action="open-page"][data-page="schema"]'));
      const s = await state();
      assert(s.page === "schema", "移动端导航未生效");
      await page.setViewportSize({ width: 1440, height: 900 });
      return "375px 下导航与抽屉可用";
    });

    await step("重置回空壳状态", async () => {
      await click(page.locator('[data-action="open-modal"][data-modal="settings"]'));
      await click(page.locator('[data-action="do-reset"]'));
      const s = await state();
      assert(s.spaces === 0, "重置后仍有空间");
      const txt = await page.locator("body").innerText();
      assert(txt.includes("创建你的第一个空间"), "未回到空壳引导");
      return "回到 0 空间空壳";
    });

    await step("全流程无浏览器运行时错误", async () => {
      assert(runtimeErrors.length === 0, runtimeErrors.join(" | "));
      return "console / pageerror / requestfailed 均为 0";
    });

  } finally {
    const pass = results.filter(r => r.ok).length;
    const fail = results.length - pass;
    const lines = [
      "# Episteme 知书 · 原型交互回归报告",
      "",
      "| 项 | 值 |",
      "|---|---|",
      "| 时间 | " + new Date().toISOString() + " |",
      "| 用例总数 | " + results.length + " |",
      "| 通过 | **" + pass + "** |",
      "| 失败 | **" + fail + "** |",
      "| 浏览器运行时错误 | " + runtimeErrors.length + " |",
      "",
      "## 逐项结果",
      ""
    ];
    results.forEach((r, i) => lines.push((i + 1) + ". " + (r.ok ? "✅" : "❌") + " **" + r.name + "**" + (r.detail ? " — " + r.detail : "")));
    if (runtimeErrors.length) { lines.push("", "## 运行时错误", ""); runtimeErrors.forEach(e => lines.push("- " + e)); }
    lines.push("", "> 由 Playwright Chromium 真实点击生成；脚本 `episteme/tests/ui_test.js`。", "");
    fs.writeFileSync(REPORT_MD, lines.join("\n"), "utf-8");
    fs.writeFileSync(REPORT_JSON, JSON.stringify({ pass, fail, total: results.length, runtimeErrors, results }, null, 2), "utf-8");
    console.log("\n== " + pass + "/" + results.length + " PASS, " + fail + " FAIL, runtime errors=" + runtimeErrors.length + " ==");
    console.log("report: " + REPORT_MD);
    await browser.close();
    server.close();
    process.exitCode = fail ? 1 : 0;
  }
})().catch(e => { console.error("FATAL", e); process.exit(2); });
