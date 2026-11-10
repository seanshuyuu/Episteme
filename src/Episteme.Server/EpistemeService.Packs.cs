using System.Text.Json;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

/// <summary>
/// P4: Pack catalog, install (one transaction / one import Commit), export and import.
/// Packs are pure data; no user-supplied code is ever executed.
/// </summary>
public sealed partial class EpistemeService
{
    private static string PacksDirectory => Path.Combine(AppContext.BaseDirectory, "wwwroot", "packs");

    private static JsonObject? ReadPackFile(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch { return null; }
    }

    private static JsonObject? BuiltinPack(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        // Fast path by filename; Path.GetFileName also blocks traversal.
        var safe = Path.GetFileName(id);
        if (safe == id)
        {
            var direct = Path.Combine(PacksDirectory, safe + ".json");
            if (File.Exists(direct)) return ReadPackFile(direct);
        }
        // A Pack's id is authoritative, not its filename.
        if (Directory.Exists(PacksDirectory))
            foreach (var file in Directory.GetFiles(PacksDirectory, "*.json"))
            {
                var pack = ReadPackFile(file);
                if (pack is not null && PackEngine.Str(pack, "id") == id) return pack;
            }
        return null;
    }

    public Task<JsonObject> ListPacksAsync(CancellationToken ct)
    {
        var arr = new JsonArray();
        if (Directory.Exists(PacksDirectory))
        {
            foreach (var file in Directory.GetFiles(PacksDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var raw = ReadPackFile(file);
                if (raw is null) continue;
                var (pack, errors) = PackEngine.Validate(raw);
                var summary = PackEngine.Summary(pack);
                summary["builtin"] = true;
                summary["file"] = Path.GetFileName(file);
                summary["valid"] = errors.Count == 0;
                if (errors.Count > 0) summary["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
                arr.Add(summary);
            }
        }
        return Task.FromResult(new JsonObject
        {
            ["items"] = arr, ["total"] = arr.Count,
            ["schema"] = PackEngine.SchemaId,
            ["note"] = "Pack 是纯数据（目录 + Schema + 视图 + 可选示例），安装产生一条 source=import 的 Commit"
        });
    }

    public Task<JsonObject> GetPackAsync(string id, CancellationToken ct)
    {
        var raw = BuiltinPack(id)
                  ?? throw new BusinessException("PACK_NOT_FOUND", $"内置 Pack 不存在：{id}", 404);
        var (pack, errors) = PackEngine.Validate(raw);
        return Task.FromResult(new JsonObject
        {
            ["id"] = PackEngine.Str(pack, "id"), ["pack"] = pack,
            ["summary"] = PackEngine.Summary(pack),
            ["valid"] = errors.Count == 0,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray())
        });
    }

    private static JsonObject ResolveInstallPack(JsonObject args)
    {
        if (args["pack"] is JsonObject inline) return inline;
        var id = S(args, "packId");
        if (string.IsNullOrWhiteSpace(id))
            throw new BusinessException("PACK_ID_REQUIRED", "请提供 packId 或 pack 内联内容", 422);
        return BuiltinPack(id)
               ?? throw new BusinessException("PACK_NOT_FOUND", $"内置 Pack 不存在：{id}", 404);
    }

    private static bool Flag(JsonObject args, string key, bool fallback = false) =>
        args[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    /// <summary>Declared paths plus every implicit ancestor, ordered shallow to deep.</summary>
    private static List<string> NeededPaths(JsonArray packNodes)
    {
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in packNodes)
        {
            if (n is not JsonObject no) continue;
            var path = NormalizePath(PackEngine.Str(no, "path"));
            needed.Add(path);
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                var parent = path[..slash];
                needed.Add(parent);
                slash = parent.LastIndexOf('/');
            }
        }
        return needed.OrderBy(p => p.Count(c => c == '/'))
            .ThenBy(p => p, StringComparer.Ordinal).ToList();
    }

    public async Task<JsonObject> InstallPackAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner");
        var raw = ResolveInstallPack(args);
        var (pack, errors) = PackEngine.Validate(raw);
        if (errors.Count > 0)
            throw new BusinessException("PACK_INVALID", string.Join("；", errors), 422);

        var includeSamples = Flag(args, "includeSamples");
        var overwrite = Flag(args, "overwrite");
        var packTypes = pack["contentTypes"] as JsonArray ?? new JsonArray();
        var packNodes = pack["nodes"] as JsonArray ?? new JsonArray();

        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var existingSlugs = (await db.ContentTypes.Where(c => c.SpaceId == spaceId)
            .Select(c => c.Slug).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var existingPaths = (await db.Nodes.Where(n => n.SpaceId == spaceId)
            .Select(n => n.Path).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var conflicts = new JsonArray();
        var newTypeCount = 0;
        foreach (var t in packTypes)
        {
            if (t is not JsonObject to) continue;
            var slug = S(to, "slug");
            if (existingSlugs.Contains(slug)) conflicts.Add("type:" + slug);
            else newTypeCount++;
        }
        var neededPaths = NeededPaths(packNodes);
        var newNodeCount = neededPaths.Count(p => !existingPaths.Contains(p));
        foreach (var n in packNodes)
        {
            if (n is not JsonObject no) continue;
            var path = NormalizePath(S(no, "path"));
            if (existingPaths.Contains(path)) conflicts.Add("node:" + path);
        }

        var summary = PackEngine.Summary(pack);
        if (ctx.DryRun)
            return new JsonObject
            {
                ["dryRun"] = true, ["pack"] = summary, ["conflicts"] = conflicts,
                ["overwrite"] = overwrite, ["wouldCreateContentTypes"] = newTypeCount,
                ["wouldCreateNodes"] = newNodeCount,
                ["wouldCreateSampleDocuments"] = includeSamples
                    ? (pack["sampleDocuments"] as JsonArray)?.Count ?? 0 : 0,
                ["commitId"] = null,
                ["note"] = "预览模式未写入任何数据"
            };

        // A replay must short-circuit before conflict detection: the objects created by the
        // original call are already present, so they would otherwise look like a PACK_CONFLICT.
        var prior = await FindIdempotentAsync(db, spaceId, ctx, ct);
        if (prior is not null)
            return new JsonObject
            {
                ["packId"] = S(pack, "id"), ["packVersion"] = S(pack, "version"),
                ["createdContentTypes"] = 0, ["createdNodes"] = 0,
                ["reboundNodes"] = 0, ["createdSampleDocuments"] = 0,
                ["conflicts"] = conflicts, ["commitId"] = prior.Id,
                ["commitSource"] = prior.Source, ["idempotentReplay"] = true
            };

        if (conflicts.Count > 0 && !overwrite)
            throw new BusinessException("PACK_CONFLICT",
                $"目标空间已有 {conflicts.Count} 项同名对象（{string.Join("、", conflicts.Select(c => Jsonx.Plain(c)))}）；如需覆盖请传 overwrite=true", 409);

        // Content types first, so nodes can bind to the freshly versioned rows.
        // Keep the pending rows in memory too: sample documents need their definitions
        // before SaveChanges makes them queryable.
        var typeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var typeRows = new Dictionary<string, ContentType>(StringComparer.Ordinal);
        foreach (var t in packTypes)
        {
            if (t is not JsonObject to) continue;
            var def = Jsonx.Obj<ContentTypeDefinition>(to.ToJsonString())
                      ?? throw new BusinessException("PACK_INVALID", "contentType 定义无法解析", 422);
            var existing = await db.ContentTypes
                .Where(c => c.SpaceId == spaceId && c.Slug == def.Slug)
                .OrderByDescending(c => c.Version).FirstOrDefaultAsync(ct);
            def.Version = existing is null ? 1 : existing.Version + 1;
            var rowId = Ids.New("ct");
            var row = new ContentType
            {
                Id = rowId, SpaceId = spaceId, Slug = def.Slug, Label = def.Label,
                Version = def.Version, DefinitionJson = Jsonx.Str(def), ExposeToMcp = def.ExposeToMcp
            };
            db.ContentTypes.Add(row);
            typeIds[def.Slug] = rowId;
            typeRows[rowId] = row;
        }

        // Materialize the whole path set in memory first. Calling EnsureParentsAsync per node
        // would query a DbContext whose pending additions are not yet visible, creating duplicates.
        var existingNodes = await db.Nodes.Where(n => n.SpaceId == spaceId).ToListAsync(ct);
        var nodeByPath = existingNodes.ToDictionary(n => n.Path, n => n, StringComparer.Ordinal);
        var createdNodes = 0;
        foreach (var path in neededPaths)
        {
            if (nodeByPath.ContainsKey(path)) continue;
            var slash = path.LastIndexOf('/');
            var parentPath = slash > 0 ? path[..slash] : "";
            var row = new Node
            {
                Id = Ids.New("nd"), SpaceId = spaceId, Path = path,
                Name = path.TrimEnd('/').Split('/').Last(), Icon = "folder",
                ExposeToMcp = true,
                ParentId = parentPath.Length > 0 ? nodeByPath.GetValueOrDefault(parentPath)?.Id : null
            };
            db.Nodes.Add(row);
            nodeByPath[path] = row;
            createdNodes++;
        }

        var reboundNodes = 0;
        foreach (var n in packNodes)
        {
            if (n is not JsonObject no) continue;
            var path = NormalizePath(S(no, "path"));
            if (!nodeByPath.TryGetValue(path, out var row)) continue;
            var slug = S(no, "contentType");
            if (slug.Length > 0 && typeIds.TryGetValue(slug, out var typeId))
            {
                row.ContentTypeId = typeId;
                reboundNodes++;
            }
            if (no["icon"] is not null) row.Icon = S(no, "icon", "folder");
            var kinds = no["defaultPartKinds"] as JsonArray;
            var defaultKind = kinds is { Count: > 0 } ? Jsonx.Plain(kinds[0]) : null;
            if (string.IsNullOrWhiteSpace(defaultKind)) defaultKind = S(no, "defaultPartKind");
            row.DefaultPartKind = string.IsNullOrWhiteSpace(defaultKind) ? null : defaultKind;
            row.ExposeToMcp = Flag(no, "exposeToMcp", true);
        }

        var createdSamples = 0;
        if (includeSamples && pack["sampleDocuments"] is JsonArray samples)
        {
            foreach (var s in samples)
            {
                if (s is not JsonObject so) continue;
                var nodePath = NormalizePath(S(so, "node"));
                if (!nodeByPath.TryGetValue(nodePath, out var node)) continue;
                var typeSlug = S(so, "type");
                var typeId = typeIds.GetValueOrDefault(typeSlug) ?? node.ContentTypeId;
                if (typeId is null) continue;
                var typeRow = typeRows.GetValueOrDefault(typeId);
                var def = typeRow is null ? null : Jsonx.Obj<ContentTypeDefinition>(typeRow.DefinitionJson);
                var fields = (so["fields"] as JsonObject ?? new JsonObject()).DeepClone() as JsonObject ?? new JsonObject();
                if (def is not null)
                {
                    // Sample documents declare their title at the top level; mirror it into the
                    // primary field so the same Schema validation used by normal writes applies.
                    if (!fields.ContainsKey(def.PrimaryField) && !string.IsNullOrWhiteSpace(S(so, "title")))
                        fields[def.PrimaryField] = S(so, "title");
                    SchemaEngine.ApplyDefaults(def, fields);
                    var fieldErrors = SchemaEngine.Validate(def, fields);
                    if (fieldErrors.Count > 0)
                        throw new BusinessException("PACK_SAMPLE_INVALID",
                            $"示例文档 {S(so, "title")}：{string.Join("；", fieldErrors)}", 422);
                }
                var doc = new Document
                {
                    Id = Ids.New("doc"), SpaceId = spaceId, NodeId = node.Id,
                    ContentTypeId = typeId, SchemaVersion = typeRow?.Version ?? 1,
                    Branch = WriteContext.RefName(ctx.Branch), Title = S(so, "title", "未命名文档"),
                    FieldsJson = fields.ToJsonString(), TagsJson = "[]"
                };
                var ord = 0;
                if (so["parts"] is JsonArray parts)
                {
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
                }
                db.Documents.Add(doc);
                createdSamples++;
            }
        }

        ctx.Source = "import";
        ctx.Action = "install_pack";
        space.ToolsVersion++;
        var commit = await PersistAsync(db, space, ctx,
            $"安装 Pack {S(pack, "id")} v{S(pack, "version")}", ct);
        var replay = IsReplay(commit, ctx);
        return new JsonObject
        {
            ["packId"] = S(pack, "id"), ["packVersion"] = S(pack, "version"),
            ["createdContentTypes"] = replay ? 0 : typeIds.Count,
            ["createdNodes"] = replay ? 0 : createdNodes,
            ["reboundNodes"] = replay ? 0 : reboundNodes,
            ["createdSampleDocuments"] = replay ? 0 : createdSamples,
            ["conflicts"] = conflicts, ["commitId"] = commit.Id,
            ["commitSource"] = commit.Source, ["toolsVersion"] = space.ToolsVersion,
            ["idempotentReplay"] = replay
        };
    }

    public Task<JsonObject> ImportPackAsync(string spaceId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        if (args["pack"] is not JsonObject inline)
            throw new BusinessException("PACK_INLINE_REQUIRED", "导入需提供 pack 内联 JSON（或用 packId 安装内置模板）", 422);
        var normalized = inline.DeepClone() as JsonObject ?? new JsonObject();
        var installArgs = new JsonObject
        {
            ["pack"] = normalized,
            ["includeSamples"] = Flag(args, "includeSamples"),
            ["overwrite"] = Flag(args, "overwrite")
        };
        return InstallPackAsync(spaceId, installArgs, ctx, ct);
    }

    public async Task<JsonObject> ExportPackAsync(string spaceId, JsonObject args, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var includeDocuments = Flag(args, "includeDocuments");

        var types = await LatestDefinitionsAsync(db, spaceId, ct);
        var nodes = await db.Nodes.Where(n => n.SpaceId == spaceId)
            .OrderBy(n => n.Path).ToListAsync(ct);
        var typeById = types.ToDictionary(t => t.Row.Id, t => t.Def.Slug, StringComparer.Ordinal);

        var typeArray = new JsonArray();
        foreach (var (_, def) in types)
            typeArray.Add(JsonSerializer.SerializeToNode(def, Jsonx.Opts) ?? new JsonObject());
        var nodeArray = new JsonArray();
        foreach (var n in nodes)
        {
            var item = new JsonObject
            {
                ["path"] = n.Path, ["icon"] = n.Icon, ["exposeToMcp"] = n.ExposeToMcp
            };
            if (n.ContentTypeId is not null && typeById.TryGetValue(n.ContentTypeId, out var slug))
                item["contentType"] = slug;
            if (!string.IsNullOrWhiteSpace(n.DefaultPartKind))
                item["defaultPartKinds"] = new JsonArray(n.DefaultPartKind);
            nodeArray.Add(item);
        }

        var sampleArray = new JsonArray();
        if (includeDocuments)
        {
            var docs = await db.Documents.Include(d => d.Parts)
                .Where(d => d.SpaceId == spaceId && d.Branch == "refs/heads/main").ToListAsync(ct);
            var pathById = nodes.ToDictionary(n => n.Id, n => n.Path, StringComparer.Ordinal);
            foreach (var d in docs)
            {
                var parts = new JsonArray();
                foreach (var p in d.Parts.OrderBy(p => p.Ord))
                    parts.Add(new JsonObject
                    {
                        ["kind"] = p.Kind, ["content"] = p.Content,
                        ["sourceUri"] = p.SourceUri, ["language"] = p.Language,
                        ["meta"] = Jsonx.Node(p.MetaJson)
                    });
                sampleArray.Add(new JsonObject
                {
                    ["node"] = pathById.GetValueOrDefault(d.NodeId, "/"),
                    ["type"] = typeById.GetValueOrDefault(d.ContentTypeId, ""),
                    ["title"] = d.Title, ["fields"] = Jsonx.Node(d.FieldsJson),
                    ["parts"] = parts
                });
            }
        }

        var exportId = "export-" + new string(space.Slug.ToLowerInvariant()
            .Select(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' ? ch : '-')
            .ToArray());
        var pack = new JsonObject
        {
            ["$schema"] = PackEngine.SchemaId,
            ["id"] = exportId,
            ["name"] = S(args, "name", space.Name + " 导出模板"),
            ["version"] = "1.0.0", ["episteme"] = ">=0.3.0",
            ["description"] = S(args, "description", $"从空间 {space.Name} 导出的目录与 Schema 模板"),
            ["author"] = "local", ["exportedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["sourceSpace"] = space.Id, ["sampleData"] = includeDocuments,
            ["nodes"] = nodeArray, ["contentTypes"] = typeArray,
            ["views"] = new JsonArray(), ["prompts"] = new JsonArray(), ["jobs"] = new JsonArray(),
            ["sampleDocuments"] = sampleArray
        };
        var (validated, validationErrors) = PackEngine.Validate(pack);
        return new JsonObject
        {
            ["pack"] = validated, ["json"] = validated.ToJsonString(),
            ["summary"] = PackEngine.Summary(validated),
            ["valid"] = validationErrors.Count == 0,
            ["errors"] = new JsonArray(validationErrors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["note"] = "导出为纯数据 JSON，可用 POST /packs/import 安装到任意空间"
        };
    }
}
