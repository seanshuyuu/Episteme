using System.Text.Json;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

public sealed class BusinessException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>Common write parameters shared by UI, REST and MCP.</summary>
public sealed class WriteContext
{
    public string Source { get; set; } = "api";
    public string Actor { get; set; } = "local";
    public string Role { get; set; } = "owner";
    public string? AgentId { get; set; }
    public string? Reason { get; set; }
    public string? IdempotencyKey { get; set; }
    public string Branch { get; set; } = "refs/heads/main";
    public string? ExpectParent { get; set; }
    public bool DryRun { get; set; }
    public string Action { get; set; } = "";
    /// <summary>P3: lets a write pre-compute its Commit id (annotations record it in their anchor).</summary>
    public string? ForcedCommitId { get; set; }
    /// <summary>P4: set by PersistAsync only when an existing Commit was replayed.</summary>
    public bool Replayed { get; set; }

    public static string RefName(string? branch) =>
        string.IsNullOrWhiteSpace(branch) ? "refs/heads/main"
        : branch.StartsWith("refs/", StringComparison.Ordinal) ? branch
        : "refs/heads/" + branch;

    public static WriteContext From(JsonObject args, string source, string role = "owner")
    {
        string? S(string key)
        {
            var v = Jsonx.Plain(args[key]);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        var requested = S("source");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "ui", "api", "mcp", "import", "schema", "system" };
        return new WriteContext
        {
            Source = requested is not null && allowed.Contains(requested) ? requested : source,
            Role = role,
            Actor = S("actor") ?? "local",
            AgentId = S("agentId"),
            Reason = S("reason"),
            IdempotencyKey = S("idempotencyKey"),
            Branch = RefName(S("branch")),
            ExpectParent = S("expectParent"),
            DryRun = BoolArg(args, "dryRun", false),
            Action = S("action") ?? ""
        };
    }

    private static bool BoolArg(JsonObject args, string key, bool fallback) =>
        args[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
}

public sealed partial class EpistemeService(IDbContextFactory<EpistemeDbContext> factory)
{
    private Task<EpistemeDbContext> Db(CancellationToken ct) => factory.CreateDbContextAsync(ct);

    private static async Task<Space> RequireSpace(EpistemeDbContext db, string id, CancellationToken ct) =>
        await db.Spaces.FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new BusinessException("SPACE_NOT_FOUND", $"空间不存在：{id}", 404);

    private static void RequireRole(WriteContext ctx, params string[] allowed)
    {
        if (!allowed.Contains(ctx.Role))
            throw new BusinessException("FORBIDDEN_SCOPE", $"当前角色 {ctx.Role} 无此操作权限", 403);
    }

    private static string S(JsonObject args, string key, string fallback = "")
    {
        var v = Jsonx.Plain(args[key]);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static int I(JsonObject args, string key, int fallback) =>
        args[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;

    private static JsonObject O(JsonObject args, string key) => args[key] as JsonObject ?? new JsonObject();

    private static JsonArray A(JsonObject args, string key) => args[key] as JsonArray ?? new JsonArray();

    private static async Task<(ContentType Row, ContentTypeDefinition Def)> LatestType(
        EpistemeDbContext db, string spaceId, string slugOrId, CancellationToken ct)
    {
        var row = await db.ContentTypes
            .Where(c => c.SpaceId == spaceId && (c.Slug == slugOrId || c.Id == slugOrId))
            .OrderByDescending(c => c.Version).FirstOrDefaultAsync(ct)
            ?? throw new BusinessException("CONTENT_TYPE_NOT_FOUND", $"内容类型不存在：{slugOrId}", 404);
        var def = Jsonx.Obj<ContentTypeDefinition>(row.DefinitionJson)
                  ?? throw new BusinessException("SCHEMA_INVALID", "内容类型定义无法解析", 500);
        return (row, def);
    }

    // ---------- Spaces ----------

    public async Task<JsonObject> ListSpacesAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        var spaces = await db.Spaces.OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var items = new JsonArray();
        foreach (var s in spaces)
        {
            var docs = await db.Documents.CountAsync(d => d.SpaceId == s.Id, ct);
            var commits = await db.Commits.CountAsync(c => c.SpaceId == s.Id, ct);
            items.Add(new JsonObject
            {
                ["id"] = s.Id, ["slug"] = s.Slug, ["name"] = s.Name,
                ["description"] = s.Description, ["headRef"] = s.HeadRef,
                ["toolsVersion"] = s.ToolsVersion, ["documentCount"] = docs,
                ["commitCount"] = commits, ["createdAt"] = s.CreatedAt
            });
        }
        return new JsonObject { ["items"] = items, ["total"] = items.Count, ["empty"] = items.Count == 0 };
    }

    public async Task<JsonObject> CreateSpaceAsync(JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        var name = S(args, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessException("BAD_REQUEST", "name 不能为空");
        var slug = S(args, "slug", "space-" + Guid.NewGuid().ToString("N")[..8]);
        if (await db.Spaces.AnyAsync(s => s.WorkspaceId == "default" && s.Slug == slug, ct))
            throw new BusinessException("SPACE_EXISTS", $"slug 已存在：{slug}", 409);

        var space = new Space
        {
            Id = Ids.New("sp"), Name = name, Slug = slug,
            Description = S(args, "description"), ToolsVersion = 1
        };
        db.Spaces.Add(space);
        await db.SaveChangesAsync(ct);

        var (treeHash, entriesJson, count) = await BuildTreeAsync(db, space.Id, "refs/heads/main", ct);
        db.Trees.Add(new Tree { Hash = treeHash, SpaceId = space.Id, EntriesJson = entriesJson, EntryCount = count });
        var initial = new Commit
        {
            Id = Ids.New("cm"), SpaceId = space.Id, TreeHash = treeHash,
            Message = "init: empty space", Source = "system", AuthorName = ctx.Actor,
            StatsJson = Jsonx.Str(new { added = 0, modified = 0, deleted = 0 })
        };
        db.Commits.Add(initial);
        db.Refs.Add(new RefRow { SpaceId = space.Id, Name = "refs/heads/main", CommitId = initial.Id });
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ids.New("al"), SpaceId = space.Id, Actor = ctx.Actor,
            Action = "create_space", CommitId = initial.Id,
            DetailJson = Jsonx.Str(new { slug = space.Slug })
        });
        await db.SaveChangesAsync(ct);

        return new JsonObject
        {
            ["id"] = space.Id, ["slug"] = space.Slug, ["name"] = space.Name,
            ["toolsVersion"] = space.ToolsVersion, ["headCommitId"] = initial.Id
        };
    }

    public async Task<JsonObject> GetSpaceAsync(string id, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var s = await RequireSpace(db, id, ct);
        var head = await db.Refs.FirstOrDefaultAsync(r => r.SpaceId == id && r.Name == "refs/heads/main", ct);
        return new JsonObject
        {
            ["id"] = s.Id, ["slug"] = s.Slug, ["name"] = s.Name,
            ["description"] = s.Description, ["headRef"] = s.HeadRef,
            ["headCommitId"] = head?.CommitId, ["toolsVersion"] = s.ToolsVersion
        };
    }

    // ---------- Nodes ----------

    public async Task<JsonObject> ListNodesAsync(string spaceId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var nodes = await db.Nodes.Where(n => n.SpaceId == spaceId)
            .OrderBy(n => n.Path).ToListAsync(ct);
        var arr = new JsonArray();
        foreach (var n in nodes)
        {
            string? slug = null;
            if (n.ContentTypeId is not null)
                slug = (await db.ContentTypes.FirstOrDefaultAsync(c => c.Id == n.ContentTypeId, ct))?.Slug;
            arr.Add(new JsonObject
            {
                ["id"] = n.Id, ["path"] = n.Path, ["name"] = n.Name,
                ["parentId"] = n.ParentId, ["contentType"] = slug,
                ["exposeToMcp"] = n.ExposeToMcp, ["defaultPartKind"] = n.DefaultPartKind,
                ["icon"] = n.Icon
            });
        }
        return new JsonObject { ["items"] = arr, ["total"] = arr.Count };
    }

    public async Task<JsonObject> CreateNodeAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var path = NormalizePath(S(args, "path"));
        if (path == "/") throw new BusinessException("BAD_REQUEST", "path 不能为根");
        if (await db.Nodes.AnyAsync(n => n.SpaceId == spaceId && n.Path == path, ct))
            throw new BusinessException("NODE_EXISTS", $"节点已存在：{path}", 409);

        await EnsureParentsAsync(db, spaceId, path, ct);

        string? typeId = null;
        var typeSlug = S(args, "contentType");
        if (!string.IsNullOrWhiteSpace(typeSlug))
            typeId = (await LatestType(db, spaceId, typeSlug, ct)).Row.Id;

        var node = new Node
        {
            Id = Ids.New("nd"), SpaceId = spaceId, Path = path,
            Name = path.TrimEnd('/').Split('/').Last(), ContentTypeId = typeId,
            ExposeToMcp = args["exposeToMcp"] is JsonValue ev && ev.TryGetValue<bool>(out var e) ? e : true,
            DefaultPartKind = string.IsNullOrWhiteSpace(S(args, "defaultPartKind")) ? null : S(args, "defaultPartKind"),
            Icon = S(args, "icon", "folder")
        };
        node.ParentId = await FindParentIdAsync(db, spaceId, path, ct);
        db.Nodes.Add(node);
        space.ToolsVersion++;
        var commit = await PersistAsync(db, space, ctx, $"新建目录 {path}", ct);
        return new JsonObject
        {
            ["id"] = node.Id, ["path"] = node.Path, ["contentType"] = typeSlug,
            ["commitId"] = commit.Id, ["toolsVersion"] = space.ToolsVersion
        };
    }

    public async Task<JsonObject> UpdateNodeAsync(string spaceId, string nodeId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.SpaceId == spaceId && n.Id == nodeId, ct)
                   ?? throw new BusinessException("NODE_NOT_FOUND", "节点不存在", 404);
        var oldPath = node.Path;

        if (args.ContainsKey("exposeToMcp") && args["exposeToMcp"] is JsonValue ev && ev.TryGetValue<bool>(out var ex))
            node.ExposeToMcp = ex;
        if (args.ContainsKey("contentType"))
        {
            var slug = S(args, "contentType");
            node.ContentTypeId = string.IsNullOrWhiteSpace(slug) ? null : (await LatestType(db, spaceId, slug, ct)).Row.Id;
        }
        if (args.ContainsKey("path"))
        {
            var newPath = NormalizePath(S(args, "path"));
            if (newPath != oldPath)
            {
                if (await db.Nodes.AnyAsync(n => n.SpaceId == spaceId && n.Path == newPath, ct))
                    throw new BusinessException("NODE_EXISTS", $"节点已存在：{newPath}", 409);
                await EnsureParentsAsync(db, spaceId, newPath, ct);
                var children = await db.Nodes
                    .Where(n => n.SpaceId == spaceId && n.Path.StartsWith(oldPath + "/")).ToListAsync(ct);
                foreach (var child in children) child.Path = newPath + child.Path[oldPath.Length..];
                node.Path = newPath;
                node.Name = newPath.TrimEnd('/').Split('/').Last();
                node.ParentId = await FindParentIdAsync(db, spaceId, newPath, ct);
            }
        }
        space.ToolsVersion++;
        var commit = await PersistAsync(db, space, ctx, $"更新目录 {oldPath}", ct);
        return new JsonObject { ["id"] = node.Id, ["path"] = node.Path, ["commitId"] = commit.Id, ["toolsVersion"] = space.ToolsVersion };
    }

    public async Task<JsonObject> DeleteNodeAsync(string spaceId, string nodeId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.SpaceId == spaceId && n.Id == nodeId, ct)
                   ?? throw new BusinessException("NODE_NOT_FOUND", "节点不存在", 404);
        var children = await db.Nodes.CountAsync(n => n.SpaceId == spaceId && n.Path.StartsWith(node.Path + "/"), ct);
        var docs = await db.Documents.CountAsync(d => d.SpaceId == spaceId && d.NodeId == nodeId, ct);
        var recursive = args["recursive"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
        if ((children > 0 || docs > 0) && !recursive)
            throw new BusinessException("NODE_NOT_EMPTY", "该目录非空，需显式 recursive=true", 409);

        var childIds = await db.Nodes.Where(n => n.SpaceId == spaceId && n.Path.StartsWith(node.Path + "/"))
            .Select(n => n.Id).ToListAsync(ct);
        var allIds = new HashSet<string>(childIds) { node.Id };
        var affected = await db.Documents.Where(d => allIds.Contains(d.NodeId)).ToListAsync(ct);
        db.Documents.RemoveRange(affected);
        db.Nodes.RemoveRange(await db.Nodes.Where(n => allIds.Contains(n.Id)).ToListAsync(ct));
        space.ToolsVersion++;
        var commit = await PersistAsync(db, space, ctx, $"删除目录 {node.Path}", ct);
        return new JsonObject { ["deleted"] = true, ["commitId"] = commit.Id, ["removedNodes"] = allIds.Count };
    }

    private static string NormalizePath(string path)
    {
        var p = (path ?? "").Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        return p.Length > 1 ? p.TrimEnd('/') : "/";
    }

    private static async Task EnsureParentsAsync(EpistemeDbContext db, string spaceId, string path, CancellationToken ct)
    {
        var segments = path.Trim('/').Split('/');
        var current = "";
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current += "/" + segments[i];
            if (!await db.Nodes.AnyAsync(n => n.SpaceId == spaceId && n.Path == current, ct))
                db.Nodes.Add(new Node
                {
                    Id = Ids.New("nd"), SpaceId = spaceId, Path = current,
                    Name = segments[i], Icon = "folder",
                    ParentId = i == 0 ? null : await FindParentIdAsync(db, spaceId, current, ct)
                });
        }
    }

    private static async Task<string?> FindParentIdAsync(EpistemeDbContext db, string spaceId, string path, CancellationToken ct)
    {
        var idx = path.LastIndexOf('/');
        if (idx <= 0) return null;
        var parentPath = path[..idx];
        return await db.Nodes.Where(n => n.SpaceId == spaceId && n.Path == parentPath)
            .Select(n => n.Id).FirstOrDefaultAsync(ct);
    }

    // ---------- Content types / schema ----------

    public async Task<JsonObject> ListContentTypesAsync(string spaceId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var latest = await LatestDefinitionsAsync(db, spaceId, ct);
        var arr = new JsonArray();
        foreach (var (row, def) in latest)
            arr.Add(new JsonObject
            {
                ["id"] = row.Id, ["slug"] = def.Slug, ["label"] = def.Label,
                ["version"] = row.Version, ["exposeToMcp"] = def.ExposeToMcp,
                ["fieldCount"] = def.Fields.Count, ["updatedAt"] = row.UpdatedAt
            });
        return new JsonObject { ["items"] = arr, ["total"] = arr.Count };
    }

    public async Task<JsonObject> GetContentTypeAsync(string spaceId, string slug, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var (row, def) = await LatestType(db, spaceId, slug, ct);
        return new JsonObject
        {
            ["id"] = row.Id, ["version"] = row.Version,
            ["definition"] = JsonSerializer.SerializeToNode(def, Jsonx.Opts)
        };
    }

    public async Task<JsonObject> UpsertContentTypeAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var raw = args["definition"] as JsonObject ?? args;
        var def = Jsonx.Obj<ContentTypeDefinition>(raw.ToJsonString())
                  ?? throw new BusinessException("SCHEMA_INVALID", "definition 无法解析");
        var errors = SchemaEngine.CheckDefinition(def);
        if (errors.Count > 0) throw new BusinessException("SCHEMA_VALIDATION", string.Join("；", errors), 422);

        var existing = await db.ContentTypes.Where(c => c.SpaceId == spaceId && c.Slug == def.Slug)
            .OrderByDescending(c => c.Version).FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            var oldDef = Jsonx.Obj<ContentTypeDefinition>(existing.DefinitionJson)!;
            def.Version = existing.Version + 1;
            var docs = await db.Documents.Where(d => d.SpaceId == spaceId && d.ContentTypeId == existing.Id).ToListAsync(ct);
            foreach (var doc in docs)
            {
                var fields = Jsonx.Node(doc.FieldsJson);
                var (migrated, migrationErrors) = SchemaEngine.Migrate(oldDef, def, fields);
                if (migrationErrors.Count > 0)
                    throw new BusinessException("SCHEMA_MIGRATION", string.Join("；", migrationErrors), 422);
                doc.FieldsJson = migrated.ToJsonString();
                doc.SchemaVersion = def.Version;
            }
        }
        else def.Version = 1;

        var rowId = Ids.New("ct");
        db.ContentTypes.Add(new ContentType
        {
            Id = rowId, SpaceId = spaceId, Slug = def.Slug, Label = def.Label,
            Version = def.Version, DefinitionJson = Jsonx.Str(def), ExposeToMcp = def.ExposeToMcp
        });

        // Rebind nodes that referenced the previous version by slug.
        var nodes = await db.Nodes.Where(n => n.SpaceId == spaceId).ToListAsync(ct);
        foreach (var n in nodes)
        {
            if (n.ContentTypeId is null) continue;
            var bound = await db.ContentTypes.FirstOrDefaultAsync(c => c.Id == n.ContentTypeId, ct);
            if (bound?.Slug == def.Slug) n.ContentTypeId = rowId;
        }
        var nodePath = S(args, "nodePath");
        if (!string.IsNullOrWhiteSpace(nodePath))
        {
            var normalized = NormalizePath(nodePath);
            var target = await db.Nodes.FirstOrDefaultAsync(n => n.SpaceId == spaceId && n.Path == normalized, ct);
            if (target is not null) target.ContentTypeId = rowId;
            else
            {
                await EnsureParentsAsync(db, spaceId, normalized, ct);
                db.Nodes.Add(new Node
                {
                    Id = Ids.New("nd"), SpaceId = spaceId, Path = normalized,
                    Name = normalized.TrimEnd('/').Split('/').Last(),
                    ContentTypeId = rowId, Icon = def.Icon ?? "folder",
                    ParentId = await FindParentIdAsync(db, spaceId, normalized, ct)
                });
            }
        }

        ctx.Source = "schema";
        space.ToolsVersion++;
        var commit = await PersistAsync(db, space, ctx, $"Schema {(existing is null ? "创建" : "更新")} {def.Slug} v{def.Version}", ct);
        return new JsonObject
        {
            ["id"] = rowId, ["slug"] = def.Slug, ["version"] = def.Version,
            ["toolsVersion"] = space.ToolsVersion, ["commitId"] = commit.Id,
            ["render"] = SchemaEngine.Render(def)
        };
    }

    public async Task<JsonObject> ValidateFieldsAsync(string spaceId, string slug, JsonObject args, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var (_, def) = await LatestType(db, spaceId, slug, ct);
        var fields = O(args, "fields");
        SchemaEngine.ApplyDefaults(def, fields);
        var errors = SchemaEngine.Validate(def, fields);
        return new JsonObject
        {
            ["valid"] = errors.Count == 0,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["fields"] = fields.DeepClone()
        };
    }

    public async Task<JsonObject> GetRenderAsync(string spaceId, string nodeId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.SpaceId == spaceId && n.Id == nodeId, ct)
                   ?? throw new BusinessException("NODE_NOT_FOUND", "节点不存在", 404);
        if (node.ContentTypeId is null)
            return new JsonObject { ["nodeId"] = nodeId, ["contentType"] = null, ["columns"] = new JsonArray(), ["controls"] = new JsonArray() };
        var type = await db.ContentTypes.FirstOrDefaultAsync(c => c.Id == node.ContentTypeId, ct)
                   ?? throw new BusinessException("CONTENT_TYPE_NOT_FOUND", "节点绑定的类型不存在", 404);
        var def = Jsonx.Obj<ContentTypeDefinition>(type.DefinitionJson)!;
        var render = SchemaEngine.Render(def);
        render["nodeId"] = nodeId;
        render["nodePath"] = node.Path;
        render["exposeToMcp"] = node.ExposeToMcp;
        render["permissions"] = new JsonObject { ["read"] = true, ["write"] = true, ["schema"] = true, ["version"] = true };
        return render;
    }

    private static async Task<List<(ContentType Row, ContentTypeDefinition Def)>> LatestDefinitionsAsync(
        EpistemeDbContext db, string spaceId, CancellationToken ct)
    {
        var rows = await db.ContentTypes.Where(c => c.SpaceId == spaceId).ToListAsync(ct);
        return rows.GroupBy(r => r.Slug)
            .Select(g => g.OrderByDescending(r => r.Version).First())
            .Select(r => (r, Jsonx.Obj<ContentTypeDefinition>(r.DefinitionJson)!))
            .Where(t => t.Item2 is not null)
            .OrderBy(t => t.Item2.Slug).ToList();
    }

    // ---------- Documents ----------

    public async Task<JsonObject> ListDocumentsAsync(string spaceId, JsonObject query, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var branch = WriteContext.RefName(query["branch"] is null ? null : Jsonx.Plain(query["branch"]));
        var nodeId = S(query, "nodeId");
        var q = S(query, "q");
        var page = Math.Max(1, I(query, "page", 1));
        var pageSize = Math.Clamp(I(query, "pageSize", 20), 1, 200);

        var list = await db.Documents.Include(d => d.Parts)
            .Where(d => d.SpaceId == spaceId && d.Branch == branch)
            .Where(d => nodeId == "" || d.NodeId == nodeId)
            .OrderByDescending(d => d.UpdatedAt).ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(q))
            list = list.Where(d => d.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || d.FieldsJson.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        var total = list.Count;
        var items = new JsonArray();
        foreach (var d in list.Skip((page - 1) * pageSize).Take(pageSize))
            items.Add(DocumentNode(d));
        return new JsonObject { ["items"] = items, ["total"] = total, ["page"] = page, ["pageSize"] = pageSize, ["branch"] = branch };
    }

    public async Task<JsonObject> GetDocumentAsync(string spaceId, string docId, JsonObject query, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var reference = S(query, "ref");
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var snapshot = await ResolveSnapshotAsync(db, spaceId, reference, docId, ct);
            if (snapshot is not null)
            {
                snapshot["ref"] = reference;
                return snapshot;
            }
        }
        var doc = await db.Documents.Include(d => d.Parts)
            .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
            ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);
        var result = DocumentNode(doc);
        return result;
    }

    public async Task<JsonObject> CreateDocumentAsync(string spaceId, string nodeId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.SpaceId == spaceId && n.Id == nodeId, ct)
                   ?? throw new BusinessException("NODE_NOT_FOUND", "节点不存在", 404);
        if (node.ContentTypeId is null)
            throw new BusinessException("NODE_UNBOUND", "该目录未绑定内容类型，请先创建或绑定类型", 422);
        var type = await db.ContentTypes.FirstOrDefaultAsync(c => c.Id == node.ContentTypeId, ct)
                   ?? throw new BusinessException("CONTENT_TYPE_NOT_FOUND", "节点绑定的类型不存在", 404);
        var def = Jsonx.Obj<ContentTypeDefinition>(type.DefinitionJson)!;

        var fields = O(args, "fields").DeepClone() as JsonObject ?? new JsonObject();
        SchemaEngine.ApplyDefaults(def, fields);
        var errors = SchemaEngine.Validate(def, fields);
        if (errors.Count > 0) throw new BusinessException("SCHEMA_VALIDATION", string.Join("；", errors), 422);

        var primary = Jsonx.Plain(fields[def.PrimaryField]);
        var title = S(args, "title", string.IsNullOrWhiteSpace(primary) ? "未命名文档" : primary);
        var doc = new Document
        {
            Id = Ids.New("doc"), SpaceId = spaceId, NodeId = nodeId,
            ContentTypeId = type.Id, SchemaVersion = def.Version,
            Branch = WriteContext.RefName(ctx.Branch), Title = title,
            FieldsJson = fields.ToJsonString(), TagsJson = A(args, "tags").ToJsonString()
        };
        var parts = A(args, "parts");
        if (parts.Count == 0)
            foreach (var kind in def.DefaultPartKinds.DefaultIfEmpty("md"))
                parts.Add(new JsonObject { ["kind"] = kind, ["content"] = "" });
        var ord = 0;
        foreach (var p in parts)
        {
            if (p is not JsonObject po) continue;
            var content = po["content"] is null ? "" : Jsonx.Plain(po["content"]);
            var (kind, mime, meta, blobHash) = NormalizePart(po, content, "md");
            doc.Parts.Add(new Part
            {
                Id = Ids.New("pt"), DocumentId = doc.Id, Ord = ord++,
                Kind = kind, Content = content, Mime = mime, BlobHash = blobHash,
                SourceUri = po["sourceUri"] is null ? null : Jsonx.Plain(po["sourceUri"]),
                Language = po["language"] is null ? null : Jsonx.Plain(po["language"]),
                MetaJson = meta.ToJsonString()
            });
        }
        db.Documents.Add(doc);
        var commit = await PersistAsync(db, space, ctx, $"新建文档 {title}", ct);
        var replay = IsReplay(commit, ctx);
        return new JsonObject
        {
            ["docId"] = replay ? null : doc.Id, ["commitId"] = commit.Id, ["branch"] = doc.Branch,
            ["toolsVersion"] = space.ToolsVersion, ["title"] = title,
            ["idempotentReplay"] = replay
        };
    }

    public async Task<JsonObject> UpdateDocumentAsync(string spaceId, string docId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var doc = await db.Documents.Include(d => d.Parts)
            .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
            ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);
        var type = await db.ContentTypes.FirstOrDefaultAsync(c => c.Id == doc.ContentTypeId, ct);
        var def = type is null ? null : Jsonx.Obj<ContentTypeDefinition>(type.DefinitionJson);

        if (args.ContainsKey("title")) doc.Title = S(args, "title", doc.Title);
        if (args["fields"] is JsonObject incoming)
        {
            var fields = Jsonx.Node(doc.FieldsJson);
            foreach (var kv in incoming) fields[kv.Key] = kv.Value?.DeepClone();
            if (def is not null)
            {
                SchemaEngine.ApplyDefaults(def, fields);
                var errors = SchemaEngine.Validate(def, fields);
                if (errors.Count > 0) throw new BusinessException("SCHEMA_VALIDATION", string.Join("；", errors), 422);
            }
            doc.FieldsJson = fields.ToJsonString();
        }
        if (args["tags"] is JsonArray tags) doc.TagsJson = tags.ToJsonString();
        doc.UpdatedAt = DateTime.UtcNow;

        var commit = await PersistAsync(db, space, ctx, $"更新文档 {doc.Title}", ct);
        return new JsonObject { ["docId"] = doc.Id, ["commitId"] = commit.Id, ["idempotentReplay"] = IsReplay(commit, ctx) };
    }

    public async Task<JsonObject> DeleteDocumentAsync(string spaceId, string docId, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
                  ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);
        db.Documents.Remove(doc);
        var commit = await PersistAsync(db, space, ctx, $"删除文档 {doc.Title}", ct);
        return new JsonObject { ["deleted"] = true, ["commitId"] = commit.Id };
    }

    public async Task<JsonObject> AppendPartAsync(string spaceId, string docId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var doc = await db.Documents.Include(d => d.Parts)
            .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
            ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);
        var content = args["content"] is null ? "" : Jsonx.Plain(args["content"]);
        var (kind, mime, meta, blobHash) = NormalizePart(args, content, "md");
        var part = new Part
        {
            Id = Ids.New("pt"), DocumentId = docId, Ord = doc.Parts.Count == 0 ? 0 : doc.Parts.Max(p => p.Ord) + 1,
            Kind = kind, Content = content, Mime = mime, BlobHash = blobHash,
            SourceUri = args["sourceUri"] is null ? null : Jsonx.Plain(args["sourceUri"]),
            Language = args["language"] is null ? null : Jsonx.Plain(args["language"]),
            MetaJson = meta.ToJsonString()
        };
        doc.Parts.Add(part);
        doc.UpdatedAt = DateTime.UtcNow;
        var commit = await PersistAsync(db, space, ctx, $"追加 {part.Kind} Part", ct);
        return new JsonObject { ["partId"] = part.Id, ["commitId"] = commit.Id };
    }

    public async Task<JsonObject> UpdatePartAsync(string spaceId, string docId, string partId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId && p.DocumentId == docId, ct)
                   ?? throw new BusinessException("PART_NOT_FOUND", "Part 不存在", 404);
        if (part.Kind == "pdf" && args["content"] is not null)
            throw new BusinessException("PART_NOT_EDITABLE",
                "PDF 原件只读；所有修改请以标注形式记录，或新建一份重抓快照", 409);
        var content = args["content"] is null ? part.Content : Jsonx.Plain(args["content"]);
        var (kind, mime, meta, blobHash) = NormalizePart(args, content, part.Kind, Jsonx.Node(part.MetaJson));
        part.Kind = kind;
        part.Content = content;
        part.Mime = mime;
        part.BlobHash = blobHash;
        part.MetaJson = meta.ToJsonString();
        if (args["sourceUri"] is not null) part.SourceUri = Jsonx.Plain(args["sourceUri"]);
        if (args["language"] is not null) part.Language = Jsonx.Plain(args["language"]);
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId, ct);
        if (doc is not null) doc.UpdatedAt = DateTime.UtcNow;
        var changed = args["content"] is not null ? "编辑" : "更新元数据";
        var commit = await PersistAsync(db, space, ctx, $"{changed} {part.Kind} Part", ct);
        return new JsonObject
        {
            ["partId"] = part.Id, ["kind"] = part.Kind, ["commitId"] = commit.Id,
            ["blobHash"] = part.BlobHash, ["meta"] = Jsonx.Node(part.MetaJson).DeepClone(),
            ["idempotentReplay"] = IsReplay(commit, ctx)
        };
    }

    public async Task<JsonObject> ListAnnotationsAsync(string spaceId, string docId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var parts = await db.Parts.Where(p => p.DocumentId == docId).ToListAsync(ct);
        var byId = parts.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
        var partIds = parts.Select(p => p.Id).ToList();
        var rows = partIds.Count == 0
            ? new List<Annotation>()
            : await db.Annotations.Where(a => partIds.Contains(a.PartId)).ToListAsync(ct);
        var arr = new JsonArray();
        var lost = 0;
        foreach (var a in rows.OrderBy(x => x.CreatedAt))
        {
            var part = byId.GetValueOrDefault(a.PartId);
            var anchor = Jsonx.Node(a.AnchorJson);
            var resolution = AnchorEngine.Resolve(anchor, part?.Content);
            if (Jsonx.Plain(resolution["status"]) == "lost") lost++;
            var node = AnnotationNode(a, part?.Kind ?? "attachment", resolution);
            node["commitId"] = Jsonx.Plain(anchor["commitId"]);
            node["status"] = Jsonx.Plain(resolution["status"]);
            arr.Add(node);
        }
        return new JsonObject
        {
            ["items"] = arr, ["total"] = arr.Count, ["anchorLost"] = lost,
            ["note"] = lost > 0 ? "存在需要重新定位的标记，已在 UI 标记而不会静默丢弃" : "全部锚点可定位"
        };
    }

    public async Task<JsonObject> AddAnnotationAsync(string spaceId, string docId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var requestedPartId = S(args, "partId");
        var part = string.IsNullOrWhiteSpace(requestedPartId)
            ? await db.Parts.Where(p => p.DocumentId == docId).OrderBy(p => p.Ord).FirstOrDefaultAsync(ct)
            : await db.Parts.FirstOrDefaultAsync(p => p.Id == requestedPartId && p.DocumentId == docId, ct);
        if (part is null)
            throw new BusinessException("PART_NOT_FOUND", "未找到目标 Part（检查 partId 或先创建 Part）", 404);

        var annKind = S(args, "kind", "highlight");
        if (!AnchorEngine.AnnotationKinds.Contains(annKind, StringComparer.Ordinal))
            throw new BusinessException("ANNOTATION_KIND_INVALID",
                $"未知标记类型 {annKind}；可用：{string.Join(", ", AnchorEngine.AnnotationKinds)}", 422);

        var input = (args["anchor"] as JsonObject ?? new JsonObject()).DeepClone() as JsonObject ?? new JsonObject();
        if (input["kind"] is null) input["kind"] = part.Kind;
        var (anchor, errors) = AnchorEngine.Normalize(
            part.Kind, input, part.Content, Jsonx.Node(part.MetaJson));
        if (errors.Count > 0)
            throw new BusinessException("ANCHOR_INVALID", string.Join("；", errors), 422);

        // Pre-compute the Commit id so the anchor and the immutable snapshot agree.
        ctx.ForcedCommitId = Ids.New("cm");
        anchor["commitId"] = ctx.ForcedCommitId;
        var ann = new Annotation
        {
            Id = Ids.New("an"), PartId = part.Id, Kind = annKind,
            AnchorJson = anchor.ToJsonString(), Text = S(args, "text"),
            Color = args["color"] is null ? null : Jsonx.Plain(args["color"]),
            Author = ctx.Actor
        };
        db.Annotations.Add(ann);
        var commit = await PersistAsync(db, space, ctx, $"新增 {ann.Kind} 标记（{part.Kind}）", ct);
        return new JsonObject
        {
            ["annotationId"] = ann.Id, ["partId"] = part.Id, ["partKind"] = part.Kind,
            ["anchor"] = anchor, ["commitId"] = commit.Id,
            ["resolution"] = AnchorEngine.Resolve(anchor, part.Content),
            ["idempotentReplay"] = IsReplay(commit, ctx)
        };
    }

    public async Task<JsonObject> SearchAsync(string spaceId, JsonObject query, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var q = S(query, "q");
        var docs = await db.Documents.Where(d => d.SpaceId == spaceId).ToListAsync(ct);
        var arr = new JsonArray();
        foreach (var d in docs.Where(d => d.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                         || d.FieldsJson.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(50))
            arr.Add(new JsonObject { ["docId"] = d.Id, ["title"] = d.Title, ["branch"] = d.Branch });
        return new JsonObject { ["items"] = arr, ["total"] = arr.Count, ["q"] = q };
    }

    private static JsonObject DocumentNode(Document d)
    {
        var parts = new JsonArray();
        foreach (var p in d.Parts.OrderBy(p => p.Ord))
            parts.Add(new JsonObject
            {
                ["id"] = p.Id, ["kind"] = p.Kind, ["ord"] = p.Ord,
                ["content"] = p.Content, ["sourceUri"] = p.SourceUri,
                ["language"] = p.Language, ["mime"] = p.Mime,
                ["meta"] = Jsonx.Node(p.MetaJson).DeepClone()
            });
        return new JsonObject
        {
            ["id"] = d.Id, ["nodeId"] = d.NodeId, ["contentTypeId"] = d.ContentTypeId,
            ["schemaVersion"] = d.SchemaVersion, ["branch"] = d.Branch,
            ["title"] = d.Title, ["fields"] = Jsonx.Node(d.FieldsJson).DeepClone(),
            ["tags"] = Jsonx.Array(d.TagsJson).DeepClone(), ["parts"] = parts,
            ["createdAt"] = d.CreatedAt, ["updatedAt"] = d.UpdatedAt
        };
    }

    // Only PersistAsync can know whether the Commit existed before this call; comparing the
    // idempotency key alone would mark the *first* write with a key as a replay too.
    private static bool IsReplay(Commit commit, WriteContext ctx) => ctx.Replayed;
}
