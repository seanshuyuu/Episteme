#!/usr/bin/env python3
"""Episteme MCP stdio transport regression (no third-party dependencies)."""
import json
import os
import shlex
import subprocess
import sys

DLL = os.environ.get(
    "EPISTEME_STDIO_DLL",
    "src/Episteme.McpStdio/bin/Release/net10.0/Episteme.McpStdio.dll")
BASE = os.environ.get("EPISTEME_MCP_HTTP_URL", "http://127.0.0.1:5080/mcp")

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


requests = [
    {"jsonrpc": "2.0", "id": 1, "method": "initialize",
     "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                "clientInfo": {"name": "stdio-smoke", "version": "1.0"}}},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
    {"jsonrpc": "2.0", "id": 3, "method": "tools/call",
     "params": {"name": "episteme.capabilities", "arguments": {}}}
]
payload = "\n".join(json.dumps(r, ensure_ascii=False) for r in requests) + "\n"
env = os.environ.copy()
env["EPISTEME_MCP_HTTP_URL"] = BASE
env["EPISTEME_MCP_ROLE"] = "owner"

print("== Episteme MCP stdio 传输回归 ==")
print("DLL=" + DLL)
try:
    proc = subprocess.run(shlex.split(os.environ.get("EPISTEME_DOTNET_CMD", "dotnet")) + [DLL],
                          input=payload, capture_output=True,
                          text=True, env=env, timeout=120)
except Exception as ex:
    check("stdio 进程可启动", False, ex)
    print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
    sys.exit(1)

lines = [line for line in proc.stdout.splitlines() if line.strip()]
check("stdio 进程正常退出", proc.returncode == 0,
      {"code": proc.returncode, "stderr": proc.stderr[:200]})
check("三行请求各返回一行 JSON-RPC 响应", len(lines) == 3, lines[:3])

parsed = []
for line in lines:
    try:
        parsed.append(json.loads(line))
    except Exception as ex:
        parsed.append({"__error__": str(ex)})
check("stdout 全部为合法 JSON 且无构建日志污染",
      all("__error__" not in p and p.get("jsonrpc") == "2.0" for p in parsed), parsed)
check("initialize 返回协议版本",
      len(parsed) > 0 and parsed[0].get("result", {}).get("protocolVersion") == "2025-06-18",
      parsed[0] if parsed else None)
check("tools/list 返回 30 个静态工具",
      len(parsed) > 1 and len(parsed[1].get("result", {}).get("tools", [])) >= 30,
      len(parsed[1].get("result", {}).get("tools", [])) if len(parsed) > 1 else None)
check("tools/call capabilities 走同一 Dispatch",
      len(parsed) > 2 and parsed[2].get("result", {})
      .get("structuredContent", {}).get("version", "").startswith("0.5"),
      parsed[2] if len(parsed) > 2 else None)
check("诊断信息只写 stderr", "[episteme-mcp-stdio]" not in proc.stdout, proc.stdout[:120])

print("\n== 结果: %d PASS / %d FAIL ==" % (PASS, FAIL))
sys.exit(1 if FAIL else 0)
