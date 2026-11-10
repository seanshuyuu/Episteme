using System.Text;
using System.Text.Json.Nodes;

namespace Episteme.UI.Avalonia.Api;

/// <summary>
/// P5: lets the native clients use the same dynamic MCP tool surface as an Agent.
/// Streamable HTTP POST /mcp; stdio remains an explicit, documented boundary of this release.
/// </summary>
public sealed class EpistemeMcpClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public EpistemeMcpClient(string baseUrl, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<JsonObject> CallAsync(
        string method, JsonObject? parameters = null, string? space = null, CancellationToken ct = default)
    {
        var url = _baseUrl + "/mcp" +
                  (string.IsNullOrWhiteSpace(space) ? "" : "?space=" + Uri.EscapeDataString(space));
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method,
            ["params"] = parameters?.DeepClone() ?? new JsonObject()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Episteme-Role", "owner");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        var envelope = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        if (envelope["error"] is JsonObject error)
            throw new EpistemeApiException((int)response.StatusCode, "MCP_ERROR",
                EpistemeApiClient.Str(error, "message", "MCP 调用失败"));
        return envelope["result"] as JsonObject ?? new JsonObject();
    }

    public Task<JsonObject> InitializeAsync(string? space = null, CancellationToken ct = default) =>
        CallAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "episteme-avalonia", ["version"] = "0.4.0" }
        }, space, ct);

    public Task<JsonObject> ToolsListAsync(string? space = null, CancellationToken ct = default) =>
        CallAsync("tools/list", new JsonObject(), space, ct);

    public Task<JsonObject> CallToolAsync(
        string name, JsonObject arguments, string? space = null, CancellationToken ct = default) =>
        CallAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, space, ct);

    public static JsonObject Structured(JsonObject result) =>
        result["structuredContent"] as JsonObject ?? result;
}
