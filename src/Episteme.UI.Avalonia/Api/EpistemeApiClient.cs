using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Episteme.UI.Avalonia.Api;

public sealed class EpistemeApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public EpistemeApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }
}

/// <summary>
/// P5: one REST client shared by the Desktop and Android heads. It talks to the very same
/// /api/v1/episteme contract used by the Web SPA and by MCP, so all three surfaces stay in sync.
/// </summary>
public sealed class EpistemeApiClient
{
    private readonly HttpClient _http;

    public string BaseUrl { get; }
    public string ApiRoot => BaseUrl.TrimEnd('/') + "/api/v1/episteme";

    /// <summary>WORKBENCH_API is kept as a compatibility alias for the older Workbench shell.</summary>
    public static string DefaultBaseUrl =>
        Environment.GetEnvironmentVariable("EPISTEME_API")
        ?? Environment.GetEnvironmentVariable("WORKBENCH_API")
        ?? "http://127.0.0.1:5080";

    public EpistemeApiClient(string? baseUrl = null, HttpClient? http = null)
    {
        BaseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.Contains("X-Episteme-Role"))
            _http.DefaultRequestHeaders.Add("X-Episteme-Role", "owner");
    }

    public static string Str(JsonObject obj, string key, string fallback = "")
    {
        if (obj[key] is null) return fallback;
        try { return obj[key]!.ToString(); }
        catch { return fallback; }
    }

    public static int Int(JsonObject obj, string key, int fallback = 0)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<int>(out var n)) return n;
        return fallback;
    }

    public async Task<(int Status, JsonObject Body)> SendAsync(
        HttpMethod method, string url, JsonObject? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        JsonObject parsed;
        try { parsed = JsonNode.Parse(raw) as JsonObject ?? new JsonObject { ["raw"] = raw }; }
        catch { parsed = new JsonObject { ["raw"] = raw }; }
        return ((int)response.StatusCode, parsed);
    }

    private async Task<JsonObject> RequestAsync(
        HttpMethod method, string url, JsonObject? body = null, CancellationToken ct = default)
    {
        var (status, result) = await SendAsync(method, url, body, ct);
        if (status is < 200 or >= 300)
            throw new EpistemeApiException(status, Str(result, "code", "HTTP_ERROR"),
                Str(result, "message", Str(result, "error", $"HTTP {status}")));
        return result;
    }

    private Task<JsonObject> Get(string path, CancellationToken ct) =>
        RequestAsync(HttpMethod.Get, ApiRoot + path, null, ct);

    private Task<JsonObject> Post(string path, JsonObject body, CancellationToken ct) =>
        RequestAsync(HttpMethod.Post, ApiRoot + path, body, ct);

    private static string S(string value) => Uri.EscapeDataString(value);
    private string Sp(string spaceId, string suffix = "") => $"/spaces/{S(spaceId)}{suffix}";

    public Task<JsonObject> HealthAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, BaseUrl + "/health", null, ct);

    public Task<JsonObject> SpacesAsync(CancellationToken ct = default) => Get("/spaces", ct);

    public Task<JsonObject> CreateSpaceAsync(string name, string slug, CancellationToken ct = default) =>
        Post("/spaces", new JsonObject { ["name"] = name, ["slug"] = slug, ["source"] = "desktop" }, ct);

    public Task<JsonObject> TreeAsync(string spaceId, CancellationToken ct = default) =>
        Get(Sp(spaceId, "/tree"), ct);

    public Task<JsonObject> ContentTypesAsync(string spaceId, CancellationToken ct = default) =>
        Get(Sp(spaceId, "/content-types"), ct);

    public Task<JsonObject> DocumentsAsync(string spaceId, CancellationToken ct = default) =>
        Get(Sp(spaceId, "/documents?pageSize=100"), ct);

    public Task<JsonObject> NodeDocumentsAsync(string spaceId, string nodeId, CancellationToken ct = default) =>
        Get(Sp(spaceId, $"/nodes/{S(nodeId)}/documents?pageSize=100"), ct);

    public Task<JsonObject> DocumentAsync(string spaceId, string docId, CancellationToken ct = default) =>
        Get(Sp(spaceId, $"/documents/{S(docId)}"), ct);

    public Task<JsonObject> RenderAsync(string spaceId, string nodeId, CancellationToken ct = default) =>
        Get(Sp(spaceId, $"/nodes/{S(nodeId)}/render"), ct);

    public Task<JsonObject> CommitsAsync(string spaceId, CancellationToken ct = default) =>
        Get(Sp(spaceId, "/commits?limit=50"), ct);

    public Task<JsonObject> AnnotationsAsync(string spaceId, string docId, CancellationToken ct = default) =>
        Get(Sp(spaceId, $"/documents/{S(docId)}/annotations"), ct);

    public Task<JsonObject> ViewersAsync(string spaceId, string docId, CancellationToken ct = default) =>
        Get(Sp(spaceId, $"/documents/{S(docId)}/viewers"), ct);

    public Task<JsonObject> CreateDocumentAsync(string spaceId, string nodeId, JsonObject body, CancellationToken ct = default) =>
        Post(Sp(spaceId, $"/nodes/{S(nodeId)}/documents"), body, ct);

    public Task<JsonObject> PacksAsync(CancellationToken ct = default) => Get("/packs", ct);

    public Task<JsonObject> PackAsync(string packId, CancellationToken ct = default) =>
        Get($"/packs/{S(packId)}", ct);

    public Task<JsonObject> InstallPackAsync(string spaceId, JsonObject args, CancellationToken ct = default) =>
        Post(Sp(spaceId, "/packs/install"), args, ct);

    public Task<JsonObject> ImportPackAsync(string spaceId, JsonObject args, CancellationToken ct = default) =>
        Post(Sp(spaceId, "/packs/import"), args, ct);

    public Task<JsonObject> ExportPackAsync(string spaceId, JsonObject args, CancellationToken ct = default) =>
        Post(Sp(spaceId, "/packs/export"), args, ct);

    public Task<JsonObject> McpToolsAsync(string spaceId, CancellationToken ct = default) =>
        Get(Sp(spaceId, "/mcp/tools"), ct);
}
