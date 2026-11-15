#!/usr/bin/env python3
"""Per-account MCP token + account-scoped authorization regression (users mode).

Requires an EMPTY users-mode instance. Verifies the whole credential lifecycle
(status / generate / reset / revoke), that reset invalidates the old plaintext
immediately, that a token can only reach its own account's spaces, and that a
machine credential cannot manage itself or reach the admin console.

  EPISTEME_TEST_BASE=http://127.0.0.1:5090 \
  EPISTEME_ADMIN_PASSWORD='...' \
  EPISTEME_MACHINE_SECRET='...' (optional, to assert the HMAC channel is disabled) \
  python3 tests/mcp_token_smoke.py
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


def write_doc(space, token, title):
    return mcp("tools/call", {
        "spaceId": space, "name": "episteme.create_document",
        "arguments": {"nodePath": "/代码项目/桌面端", "title": title,
                      "fields": {"title": title, "appType": "桌面端"}}
    }, token)


def hmac_headers(method, path, body, secret, role="owner"):
    payload = json.dumps(body, ensure_ascii=False).encode() if body is not None else b""
    ts, nonce = str(int(time.time())), uuid.uuid4().hex
    canonical = "\n".join([method, path, "", ts, nonce, role,
                           hashlib.sha256(payload).hexdigest()])
    sig = hmac.new(secret.encode(), canonical.encode(), hashlib.sha256).hexdigest()
    return {"X-Episteme-Key": "legacy-ci", "X-Episteme-Timestamp": ts,
            "X-Episteme-Nonce": nonce, "X-Episteme-Signature": sig,
            "X-Episteme-Role": role}


print("=" * 92)
print("Episteme 按账号授权 / 每账号 MCP token 回归  base=%s" % BASE)
print("=" * 92)
if not ADMIN_PASSWORD:
    print("需要 EPISTEME_ADMIN_PASSWORD")
    sys.exit(2)

st, cfg = req("GET", AUTH + "/config")
check("auth/config 声明按账号授权与 MCP token 端点",
      st == 200 and cfg.get("authorizationModel") == "account"
      and (cfg.get("mcpToken") or {}).get("endpoint") == "/api/v1/auth/me/mcp-token", cfg)

st, caps = req("GET", API + "/capabilities")
sec = caps.get("security") or {}
check("capabilities 声明 accountScoped / perAccountMcpTokens / 无空间授权",
      st == 200 and sec.get("authorizationModel") == "account"
      and sec.get("accountScoped") is True and sec.get("perAccountMcpTokens") is True
      and sec.get("perSpaceGrants") is False, sec)

st, lg = req("POST", AUTH + "/login", {"userName": "admin", "password": ADMIN_PASSWORD})
admin_t = lg.get("token", "")
check("管理员登录", st == 200 and bool(admin_t), {"status": st, "res": lg})
if not admin_t:
    sys.exit(1)

accounts, spaces = {}, {}
for who in ("alice", "bob"):
    name = who + "_" + SUFFIX
    st, r = req("POST", AUTH + "/register",
                {"userName": name, "password": "Tok-2026!pass", "displayName": who})
    accounts[who] = {"token": r.get("token", ""), "id": (r.get("user") or {}).get("id", ""),
                     "name": name}
    check("注册账号 " + who, st == 200 and bool(accounts[who]["token"]), {"status": st, "res": r})
    st, r = mcp("tools/call", {
        "name": "episteme.create_space",
        "arguments": {"name": who + " 的私有空间", "slug": who + "-" + SUFFIX,
                      "packId": "pack_code-workspace", "includeSamples": False,
                      "description": "按账号授权回归"}
    }, accounts[who]["token"])
    sc = sc_of(r)
    spaces[who] = sc.get("id", "")
    check("MCP create_space 为 " + who + " 建空间并绑定归属",
          st == 200 and bool(spaces[who]) and sc.get("ownerUserId") == accounts[who]["id"],
          {"status": st, "res": r})

A, B = accounts["alice"], accounts["bob"]
SA, SB = spaces["alice"], spaces["bob"]

st, owners = req("GET", ADMIN + "/owners", token=admin_t)
pairs = [(o.get("userId"), o.get("spaceId")) for o in owners.get("items", [])]
check("空间归属表包含本轮两条且各归其主",
      (A["id"], SA) in pairs and (B["id"], SB) in pairs
      and owners.get("authorizationModel") == "account", pairs)

# ---- A. token status + generate ---------------------------------------------
st, status = req("GET", AUTH + "/me/mcp-token", token=A["token"])
check("初始状态：无令牌且不泄露任何明文",
      st == 200 and status.get("hasToken") is False
      and "token" not in status and status.get("prefix") == "epm_", status)

st, gen = req("POST", AUTH + "/me/mcp-token", {"action": "generate"}, A["token"])
tok1 = gen.get("token", "")
check("登录会话可生成专属长期令牌（一次性明文）",
      st == 200 and gen.get("generated") is True and gen.get("rotated") is False
      and tok1.startswith("epm_") and len(tok1) > 40, {"status": st, "res": {k: gen.get(k) for k in ("generated", "rotated", "hint")}})

st, status = req("GET", AUTH + "/me/mcp-token", token=A["token"])
check("状态视图只返回提示 / 时间，不返回明文",
      st == 200 and status.get("hasToken") is True and status.get("active") is True
      and bool(status.get("hint")) and "token" not in status and "tokenHash" not in status, status)

# ---- B. token works, but only inside its own account ------------------------
st, listed = req("GET", API + "/spaces", token=tok1)
check("MCP token 只看到本账号名下空间",
      st == 200 and [i.get("slug") for i in listed.get("items", [])] == ["alice-" + SUFFIX],
      listed.get("items"))
st, r = req("GET", API + "/spaces/" + SB + "/documents", token=tok1)
check("MCP token 直读他人空间被 403 ACCOUNT_FORBIDDEN",
      st == 403 and code_of(r) == "ACCOUNT_FORBIDDEN", {"status": st, "res": r})
st, r = mcp("tools/call", {
    "spaceId": SB, "name": "episteme.create_document",
    "arguments": {"nodePath": "/代码项目/桌面端", "title": "越权", "fields": {"title": "越权"}}
}, tok1)
check("MCP token 写他人空间被 ACCOUNT_FORBIDDEN",
      code_of(r) == "ACCOUNT_FORBIDDEN", {"status": st, "res": r})
st, r = mcp("tools/list", {}, tok1)
check("MCP token 可正常 tools/list",
      st == 200 and len((r.get("result") or {}).get("tools") or []) > 0, r)

st, r = write_doc(SA, tok1, "令牌写入-1")
check("MCP token 写自己空间成功", bool(sc_of(r).get("docId")), r)
st, r = req("GET", API + "/spaces/" + SA + "/documents", token=tok1)
check("自己空间的文档可被令牌读回", st == 200 and (r.get("total") or 0) >= 1, r)

st, r = req("POST", "/mcp", {"jsonrpc": "2.0", "id": 3, "method": "tools/list", "params": {}},
            None, {"X-Episteme-Mcp-Token": tok1})
check("X-Episteme-Mcp-Token 头同样可用",
      st == 200 and len((r.get("result") or {}).get("tools") or []) > 0, r)

# ---- C. token cannot escalate ----------------------------------------------
st, r = req("GET", ADMIN + "/users", token=tok1)
check("MCP token 不能进管理后台", st == 403 and code_of(r) == "ADMIN_REQUIRED", r)
st, r = req("POST", AUTH + "/me/mcp-token", {"action": "reset"}, tok1)
check("MCP token 不能重置自己", st == 403 and code_of(r) == "MCP_TOKEN_SESSION_REQUIRED", r)
st, r = req("DELETE", AUTH + "/me/mcp-token", token=tok1)
check("MCP token 不能停用自己", st == 403 and code_of(r) == "MCP_TOKEN_SESSION_REQUIRED", r)
st, r = req("POST", AUTH + "/password",
            {"oldPassword": "Tok-2026!pass", "newPassword": "Nope-2026!pass"}, tok1)
check("MCP token 不能改账号密码", st == 403, r)
st, r = req("GET", AUTH + "/me", token=tok1)
check("/auth/me 标记凭据通道为 mcp-token",
      st == 200 and r.get("authChannel") == "mcp-token"
      and r.get("authorizationModel") == "account", r)

st, r = req("PUT", ADMIN + "/members", {"userId": A["id"], "spaceId": SB, "role": "owner"}, admin_t)
check("旧空间授权矩阵写入被明确拒绝 GRANT_MODEL_REMOVED",
      st == 409 and code_of(r) == "GRANT_MODEL_REMOVED", {"status": st, "res": r})
st, r = req("PUT", ADMIN + "/members",
            {"userId": A["id"], "spaceId": SB, "role": "owner"}, A["token"])
check("普通账号调管理端点仍被 ADMIN_REQUIRED 拒绝",
      st == 403 and code_of(r) == "ADMIN_REQUIRED", {"status": st, "res": r})

# ---- D. reset invalidates old token immediately -----------------------------
st, gen2 = req("POST", AUTH + "/me/mcp-token", {"action": "reset"}, A["token"])
tok2 = gen2.get("token", "")
check("重置返回新明文且标记 rotated=true",
      st == 200 and gen2.get("rotated") is True and tok2.startswith("epm_")
      and tok2 != tok1, {"status": st, "res": {k: gen2.get(k) for k in ("rotated", "hint")}})
st, r = req("GET", API + "/spaces", token=tok1)
check("旧令牌重置后立即 401 MCP_TOKEN_INVALID",
      st == 401 and code_of(r) == "MCP_TOKEN_INVALID", {"status": st, "res": r})
st, r = write_doc(SA, tok2, "令牌写入-2")
check("新令牌可继续写自己空间", bool(sc_of(r).get("docId")), r)

# ---- E. independent tokens per account -------------------------------------
st, genb = req("POST", AUTH + "/me/mcp-token", {"action": "generate"}, B["token"])
tokb = genb.get("token", "")
check("bob 也能生成自己独立的令牌", st == 200 and tokb.startswith("epm_") and tokb != tok2, genb.get("rotated"))
st, r = req("GET", API + "/spaces/" + SA + "/documents", token=tokb)
check("bob 令牌读 alice 空间被拒",
      st == 403 and code_of(r) == "ACCOUNT_FORBIDDEN", {"status": st, "res": r})
st, r = req("GET", API + "/spaces/" + SB + "/documents", token=tokb)
check("bob 令牌可读自己空间", st == 200 and r.get("total") == 0, r)

# ---- F. revoke + re-generate ------------------------------------------------
st, r = req("DELETE", AUTH + "/me/mcp-token", token=B["token"])
check("登录会话可停用令牌", st == 200 and r.get("revoked") is True, r)
st, r = req("GET", API + "/spaces", token=tokb)
check("停用后令牌立即失效", st == 401, {"status": st, "res": r})
st, status = req("GET", AUTH + "/me/mcp-token", token=B["token"])
check("状态显示已停用", st == 200 and status.get("revoked") is True, status)
st, genb2 = req("POST", AUTH + "/me/mcp-token", {"action": "generate"}, B["token"])
tokb2 = genb2.get("token", "")
check("停用后可重新生成并立即生效",
      st == 200 and tokb2.startswith("epm_")
      and req("GET", API + "/spaces", token=tokb2)[0] == 200, genb2.get("rotated"))

# ---- G. ownership transfer + HMAC channel -----------------------------------
st, r = req("PUT", ADMIN + "/spaces/" + SB + "/owner", {"userId": A["id"]}, admin_t)
check("管理员可转让空间归属", st == 200 and r.get("transferred") is True, r)
st, r = req("GET", API + "/spaces/" + SB + "/documents", token=B["token"])
check("转让后原账号立即失去该空间权限",
      st == 403 and code_of(r) == "ACCOUNT_FORBIDDEN", {"status": st, "res": r})
st, r = req("GET", API + "/spaces/" + SB + "/documents", token=A["token"])
check("新归属账号可访问该空间", st == 200, {"status": st, "res": r})

if MACHINE_SECRET:
    path = API + "/spaces/" + SA + "/nodes"
    rel = path[len(BASE):]
    body = {"path": "/MACHINE-" + SUFFIX, "name": "MACHINE"}
    st, r = req("POST", rel, body, None, hmac_headers("POST", rel, body, MACHINE_SECRET))
    check("无账号身份的 HMAC 通道默认停用 MACHINE_CHANNEL_DISABLED",
          st == 401 and code_of(r) == "MACHINE_CHANNEL_DISABLED", {"status": st, "res": r})
else:
    print("  SKIP  HMAC 通道用例（未提供 EPISTEME_MACHINE_SECRET）")

# ---- H. landing zones -------------------------------------------------------
st, r = req("GET", API + "/spaces/" + SB + "/documents", token=admin_t)
check("bob 原空间始终 0 篇文档（无越权落地）", st == 200 and r.get("total") == 0, r)
st, r = req("GET", API + "/spaces/" + SA + "/documents", token=admin_t)
check("alice 的两次写入都在自己空间", st == 200 and (r.get("total") or 0) >= 2, r)
st, r = req("GET", ADMIN + "/overview", token=admin_t)
check("后台总览报告账号授权模型与 MCP 令牌数",
      st == 200 and r.get("authorizationModel") == "account"
      and (r.get("mcpTokens") or 0) >= 2, r)

print("=" * 92)
print("mcp_token_smoke: %d passed / %d failed" % (len(PASS), len(FAIL)))
if FAIL:
    for f in FAIL:
        print("  FAILED: " + f)
sys.exit(1 if FAIL else 0)
