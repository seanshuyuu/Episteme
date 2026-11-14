/* Episteme users 模式登录墙 / 管理后台 DOM 交互回归（jsdom + 真实 HTTP 服务）。
 *
 * 与 ui_smoke.js 不同：这里只加载 auth.js（不含 app.js），验证 users 模式的
 * 渐进增强层本身能注册、登录、打开管理后台、登出，并在 legacy 模式下完全不注入 DOM。
 *
 *   EPISTEME_TEST_BASE=http://127.0.0.1:5081 node tests/auth_ui_smoke.js
 */
const fs = require('fs');
const http = require('node:http');
const path = require('node:path');
const { JSDOM } = require('jsdom');

const BASE = (process.env.EPISTEME_TEST_BASE || 'http://127.0.0.1:5080').replace(/\/$/, '');
const WWW = path.resolve(__dirname, '..', 'src', 'Episteme.Server', 'wwwroot');
const AUTH_JS = fs.readFileSync(path.join(WWW, 'auth.js'), 'utf8');
const ADMIN_USER = process.env.EPISTEME_ADMIN_USER || 'admin';
const ADMIN_PASSWORD = process.env.EPISTEME_ADMIN_PASSWORD || '';

let pass = 0, fail = 0;
function check(name, cond, detail) {
  let text;
  try { text = JSON.stringify(detail === undefined ? null : detail); }
  catch (e) { text = String(detail); }
  if (!text) text = 'null';
  if (cond) { pass++; console.log('  PASS  ' + name); }
  else { fail++; console.log('  FAIL  ' + name + ' :: ' + text.slice(0, 260)); }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Host shell has a 4 GB hard ulimit -v; Node's built-in fetch (undici Wasm) cannot
// allocate here. Use a plain node:http shim, same as ui_smoke.js.
function nodeFetch(url, options) {
  options = options || {};
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const req = http.request({
      hostname: u.hostname, port: u.port || 80,
      path: u.pathname + u.search,
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

function makeDom(fetchImpl) {
  const dom = new JSDOM(
    '<!doctype html><html><head></head><body>' +
    '<div class="topbar-meta"><button id="btn-help">帮助</button></div>' +
    '<div id="app"></div></body></html>',
    { url: BASE + '/', runScripts: 'outside-only', pretendToBeVisual: true });
  dom.window.fetch = fetchImpl;
  return dom;
}

async function waitFor(fn, ms = 8000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (fn()) return true;
    await sleep(60);
  }
  return false;
}

function click(dom, selector) {
  const el = dom.window.document.querySelector(selector);
  if (!el) return false;
  el.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  return true;
}

const hidden = (dom, id) => {
  const el = dom.window.document.getElementById(id);
  return !!el && el.classList.contains('hidden');
};
const visible = (dom, id) => {
  const el = dom.window.document.getElementById(id);
  return !!el && !el.classList.contains('hidden');
};

(async () => {
  console.log('== Episteme users 登录墙 / 管理后台 DOM 回归 ==');
  console.log('BASE=' + BASE);
  if (!ADMIN_PASSWORD) {
    console.log('需要 EPISTEME_ADMIN_PASSWORD');
    process.exit(2);
  }

  const dom = makeDom((url, opt) =>
    nodeFetch(String(url).startsWith('http') ? url : BASE + url, opt));
  const doc = dom.window.document;
  dom.window.eval(AUTH_JS);

  const booted = await waitFor(() => !!doc.getElementById('auth-overlay'));
  check('users 模式自动注入登录墙与管理后台容器', booted
    && !!doc.getElementById('admin-overlay'), !!doc.getElementById('auth-overlay'));
  check('顶栏注入用户芯片 / 管理后台 / 登出按钮',
    !!doc.getElementById('auth-user-chip') && !!doc.getElementById('btn-admin')
    && !!doc.getElementById('btn-logout'));
  check('未登录时显示登录墙且身份按钮隐藏',
    visible(dom, 'auth-overlay') && hidden(dom, 'auth-user-chip')
    && hidden(dom, 'btn-admin') && hidden(dom, 'btn-logout'));
  check('auth.js 同步暴露 token provider',
    typeof dom.window.EpistemeAuth.token === 'function'
    && dom.window.EpistemeAuth.token() === '', dom.window.EpistemeAuth.mode);

  click(dom, '[data-auth-action="tab-register"]');
  check('注册页签可切换到注册表单',
    visible(dom, 'auth-register-form') && hidden(dom, 'auth-login-form')
    && !!doc.getElementById('reg-pass2'));
  click(dom, '[data-auth-action="tab-login"]');
  check('可切回登录表单',
    visible(dom, 'auth-login-form') && hidden(dom, 'auth-register-form'));

  click(dom, '[data-auth-action="login"]');
  await sleep(120);
  check('空账号登录给出本地校验提示',
    (doc.getElementById('auth-hint').textContent || '').includes('请输入账号与密码'),
    doc.getElementById('auth-hint').textContent);

  doc.getElementById('auth-user').value = ADMIN_USER;
  doc.getElementById('auth-pass').value = 'wrong-password';
  click(dom, '[data-auth-action="login"]');
  await waitFor(() => (doc.getElementById('auth-hint').textContent || '').includes('错误'), 6000);
  check('错误密码在登录墙显示服务端错误',
    (doc.getElementById('auth-hint').textContent || '').includes('错误'),
    doc.getElementById('auth-hint').textContent);

  doc.getElementById('auth-user').value = ADMIN_USER;
  doc.getElementById('auth-pass').value = ADMIN_PASSWORD;
  click(dom, '[data-auth-action="login"]');
  const loggedIn = await waitFor(() => hidden(dom, 'auth-overlay')
    && !!dom.window.localStorage.getItem('episteme.auth.token')
    && visible(dom, 'auth-user-chip')
    && (doc.getElementById('auth-user-chip').textContent || '').includes('admin'), 8000);
  check('管理员登录成功并持久化令牌', loggedIn
    && dom.window.EpistemeAuth.token().length > 40,
    { token: (dom.window.EpistemeAuth.token() || '').slice(0, 12) });
  check('登录后显示身份芯片与管理员按钮、隐藏登录墙',
    visible(dom, 'auth-user-chip') && visible(dom, 'btn-admin')
    && visible(dom, 'btn-logout') && hidden(dom, 'auth-overlay'),
    doc.getElementById('auth-user-chip').textContent);
  check('身份芯片显示全局角色',
    (doc.getElementById('auth-user-chip').textContent || '').includes('admin'),
    doc.getElementById('auth-user-chip').textContent);
  check('window.EpistemeAuth 暴露管理后台入口',
    typeof dom.window.EpistemeAuth.openAdmin === 'function');

  click(dom, '#btn-admin');
  const opened = await waitFor(() => visible(dom, 'admin-overlay')
    && (doc.getElementById('admin-overview').textContent || '').includes('用户'), 8000);
  check('管理后台打开并渲染总览统计',
    opened && (doc.getElementById('admin-overview').textContent || '').includes('鉴权模式'),
    (doc.getElementById('admin-overview').textContent || '').slice(0, 120));
  check('管理后台渲染用户与角色表格',
    !!doc.querySelector('#admin-body table')
    && (doc.getElementById('admin-body').textContent || '').includes(ADMIN_USER),
    (doc.getElementById('admin-body').textContent || '').slice(0, 120));

  click(dom, '[data-auth-action="admin-grants"]');
  await sleep(220);
  check('空间授权页签可切换',
    (doc.getElementById('admin-body').textContent || '').length > 0,
    (doc.getElementById('admin-body').textContent || '').slice(0, 120));
  click(dom, '[data-auth-action="close-admin"]');
  check('管理后台可关闭', hidden(dom, 'admin-overlay'));

  dom.window.EpistemeAuth.onUnauthorized();
  check('401 时清除令牌并重新弹出登录墙',
    !dom.window.localStorage.getItem('episteme.auth.token')
    && visible(dom, 'auth-overlay') && hidden(dom, 'auth-user-chip')
    && (doc.getElementById('auth-hint').textContent || '').includes('登录已失效'),
    doc.getElementById('auth-hint').textContent);

  // Self-registration through the UI: an ordinary account must NOT see the admin button.
  const newName = 'ui' + Date.now().toString(36);
  click(dom, '[data-auth-action="tab-register"]');
  doc.getElementById('reg-user').value = newName;
  doc.getElementById('reg-pass').value = 'Ui-Pass-2026!';
  doc.getElementById('reg-pass2').value = 'Ui-Pass-2026!';
  doc.getElementById('reg-display').value = 'UI 注册用户';
  click(dom, '[data-auth-action="register"]');
  const registered = await waitFor(() => hidden(dom, 'auth-overlay')
    && !!dom.window.localStorage.getItem('episteme.auth.token'), 8000);
  check('普通用户可通过登录墙自助注册并自动登录', registered,
    doc.getElementById('auth-hint').textContent);
  check('普通用户不显示管理员后台按钮',
    visible(dom, 'auth-user-chip') && hidden(dom, 'btn-admin')
    && (doc.getElementById('auth-user-chip').textContent || '').includes('editor'),
    doc.getElementById('auth-user-chip').textContent);

  click(dom, '#btn-logout');
  await sleep(150);
  check('登出清除令牌并回到登录墙',
    !dom.window.localStorage.getItem('episteme.auth.token')
    && visible(dom, 'auth-overlay') && hidden(dom, 'auth-user-chip'),
    doc.getElementById('auth-hint').textContent);

  // Progressive enhancement: legacy mode must inject nothing at all.
  const legacyDom = makeDom(async (url) => {
    if (String(url).includes('/auth/config')) {
      return { ok: true, status: 200, json: async () => ({ mode: 'legacy' }), text: async () => '{}' };
    }
    return { ok: true, status: 200, json: async () => ({}), text: async () => '{}' };
  });
  legacyDom.window.eval(AUTH_JS);
  await sleep(300);
  check('legacy 模式不注入任何登录/管理 DOM',
    !legacyDom.window.document.getElementById('auth-overlay')
    && !legacyDom.window.document.getElementById('admin-overlay')
    && legacyDom.window.EpistemeAuth.token() === '',
    legacyDom.window.EpistemeAuth.mode);

  dom.window.close();
  legacyDom.window.close();
  console.log('\n== 结果: ' + pass + ' PASS / ' + fail + ' FAIL ==');
  process.exit(fail ? 1 : 0);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
