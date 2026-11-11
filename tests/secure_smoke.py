#!/usr/bin/env python3
"""Episteme v0.5 secure-mode regression.

Run against a *separate* instance started with:
  EPISTEME_AUTH_MODE=secure EPISTEME_AUTH_SECRET=<secret> EPISTEME_RATE_LIMIT=<n>
The default instance stays in legacy mode, so this file is an opt-in extra suite.
"""
import base64
import hashlib
import hmac
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = os.environ.get("EPISTEME_TEST_BASE", "http://127.0.0.1:5081")
SECRET = os.environ.get("EPISTEME_AUTH_SECRET", "episteme-secure-test-secret")
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
        print("  FAIL  " + name + " :: " + str(detail)[:300])


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def make_jwt(role="owner", ttl=600, secret=SECRET, tamper=False):
    now = int(time.time())
    head = b64url(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    payload = b64url(json.dumps(
        {"sub": "secure-test", "role": role, "iat": now, "exp": now + ttl},
        separators=(",", ":")).encode())
    signing = head + "." + payload
    sig = hmac.new(secret.encode(), signing.encode(), hashlib.sha256).digest()
    if tamper:
        sig = bytes([sig[0] ^ 0xFF]) + sig[1:]
    return signing + "." + b64url(sig)


def request(method, path, body=None, headers=None):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method,
                                 headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with OPENER.open(req, timeout=10) as r:
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


def signed_headers(method, path, body, role="owner", ttl=0, nonce=None, secret=SECRET):
    ts = str(int(time.time()) - ttl)
    nonce = nonce or ("n" + str(time.time_ns()))
    raw = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else b""
    body_hash = hashlib.sha256(raw).hexdigest()
    canonical = "\n".join([method, path, "", ts, nonce, role, body_hash])
    sig = hmac.new(secret.encode(), canonical.encode(), hashlib.sha256).hexdigest()
    return {
        "X-Episteme-Key": "desktop-1",
        "X-Episteme-Timestamp": ts,
        "X-Episteme-Nonce": nonce,
        "X-Episteme-Signature": sig,
        "X-Episteme-Role": role
    }, raw


print("== Episteme v0.5 secure-mode 回归 (%s) ==" % BASE)

code, health = request("GET", "/health")
check("health 在 secure 模式仍公开", code == 200 and health.get("authMode") == "secure", health)
code, caps = request("GET", "/api/v1/episteme/capabilities")
check("capabilities 在 secure 模式仍公开",
      code == 200 and caps.get("security", {}).get("active") is True, caps)

code, denied = request("GET", "/api/v1/episteme/spaces")
check("无凭证访问 API 返回 401", code == 401 and denied.get("code") == "UNAUTHORIZED", denied)

code, bad = request("GET", "/api/v1/episteme/spaces",
                    headers={"Authorization": "Bearer " + make_jwt(tamper=True)})
check("篡改签名的 JWT 被拒绝", code == 401 and bad.get("code") == "JWT_SIGNATURE_INVALID", bad)

code, expired = request("GET", "/api/v1/episteme/spaces",
                        headers={"Authorization": "Bearer " + make_jwt(ttl=-60)})
check("过期 JWT 被拒绝", code == 401 and expired.get("code") == "JWT_EXPIRED", expired)

code, ok = request("GET", "/api/v1/episteme/spaces",
                   headers={"Authorization": "Bearer " + make_jwt("owner")})
check("合法 JWT 可读取空间", code == 200 and "items" in ok, ok)

code, forbidden = request("POST", "/api/v1/episteme/spaces",
                          {"name": "viewer 不应能创建", "slug": "viewer-denied"},
                          headers={"Authorization": "Bearer " + make_jwt("viewer")})
check("JWT 角色透传并由 RequireRole 拦截",
      code == 403 and forbidden.get("code") == "FORBIDDEN_SCOPE", forbidden)

body = {"name": "HMAC 空间", "slug": "hmac-" + str(int(time.time())), "source": "api"}
sig_headers, raw = signed_headers("POST", "/api/v1/episteme/spaces", body)
code, signed = request("POST", "/api/v1/episteme/spaces", body, headers=sig_headers)
check("HMAC-SHA256 签名请求可写入", code == 200 and bool(signed.get("id")), signed)

sig_headers2, _ = signed_headers("POST", "/api/v1/episteme/spaces", body,
                                 nonce="replay-fixed-nonce")
code, first = request("POST", "/api/v1/episteme/spaces", body, headers=sig_headers2)
sig_headers3, _ = signed_headers("POST", "/api/v1/episteme/spaces", body,
                                 nonce="replay-fixed-nonce")
code2, replay = request("POST", "/api/v1/episteme/spaces", body, headers=sig_headers3)
check("同一 Nonce 重放被 409 拒绝",
      code2 == 409 and replay.get("code") == "NONCE_REPLAYED",
      {"first": code, "second": code2, "body": replay})

old_headers, _ = signed_headers("GET", "/api/v1/episteme/spaces", None, ttl=3600,
                                nonce="expired-ts")
code, old = request("GET", "/api/v1/episteme/spaces", headers=old_headers)
check("超出时间偏差的请求被拒绝", code == 401 and old.get("code") == "SIGNATURE_EXPIRED", old)

tampered_headers, _ = signed_headers("GET", "/api/v1/episteme/spaces", None)
tampered_headers["X-Episteme-Signature"] = "00" * 32
code, tampered = request("GET", "/api/v1/episteme/spaces", headers=tampered_headers)
check("非法签名被拒绝", code == 401 and tampered.get("code") == "SIGNATURE_INVALID", tampered)

limit = int(os.environ.get("EPISTEME_TEST_RATE_LIMIT", "0"))
if limit > 0:
    statuses = []
    for i in range(limit + 4):
        c, _ = request("GET", "/api/v1/episteme/spaces",
                       headers={"Authorization": "Bearer " + make_jwt("owner")})
        statuses.append(c)
    check("固定窗口限流在超阈值后返回 429", 429 in statuses, statuses)
else:
    check("固定窗口限流已由 capabilities 声明（专项实例未设置阈值）",
          caps.get("security", {}).get("rateLimitPerMinute", 0) > 0,
          caps.get("security"))

print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
sys.exit(1 if FAIL else 0)
