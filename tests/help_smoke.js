/* 应用内帮助手册专项交互冒烟（jsdom + 真实 HTTP 服务）。
 * EPISTEME_TEST_BASE 可覆盖服务地址；WWW 用 __dirname 相对定位，换机器无需改动。
 */
const fs = require('fs');
const http = require('node:http');
const { JSDOM } = require('jsdom');

const BASE = (process.env.EPISTEME_TEST_BASE || 'http://127.0.0.1:5080').replace(/\/$/, '');
const WWW = require('node:path').resolve(__dirname, '..', 'src', 'Episteme.Server', 'wwwroot');

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
  const text = JSON.stringify(detail === undefined ? null : detail);
  if (cond) { pass++; console.log('  PASS  ' + name); }
  else { fail++; console.log('  FAIL  ' + name + ' :: ' + String(text).slice(0, 240)); }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function makeDom(withHelp, helpjs, appjs, html) {
  const dom = new JSDOM(html, { url: BASE + '/', runScripts: 'outside-only', pretendToBeVisual: true });
  const w = dom.window;
  w.fetch = (u, o) => nodeFetch(String(u).startsWith('http') ? String(u) : BASE + String(u), o);
  if (withHelp) w.eval(helpjs);
  w.eval(appjs);
  await sleep(900);
  return { w, doc: w.document };
}

(async () => {
  const html = fs.readFileSync(WWW + '/index.html', 'utf8');
  const appjs = fs.readFileSync(WWW + '/app.js', 'utf8');
  const helpjs = fs.readFileSync(WWW + '/help-content.js', 'utf8');

  const dataWin = new JSDOM('<!doctype html><html><body></body></html>', { runScripts: 'outside-only' }).window;
  dataWin.eval(helpjs);
  const help = dataWin.window ? dataWin.window.EpistemeHelp : dataWin.EpistemeHelp;
  const sections = (help && help.sections) || [];
  console.log('== 应用内帮助手册专项冒烟 ==');
  check('help-content.js 暴露 EpistemeHelp 数据', !!help && Array.isArray(help.sections));
  check('主题数为 17', sections.length === 17, sections.length);
  const groups = Array.from(new Set(sections.map((s) => s.group)));
  check('分组数为 4', groups.length === 4, groups);
  check('每个主题含 id / title / blocks', sections.every((s) => s.id && s.title && Array.isArray(s.blocks) && s.blocks.length > 0));
  check('帮助内容零外部资源引用', !/<script|<link|cdn\./i.test(helpjs));
  check('index.html 引用本地 help-content.js', /<script src="\/help-content\.js">/.test(html));

  const { w, doc } = await makeDom(true, helpjs, appjs, html);
  const overlay = doc.getElementById('help-overlay');
  check('顶栏「帮助」按钮存在', !!doc.getElementById('btn-help'));
  check('空壳首页与顶栏共 2 个打开入口', doc.querySelectorAll('[data-action="open-help"]').length === 2,
    doc.querySelectorAll('[data-action="open-help"]').length);
  check('初始面板隐藏且有 aria-hidden', overlay.classList.contains('hidden') && overlay.getAttribute('aria-hidden') === 'true');

  doc.getElementById('btn-help').click();
  check('点击「帮助」打开面板', !overlay.classList.contains('hidden') && overlay.getAttribute('aria-hidden') === 'false');
  check('状态 helpOpen = true', w.EpistemeApp.getState().helpOpen === true);
  check('版本号取自 help-content.js', doc.getElementById('help-version').textContent === 'v' + help.version,
    doc.getElementById('help-version').textContent);
  check('主题计数显示「共 17 个主题」', /共 17 个主题/.test(doc.getElementById('help-count').textContent),
    doc.getElementById('help-count').textContent);
  check('侧栏渲染 17 个主题按钮', doc.querySelectorAll('#help-nav [data-action="help-goto"]').length === 17,
    doc.querySelectorAll('#help-nav [data-action="help-goto"]').length);
  check('侧栏渲染 4 个分组标题', doc.querySelectorAll('#help-nav .help-nav-group').length === 4);
  check('正文默认渲染 1 节', doc.querySelectorAll('#help-content .help-section').length === 1);
  check('首节为「开始之前 · 必读」', doc.querySelector('#help-content .help-section').id === 'help-section-start',
    doc.querySelector('#help-content .help-section').id);
  check('首节「上一节」禁用', doc.getElementById('help-prev').disabled === true);
  check('页码显示 1 / 17', doc.getElementById('help-position').textContent === '1 / 17',
    doc.getElementById('help-position').textContent);

  doc.getElementById('help-next').click();
  check('「下一节」切到第 2 节', doc.getElementById('help-position').textContent === '2 / 17' &&
    w.EpistemeApp.getState().helpSection === sections[1].id,
    { pos: doc.getElementById('help-position').textContent, section: w.EpistemeApp.getState().helpSection });
  doc.getElementById('help-prev').click();
  check('「上一节」回到第 1 节', doc.getElementById('help-position').textContent === '1 / 17');

  const target = sections[4];
  const navBtn = doc.querySelector('#help-nav [data-help-id="' + target.id + '"]');
  navBtn.click();
  check('点击侧栏主题跳转并渲染正文', w.EpistemeApp.getState().helpSection === target.id &&
    doc.querySelector('#help-content .help-section').id === 'help-section-' + target.id, target.id);

  const search = doc.getElementById('help-search');
  search.value = '回滚';
  search.dispatchEvent(new w.Event('input', { bubbles: true }));
  const matched = sections.filter((s) => JSON.stringify(s).toLowerCase().indexOf('回滚') >= 0).length;
  check('搜索关键词过滤主题', matched > 0 && matched < 17 &&
    doc.querySelectorAll('#help-nav [data-action="help-goto"]').length === matched,
    { matched, navCount: doc.querySelectorAll('#help-nav [data-action="help-goto"]').length });
  check('计数显示匹配数', /匹配 \d+ \/ 17 个主题/.test(doc.getElementById('help-count').textContent),
    doc.getElementById('help-count').textContent);
  check('状态 helpQuery 同步', w.EpistemeApp.getState().helpQuery === '回滚');

  search.value = '';
  search.dispatchEvent(new w.Event('input', { bubbles: true }));
  check('清空搜索恢复 17 个主题', doc.querySelectorAll('#help-nav [data-action="help-goto"]').length === 17);

  doc.getElementById('btn-help-all').click();
  check('「阅读全部」一次渲染 17 节', doc.querySelectorAll('#help-content .help-section').length === 17,
    doc.querySelectorAll('#help-content .help-section').length);
  check('按钮切换为「分节阅读」', doc.getElementById('btn-help-all').textContent === '分节阅读');
  check('状态 helpAll = true', w.EpistemeApp.getState().helpAll === true);
  doc.getElementById('btn-help-all').click();
  check('「分节阅读」恢复单节', doc.querySelectorAll('#help-content .help-section').length === 1 &&
    w.EpistemeApp.getState().helpAll === false);

  doc.dispatchEvent(new w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  check('Esc 关闭面板', overlay.classList.contains('hidden') && w.EpistemeApp.getState().helpOpen === false);

  doc.getElementById('btn-help').click();
  doc.getElementById('btn-help-close').click();
  check('「关闭 ×」按钮关闭面板', overlay.classList.contains('hidden'));

  doc.getElementById('btn-help').click();
  overlay.dispatchEvent(new w.MouseEvent('click', { bubbles: true }));
  check('点击遮罩关闭面板', overlay.classList.contains('hidden'));

  doc.querySelector('.help-entry [data-action="open-help"]').click();
  check('空壳首页入口也能打开帮助', !overlay.classList.contains('hidden'));

  const { doc: doc2 } = await makeDom(false, helpjs, appjs, html);
  doc2.getElementById('btn-help').click();
  check('缺失 help-content.js 时降级为提示而非白屏',
    /帮助内容未加载/.test(doc2.getElementById('help-content').textContent) &&
    !doc2.getElementById('help-overlay').classList.contains('hidden'),
    doc2.getElementById('help-content').textContent.slice(0, 80));

  console.log('  == 结果: ' + pass + ' PASS / ' + fail + ' FAIL ==');
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error('EXCEPTION', e); process.exit(2); });
