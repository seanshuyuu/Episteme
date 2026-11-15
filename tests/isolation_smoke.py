"""Cross-account isolation regression for EPISTEME_AUTH_MODE=users (ACCOUNT model).

Starts from an EMPTY database, creates two independent accounts each with its own
MCP-created space and its own per-account MCP token, then tries every plausible way
for account A to write into account B's space. Also verifies that omitting spaceId
cannot inherit a role, that the legacy grant matrix is gone, and that the identity-less
HMAC channel is disabled by default.

Usage:
    EPISTEME_TEST_BASE=http://127.0.0.1:5090 \
    EPISTEME_ADMIN_PASSWORD='...' \
    EPISTEME_MACHINE_SECRET='...' \
    EPISTEME_MACHINE_USER_SET=0|1 \
    python3 tests/isolation_smoke.py

EPISTEME_MACHINE_USER_SET=1 expects the instance to have been started with
EPISTEME_MACHINE_USER pointing at alice's account (bound HMAC is account-scoped).
"""
import hashlib
import hmac
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = os.environ.get("EPISTEME_TEST_BASE", "http://127.0.0.1:5090").rstrip("/")
API = BASE + "/api/v1/episteme"
AUTH = BASE + "/api/v1/auth"
ADMIN = BASE + "/api/v1/admin"
ADMIN_PASSWORD = os.environ.get("EPISTEME_ADMIN_PASSWORD", "")
MACHINE_SECRET = os.environ.get("EPISTEME_MACHINE_SECRET", "")
MACHINE_BOUND = os.environ.get("EPISTEME_MACHINE_USER_SET", "0") == "1"
SUFFIX = os.environ.get("EPISTEME_TEST_SUFFIX") or uuid.uuid4().hex[:8]
OP = urllib.request.build_opener(urllib.request.ProxyHandler({}))

PASS, FAIL = [], []


def check(label, ok, detail=None):
    (PASS if ok else FAIL).append(label)
    suffix = "" if ok else "   <<< FAIL " + json.dumps(detail, ensure_ascii=False)[:300]
    print(("  PASS  " if ok else "  FAIL  ") + label + suffix)


def req(method, path, body=None, token=None, extra=None):
    data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    if extra:
        headers.update(extra)
    url = path if "://" in path else BASE + path
    r = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with OP.open(r, timeout=25) as resp:
            return resp.status, json.loads(resp.read().decode() or "{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read().decode() or "{}")
        except Exception:
            return e.code, {}
    except Exception as e:
        return -1, {"error": str(e)}


def mcp(method, params, token=None, extra=None, query=""):
    return req("POST", "/mcp" + query,
               {"jsonrpc": "2.0", "id": 1, "method": method, "params": params},
               token, extra)


def code_of(res):
    if isinstance(res.get("error"), dict):
        return ((res["error"].get("data") or {}).get("code") or res.get("code") or "")
    return res.get("code") or ""


def sc_of(res):
    return ((res.get("result") or {}).get("structuredContent") or {})


def hmac_headers(method, path, body, secret, role="owner"):
    payload = json.dumps(body, ensure_ascii=False).encode() if body is not None else b""
    ts, nonce = str(int(time.time())), uuid.uuid4().hex
    canonical = "\n".join([method, path, "", ts, nonce, role,
                           hashlib.sha256(payload).hexdigest()])
    sig = hmac.new(secret.encode(), canonical.encode(), hashlib.sha256).hexdigest()
    return {"X-Episteme-Key": "iso-machine", "X-Episteme-Timestamp": ts,
            "X-Episteme-Nonce": nonce, "X-Episteme-Signature": sig,
            "X-Episteme-Role": role}


def write_doc(space=None, token=None, extra=None, query="", args_space=None):
    doc = {"nodePath": "/代码项目/桌面端", "title": "越权探针",
           "fields": {"title": "越权探针", "appType": "桌面端"}}
    params = {"name": "episteme.create_document", "arguments": doc}
    if space is not None:
        params["spaceId"] = space
    if args_space is not None:
        params["arguments"]["spaceId"] = args_space
    return mcp("tools/call", params, token, extra, query)


print("=" * 92)
print("Episteme 按账号授权跨账户隔离回归  base=%s  hmacBound=%s" % (BASE, MACHINE_BOUND))
print("=" * 92)
if not ADMIN_PASSWORD:
    print("需要 EPISTEME_ADMIN_PASSWORD")
    sys.exit(2)

st, lg = req("POST", AUTH + "/login", {"userName": "admin", "password": ADMIN_PASSWORD})
admin_t = lg.get("token", "")
check("管理员可登录", st == 200 and bool(admin_t), {"status": st, "res": lg})
if not admin_t:
    sys.exit(1)

accounts, spaces, tokens = {}, {}, {}
for who in ("alice", "bob"):
    name = who + "_" + SUFFIX
    st, r = req("POST", AUTH + "/register",
                {"userName": name, "password": "Iso-2026!pass",
                 "email": name + "@example.test", "displayName": who})
    accounts[who] = {"token": r.get("token", ""), "id": (r.get("user") or {}).get("id", ""),
                     "name": name}
    check("注册账号 " + who, st == 200 and bool(accounts[who]["token"]), {"status": st, "res": r})

    st, r = mcp("tools/call", {
        "name": "episteme.create_space",
        "arguments": {"name": who + " 的私有空间", "slug": who + "-" + SUFFIX,
                      "packId": "pack_code-workspace", "includeSamples": False},
    }, accounts[who]["token"])
    sc = sc_of(r)
    spaces[who] = {"id": sc.get("id", ""), "slug": who + "-" + SUFFIX}
    check("MCP create_space 为 " + who + " 建空间并绑定归属",
          st == 200 and bool(sc.get("id")) and sc.get("ownerUserId") == accounts[who]["id"],
          {"status": st, "res": r})

    st, r = req("POST", AUTH + "/me/mcp-token", {"action": "generate"}, accounts[who]["token"])
    tokens[who] = r.get("token", "")
    check("为 " + who + " 生成独立 MCP token",
          st == 200 and tokens[who].startswith("epm_"), {"status": st, "res": r.get("rotated")})

A, B = accounts["alice"], accounts["bob"]
SA, SB = spaces["alice"], spaces["bob"]

st, owners = req("GET", ADMIN + "/owners", token=admin_t)
pairs = [(m.get("userId"), m.get("spaceId")) for m in owners.get("items", [])]
check("归属表包含本轮两条且各自归属正确",
      (A["id"], SA["id"]) in pairs and (B["id"], SB["id"]) in pairs, pairs)

# ---- A. visibility ----------------------------------------------------------
for who, own in (("alice", SA), ("bob", SB)):
    st, r = req("GET", API + "/spaces", token=accounts[who]["token"])
    check(who + " 只看到自己的空间",
          [i.get("slug") for i in r.get("items", [])] == [own["slug"]], r.get("items"))
    st, r = req("GET", API + "/spaces", token=tokens[who])
    check(who + " 的 MCP token 也只看到自己的空间",
          [i.get("slug") for i in r.get("items", [])] == [own["slug"]], r.get("items"))

st, r = req("GET", API + "/spaces", token=admin_t)
check("管理员仍可看到全部空间", st == 200 and (r.get("total") or 0) >= 2, r.get("total"))

# ---- B. cross-account write matrix (login JWT + MCP token) ------------------
cases = [
    ("REST GET 他人空间文档（登录 JWT）",
     lambda: req("GET", API + "/spaces/" + SB["id"] + "/documents", token=A["token"])),
    ("REST POST 他人空间建节点（登录 JWT）",
     lambda: req("POST", API + "/spaces/" + SB["id"] + "/nodes",
                 {"path": "/HACK", "name": "HACK"}, A["token"])),
    ("MCP params.spaceId 指向他人空间",
     lambda: write_doc(SB["id"], A["token"])),
    ("MCP ?space= 指向他人空间",
     lambda: write_doc(None, A["token"], query="?space=" + SB["slug"])),
    ("伪造 X-Episteme-Role: owner 访问他人空间",
     lambda: req("POST", API + "/spaces/" + SB["id"] + "/nodes",
                 {"path": "/HACK2", "name": "HACK2"}, A["token"],
                 {"X-Episteme-Role": "owner"})),
    ("MCP arguments.spaceId 指向他人空间（不得静默改道）",
     lambda: write_doc(None, A["token"], args_space=SB["id"])),
    ("REST ?space= 列举他人空间工具",
     lambda: req("GET", API + "/mcp/tools?space=" + SB["slug"], token=A["token"])),
    ("MCP token REST 直读他人空间",
     lambda: req("GET", API + "/spaces/" + SB["id"] + "/documents", token=tokens["alice"])),
    ("MCP token 写他人空间",
     lambda: write_doc(SB["id"], tokens["alice"])),
]
for label, fn in cases:
    st, r = fn()
    check(label + " -> 403 ACCOUNT_FORBIDDEN",
          st == 403 and code_of(r) == "ACCOUNT_FORBIDDEN", {"status": st, "res": r})

# ---- C. own-account write + global-role boundary ----------------------------
st, r = write_doc(SA["id"], A["token"])
check("归属账号显式指定自己空间可以写入", bool(sc_of(r).get("docId")), r)
st, r = write_doc(None, A["token"])
check("省略 spaceId 回退到自己的空间可写入", bool(sc_of(r).get("docId")), r)
st, r = write_doc(SA["id"], tokens["alice"])
check("MCP token 写自己空间成功", bool(sc_of(r).get("docId")), r)

st, r = req("PATCH", ADMIN + "/users/" + A["id"], {"globalRole": "viewer"}, admin_t)
check("管理员把 alice 全局角色降为 viewer", st == 200, {"status": st, "res": r})
st, r = write_doc(SA["id"], A["token"])
check("viewer 显式 spaceId 写入被 FORBIDDEN_SCOPE 拒绝",
      code_of(r) == "FORBIDDEN_SCOPE", {"status": st, "res": r})
st, r = write_doc(None, A["token"])
check("viewer 省略 spaceId 也不能借用旧角色提权",
      code_of(r) == "FORBIDDEN_SCOPE" and not sc_of(r).get("docId"), {"status": st, "res": r})
st, r = write_doc(SA["id"], tokens["alice"])
check("viewer 的 MCP token 同样被降级",
      code_of(r) == "FORBIDDEN_SCOPE", {"status": st, "res": r})
st, r = req("PATCH", ADMIN + "/users/" + A["id"], {"globalRole": "editor"}, admin_t)
check("恢复 alice 全局角色", st == 200, {"status": st, "res": r})

# ---- D. legacy grant matrix removed -----------------------------------------
st, r = req("PUT", ADMIN + "/members",
            {"userId": A["id"], "spaceId": SB["id"], "role": "owner"}, admin_t)
check("旧空间授权矩阵返回 GRANT_MODEL_REMOVED",
      st == 409 and code_of(r) == "GRANT_MODEL_REMOVED", {"status": st, "res": r})
st, r = req("GET", ADMIN + "/members", token=admin_t)
check("旧授权列表降级为只读归属视图",
      st == 200 and r.get("deprecated") is True
      and r.get("authorizationModel") == "account", {"status": st, "res": r})
st, r = req("PUT", ADMIN + "/members",
            {"userId": A["id"], "spaceId": SB["id"], "role": "owner"}, A["token"])
check("普通账号不能调管理端点给自己授权",
      st == 403 and code_of(r) == "ADMIN_REQUIRED", {"status": st, "res": r})

# ---- E. HMAC machine channel ------------------------------------------------
if MACHINE_SECRET:
    rel = (API + "/spaces/" + SB["id"] + "/nodes")[len(BASE):]
    body = {"path": "/MACHINE-" + SUFFIX, "name": "MACHINE"}
    st, r = req("POST", rel, body, None, hmac_headers("POST", rel, body, MACHINE_SECRET))
    if MACHINE_BOUND:
        check("绑定账号的 HMAC 也只能写该账号名下空间",
              st == 403 and code_of(r) in ("ACCOUNT_FORBIDDEN", "MACHINE_SPACE_FORBIDDEN"),
              {"status": st, "res": r})
        rel_ok = (API + "/spaces/" + SA["id"] + "/nodes")[len(BASE):]
        body_ok = {"path": "/MACHINE-OK-" + SUFFIX, "name": "MACHINE-OK"}
        st2, r2 = req("POST", rel_ok, body_ok, None,
                      hmac_headers("POST", rel_ok, body_ok, MACHINE_SECRET))
        check("绑定账号的 HMAC 在自己空间内可正常写入", st2 == 200,
              {"status": st2, "res": r2})
    else:
        check("无账号身份的 HMAC 通道默认停用 MACHINE_CHANNEL_DISABLED",
              st == 401 and code_of(r) == "MACHINE_CHANNEL_DISABLED",
              {"status": st, "res": r})
else:
    print("  SKIP  机器通道用例（未提供 EPISTEME_MACHINE_SECRET）")

# ---- F. landing zones -------------------------------------------------------
st, r = req("GET", API + "/spaces/" + SB["id"] + "/documents", token=admin_t)
check("bob 空间始终 0 篇文档（没有一次越权写入落地）",
      st == 200 and r.get("total") == 0, {"status": st, "res": r})
st, r = req("GET", API + "/spaces/" + SA["id"] + "/documents", token=admin_t)
check("alice 的写入全部落在自己空间", st == 200 and (r.get("total") or 0) >= 3, r)

print("=" * 92)
print("isolation_smoke: %d passed / %d failed" % (len(PASS), len(FAIL)))
if FAIL:
    for f in FAIL:
        print("  FAILED: " + f)
sys.exit(1 if FAIL else 0)
