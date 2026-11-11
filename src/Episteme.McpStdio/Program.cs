/* Episteme MCP stdio bridge.
 * Reads one JSON-RPC 2.0 request per stdin line, forwards it to the Streamable HTTP
 * endpoint and writes exactly one JSON line per response to stdout. All diagnostics go
 * to stderr so the protocol stream is never polluted.
 */
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

var httpUrl = Environment.GetEnvironmentVariable("EPISTEME_MCP_HTTP_URL")
              ?? "http://127.0.0.1:5080/mcp";
var space = Environment.GetEnvironmentVariable("EPISTEME_MCP_SPACE") ?? "";
var role = Environment.GetEnvironmentVariable("EPISTEME_MCP_ROLE") ?? "owner";
var bearer = Environment.GetEnvironmentVariable("EPISTEME_AUTH_TOKEN") ?? "";

var uri = httpUrl;
if (!string.IsNullOrWhiteSpace(space))
    uri += (uri.Contains('?') ? "&" : "?") + "space=" + Uri.EscapeDataString(space);

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
Console.Error.WriteLine("[episteme-mcp-stdio] forwarding to " + uri);

string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonObject? request;
    try { request = JsonNode.Parse(line) as JsonObject; }
    catch (Exception ex)
    {
        Console.WriteLine(Error(null, -32700, "JSON 解析失败：" + ex.Message));
        Console.Out.Flush();
        continue;
    }
    if (request is null)
    {
        Console.WriteLine(Error(null, -32600, "请求必须是 JSON 对象"));
        Console.Out.Flush();
        continue;
    }

    var method = request["method"]?.ToString() ?? "";
    using var message = new HttpRequestMessage(HttpMethod.Post, uri)
    {
        Content = new StringContent(line, Encoding.UTF8, "application/json")
    };
    if (!string.IsNullOrWhiteSpace(role))
        message.Headers.TryAddWithoutValidation("X-Episteme-Role", role);
    if (!string.IsNullOrWhiteSpace(bearer))
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);

    string responseText;
    try
    {
        using var response = await client.SendAsync(message);
        responseText = (await response.Content.ReadAsStringAsync())
            .Replace("\r", "").Replace("\n", "");
    }
    catch (Exception ex)
    {
        Console.WriteLine(Error(request["id"]?.DeepClone(), -32603, "HTTP 转发失败：" + ex.Message));
        Console.Out.Flush();
        continue;
    }

    // notifications/* are accepted with 202 and must not produce a stdout response.
    if (method.StartsWith("notifications/", StringComparison.Ordinal)) continue;
    Console.WriteLine(responseText);
    Console.Out.Flush();
}

static string Error(JsonNode? id, int code, string message)
{
    var envelope = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
    if (id is not null) envelope["id"] = id;
    return envelope.ToJsonString();
}
