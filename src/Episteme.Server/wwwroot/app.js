/* Episteme · 知书 — schema-driven SPA. No build step, no dependencies. */
(function () {
  'use strict';

  var BASE = window.location.origin;
  var API = BASE + '/api/v1/episteme';
  var FIELD_TYPES = ['text', 'longtext', 'markdown', 'code', 'number', 'date', 'boolean',
    'select', 'multi_select', 'tags', 'url', 'ref', 'json', 'file', 'user'];

  var state = {
    spaces: [], spaceId: null, spaceName: '', nodes: [], nodeId: null,
    contentTypes: [], render: null, documents: [], commits: [],
    tools: null, toolsVersion: 0, tokens: null, theme: 'mint',
    selectedCommit: null, tab: 'docs', fields: []
  };

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
  function $(id) { return document.getElementById(id); }
  function val(id) { var e = $(id); return e ? String(e.value || '').trim() : ''; }
  function hint(id, msg, kind) {
    var e = $(id); if (!e) return;
    e.textContent = msg || '';
    e.className = 'hint' + (kind ? ' ' + kind : '');
  }
  function show(id) { var e = $(id); if (e) e.classList.remove('hidden'); }
  function hide(id) { var e = $(id); if (e) e.classList.add('hidden'); }

  async function api(path, options) {
    options = options || {};
    var res = await fetch(API + path, {
      method: options.method || 'GET',
      headers: Object.assign({ 'Content-Type': 'application/json', 'X-Episteme-Role': 'owner' }, options.headers || {}),
      body: options.body
    });
    var data = {};
    try { data = await res.json(); } catch (e) { /* empty body */ }
    if (!res.ok) throw new Error(data.message || ('HTTP ' + res.status));
    return data;
  }

  function applyTheme(name) {
    var themes = state.tokens && state.tokens.themes;
    var t = themes && themes[name];
    if (!t) return;
    var map = {
      '--bg': t.bg, '--surface': t.surface, '--surface2': t.surface2,
      '--line': t.line, '--text': t.text, '--muted': t.muted,
      '--primary': t.primary, '--primarySoft': t.primarySoft, '--primaryDeep': t.primaryDeep
    };
    Object.keys(map).forEach(function (k) { document.documentElement.style.setProperty(k, map[k]); });
    state.theme = name;
  }

  // ---------- loading ----------

  async function loadSpaces() {
    var res = await api('/spaces');
    state.spaces = res.items || [];
    if (state.spaces.length === 0) {
      show('empty-state'); hide('workspace');
      $('space-name').textContent = '未选择空间';
      return;
    }
    hide('empty-state'); show('workspace');
    var s = state.spaces[0];
    state.spaceId = s.id; state.spaceName = s.name;
    $('space-name').textContent = s.name;
    await loadAll();
  }

  async function loadAll() {
    if (!state.spaceId) return;
    var id = state.spaceId;
    var results = await Promise.all([
      api('/spaces/' + id),
      api('/spaces/' + id + '/tree'),
      api('/spaces/' + id + '/content-types'),
      api('/spaces/' + id + '/documents?pageSize=50'),
      api('/spaces/' + id + '/commits?limit=30'),
      api('/spaces/' + id + '/mcp/tools')
    ]);
    state.toolsVersion = results[0].toolsVersion || 0;
    $('tools-version').textContent = 'tools v' + state.toolsVersion;
    state.nodes = results[1].items || [];
    state.contentTypes = results[2].items || [];
    state.documents = results[3].items || [];
    state.commits = results[4].items || [];
    state.tools = results[5];
    renderNodes(); renderDocs(); renderCommits(); renderTools();
    if (state.nodeId) await selectNode(state.nodeId);
    else renderForm(null);
  }

  async function loadTools() {
    if (!state.spaceId) return null;
    state.tools = await api('/spaces/' + state.spaceId + '/mcp/tools');
    state.toolsVersion = state.tools.toolsVersion || 0;
    $('tools-version').textContent = 'tools v' + state.toolsVersion;
    renderTools();
    return state.tools;
  }

  // ---------- rendering ----------

  function renderNodes() {
    var box = $('node-tree');
    if (!state.nodes.length) {
      box.innerHTML = '<p class="muted">还没有目录。新建目录并绑定内容类型后，即可创建文档。</p>';
      return;
    }
    box.innerHTML = state.nodes.map(function (n) {
      var depth = (n.path.match(/\//g) || []).length - 1;
      return '<div class="tree-item' + (n.id === state.nodeId ? ' active' : '') +
        '" data-node-id="' + esc(n.id) + '" style="padding-left:' + (10 + depth * 12) + 'px">' +
        '<span>◇</span><span>' + esc(n.path) + '</span>' +
        (n.contentType ? '<span class="tag">' + esc(n.contentType) + '</span>' : '') +
        (n.exposeToMcp === false ? '<span class="tag">隐藏</span>' : '') + '</div>';
    }).join('');
  }

  async function selectNode(nodeId) {
    state.nodeId = nodeId;
    renderNodes();
    var render = await api('/spaces/' + state.spaceId + '/nodes/' + nodeId + '/render');
    state.render = render;
    $('render-label').textContent = render.contentType
      ? '动态表单 · ' + render.label + ' v' + render.version
      : '动态表单 · 该目录未绑定内容类型';
    renderForm(render);
    var docs = await api('/spaces/' + state.spaceId + '/nodes/' + nodeId + '/documents?pageSize=50');
    state.documents = docs.items || [];
    renderDocs();
    return render;
  }

  function renderForm(render) {
    var box = $('dynamic-form');
    var controls = (render && render.controls) || [];
    if (!render || !render.contentType) {
      box.innerHTML = '<p class="muted">请先定义内容类型或选择一个已绑定类型的目录。</p>';
      $('btn-create-doc').disabled = true;
      return;
    }
    $('btn-create-doc').disabled = false;
    var html = '<label class="field"><span>标题</span><input data-key="__title" placeholder="文档标题"></label>';
    controls.forEach(function (c) {
      var key = esc(c.key), label = esc(c.label), type = c.type;
      html += '<label class="field"><span>' + label + (c.required ? ' *' : '') +
        (c.exposeToMcp === false ? ' · 不对 Agent 暴露' : '') + '</span>';
      if (c.control === 'select') {
        var options = (c.options || []).map(function (o) { return String(o); });
        html += '<select data-key="' + key + '" data-type="select">' +
          '<option value="">—</option>' + options.map(function (o) {
            return '<option value="' + esc(o) + '">' + esc(o) + '</option>';
          }).join('') + '</select>';
      } else if (c.control === 'switch') {
        html += '<input type="checkbox" data-key="' + key + '" data-type="boolean">';
      } else if (c.control === 'textarea' || c.control === 'code-json') {
        html += '<textarea data-key="' + key + '" data-type="' + esc(type) + '"></textarea>';
      } else {
        var inputType = c.control === 'number' ? 'number' : c.control === 'date' ? 'date' : 'text';
        html += '<input type="' + inputType + '" data-key="' + key + '" data-type="' + esc(type) + '"' +
          (c.hint ? ' placeholder="' + esc(c.hint) + '"' : '') + '>';
      }
      html += '</label>';
    });
    html += '<label class="field"><span>Markdown 正文</span><textarea data-key="__md" placeholder="可编辑内容，进入版本 diff"></textarea></label>';
    box.innerHTML = html;
  }

  function collectForm() {
    var fields = {};
    Array.prototype.forEach.call($('dynamic-form').querySelectorAll('[data-key]'), function (el) {
      var key = el.getAttribute('data-key');
      if (key === '__title' || key === '__md') return;
      var type = el.getAttribute('data-type');
      if (type === 'boolean') fields[key] = !!el.checked;
      else if (type === 'number') fields[key] = el.value === '' ? null : Number(el.value);
      else if (type === 'tags' || type === 'multi_select')
        fields[key] = el.value.split(/[,，]/).map(function (s) { return s.trim(); }).filter(Boolean);
      else if (el.value !== '') fields[key] = el.value;
    });
    return fields;
  }

  function renderDocs() {
    var box = $('doc-list');
    if (!state.documents.length) {
      box.innerHTML = '<p class="muted">暂无文档。</p>';
      return;
    }
    box.innerHTML = state.documents.map(function (d) {
      var summary = Object.keys(d.fields || {}).slice(0, 4).map(function (k) {
        return esc(k) + ': ' + esc(typeof d.fields[k] === 'object' ? JSON.stringify(d.fields[k]) : d.fields[k]);
      }).join(' · ');
      return '<div class="card"><div class="title">' + esc(d.title) + '</div>' +
        '<div class="meta"><span class="badge">' + esc(d.branch || 'main') + '</span>' +
        esc(summary || (d.parts || []).length + ' 个 Part') + '</div></div>';
    }).join('');
  }

  function renderCommits() {
    var box = $('commit-list');
    if (!state.commits.length) { box.innerHTML = '<p class="muted">暂无提交。</p>'; return; }
    box.innerHTML = state.commits.map(function (c) {
      return '<div class="card' + (c.id === state.selectedCommit ? ' selected' : '') +
        '" data-commit-id="' + esc(c.id) + '">' +
        '<div class="title">' + esc(c.message) + '</div>' +
        '<div class="meta"><span class="badge">' + esc(c.source) + '</span>' +
        esc((c.createdAt || '').slice(0, 19).replace('T', ' ')) +
        (c.agentId ? ' · ' + esc(c.agentId) : '') + '</div></div>';
    }).join('');
  }

  function renderDiff(diff) {
    var box = $('diff-view');
    var items = diff.items || [];
    if (!items.length) { box.innerHTML = '<p class="muted">两次快照无差异。</p>'; return; }
    box.innerHTML = '<div class="subhead">差异 · ' + items.length + ' 项</div>' + items.map(function (it) {
      var rows = '<div class="card"><div class="title">' + esc(it.change) + ' · ' + esc(it.path) + '</div>';
      (it.fields || []).forEach(function (f) {
        rows += '<div class="diff-line removed">- ' + esc(f.field) + ': ' + esc(f.before) + '</div>' +
          '<div class="diff-line added">+ ' + esc(f.field) + ': ' + esc(f.after) + '</div>';
      });
      (it.lines || []).forEach(function (l) {
        rows += '<div class="diff-line ' + (l.change === 'added' ? 'added' : 'removed') + '">' +
          (l.change === 'added' ? '+ ' : '- ') + esc(l.text) + '</div>';
      });
      return rows + '</div>';
    }).join('');
  }

  function renderTools() {
    var t = state.tools;
    if (!t) return;
    $('mcp-summary').innerHTML = 'toolsVersion <strong>' + esc(t.toolsVersion) + '</strong> · 静态 ' +
      esc(t.staticToolCount) + ' · 动态 ' + esc(t.dynamicToolCount) + ' · 共 <strong>' + esc(t.total) + '</strong>';
    $('tool-list').innerHTML = (t.tools || []).map(function (tool) {
      var props = (tool.inputSchema && tool.inputSchema.properties) || {};
      var required = (tool.inputSchema && tool.inputSchema.required) || [];
      var keys = Object.keys(props);
      return '<div class="tool"><code>' + esc(tool.name) + '</code>' +
        '<div class="params">参数 ' + keys.length + ' · 必填 ' + required.length +
        ' · ' + esc(keys.slice(0, 8).join(', ')) + (keys.length > 8 ? ' …' : '') + '</div></div>';
    }).join('');
  }

  function switchTab(tab) {
    state.tab = tab;
    ['docs', 'versions', 'mcp'].forEach(function (name) {
      var el = $('tab-' + name);
      if (el) el.classList.toggle('hidden', name !== tab);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.tabs button'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-tab') === tab);
    });
  }

  // ---------- actions ----------

  async function createSpace() {
    var name = val('new-space-name');
    if (!name) { hint('empty-hint', '请填写空间名称', 'error'); return null; }
    var res = await api('/spaces', {
      method: 'POST', body: JSON.stringify({ name: name, slug: val('new-space-slug'), source: 'ui' })
    });
    hint('empty-hint', '已创建：' + res.slug, 'ok');
    await loadSpaces();
    return res;
  }

  async function createNode() {
    var path = val('new-node-path');
    if (!path) { hint('doc-hint', '请填写目录路径', 'error'); return null; }
    var body = { path: path, contentType: val('new-node-type'), source: 'ui' };
    var res = await api('/spaces/' + state.spaceId + '/nodes', { method: 'POST', body: JSON.stringify(body) });
    hide('node-form');
    $('new-node-path').value = '';
    await loadAll();
    return res;
  }

  function fieldRow(index, preset) {
    var p = preset || {};
    var opts = FIELD_TYPES.map(function (t) {
      return '<option value="' + t + '"' + (p.type === t ? ' selected' : '') + '>' + t + '</option>';
    }).join('');
    return '<div class="field-row" data-index="' + index + '">' +
      '<input data-f="key" placeholder="key" value="' + esc(p.key || '') + '">' +
      '<input data-f="label" placeholder="显示名" value="' + esc(p.label || '') + '">' +
      '<select data-f="type">' + opts + '</select>' +
      '<div class="checks">' +
      '<label><input type="checkbox" data-f="required"' + (p.required ? ' checked' : '') + '>必填</label>' +
      '<label><input type="checkbox" data-f="indexed" checked>索引</label>' +
      '<label><input type="checkbox" data-f="exposeToMcp" checked>MCP</label>' +
      '<label><input type="checkbox" data-f="mcpWritable" checked>可写</label>' +
      '<button data-action="remove-field" data-index="' + index + '">×</button></div></div>';
  }

  function addField() {
    var box = $('field-rows');
    var index = box.querySelectorAll('.field-row').length;
    box.insertAdjacentHTML('beforeend', fieldRow(index));
  }

  function removeField(index) {
    var row = $('field-rows').querySelector('.field-row[data-index="' + index + '"]');
    if (row) row.remove();
  }

  async function saveType() {
    var slug = val('type-slug'), label = val('type-label');
    if (!slug || !label) { hint('type-hint', '请填写 slug 与显示名称', 'error'); return null; }
    var rows = Array.prototype.slice.call($('field-rows').querySelectorAll('.field-row'));
    var fields = rows.map(function (row, i) {
      function get(k) { return row.querySelector('[data-f="' + k + '"]'); }
      return {
        key: get('key').value.trim(), label: get('label').value.trim(),
        type: get('type').value, required: get('required').checked,
        indexed: get('indexed').checked, exposeToMcp: get('exposeToMcp').checked,
        mcpWritable: get('mcpWritable').checked, order: i
      };
    }).filter(function (f) { return f.key; });
    if (!fields.length) { hint('type-hint', '至少需要一个字段', 'error'); return null; }
    var definition = {
      slug: slug, label: label, icon: 'file-text', version: 1,
      exposeToMcp: true, primaryField: fields[0].key,
      defaultPartKinds: ['md'], fields: fields,
      views: [{ name: '全部', filter: {}, sort: [] }]
    };
    var body = { definition: definition, source: 'ui' };
    var nodePath = val('type-node');
    if (nodePath) body.nodePath = nodePath;
    var res = await api('/spaces/' + state.spaceId + '/content-types', {
      method: 'POST', body: JSON.stringify(body)
    });
    hint('type-hint', '已保存 ' + res.slug + ' v' + res.version + '，工具清单已同步', 'ok');
    hide('schema-builder');
    await loadAll();
    await loadTools();
    if (nodePath) {
      var node = state.nodes.find(function (n) { return n.path === nodePath; });
      if (node) await selectNode(node.id);
    }
    return res;
  }

  async function createDocument() {
    if (!state.nodeId) { hint('doc-hint', '请先选择一个目录', 'error'); return null; }
    var titleEl = $('dynamic-form').querySelector('[data-key="__title"]');
    var mdEl = $('dynamic-form').querySelector('[data-key="__md"]');
    var payload = {
      title: titleEl && titleEl.value.trim() ? titleEl.value.trim() : '未命名文档',
      fields: collectForm(),
      parts: [{ kind: 'md', content: mdEl ? mdEl.value : '' }],
      source: 'ui'
    };
    var res = await api('/spaces/' + state.spaceId + '/nodes/' + state.nodeId + '/documents', {
      method: 'POST', body: JSON.stringify(payload)
    });
    hint('doc-hint', '已创建文档，Commit ' + res.commitId, 'ok');
    await loadAll();
    return res;
  }

  async function selectCommit(commitId) {
    state.selectedCommit = commitId;
    renderCommits();
    var index = state.commits.findIndex(function (c) { return c.id === commitId; });
    var commit = index >= 0 ? state.commits[index] : null;
    var parent = commit && commit.parents && commit.parents[0];
    if (parent) {
      var diff = await api('/spaces/' + state.spaceId + '/diff?base=' + encodeURIComponent(parent) +
        '&target=' + encodeURIComponent(commitId));
      renderDiff(diff);
    } else {
      renderDiff({ items: [] });
    }
  }

  async function revert() {
    if (!state.selectedCommit) { hint('doc-hint', '请先选择一个提交', 'error'); return null; }
    var res = await api('/spaces/' + state.spaceId + '/reverts', {
      method: 'POST', body: JSON.stringify({ commitId: state.selectedCommit, source: 'ui' })
    });
    await loadAll();
    return res;
  }

  // ---------- events ----------

  function bindEvents() {
    document.addEventListener('click', async function (ev) {
      var actionEl = ev.target.closest('[data-action]');
      if (actionEl) {
        var action = actionEl.getAttribute('data-action');
        try {
          switch (action) {
            case 'create-space': await createSpace(); break;
            case 'new-node': show('node-form'); break;
            case 'save-node': await createNode(); break;
            case 'new-type': show('schema-builder'); if (!$('field-rows').children.length) addField(); break;
            case 'add-field': addField(); break;
            case 'remove-field': removeField(actionEl.getAttribute('data-index')); break;
            case 'save-type': await saveType(); break;
            case 'create-document': await createDocument(); break;
            case 'refresh-docs': await loadAll(); break;
            case 'refresh-commits': await loadAll(); break;
            case 'revert': await revert(); break;
            case 'switch-tab': switchTab(actionEl.getAttribute('data-tab')); break;
          }
        } catch (e) {
          hint('doc-hint', e.message, 'error');
          hint('type-hint', e.message, 'error');
          hint('empty-hint', e.message, 'error');
        }
        return;
      }
      var node = ev.target.closest('[data-node-id]');
      if (node) { try { await selectNode(node.getAttribute('data-node-id')); } catch (e) { hint('doc-hint', e.message, 'error'); } return; }
      var commit = ev.target.closest('[data-commit-id]');
      if (commit) { try { await selectCommit(commit.getAttribute('data-commit-id')); } catch (e) { hint('doc-hint', e.message, 'error'); } }
    });

    var theme = $('theme-select');
    if (theme) theme.addEventListener('change', function (e) { applyTheme(e.target.value); });
  }

  async function init() {
    bindEvents();
    try {
      var res = await fetch(BASE + '/tokens.json');
      if (res.ok) state.tokens = await res.json();
    } catch (e) { /* offline fallback in css */ }
    if (state.tokens && state.tokens.defaultTheme) state.theme = state.tokens.defaultTheme;
    var theme = $('theme-select'); if (theme) theme.value = state.theme;
    applyTheme(state.theme);
    try { await loadSpaces(); } catch (e) { hint('empty-hint', e.message, 'error'); }
  }

  window.EpistemeApp = {
    getState: function () {
      return {
        spaces: state.spaces.slice(), spaceId: state.spaceId, spaceName: state.spaceName,
        nodes: state.nodes.slice(), nodeId: state.nodeId,
        contentTypes: state.contentTypes.slice(), documents: state.documents.slice(),
        commits: state.commits.slice(), toolsVersion: state.toolsVersion,
        toolCount: state.tools ? state.tools.total : 0,
        renderColumns: state.render && state.render.columns ? state.render.columns.length : 0,
        theme: state.theme, tab: state.tab, selectedCommit: state.selectedCommit
      };
    },
    api: api, refresh: loadAll, loadSpaces: loadSpaces, createSpace: createSpace,
    createNode: createNode, addField: addField, saveType: saveType,
    createDocument: createDocument, selectNode: selectNode,
    loadTools: loadTools, revert: revert, switchTab: switchTab, applyTheme: applyTheme
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
