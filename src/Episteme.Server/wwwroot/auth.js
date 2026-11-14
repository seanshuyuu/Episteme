/* Episteme · 知书 — accounts, login wall and admin console.
 *
 * Progressive enhancement: in legacy / secure mode this file does nothing at all
 * (token() returns ''), so every existing REST / MCP / UI regression is untouched.
 * In EPISTEME_AUTH_MODE=users it adds a login wall, a Bearer token to all API calls,
 * and an admin console for users / roles / per-space grants.
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
  var SPACE_ROLES = ['owner', 'editor', 'agent', 'viewer'];

  var state = {
    mode: 'legacy', token: '', user: null, isAdmin: false,
    config: null, users: [], spaces: [], members: [],
    adminOpen: false, tab: 'login', busy: false
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
      '    <p class="auth-foot">账号与空间授权由服务端校验；令牌仅保存在本浏览器。</p>' +
      '  </div>' +
      '</section>' +
      '<section id="admin-overlay" class="admin-overlay hidden" role="dialog" aria-modal="true" aria-label="管理后台">' +
      '  <div class="admin-dialog">' +
      '    <header class="admin-head"><strong>管理后台 · 用户与授权</strong>' +
      '      <button data-auth-action="close-admin" class="ghost">关闭 ×</button></header>' +
      '    <div id="admin-overview" class="admin-overview"></div>' +
      '    <nav class="admin-tabs">' +
      '      <button data-auth-action="admin-users" class="active">用户与角色</button>' +
      '      <button data-auth-action="admin-grants">空间授权</button>' +
      '    </nav>' +
      '    <div id="admin-body"></div>' +
      '    <p id="admin-hint" class="hint"></p>' +
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
      if (ev.key === 'Escape' && state.adminOpen) closeAdmin();
    });
    // topbar identity chip placed after the help button
    var topbar = document.querySelector('.topbar-meta');
    if (topbar) {
      var chip = document.createElement('span');
      chip.id = 'auth-user-chip';
      chip.className = 'chip auth-chip hidden';
      topbar.appendChild(chip);
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
    var chip = $('auth-user-chip'), adminBtn = $('btn-admin'), out = $('btn-logout');
    if (!chip) return;
    if (!state.user) {
      hide(chip); hide(adminBtn); hide(out);
      return;
    }
    chip.textContent = state.user.displayName + ' · ' + state.user.globalRole;
    show(chip);
    show(out);
    if (state.isAdmin) show(adminBtn); else hide(adminBtn);
  }

  function logout() {
    try { request(AUTH, '/logout', { method: 'POST', body: '{}' }); } catch (e) { /* best effort */ }
    saveToken('');
    state.user = null; state.isAdmin = false;
    closeAdmin();
    renderIdentity();
    openLogin('已登出');
  }

  // Called by app.js when any API request returns 401.
  function onUnauthorized() {
    if (!EpistemeConfigUsers()) return;
    saveToken('');
    state.user = null; state.isAdmin = false;
    renderIdentity();
    closeAdmin();
    openLogin('登录已失效，请重新登录');
  }

  function EpistemeConfigUsers() { return state.mode === 'users'; }

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
      var [overview, users, spaces, members] = await Promise.all([
        request(ADMIN, '/overview'),
        request(ADMIN, '/users'),
        request(ADMIN, '/spaces'),
        request(ADMIN, '/members')
      ]);
      state.configOverview = overview;
      state.users = users.items || [];
      state.spaces = spaces.items || [];
      state.members = members.items || [];
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
      ['空间', o.spaces], ['授权', o.grants], ['文档', o.documents]
    ].map(function (pair) {
      return '<div class="stat"><strong>' + esc(pair[1] == null ? '–' : pair[1]) + '</strong>' +
        '<span>' + esc(pair[0]) + '</span></div>';
    }).join('') + '<div class="stat"><strong>' + esc(o.authMode) + '</strong><span>鉴权模式</span></div>';
  }

  function roleOptions(current, roles) {
    return roles.map(function (r) {
      return '<option value="' + r + '"' + (r === current ? ' selected' : '') + '>' + r + '</option>';
    }).join('');
  }

  function renderAdminBody() {
    var body = $('admin-body');
    if (!body) return;
    if (state.adminTab === 'grants') { renderGrants(body); return; }
    var rows = state.users.map(function (u) {
      return '<tr data-user-id="' + esc(u.id) + '">' +
        '<td><strong>' + esc(u.userName) + '</strong><br><small>' + esc(u.displayName || '') + '</small></td>' +
        '<td>' + esc(u.email || '–') + '</td>' +
        '<td><select data-field="globalRole">' + roleOptions(u.globalRole, ROLES) + '</select></td>' +
        '<td>' + (u.isActive ? '<span class="badge">启用</span>' : '<span class="badge warn">停用</span>') + '</td>' +
        '<td>' + esc(u.spaceCount == null ? '–' : u.spaceCount) + '</td>' +
        '<td class="admin-ops">' +
        '<button data-auth-action="save-user" class="ghost">保存</button>' +
        '<button data-auth-action="toggle-user" class="ghost">' + (u.isActive ? '停用' : '启用') + '</button>' +
        '<button data-auth-action="reset-password" class="ghost">重置密码</button>' +
        '<button data-auth-action="delete-user" class="ghost danger">删除</button>' +
        '</td></tr>';
    }).join('');
    body.innerHTML =
      '<div class="subhead">用户列表 · 角色即时生效</div>' +
      '<table class="admin-table"><thead><tr><th>账号</th><th>邮箱</th><th>全局角色</th>' +
      '<th>状态</th><th>空间</th><th>操作</th></tr></thead><tbody>' + rows + '</tbody></table>' +
      '<div class="subhead">新建用户</div>' +
      '<div class="admin-form">' +
      '<input id="new-user-name" placeholder="用户名" aria-label="新用户名">' +
      '<input id="new-user-pass" type="password" placeholder="初始密码（≥8）" aria-label="初始密码">' +
      '<input id="new-user-display" placeholder="显示名称" aria-label="显示名称">' +
      '<select id="new-user-role">' + roleOptions('viewer', ROLES) + '</select>' +
      '<button data-auth-action="create-user" class="primary">创建</button></div>';
  }

  function renderGrants(body) {
    var userOptions = state.users.map(function (u) {
      return '<option value="' + esc(u.id) + '">' + esc(u.userName) + '</option>';
    }).join('');
    var spaceOptions = state.spaces.map(function (s) {
      return '<option value="' + esc(s.id) + '">' + esc(s.name) + ' · ' + esc(s.slug) + '</option>';
    }).join('');
    var rows = state.members.map(function (m) {
      var space = state.spaces.filter(function (s) { return s.id === m.spaceId; })[0];
      return '<tr><td>' + esc(m.userName) + '</td><td>' + esc(space ? space.name : m.spaceId) + '</td>' +
        '<td><span class="badge">' + esc(m.role) + '</span></td><td>' + esc(m.grantedBy || '–') + '</td>' +
        '<td><button data-auth-action="revoke" data-user="' + esc(m.userId) +
        '" data-space="' + esc(m.spaceId) + '" class="ghost danger">撤销</button></td></tr>';
    }).join('');
    body.innerHTML =
      '<div class="subhead">空间授权矩阵 · 未授权用户看不到该空间</div>' +
      '<div class="admin-form">' +
      '<select id="grant-user" aria-label="选择用户">' + userOptions + '</select>' +
      '<select id="grant-space" aria-label="选择空间">' + spaceOptions + '</select>' +
      '<select id="grant-role" aria-label="空间角色">' + roleOptions('viewer', SPACE_ROLES) + '</select>' +
      '<button data-auth-action="grant" class="primary">授权 / 更新</button></div>' +
      '<table class="admin-table"><thead><tr><th>用户</th><th>空间</th><th>角色</th>' +
      '<th>授权人</th><th>操作</th></tr></thead><tbody>' +
      (rows || '<tr><td colspan="5" class="muted">暂无授权记录</td></tr>') + '</tbody></table>';
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
      else if (action === 'admin-users') {
        state.adminTab = 'users';
        document.querySelectorAll('.admin-tabs button').forEach(function (b) { b.classList.remove('active'); });
        el.classList.add('active'); renderAdminBody();
      } else if (action === 'admin-grants') {
        state.adminTab = 'grants';
        document.querySelectorAll('.admin-tabs button').forEach(function (b) { b.classList.remove('active'); });
        el.classList.add('active'); renderAdminBody();
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
      } else if (action === 'grant') {
        var userId = $('grant-user') && $('grant-user').value;
        var spaceId = $('grant-space') && $('grant-space').value;
        var grantRole = $('grant-role') && $('grant-role').value;
        if (!userId || !spaceId) { hint('admin-hint', '请先选择用户与空间', 'error'); return; }
        await request(ADMIN, '/members', {
          method: 'PUT', body: JSON.stringify({ userId: userId, spaceId: spaceId, role: grantRole })
        });
        await loadAdmin();
        state.adminTab = 'grants'; renderAdminBody();
        hint('admin-hint', '授权已保存', 'ok');
      } else if (action === 'revoke') {
        await request(ADMIN, '/members', {
          method: 'DELETE', body: JSON.stringify({
            userId: el.getAttribute('data-user'), spaceId: el.getAttribute('data-space')
          })
        });
        await loadAdmin();
        state.adminTab = 'grants'; renderAdminBody();
        hint('admin-hint', '授权已撤销', 'ok');
      }
    } catch (e) { hint('admin-hint', e.message, 'error'); }
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
      refreshMe: refreshMe, openAdmin: openAdmin, logout: logout
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
