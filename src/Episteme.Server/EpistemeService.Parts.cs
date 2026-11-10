using System.Text.Json.Nodes;
using Episteme.Core;

namespace Episteme.Server;

public sealed partial class EpistemeService
{
    /// <summary>
    /// Single write path for all four media kinds: validates kind, derives mime / meta / blobHash.
    /// PDF binary lives outside this round; its text layer is stored as page-separated text.
    /// </summary>
    private static (string Kind, string Mime, JsonObject Meta, string BlobHash) NormalizePart(
        JsonObject args, string? content, string fallbackKind, JsonObject? existingMeta = null)
    {
        var kind = S(args, "kind", fallbackKind);
        if (!ViewerKinds.IsKnown(kind))
            throw new BusinessException("PART_KIND_INVALID",
                $"未知媒介类型 {kind}；可用：{string.Join(", ", ViewerKinds.All)}", 422);

        var meta = existingMeta is null
            ? new JsonObject()
            : (JsonObject)existingMeta.DeepClone();
        if (args["meta"] is JsonObject incoming)
            foreach (var kv in incoming) meta[kv.Key] = kv.Value?.DeepClone();

        var readable = PartContent.ReaderText(kind, content);
        switch (kind)
        {
            case "md":
                meta["lineCount"] = PartContent.LineCount(content);
                meta["wordCount"] = PartContent.WordCount(content);
                meta["frontMatter"] = false;
                break;
            case "code":
                meta["lineCount"] = PartContent.LineCount(content);
                meta["encoding"] = "utf-8";
                if (!string.IsNullOrWhiteSpace(S(args, "language"))) meta["language"] = S(args, "language");
                break;
            case "pdf":
                meta["pages"] = PartContent.PageCount(content);
                meta["textLayer"] = !string.IsNullOrWhiteSpace(content);
                meta["origin"] = "text-layer";
                meta["note"] = "P3 使用文本层阅读；canvas / pdf.js 渲染与 >100 MB 分片属于后续能力";
                break;
            case "web":
                meta["extractor"] = "readability-lite";
                meta["plainTextLength"] = readable.Length;
                if (meta["fetchedAt"] is null)
                    meta["fetchedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                if (!string.IsNullOrWhiteSpace(S(args, "sourceUri"))) meta["sourceUri"] = S(args, "sourceUri");
                meta["origin"] = "external";
                break;
            default:
                meta["size"] = (content ?? "").Length;
                break;
        }
        meta["fingerprint"] = PartContent.Fingerprint(content);

        var defaultMime = ViewerRegistry.Describe(kind)["mime"] is JsonValue mv &&
                          mv.TryGetValue<string>(out var ms) ? ms : "application/octet-stream";
        var mime = S(args, "mime", defaultMime);
        var blobHash = "sha256:" + Canonical.Hash(readable);
        return (kind, mime, meta, blobHash);
    }
}
