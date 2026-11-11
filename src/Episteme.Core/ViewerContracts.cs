using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Episteme.Core;

/// <summary>P3: the four first-class media kinds. A document carries 1..N Parts; a Viewer renders one Part.</summary>
public static class ViewerKinds
{
    public static readonly string[] All = { "md", "code", "pdf", "web", "attachment" };

    public static bool IsKnown(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>PDF originals are immutable; edits are annotations. Web snapshots may be re-fetched.</summary>
    public static bool Editable(string kind) => kind is "md" or "code" or "web";

    public static bool Binary(string kind) => kind is "pdf" or "attachment";

    public static string Label(string kind) => kind switch
    {
        "md" => "Markdown",
        "code" => "代码",
        "pdf" => "PDF",
        "web" => "网页快照",
        _ => "附件"
    };
}

/// <summary>Content helpers shared by readers, anchors and diff. Kept dependency-free on purpose.</summary>
public static class PartContent
{
    private static readonly Regex TagRe = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ScriptRe = new(
        "<(script|style|noscript)\\b[^>]*>[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpaceRe = new("[ \\t\\r\\n\\f\\v]+", RegexOptions.Compiled);

    public static string Text(string? content) => (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    public static int LineCount(string? content)
    {
        var text = Text(content);
        return text.Length == 0 ? 0 : text.Split('\n').Length;
    }

    public static JsonArray Lines(string? content, int from, int to)
    {
        var all = Text(content).Split('\n');
        var start = Math.Max(1, from);
        var end = Math.Min(all.Length, to < 1 ? all.Length : to);
        var arr = new JsonArray();
        for (var i = start; i <= end; i++)
            arr.Add(new JsonObject { ["index"] = i, ["text"] = all[i - 1] });
        return arr;
    }

    /// <summary>PDF text layer is stored as plain text split by form-feed page breaks.</summary>
    public static string[] Pages(string? content) => Text(content).Split('\f');

    public static int PageCount(string? content) => Pages(content).Length;

    public static string PageText(string? content, int page)
    {
        var pages = Pages(content);
        return page >= 1 && page <= pages.Length ? pages[page - 1] : "";
    }

    /// <summary>Readability-lite: strip scripts/styles/tags, decode the common entities, collapse spaces.</summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = ScriptRe.Replace(html, " ");
        text = TagRe.Replace(text, " ");
        text = text.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
                   .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        return SpaceRe.Replace(text, " ").Trim();
    }

    public static string ReaderText(string kind, string? content) =>
        kind == "web" ? PlainText(content) : Text(content);

    public static int WordCount(string? content)
    {
        var text = ReaderText("md", content);
        return text.Length == 0 ? 0 : SpaceRe.Split(text).Length;
    }

    public static string Fingerprint(string? content) => Canonical.Hash(Text(content));

    /// <summary>Normalize whitespace for fuzzy quote matching, keeping a map back to original offsets.</summary>
    public static (string Text, int[] Map) NormalizeWithMap(string text)
    {
        var sb = new StringBuilder();
        var map = new List<int>(text.Length);
        var pendingSpace = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                map.Add(i);
                pendingSpace = false;
            }
            sb.Append(c);
            map.Add(i);
        }
        return (sb.ToString(), map.ToArray());
    }
}

/// <summary>One Viewer contract: mount / read / edit / annotate / snapshot / capabilities.</summary>
public static class ViewerRegistry
{
    public static JsonObject Describe(string kind)
    {
        // Unknown kinds degrade to the attachment viewer; the Server layer rejects them on write.
        if (!ViewerKinds.IsKnown(kind)) kind = "attachment";

        var anchor = new JsonObject
        {
            ["version"] = 1,
            ["required"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["fallback"] = "text-quote"
        };
        switch (kind)
        {
            case "md":
                anchor["required"] = new JsonArray("textQuote.exact", "或 blockIndex", "或 line");
                anchor["fields"] = new JsonArray("textQuote", "textPosition", "line", "blockIndex");
                break;
            case "code":
                anchor["required"] = new JsonArray("line", "或 lineRange");
                anchor["fields"] = new JsonArray("line", "lineRange", "textQuote", "textPosition");
                anchor["fallback"] = "line";
                break;
            case "pdf":
                anchor["required"] = new JsonArray("page");
                anchor["fields"] = new JsonArray("page", "rect", "textQuote", "textPosition", "pdfFingerprint");
                anchor["fallback"] = "page-rect";
                break;
            case "web":
                anchor["required"] = new JsonArray("domPath", "或 textQuote.exact");
                anchor["fields"] = new JsonArray("domPath", "textQuote", "textPosition", "sourceUri");
                anchor["fallback"] = "dom-path";
                break;
            default:
                anchor["required"] = new JsonArray("textQuote.exact", "或 offset");
                anchor["fields"] = new JsonArray("offset", "textQuote");
                anchor["fallback"] = "offset";
                break;
        }

        return new JsonObject
        {
            ["kind"] = kind,
            ["label"] = ViewerKinds.Label(kind),
            ["contract"] = new JsonArray("mount", "read", "edit", "annotate", "snapshot", "capabilities", "destroy"),
            ["capabilities"] = new JsonObject
            {
                ["editable"] = ViewerKinds.Editable(kind),
                ["annotatable"] = true,
                ["searchable"] = kind != "attachment",
                ["exportable"] = true,
                ["binary"] = ViewerKinds.Binary(kind),
                ["snapshotInCommit"] = true
            },
            ["anchor"] = anchor,
            ["renderers"] = kind switch
            {
                "md" => new JsonArray("markdown-preview", "textarea"),
                "code" => new JsonArray("monaco", "textarea-fallback", "code-lines"),
                "pdf" => new JsonArray("pdfjs-canvas", "text-layer"),
                "web" => new JsonArray("readability", "plain-text", "source-snapshot"),
                _ => new JsonArray("metadata")
            },
            ["mime"] = kind switch
            {
                "md" => "text/markdown", "code" => "text/plain",
                "pdf" => "application/pdf", "web" => "text/html", _ => "application/octet-stream"
            },
            ["note"] = kind switch
            {
                "md" => "源码 / 预览双模式；行内批注锚定 textQuote",
                "code" => "Monaco 编辑器（不可用时降级 textarea）；行号 + 语法高亮，与 blame 联动",
                "pdf" => "二进制原件由本地 pdf.js 渲染到 canvas；文本层 Part 保留 \\f 分页；原件只读",
                "web" => "fetch-worker + Readability 抽正文并白名单清洗；正文 / 纯文本 / 源码三态，不执行脚本",
                _ => "未识别媒介降级为附件元信息预览"
            }
        };
    }

    public static JsonArray All()
    {
        var arr = new JsonArray();
        foreach (var kind in ViewerKinds.All) arr.Add(Describe(kind));
        return arr;
    }
}

/// <summary>
/// Robust anchors: normalize on write, relocate on read with a four-level fallback chain.
/// A lost anchor is never silently dropped — it is reported as ANNOTATION_ANCHOR_LOST.
/// </summary>
public static class AnchorEngine
{
    public static readonly string[] AnnotationKinds =
        { "highlight", "underline", "note", "area", "bookmark" };

    public static (JsonObject Anchor, List<string> Errors) Normalize(
        string partKind, JsonObject input, string? content, JsonObject? meta)
    {
        var errors = new List<string>();
        if (!ViewerKinds.IsKnown(partKind))
            errors.Add($"未知 Part 类型：{partKind}");

        var declared = Str(input, "kind");
        if (!string.IsNullOrWhiteSpace(declared) && declared != partKind)
            errors.Add($"anchor.kind={declared} 与 Part 类型 {partKind} 不一致");

        var quote = input["textQuote"] as JsonObject;
        var exact = quote is null ? "" : Str(quote, "exact");
        var prefix = quote is null ? "" : Str(quote, "prefix");
        var suffix = quote is null ? "" : Str(quote, "suffix");

        var anchor = new JsonObject
        {
            ["version"] = 1,
            ["kind"] = partKind,
            ["partKind"] = partKind,
            ["fingerprint"] = PartContent.Fingerprint(content),
            ["status"] = "anchored"
        };

        var text = PartContent.ReaderText(partKind, content);
        var (start, end) = Locate(text, exact, prefix, suffix);
        if (!string.IsNullOrWhiteSpace(exact) && start < 0 && partKind is not "pdf")
            errors.Add($"textQuote.exact 未在正文中找到：{exact}");

        if (start >= 0)
        {
            anchor["textPosition"] = new JsonObject { ["start"] = start, ["end"] = end };
            var tq = new JsonObject { ["exact"] = text.Substring(start, end - start) };
            if (!string.IsNullOrWhiteSpace(prefix)) tq["prefix"] = prefix;
            if (!string.IsNullOrWhiteSpace(suffix)) tq["suffix"] = suffix;
            anchor["textQuote"] = tq;
        }
        else if (!string.IsNullOrWhiteSpace(exact))
        {
            var tq = new JsonObject { ["exact"] = exact };
            if (!string.IsNullOrWhiteSpace(prefix)) tq["prefix"] = prefix;
            if (!string.IsNullOrWhiteSpace(suffix)) tq["suffix"] = suffix;
            anchor["textQuote"] = tq;
            if (input["textPosition"] is JsonObject pos) anchor["textPosition"] = pos.DeepClone();
        }

        switch (partKind)
        {
            case "md":
            {
                var blockIndex = Int(input, "blockIndex", -1);
                var line = Int(input, "line", -1);
                if (start < 0 && blockIndex < 0 && line < 1)
                    errors.Add("md 锚点需要 textQuote.exact / blockIndex / line 至少一项");
                if (blockIndex >= 0) anchor["blockIndex"] = blockIndex;
                if (line >= 1)
                {
                    var count = PartContent.LineCount(content);
                    if (line > Math.Max(1, count)) errors.Add($"line={line} 超出正文行数 {count}");
                    anchor["line"] = line;
                }
                anchor["fallback"] = "block-or-line";
                break;
            }
            case "code":
            {
                var line = Int(input, "line", -1);
                var range = input["lineRange"] as JsonObject;
                var first = range is null ? line : Int(range, "start", line);
                var last = range is null ? line : Int(range, "end", line);
                if (first < 1) errors.Add("code 锚点需要 line >= 1 或 lineRange.start");
                if (last < first) errors.Add("lineRange.end 不能小于 start");
                var count = PartContent.LineCount(content);
                if (last > Math.Max(1, count)) errors.Add($"lineRange.end={last} 超出代码行数 {count}");
                if (first >= 1 && last >= first)
                {
                    anchor["line"] = first;
                    anchor["lineRange"] = new JsonObject { ["start"] = first, ["end"] = last };
                    if (string.IsNullOrWhiteSpace(exact))
                    {
                        var lines = PartContent.Lines(content, first, last);
                        anchor["textQuote"] = new JsonObject
                        {
                            ["exact"] = string.Join("\n", lines.OfType<JsonObject>().Select(o => Str(o, "text")))
                        };
                    }
                }
                anchor["fallback"] = "line";
                break;
            }
            case "pdf":
            {
                var page = Int(input, "page", 0);
                if (page < 1) errors.Add("pdf 锚点需要 page >= 1");
                var pages = meta is null ? 0 : Int(meta, "pages", 0);
                if (pages > 0 && page > pages) errors.Add($"page={page} 超出 PDF 页数 {pages}");
                if (page >= 1) anchor["page"] = page;
                if (input["rect"] is JsonObject rect)
                {
                    var r = new JsonObject();
                    foreach (var key in new[] { "x", "y", "w", "h" })
                        if (rect[key] is JsonValue v && v.TryGetValue<double>(out var d)) r[key] = d;
                    if (r.Count == 4) anchor["rect"] = r;
                    else errors.Add("rect 需要 x / y / w / h 四个数值");
                }
                if (!string.IsNullOrWhiteSpace(Str(input, "pdfFingerprint")))
                    anchor["pdfFingerprint"] = Str(input, "pdfFingerprint");
                anchor["fallback"] = "page-rect";
                break;
            }
            case "web":
            {
                var domPath = Str(input, "domPath");
                if (string.IsNullOrWhiteSpace(domPath) && start < 0)
                    errors.Add("web 锚点需要 domPath 或 textQuote.exact");
                if (!string.IsNullOrWhiteSpace(domPath)) anchor["domPath"] = domPath;
                if (!string.IsNullOrWhiteSpace(Str(input, "sourceUri"))) anchor["sourceUri"] = Str(input, "sourceUri");
                anchor["fallback"] = "dom-path";
                break;
            }
            default:
            {
                var offset = Int(input, "offset", -1);
                if (start < 0 && offset < 0) errors.Add("attachment 锚点需要 textQuote.exact 或 offset");
                if (offset >= 0) anchor["offset"] = offset;
                anchor["fallback"] = "offset";
                break;
            }
        }

        if (errors.Count > 0) anchor["status"] = "invalid";
        return (anchor, errors);
    }

    /// <summary>Level 1..4 fallback. Returns a resolution report; callers must surface "lost" to the UI.</summary>
    public static JsonObject Resolve(JsonObject anchor, string? content)
    {
        var kind = Str(anchor, "kind");
        var text = PartContent.ReaderText(kind, content);
        var current = PartContent.Fingerprint(content);
        var sameDocument = Str(anchor, "fingerprint") == current;
        var exact = anchor["textQuote"] is JsonObject q ? Str(q, "exact") : "";
        var prefix = anchor["textQuote"] is JsonObject qp ? Str(qp, "prefix") : "";
        var suffix = anchor["textQuote"] is JsonObject qs ? Str(qs, "suffix") : "";

        // L1: fingerprint + textPosition
        if (sameDocument && anchor["textPosition"] is JsonObject pos)
        {
            var s = Int(pos, "start", -1);
            var e = Int(pos, "end", -1);
            if (s >= 0 && e > s && e <= text.Length &&
                (string.IsNullOrEmpty(exact) || text.Substring(s, Math.Min(e - s, text.Length - s)) == exact))
                return Resolution("anchored", "fingerprint-position", s, e, anchor);
        }

        // L2: text quote (exact, prefix+suffix, then whitespace-normalized fuzzy match)
        if (!string.IsNullOrWhiteSpace(exact))
        {
            var (s, e) = Locate(text, exact, prefix, suffix);
            if (s >= 0) return Resolution(sameDocument ? "anchored" : "relocated", "text-quote", s, e, anchor);
        }

        // L3: structural locator
        var page = Int(anchor, "page", 0);
        var line = Int(anchor, "line", 0);
        var domPath = Str(anchor, "domPath");
        if (page >= 1 && page <= PartContent.PageCount(content))
            return Resolution("relocated", "page-rect", -1, -1, anchor);
        if (line >= 1 && line <= Math.Max(1, PartContent.LineCount(content)))
            return Resolution("relocated", "line", -1, -1, anchor);
        if (!string.IsNullOrWhiteSpace(domPath) || Int(anchor, "blockIndex", -1) >= 0)
            return Resolution("relocated", "dom-path", -1, -1, anchor);

        // L4: never silently drop an annotation
        return new JsonObject
        {
            ["status"] = "lost", ["strategy"] = "none",
            ["code"] = "ANNOTATION_ANCHOR_LOST",
            ["message"] = "锚点无法重定位，请在阅读器中重新定位该标记"
        };
    }

    private static JsonObject Resolution(string status, string strategy, int start, int end, JsonObject anchor)
    {
        var result = new JsonObject { ["status"] = status, ["strategy"] = strategy };
        if (start >= 0) { result["start"] = start; result["end"] = end; }
        if (Int(anchor, "page", 0) >= 1) result["page"] = Int(anchor, "page", 0);
        if (Int(anchor, "line", 0) >= 1) result["line"] = Int(anchor, "line", 0);
        return result;
    }

    /// <summary>Exact → prefix/suffix disambiguation → whitespace-normalized fuzzy match.</summary>
    public static (int Start, int End) Locate(string text, string exact, string prefix, string suffix)
    {
        if (string.IsNullOrWhiteSpace(exact) || string.IsNullOrEmpty(text)) return (-1, -1);
        var index = text.IndexOf(exact, StringComparison.Ordinal);
        if (index < 0) index = text.IndexOf(exact, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && (!string.IsNullOrEmpty(prefix) || !string.IsNullOrEmpty(suffix)))
        {
            var needle = prefix + exact + suffix;
            var at = text.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0) index = at + prefix.Length;
        }
        if (index >= 0) return (index, index + exact.Length);

        var (normalized, map) = PartContent.NormalizeWithMap(text);
        var (normExact, _) = PartContent.NormalizeWithMap(exact);
        var fuzzy = normalized.IndexOf(normExact, StringComparison.OrdinalIgnoreCase);
        if (fuzzy >= 0 && fuzzy + normExact.Length <= map.Length)
        {
            var s = map[fuzzy];
            var e = map[fuzzy + normExact.Length - 1] + 1;
            return (s, Math.Min(e, text.Length));
        }
        return (-1, -1);
    }

    private static string Str(JsonObject o, string key) => Jsonx.Plain(o[key]);

    private static int Int(JsonObject o, string key, int fallback) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : fallback;
}
