using System.Text;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

/// <summary>P3: Viewer contract endpoints, robust-anchor resolution and annotation export.</summary>
public sealed partial class EpistemeService
{
    public Task<JsonObject> ViewersAsync(CancellationToken ct)
    {
        var viewers = ViewerRegistry.All();
        return Task.FromResult(new JsonObject
        {
            ["contractVersion"] = 1,
            ["contract"] = new JsonArray("mount", "read", "edit", "annotate", "snapshot", "capabilities", "destroy"),
            ["items"] = viewers,
            ["total"] = viewers.Count,
            ["note"] = "文档是载体，格式是 Part；四类阅读器共享同一套读取 / 编辑 / 标注 / 快照契约"
        });
    }

    /// <summary>Per-Part viewer descriptors + live anchor resolution. ?ref= renders an immutable snapshot read-only.</summary>
    public async Task<JsonObject> DocumentViewersAsync(
        string spaceId, string docId, JsonObject query, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var reference = S(query, "ref");
        JsonObject? snapshot = null;
        if (!string.IsNullOrWhiteSpace(reference))
            snapshot = await ResolveSnapshotAsync(db, spaceId, reference, docId, ct);

        var parts = new List<(string Id, string Kind, int Ord, string? Content, string? SourceUri,
                              string? Language, JsonObject Meta)>();
        var annotations = new List<Annotation>();

        if (snapshot is not null)
        {
            var ord = 0;
            if (snapshot["parts"] is JsonArray snapParts)
                foreach (var p in snapParts.OfType<JsonObject>())
                    parts.Add((Jsonx.Plain(p["id"]), Jsonx.Plain(p["kind"]), ord++,
                               p["content"] is null ? null : Jsonx.Plain(p["content"]),
                               p["sourceUri"] is null ? null : Jsonx.Plain(p["sourceUri"]),
                               p["language"] is null ? null : Jsonx.Plain(p["language"]),
                               p["meta"] as JsonObject ?? new JsonObject()));
            if (snapshot["annotations"] is JsonArray snapAnns)
                foreach (var a in snapAnns.OfType<JsonObject>())
                    annotations.Add(new Annotation
                    {
                        Id = Jsonx.Plain(a["id"]), PartId = Jsonx.Plain(a["partId"]),
                        Kind = Jsonx.Plain(a["kind"]), Text = Jsonx.Plain(a["text"]),
                        AnchorJson = a["anchor"] is JsonObject an ? an.ToJsonString() : "{}",
                        Author = Jsonx.Plain(a["author"]), Color = a["color"] is null ? null : Jsonx.Plain(a["color"])
                    });
        }
        else
        {
            var doc = await db.Documents.Include(d => d.Parts)
                .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
                ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);
            foreach (var p in doc.Parts.OrderBy(p => p.Ord))
                parts.Add((p.Id, p.Kind, p.Ord, p.Content, p.SourceUri, p.Language, Jsonx.Node(p.MetaJson)));
            var partIds = parts.Select(p => p.Id).ToList();
            annotations = await db.Annotations.Where(a => partIds.Contains(a.PartId)).ToListAsync(ct);
        }

        var items = new JsonArray();
        var lost = 0;
        foreach (var part in parts)
        {
            var viewer = ViewerRegistry.Describe(part.Kind);
            var text = PartContent.ReaderText(part.Kind, part.Content);
            var partAnns = annotations.Where(a => a.PartId == part.Id).OrderBy(a => a.CreatedAt).ToList();
            var binaryPdf = part.Kind == "pdf" && Jsonx.Plain(part.Meta["pdfFormat"]) == "binary";
            var binaryUrl = binaryPdf
                ? $"/api/v1/episteme/spaces/{spaceId}/documents/{docId}/parts/{part.Id}/binary"
                : "";
            var pageCount = binaryPdf
                ? Math.Max(1, IntOf(part.Meta, "pages"))
                : part.Kind == "pdf" ? PartContent.PageCount(part.Content) : 0;
            var annArr = new JsonArray();
            foreach (var a in partAnns)
            {
                var anchor = Jsonx.Node(a.AnchorJson);
                var resolution = AnchorEngine.Resolve(anchor, part.Content);
                if (Jsonx.Plain(resolution["status"]) == "lost") lost++;
                annArr.Add(AnnotationNode(a, part.Kind, resolution));
            }
            items.Add(new JsonObject
            {
                ["partId"] = part.Id,
                ["ord"] = part.Ord,
                ["kind"] = part.Kind,
                ["viewer"] = viewer,
                ["content"] = part.Content,
                ["sourceUri"] = part.SourceUri,
                ["language"] = part.Language,
                ["meta"] = part.Meta.DeepClone(),
                ["binaryUrl"] = binaryUrl,
                ["renderHint"] = binaryPdf ? "pdfjs-canvas" : part.Kind switch
                {
                    "code" => "monaco", "web" => "readability", "md" => "markdown-preview", _ => "metadata"
                },
                ["stats"] = new JsonObject
                {
                    ["lines"] = PartContent.LineCount(part.Content),
                    ["pages"] = pageCount,
                    ["words"] = PartContent.WordCount(text),
                    ["characters"] = text.Length,
                    ["fingerprint"] = PartContent.Fingerprint(part.Content)
                },
                ["annotations"] = annArr,
                ["annotationCount"] = annArr.Count
            });
        }

        return new JsonObject
        {
            ["docId"] = docId,
            ["ref"] = snapshot is null ? "refs/heads/main" : reference,
            ["readOnly"] = snapshot is not null,
            ["title"] = snapshot is null ? "" : Jsonx.Plain(snapshot["title"]),
            ["items"] = items,
            ["total"] = items.Count,
            ["annotationTotal"] = annotations.Count,
            ["anchorLost"] = lost,
            ["contractVersion"] = 1
        };
    }

    /// <summary>Read one Part in the shape its Viewer expects: lines / page / plain text / source.</summary>
    public async Task<JsonObject> ReadPartAsync(
        string spaceId, string docId, string partId, JsonObject query, CancellationToken ct)
    {
        await using var db = await Db(ct);
        await RequireSpace(db, spaceId, ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId && p.DocumentId == docId, ct)
                   ?? throw new BusinessException("PART_NOT_FOUND", "Part 不存在", 404);
        var mode = S(query, "mode", "read");
        var text = PartContent.ReaderText(part.Kind, part.Content);
        var result = new JsonObject
        {
            ["partId"] = part.Id, ["docId"] = docId, ["kind"] = part.Kind,
            ["viewer"] = ViewerRegistry.Describe(part.Kind),
            ["mode"] = mode, ["sourceUri"] = part.SourceUri, ["language"] = part.Language,
            ["meta"] = Jsonx.Node(part.MetaJson).DeepClone(),
            ["fingerprint"] = PartContent.Fingerprint(part.Content),
            ["stats"] = new JsonObject
            {
                ["lines"] = PartContent.LineCount(part.Content),
                ["pages"] = part.Kind == "pdf" ? PartContent.PageCount(part.Content) : 0,
                ["characters"] = text.Length
            }
        };
        switch (part.Kind)
        {
            case "code":
            case "md":
                result["lines"] = PartContent.Lines(part.Content, I(query, "from", 1), I(query, "to", 0));
                result["text"] = text;
                break;
            case "pdf":
            {
                var page = Math.Max(1, I(query, "page", 1));
                result["page"] = page;
                result["pageCount"] = PartContent.PageCount(part.Content);
                result["text"] = PartContent.PageText(part.Content, page);
                result["lines"] = PartContent.Lines(PartContent.PageText(part.Content, page), 1, 0);
                break;
            }
            case "web":
                result["plainText"] = PartContent.PlainText(part.Content);
                result["text"] = mode == "source" ? part.Content : text;
                break;
            default:
                result["text"] = text;
                break;
        }
        return result;
    }

    /// <summary>Export every annotation across Parts into a new md Part in one Commit.</summary>
    public async Task<JsonObject> ExportNotesAsync(
        string spaceId, string docId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var doc = await db.Documents.Include(d => d.Parts)
            .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
            ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);
        var partIds = doc.Parts.Select(p => p.Id).ToList();
        var rows = await db.Annotations.Where(a => partIds.Contains(a.PartId)).ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# 标注汇总 · {doc.Title}");
        sb.AppendLine();
        sb.AppendLine($"> 由 Episteme 导出 · {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · 共 {rows.Count} 条标记 · {doc.Parts.Count} 个 Part");
        sb.AppendLine();
        foreach (var part in doc.Parts.OrderBy(p => p.Ord))
        {
            var group = rows.Where(a => a.PartId == part.Id).OrderBy(a => a.CreatedAt).ToList();
            if (group.Count == 0) continue;
            sb.AppendLine($"## {ViewerKinds.Label(part.Kind)} · Part {part.Ord + 1}");
            sb.AppendLine();
            foreach (var a in group)
            {
                var anchor = Jsonx.Node(a.AnchorJson);
                var quote = anchor["textQuote"] is JsonObject q ? Jsonx.Plain(q["exact"]) : "";
                var where = new List<string>();
                if (IntOf(anchor, "page") >= 1) where.Add("p." + IntOf(anchor, "page"));
                if (IntOf(anchor, "line") >= 1) where.Add("L" + IntOf(anchor, "line"));
                if (Jsonx.Plain(anchor["domPath"]) is { Length: > 0 } dom) where.Add(dom);
                var loc = where.Count > 0 ? " `" + string.Join(" · ", where) + "`" : "";
                var kindLabel = a.Kind switch
                {
                    "highlight" => "高亮", "underline" => "下划线", "note" => "批注",
                    "area" => "区域", _ => "书签"
                };
                sb.AppendLine($"- **{kindLabel}**{loc} {(quote.Length > 0 ? "「" + quote.Replace("\n", " ") + "」" : "")}" +
                              $"{(string.IsNullOrWhiteSpace(a.Text) ? "" : " — " + a.Text)} · {a.Author}");
            }
            sb.AppendLine();
        }
        if (rows.Count == 0) sb.AppendLine("_（当前文档还没有标记）_").AppendLine();

        var note = new Part
        {
            Id = Ids.New("pt"), DocumentId = docId,
            Ord = doc.Parts.Count == 0 ? 0 : doc.Parts.Max(p => p.Ord) + 1,
            Kind = "md", Content = sb.ToString(), Mime = "text/markdown",
            MetaJson = new JsonObject
            {
                ["origin"] = "annotation-export", ["annotationCount"] = rows.Count,
                ["sourceDocId"] = docId
            }.ToJsonString()
        };
        db.Parts.Add(note);
        doc.UpdatedAt = DateTime.UtcNow;
        var commit = await PersistAsync(db, space, ctx, $"导出标注笔记（{rows.Count} 条）", ct);
        return new JsonObject
        {
            ["partId"] = note.Id, ["commitId"] = commit.Id,
            ["annotationCount"] = rows.Count, ["markdown"] = note.Content,
            ["idempotentReplay"] = IsReplay(commit, ctx)
        };
    }

    internal static JsonObject AnnotationNode(Annotation a, string partKind, JsonObject? resolution = null) => new()
    {
        ["id"] = a.Id, ["partId"] = a.PartId, ["partKind"] = partKind,
        ["kind"] = a.Kind, ["anchor"] = Jsonx.Node(a.AnchorJson).DeepClone(),
        ["text"] = a.Text, ["color"] = a.Color, ["author"] = a.Author,
        ["createdAt"] = a.CreatedAt,
        ["resolution"] = resolution is null
            ? AnchorEngine.Resolve(Jsonx.Node(a.AnchorJson), null)
            : (JsonObject)resolution.DeepClone()
    };

    private static int IntOf(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>Immutable snapshot payload for annotations; consumed by diff / revert / ?ref=.</summary>
    internal static JsonArray AnnotationSnapshot(IEnumerable<Annotation> rows)
    {
        var arr = new JsonArray();
        foreach (var a in rows.OrderBy(x => x.Id, StringComparer.Ordinal))
            arr.Add(new JsonObject
            {
                ["id"] = a.Id, ["partId"] = a.PartId, ["kind"] = a.Kind,
                ["anchor"] = Jsonx.Node(a.AnchorJson).DeepClone(), ["text"] = a.Text,
                ["color"] = a.Color, ["author"] = a.Author, ["createdAt"] = a.CreatedAt
            });
        return arr;
    }
}
