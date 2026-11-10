/* Episteme 动态 UI 交互回归（jsdom + 真实 HTTP 服务） */
const fs = require('fs');
const http = require('node:http');
const { JSDOM } = require('jsdom');

const BASE = 'http://127.0.0.1:5080';
const WWW = '/mnt/work/JobAndResearch/Coding/WorkBuddy/2026-11-08-10-00-09/episteme/src/Episteme.Server/wwwroot';

// Host shell has a 4 GB hard ulimit -v; Node's built-in fetch (undici lazillhttp Wasm)
// cannot allocate memory here. Use a plain node:http fetch shim instead.
function nodeFetch(url, options) {
  options = options || {};
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const req = http.request({
      hostname: u.hostname, port: u.port || 80, path: u.pathname + u.search,
      method: options.method || 'GET', headers: options.headers || {}
    }, (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', (chunk) => { data += chunk; });
      res.on('end', () => resolve({
        ok: res.statusCode >= 200 && res.statusCode < 300,
        status: res.statusCode,
        json: async () => JSON.parse(data || '{}'),
        text: async () => data
      }));
    });
    req.on('error', reject);
    if (options.body) req.write(options.body);
    req.end();
  });
}

let pass = 0, fail = 0;
function check(name, cond, detail) {
  var text;
  try { text = JSON.stringify(detail === undefined ? null : detail); }
  catch (e) { text = String(detail); }
  if (!text) text = 'null';
  if (cond) { pass++; console.log('  PASS  ' + name); }
  else { fail++; console.log('  FAIL  ' + name + ' :: ' + text.slice(0, 300)); }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function waitFor(fn, ms = 10000) {
  const start = Date.now();
  while (Date.now() - start < ms) {
    if (fn()) return true;
    await sleep(100);
  }
  return false;
}

(async () => {
  const html = fs.readFileSync(WWW + '/index.html', 'utf8');
  const appjs = fs.readFileSync(WWW + '/app.js', 'utf8');
  const dom = new JSDOM(html, {
    url: BASE + '/', runScripts: 'outside-only', pretendToBeVisual: true
  });
  const { window } = dom;
  const doc = window.document;
  window.fetch = (u, o) => nodeFetch(String(u).startsWith('http') ? String(u) : BASE + String(u), o);
  window.eval(appjs);

  console.log('== Episteme 动态 UI 交互回归 ==');
  await waitFor(() => !!window.EpistemeApp);
  check('EpistemeApp 状态接口就绪', !!window.EpistemeApp);
  await sleep(1500); // wait for async init / loadSpaces

  const banned = ['素材', '热点', '拆文', '报告', '选题'];
  check('空启动首页零业务概念', !banned.some((w) => html.includes(w)),
    banned.filter((w) => html.includes(w)));

  check('主题令牌来自 tokens.json 并已应用',
    doc.documentElement.style.getPropertyValue('--primary').length > 0,
    doc.documentElement.style.getPropertyValue('--primary'));

  let st = window.EpistemeApp.getState();
  if (!st.spaceId) {
    doc.getElementById('new-space-name').value = 'UI 回归空间';
    doc.getElementById('btn-create-space').click();
    await waitFor(() => !!window.EpistemeApp.getState().spaceId);
    st = window.EpistemeApp.getState();
    check('真实点击「创建空间」生效', !!st.spaceId, st);
  } else {
    check('已存在可用空间', !!st.spaceId, st.spaceId);
  }

  const theme = doc.getElementById('theme-select');
  theme.value = 'sky';
  theme.dispatchEvent(new window.Event('change'));
  check('主题切换改变状态', window.EpistemeApp.getState().theme === 'sky',
    window.EpistemeApp.getState().theme);

  doc.querySelector('[data-tab="mcp"]').click();
  check('Tab 切换生效', window.EpistemeApp.getState().tab === 'mcp',
    window.EpistemeApp.getState().tab);

  doc.getElementById('btn-new-type').click();
  check('Schema Builder 打开',
    !doc.getElementById('schema-builder').classList.contains('hidden'));

  const rows = doc.getElementById('field-rows');
  rows.innerHTML = '';
  window.EpistemeApp.addField();
  check('添加字段行', rows.querySelectorAll('.field-row').length === 1);
  const row = rows.querySelector('.field-row');
  row.querySelector('[data-f="key"]').value = 'name';
  row.querySelector('[data-f="label"]').value = '名称';
  row.querySelector('[data-f="required"]').checked = true;

  const slug = 'ui_' + String(Date.now()).slice(-8);
  const nodePath = '/UI-' + String(Date.now()).slice(-5);
  doc.getElementById('type-slug').value = slug;
  doc.getElementById('type-label').value = 'UI 回归类型';
  doc.getElementById('type-node').value = nodePath;

  const toolsBefore = window.EpistemeApp.getState().toolCount;
  const typesBefore = window.EpistemeApp.getState().contentTypes.length;
  doc.getElementById('btn-save-type').click();
  const saved = await waitFor(() =>
    window.EpistemeApp.getState().toolCount >= toolsBefore + 3 &&
    window.EpistemeApp.getState().contentTypes.length > typesBefore);
  check('保存类型后同会话内容类型增加', saved, window.EpistemeApp.getState().contentTypes.length);
  check('保存类型后动态工具 +3（无需重启）',
    window.EpistemeApp.getState().toolCount >= toolsBefore + 3,
    { before: toolsBefore, after: window.EpistemeApp.getState().toolCount });

  const node = window.EpistemeApp.getState().nodes.find((n) => n.path === nodePath);
  check('Schema 自动创建并绑定目录', !!node && node.contentType === slug, node);
  if (node) await window.EpistemeApp.selectNode(node.id);

  const controls = doc.querySelectorAll('#dynamic-form [data-key]').length;
  check('动态表单控件由 Schema 驱动（1 字段 + 标题 + 正文）', controls === 3, controls);
  const stateAfterSelect = window.EpistemeApp.getState();
  check('render 描述返回列数', stateAfterSelect.renderColumns >= 1, stateAfterSelect.renderColumns);

  const commitsBefore = window.EpistemeApp.getState().commits.length;
  const titleInput = doc.querySelector('#dynamic-form [data-key="__title"]');
  const nameInput = doc.querySelector('#dynamic-form [data-key="name"]');
  const mdInput = doc.querySelector('#dynamic-form [data-key="__md"]');
  if (titleInput && nameInput && mdInput) {
    titleInput.value = 'UI 回归文档';
    nameInput.value = 'UI 回归文档';
    mdInput.value = '# 正文';
    doc.getElementById('btn-create-doc').click();
    await waitFor(() => window.EpistemeApp.getState().commits.length > commitsBefore);
  }
  check('真实点击「创建文档」产生 Commit',
    window.EpistemeApp.getState().commits.length > commitsBefore,
    { before: commitsBefore, after: window.EpistemeApp.getState().commits.length });

  doc.querySelector('[data-tab="versions"]').click();
  check('版本历史有提交', window.EpistemeApp.getState().commits.length > 0);

  await window.EpistemeApp.loadTools();
  check('MCP 工具面板渲染工具卡片',
    doc.querySelectorAll('#tool-list .tool').length >= 20,
    doc.querySelectorAll('#tool-list .tool').length);

  // ---------------- P3 四类阅读器 ----------------
  console.log('---- P3 四类阅读器交互 ----');
  const st4 = window.EpistemeApp.getState();
  check('Viewer 契约由 /viewers 加载（四类 + 附件）', st4.viewerCount >= 5, st4.viewerCount);

  const card = doc.querySelector('.doc-card');
  if (card) {
    card.click();
    // activeDocId is set before the HTTP request; wait for the full open flow
    // (viewer descriptors loaded + reader tab activated) instead of racing it.
    await waitFor(() => {
      const s = window.EpistemeApp.getState();
      return s.tab === 'reader' && !!s.activeDocId && !!s.reader;
    });
  }
  check('真实点击文档卡片打开阅读器 Tab',
    window.EpistemeApp.getState().tab === 'reader' && !!window.EpistemeApp.getState().activeDocId,
    window.EpistemeApp.getState().activeDocId);
  check('阅读器渲染 Part tabs 与 Markdown 源码编辑器',
    doc.querySelectorAll('#reader-part-tabs .part-tab').length >= 1 && !!doc.getElementById('part-editor'));

  const preview = doc.querySelector('[data-action="reader-mode"][data-mode="preview"]');
  if (preview) preview.click();
  check('Markdown 源码 / 预览双模式切换',
    window.EpistemeApp.getState().readerMode === 'preview' && !!doc.querySelector('.md-preview'),
    window.EpistemeApp.getState().readerMode);
  const mdSource = doc.querySelector('[data-action="reader-mode"][data-mode="source"]');
  if (mdSource) mdSource.click();

  doc.getElementById('ann-quote').value = '# 正文';
  doc.getElementById('ann-text').value = 'UI 回归批注';
  doc.getElementById('btn-add-annotation').click();
  await waitFor(() => window.EpistemeApp.getState().annotationTotal >= 1);
  check('真实点击「添加标记」写入带锚点的标注',
    window.EpistemeApp.getState().annotationTotal >= 1,
    window.EpistemeApp.getState().annotationTotal);
  check('标注侧栏展示锚点解析结果',
    doc.querySelectorAll('.annotation-card').length >= 1 &&
    doc.querySelector('.annotation-card').textContent.indexOf('anchored') >= 0,
    doc.querySelector('.annotation-card') && doc.querySelector('.annotation-card').textContent.slice(0, 120));

  const extraParts = [
    ['code', 'const x = 1;\nfunction demo() {\n  return 2;\n}'],
    ['pdf', '第一页文本\f第二页文本'],
    ['web', '<article><p>网页锚点句子</p></article>']
  ];
  for (const [kind, content] of extraParts) {
    const showBtn = doc.querySelector('[data-action="show-part-form"]');
    if (showBtn) showBtn.click();
    doc.getElementById('new-part-kind').value = kind;
    doc.getElementById('new-part-content').value = content;
    const before = window.EpistemeApp.getState().readerPartCount;
    doc.getElementById('btn-append-part').click();
    await waitFor(() => window.EpistemeApp.getState().readerPartCount > before);
  }
  check('真实点击追加 code / pdf / web 三类 Part',
    window.EpistemeApp.getState().readerPartCount >= 4,
    window.EpistemeApp.getState().readerPartCount);

  let ptabs = doc.querySelectorAll('[data-action="select-part"]');
  if (ptabs[1]) ptabs[1].click();
  check('切到 Code Part', window.EpistemeApp.getState().readerKind === 'code',
    window.EpistemeApp.getState().readerKind);
  const readMode = doc.querySelector('[data-action="reader-mode"][data-mode="read"]');
  if (readMode) readMode.click();
  const expectedCodeLines = extraParts[0][1].split('\n').length;
  check('Code 阅读模式渲染行号与语法着色',
    doc.querySelectorAll('.code-line').length === expectedCodeLines && !!doc.querySelector('.tok-kw'),
    { lines: doc.querySelectorAll('.code-line').length, expected: expectedCodeLines,
      keywords: doc.querySelectorAll('.tok-kw').length });
  const firstLineNo = doc.querySelector('.line-no');
  if (firstLineNo) firstLineNo.click();
  check('点击行号自动填入锚点行号',
    doc.getElementById('ann-line').value === '1', doc.getElementById('ann-line').value);

  ptabs = doc.querySelectorAll('[data-action="select-part"]');
  if (ptabs[2]) ptabs[2].click();
  check('PDF 文本层渲染页卡与只读提示',
    window.EpistemeApp.getState().readerKind === 'pdf' && !!doc.querySelector('.pdf-page') &&
    doc.getElementById('reader-toolbar').textContent.indexOf('原件只读') >= 0,
    doc.getElementById('reader-toolbar').textContent.slice(0, 120));
  const nextPage = doc.querySelector('[data-action="reader-page"][data-step="1"]');
  if (nextPage) nextPage.click();
  check('PDF 翻页生效', window.EpistemeApp.getState().readerPage === 2,
    window.EpistemeApp.getState().readerPage);

  ptabs = doc.querySelectorAll('[data-action="select-part"]');
  if (ptabs[3]) ptabs[3].click();
  check('Web 正文模式显示外部内容标识',
    window.EpistemeApp.getState().readerKind === 'web' && !!doc.querySelector('.web-origin') &&
    !!doc.querySelector('.web-read'), window.EpistemeApp.getState().readerKind);
  const plainMode = doc.querySelector('[data-action="reader-mode"][data-mode="plain"]');
  if (plainMode) plainMode.click();
  check('Web 三态之一：纯文本', window.EpistemeApp.getState().readerMode === 'plain' &&
    !!doc.querySelector('.web-plain'));
  const sourceMode = doc.querySelector('[data-action="reader-mode"][data-mode="source"]');
  if (sourceMode) sourceMode.click();
  check('Web 三态之二：原文快照', window.EpistemeApp.getState().readerMode === 'source' &&
    !!doc.querySelector('.web-source'));

  const partsBeforeExport = window.EpistemeApp.getState().readerPartCount;
  doc.getElementById('btn-export-notes').click();
  await waitFor(() => window.EpistemeApp.getState().readerPartCount > partsBeforeExport);
  check('一键导出标注汇总生成新 md Part',
    window.EpistemeApp.getState().readerPartCount > partsBeforeExport,
    window.EpistemeApp.getState().readerPartCount);

  ptabs = doc.querySelectorAll('[data-action="select-part"]');
  if (ptabs[0]) ptabs[0].click();
  const editor = doc.getElementById('part-editor');
  if (editor) editor.value = '# 正文\n保存后的新行';
  const commitsBeforeSave = window.EpistemeApp.getState().commits.length;
  const saveBtn = doc.querySelector('[data-action="save-part"]');
  if (saveBtn) saveBtn.click();
  await waitFor(() => window.EpistemeApp.getState().commits.length > commitsBeforeSave);
  check('Markdown 编辑保存产生新 Commit',
    window.EpistemeApp.getState().commits.length > commitsBeforeSave,
    window.EpistemeApp.getState().commits.length);

  doc.querySelector('[data-tab="versions"]').click();
  check('阅读器接入后版本 Tab 仍可用', window.EpistemeApp.getState().tab === 'versions');

  // ---------------- P4 Pack templates ----------------
  console.log('---- P4 声明式 Pack 交互 ----');
  doc.querySelector('[data-tab="packs"]').click();
  check('模板 Tab 切换生效', window.EpistemeApp.getState().tab === 'packs',
    window.EpistemeApp.getState().tab);
  await window.EpistemeApp.loadPacks();
  const packCards = doc.querySelectorAll('#pack-list .pack-card');
  check('内置模板渲染为可点击卡片', packCards.length >= 2, packCards.length);
  check('Pack 状态接口可读', window.EpistemeApp.getState().packCount >= 2,
    window.EpistemeApp.getState().packCount);

  const previewBtn = doc.querySelector('[data-action="preview-pack"]');
  if (previewBtn) previewBtn.click();
  await waitFor(() => !!window.EpistemeApp.getState().packPreview);
  check('真实点击「预览」返回 dryRun 且不写库',
    window.EpistemeApp.getState().packPreview.dryRun === true &&
    window.EpistemeApp.getState().packPreview.commitId === null,
    window.EpistemeApp.getState().packPreview);

  const nodesBeforeInstall = window.EpistemeApp.getState().nodes.length;
  const wbCard = Array.from(packCards)
    .find((c) => c.getAttribute('data-pack-id') === 'workbench') || packCards[0];
  const installBtn = wbCard && wbCard.querySelector('[data-action="install-pack"]');
  if (installBtn) installBtn.click();
  await waitFor(() => window.EpistemeApp.getState().nodes.length > nodesBeforeInstall + 5);
  check('真实点击「安装」生成目录树',
    window.EpistemeApp.getState().nodes.length >= nodesBeforeInstall + 10,
    { before: nodesBeforeInstall, after: window.EpistemeApp.getState().nodes.length });

  doc.getElementById('btn-export-pack').click();
  await waitFor(() => (doc.getElementById('pack-json').value || '').length > 100);
  check('真实点击「导出」生成 Pack JSON',
    doc.getElementById('pack-json').value.indexOf('"nodes"') >= 0,
    doc.getElementById('pack-json').value.length);
  check('导出结果进入状态接口',
    window.EpistemeApp.getState().packExportNodes >= 10,
    window.EpistemeApp.getState().packExportNodes);

  const custom = {
    id: 'ui_custom', name: 'UI 自定义模板', version: '1.0.0',
    nodes: [{ path: '/UI-自定义', contentType: 'ui_note' }],
    contentTypes: [{ slug: 'ui_note', label: '便签',
      fields: [{ key: 'title', label: '标题', type: 'text', required: true }] }]
  };
  doc.getElementById('pack-json').value = JSON.stringify(custom);
  const nodesBeforeImport = window.EpistemeApp.getState().nodes.length;
  doc.getElementById('btn-import-pack').click();
  await waitFor(() => window.EpistemeApp.getState().nodes.length > nodesBeforeImport);
  check('真实点击「导入」落地自定义目录与内容类型',
    window.EpistemeApp.getState().nodes.length > nodesBeforeImport &&
    window.EpistemeApp.getState().contentTypes.some((t) => t.slug === 'ui_note'),
    { before: nodesBeforeImport, after: window.EpistemeApp.getState().nodes.length });

  console.log('\n== 结果: ' + pass + ' PASS / ' + fail + ' FAIL ==');
  process.exit(fail ? 1 : 0);
})().catch((e) => {
  console.error('FATAL', e);
  process.exit(1);
});
