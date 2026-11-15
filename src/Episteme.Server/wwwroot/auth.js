/* Episteme · 知书 — accounts, login wall, per-account MCP token and admin console.
 *
 * Progressive enhancement: in legacy / secure mode this file does nothing at all
 * (token() returns ''), so every existing REST / MCP / UI regression is untouched.
 * In EPISTEME_AUTH_MODE=users it adds a login wall, a Bearer token to all API calls,
 * a self-service "我的 MCP Token" panel and an admin console for users / roles /
 * account-owned spaces.
 *
 * Zero build step, zero dependencies, no framework.
 */
(function () {
  'use strict';

  var AUTH = '/api/v1/auth';
  var ADMIN = '/api/v1/admin';
  var API = '/api/v1/episteme';
  var KEY = 'episteme.auth.token';
  var ROLES = ['admin', 'owner', 'editor', 'agent', 'viewer'];

  var state = {
    mode: 'legacy', token: '', user: null, isAdmin: false,
    config: null, users: [], spaces: [], owners: [],
    adminOpen: false, tokenOpen: false, tokenInfo: null, tokenPlain: '',
    tab: 'login', busy: false
  };

  function $(id) { return document.getElementById(id); }
  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
  function show(el) { if (el) el.classList.remove('hidden'); }
  function hide(el) { if (el) el.classList.add('hidden'); }

  function token() { return state.token || ''; }

  async function request(base, path, options) {
    options = options || {};
    var headers = Object.assign({ 'Content-Type': 'application/json' }, options.headers || {});
    if (state.token) headers['Authorization'] = 'Bearer ' + state.token;
    var res = await fetch(base + path, {
      method: options.method || 'GET', headers: headers, body: options.body
    });
    var data = {};
    try { data = await res.json(); } catch (e) { /* empty body */ }
    if (!res.ok) throw new Error(data.message || ('HTTP ' + res.status));
    return data;
  }

  function saveToken(value, user) {
    state.token = value || '';
    if (value) {
      try { localStorage.setItem(KEY, value); } catch (e) { /* storage disabled */ }
    } else {
      try { localStorage.removeItem(KEY); } catch (e) { /* storage disabled */ }
    }
    if (user) { state.user = user; state.isAdmin = (user.globalRole === 'admin'); }
  }
  function loadToken() {
    try { return localStorage.getItem(KEY) || ''; } catch (e) { return ''; }
  }

  // ---------- DOM scaffold (created lazily, only in users mode) ----------

  function scaffold() {
    if ($('auth-overlay')) return;
    var wrap = document.createElement('div');
    wrap.innerHTML =
      '<section id="auth-overlay" class="auth-overlay hidden" role="dialog" aria-modal="true" aria-label="登录">' +
      '  <div class="auth-card">' +
      '    <div class="auth-brand"><span class="brand-mark">ε</span>' +
      '      <div><strong>Episteme · 知书</strong><small>个人代码管理工作空间</small></div></div>' +
      '    <div class="auth-tabs">' +
      '      <button data-auth-action="tab-login" class="active">登录</button>' +
      '      <button data-auth-action="tab-register">注册</button>' +
      '    </div>' +
      '    <div id="auth-login-form">' +
      '      <label>账号<input id="auth-user" autocomplete="username" placeholder="用户名"></label>' +
      '      <label>密码<input id="auth-pass" type="password" autocomplete="current-password" placeholder="密码"></label>' +
      '      <button data-auth-action="login" class="primary full">登录</button>' +
      '    </div>' +
      '    <div id="auth-register-form" class="hidden">' +
      '      <label>账号<input id="reg-user" autocomplete="username" placeholder="3–64 位字母/数字/_ - ."></label>' +
      '      <label>显示名称<input id="reg-display" placeholder="可留空"></label>' +
      '      <label>邮箱<input id="reg-email" placeholder="可留空"></label>' +
      '      <label>密码<input id="reg-pass" type="password" autocomplete="new-password" placeholder="至少 8 位"></label>' +
      '      <label>确认密码<input id="reg-pass2" type="password" placeholder="再输入一次"></label>' +
      '      <button data-auth-action="register" class="primary full">注册并登录</button>' +
      '    </div>' +
      '    <p id="auth-hint" class="hint"></p>' +
      '    <p class="auth-foot">按账号授权：空间归属与 MCP 令牌均由服务端校验；登录令牌仅保存在本浏览器。</p>' +
      '  </div>' +
      '</section>' +
      '<section id="admin-overlay" class="admin-overlay hidden" role="dialog" aria-modal="true" aria-label="管理后台">' +
      '  <div class="admin-dialog">' +
      '    <header class="admin-head"><strong>管理后台 · 账号与空间归属</strong>' +
      '      <button data-auth-action="close-admin" class="ghost">关闭 ×</button></header>' +
      '    <div id="admin-overview" class="admin-overview"></div>' +
      '    <nav class="admin-tabs">' +
      '      <button data-auth-action="admin-users" class="active">用户与角色</button>' +
      '      <button data-auth-action="admin-ownership">空间归属</button>' +
      '    </nav>' +
      '    <div id="admin-body"></div>' +
      '    <p id="admin-hint" class="hint"></p>' +
      '  </div>' +
      '</section>' +
      '<section id="token-overlay" class="admin-overlay hidden" role="dialog" aria-modal="true" aria-label="我的 MCP Token">' +
      '  <div class="token-dialog">' +
      '    <header class="admin-head"><strong>我的 MCP Token</strong>' +
      '      <button data-auth-action="close-token" class="ghost">关闭 ×</button></header>' +
      '    <p class="token-lead">按账号授权：这个长期令牌只能读写 <strong id="token-account">本账号</strong> 名下的空间，' +
      '      与浏览器登录 JWT 相互独立。重置后旧令牌立即失效。</p>' +
      '    <div id="token-status" class="token-status"></div>' +
      '    <div id="token-reveal" class="token-reveal hidden"></div>' +
      '    <div class="token-ops">' +
      '      <button data-auth-action="token-generate" id="btn-token-generate" class="primary">生成令牌</button>' +
      '      <button data-auth-action="token-reset" id="btn-token-reset" class="ghost hidden">重置令牌</button>' +
      '      <button data-auth-action="token-revoke" id="btn-token-revoke" class="ghost danger hidden">停用</button>' +
      '      <button data-auth-action="token-copy" id="btn-token-copy" class="ghost hidden">复制</button>' +
      '    </div>' +
      '    <div class="token-usage">' +
      '      <div class="subhead">用法</div>' +
      '      <pre>Authorization: Bearer &lt;epm_...&gt;\n' +
      'POST /mcp        JSON-RPC tools/list / tools/call\n' +
      'GET  /mcp/sse    SSE 传输\n' +
      'python3 tools/git-sync.py --auth bearer --token &lt;明文&gt; --space &lt;自己空间的 id&gt;</pre>' +
      '    </div>' +
      '    <p id="token-hint" class="hint"></p>' +
      '  </div>' +
      '</section>';
    document.body.appendChild(wrap);
    wrap.addEventListener('click', onAuthClick);
    wrap.addEventListener('keydown', function (ev) {
      if (ev.key !== 'Enter') return;
      var target = ev.target;
      if (!target || !target.id) return;
      if (target.id.indexOf('auth-') === 0 || target.id.indexOf('reg-') === 0) {
        ev.preventDefault();
        if (state.tab === 'register') doRegister(); else doLogin();
      }
      if (ev.key === 'Escape') { /* handled globally below */ }
    });
    document.addEventListener('keydown', function (ev) {
      if (ev.key !== 'Escape') return;
      if (state.tokenOpen) closeToken();
      else if (state.adminOpen) closeAdmin();
    });
    // topbar identity chip placed after the help button
    var topbar = document.querySelector('.topbar-meta');
    if (topbar) {
      var chip = document.createElement('span');
      chip.id = 'auth-user-chip';
      chip.className = 'chip auth-chip hidden';
      topbar.appendChild(chip);
      var tok = document.createElement('button');
      tok.id = 'btn-token';
      tok.className = 'ghost hidden';
      tok.setAttribute('data-auth-action', 'open-token');
      tok.textContent = '我的 MCP Token';
      topbar.appendChild(tok);
      var btn = document.createElement('button');
      btn.id = 'btn-admin';
      btn.className = 'ghost hidden';
      btn.setAttribute('data-auth-action', 'open-admin');
      btn.textContent = '管理后台';
      topbar.appendChild(btn);
      var out = document.createElement('button');
      out.id = 'btn-logout';
      out.className = 'ghost hidden';
      out.setAttribute('data-auth-action', 'logout');
      out.textContent = '登出';
      topbar.appendChild(out);
      topbar.addEventListener('click', onAuthClick);
    }
  }

  function hint(id, msg, kind) {
    var e = $(id); if (!e) return;
    e.textContent = msg || '';
    e.className = 'hint' + (kind ? ' ' + kind : '');
  }

  function setTab(which) {
    state.tab = which;
    var loginForm = $('auth-login-form'), regForm = $('auth-register-form');
    if (which === 'register') { hide(loginForm); show(regForm); }
    else { show(loginForm); hide(regForm); }
    var tabs = document.querySelectorAll('.auth-tabs button');
    for (var i = 0; i < tabs.length; i++)
      tabs[i].classList.toggle('active',
        tabs[i].getAttribute('data-auth-action') === 'tab-' + which);
    var regTab = document.querySelector('[data-auth-action="tab-register"]');
    if (regTab) regTab.style.display = state.config && state.config.registrationEnabled ? '' : 'none';
    hint('auth-hint', '');
  }

  function openLogin(message) {
    show($('auth-overlay'));
    setTab('login');
    if (message) hint('auth-hint', message, 'error');
  }
  function closeLogin() { hide($('auth-overlay')); }

  // Once authenticated, re-run the SPA bootstrap so spaces/nodes load with the token.
  function refreshApp() {
    try {
      if (window.EpistemeApp && typeof window.EpistemeApp.refresh === 'function')
        return window.EpistemeApp.refresh();
    } catch (e) { /* SPA may not be loaded yet */ }
    return Promise.resolve();
  }

  // ---------- auth flows ----------

  async function doLogin() {
    if (state.busy) return;
    var u = ($('auth-user') && $('auth-user').value || '').trim();
    var p = ($('auth-pass') && $('auth-pass').value) || '';
    if (!u || !p) { hint('auth-hint', '请输入账号与密码', 'error'); return; }
    state.busy = true;
    try {
      var res = await request(AUTH, '/login', {
        method: 'POST', body: JSON.stringify({ userName: u, password: p })
      });
      saveToken(res.token, res.user);
      // The login response already carries the user, so paint the identity chip
      // immediately instead of leaving a blank topbar until /auth/me returns.
      renderIdentity();
      closeLogin();
      hint('auth-hint', '');
      await refreshMe();
      await refreshApp();
    } catch (e) {
      hint('auth-hint', e.message, 'error');
    } finally { state.busy = false; }
  }

  async function doRegister() {
    if (state.busy) return;
    var u = ($('reg-user') && $('reg-user').value || '').trim();
    var p = ($('reg-pass') && $('reg-pass').value) || '';
    var p2 = ($('reg-pass2') && $('reg-pass2').value) || '';
    if (p !== p2) { hint('auth-hint', '两次密码不一致', 'error'); return; }
    state.busy = true;
    try {
      var res = await request(AUTH, '/register', {
        method: 'POST', body: JSON.stringify({
          userName: u, password: p,
          displayName: ($('reg-display') && $('reg-display').value || '').trim(),
          email: ($('reg-email') && $('reg-email').value || '').trim()
        })
      });
      saveToken(res.token, res.user);
      renderIdentity();
      closeLogin();
      await refreshMe();
      await refreshApp();
    } catch (e) { hint('auth-hint', e.message, 'error'); }
    finally { state.busy = false; }
  }

  async function refreshMe() {
    if (!state.token) return;
    try {
      var me = await request(AUTH, '/me');
      state.user = me.user;
      state.isAdmin = !!me.isAdmin;
      renderIdentity();
    } catch (e) {
      saveToken('');
      state.user = null; state.isAdmin = false;
      renderIdentity();
      openLogin('登录已失效，请重新登录');
    }
  }

  function renderIdentity() {
    var chip = $('auth-user-chip'), tokBtn = $('btn-token'), adminBtn = $('btn-admin'), out = $('btn-logout');
    if (!chip) return;
    if (!state.user) {
      hide(chip); hide(tokBtn); hide(adminBtn); hide(out);
      return;
    }
    chip.textContent = state.user.displayName + ' · ' + state.user.globalRole;
    show(chip);
    show(tokBtn);
    show(out);
    if (state.isAdmin) show(adminBtn); else hide(adminBtn);
  }

  // jsdom has no native dialogs; a guarded helper keeps the DOM regression headless.
  function confirmAction(message) {
    try { return window.confirm ? window.confirm(message) : true; }
    catch (e) { return true; }
  }

  function logout() {
    try { request(AUTH, '/logout', { method: 'POST', body: '{}' }); } catch (e) { /* best effort */ }
    saveToken('');
    state.user = null; state.isAdmin = false;
    state.tokenInfo = null; state.tokenPlain = '';
    closeAdmin(); closeToken();
    renderIdentity();
    openLogin('已登出');
  }

  // Called by app.js when any API request returns 401.
  function onUnauthorized() {
    if (!EpistemeConfigUsers()) return;
    saveToken('');
    state.user = null; state.isAdmin = false;
    state.tokenInfo = null; state.tokenPlain = '';
    renderIdentity();
    closeAdmin(); closeToken();
    openLogin('登录已失效，请重新登录');
  }

  function EpistemeConfigUsers() { return state.mode === 'users'; }

  // ---------- per-account MCP token (self-service) ----------

  async function openToken() {
    if (!state.token || !state.user) { openLogin('请先登录，再管理自己的 MCP Token'); return; }
    state.tokenOpen = true;
    state.tokenPlain = '';
    show($('token-overlay'));
    renderTokenReveal();
    hint('token-hint', '');
    await loadTokenStatus();
  }

  function closeToken() {
    state.tokenOpen = false;
    hide($('token-overlay'));
    state.tokenPlain = '';
    renderTokenReveal();
  }

  function fmtTime(value) {
    if (!value) return '—';
    var d = new Date(value);
    return isNaN(d.getTime()) ? String(value) : d.toLocaleString();
  }

  async function loadTokenStatus() {
    try {
      var info = await request(AUTH, '/me/mcp-token');
      state.tokenInfo = info;
      renderTokenPanel();
      hint('token-hint', '');
    } catch (e) { hint('token-hint', e.message, 'error'); }
  }

  function renderTokenPanel() {
    var info = state.tokenInfo || {};
    var box = $('token-status');
    if (!box) return;
    var label = !info.hasToken ? '未生成' : (info.revoked ? '已停用' : '已启用');
    var rows = [
      ['状态', label],
      ['令牌提示', info.hint || '—'],
      ['创建时间', fmtTime(info.createdAt)],
      ['最后使用', fmtTime(info.lastUsedAt)],
      ['作用范围', '仅本账号名下的空间'],
      ['令牌前缀', info.prefix || 'epm_']
    ];
    box.innerHTML = rows.map(function (r) {
      return '<div class="token-row"><span>' + esc(r[0]) + '</span><strong>' + esc(r[1]) + '</strong></div>';
    }).join('');
    var acc = $('token-account');
    if (acc) acc.textContent = state.user ? state.user.userName : (info.account || '本账号');
    var gen = $('btn-token-generate');
    if (info.hasToken && !info.revoked) {
      hide(gen); show($('btn-token-reset')); show($('btn-token-revoke'));
    } else {
      show(gen); hide($('btn-token-reset')); hide($('btn-token-revoke'));
      if (gen) gen.textContent = info.revoked ? '重新生成令牌' : '生成令牌';
    }
  }

  function renderTokenReveal() {
    var box = $('token-reveal'), copy = $('btn-token-copy');
    if (!box) return;
    if (!state.tokenPlain) {
      box.innerHTML = ''; hide(box); if (copy) hide(copy); return;
    }
    box.innerHTML =
      '<div class="token-once-title">新令牌（仅显示这一次，请立即复制）</div>' +
      '<code id="token-plain">' + esc(state.tokenPlain) + '</code>' +
      '<div class="token-once-note">服务端只保存 SHA-256 摘要；关闭窗口后无法再次查看明文。</div>';
    show(box);
    if (copy) show(copy);
  }

  async function rotateToken() {
    if (state.busy) return;
    var had = !!(state.tokenInfo && state.tokenInfo.hasToken && !state.tokenInfo.revoked);
    if (had && !confirmAction(
        '重置后旧 MCP 令牌立即失效，正在使用它的 MCP 客户端 / CI 需要更新。确定重置？')) return;
    state.busy = true;
    try {
      var res = await request(AUTH, '/me/mcp-token', {
        method: 'POST', body: JSON.stringify({ action: had ? 'reset' : 'generate' })
      });
      state.tokenPlain = res.token || '';
      renderTokenReveal();
      await loadTokenStatus();
      hint('token-hint', had ? '已重置，旧令牌已失效' : '已生成专属 MCP 令牌', 'ok');
    } catch (e) { hint('token-hint', e.message, 'error'); }
    finally { state.busy = false; }
  }

  async function revokeToken() {
    if (!confirmAction('停用后该令牌立即失效且不能恢复（只能重新生成）。确定停用？')) return;
    try {
      await request(AUTH, '/me/mcp-token', { method: 'DELETE' });
      state.tokenPlain = '';
      renderTokenReveal();
      await loadTokenStatus();
      hint('token-hint', '已停用 MCP 令牌', 'ok');
    } catch (e) { hint('token-hint', e.message, 'error'); }
  }

  function copyToken() {
    if (!state.tokenPlain) return;
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(state.tokenPlain);
        hint('token-hint', '已复制到剪贴板', 'ok');
        return;
      }
    } catch (e) { /* clipboard unavailable */ }
    hint('token-hint', '请选中上方令牌文本手动复制', 'ok');
  }

  // ---------- admin console ----------

  async function openAdmin() {
    if (!state.isAdmin) return;
    state.adminOpen = true;
    show($('admin-overlay'));
    await loadAdmin();
  }
  function closeAdmin() {
    state.adminOpen = false;
    hide($('admin-overlay'));
  }

  async function loadAdmin() {
    try {
      var [overview, users, spaces, owners] = await Promise.all([
        request(ADMIN, '/overview'),
        request(ADMIN, '/users'),
        request(ADMIN, '/spaces'),
        request(ADMIN, '/owners')
      ]);
      state.configOverview = overview;
      state.users = users.items || [];
      state.spaces = spaces.items || [];
      state.owners = owners.items || [];
      renderOverview();
      renderAdminBody();
      hint('admin-hint', '');
    } catch (e) { hint('admin-hint', e.message, 'error'); }
  }

  function renderOverview() {
    var o = state.configOverview || {};
    var box = $('admin-overview');
    if (!box) return;
    box.innerHTML = [
      ['用户', o.users], ['在用', o.activeUsers], ['管理员', o.admins],
      ['空间', o.spaces], ['归属', o.ownedSpaces], ['MCP 令牌', o.mcpTokens],
      ['文档', o.documents]
    ].map(function (pair) {
      return '<div class="stat"><strong>' + esc(pair[1] == null ? '–' : pair[1]) + '</strong>' +
        '<span>' + esc(pair[0]) + '</span></div>';
    }).join('') + '<div class="stat"><strong>' + esc(o.authorizationModel || o.authMode) +
      '</strong><span>鉴权模式</span></div>';
  }

  function roleOptions(current, roles) {
    return roles.map(function (r) {
      return '<option value="' + r + '"' + (r === current ? ' selected' : '') + '>' + r + '</option>';
    }).join('');
  }

  function renderAdminBody() {
    var body = $('admin-body');
    if (!body) return;
    if (state.adminTab === 'ownership') { renderOwnership(body); return; }
    var rows = state.users.map(function (u) {
      return '<tr data-user-id="' + esc(u.id) + '">' +
        '<td><strong>' + esc(u.userName) + '</strong><br><small>' + esc(u.displayName || '') + '</small></td>' +
        '<td>' + esc(u.email || '–') + '</td>' +
        '<td><select data-field="globalRole">' + roleOptions(u.globalRole, ROLES) + '</select></td>' +
        '<td>' + (u.isActive ? '<span class="badge">启用</span>' : '<span class="badge warn">停用</span>') + '</td>' +
        '<td>' + esc(u.spaceCount == null ? '–' : u.spaceCount) + '</td>' +
        '<td>' + (u.hasMcpToken ? '<span class="badge">已启用</span>' : '<span class="muted">未生成</span>') + '</td>' +
        '<td class="admin-ops">' +
        '<button data-auth-action="save-user" class="ghost">保存</button>' +
        '<button data-auth-action="toggle-user" class="ghost">' + (u.isActive ? '停用' : '启用') + '</button>' +
        '<button data-auth-action="reset-password" class="ghost">重置密码</button>' +
        '<button data-auth-action="delete-user" class="ghost danger">删除</button>' +
        '</td></tr>';
    }).join('');
    body.innerHTML =
      '<div class="subhead">用户列表 · 全局角色对该账号名下所有空间即时生效</div>' +
      '<table class="admin-table"><thead><tr><th>账号</th><th>邮箱</th><th>全局角色</th>' +
      '<th>状态</th><th>名下空间</th><th>MCP 令牌</th><th>操作</th></tr></thead><tbody>' + rows + '</tbody></table>' +
      '<div class="subhead">新建用户</div>' +
      '<div class="admin-form">' +
      '<input id="new-user-name" placeholder="用户名" aria-label="新用户名">' +
      '<input id="new-user-pass" type="password" placeholder="初始密码（≥8）" aria-label="初始密码">' +
      '<input id="new-user-display" placeholder="显示名称" aria-label="显示名称">' +
      '<select id="new-user-role">' + roleOptions('viewer', ROLES) + '</select>' +
      '<button data-auth-action="create-user" class="primary">创建</button></div>' +
      '<p class="muted">按账号授权：新建账号默认没有任何空间，可在「空间归属」转让一个已有空间，或让账号自行创建。</p>';
  }

  function renderOwnership(body) {
    var userOptions = state.users.map(function (u) {
      return '<option value="' + esc(u.id) + '">' + esc(u.userName) + '</option>';
    }).join('');
    var rows = state.spaces.map(function (s) {
      var owner = state.users.filter(function (u) { return u.id === s.ownerUserId; })[0];
      return '<tr data-space-id="' + esc(s.id) + '">' +
        '<td><strong>' + esc(s.name) + '</strong><br><small>' + esc(s.slug) + '</small></td>' +
        '<td>' + esc(owner ? owner.userName : (s.ownerUserId || '未归属')) + '</td>' +
        '<td>' + esc(s.documentCount == null ? '–' : s.documentCount) + '</td>' +
        '<td class="admin-ops">' +
        '<select data-field="newOwner" aria-label="新归属账号">' + userOptions + '</select>' +
        '<button data-auth-action="transfer-owner" data-space="' + esc(s.id) +
        '" class="ghost">转让归属</button></td></tr>';
    }).join('');
    body.innerHTML =
      '<div class="subhead">空间归属 · 每个空间只属于一个账号，其他账号完全不可见</div>' +
      '<table class="admin-table"><thead><tr><th>空间</th><th>归属账号</th>' +
      '<th>文档</th><th>转让归属（管理员）</th></tr></thead><tbody>' +
      (rows || '<tr><td colspan="4" class="muted">暂无空间</td></tr>') + '</tbody></table>' +
      '<p class="muted">不再支持「把某空间授权给另一个账号」；如需共享数据，请先转让归属或由账号自行复制内容。</p>';
  }

  function rowUser(el) {
    var row = el.closest('tr');
    if (!row) return null;
    var id = row.getAttribute('data-user-id');
    return state.users.filter(function (u) { return u.id === id; })[0] || null;
  }

  async function patchUser(u, patch) {
    await request(ADMIN, '/users/' + encodeURIComponent(u.id), {
      method: 'PATCH', body: JSON.stringify(patch)
    });
    await loadAdmin();
    hint('admin-hint', '已保存「' + u.userName + '」', 'ok');
  }

  async function onAuthClick(ev) {
    var el = ev.target.closest('[data-auth-action]');
    if (!el) return;
    var action = el.getAttribute('data-auth-action');
    try {
      if (action === 'tab-login') setTab('login');
      else if (action === 'tab-register') setTab('register');
      else if (action === 'login') await doLogin();
      else if (action === 'register') await doRegister();
      else if (action === 'logout') logout();
      else if (action === 'open-admin') await openAdmin();
      else if (action === 'close-admin') closeAdmin();
      else if (action === 'open-token') await openToken();
      else if (action === 'close-token') closeToken();
      else if (action === 'token-generate' || action === 'token-reset') await rotateToken();
      else if (action === 'token-revoke') await revokeToken();
      else if (action === 'token-copy') copyToken();
      else if (action === 'admin-users') {
        state.adminTab = 'users';
        document.querySelectorAll('.admin-tabs button').forEach(function (b) { b.classList.remove('active'); });
        el.classList.add('active'); renderAdminBody();
      } else if (action === 'admin-ownership') {
        state.adminTab = 'ownership';
        document.querySelectorAll('.admin-tabs button').forEach(function (b) { b.classList.remove('active'); });
        el.classList.add('active'); renderAdminBody();
      } else if (action === 'transfer-owner') {
        var spaceId = el.getAttribute('data-space');
        var sel = el.closest('tr') && el.closest('tr').querySelector('[data-field="newOwner"]');
        var newOwner = sel && sel.value;
        if (!spaceId || !newOwner) { hint('admin-hint', '请选择新的归属账号', 'error'); return; }
        await request(ADMIN, '/spaces/' + encodeURIComponent(spaceId) + '/owner', {
          method: 'PUT', body: JSON.stringify({ userId: newOwner })
        });
        await loadAdmin();
        state.adminTab = 'ownership'; renderAdminBody();
        hint('admin-hint', '空间归属已转让，原账号立即失去权限', 'ok');
      } else if (action === 'save-user') {
        var u = rowUser(el);
        if (u) {
          var sel = el.closest('tr').querySelector('[data-field="globalRole"]');
          await patchUser(u, { globalRole: sel.value });
        }
      } else if (action === 'toggle-user') {
        var u2 = rowUser(el);
        if (u2) await patchUser(u2, { isActive: !u2.isActive });
      } else if (action === 'reset-password') {
        var u3 = rowUser(el);
        if (u3) {
          var pwd = window.prompt('为「' + u3.userName + '」设置新密码（至少 8 位）', '');
          if (pwd) await patchUser(u3, { password: pwd });
        }
      } else if (action === 'delete-user') {
        var u4 = rowUser(el);
        if (u4 && window.confirm('删除用户「' + u4.userName + '」及其全部空间授权？')) {
          await request(ADMIN, '/users/' + encodeURIComponent(u4.id), { method: 'DELETE' });
          await loadAdmin();
          hint('admin-hint', '已删除用户', 'ok');
        }
      } else if (action === 'create-user') {
        var name = ($('new-user-name') && $('new-user-name').value || '').trim();
        var pwd2 = ($('new-user-pass') && $('new-user-pass').value) || '';
        var display = ($('new-user-display') && $('new-user-display').value || '').trim();
        var role = $('new-user-role') ? $('new-user-role').value : 'viewer';
        if (!name || !pwd2) { hint('admin-hint', '需要用户名与初始密码', 'error'); return; }
        await request(ADMIN, '/users', {
          method: 'POST', body: JSON.stringify({
            userName: name, password: pwd2, displayName: display, globalRole: role
          })
        });
        await loadAdmin();
        hint('admin-hint', '已创建用户「' + name + '」', 'ok');
      }
    } catch (e) {
      if (state.tokenOpen) hint('token-hint', e.message, 'error');
      else hint('admin-hint', e.message, 'error');
    }
  }

  // ---------- bootstrap ----------

  async function init() {
    try {
      var res = await fetch(AUTH + '/config');
      var cfg = res.ok ? await res.json() : {};
      state.mode = cfg.mode || 'legacy';
      state.config = cfg;
    } catch (e) { state.mode = 'legacy'; }

    if (state.mode !== 'users') {
      // Legacy/secure: expose a no-op token provider and stop. Nothing else changes.
      window.EpistemeAuth = { token: token, onUnauthorized: onUnauthorized, mode: state.mode };
      return;
    }

    scaffold();
    window.EpistemeAuth = {
      token: token, onUnauthorized: onUnauthorized, mode: state.mode,
      refreshMe: refreshMe, openAdmin: openAdmin, openToken: openToken, logout: logout
    };
    state.adminTab = 'users';
    var stored = loadToken();
    if (stored) {
      state.token = stored;
      await refreshMe();
      if (state.user) closeLogin();
      else openLogin();
    } else {
      openLogin();
    }
  }

  // Expose a no-op token synchronously so app.js never sees an undefined provider,
  // even if it initializes before the /config round-trip finishes.
  window.EpistemeAuth = { token: token, onUnauthorized: onUnauthorized, mode: 'legacy' };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
