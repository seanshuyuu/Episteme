#!/usr/bin/env python3
"""Episteme users 模式端到端回归：账号体系 + MCP 代码工作空间 + git 归档预览。

Prerequisites: an instance started with EPISTEME_AUTH_MODE=users and a configured
EPISTEME_AUTH_SECRET / EPISTEME_ADMIN_PASSWORD. It is intentionally stateful: the
MCP-created workspace and the synced commits are the deliverable being verified.

  EPISTEME_TEST_BASE=http://127.0.0.1:5080 \
  EPISTEME_ADMIN_USER=admin EPISTEME_ADMIN_PASSWORD=<password> \
  python3 tests/auth_smoke.py

No third-party dependencies. Zero assertions about legacy/secure behaviour here —
those remain covered by api_smoke.py / secure_smoke.py under their own modes.
"""
import json
import os
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = os.environ.get("EPISTEME_TEST_BASE", "http://127.0.0.1:5080").rstrip("/")
API = BASE + "/api/v1/episteme"
AUTH = BASE + "/api/v1/auth"
ADMIN = BASE + "/api/v1/admin"
ADMIN_USER = os.environ.get("EPISTEME_ADMIN_USER", "admin")
ADMIN_PASSWORD = os.environ.get("EPISTEME_ADMIN_PASSWORD", "")
LOGIN_LIMIT = int(os.environ.get("EPISTEME_LOGIN_RATE_LIMIT", "30"))
PY = sys.executable
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

PASS = 0
FAIL = 0


def check(name, ok, detail=None):
    global PASS, FAIL
    if ok:
        PASS += 1
        print("  PASS  " + name)
    else:
        FAIL += 1
        text = "" if detail is None else json.dumps(detail, ensure_ascii=False)[:300]
        print("  FAIL  " + name + " :: " + text)


def http(method, url, body=None, token=None, headers=None, timeout=30):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    hdr = {"Content-Type": "application/json"}
    if token:
        hdr["Authorization"] = "Bearer " + token
    if headers:
        hdr.update(headers)
    req = urllib.request.Request(url, data=data, headers=hdr, method=method)
    try:
        with OPENER.open(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", "replace") or "{}"
            try:
                return resp.status, json.loads(raw)
            except Exception:
                return resp.status, {"raw": raw[:300]}
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace") or "{}"
        try:
            return exc.code, json.loads(raw)
        except Exception:
            return exc.code, {"raw": raw[:300]}
    except Exception as exc:
        return -1, {"message": str(exc)}


def text(url):
    with OPENER.open(urllib.request.Request(url), timeout=30) as resp:
        return resp.status, resp.read().decode("utf-8", "replace")


def mcp(method, params, token, space=None):
    url = BASE + "/mcp" + (("?space=" + space) if space else "")
    body = {"jsonrpc": "2.0", "id": 1, "method": method}
    if params is not None:
        body["params"] = params
    return http("POST", url, body, token=token,
                headers={"MCP-Protocol-Version": "2025-06-18"})


def structured(rpc):
    result = rpc.get("result") or {}
    return result.get("structuredContent") or result


def tree_paths(tree):
    return {i.get("path"): i.get("id") for i in (tree.get("items") or [])}


def run_sync(repo, space_id, app_type, project, auth_args):
    cmd = [PY, str(ROOT / "tools" / "git-sync.py"),
           "--mode", "head", "--api", BASE, "--space", space_id,
           "--repo", repo, "--app-type", app_type,
           "--project-name", project, "--install-pack"] + auth_args
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    except Exception as exc:
        return -1, [], str(exc)
    events = []
    for line in proc.stdout.splitlines():
        if line.startswith("{"):
            try:
                events.append(json.loads(line))
            except Exception:
                pass
    detail = proc.stdout[-300:] + " | " + proc.stderr[-300:]
    return proc.returncode, events, detail


print("== Episteme users 模式 / MCP 代码工作空间 回归 ==")
print("BASE=" + BASE)
if not ADMIN_PASSWORD:
    print("需要 EPISTEME_ADMIN_PASSWORD（users 模式种子管理员密码）")
    sys.exit(2)

stamp = uuid.uuid4().hex[:8]

# ---------------------------------------------------------------- A. 认证基础
code, health = http("GET", BASE + "/health")
check("健康检查报告 users 模式",
      code == 200 and health.get("status") == "ok" and health.get("authMode") == "users",
      health)

code, cfg = http("GET", AUTH + "/config")
check("auth/config 声明账号体系与自助注册",
      code == 200 and cfg.get("mode") == "users" and cfg.get("accountsEnabled") is True
      and cfg.get("registrationEnabled") is True, cfg)

code, caps = http("GET", API + "/capabilities")
sec = caps.get("security") or {}
check("capabilities 公开且声明按账号授权/MCP 令牌/管理后台",
      code == 200 and caps.get("auth", {}).get("mode") == "users"
      and sec.get("adminConsole") is True and sec.get("authorizationModel") == "account"
      and sec.get("accountScoped") is True and sec.get("perAccountMcpTokens") is True
      and sec.get("perSpaceGrants") is False,
      {"auth": caps.get("auth"), "security": sec})

code, res = http("GET", API + "/spaces")
check("未登录访问空间列表被 401 拒绝",
      code == 401 and res.get("code") == "UNAUTHORIZED", res)
code, res = http("GET", API + "/spaces", headers={"X-Episteme-Role": "owner"})
check("伪造 X-Episteme-Role 头不能绕过登录", code == 401, res)
code, res = http("GET", API + "/capabilities")
check("capabilities 无需登录即可读取", code == 200, res)

code, adm = http("POST", AUTH + "/login",
                 {"userName": ADMIN_USER, "password": ADMIN_PASSWORD})
admin_token = adm.get("token", "")
admin_id = (adm.get("user") or {}).get("id", "")
check("种子管理员登录成功并签发 JWT",
      code == 200 and len(admin_token) > 40
      and (adm.get("user") or {}).get("globalRole") == "admin", adm)
if not admin_token:
    print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
    sys.exit(1)

code, res = http("POST", AUTH + "/login",
                 {"userName": ADMIN_USER, "password": "definitely-wrong"})
check("错误密码返回 401 INVALID_CREDENTIALS",
      code == 401 and res.get("code") == "INVALID_CREDENTIALS", res)

user_name = "dev" + stamp
user_password = "Dev-Pass-2026!"
code, res = http("POST", AUTH + "/register",
                 {"userName": "weak" + stamp, "password": "123"})
check("弱密码注册被拒绝", code == 400 and res.get("code") == "WEAK_PASSWORD", res)
code, reg = http("POST", AUTH + "/register",
                 {"userName": user_name, "password": user_password,
                  "displayName": "普通开发者", "email": "dev@episteme.local"})
user_token = reg.get("token", "")
user_id = (reg.get("user") or {}).get("id", "")
check("普通用户自助注册并自动登录（默认 editor）",
      code == 200 and bool(user_token)
      and (reg.get("user") or {}).get("globalRole") == "editor", reg)
code, res = http("POST", AUTH + "/register",
                 {"userName": user_name, "password": user_password})
check("重复用户名返回 409 USER_EXISTS",
      code == 409 and res.get("code") == "USER_EXISTS", res)

code, me = http("GET", AUTH + "/me", token=user_token)
check("普通用户 /auth/me 身份正确且非管理员",
      code == 200 and me.get("isAdmin") is False
      and (me.get("user") or {}).get("globalRole") == "editor", me)
code, me_admin = http("GET", AUTH + "/me", token=admin_token)
check("管理员 /auth/me 标记 isAdmin", code == 200 and me_admin.get("isAdmin") is True, me_admin)

# ---------------------------------------------------------------- B. 角色与授权
code, res = http("GET", ADMIN + "/users", token=user_token)
check("普通用户访问管理后台被 403 拒绝",
      code == 403 and res.get("code") == "ADMIN_REQUIRED", res)
code, users = http("GET", ADMIN + "/users", token=admin_token)
check("管理员可列出账号（含新注册用户）",
      code == 200 and users.get("total", 0) >= 2
      and any(u.get("userName") == user_name for u in users.get("items", [])), users.get("total"))
code, overview = http("GET", ADMIN + "/overview", token=admin_token)
check("管理总览返回账号/空间/授权统计",
      code == 200 and overview.get("users", 0) >= 2
      and overview.get("authMode") == "users", overview)

# MCP endpoints only accept epm_ tokens; login JWTs are valid on /api only.
code, adm_gen = http("POST", AUTH + "/me/mcp-token", {"action": "generate"}, admin_token)
admin_mcp = adm_gen.get("token", "")
check("管理员可生成自己的长期 MCP token",
      code == 200 and admin_mcp.startswith("epm_"), adm_gen.get("rotated"))
code, usr_gen = http("POST", AUTH + "/me/mcp-token", {"action": "generate"}, user_token)
user_mcp = usr_gen.get("token", "")
check("普通用户可生成自己的长期 MCP token",
      code == 200 and user_mcp.startswith("epm_"), usr_gen.get("rotated"))

# ---------------------------------------------------------------- C. MCP 创建代码工作空间
code, packs = http("GET", API + "/packs", token=admin_token)
pack_items = packs.get("items", packs.get("packs", []))
check("内置 Pack 目录列出 4 套（含 code-workspace）",
      code == 200 and len(pack_items) == 4
      and any(p.get("id") == "pack_code-workspace" for p in pack_items),
      [p.get("id") for p in pack_items])

code, rpc = mcp("tools/list", {}, admin_mcp)
tools = (rpc.get("result") or {}).get("tools") or []
tool_names = [t.get("name") for t in tools]
check("空库 tools/list 暴露 create_space 引导工具",
      code == 200 and "episteme.create_space" in tool_names,
      {"total": len(tools), "bootstrap": (rpc.get("result") or {}).get("bootstrap")})
code, bootstrap = http("GET", API + "/mcp/tools", token=admin_token)
check("REST /mcp/tools 在无空间时同样返回引导工具",
      code == 200 and "episteme.create_space" in
      [t.get("name") for t in bootstrap.get("tools", [])], bootstrap.get("total"))

slug = "personal-code-" + stamp
code, rpc = mcp("tools/call", {
    "name": "episteme.create_space",
    "arguments": {
        "name": "个人代码管理工作空间", "slug": slug,
        "description": "桌面端 / 移动端 / 跨平台 / 前后端分离项目归档与代码在线预览",
        "packId": "pack_code-workspace", "includeSamples": True
    }}, admin_mcp)
created = structured(rpc)
space_id = created.get("id", "")
check("MCP create_space 一次创建并安装代码工作空间模板",
      code == 200 and bool(space_id) and created.get("createdBy") == "mcp"
      and (created.get("pack") or {}).get("installed") is True, created)
if not space_id:
    print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
    sys.exit(1)

code, tree = http("GET", API + "/spaces/" + space_id + "/tree", token=admin_token)
paths = tree_paths(tree)
project_nodes = ["/代码项目/桌面端", "/代码项目/移动端", "/代码项目/跨平台", "/代码项目/前后端分离"]
archive_nodes = ["/Git 提交/桌面端", "/Git 提交/移动端", "/Git 提交/跨平台", "/Git 提交/前后端分离"]
check("四类软件应用目录齐备", code == 200 and all(p in paths for p in project_nodes),
      sorted(paths.keys()))
check("四类 git 提交归档目录 + 在线预览节点齐备",
      all(p in paths for p in archive_nodes) and "/在线预览" in paths,
      sorted(paths.keys()))

code, cts = http("GET", API + "/spaces/" + space_id + "/content-types", token=admin_token)
ct_slugs = [c.get("slug") for c in cts.get("items", [])]
check("模板声明 3 类内容类型",
      code == 200 and all(s in ct_slugs for s in
                          ["software_project", "git_commit", "code_snapshot"]), ct_slugs)

code, docs = http("GET", API + "/spaces/" + space_id + "/documents", token=admin_token)
check("模板导入 2 条示例项目文档",
      code == 200 and docs.get("total", 0) >= 2, docs.get("total"))

# Mixed Pack scenario: git-ingest and code-workspace share git_commit + /Git 提交.
code, mixed = http("POST", API + "/spaces/" + space_id + "/packs/install",
                   {"packId": "pack_git-ingest", "overwrite": True}, admin_token)
check("同一空间可叠加 git-ingest 与 code-workspace 而不冲突",
      code == 200 and "PACK_CONFLICT" not in json.dumps(mixed, ensure_ascii=False), mixed)
code, mixed2 = http("POST", API + "/spaces/" + space_id + "/packs/install",
                    {"packId": "pack_code-workspace", "overwrite": True}, admin_token)
check("再次安装 code-workspace 模板仍成功", code == 200, mixed2)

code, rpc = mcp("tools/list", {"spaceId": space_id}, admin_mcp)
result2 = rpc.get("result") or {}
names2 = [t.get("name") for t in (result2.get("tools") or [])]
check("安装模板后动态派生 query/create 工具",
      code == 200 and (result2.get("dynamicToolCount") or 0) > 0
      and any((n or "").startswith("episteme.query_") for n in names2)
      and any((n or "").startswith("episteme.create_") and n != "episteme.create_space"
              for n in names2)
      and "episteme.create_space" in names2,
      {"total": len(names2), "dynamic": result2.get("dynamicToolCount")})

# Account ownership boundary: admin's space is invisible to a different account.
code, visible = http("GET", API + "/spaces", token=user_token)
visible_ids = [s.get("id") for s in visible.get("items", [])]
check("普通用户看不到别人名下的空间", space_id not in visible_ids, visible_ids)
code, res = http("GET", API + "/spaces/" + space_id + "/tree", token=user_token)
check("普通用户直读他人空间被 403 ACCOUNT_FORBIDDEN",
      code == 403 and res.get("code") == "ACCOUNT_FORBIDDEN", res)
code, res = mcp("tools/call", {"spaceId": space_id, "name": "episteme.tree",
                               "arguments": {}}, user_mcp)
check("MCP 携带他人 spaceId 同样被 403", code == 403, res)

code, res = http("PUT", ADMIN + "/members",
                 {"userId": user_id, "spaceId": space_id, "role": "viewer"}, admin_token)
check("旧空间授权矩阵已停用 GRANT_MODEL_REMOVED",
      code == 409 and res.get("code") == "GRANT_MODEL_REMOVED", res)
code, owners = http("GET", ADMIN + "/owners", token=admin_token)
check("空间归属表可查询且该空间归属管理员",
      code == 200 and any(o.get("spaceId") == space_id and o.get("userId") == admin_id
                          for o in owners.get("items", [])), owners.get("items"))

# Any authenticated account may create its own space through MCP.
code, rpc = mcp("tools/call", {
    "name": "episteme.create_space",
    "arguments": {"name": "我的私人空间", "slug": "mine-" + stamp}
}, user_mcp)
own = structured(rpc)
own_id = own.get("id", "")
check("普通用户可用 MCP 创建自己的空间", code == 200 and bool(own_id), own)
code, res = http("GET", API + "/spaces/" + own_id + "/tree", token=user_token)
check("空间创建者自动成为归属账号", code == 200, res)
code, res = http("GET", API + "/spaces/" + own_id + "/tree", token=admin_token)
check("管理员仍可查看所有账号的空间", code == 200, res)
# Global role is effective inside the account's own spaces.
project_node_id = paths.get("/代码项目/桌面端") or ""
write_doc = {"title": "归属写入验证",
             "fields": {"title": "归属写入验证", "appType": "桌面端"}}
code, res = http("POST", API + "/spaces/" + space_id + "/nodes/" +
                 project_node_id + "/documents", write_doc, user_token)
check("普通用户不能向他人空间写文档", code == 403, res)

# ---- per-account MCP token lifecycle (MCP tokens only work on /mcp) ----
code, st0 = http("GET", AUTH + "/me/mcp-token", token=user_token)
check("普通用户可查看自己的 MCP token 状态（无明文）",
      code == 200 and st0.get("hasToken") is True and "token" not in st0, st0)
code, res = http("GET", API + "/spaces", token=user_mcp)
check("MCP token 不能调用普通 /api 端点",
      code == 403 and res.get("code") == "CREDENTIAL_SCOPE_MISMATCH", res)
code, res = mcp("tools/list", {}, user_mcp)
check("MCP token 可访问 /mcp 工具清单",
      code == 200 and bool((res.get("result") or {}).get("tools")), res.get("code"))
code, res = http("POST", AUTH + "/me/mcp-token", {"action": "reset"}, user_mcp)
check("MCP token 不能重置自己", code == 403 and
      res.get("code") == "CREDENTIAL_SCOPE_MISMATCH", res)
code, gen2 = http("POST", AUTH + "/me/mcp-token", {"action": "reset"}, user_token)
user_mcp2 = gen2.get("token", "")
check("登录会话可重置 MCP token 且返回新明文",
      code == 200 and gen2.get("rotated") is True and user_mcp2 != user_mcp
      and user_mcp2.startswith("epm_"), gen2.get("rotated"))
code, res = mcp("tools/list", {}, user_mcp)
check("重置后旧 token 立即 401 MCP_TOKEN_INVALID",
      code == 401 and res.get("code") == "MCP_TOKEN_INVALID", res)
code, res = mcp("tools/list", {}, user_mcp2)
check("新 token 立即可用", code == 200, res)
code, res = http("DELETE", AUTH + "/me/mcp-token", {}, user_token)
check("登录会话可停用 MCP token", code == 200 and res.get("revoked") is True, res)
code, res = mcp("tools/list", {}, user_mcp2)
check("停用后 token 立即失效", code == 401 and res.get("code") == "MCP_TOKEN_INVALID", res)

# ---------------------------------------------------------------- D. 管理员后台
qa_name = "qa" + stamp
code, created_user = http("POST", ADMIN + "/users",
                          {"userName": qa_name, "password": "Qa-Pass-2026!",
                           "globalRole": "viewer", "spaceId": own_id}, admin_token)
qa_id = ((created_user.get("user") or {}).get("id", ""))
check("管理员创建用户并一步转让空间归属",
      code == 200 and bool(qa_id)
      and (created_user.get("ownershipTransferred") or {}).get("spaceId") == own_id,
      created_user)
code, res = http("GET", API + "/spaces/" + own_id + "/tree", token=user_token)
check("归属转让后原账号立即失去该空间权限",
      code == 403 and res.get("code") == "ACCOUNT_FORBIDDEN", res)
code, res = http("PATCH", ADMIN + "/users/" + qa_id,
                 {"globalRole": "agent", "isActive": False}, admin_token)
check("管理员可改全局角色并停用账号",
      code == 200 and (res.get("user") or {}).get("globalRole") == "agent", res)
code, res = http("POST", AUTH + "/login", {"userName": qa_name, "password": "Qa-Pass-2026!"})
check("停用账号无法登录", code == 401 and res.get("code") == "USER_DISABLED", res)
code, res = http("PATCH", ADMIN + "/users/" + qa_id,
                 {"isActive": True, "password": "New-Pass-2026!"}, admin_token)
code, res2 = http("POST", AUTH + "/login", {"userName": qa_name, "password": "New-Pass-2026!"})
check("管理员重置密码后新密码可登录", code == 200 and res2.get("code") is None, res2)
code, res = http("DELETE", ADMIN + "/users/" + qa_id, token=admin_token)
check("管理员删除用户并清理其授权",
      code == 200 and res.get("grantsRemoved") is True, res)
code, res = http("DELETE", ADMIN + "/users/" + admin_id, token=admin_token)
check("管理员不能删除自己",
      code == 400 and res.get("code") == "SELF_DELETE_FORBIDDEN", res)

new_password = user_password + "X"
code, res = http("POST", AUTH + "/password",
                 {"oldPassword": user_password, "newPassword": new_password}, user_token)
check("普通用户可自助修改密码", code == 200 and res.get("changed") is True, res)
code, res = http("POST", AUTH + "/login",
                 {"userName": user_name, "password": new_password})
check("改密后新密码可登录", code == 200 and bool(res.get("token")), res.get("code"))
code, res = http("POST", AUTH + "/logout", {}, token=user_token)
check("登出端点返回无状态令牌说明", code == 200 and res.get("loggedOut") is True, res)

brute = "brute" + stamp
codes = []
for _ in range(LOGIN_LIMIT + 1):
    c, _ = http("POST", AUTH + "/login", {"userName": brute, "password": "nope"})
    codes.append(c)
check("登录限流在阈值后返回 429 LOGIN_RATE_LIMITED",
      codes[-1] == 429 and codes[0] == 401,
      {"first": codes[0], "last": codes[-1], "limit": LOGIN_LIMIT})

# ---------------------------------------------------------------- E. git 同步与在线预览
# git-sync writes through /api, so every route uses a login JWT (--auth mcp is retired).
with tempfile.TemporaryDirectory(prefix="episteme-git-") as repo:
    def git(*args):
        return subprocess.run(["git", "-C", repo, *args],
                              capture_output=True, text=True, check=True)

    git("init", "-q")
    git("config", "user.name", "Episteme Test")
    git("config", "user.email", "dev@episteme.local")
    (Path(repo) / "src").mkdir()
    (Path(repo) / "web").mkdir()
    (Path(repo) / "src" / "main.py").write_text(
        "def hello(name):\n    return 'hello ' + name\n", encoding="utf-8")
    (Path(repo) / "web" / "index.ts").write_text(
        "export const render = (el: HTMLElement) => { el.textContent = 'ok'; };\n",
        encoding="utf-8")
    (Path(repo) / "README.md").write_text(
        "# 订单中台\n\n前后端分离示例项目。\n", encoding="utf-8")
    git("add", "-A")
    git("commit", "-q", "-m", "feat: 前后端分离示例提交")

    bearer = ["--auth", "bearer", "--token", admin_token]
    login = ["--auth", "login", "--user", ADMIN_USER, "--password", ADMIN_PASSWORD]
    routes = [
        ("desktop", "桌面端", "桌面客户端", bearer, "bearer"),
        ("mobile", "移动端", "移动 App", bearer, "bearer"),
        ("cross", "跨平台", "跨平台工具", login, "login"),
        ("fullstack", "前后端分离", "订单中台", bearer, "bearer"),
    ]
    synced = {}
    for app_type, label, project, auth_args, auth_label in routes:
        rc, events, detail = run_sync(repo, space_id, app_type, project, auth_args)
        events_synced = [e for e in events if e.get("event") == "synced"]
        synced[app_type] = events_synced[0] if events_synced else {}
        check("git 同步归档到%s（%s 认证）" % (label, auth_label),
              rc == 0 and bool(events_synced) and bool(synced[app_type].get("docId")),
              {"rc": rc, "detail": detail[:220]})

    for app_type, label, project, _, _ in routes:
        node_path = "/Git 提交/" + label
        doc_id = (synced.get(app_type) or {}).get("docId", "")
        if not doc_id:
            check("%s 文档字段与归档目录" % label, False, "无 docId")
            continue
        code, detail = http("GET", API + "/spaces/" + space_id + "/documents/" + doc_id,
                            token=admin_token)
        fields = detail.get("fields") or {}
        check("%s 提交文档写入 appType / projectName / gitSyncNode" % label,
              code == 200 and fields.get("appType") == label
              and fields.get("projectName") == project
              and fields.get("gitSyncNode") == node_path,
              {k: fields.get(k) for k in ("appType", "projectName", "gitSyncNode")})
        code, node_docs = http("GET", API + "/spaces/" + space_id + "/nodes/" +
                               (paths.get(node_path) or "") + "/documents", token=admin_token)
        check("%s 归档节点下可查到提交文档" % label,
              code == 200 and node_docs.get("total", 0) >= 1, node_docs.get("total"))

    doc_id = (synced.get("fullstack") or {}).get("docId", "")
    code, detail = http("GET", API + "/spaces/" + space_id + "/documents/" + doc_id,
                        token=admin_token)
    fields = detail.get("fields") or {}
    check("commit 携带提交标识/作者时间/说明变更/文件内容索引",
          code == 200 and all(k in fields for k in
                              ["gitSha", "gitShortSha", "gitRepo", "gitBranch",
                               "gitAuthorName", "gitAuthorEmail", "gitAuthorDate",
                               "gitMessage", "gitDiff", "gitChangedFiles",
                               "gitFileContents", "gitPartCount"]),
          sorted(fields.keys()))

    parts = detail.get("parts") or []
    metas = [(p.get("meta") or {}) for p in parts]
    check("代码 Part 携带真实文件名 / 路径 / 语言（文件树可渲染）",
          any(str(m.get("name", "")).endswith(".py") for m in metas)
          and any(str(m.get("gitPath", "")).startswith("src/") for m in metas)
          and any(m.get("language") in ("python", "typescript") for m in metas),
          [{k: m.get(k) for k in ("name", "gitPath", "language")} for m in metas[:4]])
    check("代码 Part 内容可在线预览（Monaco 高亮）",
          any("def hello" in (p.get("content") or "") for p in parts)
          and any(p.get("language") == "python" for p in parts),
          [(p.get("kind"), p.get("language")) for p in parts[:6]])

    rc, events, detail = run_sync(repo, space_id, "fullstack", "订单中台", bearer)
    replay = [e for e in events if e.get("event") == "synced"]
    check("重放同一次提交不产生重复文档",
          rc == 0 and bool(replay) and replay[0].get("idempotentReplay") is True,
          {"detail": detail[:220], "event": replay[:1]})

# ---------------------------------------------------------------- F. 静态交付面
for asset in ["/", "/index.html", "/auth.js", "/app.js", "/app.css",
              "/help-content.js", "/packs/code-workspace.json"]:
    code, _ = http("GET", BASE + asset)
    check("静态资源 %s 无需登录即可加载" % asset, code == 200, code)

_, html = text(BASE + "/")
check("SPA 外壳挂载 auth.js 与代码文件树容器",
      "auth.js" in html and "reader-file-tree" in html, html[:160])
_, auth_js = text(BASE + "/auth.js")
check("登录墙包含注册/登录/管理后台逻辑",
      "request(AUTH, '/register'" in auth_js
      and "request(ADMIN, '/users'" in auth_js
      and "function openAdmin" in auth_js
      and "AUTH + '/config'" in auth_js, len(auth_js))
check("auth.js 注入每账号 MCP token 面板与空间归属页签",
      "function openToken" in auth_js
      and "AUTH, '/me/mcp-token'" in auth_js
      and "data-auth-action=\"admin-ownership\"" in auth_js
      and "btn-token" in auth_js, len(auth_js))
_, app_css = text(BASE + "/app.css")
check("app.css 提供 MCP token 面板样式", ".token-dialog" in app_css, len(app_css))
_, app_js = text(BASE + "/app.js")
check("阅读器实现按路径分组的代码文件树",
      "renderFileTree" in app_js and "select-git-file" in app_js, len(app_js))

print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
print("交付空间: id=%s slug=%s" % (space_id, slug))
sys.exit(1 if FAIL else 0)
