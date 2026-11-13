#!/usr/bin/env python3
"""End-to-end regression for the git bypass sync (Pack + script + REST).

Prerequisites: Episteme api running (default http://127.0.0.1:5080) with the
git-ingest Pack in the image. Optional secure checks run when
EPISTEME_SECURE_BASE and EPISTEME_SECURE_SECRET are provided.
"""
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SYNC = ROOT / "tools" / "git-sync.py"
BASE = os.environ.get("EPISTEME_TEST_BASE", "http://127.0.0.1:5080")
SECURE_BASE = os.environ.get("EPISTEME_SECURE_BASE", "")
SECURE_SECRET = os.environ.get("EPISTEME_SECURE_SECRET", "")
API = "/api/v1/episteme"
PY = sys.executable

PASS = []
FAIL = []


def check(name, ok, detail=None):
    (PASS if ok else FAIL).append(name)
    mark = "PASS" if ok else "FAIL"
    text = "" if detail is None else "  " + json.dumps(detail, ensure_ascii=False)[:260]
    print(f"[{mark}] {name}{text}")


def http(method, path, body=None, base=BASE, headers=None):
    data = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
    hdr = {"X-Episteme-Role": "owner"}
    if body is not None:
        hdr["Content-Type"] = "application/json"
    if headers:
        hdr.update(headers)
    req = urllib.request.Request(base + path, data=data, headers=hdr, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(req, timeout=30) as resp:
            raw = resp.read().decode("utf-8", "replace")
            return resp.status, (json.loads(raw) if raw else {})
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace")
        try:
            return exc.code, json.loads(raw)
        except json.JSONDecodeError:
            return exc.code, {"raw": raw[:300]}


def git(repo, *args):
    proc = subprocess.run(["git", "-C", repo, *args], stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, text=True)
    if proc.returncode != 0:
        raise RuntimeError("git {} -> {}".format(" ".join(args[:2]), proc.stderr[:300]))
    return proc.stdout.strip()


def new_space(label):
    slug = "gitsync-{}-{}-{}".format(label, time.strftime("%H%M%S"),
                                      str(time.time_ns())[-5:])
    code, res = http("POST", API + "/spaces",
                     {"name": label, "slug": slug, "source": "api"})
    if code != 200:
        raise RuntimeError("创建空间失败 {} {}".format(code, res))
    return res["id"]


def sync(space, extra, stdin=None, base=BASE):
    proc = subprocess.run(
        [PY, str(SYNC), "--api", base, "--space", space] + extra,
        input=stdin, text=True, capture_output=True, timeout=180)
    events = []
    for line in proc.stdout.splitlines():
        line = line.strip()
        if line.startswith("{"):
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    return proc, events


def build_repo(root):
    repo = str(Path(root) / "work")
    os.makedirs(repo)
    git(repo, "init", "-q")
    git(repo, "config", "user.name", "Git Sync Tester")
    git(repo, "config", "user.email", "git-sync@example.com")
    Path(repo, "README.md").write_text("# 同步测试\n\n第一段说明文字。\n", encoding="utf-8")
    Path(repo, "main.py").write_text("def hello():\n    return 'episteme'\n", encoding="utf-8")
    Path(repo, "index.html").write_text(
        "<!doctype html><html><head><title>页面</title></head>"
        "<body><article><h1>标题</h1><p>网页快照正文内容足够长以便阅读器识别。</p></article></body></html>",
        encoding="utf-8")
    Path(repo, "blob.bin").write_bytes(b"\x00\x01\x02binary\xff")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "first commit")
    sha1 = git(repo, "rev-parse", "HEAD")
    Path(repo, "README.md").write_text(
        "# 同步测试\n\n第一段说明文字。\n\n第二次提交新增内容。\n", encoding="utf-8")
    Path(repo, "notes.txt").write_text("纯文本附件示例\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "second commit")
    sha2 = git(repo, "rev-parse", "HEAD")
    return repo, sha1, sha2


def main():
    tmp = tempfile.mkdtemp(prefix="episteme-git-sync-")
    try:
        repo, sha1, sha2 = build_repo(tmp)
        print("\n---- git 旁路同步：Pack / 脚本 / REST 端到端 ----")

        code, packs = http("GET", API + "/packs")
        check("内置 Pack 含 git-ingest 且全部校验通过",
              code == 200 and any(p.get("id") == "pack_git-ingest" and p.get("valid")
                                  for p in packs.get("items", [])),
              [(p.get("id"), p.get("valid")) for p in packs.get("items", [])])

        sid = new_space("Git 同步回归")
        check("创建独立回归空间", bool(sid), sid)

        proc, dry = sync(sid, ["--repo", repo, "--install-pack", "--dry-run"])
        dry_event = next((e for e in dry if e.get("event") == "dry-run"), {})
        check("dry-run 成功且列出全部 git 元数据字段",
              proc.returncode == 0 and len(dry_event.get("fields", [])) >= 17,
              {"exit": proc.returncode, "fields": len(dry_event.get("fields", [])),
               "stderr": proc.stderr[:160]})
        check("dry-run 采集到文件内容 Part",
              dry_event.get("parts", 0) >= 2, dry_event.get("parts"))

        code, before = http("GET", API + "/spaces/" + sid + "/documents")
        check("dry-run 未写入任何文档", code == 200 and before.get("total") == 0,
              before.get("total"))

        code, tools = http("GET", API + "/spaces/" + sid + "/mcp/tools")
        tool_names = [t.get("name", "") for t in tools.get("tools", [])]
        check("安装 Pack 派生 gitcommit 动态 MCP 工具",
              code == 200 and any("gitcommit" in n for n in tool_names),
              [n for n in tool_names if "gitcommit" in n][:4])

        # 首次 push：新分支（old 全零）只带根提交 sha1，四个变更文件都在其中
        zero = "0" * 40
        proc, first_events = sync(sid, ["--repo", repo, "--mode", "hook"],
                                  stdin="{} {} refs/heads/main\n".format(zero, sha1))
        first = next((e for e in first_events if e.get("event") == "synced"), {})
        check("post-receive 新分支同步首个提交产生 docId / Commit",
              proc.returncode == 0 and first.get("sha") == sha1
              and bool(first.get("docId")) and bool(first.get("commitId")),
              {"exit": proc.returncode, "event": first, "stderr": proc.stderr[:200]})
        doc_id = first.get("docId", "")

        code, docs = http("GET", API + "/spaces/" + sid + "/documents")
        check("首个提交后空间内只有 1 篇文档", code == 200 and docs.get("total") == 1,
              docs.get("total"))

        code, doc = http("GET", API + "/spaces/" + sid + "/documents/" + doc_id)
        fields = doc.get("fields", {})
        parts = doc.get("parts", [])
        check("提交标识：SHA / 短 SHA / 仓库 / 分支齐全",
              code == 200 and fields.get("gitSha") == sha1
              and fields.get("gitShortSha") == sha1[:7]
              and bool(fields.get("gitRepo")) and fields.get("gitBranch") == "main",
              {k: fields.get(k) for k in ("gitSha", "gitShortSha", "gitRepo", "gitBranch")})
        check("作者与时间：姓名 / 邮箱 / 时间 / 提交者齐全",
              fields.get("gitAuthorName") == "Git Sync Tester"
              and fields.get("gitAuthorEmail") == "git-sync@example.com"
              and bool(fields.get("gitAuthorDate"))
              and bool(fields.get("gitCommitter")),
              {k: fields.get(k) for k in ("gitAuthorName", "gitAuthorEmail",
                                          "gitAuthorDate", "gitCommitter")})
        check("说明与变更：message / 文件列表 / diff 齐全",
              fields.get("gitMessage") == "first commit"
              and fields.get("gitChangedFiles", {}).get("count", 0) >= 4
              and "README.md" in fields.get("gitDiff", ""),
              {"message": fields.get("gitMessage"),
               "count": fields.get("gitChangedFiles", {}).get("count"),
               "diffLen": len(fields.get("gitDiff", ""))})

        imported = {i["path"]: i["kind"] for i in
                    fields.get("gitFileContents", {}).get("imported", [])}
        skipped = {i["path"]: i["reason"] for i in
                   fields.get("gitFileContents", {}).get("skipped", [])}
        check("文件内容：md / code / web 三类按扩展名映射",
              imported.get("README.md") == "md"
              and imported.get("main.py") == "code"
              and imported.get("index.html") == "web", imported)
        check("二进制文件被安全跳过并记录原因",
              skipped.get("blob.bin") in ("binary", "non-utf8"), skipped)
        check("Part 数与索引一致且正文可阅读",
              fields.get("gitPartCount") == len(parts)
              and any(p.get("kind") == "md" and "同步测试" in p.get("content", "")
                      for p in parts)
              and any(p.get("kind") == "code" and p.get("language") == "python"
                      for p in parts),
              {"partCount": fields.get("gitPartCount"), "actual": len(parts)})

        # 同一个 hook 收到增量 push：old=sha1, new=sha2
        proc, incr_events = sync(sid, ["--repo", repo, "--mode", "hook"],
                                 stdin="{} {} refs/heads/main\n".format(sha1, sha2))
        incr_synced = [e for e in incr_events if e.get("event") == "synced"]
        code, after_incr = http("GET", API + "/spaces/" + sid + "/documents")
        check("post-receive 增量 push 同步第二个提交",
              proc.returncode == 0 and any(e.get("sha") == sha2 for e in incr_synced)
              and after_incr.get("total") == 2,
              {"exit": proc.returncode, "total": after_incr.get("total"),
               "stderr": proc.stderr[:200]})
        second = next((d for d in after_incr.get("items", [])
                       if d.get("fields", {}).get("gitSha") == sha2), {})
        check("增量写入的分支 / 说明来自 ref 与提交",
              second.get("fields", {}).get("gitBranch") == "main"
              and second.get("fields", {}).get("gitMessage") == "second commit",
              {k: second.get("fields", {}).get(k) for k in ("gitBranch", "gitMessage")})

        # head 模式：另起一个空间验证首次写入，再在主空间验证重放
        sid_head = new_space("Git 同步 head")
        proc, head_first = sync(sid_head, ["--repo", repo, "--install-pack",
                                           "--branch", "main"])
        hf = next((e for e in head_first if e.get("event") == "synced"), {})
        check("head 模式首次写入产生文档",
              proc.returncode == 0 and bool(hf.get("docId"))
              and hf.get("idempotentReplay") is False,
              {"exit": proc.returncode, "event": hf, "stderr": proc.stderr[:200]})

        proc, head_events = sync(sid, ["--repo", repo, "--branch", "main"])
        head_replay = next((e for e in head_events if e.get("event") == "synced"), {})
        code, after_head = http("GET", API + "/spaces/" + sid + "/documents")
        check("head 模式重放同一提交幂等且总数不变",
              proc.returncode == 0 and head_replay.get("idempotentReplay") is True
              and after_head.get("total") == 2,
              {"replay": head_replay.get("idempotentReplay"),
               "total": after_head.get("total")})

        proc, range_events = sync(sid, ["--repo", repo, "--mode", "range",
                                        "--range", "{}..{}".format(sha1, sha2)])
        range_synced = next((e for e in range_events if e.get("event") == "synced"), {})
        check("range 模式重跑仍幂等",
              proc.returncode == 0 and range_synced.get("idempotentReplay") is True,
              {"exit": proc.returncode, "event": range_synced})

        if SECURE_BASE and SECURE_SECRET:
            print("\n---- secure 模式 HMAC 链路 ----")
            spec = importlib.util.spec_from_file_location("episteme_git_sync", SYNC)
            gs = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(gs)
            client = gs.Client(SECURE_BASE, "hmac", SECURE_SECRET,
                               "git-sync", "owner", 30, 600)
            slug = "gitsync-secure-" + str(time.time_ns())[-8:]
            code, space = client.post("/spaces", {"name": "Git 同步 secure",
                                                  "slug": slug, "source": "api"})
            sec_sid = space.get("id", "")
            check("secure 实例用 HMAC 创建空间成功", code == 200 and bool(sec_sid),
                  {"code": code, "body": space})
            if sec_sid:
                proc, sec_events = sync(
                    sec_sid, ["--repo", repo, "--install-pack", "--auth", "hmac",
                              "--secret", SECURE_SECRET, "--branch", "main"],
                    base=SECURE_BASE)
                sec_synced = next((e for e in sec_events if e.get("event") == "synced"), {})
                check("secure 实例 HMAC 签名写入提交成功",
                      proc.returncode == 0 and bool(sec_synced.get("docId")),
                      {"exit": proc.returncode, "event": sec_synced,
                       "stderr": proc.stderr[:200]})
                code, unauth = http("POST", API + "/spaces",
                                    {"name": "无签名", "slug": "no-sig-" + slug},
                                    base=SECURE_BASE)
                check("secure 实例拒绝未签名写入", code == 401,
                      {"code": code, "body": unauth})
        else:
            print("\n(未配置 EPISTEME_SECURE_BASE / EPISTEME_SECURE_SECRET，跳过 secure 用例)")

        print("\n==== git 同步回归：{} PASS / {} FAIL ====".format(len(PASS), len(FAIL)))
        if FAIL:
            for name in FAIL:
                print("  FAIL " + name)
        return 1 if FAIL else 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
