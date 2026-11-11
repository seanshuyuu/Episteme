using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Episteme.Core;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

/// <summary>
/// P5 polish: real media ingestion. Web capture goes through the isolated fetch-worker
/// (SSRF guards + Mozilla Readability); PDF binaries live in a content-addressed blob store
/// and are rendered client-side by pdf.js; lost anchors can be interactively rebound.
/// </summary>
public sealed partial class EpistemeService
{
    private const int MaxPdfBytes = 20 * 1024 * 1024;

    private static readonly HttpClient WorkerHttp = new() { Timeout = TimeSpan.FromSeconds(25) };

    private static string WorkerBase() =>
        (Environment.GetEnvironmentVariable("EPISTEME_FETCH_WORKER_URL") ?? "").Trim().TrimEnd('/');

    private static string DataDir() =>
        Environment.GetEnvironmentVariable("EPISTEME_DATA_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "data");

    private static string WorkerToken() =>
        Environment.GetEnvironmentVariable("EPISTEME_FETCH_TOKEN") ?? "";

    private static string[] ConfiguredAllowlist() =>
        (Environment.GetEnvironmentVariable("EPISTEME_FETCH_ALLOWLIST") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>POST to the isolated worker; maps worker error codes to business errors.</summary>
    private static async Task<JsonObject> InvokeWorkerAsync(string path, JsonObject payload, CancellationToken ct)
    {
        var baseUrl = WorkerBase();
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new BusinessException("FETCH_WORKER_UNAVAILABLE",
                "未配置 EPISTEME_FETCH_WORKER_URL；网页抓取需启动独立 fetch-worker", 503);

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var token = WorkerToken();
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.TryAddWithoutValidation("X-Worker-Token", token);

        HttpResponseMessage response;
        try { response = await WorkerHttp.SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new BusinessException("FETCH_WORKER_UNAVAILABLE",
                "fetch-worker 不可达：" + ex.Message, 503);
        }
        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            JsonObject parsed;
            try { parsed = Jsonx.Node(raw); }
            catch { throw new BusinessException("FETCH_WORKER_BAD_RESPONSE", "worker 返回非法 JSON", 502); }

            if (!response.IsSuccessStatusCode || Jsonx.Plain(parsed["ok"]) == "false")
            {
                var error = parsed["error"] as JsonObject ?? new JsonObject();
                var code = Jsonx.Plain(error["code"]) is { Length: > 0 } c ? c : "FETCH_FAILED";
                var message = Jsonx.Plain(error["message"]) is { Length: > 0 } m ? m : "抓取失败";
                throw new BusinessException(code, message, (int)response.StatusCode is >= 400 and <= 599
                    ? (int)response.StatusCode : 502);
            }
            return parsed;
        }
    }

    /// <summary>Diagnostic probe for the isolated worker (no token or secrets are echoed).</summary>
    public static async Task<JsonObject> WorkerHealthAsync(CancellationToken ct)
    {
        var baseUrl = WorkerBase();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new JsonObject
            {
                ["status"] = "disabled", ["configured"] = false,
                ["note"] = "未配置 EPISTEME_FETCH_WORKER_URL；网页抓取能力不可用，其余功能不受影响"
            };
        try
        {
            using var response = await WorkerHttp.GetAsync(baseUrl + "/health", ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            var worker = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
            return new JsonObject
            {
                ["status"] = response.IsSuccessStatusCode ? "ok" : "degraded",
                ["configured"] = true,
                ["httpStatus"] = (int)response.StatusCode,
                ["worker"] = worker.DeepClone()
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["status"] = "unavailable", ["configured"] = true, ["message"] = ex.Message
            };
        }
    }

    /// <summary>
    /// Capture a URL (network mode) or extract a pasted HTML snapshot (offline mode),
    /// then append/replace a sanitized web Part in one Commit.
    /// </summary>
    public async Task<JsonObject> FetchWebAsync(
        string spaceId, string docId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        var url = S(args, "url");
        var html = S(args, "html");
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(html))
            throw new BusinessException("BAD_REQUEST", "需要 url（在线抓取）或 html（离线快照）", 400);

        var payload = new JsonObject();
        var offline = string.IsNullOrWhiteSpace(url);
        if (offline) { payload["html"] = html; payload["url"] = S(args, "sourceUri", "https://offline.example/"); }
        else payload["url"] = url;

        var allowed = new JsonArray();
        if (args["allowedHosts"] is JsonArray requestAllowlist)
            foreach (var item in requestAllowlist) allowed.Add(item?.DeepClone());
        if (allowed.Count == 0)
            foreach (var host in ConfiguredAllowlist()) allowed.Add(host);
        if (allowed.Count > 0) payload["allowedHosts"] = allowed;

        var article = await InvokeWorkerAsync(offline ? "/extract" : "/fetch", payload, ct);
        var content = Jsonx.Plain(article["html"]);
        if (string.IsNullOrWhiteSpace(content))
            throw new BusinessException("EMPTY_ARTICLE", "worker 未抽取到正文", 422);

        var finalUrl = Jsonx.Plain(article["finalUrl"]) is { Length: > 0 } f ? f : (offline ? "" : url);
        var extractor = Jsonx.Plain(article["extractor"]) is { Length: > 0 } e ? e : "unknown";

        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var doc = await db.Documents.Include(d => d.Parts)
            .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
            ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);

        var replaceId = S(args, "replacePartId");
        var part = string.IsNullOrWhiteSpace(replaceId)
            ? null
            : doc.Parts.FirstOrDefault(p => p.Id == replaceId && p.Kind == "web");
        if (!string.IsNullOrWhiteSpace(replaceId) && part is null)
            throw new BusinessException("PART_NOT_FOUND", "未找到可重抓的 web Part", 404);
        var isNew = part is null;

        var meta = part is null ? new JsonObject() : Jsonx.Node(part.MetaJson);
        meta["extractor"] = extractor;
        meta["origin"] = "external";
        meta["sanitized"] = true;
        meta["finalUrl"] = finalUrl;
        meta["httpStatus"] = article["httpStatus"]?.DeepClone() ?? 0;
        meta["fetchedAt"] = Jsonx.Plain(article["fetchedAt"]);
        meta["wordCount"] = article["wordCount"]?.DeepClone() ?? 0;
        meta["byteLength"] = article["bytes"]?.DeepClone() ?? 0;
        meta["captureMode"] = Jsonx.Plain(article["mode"]);
        meta["title"] = Jsonx.Plain(article["title"]);
        meta["byline"] = Jsonx.Plain(article["byline"]);
        meta["excerpt"] = Jsonx.Plain(article["excerpt"]);
        meta["sourceUri"] = finalUrl;
        meta["redirects"] = article["redirects"]?.DeepClone() ?? 0;
        meta["note"] = "正文由 fetch-worker (Readability) 抽取并做白名单清洗；快照不执行脚本";

        if (part is null)
        {
            part = new Part
            {
                Id = Ids.New("pt"), DocumentId = docId,
                Ord = doc.Parts.Count == 0 ? 0 : doc.Parts.Max(p => p.Ord) + 1,
                Kind = "web", Mime = "text/html", SourceUri = finalUrl,
                BlobHash = "sha256:" + Canonical.Hash(Jsonx.Plain(article["text"]))
            };
            doc.Parts.Add(part);
        }
        part.Content = content;
        part.MetaJson = meta.ToJsonString();
        doc.UpdatedAt = DateTime.UtcNow;

        var commit = await PersistAsync(db, space, ctx,
            $"{(isNew ? "抓取" : "重抓")}网页：{Jsonx.Plain(article["title"])}", ct);
        return new JsonObject
        {
            ["partId"] = part.Id, ["docId"] = docId, ["commitId"] = commit.Id,
            ["mode"] = Jsonx.Plain(article["mode"]),
            ["extractor"] = extractor,
            ["title"] = Jsonx.Plain(article["title"]),
            ["finalUrl"] = finalUrl,
            ["sourceUri"] = finalUrl,
            ["httpStatus"] = article["httpStatus"]?.DeepClone() ?? 0,
            ["sanitized"] = true,
            ["wordCount"] = article["wordCount"]?.DeepClone() ?? 0,
            ["links"] = article["links"]?.DeepClone() ?? new JsonArray(),
            ["htmlLength"] = content.Length,
            ["idempotentReplay"] = IsReplay(commit, ctx)
        };
    }

    /// <summary>Store a real PDF binary in the blob store; the Part keeps only a content-addressed reference.</summary>
    public async Task<JsonObject> UploadPdfAsync(
        string spaceId, string docId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        var raw = S(args, "base64");
        if (string.IsNullOrWhiteSpace(raw)) raw = S(args, "dataUrl");
        var comma = raw.IndexOf(',');
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) raw = raw[(comma + 1)..];
        if (string.IsNullOrWhiteSpace(raw))
            throw new BusinessException("BAD_REQUEST", "需要 base64 / dataUrl 形式的 PDF 数据", 400);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(raw.Trim()); }
        catch { throw new BusinessException("PDF_INVALID_BASE64", "PDF 数据不是合法 base64", 422); }
        if (bytes.Length == 0 || bytes.Length > MaxPdfBytes)
            throw new BusinessException("PDF_SIZE_INVALID", $"PDF 大小需在 1 ~ {MaxPdfBytes} 字节之间", 422);
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
            throw new BusinessException("PDF_INVALID_HEADER", "文件头不是 %PDF-，拒绝写入", 422);

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var blobDir = Path.Combine(DataDir(), "blobs");
        Directory.CreateDirectory(blobDir);
        var filePath = Path.Combine(blobDir, hash + ".pdf");
        if (!File.Exists(filePath)) await File.WriteAllBytesAsync(filePath, bytes, ct);

        var pages = Regex.Matches(Encoding.Latin1.GetString(bytes), @"/Type\s*/Page[^s]").Count;

        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var doc = await db.Documents.Include(d => d.Parts)
            .FirstOrDefaultAsync(d => d.SpaceId == spaceId && d.Id == docId, ct)
            ?? throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);

        var meta = new JsonObject
        {
            ["pdfFormat"] = "binary", ["blobHash"] = "sha256:" + hash,
            ["byteLength"] = bytes.Length, ["pages"] = Math.Max(1, pages),
            ["originalName"] = S(args, "name", "document.pdf"),
            ["origin"] = "binary", ["textLayer"] = false,
            ["renderer"] = "pdfjs-4.10.38",
            ["note"] = "PDF 原件只读；二进制存于内容寻址 blob 仓库，版本树只记引用"
        };
        var part = new Part
        {
            Id = Ids.New("pt"), DocumentId = docId,
            Ord = doc.Parts.Count == 0 ? 0 : doc.Parts.Max(p => p.Ord) + 1,
            Kind = "pdf", Mime = "application/pdf",
            Content = "blob:sha256:" + hash,
            BlobHash = "sha256:" + hash,
            SourceUri = args["sourceUri"] is null ? null : Jsonx.Plain(args["sourceUri"]),
            MetaJson = meta.ToJsonString()
        };
        doc.Parts.Add(part);
        doc.UpdatedAt = DateTime.UtcNow;
        var commit = await PersistAsync(db, space, ctx, $"上传 PDF 原件（{bytes.Length} 字节）", ct);
        return new JsonObject
        {
            ["partId"] = part.Id, ["docId"] = docId, ["commitId"] = commit.Id,
            ["blobHash"] = "sha256:" + hash, ["byteLength"] = bytes.Length,
            ["pages"] = Math.Max(1, pages), ["pdfFormat"] = "binary",
            ["renderer"] = "pdfjs-4.10.38",
            ["binaryUrl"] = $"/api/v1/episteme/spaces/{spaceId}/documents/{docId}/parts/{part.Id}/binary",
            ["idempotentReplay"] = IsReplay(commit, ctx)
        };
    }

    /// <summary>Read a binary PDF back for pdf.js. Verifies space/doc/part ownership and the blob marker.</summary>
    public async Task<byte[]> PdfBinaryAsync(string spaceId, string docId, string partId, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId && p.DocumentId == docId, ct)
                   ?? throw new BusinessException("PART_NOT_FOUND", "Part 不存在", 404);
        var docExists = await db.Documents.AnyAsync(d => d.Id == docId && d.SpaceId == spaceId, ct);
        if (!docExists) throw new BusinessException("DOCUMENT_NOT_FOUND", "文档不存在", 404);

        var meta = Jsonx.Node(part.MetaJson);
        if (Jsonx.Plain(meta["pdfFormat"]) != "binary" ||
            part.Content is null || !part.Content.StartsWith("blob:sha256:", StringComparison.Ordinal))
            throw new BusinessException("PART_NOT_BINARY", "该 Part 不是二进制 PDF（可能是文本层 Part）", 409);

        var hash = part.Content["blob:sha256:".Length..];
        if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$"))
            throw new BusinessException("BLOB_HASH_INVALID", "blob 引用非法", 422);
        var path = Path.Combine(DataDir(), "blobs", hash + ".pdf");
        if (!File.Exists(path)) throw new BusinessException("BLOB_MISSING", "PDF blob 文件缺失", 404);
        return await File.ReadAllBytesAsync(path, ct);
    }

    /// <summary>Interactively re-anchor a lost annotation; normalizes the new anchor and records a Commit.</summary>
    public async Task<JsonObject> RebindAnnotationAsync(
        string spaceId, string docId, string annotationId, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        RequireRole(ctx, "owner", "editor", "agent");
        await using var db = await Db(ct);
        var space = await RequireSpace(db, spaceId, ct);
        var ann = await db.Annotations.FirstOrDefaultAsync(a => a.Id == annotationId, ct)
                  ?? throw new BusinessException("ANNOTATION_NOT_FOUND", "标记不存在", 404);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == ann.PartId && p.DocumentId == docId, ct)
                   ?? throw new BusinessException("PART_NOT_FOUND", "标记所属 Part 不存在", 404);

        var input = (args["anchor"] as JsonObject ?? new JsonObject()).DeepClone() as JsonObject ?? new JsonObject();
        if (input["kind"] is null) input["kind"] = part.Kind;
        var (anchor, errors) = AnchorEngine.Normalize(part.Kind, input, part.Content, Jsonx.Node(part.MetaJson));
        if (errors.Count > 0)
            throw new BusinessException("ANCHOR_INVALID", string.Join("；", errors), 422);

        ctx.ForcedCommitId = Ids.New("cm");
        anchor["commitId"] = ctx.ForcedCommitId;
        anchor["rebound"] = true;
        ann.AnchorJson = anchor.ToJsonString();
        if (args["text"] is not null) ann.Text = S(args, "text");
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId, ct);
        if (doc is not null) doc.UpdatedAt = DateTime.UtcNow;

        var commit = await PersistAsync(db, space, ctx, $"重新定位 {ann.Kind} 标记", ct);
        return new JsonObject
        {
            ["annotationId"] = ann.Id, ["partId"] = part.Id, ["commitId"] = commit.Id,
            ["anchor"] = anchor.DeepClone(),
            ["resolution"] = AnchorEngine.Resolve(anchor, part.Content),
            ["idempotentReplay"] = IsReplay(commit, ctx)
        };
    }
}
