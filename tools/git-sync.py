#!/usr/bin/env python3
"""Sync git commits into Episteme, with full git metadata and file contents.

Bypass design: Episteme is NOT a git remote. A normal `git push` lands on your
git server (Gitea / GitHub / a bare repo); its post-receive hook (or a CI step)
calls this script, which writes one Episteme document per commit via REST.

What is recorded (install the `pack_git-ingest` Pack first):
  - identity   : sha, short sha, repo, branch, tag
  - authorship : author name/email/date, committer name/email/date
  - change     : subject, body, changed file list, unified diff
  - contents   : changed text files become md / code / web / attachment Parts

Authentication (matches Episteme v0.5 secure mode):
  --auth legacy  X-Episteme-Role header (default, local/LAN only)
  --auth jwt     HS256 Bearer token built from --secret
  --auth hmac    X-Episteme-Key / Timestamp / Nonce / Signature / Role,
                 canonical = method\\npath\\nquery\\ntimestamp\\nnonce\\nrole\\nbodySha256

Idempotency: idempotencyKey = git-sync:<sha>:<repo-hash>. Re-running a hook or
CI job never creates duplicate documents.

Examples:
  # one-off, current HEAD
  python3 tools/git-sync.py --mode head --api http://127.0.0.1:5080 \
      --space <spaceId> --install-pack

  # bare repo post-receive hook (stdin: "<old> <new> <ref>" per line)
  #!/bin/sh
  EPISTEME_API=https://episteme.example.com \
  EPISTEME_SPACE_ID=<spaceId> EPISTEME_AUTH_MODE=secure \
  EPISTEME_AUTH_SECRET="$EPISTEME_AUTH_SECRET" \
  python3 /opt/episteme/tools/git-sync.py --mode hook

  # CI / range
  python3 tools/git-sync.py --mode range --range origin/main~5..origin/main \
      --api https://episteme.example.com --space <spaceId> --node <nodeId> \
      --auth hmac --secret "$EPISTEME_AUTH_SECRET"
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

API_ROOT = "/api/v1/episteme"
DEFAULT_PACK = "pack_git-ingest"
DEFAULT_NODE_PATH = "/Git 提交"

CODE_LANG = {
    ".cs": "csharp", ".py": "python", ".js": "javascript", ".mjs": "javascript",
    ".cjs": "javascript", ".ts": "typescript", ".tsx": "typescript",
    ".jsx": "javascript", ".json": "json", ".yml": "yaml", ".yaml": "yaml",
    ".sh": "bash", ".bash": "bash", ".sql": "sql", ".rs": "rust",
    ".go": "go", ".java": "java", ".kt": "kotlin", ".swift": "swift",
    ".c": "c", ".h": "c", ".cpp": "cpp", ".hpp": "cpp", ".css": "css",
    ".scss": "scss", ".xml": "xml", ".toml": "toml", ".ini": "ini",
    ".vue": "vue", ".svelte": "svelte", ".php": "php", ".rb": "ruby",
    ".proto": "protobuf", ".graphql": "graphql", ".tf": "hcl", ".lua": "lua",
}
MD_EXT = {".md", ".markdown", ".mdx"}
WEB_EXT = {".html", ".htm", ".xhtml"}


class SyncError(Exception):
    pass


class Git:
    """Run git either against a worktree (-C) or a bare repo (GIT_DIR)."""

    def __init__(self, repo: str = "", git_dir: str = ""):
        self.repo = repo
        self.git_dir = git_dir

    def run(self, *args: str, binary: bool = False):
        env = os.environ.copy()
        cmd = ["git"]
        if self.git_dir:
            env["GIT_DIR"] = self.git_dir
            env.pop("GIT_WORK_TREE", None)
        elif self.repo:
            cmd += ["-C", self.repo]
        cmd += list(args)
        proc = subprocess.run(cmd, env=env, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE)
        if proc.returncode != 0:
            raise SyncError("git {} 失败: {}".format(
                " ".join(args[:3]), proc.stderr.decode("utf-8", "replace").strip()[:300]))
        return proc.stdout if binary else proc.stdout.decode("utf-8", "replace")

    def try_run(self, *args: str, binary: bool = False):
        try:
            return self.run(*args, binary=binary)
        except SyncError:
            return b"" if binary else ""


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def jwt_hs256(secret: str, role: str, ttl: int) -> str:
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {"role": role, "sub": "git-sync", "exp": int(time.time()) + ttl}
    signing = b64url(json.dumps(header, separators=(",", ":")).encode()) + "." + \
              b64url(json.dumps(payload, separators=(",", ":")).encode())
    sig = hmac.new(secret.encode(), signing.encode("ascii"), hashlib.sha256).digest()
    return signing + "." + b64url(sig)


class Client:
    def __init__(self, base: str, auth: str, secret: str, key_id: str,
                 role: str, timeout: int, jwt_ttl: int):
        self.base = base.rstrip("/")
        self.auth = auth
        self.secret = secret
        self.key_id = key_id
        self.role = role
        self.timeout = timeout
        self.jwt_ttl = jwt_ttl

    def url(self, path: str) -> str:
        return self.base + API_ROOT + path

    def request(self, method: str, url: str, body=None):
        data = b"" if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers = {"Content-Type": "application/json"}
        split = urllib.parse.urlsplit(url)
        if self.auth == "legacy":
            headers["X-Episteme-Role"] = self.role
        elif self.auth == "jwt":
            if not self.secret:
                raise SyncError("--auth jwt 需要 --secret / EPISTEME_AUTH_SECRET")
            headers["Authorization"] = "Bearer " + jwt_hs256(
                self.secret, self.role, self.jwt_ttl)
        elif self.auth == "hmac":
            if not self.secret:
                raise SyncError("--auth hmac 需要 --secret / EPISTEME_AUTH_SECRET")
            ts = str(int(time.time()))
            nonce = uuid.uuid4().hex
            body_hash = hashlib.sha256(data).hexdigest()
            query = ("?" + split.query) if split.query else ""
            canonical = "\n".join([method, split.path, query, ts, nonce,
                                   self.role, body_hash])
            signature = hmac.new(self.secret.encode(), canonical.encode("utf-8"),
                                 hashlib.sha256).hexdigest()
            headers.update({
                "X-Episteme-Key": self.key_id,
                "X-Episteme-Timestamp": ts,
                "X-Episteme-Nonce": nonce,
                "X-Episteme-Signature": signature,
                "X-Episteme-Role": self.role,
            })
        else:
            raise SyncError("未知认证模式：" + self.auth)

        req = urllib.request.Request(
            url, data=(data if body is not None else None),
            headers=headers, method=method)
        host = split.hostname or ""
        try:
            if host in ("127.0.0.1", "localhost", "::1"):
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
                resp = opener.open(req, timeout=self.timeout)
            else:
                resp = urllib.request.urlopen(req, timeout=self.timeout)
            with resp:
                raw = resp.read().decode("utf-8", "replace")
                return resp.status, (json.loads(raw) if raw else {})
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", "replace")
            try:
                return exc.code, json.loads(raw)
            except json.JSONDecodeError:
                return exc.code, {"raw": raw[:400]}
        except OSError as exc:
            raise SyncError("请求 {} 失败：{}".format(split.path, exc))

    def get(self, path: str):
        return self.request("GET", self.url(path))

    def post(self, path: str, body):
        return self.request("POST", self.url(path), body)


def part_kind(path: str):
    ext = Path(path).suffix.lower()
    if ext in MD_EXT:
        return "md", "markdown"
    if ext in WEB_EXT:
        return "web", "html"
    if ext in CODE_LANG:
        return "code", CODE_LANG[ext]
    return "attachment", ""


def collect_commit(git: Git, sha: str, branch: str, repo_url: str,
                   opts) -> dict:
    fmt = ("%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%s%x1f%b%x1e")
    raw = git.run("show", "-s", "--format=" + fmt, sha)
    meta = raw.split("\x1e")[0].split("\x1f")
    while len(meta) < 10:
        meta.append("")
    (full, short, an, ae, ad, cn, ce, cd, subject, body) = meta[:10]

    tags_raw = git.try_run("tag", "--points-at", sha).strip()
    tags = ",".join(t for t in tags_raw.splitlines() if t.strip())

    changed_raw = git.try_run("diff-tree", "--no-commit-id", "--name-status",
                              "-r", "--root", sha)
    changed = []
    for line in changed_raw.splitlines():
        cols = line.split("\t")
        if len(cols) < 2:
            continue
        status = cols[0][:1] or "M"
        path = cols[-1].strip()
        if path:
            changed.append({"status": status, "path": path})

    diff = git.try_run("show", "--format=", "--no-ext-diff", "--no-renames",
                       "--unified=3", sha)
    diff_bytes = diff.encode("utf-8", "replace")
    diff_truncated = len(diff_bytes) > opts.max_diff_bytes
    diff = diff_bytes[:opts.max_diff_bytes].decode("utf-8", "ignore")

    parts = []
    imported, skipped = [], []
    for entry in changed:
        path, status = entry["path"], entry["status"]
        if status == "D":
            skipped.append({"path": path, "reason": "deleted"})
            continue
        if len(imported) >= opts.max_files:
            skipped.append({"path": path, "reason": "max-files"})
            continue
        blob = git.try_run("show", "{}:{}".format(sha, path), binary=True)
        if not blob:
            skipped.append({"path": path, "reason": "unreadable"})
            continue
        if len(blob) > opts.max_file_bytes:
            skipped.append({"path": path, "reason": "too-large"})
            continue
        if b"\x00" in blob:
            skipped.append({"path": path, "reason": "binary"})
            continue
        try:
            text = blob.decode("utf-8")
        except UnicodeDecodeError:
            skipped.append({"path": path, "reason": "non-utf8"})
            continue
        kind, language = part_kind(path)
        part = {
            "kind": kind,
            "content": text,
            "sourceUri": "git:{}:{}".format(sha, path),
            "meta": {"gitPath": path, "gitStatus": status, "gitSha": sha},
        }
        if language:
            part["language"] = language
        parts.append(part)
        imported.append({"path": path, "kind": kind,
                         "bytes": len(blob), "language": language})

    if not parts:
        # Keep at least one readable Part so the document opens in the reader.
        parts.append({"kind": "md", "content": "# {}\n\n{}".format(short, subject),
                      "sourceUri": "git:{}:summary".format(sha)})

    title = "{} {}".format(short, subject.strip())[:240]
    fields = {
        "title": title,
        "gitSha": full,
        "gitShortSha": short,
        "gitRepo": repo_url,
        "gitBranch": branch,
        "gitTag": tags,
        "gitAuthorName": an,
        "gitAuthorEmail": ae,
        "gitAuthorDate": ad.strip(),
        "gitCommitter": "{} <{}> {}".format(cn, ce, cd.strip()),
        "gitMessage": subject.strip(),
        "gitBody": body.strip(),
        "gitChangedFiles": {
            "count": len(changed),
            "truncated": len(changed) > opts.max_files,
            "files": changed[:opts.max_files],
        },
        "gitDiff": diff + ("\n\n[diff truncated]" if diff_truncated else ""),
        "gitFileContents": {
            "imported": imported,
            "skipped": skipped,
            "maxFiles": opts.max_files,
            "maxFileBytes": opts.max_file_bytes,
        },
        "gitPartCount": len(parts),
        "gitSyncSource": "git-sync/1.0",
    }
    repo_hash = hashlib.sha256(repo_url.encode("utf-8")).hexdigest()[:8]
    payload = {
        "title": title,
        "fields": fields,
        "parts": parts,
        "source": "api",
        "actor": "{} <{}>".format(an, ae),
        "agentId": "git-sync",
        "reason": "git:" + full,
        "action": "git_ingest",
        "idempotencyKey": "git-sync:{}:{}".format(full, repo_hash),
    }
    return {"sha": full, "short": short, "payload": payload,
            "parts": len(parts), "changed": len(changed)}


def resolve_node(client: Client, space: str, opts) -> str:
    if opts.node:
        return opts.node
    code, tree = client.get("/spaces/{}/tree".format(space))
    if code != 200:
        raise SyncError("读取目录树失败 {}：{}".format(code, tree))
    for item in tree.get("items", []):
        if item.get("path") == opts.node_path:
            return item.get("id", "")
    raise SyncError("找不到节点 {}；先加 --install-pack 安装 {}".format(
        opts.node_path, opts.pack_id))


def install_pack(client: Client, space: str, opts):
    code, res = client.post("/spaces/{}/packs/install".format(space), {
        "packId": opts.pack_id,
        "source": "api",
        "idempotencyKey": "git-sync-pack:" + opts.pack_id,
    })
    if code != 200:
        raise SyncError("安装 Pack 失败 {}：{}".format(code, res))
    return res


def repo_url(git: Git, override: str) -> str:
    if override:
        return override
    value = git.try_run("remote", "get-url", "origin").strip()
    return value or os.environ.get("GIT_REPO_URL", "") or "local"


def commits_for(git: Git, opts):
    """Return [(sha, branch)] in oldest-first order."""
    out = []
    if opts.mode == "head":
        sha = git.run("rev-parse", "HEAD").strip()
        branch = opts.branch or git.try_run("branch", "--show-current").strip()
        return [(sha, branch or opts.branch or "HEAD")]
    if opts.mode == "range":
        if ".." not in opts.range:
            raise SyncError("--range 需要 A..B 形式")
        shas = git.run("rev-list", "--reverse",
                       "--max-count=" + str(opts.max_commits), opts.range).split()
        return [(s, opts.branch or "range") for s in shas]
    # hook mode: stdin lines "<old> <new> <ref>"
    zero = "0" * 40
    for line in sys.stdin.read().splitlines():
        cols = line.split()
        if len(cols) < 3:
            continue
        old, new, ref = cols[0], cols[1], cols[2]
        if new == zero:
            continue
        branch = ref[len("refs/heads/"):] if ref.startswith("refs/heads/") else ref
        if old == zero:
            listing = git.try_run("rev-list", "--reverse",
                                  "--max-count=" + str(opts.max_commits), new)
        else:
            listing = git.try_run("rev-list", "--reverse",
                                  "--max-count=" + str(opts.max_commits),
                                  "{}..{}".format(old, new))
        for sha in listing.split():
            out.append((sha, opts.branch or branch))
    return out


def env_default(name: str, fallback: str = "") -> str:
    return os.environ.get(name, fallback)


def parse_args(argv=None):
    p = argparse.ArgumentParser(
        description="把 git 提交（含元数据与文件内容）同步进 Episteme",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="环境变量：EPISTEME_API / EPISTEME_SPACE_ID / EPISTEME_NODE_ID / "
               "EPISTEME_AUTH_MODE / EPISTEME_AUTH_SECRET / EPISTEME_API_KEY_ID / "
               "EPISTEME_GIT_DIR / EPISTEME_GIT_REPO / EPISTEME_GIT_NODE_PATH")
    p.add_argument("--mode", choices=["head", "hook", "range"], default="head")
    p.add_argument("--range", default="", help="range 模式下的 A..B")
    p.add_argument("--api", default=env_default("EPISTEME_API", "http://127.0.0.1:5080"))
    p.add_argument("--space", default=env_default("EPISTEME_SPACE_ID"))
    p.add_argument("--node", default=env_default("EPISTEME_NODE_ID"),
                   help="目标目录节点 ID；省略时按 --node-path 查找")
    p.add_argument("--node-path", default=env_default("EPISTEME_GIT_NODE_PATH", DEFAULT_NODE_PATH))
    p.add_argument("--pack-id", default=DEFAULT_PACK)
    p.add_argument("--install-pack", action="store_true", help="先安装 git-ingest Pack")
    p.add_argument("--repo", default="", help="本地工作仓库路径")
    p.add_argument("--git-dir", default=env_default("EPISTEME_GIT_DIR"),
                   help="bare 仓库 GIT_DIR（hook 中通常无需指定，会继承环境）")
    p.add_argument("--repo-url", default=env_default("EPISTEME_GIT_REPO"))
    p.add_argument("--branch", default="")
    p.add_argument("--auth", choices=["legacy", "secure", "jwt", "hmac"],
                   default=env_default("EPISTEME_AUTH_MODE", "legacy"),
                   help="secure 等价于 hmac；默认 legacy")
    p.add_argument("--secret", default=env_default("EPISTEME_AUTH_SECRET"))
    p.add_argument("--key-id", default=env_default("EPISTEME_API_KEY_ID", "git-sync"))
    p.add_argument("--role", default=env_default("EPISTEME_SYNC_ROLE", "owner"))
    p.add_argument("--timeout", type=int, default=30)
    p.add_argument("--jwt-ttl", type=int, default=600)
    p.add_argument("--max-commits", type=int, default=20)
    p.add_argument("--max-files", type=int, default=20)
    p.add_argument("--max-file-bytes", type=int, default=262144)
    p.add_argument("--max-diff-bytes", type=int, default=65536)
    p.add_argument("--dry-run", action="store_true")
    return p.parse_args(argv)


def main(argv=None) -> int:
    opts = parse_args(argv)
    if not opts.space:
        print("缺少 --space / EPISTEME_SPACE_ID", file=sys.stderr)
        return 2
    auth = opts.auth
    if auth == "secure":
        auth = "hmac"
    elif auth not in ("legacy", "jwt", "hmac"):
        auth = "legacy"
    opts.auth = auth

    client = Client(opts.api, auth, opts.secret, opts.key_id,
                    opts.role, opts.timeout, opts.jwt_ttl)
    git = Git(opts.repo, opts.git_dir)
    try:
        if opts.install_pack:
            result = install_pack(client, opts.space, opts)
            print(json.dumps({"event": "pack-installed",
                              "createdNodes": result.get("createdNodes"),
                              "createdContentTypes": result.get("createdContentTypes")},
                             ensure_ascii=False))
        node = resolve_node(client, opts.space, opts)
        pairs = commits_for(git, opts)
        if not pairs:
            print(json.dumps({"event": "no-commits", "mode": opts.mode}))
            return 0
        url = repo_url(git, opts.repo_url)
        summary = []
        for sha, branch in pairs:
            item = collect_commit(git, sha, branch, url, opts)
            if opts.dry_run:
                payload = item["payload"]
                print(json.dumps({"event": "dry-run", "sha": item["sha"],
                                  "branch": branch, "parts": item["parts"],
                                  "changed": item["changed"],
                                  "fields": sorted(payload["fields"].keys())},
                                 ensure_ascii=False))
                continue
            code, res = client.post(
                "/spaces/{}/nodes/{}/documents".format(opts.space, node),
                item["payload"])
            if code != 200:
                raise SyncError("写入提交 {} 失败 {}：{}".format(
                    item["short"], code, json.dumps(res, ensure_ascii=False)[:400]))
            summary.append({"sha": item["sha"], "docId": res.get("docId"),
                            "commitId": res.get("commitId"),
                            "idempotentReplay": res.get("idempotentReplay", False),
                            "parts": item["parts"]})
            print(json.dumps({"event": "synced", **summary[-1]}, ensure_ascii=False))
        print(json.dumps({"event": "done", "mode": opts.mode,
                          "total": len(summary),
                          "replayed": sum(1 for s in summary if s["idempotentReplay"]),
                          "dryRun": opts.dry_run}, ensure_ascii=False))
        return 0
    except SyncError as exc:
        print("git-sync: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
