#!/usr/bin/env python3
"""Episteme P1+P2+P3+P4 REST / MCP 冒烟回归（零第三方依赖）。"""
import base64
import copy
import json
import re
import sys
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5080"
API = BASE + "/api/v1/episteme"
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

PASS = 0
FAIL = 0


def check(name, condition, detail=""):
    global PASS, FAIL
    if condition:
        PASS += 1
        print("  PASS  " + name)
    else:
        FAIL += 1
        print("  FAIL  " + name + " :: " + str(detail)[:400])


def http(method, url, body=None, headers=None, timeout=25):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    req = urllib.request.Request(
        url, data=data, method=method,
        headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with OPENER.open(req, timeout=timeout) as r:
            raw = r.read().decode("utf-8") or "{}"
            try:
                return r.status, json.loads(raw)
            except Exception:
                return r.status, raw
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8") or "{}"
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, raw
    except Exception as e:
        return -1, {"message": str(e)}


def rest(method, path, body=None, role="owner"):
    return http(method, API + path, body, {"X-Episteme-Role": role})


def mcp(method, params=None, role="owner", space=None):
    url = BASE + "/mcp" + (("?space=" + space) if space else "")
    body = {"jsonrpc": "2.0", "id": 1, "method": method}
    if params is not None:
        body["params"] = params
    return http("POST", url, body,
                {"X-Episteme-Role": role, "MCP-Protocol-Version": "2025-06-18"})


print("== Episteme P1+P2+P3+P4 REST / MCP 冒烟 ==")

code, health = http("GET", BASE + "/health")
check("健康检查 200", code == 200 and health.get("status") == "ok", health)

code, home = http("GET", BASE + "/")
html = home if isinstance(home, str) else str(home)
banned = ["素材", "热点", "拆文", "报告", "选题"]
check("空启动首页无业务专有名词", code == 200 and not any(w in html for w in banned),
      [w for w in banned if w in html])

slug = "smoke-" + time.strftime("%m%d%H%M%S")
code, space = rest("POST", "/spaces", {"name": "回归空间", "slug": slug, "source": "api"})
check("创建空间", code == 200 and bool(space.get("id")), space)
sid = space.get("id", "")

code, before = rest("GET", f"/spaces/{sid}/mcp/tools")
check("初始 30 个静态工具（P4 Pack 3 个 + v0.5 capabilities / fetch_web / upload_pdf_base64 / rebind_annotation）",
      code == 200 and before.get("total") == 30 and before.get("staticToolCount") == 30, before)

definition = {
    "slug": "paper", "label": "论文", "version": 1, "exposeToMcp": True,
    "primaryField": "name", "defaultPartKinds": ["md"],
    "fields": [
        {"key": "name", "label": "名称", "type": "text", "required": True,
         "indexed": True, "exposeToMcp": True, "mcpWritable": True, "order": 0},
        {"key": "year", "label": "年份", "type": "number", "required": True,
         "indexed": True, "exposeToMcp": True, "mcpWritable": True,
         "validation": {"min": 1900, "max": 2100, "integer": True}, "order": 1},
        {"key": "rating", "label": "评级", "type": "select", "required": False,
         "indexed": True, "exposeToMcp": True, "mcpWritable": True,
         "options": ["必读", "选读"], "order": 2},
        {"key": "reviewedBy", "label": "人工复核", "type": "text", "required": False,
         "indexed": False, "exposeToMcp": True, "mcpWritable": False, "order": 3},
        {"key": "internalCost", "label": "内部成本", "type": "number", "required": False,
         "indexed": False, "exposeToMcp": False, "mcpWritable": False, "order": 4}
    ],
    "views": [{"name": "全部", "filter": {}, "sort": []}]
}

code, ct = rest("POST", f"/spaces/{sid}/content-types",
                {"definition": definition, "nodePath": "/论文库", "source": "api"})
check("新建内容类型", code == 200 and ct.get("slug") == "paper", ct)
check("Schema 返回 render 描述",
      len(ct.get("render", {}).get("controls", [])) == 5, ct.get("render"))

code, after = rest("GET", f"/spaces/{sid}/mcp/tools")
names = [t["name"] for t in after.get("tools", [])]
new_tools = ["episteme.query_paper", "episteme.create_paper", "episteme.update_paper"]
check("Schema 改后 tools/list 同会话 +3",
      code == 200 and after.get("total") == 33 and all(n in names for n in new_tools),
      {"total": after.get("total"), "names": names[-5:]})
check("toolsVersion 递增", after.get("toolsVersion", 0) > before.get("toolsVersion", 0), after)

create_tool = next((t for t in after.get("tools", []) if t["name"] == "episteme.create_paper"), {})
props = create_tool.get("inputSchema", {}).get("properties", {})
required = set(create_tool.get("inputSchema", {}).get("required", []))
check("exposeToMcp=false 字段完全不出现", "internalCost" not in props, sorted(props))
check("mcpWritable=false 字段不出现在写工具", "reviewedBy" not in props, sorted(props))
check("必填字段由 Schema 映射", {"name", "year"} <= required, required)
check("MCP 初始化握手", mcp("initialize", {"protocolVersion": "2025-06-18"})
      [1].get("result", {}).get("protocolVersion") == "2025-06-18")

code, tree = rest("GET", f"/spaces/{sid}/tree")
node = next((n for n in tree.get("items", []) if n["path"] == "/论文库"), None)
check("Schema 自动创建并绑定目录", bool(node) and node.get("contentType") == "paper", tree)
if not node:
    print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
    sys.exit(1)

code, render = rest("GET", f"/spaces/{sid}/nodes/{node['id']}/render")
check("动态渲染列 / 控件与 Schema 一致",
      code == 200 and len(render.get("columns", [])) == 5 and len(render.get("controls", [])) == 5,
      render)

code, invalid = rest("POST", f"/spaces/{sid}/content-types/paper/validate",
                     {"fields": {"year": 1800}})
check("字段校验按 Schema 生效", code == 200 and invalid.get("valid") is False, invalid)


def new_doc(title, source, extra=None):
    body = {
        "title": title, "fields": {"name": title, "year": 2026, "rating": "必读"},
        "parts": [{"kind": "md", "content": "# " + title + "\n第一版"}],
        "source": source
    }
    if extra:
        body.update(extra)
    return rest("POST", f"/spaces/{sid}/nodes/{node['id']}/documents", body)


code, d1 = new_doc("UI 写入", "ui")
check("UI 路径写文档生成 Commit", code == 200 and bool(d1.get("commitId")), d1)
code, d2 = new_doc("API 写入", "api")
check("REST 路径写文档生成 Commit", code == 200 and bool(d2.get("commitId")), d2)
code, rpc = mcp("tools/call", {
    "name": "episteme.create_paper",
    "arguments": {"title": "MCP 写入", "name": "MCP 写入", "year": 2026,
                  "parts": [{"kind": "md", "content": "# MCP 写入"}],
                  "reason": "冒烟写入", "idempotencyKey": "mcp-write-1"}
}, space=slug)
d3 = rpc.get("result", {}).get("structuredContent", {})
check("MCP 动态工具写文档生成 Commit", bool(d3.get("commitId")), rpc)

code, log = rest("GET", f"/spaces/{sid}/commits?limit=50")
sources = [c["source"] for c in log.get("items", [])]
check("UI / REST / MCP 三条写路径同一 Commit 链",
      all(s in sources for s in ("ui", "api", "mcp", "schema")), sources)

commits = log.get("items", [])
if len(commits) >= 2:
    code, diff = rest("GET", f"/spaces/{sid}/diff?base={commits[1]['id']}&target={commits[0]['id']}")
    check("diff 输出字段级 / 行级差异", code == 200 and diff.get("total", 0) >= 1, diff)
else:
    check("diff 输出字段级 / 行级差异", False, "提交不足 2 条")

code, docs_before_revert = rest("GET", f"/spaces/{sid}/documents")
latest = commits[0]["id"]
code, rev = rest("POST", f"/spaces/{sid}/reverts", {"commitId": latest, "source": "api"})
check("revert 生成反向提交", code == 200 and bool(rev.get("newCommitId")), rev)
code, docs_after_revert = rest("GET", f"/spaces/{sid}/documents")
check("revert 后内容回到父版本",
      docs_after_revert.get("total") == docs_before_revert.get("total", 0) - 1,
      {"before": docs_before_revert.get("total"), "after": docs_after_revert.get("total")})

code, idem1 = new_doc("幂等文档", "api", {"idempotencyKey": "idem-key-1"})
code, idem2 = new_doc("幂等文档", "api", {"idempotencyKey": "idem-key-1"})
check("幂等重放返回原 Commit",
      idem1.get("commitId") == idem2.get("commitId") and idem2.get("idempotentReplay") is True,
      {"first": idem1, "second": idem2})
code, log2 = rest("GET", f"/spaces/{sid}/commits?limit=200")
replays = [c for c in log2.get("items", []) if c.get("idempotencyKey") == "idem-key-1"]
check("同一幂等键只产生 1 条 Commit", len(replays) == 1, len(replays))

code, conflict = new_doc("冲突文档", "api", {"expectParent": "cm_not_exist"})
check("expectParent 乐观锁返回 409",
      code == 409 and conflict.get("code") == "CONFLICT_PARENT", conflict)

code, forbidden = mcp("tools/call", {
    "name": "episteme.define_content_type",
    "arguments": {"definition": {"slug": "hack", "label": "越权", "fields": [
        {"key": "x", "label": "X", "type": "text"}]}}
}, role="agent", space=slug)
err = forbidden.get("error", {}).get("data", {})
check("Agent 默认无权修改 Schema", err.get("code") == "FORBIDDEN_SCOPE", forbidden)

code, space_before = rest("GET", f"/spaces/{sid}")
code, branch = rest("POST", f"/spaces/{sid}/branches", {"name": "agent/smoke", "source": "api"})
check("创建 Agent 隔离分支", code == 200 and branch.get("created") is True, branch)
code, agent_doc = new_doc("Agent 分支文档", "api", {"branch": "agent/smoke"})
check("Agent 分支可写入", code == 200 and bool(agent_doc.get("commitId")), agent_doc)
code, space_after = rest("GET", f"/spaces/{sid}")
check("Agent 分支写入不影响 main HEAD",
      space_before.get("headCommitId") == space_after.get("headCommitId"),
      {"before": space_before.get("headCommitId"), "after": space_after.get("headCommitId")})
code, main_docs = rest("GET", f"/spaces/{sid}/documents?branch=main")
check("main 视图看不到 Agent 分支文档",
      all(d["title"] != "Agent 分支文档" for d in main_docs.get("items", [])), main_docs)

code, merge = rest("POST", f"/spaces/{sid}/merges", {"from": "agent/smoke", "source": "api"})
check("合并分支返回明确结果", code == 200 and
      (merge.get("merged") is True or len(merge.get("conflicts", [])) > 0), merge)

code, tags = rest("POST", f"/spaces/{sid}/tags", {"name": "v1", "source": "api"})
check("打标签", code == 200 and tags.get("name") == "refs/tags/v1", tags)

code, resources = mcp("resources/list", {}, space=slug)
uris = [r["uri"] for r in resources.get("result", {}).get("resources", [])]
check("resources/list 只列暴露节点", any("论文库" in u for u in uris), uris)

code, search = rest("GET", f"/spaces/{sid}/search?q=API")
check("全文检索命中", code == 200 and search.get("total", 0) >= 1, search)

code, audit = rest("GET", f"/spaces/{sid}/audit")
check("每次写入有审计记录", code == 200 and audit.get("total", 0) >= 3, audit.get("total"))

definition2 = copy.deepcopy(definition)
definition2["version"] = 2
definition2["fields"].append({
    "key": "pages", "label": "页数", "type": "number", "required": False,
    "indexed": False, "exposeToMcp": True, "mcpWritable": True,
    "defaultValue": 1, "order": 5})
code, ct2 = rest("PUT", f"/spaces/{sid}/content-types/paper",
                 {"definition": definition2, "source": "api"})
check("Schema 版本递增", code == 200 and ct2.get("version") == 2, ct2)
code, migrated = rest("GET", f"/spaces/{sid}/documents")
with_default = [d for d in migrated.get("items", []) if "pages" in d.get("fields", {})]
check("新增字段回填默认值",
      len(with_default) > 0 and all(d["fields"]["pages"] == 1 for d in with_default),
      [d["fields"] for d in migrated.get("items", [])[:3]])

# ---------------------------------------------------------------- P3 四类阅读器
print("\n---- P3 四类阅读器 / 稳健锚点 / 标注进版本 ----")

code, viewers = rest("GET", "/viewers")
vlist = viewers.get("items", [])
vmap = {v["kind"]: v for v in vlist}
check("Viewer 契约列出四类媒介 + 附件",
      code == 200 and set(["md", "code", "pdf", "web"]).issubset(set(vmap)), list(vmap))
check("Viewer capabilities：PDF 原件只读、Code/MD 可编辑、四类可标注",
      vmap.get("pdf", {}).get("capabilities", {}).get("editable") is False
      and vmap.get("code", {}).get("capabilities", {}).get("editable") is True
      and all(v["capabilities"]["annotatable"] for v in vlist), vmap.get("pdf"))

md_text = "# 多媒介文档\n第一行内容\n锚点句子在这里"
code_text = "using System;\npublic class Demo {\n  public int Value = 42;\n}"
pdf_text = "第一页：内容寻址快照 DAG 保证事务一致性\n第一页第二行\f第二页：稳健锚点四级回退"
web_html = "<html><head><style>x</style></head><body><article><h1>标题</h1><p>网页正文锚点句子</p></article></body></html>"
code, multi = rest("POST", f"/spaces/{sid}/nodes/{node['id']}/documents", {
    "title": "四类阅读器文档", "fields": {"name": "四类阅读器文档", "year": 2026, "rating": "必读"},
    "parts": [
        {"kind": "md", "content": md_text},
        {"kind": "code", "content": code_text, "language": "csharp"},
        {"kind": "pdf", "content": pdf_text, "sourceUri": "https://example.com/paper.pdf"},
        {"kind": "web", "content": web_html, "sourceUri": "https://example.com/article",
         "meta": {"httpStatus": 200}}
    ], "source": "api"
})
check("一份文档同时挂载 md / code / pdf / web 四类 Part", code == 200 and bool(multi.get("docId")), multi)
doc_id = multi.get("docId", "")
if not doc_id:
    print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
    sys.exit(1)

code, dv = rest("GET", f"/spaces/{sid}/documents/{doc_id}/viewers")
parts = dv.get("items", [])
bykind = {p["kind"]: p for p in parts}
check("四类 Part 各返回 Viewer 描述与统计",
      code == 200 and len(parts) == 4 and set(bykind) == {"md", "code", "pdf", "web"}, dv.get("total"))
check("PDF meta 自动推导页数 / 文本层",
      bykind.get("pdf", {}).get("meta", {}).get("pages") == 2
      and bykind.get("pdf", {}).get("meta", {}).get("textLayer") is True, bykind.get("pdf", {}).get("meta"))
check("Web meta 自动推导 extractor / httpStatus / 外部来源",
      bykind.get("web", {}).get("meta", {}).get("extractor") == "readability-lite"
      and bykind.get("web", {}).get("meta", {}).get("httpStatus") == 200
      and bykind.get("web", {}).get("meta", {}).get("origin") == "external", bykind.get("web", {}).get("meta"))
check("Code 自动统计行数并保留 language",
      bykind.get("code", {}).get("stats", {}).get("lines") == 4
      and bykind.get("code", {}).get("meta", {}).get("language") == "csharp", bykind.get("code", {}).get("meta"))
check("每个 Part 生成 64 位内容指纹并写入 meta",
      all(len(p.get("stats", {}).get("fingerprint", "")) == 64
          and len(p.get("meta", {}).get("fingerprint", "")) == 64 for p in parts),
      [p.get("stats", {}).get("fingerprint") for p in parts])

code, rp = rest("GET", f"/spaces/{sid}/documents/{doc_id}/parts/{bykind['code']['partId']}?from=2&to=3")
check("read_part 按行区间读取代码", code == 200 and
      [l["index"] for l in rp.get("lines", [])] == [2, 3], rp.get("lines"))
code, pp = rest("GET", f"/spaces/{sid}/documents/{doc_id}/parts/{bykind['pdf']['partId']}?page=2")
check("read_part 按 PDF 页码读取文本层", code == 200 and "第二页" in pp.get("text", "")
      and "第一页" not in pp.get("text", ""), pp.get("text"))
code, wp = rest("GET", f"/spaces/{sid}/documents/{doc_id}/parts/{bykind['web']['partId']}?mode=plain")
check("Web 纯文本抽取去掉标签与样式", code == 200 and "网页正文锚点句子" in wp.get("plainText", "")
      and "<article>" not in wp.get("plainText", ""), wp.get("plainText"))

code, edited = rest("PUT", f"/spaces/{sid}/documents/{doc_id}/parts/{bykind['md']['partId']}",
                    {"content": md_text + "\n新增一行", "source": "ui"})
check("Markdown 编辑返回 Commit 与新 blobHash",
      code == 200 and bool(edited.get("commitId")) and edited.get("kind") == "md", edited)
code, edit_commit = rest("GET", f"/spaces/{sid}/commits/{edited['commitId']}")
parent = (edit_commit.get("parents") or [""])[0]
code, line_diff = rest("GET", f"/spaces/{sid}/diff?base={parent}&target={edited['commitId']}")
added_lines = [l["text"] for it in line_diff.get("items", [])
               for l in it.get("lines", []) if l["change"] == "added"]
check("Markdown 编辑产生行级 diff", code == 200 and any("新增一行" in t for t in added_lines), added_lines)

code, denied = rest("PUT", f"/spaces/{sid}/documents/{doc_id}/parts/{bykind['pdf']['partId']}",
                    {"content": "篡改原件", "source": "api"})
check("PDF 原件只读，拒绝直接编辑", code == 409 and denied.get("code") == "PART_NOT_EDITABLE", denied)

md_text += "\n新增一行"


def annotate(kind, ann_kind, anchor, text=""):
    return rest("POST", f"/spaces/{sid}/documents/{doc_id}/annotations", {
        "partId": bykind[kind]["partId"], "kind": ann_kind,
        "text": text, "anchor": anchor, "source": "api"})


code, a_md = annotate("md", "highlight", {"textQuote": {"exact": "锚点句子在这里"}}, "重点")
code, a_code = annotate("code", "note", {"line": 3, "textQuote": {"exact": "public int Value = 42;"}}, "行级批注")
code, a_pdf = annotate("pdf", "highlight", {
    "page": 1, "rect": {"x": 72.5, "y": 310.2, "w": 180.4, "h": 14.2},
    "textQuote": {"exact": "内容寻址快照 DAG"}}, "PDF 高亮")
code, a_web = annotate("web", "note", {
    "domPath": "article/p[1]", "textQuote": {"exact": "网页正文锚点句子"}}, "划词批注")
ann_results = {"md": a_md, "code": a_code, "pdf": a_pdf, "web": a_web}
check("四类媒介均可生成高亮 / 批注",
      all(r.get("annotationId") for r in ann_results.values()), ann_results)
check("锚点归一化：kind / fingerprint / textPosition / fallback 齐备",
      all(r.get("anchor", {}).get("kind") == k
          and len(r.get("anchor", {}).get("fingerprint", "")) == 64
          and r.get("anchor", {}).get("fallback") for k, r in ann_results.items()),
      {k: r.get("anchor") for k, r in ann_results.items()})
check("标注归属产生它的 Commit（anchor.commitId 一致）",
      all(r.get("anchor", {}).get("commitId") == r.get("commitId") for r in ann_results.values()),
      {k: (r.get("commitId"), r.get("anchor", {}).get("commitId")) for k, r in ann_results.items()})
check("写入时锚点解析为 anchored",
      all(r.get("resolution", {}).get("status") == "anchored" for r in ann_results.values()),
      {k: r.get("resolution") for k, r in ann_results.items()})

bad_line_code, bad_line = annotate("code", "highlight", {"line": 999})
bad_page_code, bad_page = annotate("pdf", "highlight", {"page": 0})
bad_quote_code, bad_quote = annotate("md", "highlight", {"textQuote": {"exact": "根本不存在的句子"}})
bad_web_code, bad_web = annotate("web", "highlight", {})
bad_kind_code, bad_kind = rest("POST", f"/spaces/{sid}/documents/{doc_id}/annotations", {"kind": "unknown"})
check("非法锚点被拒：行号越界 / 页码为 0 / 引文不存在 / web 缺定位 / 标记类型未知",
      all(c == 422 for c in (bad_line_code, bad_page_code, bad_quote_code,
                             bad_web_code, bad_kind_code))
      and bad_line.get("code") == "ANCHOR_INVALID"
      and bad_kind.get("code") == "ANNOTATION_KIND_INVALID",
      {"codes": [bad_line_code, bad_page_code, bad_quote_code, bad_web_code, bad_kind_code],
       "bodies": [bad_line, bad_page, bad_quote, bad_web, bad_kind]})

last_ann = a_web["commitId"]
code, snap = rest("GET", f"/spaces/{sid}/documents/{doc_id}?ref={last_ann}")
check("历史 Commit 快照包含全部标注（标记进入版本）",
      code == 200 and len(snap.get("annotations", [])) >= 4, snap.get("annotations"))
code, ann_commit = rest("GET", f"/spaces/{sid}/commits/{last_ann}")
code, ann_diff = rest("GET", f"/spaces/{sid}/diff?base={(ann_commit.get('parents') or [''])[0]}&target={last_ann}")
check("diff 输出标注级差异", code == 200 and
      any(it.get("annotations") for it in ann_diff.get("items", [])), ann_diff.get("items"))

# Re-fetch the web snapshot: the old quote disappears, so the anchor must relocate, never vanish.
code, refetch = rest("PUT", f"/spaces/{sid}/documents/{doc_id}/parts/{bykind['web']['partId']}",
                     {"content": web_html.replace("网页正文锚点句子", "网页正文已更新"),
                      "sourceUri": "https://example.com/article?v=2",
                      "meta": {"httpStatus": 200}, "source": "api"})
check("Web 快照可重抓并更新 sourceUri / meta", code == 200 and bool(refetch.get("commitId")), refetch)
code, anns = rest("GET", f"/spaces/{sid}/documents/{doc_id}/annotations")
web_ann = next((a for a in anns.get("items", []) if a["id"] == a_web["annotationId"]), {})
check("内容变更后锚点自动降级重定位，不静默丢弃",
      anns.get("total") == 4 and web_ann.get("status") in ("relocated", "lost")
      and web_ann.get("resolution", {}).get("strategy") in ("dom-path", "text-quote", "none"),
      {"total": anns.get("total"), "web": web_ann})

code, rpc_viewers = mcp("tools/call", {"name": "episteme.viewers", "arguments": {}}, space=slug)
check("MCP viewers 工具返回统一契约",
      rpc_viewers.get("result", {}).get("structuredContent", {}).get("total", 0) >= 5, rpc_viewers)
code, rpc_note = mcp("tools/call", {
    "name": "episteme.annotate",
    "arguments": {"docId": doc_id, "partId": bykind["pdf"]["partId"], "kind": "note",
                  "text": "MCP 批注", "anchor": {"page": 2}}
}, space=slug)
mcp_note = rpc_note.get("result", {}).get("structuredContent", {})
check("MCP annotate 带锚点写入并生成 Commit",
      bool(mcp_note.get("commitId")) and mcp_note.get("partKind") == "pdf", rpc_note)

code, exported = rest("POST", f"/spaces/{sid}/documents/{doc_id}/exports/notes", {"source": "ui"})
check("导出标注笔记生成新 md Part + Commit",
      code == 200 and exported.get("annotationCount") >= 5
      and "标注汇总" in exported.get("markdown", ""), exported)
code, exported2 = mcp("tools/call", {
    "name": "episteme.export_notes", "arguments": {"docId": doc_id}
}, space=slug)
check("MCP export_notes 可用",
      bool(exported2.get("result", {}).get("structuredContent", {}).get("commitId")), exported2)
code, dv2 = rest("GET", f"/spaces/{sid}/documents/{doc_id}/viewers")
check("导出后 Part 数增加且跨 Part 汇总标注",
      dv2.get("total", 0) >= 6 and dv2.get("annotationTotal", 0) >= 5, dv2.get("total"))

code, log3 = rest("GET", f"/spaces/{sid}/commits?limit=1")
head3 = (log3.get("items") or [{}])[0].get("id", "")
code, rev3 = rest("POST", f"/spaces/{sid}/reverts", {"commitId": head3, "source": "api"})
code, anns3 = rest("GET", f"/spaces/{sid}/documents/{doc_id}/annotations")
check("revert 后标注随父快照恢复（partId 重映射，不丢失）",
      code == 200 and rev3.get("newCommitId") and anns3.get("total", 0) >= 5,
      {"revert": rev3, "annotations": anns3.get("total")})

# ---------------- P4: declarative Packs ----------------
print("\n---- P4 声明式 Pack / 模板导入导出 ----")
code, packs = rest("GET", "/packs")
pack_items = packs.get("items", [])
check("内置 Pack 目录列出 3 套且校验通过",
      code == 200 and len(pack_items) == 3 and all(p.get("valid") for p in pack_items)
      and any(p.get("id") == "pack_git-ingest" for p in pack_items),
      [(p.get("id"), p.get("valid")) for p in pack_items])
wb_pack = next((p for p in pack_items if p.get("id") == "workbench"), {})
check("workbench Pack 声明 15 目录 / 12 内容类型",
      wb_pack.get("nodeCount") == 15 and wb_pack.get("contentTypeCount") == 12, wb_pack)

pack_slug = "pack-" + time.strftime("%H%M%S") + "-a"
code, pspace = rest("POST", "/spaces", {"name": "Pack 回归空间", "slug": pack_slug, "source": "api"})
psid = pspace.get("id", "")
check("创建 Pack 测试空间", code == 200 and bool(psid), pspace)

code, ptree_before = rest("GET", f"/spaces/{psid}/tree")
code, pdry = rest("POST", f"/spaces/{psid}/packs/install",
                  {"packId": "workbench", "dryRun": True, "source": "api"})
code, ptree_after_dry = rest("GET", f"/spaces/{psid}/tree")
check("dryRun 预览不写入任何数据",
      code == 200 and pdry.get("dryRun") is True and pdry.get("commitId") is None
      and ptree_before.get("total") == 0 and ptree_after_dry.get("total") == 0,
      {"dryRun": pdry, "before": ptree_before.get("total"), "after": ptree_after_dry.get("total")})

code, pinst = rest("POST", f"/spaces/{psid}/packs/install",
                   {"packId": "workbench", "source": "api", "idempotencyKey": "pack-once"})
code, ptree = rest("GET", f"/spaces/{psid}/tree")
code, ptypes = rest("GET", f"/spaces/{psid}/content-types")
ppaths = [n.get("path", "") for n in ptree.get("items", [])]
check("一次事务安装出 15 目录 / 12 内容类型",
      code == 200 and pinst.get("createdNodes") == 15
      and pinst.get("createdContentTypes") == 12
      and ptree.get("total") == 15 and ptypes.get("total") == 12,
      {"install": pinst, "tree": ptree.get("total"), "types": ptypes.get("total")})
check("11 个 Workbench 模块目录全部生成",
      all(any(f"M{i}-" in p for p in ppaths) for i in range(1, 12)), ppaths[:3])
check("安装产生一条 source=import 的 Commit",
      pinst.get("commitSource") == "import" and bool(pinst.get("commitId")), pinst)
check("首次带幂等键写入不被误判为重放（IsReplay 修复）",
      pinst.get("idempotentReplay") is False and pinst.get("createdNodes") == 15, pinst)

code, pdup = rest("POST", f"/spaces/{psid}/packs/install", {"packId": "workbench", "source": "api"})
check("重复安装被 409 PACK_CONFLICT 拦截",
      code == 409 and pdup.get("code") == "PACK_CONFLICT", {"code": code, "body": pdup})
code, preplay = rest("POST", f"/spaces/{psid}/packs/install",
                     {"packId": "workbench", "source": "api", "idempotencyKey": "pack-once"})
check("幂等重放返回原 Commit 且不再写入",
      code == 200 and preplay.get("idempotentReplay") is True
      and preplay.get("commitId") == pinst.get("commitId")
      and preplay.get("createdNodes") == 0, preplay)

code, pexp = rest("POST", f"/spaces/{psid}/packs/export", {"source": "api"})
check("导出为合法 Pack JSON（目录 / 类型数一致）",
      code == 200 and pexp.get("valid") is True
      and pexp.get("summary", {}).get("nodeCount") == 15
      and pexp.get("summary", {}).get("contentTypeCount") == 12, pexp.get("summary"))
code, pspace2 = rest("POST", "/spaces",
                     {"name": "Pack 导入空间", "slug": "pack-" + time.strftime("%H%M%S") + "-b", "source": "api"})
psid2 = pspace2.get("id", "")
code, pimp = rest("POST", f"/spaces/{psid2}/packs/import",
                  {"pack": pexp.get("pack"), "source": "api"})
code, ptree2 = rest("GET", f"/spaces/{psid2}/tree")
check("导出结果可导入到另一个空间且目录一致",
      code == 200 and pimp.get("createdNodes") == 15 and ptree2.get("total") == 15,
      {"import": pimp, "tree": ptree2.get("total")})

bad_pack = {"id": "BAD ID", "name": "非法模板",
            "nodes": [{"path": "/x", "contentType": "missing"}], "contentTypes": []}
code, pbad = rest("POST", f"/spaces/{psid2}/packs/import", {"pack": bad_pack, "source": "api"})
check("非法 Pack（ID 非法 / 引用未声明类型）被 422 拒绝",
      code == 422 and pbad.get("code") == "PACK_INVALID", {"code": code, "body": pbad})
code, rpc_packs = mcp("tools/call", {"name": "episteme.list_packs", "arguments": {}}, space=pack_slug)
check("MCP list_packs 返回内置模板",
      rpc_packs.get("result", {}).get("structuredContent", {}).get("total", 0) == 3, rpc_packs)

code, pspace3 = rest("POST", "/spaces",
                     {"name": "示例 Pack 空间", "slug": "pack-" + time.strftime("%H%M%S") + "-c", "source": "api"})
psid3 = pspace3.get("id", "")
code, psamp = rest("POST", f"/spaces/{psid3}/packs/install",
                   {"packId": "pack_reading-desk", "includeSamples": True, "source": "api"})
code, pdocs = rest("GET", f"/spaces/{psid3}/documents")
check("includeSamples 同时导入示例文档",
      code == 200 and psamp.get("createdSampleDocuments", 0) >= 1
      and pdocs.get("total", 0) >= 1,
      {"install": psamp, "docs": pdocs.get("total")})

print("\n---- v0.5 真实媒介 / fetch-worker / 传输 ----")

code, caps = rest("GET", "/capabilities")
check("capabilities 声明版本、真实渲染器与生产开关",
      code == 200 and str(caps.get("version", "")).startswith("0.5")
      and "pdf.js" in caps.get("renderers", {}).get("pdf", "")
      and "Monaco" in caps.get("renderers", {}).get("code", "")
      and "sse" in caps.get("transports", []), caps)
check("health 暴露 authMode / dbProvider",
      str(health.get("authMode")) == "legacy" and str(health.get("dbProvider")) == "sqlite", health)

code, whealth = rest("GET", "/fetch-worker/health")
check("独立 fetch-worker 仅经 compose 内网可达",
      code == 200 and whealth.get("status") == "ok"
      and whealth.get("worker", {}).get("extractor") == "mozilla-readability", whealth)

for label, path in (("pdf.js 本地打包", "/vendor/pdfjs/pdf.min.mjs"),
                    ("pdf.js worker 本地打包", "/vendor/pdfjs/pdf.worker.min.mjs"),
                    ("Monaco loader 本地打包", "/vendor/monaco/vs/loader.js")):
    c, _body = http("GET", BASE + path)
    check(label + " 可离线加载", c == 200, c)

snapshot = ("<!doctype html><html><head><title>知书 · 抓取验收</title><style>.x{}</style></head>"
            "<body><nav>导航噪声</nav><article><h1>真实阅读器</h1>"
            "<p>第一段正文，用于验证 Readability 抽取与白名单清洗。</p>"
            "<p>第二段含 <a href=\"/next\">相对链接</a> 与 <strong>加粗</strong>。</p>"
            "<script>alert('xss')</script><img src=\"javascript:alert(1)\" onerror=\"alert(2)\">"
            "<a href=\"http://127.0.0.1:5080/admin\">内网链接</a></article></body></html>")
code, fetched = rest("POST", f"/spaces/{sid}/documents/{doc_id}/parts/web",
                     {"html": snapshot, "sourceUri": "https://example.com/post/1", "source": "api"})
check("离线 HTML 快照经 Readability 抽取并生成 web Part",
      code == 200 and fetched.get("extractor") == "mozilla-readability"
      and fetched.get("mode") == "offline-snapshot" and fetched.get("sanitized") is True
      and fetched.get("htmlLength", 0) > 40, fetched)
code, fetched_view = rest("GET", f"/spaces/{sid}/documents/{doc_id}/viewers")
new_web = next((p for p in fetched_view.get("items", [])
                if p.get("partId") == fetched.get("partId")), {})
clean = new_web.get("content", "")
check("抽取结果不含 script / 事件属性 / javascript: / 内网链接",
      "<script" not in clean.lower() and "onerror" not in clean.lower()
      and "javascript:" not in clean.lower() and "127.0.0.1" not in clean
      and "第一段正文" in clean, clean[:240])
check("相对链接绝对化且 meta 记录 extractor / finalUrl",
      "https://example.com/next" in clean
      and new_web.get("meta", {}).get("extractor") == "mozilla-readability",
      new_web.get("meta", {}))

ssrf_targets = [
    ("环回地址", "http://127.0.0.1:5080/health"),
    ("云元数据", "http://169.254.169.254/latest/meta-data/"),
    ("内网域名", "http://localhost/"),
    ("非标准端口", "http://example.com:8080/admin"),
    ("非 http 协议", "file:///etc/passwd"),
    ("私有网段", "http://192.168.1.1/"),
]
ssrf_ok = True
ssrf_detail = []
for label, target in ssrf_targets:
    c, body = rest("POST", f"/spaces/{sid}/documents/{doc_id}/parts/web",
                   {"url": target, "source": "api"})
    blocked = c in (400, 422) and body.get("code") == "SSRF_BLOCKED"
    ssrf_ok = ssrf_ok and blocked
    ssrf_detail.append({"target": target, "status": c, "code": body.get("code")})
check("SSRF 六类攻击面全部被 fetch-worker 拒绝", ssrf_ok, ssrf_detail)

c, allow = rest("POST", f"/spaces/{sid}/documents/{doc_id}/parts/web",
                {"url": "https://example.com/", "allowedHosts": ["wikipedia.org"], "source": "api"})
check("单次抓取域名白名单可进一步收紧", c in (400, 422)
      and allow.get("code") == "SSRF_BLOCKED", {"status": c, "body": allow})

code, refetched = rest("POST", f"/spaces/{sid}/documents/{doc_id}/parts/web",
                       {"html": snapshot.replace("第一段正文", "重抓后的正文"),
                        "replacePartId": fetched.get("partId"), "source": "api"})
check("重抓覆盖同一 web Part 而不新增 Part",
      code == 200 and refetched.get("partId") == fetched.get("partId"), refetched)

pdf_bytes = (b"%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n"
             b"2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n"
             b"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >> endobj\n"
             b"trailer << /Root 1 0 R >>\n%%EOF\n")
code, uploaded = rest("POST", f"/spaces/{sid}/documents/{doc_id}/parts/pdf",
                      {"base64": base64.b64encode(pdf_bytes).decode("ascii"),
                       "name": "acceptance.pdf", "source": "api"})
check("真实 PDF 二进制写入内容寻址 blob 仓库",
      code == 200 and uploaded.get("pdfFormat") == "binary"
      and uploaded.get("byteLength") == len(pdf_bytes)
      and str(uploaded.get("blobHash", "")).startswith("sha256:"), uploaded)


def raw_get(path):
    """Read a binary endpoint without the JSON-oriented rest() helper."""
    import http.client
    conn = http.client.HTTPConnection("127.0.0.1", 5080, timeout=6)
    conn.request("GET", path, headers={"X-Episteme-Role": "owner"})
    resp = conn.getresponse()
    payload = resp.read()
    result = (resp.status, resp.getheader("Content-Type", ""), payload)
    conn.close()
    return result


bstatus, bctype, bbody = raw_get(uploaded.get("binaryUrl", "/"))
check("PDF 原件可由 /binary 端点按 application/pdf 读回",
      bstatus == 200 and "application/pdf" in bctype and bbody.startswith(b"%PDF-"),
      {"status": bstatus, "type": bctype, "head": bbody[:8]})

code, bad_pdf = rest("POST", f"/spaces/{sid}/documents/{doc_id}/parts/pdf",
                     {"base64": base64.b64encode(b"not a pdf").decode("ascii"), "source": "api"})
check("非 %PDF- 文件头被拒绝", code == 422 and bad_pdf.get("code") == "PDF_INVALID_HEADER", bad_pdf)

code, vfinal = rest("GET", f"/spaces/{sid}/documents/{doc_id}/viewers")
binary_part = next((p for p in vfinal.get("items", [])
                    if p.get("partId") == uploaded.get("partId")), {})
check("Viewer 为二进制 PDF 标记 pdfjs-canvas 与页数",
      binary_part.get("renderHint") == "pdfjs-canvas"
      and bool(binary_part.get("binaryUrl"))
      and binary_part.get("stats", {}).get("pages", 0) >= 1, binary_part.get("stats"))

code, rebound = rest("PUT", f"/spaces/{sid}/documents/{doc_id}/annotations/{a_web['annotationId']}",
                     {"anchor": {"kind": "web", "domPath": "article/p[1]",
                                 "textQuote": {"exact": "网页正文已更新"}},
                      "text": "交互式重新定位", "source": "api"})
check("丢失锚点可交互式重定位并产生新 Commit",
      code == 200 and rebound.get("anchor", {}).get("rebound") is True
      and bool(rebound.get("commitId"))
      and rebound.get("resolution", {}).get("status") in ("anchored", "relocated"), rebound)

code, rpc_caps = mcp("tools/call", {"name": "episteme.capabilities", "arguments": {}}, space=slug)
check("MCP capabilities 工具可用",
      rpc_caps.get("result", {}).get("structuredContent", {}).get("version", "").startswith("0.5"),
      rpc_caps.get("result", {}).get("structuredContent", {}).get("version"))
code, rpc_fetch = mcp("tools/call", {
    "name": "episteme.fetch_web",
    # Keep the snapshot long enough for Readability's charThreshold; a 9-character
    # fragment legitimately takes the worker's readability-fallback path.
    "arguments": {"docId": doc_id, "html": snapshot,
                  "sourceUri": "https://example.com/mcp/1", "source": "mcp"}
}, space=slug)
rpc_fetch_body = rpc_fetch.get("result", {}).get("structuredContent", {})
check("MCP fetch_web 走同一 worker 与同一写入链路",
      rpc_fetch_body.get("extractor") == "mozilla-readability"
      and bool(rpc_fetch_body.get("commitId")), rpc_fetch_body)

code, mcp_info = http("GET", BASE + "/mcp")
check("MCP 元信息声明 SSE / stdio 传输",
      code == 200 and "sse" in str(mcp_info.get("endpoints", {}))
      and "stdio" in str(mcp_info.get("endpoints", {})), mcp_info)
code, sessions = http("GET", BASE + "/mcp/sessions")
check("SSE 会话端点可用", code == 200 and sessions.get("transport") == "sse", sessions)


def sse_roundtrip():
    # Alias the import: a bare `import http.client` here shadows the module-level
    # http() request helper used for the POST below.
    import http.client as hc
    conn = hc.HTTPConnection("127.0.0.1", 5080, timeout=8)
    conn.request("GET", "/mcp/sse", headers={"Accept": "text/event-stream"})
    resp = conn.getresponse()
    # HTTPResponse has no settimeout(); the underlying socket inherits the
    # connection's timeout, which bounds every readline() below.
    if conn.sock is not None:
        conn.sock.settimeout(6)
    first_lines = []
    try:
        for _ in range(5):
            line = resp.readline().decode("utf-8", "replace")
            first_lines.append(line)
            if not line or line.strip() == "":
                break
    except Exception:
        pass
    first = "".join(first_lines)
    match = re.search(r"sessionId=([A-Za-z0-9_.-]+)", first)
    sid_sse = match.group(1) if match else ""
    ok_frame = resp.status == 200 and "event: endpoint" in first and bool(sid_sse)
    pushed = False
    if sid_sse:
        code2, _ = http("POST", BASE + "/mcp/messages?sessionId=" + sid_sse,
                        {"jsonrpc": "2.0", "id": 7, "method": "ping"})
        pushed_code_ok = code2 == 202
        pushed_lines = []
        try:
            for _ in range(5):
                line = resp.readline().decode("utf-8", "replace")
                pushed_lines.append(line)
                if not line or line.strip() == "":
                    break
        except Exception:
            pass
        chunk = "".join(pushed_lines)
        pushed = pushed_code_ok and "event: message" in chunk and '"jsonrpc":"2.0"' in chunk
    conn.close()
    return ok_frame, pushed, first[:160]


sse_ok, sse_push, sse_first = sse_roundtrip()
check("SSE 首帧下发 endpoint 会话地址", sse_ok, sse_first)
check("SSE messages 往返推送同一 JSON-RPC 信封", sse_push, sse_first)

print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
sys.exit(1 if FAIL else 0)
