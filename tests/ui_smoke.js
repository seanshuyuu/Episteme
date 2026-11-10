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
  if (cond) { pass++; console.log('  PASS  ' + name); }
  else { fail++; console.log('  FAIL  ' + name + ' :: ' + JSON.stringify(detail).slice(0, 300)); }
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

  console.log('\n== 结果: ' + pass + ' PASS / ' + fail + ' FAIL ==');
  process.exit(fail ? 1 : 0);
})().catch((e) => {
  console.error('FATAL', e);
  process.exit(1);
});
