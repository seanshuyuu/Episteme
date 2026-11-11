using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Server;

/// <summary>
/// MCP over Server-Sent Events. GET /mcp/sse opens the event stream and announces the
/// POST /mcp/messages?sessionId=... endpoint; both transports share McpEndpoint.ProcessAsync,
/// so the JSON-RPC envelopes are byte-for-byte identical to POST /mcp.
/// </summary>
public static class SseMcpEndpoint
{
    private sealed class Session(HttpResponse response)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task SendAsync(string data, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                await response.WriteAsync("event: message\ndata: " + data.Replace("\n", "") + "\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            finally { _gate.Release(); }
        }

        public async Task PingAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                await response.WriteAsync(": keepalive\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            finally { _gate.Release(); }
        }
    }

    private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapSseMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/mcp/sse", async (HttpContext http) =>
        {
            var id = Ids.New("sess");
            var response = http.Response;
            response.Headers["Content-Type"] = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache, no-transform";
            response.Headers["Connection"] = "keep-alive";
            response.Headers["X-Accel-Buffering"] = "no";
            var session = new Session(response);
            Sessions[id] = session;
            http.Response.Headers["Mcp-Session-Id"] = id;

            await response.WriteAsync($"event: endpoint\ndata: /mcp/messages?sessionId={id}\n\n",
                http.RequestAborted);
            await response.Body.FlushAsync(http.RequestAborted);
            try
            {
                while (!http.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(20000, http.RequestAborted);
                    await session.PingAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            catch (Exception) { /* broken stream */ }
            finally { Sessions.TryRemove(id, out _); }
        });

        app.MapPost("/mcp/messages", async (HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
        {
            var id = http.Request.Query["sessionId"].ToString();
            if (string.IsNullOrWhiteSpace(id) || !Sessions.TryGetValue(id, out var session))
                return Results.Json(new JsonObject
                {
                    ["error"] = "SESSION_NOT_FOUND",
                    ["message"] = "SSE 会话不存在或已关闭；请先 GET /mcp/sse"
                }, statusCode: 404);

            if (Jsonx.Plain(body["method"]).StartsWith("notifications/", StringComparison.Ordinal))
                return Results.StatusCode(202);

            var envelope = await McpEndpoint.ProcessAsync(http, svc, body);
            try { await session.SendAsync(envelope.ToJsonString(), http.RequestAborted); }
            catch (Exception) { Sessions.TryRemove(id, out _); }
            return Results.StatusCode(202);
        });

        app.MapGet("/mcp/sessions", () => Results.Json(new JsonObject
        {
            ["transport"] = "sse",
            ["total"] = Sessions.Count,
            ["protocolVersion"] = "2025-06-18",
            ["note"] = "会话仅存活于当前进程；多实例部署需改用共享背板"
        }));

        return app;
    }
}
