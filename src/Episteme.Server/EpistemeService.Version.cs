using System.Text.Json;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

/// <summary>P2: content-addressed DAG, version operations and dynamic MCP tool discovery.</summary>
public sealed partial class EpistemeService
{
    private static JsonObject SnapshotOf(Document doc, string nodePath, string typeSlug)
    {
        var parts = new JsonArray();
        foreach (var p in doc.Parts.OrderBy(p => p.Ord))
            parts.Add(new JsonObject
            {
                ["id"] = p.Id, ["kind"] = p.Kind, ["ord"] = p.Ord,
                ["content"] = p.Content, ["sourceUri"] = p.SourceUri,
                ["language"] = p.Language, ["mime"] = p.Mime,
                ["meta"] = Jsonx.Node(p.MetaJson).DeepClone()
            });
        return new JsonObject
        {
            ["id"] = doc.Id, ["nodePath"] = nodePath, ["typeSlug"] = typeSlug,
            ["title"] = doc.Title, ["fields"] = Jsonx.Node(doc.FieldsJson).DeepClone(),
            ["tags"] = Jsonx.Array(doc.TagsJson).DeepClone(), ["parts"] = parts,
            ["updatedAt"] = doc.UpdatedAt
        };
    }

    private static async Task<(string Hash, string Json, int Count)> BuildTreeAsync(
        EpistemeDbContext db, string spaceId, string branch, CancellationToken ct)
    {
        var docs = await db.Documents.Include(d => d.Parts)
            .Where(d => d.SpaceId == spaceId && d.Branch == branch).ToListAsync(ct);
        var nodeIds = docs.Select(d => d.NodeId).Distinct().ToList();
        var nodes = await db.Nodes.Where(n => nodeIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var typeIds = docs.Select(d => d.ContentTypeId).Distinct().ToList();
        var types = await db.ContentTypes.Where(c => typeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        var entries = new List<TreeEntry>();
        var seenBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in docs)
        {
            var nodePath = nodes.GetValueOrDefault(doc.NodeId)?.Path ?? "/";
            var typeSlug = types.GetValueOrDefault(doc.ContentTypeId)?.Slug ?? "";
            var canonical = Canonical.Normalize(SnapshotOf(doc, nodePath, typeSlug));
            var hash = Canonical.Hash(canonical);
            if (seenBlobs.Add(hash) && !await db.Blobs.AnyAsync(b => b.Hash == hash, ct))
                db.Blobs.Add(new Blob
                {
                    Hash = hash, Size = canonical.Length, Mime = "application/json", Content = canonical
                });
            entries.Add(new TreeEntry
            {
                Path = $"{nodePath}/{doc.Id}", Kind = "document", DocumentId = doc.Id,
                TypeSlug = typeSlug, Title = doc.Title, BlobHash = hash
            });
        }

        var (json, count) = TreeStore.Serialize(entries);
        // Scope the tree hash to the Space: each Space owns an independent DAG.
        var canonicalTree = Canonical.Normalize(Jsonx.Node(json));
        return (Canonical.Hash(spaceId + ":" + canonicalTree), json, count);
    }

    private static async Task<JsonObject> ComputeStatsAsync(
        EpistemeDbContext db, string? parentCommitId, string newEntriesJson, CancellationToken ct)
    {
        var now = TreeStore.Parse(newEntriesJson);
        var before = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(parentCommitId))
        {
            var parent = await db.Commits.FirstOrDefaultAsync(c => c.Id == parentCommitId, ct);
            var tree = parent is null ? null : await db.Trees.FirstOrDefaultAsync(t => t.Hash == parent.TreeHash, ct);
            if (tree is not null) before = TreeStore.Parse(tree.EntriesJson);
        }
        return new JsonObject
        {
            ["added"] = now.Keys.Count(k => !before.ContainsKey(k)),
            ["modified"] = now.Keys.Count(k => before.TryGetValue(k, out var old) && old.BlobHash != now[k].BlobHash),
            ["deleted"] = before.Keys.Count(k => !now.ContainsKey(k)),
            ["parts"] = now.Count
        };
    }

    private static async Task<Commit?> FindIdempotentAsync(
        EpistemeDbContext db, string spaceId, WriteContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey)) return null;
        return await db.Commits.FirstOrDefaultAsync(
            c => c.SpaceId == spaceId && c.IdempotencyKey == ctx.IdempotencyKey, ct);
    }

    private async Task<Commit> WriteCommitAsync(
        EpistemeDbContext db, Space space, WriteContext ctx, string message, CancellationToken ct)
    {
        var refName = WriteContext.RefName(ctx.Branch);
        var mainRef = await db.Refs.FirstOrDefaultAsync(
            r => r.SpaceId == space.Id && r.Name == "refs/heads/main", ct);
        var refRow = await db.Refs.FirstOrDefaultAsync(
            r => r.SpaceId == space.Id && r.Name == refName, ct);
        if (refRow is null)
        {
            refRow = new RefRow
            {
                SpaceId = space.Id, Name = refName, CommitId = mainRef?.CommitId ?? ""
            };
            db.Refs.Add(refRow);
        }

        var parent = refRow.CommitId;
        if (!string.IsNullOrWhiteSpace(ctx.ExpectParent) && ctx.ExpectParent != parent)
            throw new BusinessException("CONFLICT_PARENT",
                $"HEAD 已变化：期望 {ctx.ExpectParent}，实际 {parent}。请先 log 重读再重试。", 409);

        var (treeHash, entriesJson, count) = await BuildTreeAsync(db, space.Id, refName, ct);
        if (!await db.Trees.AnyAsync(t => t.Hash == treeHash, ct))
            db.Trees.Add(new Tree
            {
                Hash = treeHash, SpaceId = space.Id, EntriesJson = entriesJson, EntryCount = count
            });

        var parents = new JsonArray();
        if (!string.IsNullOrEmpty(parent)) parents.Add(JsonValue.Create(parent));
        var stats = await ComputeStatsAsync(db, string.IsNullOrEmpty(parent) ? null : parent, entriesJson, ct);

        var commit = new Commit
        {
            Id = Ids.New("cm"), SpaceId = space.Id, ParentsJson = parents.ToJsonString(),
            TreeHash = treeHash, Message = message,
            AuthorType = ctx.Source == "mcp" ? "agent" : "user",
            AuthorId = ctx.Actor, AuthorName = ctx.Actor, Source = ctx.Source,
            Reason = ctx.Reason ?? "", AgentId = ctx.AgentId,
            IdempotencyKey = ctx.IdempotencyKey, StatsJson = stats.ToJsonString()
        };
        db.Commits.Add(commit);
        refRow.CommitId = commit.Id;
        refRow.UpdatedAt = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ids.New("al"), SpaceId = space.Id, Actor = ctx.Actor, Action = ctx.Action,
            AgentId = ctx.AgentId, CommitId = commit.Id, IdempotencyKey = ctx.IdempotencyKey,
            DetailJson = Jsonx.Str(new { source = ctx.Source, branch = refName, message })
        });
        return commit;
    }

    /// <summary>One transaction: entity mutations + DAG objects. A replay rolls back the mutation.</summary>
    private async Task<Commit> PersistAsync(
        EpistemeDbContext db, Space space, WriteContext ctx, string message, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            var replay = await FindIdempotentAsync(db, space.Id, ctx, ct);
            if (replay is not null)
            {
                await tx.RollbackAsync(ct);
                return replay;
            }
            var commit = await WriteCommitAsync(db, space, ctx, message, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return commit;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* best effort */ }
            throw;
        }
    }

    private static async Task<Commit?> ResolveCommitAsync(
        EpistemeDbContext db, string spaceId, string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference.StartsWith("refs/", StringComparison.Ordinal))
        {
            var row = await db.Refs.FirstOrDefaultAsync(r => r.SpaceId == spaceId && r.Name == reference, ct);
            return row is null ? null : await db.Commits.FirstOrDefaultAsync(c => c.Id == row.CommitId, ct);
        }
        var byId = await db.Commits.FirstOrDefaultAsync(c => c.SpaceId == spaceId && c.Id == reference, ct);
        if (byId is not null) return byId;
        var branch = await db.Refs.FirstOrDefaultAsync(
            r => r.SpaceId == spaceId && r.Name == "refs/heads/" + reference, ct);
        return branch is null ? null : await db.Commits.FirstOrDefaultAsync(c => c.Id == branch.CommitId, ct);
    }

    private static async Task<Dictionary<string, TreeEntry>> TreeOfCommitAsync(
        EpistemeDbContext db, Commit commit, CancellationToken ct)
    {
        var tree = await db.Trees.FirstOrDefaultAsync(t => t.Hash == commit.TreeHash, ct);
        return tree is null ? new Dictionary<string, TreeEntry>(StringComparer.Ordinal) : TreeStore.Parse(tree.EntriesJson);
    }

    private static async Task<JsonObject?> ResolveSnapshotAsync(
        EpistemeDbContext db, string spaceId, string reference, string docId, CancellationToken ct)
    {
        var commit = await ResolveCommitAsync(db, spaceId, reference, ct);
        if (commit is null) return null;
        foreach (var entry in (await TreeOfCommitAsync(db, commit, ct)).Values)
        {
            if (entry.DocumentId != docId) continue;
            var blob = await db.Blobs.FirstOrDefaultAsync(b => b.Hash == entry.BlobHash, ct);
            if (blob is not null) return Jsonx.Node(blob.Content);
        }
        return null;
    }

    private static JsonObject CommitNode(Commit c) => new()
    {
        ["id"] = c.Id, ["parents"] = Jsonx.Array(c.ParentsJson).DeepClone(),
        ["treeHash"] = c.TreeHash, ["message"] = c.Message, ["source"] = c.Source,
        ["reason"] = c.Reason, ["agentId"] = c.AgentId,
        ["authorType"] = c.AuthorType, ["authorName"] = c.AuthorName,
        ["idempotencyKey"] = c.IdempotencyKey, ["stats"] = Jsonx.Node(c.StatsJson).DeepClone(),
        ["createdAt"] = c.CreatedAt
    };

    // ---------- Version API ----------

    public async Task<JsonObject> LogAsync(string spaceId, JsonObject query, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var list = await db.Commits.Where(c => c.SpaceId == spaceId)
            .OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        var agentId = S(query, "agentId");
        var source = S(query, "source");
        var since = S(query, "since");
        if (!string.IsNullOrEmpty(agentId)) list = list.Where(c => c.AgentId == agentId).ToList();
        if (!string.IsNullOrEmpty(source)) list = list.Where(c => c.Source == source).ToList();
        if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, out var from))
            list = list.Where(c => c.CreatedAt >= from.ToUniversalTime()).ToList();
        var limit = Math.Clamp(I(query, "limit", 50), 1, 500);
        var arr = new JsonArray();
        foreach (var c in list.Take(limit)) arr.Add(CommitNode(c));
        return new JsonObject { ["items"] = arr, ["total"] = list.Count, ["returned"] = arr.Count };
    }

    public async Task<JsonObject> GetCommitAsync(string spaceId, string commitId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var commit = await db.Commits.FirstOrDefaultAsync(c => c.SpaceId == spaceId && c.Id == commitId, ct)
                     ?? throw new BusinessException("COMMIT_NOT_FOUND", "提交不存在", 404);
        var result = CommitNode(commit);
        result["entryCount"] = (await TreeOfCommitAsync(db, commit, ct)).Count;
        return result;
    }

    public async Task<JsonObject> DiffAsync(string spaceId, JsonObject query, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var baseCommit = await ResolveCommitAsync(db, spaceId, S(query, "base"), ct);
        var targetCommit = await ResolveCommitAsync(db, spaceId, S(query, "target"), ct);
        if (targetCommit is null && baseCommit is not null) targetCommit = await ResolveCommitAsync(db, spaceId, "refs/heads/main", ct);
        if (targetCommit is null) throw new BusinessException("REF_NOT_FOUND", "target 无法解析", 404);
        var baseEntries = baseCommit is null
            ? new Dictionary<string, TreeEntry>(StringComparer.Ordinal)
            : await TreeOfCommitAsync(db, baseCommit, ct);
        var targetEntries = await TreeOfCommitAsync(db, targetCommit, ct);

        var prefix = S(query, "path");
        if (!string.IsNullOrEmpty(prefix))
        {
            baseEntries = baseEntries.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            targetEntries = targetEntries.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        var blobs = await db.Blobs.ToDictionaryAsync(b => b.Hash, ct);
        JsonObject Resolve(string hash) =>
            blobs.TryGetValue(hash, out var blob) ? Jsonx.Node(blob.Content) : new JsonObject();
        var items = DiffService.Compare(baseEntries, targetEntries, Resolve);

        return new JsonObject
        {
            ["base"] = baseCommit?.Id, ["target"] = targetCommit.Id,
            ["items"] = JsonSerializer.SerializeToNode(items, Jsonx.Opts) ?? new JsonArray(),
            ["total"] = items.Count
        };
    }

    public async Task<JsonObject> BlameAsync(string spaceId, string docId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var doc = await db.Documents.Include(d => d.Parts)
            .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
            ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);
        var last = await db.Commits.Where(c => c.SpaceId == spaceId)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        var lines = new JsonArray();
        var index = 0;
        foreach (var p in doc.Parts.Where(p => p.Kind is "md" or "code"))
            foreach (var line in (p.Content ?? "").Replace("\r\n", "\n").Split('\n'))
                lines.Add(new JsonObject
                {
                    ["index"] = ++index, ["text"] = line,
                    ["commitId"] = last?.Id, ["source"] = last?.Source, ["agentId"] = last?.AgentId
                });
        return new JsonObject { ["docId"] = docId, ["lines"] = lines, ["simplified"] = true };
    }

    public async Task<JsonObject> RefsAsync(string spaceId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var rows = await db.Refs.Where(r => r.SpaceId == spaceId).OrderBy(r => r.Name).ToListAsync(ct);
        var arr = new JsonArray();
        foreach (var r in rows)
            arr.Add(new JsonObject { ["name"] = r.Name, ["commitId"] = r.CommitId, ["updatedAt"] = r.UpdatedAt });
        return new JsonObject { ["items"] = arr, ["total"] = arr.Count };
    }

    public async Task<JsonObject> CreateBranchAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var name = S(args, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessException("BAD_REQUEST", "name 不能为空");
        var refName = WriteContext.RefName(name);
        if (await db.Refs.AnyAsync(r => r.SpaceId == spaceId && r.Name == refName, ct))
            throw new BusinessException("REF_EXISTS", $"引用已存在：{refName}", 409);
        var from = S(args, "from");
        var source = await ResolveCommitAsync(db, spaceId, from, ct)
                     ?? await ResolveCommitAsync(db, spaceId, "refs/heads/main", ct)
                     ?? throw new BusinessException("REF_NOT_FOUND", "无法解析起点提交", 404);
        db.Refs.Add(new RefRow { SpaceId = spaceId, Name = refName, CommitId = source.Id });
        await db.SaveChangesAsync(ct);
        return new JsonObject { ["name"] = refName, ["commitId"] = source.Id, ["created"] = true };
    }

    public async Task<JsonObject> CreateTagAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var name = "refs/tags/" + S(args, "name").TrimStart("refs/tags/".ToCharArray());
        if (name == "refs/tags/") throw new BusinessException("BAD_REQUEST", "name 不能为空");
        if (await db.Refs.AnyAsync(r => r.SpaceId == spaceId && r.Name == name, ct))
            throw new BusinessException("REF_EXISTS", $"标签已存在：{name}", 409);
        var commit = await ResolveCommitAsync(db, spaceId, S(args, "commitId"), ct)
                     ?? await ResolveCommitAsync(db, spaceId, "refs/heads/main", ct)
                     ?? throw new BusinessException("COMMIT_NOT_FOUND", "无法解析提交", 404);
        db.Refs.Add(new RefRow { SpaceId = spaceId, Name = name, CommitId = commit.Id });
        await db.SaveChangesAsync(ct);
        return new JsonObject { ["name"] = name, ["commitId"] = commit.Id };
    }

    public async Task<JsonObject> MergeAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var from = WriteContext.RefName(S(args, "from"));
        var fromCommit = await ResolveCommitAsync(db, spaceId, from, ct)
                         ?? throw new BusinessException("REF_NOT_FOUND", $"来源分支不存在：{from}", 404);
        var mainRef = await db.Refs.FirstOrDefaultAsync(r => r.SpaceId == spaceId && r.Name == "refs/heads/main", ct);
        var mainCommit = mainRef is null ? null : await db.Commits.FirstOrDefaultAsync(c => c.Id == mainRef.CommitId, ct);
        var fromEntries = await TreeOfCommitAsync(db, fromCommit, ct);
        var mainEntries = mainCommit is null
            ? new Dictionary<string, TreeEntry>(StringComparer.Ordinal)
            : await TreeOfCommitAsync(db, mainCommit, ct);

        var conflicts = new JsonArray();
        foreach (var kv in fromEntries)
            if (mainEntries.TryGetValue(kv.Key, out var mine) && mine.BlobHash != kv.Value.BlobHash)
                conflicts.Add(new JsonObject
                {
                    ["path"] = kv.Key, ["ours"] = mine.BlobHash, ["theirs"] = kv.Value.BlobHash,
                    ["hint"] = "字段级自动合并未覆盖此路径，需人工选择"
                });
        if (conflicts.Count > 0)
            return new JsonObject { ["merged"] = false, ["conflicts"] = conflicts, ["commitId"] = null };

        var fromDocs = await db.Documents.Where(d => d.SpaceId == spaceId && d.Branch == from).ToListAsync(ct);
        foreach (var d in fromDocs) d.Branch = "refs/heads/main";
        ctx.Branch = "refs/heads/main";
        ctx.Source = "system";
        var commit = await PersistAsync(db, space, ctx, $"merge {from}", ct);
        return new JsonObject
        {
            ["merged"] = true, ["conflicts"] = new JsonArray(), ["commitId"] = commit.Id,
            ["mergedDocuments"] = fromDocs.Count, ["singleParent"] = true
        };
    }

    public async Task<JsonObject> RevertAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var commitId = S(args, "commitId");
        var target = await db.Commits.FirstOrDefaultAsync(c => c.SpaceId == spaceId && c.Id == commitId, ct)
                     ?? throw new BusinessException("COMMIT_NOT_FOUND", "提交不存在", 404);
        var parents = Jsonx.Array(target.ParentsJson);
        var parentId = parents.Count > 0 ? Jsonx.Plain(parents[0]) : "";
        var parentCommit = string.IsNullOrEmpty(parentId)
            ? null
            : await db.Commits.FirstOrDefaultAsync(c => c.Id == parentId, ct);
        var parentEntries = parentCommit is null
            ? new Dictionary<string, TreeEntry>(StringComparer.Ordinal)
            : await TreeOfCommitAsync(db, parentCommit, ct);

        var branch = "refs/heads/main";
        var current = await db.Documents.Where(d => d.SpaceId == spaceId && d.Branch == branch).ToListAsync(ct);
        var currentIds = current.Select(d => d.Id).ToList();
        var oldParts = await db.Parts.Where(p => currentIds.Contains(p.DocumentId)).ToListAsync(ct);
        var oldPartIds = oldParts.Select(p => p.Id).ToList();
        db.Annotations.RemoveRange(await db.Annotations.Where(a => oldPartIds.Contains(a.PartId)).ToListAsync(ct));
        db.Parts.RemoveRange(oldParts);
        db.Documents.RemoveRange(current);
        // Flush deletions before re-inserting rows with the same document ids; otherwise
        // the (DocumentId, Ord) unique index collides with the stale part rows.
        await db.SaveChangesAsync(ct);

        var nodes = await db.Nodes.Where(n => n.SpaceId == spaceId).ToListAsync(ct);
        var types = await LatestDefinitionsAsync(db, spaceId, ct);
        var blobs = await db.Blobs.ToDictionaryAsync(b => b.Hash, ct);
        var restored = 0;
        foreach (var entry in parentEntries.Values)
        {
            if (!blobs.TryGetValue(entry.BlobHash, out var blob)) continue;
            var snapshot = Jsonx.Node(blob.Content);
            var nodePath = Jsonx.Plain(snapshot["nodePath"]);
            var node = nodes.FirstOrDefault(n => n.Path == nodePath) ?? nodes.FirstOrDefault();
            var type = types.FirstOrDefault(t => t.Def.Slug == entry.TypeSlug);
            if (node is null || type.Def is null) continue;

            var doc = new Document
            {
                Id = entry.DocumentId ?? Ids.New("doc"), SpaceId = spaceId, NodeId = node.Id,
                ContentTypeId = type.Row.Id, SchemaVersion = type.Def.Version, Branch = branch,
                Title = Jsonx.Plain(snapshot["title"]), FieldsJson = (snapshot["fields"] as JsonObject ?? new JsonObject()).ToJsonString(),
                TagsJson = (snapshot["tags"] as JsonArray ?? new JsonArray()).ToJsonString()
            };
            var ord = 0;
            if (snapshot["parts"] is JsonArray parts)
                foreach (var p in parts)
                    if (p is JsonObject po)
                        doc.Parts.Add(new Part
                        {
                            Id = Ids.New("pt"), DocumentId = doc.Id, Ord = ord++,
                            Kind = Jsonx.Plain(po["kind"]), Content = po["content"] is null ? null : Jsonx.Plain(po["content"]),
                            SourceUri = po["sourceUri"] is null ? null : Jsonx.Plain(po["sourceUri"]),
                            Language = po["language"] is null ? null : Jsonx.Plain(po["language"]),
                            MetaJson = (po["meta"] as JsonObject ?? new JsonObject()).ToJsonString()
                        });
            db.Documents.Add(doc);
            restored++;
        }

        ctx.Branch = branch;
        ctx.Source = "system";
        var commit = await PersistAsync(db, space, ctx, $"revert {target.Id}", ct);
        return new JsonObject
        {
            ["revertedCommitId"] = target.Id, ["newCommitId"] = commit.Id,
            ["restoredDocuments"] = restored, ["parentCommitId"] = parentId
        };
    }

    public async Task<JsonObject> ExportGitAsync(string spaceId, JsonObject args, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var commits = await db.Commits.Where(c => c.SpaceId == spaceId)
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var arr = new JsonArray();
        foreach (var c in commits)
            arr.Add(new JsonObject
            {
                ["id"] = c.Id, ["message"] = c.Message, ["source"] = c.Source,
                ["parents"] = Jsonx.Array(c.ParentsJson).DeepClone(), ["createdAt"] = c.CreatedAt
            });
        return new JsonObject
        {
            ["format"] = "linearized-commits", ["commits"] = arr, ["total"] = arr.Count,
            ["note"] = "本轮返回线性化提交清单；生成真实 git 仓库属于后续能力"
        };
    }

    // ---------- Dynamic tools & audit ----------

    public async Task<JsonObject> ToolsListAsync(string spaceId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var defs = await LatestDefinitionsAsync(db, spaceId, ct);
        var nodes = await db.Nodes.Where(n => n.SpaceId == spaceId && n.ExposeToMcp).ToListAsync(ct);

        var bound = new List<(ContentTypeDefinition Def, string[] NodePaths)>();
        foreach (var (row, def) in defs)
        {
            var paths = nodes.Where(n => n.ContentTypeId == row.Id).Select(n => n.Path).ToArray();
            if (paths.Length > 0) bound.Add((def, paths));
        }

        var statics = ToolRegistry.StaticTools();
        var dynamics = ToolRegistry.DynamicTools(bound);
        var arr = new JsonArray();
        foreach (var tool in statics.Concat(dynamics)) arr.Add(ToolRegistry.ToMcp(tool));
        return new JsonObject
        {
            ["spaceId"] = spaceId, ["toolsVersion"] = space.ToolsVersion,
            ["tools"] = arr, ["total"] = arr.Count,
            ["staticToolCount"] = statics.Count, ["dynamicToolCount"] = dynamics.Count
        };
    }

    public async Task<JsonObject> AuditListAsync(string spaceId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var rows = await db.AuditLogs.Where(a => a.SpaceId == spaceId)
            .OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(ct);
        var arr = new JsonArray();
        foreach (var a in rows)
            arr.Add(new JsonObject
            {
                ["id"] = a.Id, ["actor"] = a.Actor, ["action"] = a.Action,
                ["agentId"] = a.AgentId, ["commitId"] = a.CommitId,
                ["idempotencyKey"] = a.IdempotencyKey,
                ["detail"] = Jsonx.Node(a.DetailJson).DeepClone(), ["createdAt"] = a.CreatedAt
            });
        return new JsonObject { ["items"] = arr, ["total"] = arr.Count };
    }
}
