#!/usr/bin/env python3
"""Episteme P1+P2 REST / MCP 冒烟回归（零第三方依赖）。"""
import copy
import json
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


print("== Episteme P1+P2 REST / MCP 冒烟 ==")

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
check("初始仅 20 个静态工具",
      code == 200 and before.get("total") == 20 and before.get("staticToolCount") == 20, before)

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
      code == 200 and after.get("total") == 23 and all(n in names for n in new_tools),
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

print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
sys.exit(1 if FAIL else 0)
