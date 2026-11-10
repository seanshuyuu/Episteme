/* Episteme · 知书 — schema-driven SPA + four first-class Viewers. No build step, no dependencies. */
(function () {
  'use strict';

  var BASE = window.location.origin;
  var API = BASE + '/api/v1/episteme';
  var FIELD_TYPES = ['text', 'longtext', 'markdown', 'code', 'number', 'date', 'boolean',
    'select', 'multi_select', 'tags', 'url', 'ref', 'json', 'file', 'user'];
  var TABS = ['docs', 'reader', 'versions', 'mcp'];

  var state = {
    spaces: [], spaceId: null, spaceName: '', nodes: [], nodeId: null,
    contentTypes: [], render: null, documents: [], commits: [],
    tools: null, toolsVersion: 0, tokens: null, theme: 'mint',
    selectedCommit: null, tab: 'docs', fields: [],
    viewers: null, activeDocId: null, reader: null, partIndex: 0,
    readerMode: 'source', webMode: 'read', page: 1, selectedLine: 0,
    readerRef: '', readOnly: false
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
      api('/spaces/' + id + '/mcp/tools'),
      api('/viewers')
    ]);
    state.toolsVersion = results[0].toolsVersion || 0;
    $('tools-version').textContent = 'tools v' + state.toolsVersion;
    state.nodes = results[1].items || [];
    state.contentTypes = results[2].items || [];
    state.documents = results[3].items || [];
    state.commits = results[4].items || [];
    state.tools = results[5];
    state.viewers = results[6];
    renderNodes(); renderDocs(); renderCommits(); renderTools();
    if (state.nodeId) await selectNode(state.nodeId);
    else renderForm(null);
  }

  async function loadViewers() {
    state.viewers = await api('/viewers');
    return state.viewers;
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
      return '<div class="card doc-card" data-action="open-reader" data-doc-id="' + esc(d.id) + '">' +
        '<div class="title">' + esc(d.title) + '</div>' +
        '<div class="meta"><span class="badge">' + esc(d.branch || 'main') + '</span>' +
        esc(summary || (d.parts || []).length + ' 个 Part') + ' · 点击打开阅读器</div></div>';
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
      (it.annotations || []).forEach(function (a) {
        rows += '<div class="diff-line ' + (a.change === 'added' ? 'added' : 'removed') + '">' +
          (a.change === 'added' ? '+ ' : a.change === 'removed' ? '- ' : '~ ') +
          '标记 ' + esc(a.kind) + ' · ' + esc(a.text || a.id) + '</div>';
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
    TABS.forEach(function (name) {
      var el = $('tab-' + name);
      if (el) el.classList.toggle('hidden', name !== tab);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.tabs button'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-tab') === tab);
    });
  }

  // ---------- P3: four Viewers ----------

  function currentPart() {
    if (!state.reader || !state.reader.items || !state.reader.items.length) return null;
    return state.reader.items[Math.min(state.partIndex, state.reader.items.length - 1)] || null;
  }

  function defaultMode(kind) {
    if (kind === 'web') return 'read';
    if (kind === 'pdf') return 'text';
    return 'source';
  }

  async function openDocument(docId, ref) {
    if (!state.spaceId || !docId) { hint('reader-hint', '请先选择一个文档', 'error'); return null; }
    state.activeDocId = docId;
    state.readerRef = ref || '';
    var q = state.readerRef ? '?ref=' + encodeURIComponent(state.readerRef) : '';
    var data = await api('/spaces/' + state.spaceId + '/documents/' + docId + '/viewers' + q);
    state.reader = data;
    state.readOnly = !!data.readOnly;
    if (state.partIndex >= (data.items || []).length) state.partIndex = 0;
    state.page = 1;
    state.selectedLine = 0;
    var p = currentPart();
    state.readerMode = p ? defaultMode(p.kind) : 'source';
    switchTab('reader');
    renderReader();
    return data;
  }

  function renderReader() {
    var r = state.reader;
    if (!r) {
      $('reader-head').innerHTML = '<p class="muted">在「文档」页点开一份文档，即可用四类阅读器查看、编辑与标记。</p>';
      $('reader-part-tabs').innerHTML = '';
      $('reader-toolbar').innerHTML = '';
      $('reader-stage').innerHTML = '';
      $('reader-annotations').innerHTML = '';
      return;
    }
    var opts = '<option value="">当前工作区 · main</option>' + (state.commits || []).map(function (c) {
      return '<option value="' + esc(c.id) + '"' + (c.id === state.readerRef ? ' selected' : '') + '>' +
        esc(c.message) + ' · ' + esc((c.createdAt || '').slice(0, 16).replace('T', ' ')) + '</option>';
    }).join('');
    $('reader-head').innerHTML = '<div class="reader-title"><strong>' + esc(r.title || '文档') + '</strong>' +
      '<span class="badge">' + esc(r.ref) + '</span>' +
      (r.readOnly ? '<span class="badge warn">只读快照</span>' : '') +
      '<span class="muted">' + esc(r.total) + ' 个 Part · ' + esc(r.annotationTotal) + ' 条标记' +
      (r.anchorLost > 0 ? ' · <em>' + esc(r.anchorLost) + ' 条需重定位</em>' : '') + '</span></div>' +
      '<select id="reader-ref-select" aria-label="版本">' + opts + '</select>';
    var refSelect = $('reader-ref-select');
    if (refSelect) refSelect.addEventListener('change', function (e) {
      openDocument(state.activeDocId, e.target.value).catch(function (err) {
        hint('reader-hint', err.message, 'error');
      });
    });

    $('reader-part-tabs').innerHTML = (r.items || []).map(function (p, i) {
      return '<button data-action="select-part" data-index="' + i + '" class="part-tab' +
        (i === state.partIndex ? ' active' : '') + '">' + esc(p.viewer.label) + ' ' + (i + 1) +
        (p.annotationCount ? ' <span class="badge">' + esc(p.annotationCount) + '</span>' : '') + '</button>';
    }).join('');

    var p = currentPart();
    if (!p) {
      $('reader-toolbar').innerHTML = '';
      $('reader-stage').innerHTML = '<p class="muted">该文档还没有 Part。</p>';
    } else {
      var caps = p.viewer.capabilities || {};
      var modes = [];
      if (p.kind === 'md') modes = [['source', '源码'], ['preview', '预览']];
      else if (p.kind === 'code') modes = [['source', '编辑'], ['read', '阅读']];
      else if (p.kind === 'web') modes = [['read', '正文'], ['plain', '纯文本'], ['source', '原文快照']];
      var modeHtml = modes.map(function (m) {
        return '<button data-action="reader-mode" data-mode="' + m[0] + '" class="ghost' +
          (state.readerMode === m[0] ? ' active' : '') + '">' + m[1] + '</button>';
      }).join('');
      $('reader-toolbar').innerHTML = '<div class="toolbar-left">' + modeHtml +
        (p.kind === 'pdf' ? '<button data-action="reader-page" data-step="-1" class="ghost">上一页</button>' +
          '<span class="chip" id="page-chip">第 ' + state.page + ' / ' + partPages(p) + ' 页</span>' +
          '<button data-action="reader-page" data-step="1" class="ghost">下一页</button>' : '') +
        '</div><div class="toolbar-right">' +
        '<span class="chip">' + (caps.editable ? '可编辑' : '原件只读') + ' · 可标注</span>' +
        (!state.readOnly && caps.editable ? '<button data-action="save-part" class="primary">保存</button>' : '') +
        (!state.readOnly ? '<button data-action="show-part-form" class="ghost">+ Part</button>' : '') +
        '</div>';
      renderStage(p);
    }
    renderAnnotations();
  }

  function partPages(p) {
    var pages = String(p.content || '').split('\f');
    return pages.length;
  }

  function plainText(html) {
    return String(html || '')
      .replace(/<script[\s\S]*?<\/script>/gi, ' ')
      .replace(/<style[\s\S]*?<\/style>/gi, ' ')
      .replace(/<[^>]+>/g, ' ')
      .replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/&lt;/g, '<')
      .replace(/&gt;/g, '>').replace(/&quot;/g, '"')
      .replace(/\s+/g, ' ').trim();
  }

  function inlineMd(line) {
    var h = esc(line);
    h = h.replace(/`([^`]+)`/g, '<code>$1</code>');
    h = h.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    h = h.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" rel="noopener noreferrer" target="_blank">$1</a>');
    return h;
  }

  function markdownToHtml(md) {
    var lines = String(md || '').replace(/\r\n/g, '\n').split('\n');
    var out = [], inCode = false;
    lines.forEach(function (raw) {
      if (/^```/.test(raw)) {
        out.push(inCode ? '</code></pre>' : '<pre><code>');
        inCode = !inCode;
        return;
      }
      if (inCode) { out.push(esc(raw) + '\n'); return; }
      var line = raw;
      var m = /^(#{1,4})\s+(.*)$/.exec(line);
      if (m) { var n = m[1].length; out.push('<h' + n + '>' + inlineMd(m[2]) + '</h' + n + '>'); return; }
      if (/^\s*[-*]\s+\[[ xX]\]/.test(line)) {
        out.push('<div class="md-task">' + inlineMd(line.replace(/^\s*[-*]\s+/, '')) + '</div>'); return;
      }
      if (/^\s*[-*]\s+/.test(line)) { out.push('<li>' + inlineMd(line.replace(/^\s*[-*]\s+/, '')) + '</li>'); return; }
      if (/^&gt;|^\>/.test(line)) { out.push('<blockquote>' + inlineMd(line.replace(/^\>\s?/, '')) + '</blockquote>'); return; }
      if (/^\s*$/.test(line)) { out.push(''); return; }
      out.push('<p>' + inlineMd(line) + '</p>');
    });
    if (inCode) out.push('</code></pre>');
    return out.join('\n');
  }

  function highlightCode(line) {
    var h = esc(line);
    h = h.replace(/(&quot;[^&]*?&quot;|'[^']*')/g, '<span class="tok-str">$1</span>');
    h = h.replace(/\b(function|const|let|var|return|if|else|for|while|class|public|private|async|await|new|import|from|def|self|None|True|False|using|namespace)\b/g,
      '<span class="tok-kw">$1</span>');
    h = h.replace(/\b(\d+)\b/g, '<span class="tok-num">$1</span>');
    return h;
  }

  function codeLinesHtml(content, clickable) {
    return '<div class="code-lines">' + String(content || '').replace(/\r\n/g, '\n').split('\n')
      .map(function (line, i) {
        return '<div class="code-line">' +
          '<button class="line-no" data-action="select-line" data-line="' + (i + 1) + '">' + (i + 1) + '</button>' +
          '<code>' + highlightCode(line) + '</code></div>';
      }).join('') + '</div>';
  }

  function renderStage(p) {
    var box = $('reader-stage');
    var content = p.content || '';
    var editable = p.viewer.capabilities && p.viewer.capabilities.editable;
    var allowEdit = editable && !state.readOnly && state.readerMode === 'source';
    var html = '';

    if (p.kind === 'md') {
      html = state.readerMode === 'preview'
        ? '<div class="md-preview">' + markdownToHtml(content) + '</div>'
        : '<textarea id="part-editor" class="part-editor" spellcheck="false"' +
          (allowEdit ? '' : ' readonly') + '>' + esc(content) + '</textarea>';
    } else if (p.kind === 'code') {
      html = state.readerMode === 'read'
        ? codeLinesHtml(content, true)
        : '<textarea id="part-editor" class="part-editor code-editor" spellcheck="false"' +
          (allowEdit ? '' : ' readonly') + '>' + esc(content) + '</textarea>';
    } else if (p.kind === 'pdf') {
      var pages = content.split('\f');
      var page = Math.min(Math.max(1, state.page), pages.length);
      var lines = (pages[page - 1] || '').replace(/\r\n/g, '\n').split('\n');
      html = '<div class="pdf-note">PDF 原件只读 · 文本层模式（' + esc(p.stats.pages) + ' 页）· 改动以标记存储</div>' +
        '<div class="pdf-page" data-page="' + page + '">' +
        '<div class="pdf-page-head">第 ' + page + ' 页 · 点击文本行可生成区域锚点</div>' +
        lines.map(function (line, i) {
          return '<div class="pdf-line" data-action="select-line" data-line="' + (i + 1) + '">' +
            '<span class="pdf-line-no">' + (i + 1) + '</span>' + esc(line) + '</div>';
        }).join('') + '</div>';
    } else if (p.kind === 'web') {
      var text = plainText(content);
      html = '<div class="web-origin">外部内容 · 原文链接：' +
        (p.sourceUri ? '<a href="' + esc(p.sourceUri) + '" rel="noopener noreferrer" target="_blank">' + esc(p.sourceUri) + '</a>'
          : '未记录') + ' · 快照不执行脚本</div>';
      if (state.readerMode === 'source') {
        html += '<pre class="web-source">' + esc(content) + '</pre>';
      } else if (state.readerMode === 'plain') {
        html += '<pre class="web-plain">' + esc(text) + '</pre>';
      } else {
        html += '<div class="web-read">' + text.split(/(?<=[。！？.!?])\s+/).map(function (para) {
          return '<p>' + esc(para) + '</p>';
        }).join('') + '</div>';
      }
    } else {
      html = '<div class="attachment"><strong>' + esc(p.viewer.label) + '</strong>' +
        '<pre>' + esc(JSON.stringify(p.meta || {}, null, 2)) + '</pre></div>';
    }
    box.innerHTML = html;
  }

  function renderAnnotations() {
    var box = $('reader-annotations');
    var rows = [];
    (state.reader && state.reader.items || []).forEach(function (p) {
      (p.annotations || []).forEach(function (a) {
        rows.push({ part: p, a: a });
      });
    });
    if (!rows.length) { box.innerHTML = '<p class="muted">还没有标记。选中文字 / 点行号后填写上方表单即可添加。</p>'; return; }
    box.innerHTML = rows.map(function (row) {
      var a = row.a, res = a.resolution || {}, anchor = a.anchor || {};
      var quote = anchor.textQuote ? anchor.textQuote.exact : '';
      var status = res.status || a.status || 'anchored';
      var cls = status === 'lost' ? 'badge warn' : 'badge';
      return '<div class="annotation-card">' +
        '<div class="an-head"><span class="' + cls + '">' + esc(a.kind) + '</span>' +
        '<span class="muted">' + esc(row.part.viewer.label) + ' · ' + esc(status) +
        (res.strategy ? ' / ' + esc(res.strategy) : '') + '</span></div>' +
        (quote ? '<div class="an-quote">「' + esc(String(quote).slice(0, 120)) + '」</div>' : '') +
        (a.text ? '<div class="an-text">' + esc(a.text) + '</div>' : '') +
        '<div class="an-meta">' + esc(a.author) +
        (anchor.page ? ' · p.' + esc(anchor.page) : '') +
        (anchor.line ? ' · L' + esc(anchor.line) : '') +
        (anchor.commitId ? ' · ' + esc(String(anchor.commitId).slice(0, 14)) : '') + '</div></div>';
    }).join('');
  }

  function buildAnchor(p) {
    var a = { kind: p.kind };
    var quote = val('ann-quote');
    var line = Number(val('ann-line')) || state.selectedLine || 0;
    var page = Number(val('ann-page')) || state.page || 1;
    if (p.kind === 'code') {
      a.line = line || 1;
      if (quote) a.textQuote = { exact: quote };
    } else if (p.kind === 'pdf') {
      a.page = page;
      a.rect = { x: 72, y: 96 + (state.selectedLine || 1) * 14, w: 240, h: 14 };
      if (quote) a.textQuote = { exact: quote };
      if (p.stats && p.stats.fingerprint) a.pdfFingerprint = p.stats.fingerprint;
    } else if (p.kind === 'web') {
      a.domPath = val('ann-dom') || 'article/p[1]';
      if (quote) a.textQuote = { exact: quote };
    } else {
      if (quote) a.textQuote = { exact: quote };
      else a.blockIndex = 0;
      if (line > 0) a.line = line;
    }
    return a;
  }

  function selectLine(line) {
    var p = currentPart();
    if (!p) return null;
    state.selectedLine = line;
    var lineInput = $('ann-line'), pageInput = $('ann-page'), quoteInput = $('ann-quote');
    if (p.kind === 'pdf') {
      var pages = String(p.content || '').split('\f');
      var current = (pages[Math.min(state.page, pages.length) - 1] || '').replace(/\r\n/g, '\n').split('\n');
      if (quoteInput && current[line - 1]) quoteInput.value = current[line - 1].trim();
      if (pageInput) pageInput.value = String(state.page);
      hint('reader-hint', '已定位第 ' + state.page + ' 页第 ' + line + ' 行，可补充批注后添加标记', 'ok');
    } else {
      var lines = String(p.content || '').replace(/\r\n/g, '\n').split('\n');
      if (quoteInput && lines[line - 1]) quoteInput.value = lines[line - 1].trim();
      if (lineInput) lineInput.value = String(line);
      hint('reader-hint', '已选中第 ' + line + ' 行，可补充批注后添加标记', 'ok');
    }
    return line;
  }

  function changePage(step) {
    var p = currentPart();
    if (!p || p.kind !== 'pdf') return state.page;
    var max = partPages(p);
    state.page = Math.min(Math.max(1, state.page + step), max);
    state.selectedLine = 0;
    renderStage(p);
    var chip = $('page-chip');
    if (chip) chip.textContent = '第 ' + state.page + ' / ' + max + ' 页';
    return state.page;
  }

  function switchReaderMode(mode) {
    state.readerMode = mode;
    var p = currentPart();
    if (p) renderStage(p);
    Array.prototype.forEach.call(document.querySelectorAll('[data-action="reader-mode"]'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-mode') === mode);
    });
    return mode;
  }

  function selectPart(index) {
    state.partIndex = Number(index) || 0;
    state.page = 1;
    state.selectedLine = 0;
    var p = currentPart();
    state.readerMode = p ? defaultMode(p.kind) : 'source';
    renderReader();
    return state.partIndex;
  }

  async function savePart() {
    var p = currentPart(), ed = $('part-editor');
    if (!p || !ed) { hint('reader-hint', '当前 Part 没有可编辑内容', 'error'); return null; }
    if (state.readOnly) { hint('reader-hint', '只读历史快照不能编辑；可切换到当前工作区', 'error'); return null; }
    var res = await api('/spaces/' + state.spaceId + '/documents/' + state.activeDocId +
      '/parts/' + p.partId, {
      method: 'PUT', body: JSON.stringify({ content: ed.value, source: 'ui' })
    });
    hint('reader-hint', '已保存，Commit ' + res.commitId, 'ok');
    await openDocument(state.activeDocId, state.readerRef);
    await loadAll();
    return res;
  }

  async function addAnnotation() {
    var p = currentPart();
    if (!p) { hint('reader-hint', '请先打开一个文档 Part', 'error'); return null; }
    var body = {
      partId: p.partId, kind: val('ann-kind') || 'highlight',
      text: val('ann-text'), anchor: buildAnchor(p), source: 'ui'
    };
    var res = await api('/spaces/' + state.spaceId + '/documents/' + state.activeDocId + '/annotations', {
      method: 'POST', body: JSON.stringify(body)
    });
    var status = res.resolution && res.resolution.status;
    hint('reader-hint', '标记已写入版本快照 · Commit ' + res.commitId +
      (status ? ' · 锚点 ' + status : ''), 'ok');
    await openDocument(state.activeDocId, state.readerRef);
    await loadAll();
    return res;
  }

  async function exportNotes() {
    if (!state.activeDocId) { hint('reader-hint', '请先打开一个文档', 'error'); return null; }
    var res = await api('/spaces/' + state.spaceId + '/documents/' + state.activeDocId +
      '/exports/notes', { method: 'POST', body: JSON.stringify({ source: 'ui' }) });
    hint('reader-hint', '已导出 ' + res.annotationCount + ' 条标记为一个新 Part', 'ok');
    await openDocument(state.activeDocId, state.readerRef);
    await loadAll();
    return res;
  }

  async function appendPart() {
    if (!state.activeDocId) { hint('reader-hint', '请先打开一个文档', 'error'); return null; }
    var kind = val('new-part-kind') || 'md';
    var content = $('new-part-content') ? $('new-part-content').value : '';
    content = content.replace(/\\f/g, '\f');
    var body = { kind: kind, content: content, source: 'ui' };
    var uri = val('new-part-source');
    if (uri) body.sourceUri = uri;
    if (kind === 'code') body.language = 'csharp';
    if (kind === 'web') body.meta = { httpStatus: 200, extractor: 'readability-lite' };
    var res = await api('/spaces/' + state.spaceId + '/documents/' + state.activeDocId + '/parts', {
      method: 'POST', body: JSON.stringify(body)
    });
    hint('reader-hint', '已添加 ' + kind + ' Part · Commit ' + res.commitId, 'ok');
    hide('part-form');
    await openDocument(state.activeDocId, state.readerRef);
    await loadAll();
    return res;
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
            case 'open-reader': await openDocument(actionEl.getAttribute('data-doc-id')); break;
            case 'select-part': selectPart(actionEl.getAttribute('data-index')); break;
            case 'reader-mode': switchReaderMode(actionEl.getAttribute('data-mode')); break;
            case 'reader-page': changePage(Number(actionEl.getAttribute('data-step'))); break;
            case 'select-line': selectLine(Number(actionEl.getAttribute('data-line'))); break;
            case 'save-part': await savePart(); break;
            case 'add-annotation': await addAnnotation(); break;
            case 'export-notes': await exportNotes(); break;
            case 'append-part': await appendPart(); break;
            case 'show-part-form': show('part-form'); break;
          }
        } catch (e) {
          hint('doc-hint', e.message, 'error');
          hint('type-hint', e.message, 'error');
          hint('empty-hint', e.message, 'error');
          hint('reader-hint', e.message, 'error');
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

    // In a real browser, selecting text pre-fills the quote input for md / web / pdf.
    document.addEventListener('mouseup', function () {
      if (!window.getSelection) return;
      var sel = window.getSelection();
      var quote = sel ? String(sel.toString() || '').trim() : '';
      if (quote.length > 0 && quote.length < 500 && $('ann-quote')) $('ann-quote').value = quote;
    });
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
      var p = currentPart();
      return {
        spaces: state.spaces.slice(), spaceId: state.spaceId, spaceName: state.spaceName,
        nodes: state.nodes.slice(), nodeId: state.nodeId,
        contentTypes: state.contentTypes.slice(), documents: state.documents.slice(),
        commits: state.commits.slice(), toolsVersion: state.toolsVersion,
        toolCount: state.tools ? state.tools.total : 0,
        renderColumns: state.render && state.render.columns ? state.render.columns.length : 0,
        theme: state.theme, tab: state.tab, selectedCommit: state.selectedCommit,
        viewerCount: state.viewers ? state.viewers.total : 0,
        activeDocId: state.activeDocId, reader: state.reader,
        readerPartCount: state.reader ? state.reader.total : 0,
        readerKind: p ? p.kind : null, readerMode: state.readerMode,
        readerPartIndex: state.partIndex, readerPage: state.page,
        readerReadOnly: state.readOnly, readerRef: state.readerRef,
        annotationTotal: state.reader ? state.reader.annotationTotal : 0,
        anchorLost: state.reader ? state.reader.anchorLost : 0
      };
    },
    api: api, refresh: loadAll, loadSpaces: loadSpaces, createSpace: createSpace,
    createNode: createNode, addField: addField, saveType: saveType,
    createDocument: createDocument, selectNode: selectNode,
    loadTools: loadTools, revert: revert, switchTab: switchTab, applyTheme: applyTheme,
    loadViewers: loadViewers, openDocument: openDocument, selectPart: selectPart,
    switchReaderMode: switchReaderMode, changePage: changePage, selectLine: selectLine,
    savePart: savePart, addAnnotation: addAnnotation, exportNotes: exportNotes,
    appendPart: appendPart, currentPart: currentPart
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
