/* Episteme fetch-worker — isolated web capture with SSRF guards and Readability extraction.
 * Runs on the compose internal network only; no port is published to the host.
 * Node 22 built-in HTTP + fetch; the only npm dependencies are Readability and linkedom.
 */
import http from 'node:http';
import dns from 'node:dns/promises';
import { isIP } from 'node:net';
import { timingSafeEqual } from 'node:crypto';
import { Readability } from '@mozilla/readability';
import { parseHTML } from 'linkedom';

const PORT = Number(process.env.EPISTEME_FETCH_PORT || 8090);
const TOKEN = process.env.EPISTEME_FETCH_TOKEN || '';
const GLOBAL_ALLOWLIST = csv(process.env.EPISTEME_FETCH_ALLOWLIST);
const MAX_BYTES = Number(process.env.EPISTEME_FETCH_MAX_BYTES || 2 * 1024 * 1024);
const MAX_BODY = 64 * 1024;
const TIMEOUT_MS = Number(process.env.EPISTEME_FETCH_TIMEOUT_MS || 9000);
const MAX_REDIRECTS = 3;

const ALLOWED_TAGS = new Set([
  'p', 'div', 'span', 'section', 'article', 'main', 'header', 'footer',
  'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'ul', 'ol', 'li', 'dl', 'dt', 'dd',
  'blockquote', 'pre', 'code', 'em', 'strong', 'b', 'i', 'u', 's', 'del', 'ins',
  'a', 'br', 'hr', 'figure', 'figcaption', 'img', 'table', 'thead', 'tbody',
  'tfoot', 'tr', 'th', 'td', 'caption', 'colgroup', 'col', 'sup', 'sub',
  'small', 'abbr', 'cite', 'q', 'time', 'mark'
]);
const DROP_CONTENT = new Set([
  'script', 'style', 'noscript', 'iframe', 'object', 'embed', 'svg', 'math',
  'form', 'input', 'button', 'select', 'textarea', 'template', 'canvas',
  'video', 'audio', 'source', 'link', 'meta', 'base'
]);

function csv(value) {
  return String(value || '').split(',').map(s => s.trim().toLowerCase()).filter(Boolean);
}

function workerError(code, message, status = 422) {
  const err = new Error(message);
  err.code = code;
  err.status = status;
  return err;
}

function safeEqual(a, b) {
  if (!a || !b) return false;
  const x = Buffer.from(String(a)), y = Buffer.from(String(b));
  return x.length === y.length && timingSafeEqual(x, y);
}

/* ---------- SSRF guards ---------- */

function forbiddenIp(ip) {
  const version = isIP(ip);
  if (version === 4) {
    const [a, b, c, d] = ip.split('.').map(Number);
    if (a === 0 || a === 10 || a === 127 || a >= 224) return true;
    if (a === 169 && b === 254) return true;                 // link-local / cloud metadata
    if (a === 172 && b >= 16 && b <= 31) return true;
    if (a === 192 && b === 168) return true;
    if (a === 192 && b === 0 && (c === 0 || c === 2)) return true;
    if (a === 100 && b >= 64 && b <= 127) return true;      // CGNAT
    if (a === 198 && (b === 18 || b === 19)) return true;
    if (a === 198 && b === 51 && c === 100) return true;
    if (a === 203 && b === 0 && c === 113) return true;
    if (a === 255 && b === 255 && c === 255 && d === 255) return true;
    return false;
  }
  if (version === 6) {
    const low = ip.toLowerCase();
    if (low === '::' || low === '::1') return true;
    if (/^f[cd]/.test(low)) return true;                    // fc00::/7 unique local
    if (/^fe[89ab]/.test(low)) return true;                 // fe80::/10 link local
    if (low.startsWith('ff')) return true;                  // multicast
    if (low.startsWith('2001:db8')) return true;            // documentation
    const mapped = /::ffff:(\d+\.\d+\.\d+\.\d+)$/.exec(low);
    if (mapped) return forbiddenIp(mapped[1]);
    return false;
  }
  return true;                                              // not an IP at all
}

function hostLooksInternal(hostname) {
  const h = hostname.toLowerCase().replace(/\.$/, '');
  if (h === 'localhost' || h.endsWith('.localhost')) return true;
  if (h.endsWith('.internal') || h.endsWith('.local') || h.endsWith('.localdomain')) return true;
  if (h === 'metadata.google.internal' || h === 'metadata') return true;
  if (h.endsWith('.arpa') || h.endsWith('.test') || h.endsWith('.invalid')) return true;
  if (!h.includes('.') && !h.includes(':')) return true;   // single-label hostnames
  return false;
}

function matchAllowlist(hostname, allowlist) {
  if (!allowlist.length) return true;
  const h = hostname.toLowerCase().replace(/\.$/, '');
  return allowlist.some(entry => h === entry || h.endsWith('.' + entry));
}

async function assertPublicUrl(raw, allowlist) {
  let url;
  try { url = new URL(raw); }
  catch { throw workerError('INVALID_URL', 'URL 无法解析', 400); }
  if (url.protocol !== 'http:' && url.protocol !== 'https:')
    throw workerError('SSRF_BLOCKED', '只允许 http / https 协议');
  if (url.username || url.password)
    throw workerError('SSRF_BLOCKED', 'URL 不允许携带 userinfo');
  const host = url.hostname.replace(/^\[|\]$/g, '').toLowerCase();
  if (hostLooksInternal(host))
    throw workerError('SSRF_BLOCKED', '内部 / 保留域名被拒绝：' + host);
  if (!matchAllowlist(host, GLOBAL_ALLOWLIST) || !matchAllowlist(host, allowlist))
    throw workerError('SSRF_BLOCKED', '域名不在抓取白名单内：' + host);

  if (isIP(host)) {
    if (forbiddenIp(host))
      throw workerError('SSRF_BLOCKED', 'IP 字面量指向内网 / 保留地址：' + host);
  } else {
    let addresses;
    try { addresses = await dns.lookup(host, { all: true, verbatim: true }); }
    catch { throw workerError('DNS_FAILED', '域名解析失败：' + host, 502); }
    if (!addresses.length) throw workerError('DNS_FAILED', '域名无解析结果：' + host, 502);
    for (const item of addresses)
      if (forbiddenIp(item.address))
        throw workerError('SSRF_BLOCKED', 'DNS 解析到内网 / 保留地址：' + item.address);
  }
  const port = url.port ? Number(url.port) : (url.protocol === 'https:' ? 443 : 80);
  if (port !== 80 && port !== 443)
    throw workerError('SSRF_BLOCKED', '只允许标准 80 / 443 端口');
  return url.toString();
}

/* ---------- HTTP fetch ---------- */

async function readCapped(response, limit) {
  if (!response.body) return Buffer.alloc(0);
  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > limit) {
      await reader.cancel();
      throw workerError('RESPONSE_TOO_LARGE', '响应体超过 ' + limit + ' 字节上限');
    }
    chunks.push(Buffer.from(value));
  }
  return Buffer.concat(chunks, total);
}

function decodeBody(buffer, contentType) {
  const match = /charset\s*=\s*["']?([\w-]+)/i.exec(contentType || '');
  const charset = (match && match[1]) || 'utf-8';
  try { return new TextDecoder(charset).decode(buffer); }
  catch { return new TextDecoder('utf-8').decode(buffer); }
}

async function capture(rawUrl, allowlist) {
  let current = rawUrl;
  let redirects = 0;
  for (let hop = 0; hop <= MAX_REDIRECTS; hop++) {
    current = await assertPublicUrl(current, allowlist);
    let response;
    try {
      response = await fetch(current, {
        redirect: 'manual',
        signal: AbortSignal.timeout(TIMEOUT_MS),
        headers: {
          'User-Agent': 'EpistemeFetchWorker/1.0 (document reader)',
          'Accept': 'text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.1',
          'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8'
        }
      });
    } catch (err) {
      const aborted = err && (err.name === 'TimeoutError' || err.name === 'AbortError');
      throw aborted
        ? workerError('FETCH_TIMEOUT', '抓取超时', 504)
        : workerError('FETCH_FAILED', '抓取失败：' + (err && err.message ? err.message : String(err)), 502);
    }
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      const location = response.headers.get('location');
      await response.body?.cancel().catch(() => {});
      if (!location) throw workerError('BAD_REDIRECT', '重定向缺少 Location 头', 502);
      if (hop >= MAX_REDIRECTS) throw workerError('TOO_MANY_REDIRECTS', '重定向次数超过上限');
      current = new URL(location, current).toString();
      redirects++;
      continue;
    }
    if (!response.ok)
      throw workerError('UPSTREAM_STATUS', '上游返回 HTTP ' + response.status, 502);
    const contentType = (response.headers.get('content-type') || '').toLowerCase();
    if (!/text\/html|application\/xhtml\+xml|text\/plain/.test(contentType))
      throw workerError('UNSUPPORTED_CONTENT_TYPE', '不支持的 Content-Type：' + (contentType || '未知'));
    const buffer = await readCapped(response, MAX_BYTES);
    return {
      finalUrl: current, status: response.status, contentType,
      html: decodeBody(buffer, contentType), bytes: buffer.byteLength, redirects
    };
  }
  throw workerError('TOO_MANY_REDIRECTS', '重定向次数超过上限');
}

/* ---------- extraction + sanitization ---------- */

function absolutize(value, base) {
  const v = String(value || '').trim();
  if (!v || /^(data:|javascript:|vbscript:|file:|blob:)/i.test(v)) return '';
  try {
    const u = new URL(v, base);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return '';
    const host = u.hostname.replace(/^\[|\]$/g, '').toLowerCase();
    if (hostLooksInternal(host) || (isIP(host) && forbiddenIp(host))) return '';
    const port = u.port ? Number(u.port) : (u.protocol === 'https:' ? 443 : 80);
    if (port !== 80 && port !== 443) return '';
    return u.toString();
  } catch { return ''; }
}

function sanitize(html, base) {
  const { document } = parseHTML('<div id="episteme-root">' + html + '</div>');
  const root = document.getElementById('episteme-root') || document.body;
  clean(root, base);
  return root.innerHTML;
}

function clean(node, base) {
  for (const child of Array.from(node.childNodes || [])) {
    if (child.nodeType === 1) {
      clean(child, base);                                  // sanitize the subtree first
      const tag = String(child.tagName || '').toLowerCase();
      if (DROP_CONTENT.has(tag) || !ALLOWED_TAGS.has(tag)) {
        if (DROP_CONTENT.has(tag)) { remove(child); continue; }
        for (const inner of Array.from(child.childNodes || []))
          node.insertBefore(inner, child);
        remove(child);
        continue;
      }
      const names = child.getAttributeNames ? child.getAttributeNames() : [];
      for (const name of names) {
        const lower = name.toLowerCase();
        if ((tag === 'a' && lower === 'href') || (tag === 'img' && lower === 'src')) continue;
        child.removeAttribute(name);
      }
      if (tag === 'a') {
        const href = absolutize(child.getAttribute('href'), base);
        child.removeAttribute('href');
        if (href) {
          child.setAttribute('href', href);
          child.setAttribute('rel', 'noopener noreferrer');
          child.setAttribute('target', '_blank');
        }
      }
      if (tag === 'img') {
        const src = absolutize(child.getAttribute('src'), base);
        child.removeAttribute('src');
        child.setAttribute('alt', '[图片]');
        if (src) { child.setAttribute('src', src); child.setAttribute('loading', 'lazy'); }
      }
    } else if (child.nodeType !== 3) {
      remove(child);
    }
  }
}

function remove(node) {
  if (node.parentNode) node.parentNode.removeChild(node);
  else if (typeof node.remove === 'function') node.remove();
}

function plainText(html) {
  return String(html || '')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'")
    .replace(/\s+/g, ' ').trim();
}

function extract(html, url, contentType) {
  const plainOnly = /text\/plain/.test(contentType || '');
  if (plainOnly) {
    const safe = '<article><pre>' + escapeHtml(html.slice(0, MAX_BYTES)) + '</pre></article>';
    return { title: new URL(url).hostname, byline: '', excerpt: '', siteName: '',
      html: safe, text: html.slice(0, MAX_BYTES), extractor: 'plain-text', links: [] };
  }
  try {
    const { document } = parseHTML(html);
    if (!document.body) throw new Error('no body');
    Object.defineProperty(document, 'baseURI', { value: url, configurable: true });
    Object.defineProperty(document, 'documentURI', { value: url, configurable: true });
    const article = new Readability(document, { charThreshold: 60 }).parse();
    if (article && article.content && article.textContent.trim().length > 0) {
      const cleanHtml = sanitize(article.content, url);
      const links = collectLinks(cleanHtml, url);
      return {
        title: article.title || '', byline: article.byline || '',
        excerpt: article.excerpt || '', siteName: article.siteName || '',
        html: cleanHtml, text: plainText(cleanHtml),
        extractor: 'mozilla-readability', links
      };
    }
  } catch (err) {
    // linkedom / Readability incompatibility on unusual markup: fall through to the safe fallback.
  }
  const cleanHtml = sanitize(html, url);
  const { document } = parseHTML(html);
  const title = (document.querySelector('title')?.textContent || '').trim();
  let body = document.querySelector('article, main, [role="main"], #content, .content');
  if (!body) body = document.body;
  const blocks = Array.from(body?.querySelectorAll('p, h1, h2, h3, h4, li, blockquote, pre') || []);
  const picked = blocks.length
    ? blocks.map(n => '<' + String(n.tagName || 'p').toLowerCase() + '>' +
        escapeHtml((n.textContent || '').trim()) + '</' + String(n.tagName || 'p').toLowerCase() + '>').join('\n')
    : '<p>' + escapeHtml(plainText(cleanHtml).slice(0, 20000)) + '</p>';
  return {
    title, byline: '', excerpt: plainText(picked).slice(0, 200), siteName: '',
    html: '<article>' + picked + '</article>', text: plainText(picked),
    extractor: 'readability-fallback', links: collectLinks(cleanHtml, url)
  };
}

function collectLinks(html, base) {
  const { document } = parseHTML(html);
  const out = [];
  for (const a of Array.from(document.querySelectorAll('a[href]') || [])) {
    const href = absolutize(a.getAttribute('href'), base);
    if (!href || out.includes(href)) continue;
    out.push(href);
    if (out.length >= 30) break;
  }
  return out;
}

function escapeHtml(s) {
  return String(s || '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[c]));
}

/* ---------- HTTP API ---------- */

async function readRequestBody(req) {
  const chunks = [];
  let total = 0;
  for await (const chunk of req) {
    total += chunk.length;
    if (total > MAX_BODY) throw workerError('BODY_TOO_LARGE', '请求体过大', 413);
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

function articlePayload(article, base, bytes) {
  return {
    bytes,
    fetchedAt: new Date().toISOString(),
    title: article.title,
    byline: article.byline,
    excerpt: article.excerpt,
    siteName: article.siteName,
    extractor: article.extractor,
    html: article.html,
    text: article.text,
    wordCount: article.text ? article.text.split(/\s+|(?<=[\u4e00-\u9fa5])/).filter(Boolean).length : 0,
    links: article.links,
    base: base
  };
}

function send(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'X-Content-Type-Options': 'nosniff',
    'Cache-Control': 'no-store'
  });
  res.end(body);
}

const server = http.createServer(async (req, res) => {
  try {
    if (req.method === 'GET' && (req.url === '/health' || req.url === '/')) {
      send(res, 200, {
        status: 'ok', service: 'episteme-fetch-worker', version: '1.0.0',
        extractor: 'mozilla-readability', allowlist: GLOBAL_ALLOWLIST,
        maxBytes: MAX_BYTES, timeoutMs: TIMEOUT_MS, maxRedirects: MAX_REDIRECTS
      });
      return;
    }
    if (req.method === 'POST' && (req.url === '/fetch' || req.url === '/extract')) {
      const raw = await readRequestBody(req);
      let body;
      try { body = JSON.parse(raw || '{}'); }
      catch { throw workerError('INVALID_JSON', '请求体不是合法 JSON', 400); }
      if (TOKEN && !safeEqual(req.headers['x-worker-token'] || body.token, TOKEN))
        throw workerError('UNAUTHORIZED', 'worker token 无效', 401);

      if (req.url === '/extract') {
        const html = String(body.html || '');
        if (!html) throw workerError('MISSING_HTML', '缺少 html', 400);
        const base = String(body.url || 'https://offline.example/');
        const article = extract(html, base, 'text/html');
        send(res, 200, Object.assign(articlePayload(article, base, Buffer.byteLength(html)), {
          ok: true, url: base, finalUrl: base, httpStatus: 0,
          contentType: 'text/html', redirects: 0, mode: 'offline-snapshot'
        }));
        return;
      }

      const target = String(body.url || '').trim();
      if (!target) throw workerError('MISSING_URL', '缺少 url', 400);
      const perRequestAllowlist = csv(Array.isArray(body.allowedHosts) ? body.allowedHosts.join(',') : body.allowedHosts);
      const captured = await capture(target, perRequestAllowlist);
      const article = extract(captured.html, captured.finalUrl, captured.contentType);
      const payload = articlePayload(article, captured.finalUrl, captured.bytes);
      payload.ok = true;
      payload.url = target;
      payload.finalUrl = captured.finalUrl;
      payload.httpStatus = captured.status;
      payload.contentType = captured.contentType;
      payload.redirects = captured.redirects;
      payload.mode = 'network-fetch';
      send(res, 200, payload);
      return;
    }
    send(res, 404, { ok: false, error: { code: 'NOT_FOUND', message: '未知端点' } });
  } catch (err) {
    const status = Number(err && err.status) || 500;
    send(res, status, {
      ok: false,
      error: {
        code: (err && err.code) || 'WORKER_INTERNAL',
        message: (err && err.message) || String(err)
      }
    });
  }
});

server.listen(PORT, '0.0.0.0', () => {
  console.log('[episteme-fetch-worker] listening on 0.0.0.0:' + PORT);
});
