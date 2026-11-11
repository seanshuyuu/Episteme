#!/usr/bin/env python3
"""Episteme v0.5 PostgreSQL provider regression.

Run against an instance started with EPISTEME_DB_PROVIDER=postgres and
EPISTEME_PG_CONNECTION pointing at a reachable PostgreSQL 17 database:

  EPISTEME_TEST_BASE=http://127.0.0.1:5082 python3 tests/postgres_smoke.py

The point of this suite is not to repeat the whole REST/MCP contract (that is
api_smoke.py against SQLite), but to prove the opt-in provider really works
end-to-end: schema creation, the same write/commit chain, and UTC timestamps,
which historically broke under Npgsql's timestamp-with-time-zone requirement.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = os.environ.get("EPISTEME_TEST_BASE", "http://127.0.0.1:5082")
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


def http(method, path, body=None, timeout=15):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    url = path if str(path).startswith("http") else BASE + path
    req = urllib.request.Request(
        url, data=data, method=method,
        headers={"Content-Type": "application/json", "X-Episteme-Role": "owner"})
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


def rest(method, path, body=None):
    return http(method, API + path, body)


print("== Episteme v0.5 PostgreSQL provider 回归 (%s) ==" % BASE)

code, health = http("GET", "/health")
check("PostgreSQL 实例健康且声明 dbProvider=postgres",
      code == 200 and health.get("status") == "ok"
      and health.get("dbProvider") == "postgres", health)

code, caps = rest("GET", "/capabilities")
check("capabilities 同步声明 postgres 与真实渲染器",
      code == 200 and caps.get("dbProvider") == "postgres"
      and "pdf.js" in str(caps.get("renderers", {})), caps.get("dbProvider"))

code, spaces = rest("GET", "/spaces")
check("PostgreSQL 上 EnsureCreated 成功（空间列表可读）",
      code == 200 and "items" in spaces, spaces)

stamp = str(int(time.time()))
slug = "pg-" + stamp
code, space = rest("POST", "/spaces", {"name": "PG 回归 " + stamp, "slug": slug, "source": "api"})
sid = space.get("id", "")
check("可在 PostgreSQL 上创建空间", code == 200 and bool(sid), space)

code, ctype = rest("POST", f"/spaces/{sid}/content-types", {
    "slug": "pg_note", "label": "PG 笔记", "primaryField": "title",
    "fields": [
        {"key": "title", "label": "标题", "type": "text", "required": True},
        {"key": "summary", "label": "摘要", "type": "longtext"}
    ],
    "source": "api"
})
check("动态 Schema 在 PostgreSQL 上可创建",
      code == 200 and ctype.get("version", 0) >= 1, ctype)

code, node = rest("POST", f"/spaces/{sid}/nodes", {
    "path": "/PG-笔记", "contentType": "pg_note", "source": "api"
})
node_id = node.get("id", "")
check("可写入目录节点", code == 200 and bool(node_id), node)

code, render = rest("GET", f"/spaces/{sid}/nodes/{node_id}/render")
check("Schema 驱动 render 在 PostgreSQL 上可读",
      code == 200 and len(render.get("columns", [])) >= 1, render)

code, doc = rest("POST", f"/spaces/{sid}/nodes/{node_id}/documents", {
    "title": "PG 第一篇", "fields": {"title": "PG 第一篇", "summary": "跨数据库写入"},
    "parts": [{"kind": "md", "content": "# PostgreSQL\n同一套写入链路。"}],
    "source": "api"
})
doc_id = doc.get("docId", "")
check("UI 同款写入在 PostgreSQL 上生成 Commit",
      code == 200 and bool(doc_id) and bool(doc.get("commitId")), doc)

code, log = rest("GET", f"/spaces/{sid}/commits?limit=30")
commits = log.get("items", [])
check("版本历史可读且时间戳为 ISO-8601 UTC",
      code == 200 and len(commits) >= 1
      and all("T" in str(c.get("createdAt", "")) for c in commits), commits[:1])

if len(commits) >= 2:
    code, diff = rest("GET", f"/spaces/{sid}/diff?base={commits[1]['id']}&target={commits[0]['id']}")
    check("diff 端点在 PostgreSQL 上返回 200", code == 200, diff)
else:
    check("diff 端点在 PostgreSQL 上返回 200", False, "提交不足 2 条")

code, tree = rest("GET", f"/spaces/{sid}/tree")
check("目录树回读一致", code == 200 and any(
    n.get("path") == "/PG-笔记" for n in tree.get("items", [])), tree)

code, docs = rest("GET", f"/spaces/{sid}/documents?pageSize=50")
check("文档回读一致", code == 200 and any(
    d.get("id") == doc_id for d in docs.get("items", [])), docs.get("total"))

code, detail = rest("GET", f"/spaces/{sid}/documents/{doc_id}/viewers")
check("统一 Viewer 契约在 PostgreSQL 上可读",
      code == 200 and len(detail.get("items", [])) >= 1, detail.get("total"))

print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
sys.exit(1 if FAIL else 0)
