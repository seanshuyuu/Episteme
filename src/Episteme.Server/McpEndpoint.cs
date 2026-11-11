using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Server;

/// <summary>Minimal Streamable-HTTP MCP server: initialize / tools / resources, JSON-RPC 2.0.</summary>
public static class McpEndpoint
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "nodePath", "nodeId", "title", "parts", "tags", "branch", "reason", "expectParent",
        "idempotencyKey", "dryRun", "docId", "ref", "page", "pageSize", "spaceId",
        "actor", "agentId", "action", "q"
    };

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/mcp", async (HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
            await HandlePost(http, svc, body));

        app.MapGet("/mcp", () => Results.Json(new JsonObject
        {
            ["name"] = "episteme", ["version"] = EpistemeConfig.Version,
            ["transport"] = "Streamable HTTP", ["protocolVersion"] = "2025-06-18",
            ["endpoints"] = new JsonObject
            {
                ["rpc"] = "POST /mcp", ["sse"] = "GET /mcp/sse",
                ["messages"] = "POST /mcp/messages?sessionId=...",
                ["stdio"] = "src/Episteme.McpStdio（独立进程转发）",
                ["debugTools"] = "GET /mcp/tools?space={slug}"
            },
            ["capabilities"] = "GET /api/v1/episteme/capabilities",
            ["note"] = "POST /mcp 为完整 JSON-RPC 通道；SSE 与 stdio 为额外传输，共用同一 Dispatch"
        }));

        app.MapGet("/mcp/tools", async (HttpContext http, EpistemeService svc) =>
            await Endpoints.Run(http, async ct =>
            {
                var spaceId = await Endpoints.ResolveSpaceId(http, svc, new JsonObject(), ct);
                return string.IsNullOrWhiteSpace(spaceId)
                    ? new JsonObject { ["tools"] = new JsonArray(), ["toolsVersion"] = 0, ["note"] = "尚未创建空间" }
                    : await svc.ToolsListAsync(spaceId, ct);
            }));

        return app;
    }

    private static async Task<IResult> HandlePost(HttpContext http, EpistemeService svc, JsonObject body)
    {
        if (Jsonx.Plain(body["method"]).StartsWith("notifications/", StringComparison.Ordinal))
            return Results.StatusCode(202);
        return Results.Json(await ProcessAsync(http, svc, body));
    }

    /// <summary>
    /// Transport-independent JSON-RPC core. POST /mcp returns the envelope directly;
    /// the SSE transport pushes exactly the same envelope onto the event stream.
    /// </summary>
    internal static async Task<JsonObject> ProcessAsync(HttpContext http, EpistemeService svc, JsonObject body)
    {
        var id = body["id"]?.DeepClone();
        var method = Jsonx.Plain(body["method"]);
        var ct = http.RequestAborted;

        try
        {
            JsonObject result;
            switch (method)
            {
                case "initialize":
                    http.Response.Headers["Mcp-Session-Id"] = Ids.New("sess");
                    http.Response.Headers["MCP-Protocol-Version"] = "2025-06-18";
                    result = new JsonObject
                    {
                        ["protocolVersion"] = "2025-06-18",
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = true },
                            ["resources"] = new JsonObject { ["listChanged"] = true },
                            ["prompts"] = new JsonObject()
                        },
                        ["serverInfo"] = new JsonObject { ["name"] = "episteme", ["version"] = EpistemeConfig.Version }
                    };
                    break;

                case "ping":
                    result = new JsonObject();
                    break;

                case "tools/list":
                {
                    var spaceId = await Endpoints.ResolveSpaceId(http, svc, body, ct);
                    result = string.IsNullOrWhiteSpace(spaceId)
                        ? new JsonObject { ["tools"] = new JsonArray(), ["toolsVersion"] = 0, ["note"] = "尚未创建空间" }
                        : await svc.ToolsListAsync(spaceId, ct);
                    break;
                }

                case "tools/call":
                    result = await ToolsCall(http, svc, body, ct);
                    break;

                case "resources/list":
                {
                    var spaceId = await Endpoints.ResolveSpaceId(http, svc, body, ct);
                    var resources = new JsonArray();
                    if (!string.IsNullOrWhiteSpace(spaceId))
                    {
                        var nodes = await svc.ListNodesAsync(spaceId, ct);
                        foreach (var node in (nodes["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            var exposed = node["exposeToMcp"] is JsonValue v && v.TryGetValue<bool>(out var e) && e;
                            if (!exposed) continue;
                            resources.Add(new JsonObject
                            {
                                ["uri"] = $"episteme://{spaceId}{Jsonx.Plain(node["path"])}",
                                ["name"] = Jsonx.Plain(node["name"]),
                                ["mimeType"] = "application/json"
                            });
                        }
                    }
                    result = new JsonObject { ["resources"] = resources };
                    break;
                }

                case "resources/read":
                {
                    var uri = Jsonx.Plain((body["params"] as JsonObject)?["uri"]);
                    result = new JsonObject
                    {
                        ["contents"] = new JsonArray(new JsonObject
                        {
                            ["uri"] = uri, ["mimeType"] = "application/json",
                            ["text"] = new JsonObject { ["note"] = "按需读取请使用 episteme.tree / episteme.read 工具" }.ToJsonString()
                        })
                    };
                    break;
                }

                default:
                    return RpcError(id, -32601, $"Method not found: {method}", "METHOD_NOT_FOUND", 404);
            }
            return Rpc(id, result);
        }
        catch (BusinessException ex)
        {
            return RpcError(id, -32000, ex.Message, ex.Code, ex.Status);
        }
        catch (Exception ex)
        {
            return RpcError(id, -32603, ex.Message, "INTERNAL", 500);
        }
    }

    private static JsonObject Rpc(JsonNode? id, JsonObject result)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0" };
        if (id is not null) o["id"] = id.DeepClone();
        o["result"] = result;
        return o;
    }

    private static JsonObject RpcError(JsonNode? id, int code, string message, string dataCode, int status)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0" };
        if (id is not null) o["id"] = id.DeepClone();
        o["error"] = new JsonObject
        {
            ["code"] = code, ["message"] = message,
            ["data"] = new JsonObject { ["code"] = dataCode, ["httpStatus"] = status }
        };
        return o;
    }

    private static async Task<JsonObject> ToolsCall(HttpContext http, EpistemeService svc, JsonObject body, CancellationToken ct)
    {
        var parameters = body["params"] as JsonObject ?? new JsonObject();
        var name = Jsonx.Plain(parameters["name"]);
        var args = parameters["arguments"] as JsonObject ?? new JsonObject();
        var spaceId = await Endpoints.ResolveSpaceId(http, svc, body, ct);
        if (string.IsNullOrWhiteSpace(spaceId) && name != "episteme.list_spaces"
            && name != "episteme.list_packs" && name != "episteme.capabilities")
            throw new BusinessException("SPACE_NOT_FOUND", "尚未创建空间", 404);

        var role = http.Request.Headers["X-Episteme-Role"].FirstOrDefault();
        var ctx = WriteContext.From(args, "mcp", string.IsNullOrWhiteSpace(role) ? "agent" : role);
        ctx.Action = name;

        var payload = await Dispatch(svc, spaceId, name, args, ctx, ct);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text", ["text"] = payload.ToJsonString()
            }),
            ["structuredContent"] = payload,
            ["isError"] = false,
            ["toolsVersion"] = payload["toolsVersion"]?.DeepClone()
                ?? (string.IsNullOrWhiteSpace(spaceId)
                    ? JsonValue.Create(0)
                    : (await svc.GetSpaceAsync(spaceId, ct))["toolsVersion"]?.DeepClone())
        };
    }

    private static async Task<JsonObject> Dispatch(
        EpistemeService svc, string spaceId, string name, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        switch (name)
        {
            case "episteme.list_spaces": return await svc.ListSpacesAsync(ct);
            case "episteme.tree": return await svc.ListNodesAsync(spaceId, ct);
            case "episteme.read":
                return await svc.GetDocumentAsync(spaceId, Jsonx.Plain(args["docId"]), args, ct);
            case "episteme.search": return await svc.SearchAsync(spaceId, args, ct);
            case "episteme.list_content_types": return await svc.ListContentTypesAsync(spaceId, ct);
            case "episteme.define_content_type": return await svc.UpsertContentTypeAsync(spaceId, args, ctx, ct);
            case "episteme.create_node": return await svc.CreateNodeAsync(spaceId, args, ctx, ct);
            case "episteme.move_node":
                return await svc.UpdateNodeAsync(spaceId, Jsonx.Plain(args["nodeId"]), args, ctx, ct);
            case "episteme.delete_node":
                return await svc.DeleteNodeAsync(spaceId, Jsonx.Plain(args["nodeId"]), args, ctx, ct);
            case "episteme.create_document":
            {
                var nodeId = await NodeIdAsync(svc, spaceId, args, null, ct);
                return await svc.CreateDocumentAsync(spaceId, nodeId, args, ctx, ct);
            }
            case "episteme.update_document":
                return await svc.UpdateDocumentAsync(spaceId, Jsonx.Plain(args["docId"]), args, ctx, ct);
            case "episteme.append_part":
                return await svc.AppendPartAsync(spaceId, Jsonx.Plain(args["docId"]), args, ctx, ct);
            case "episteme.annotate":
                return await svc.AddAnnotationAsync(spaceId, Jsonx.Plain(args["docId"]), args, ctx, ct);
            case "episteme.viewers":
                return await svc.ViewersAsync(ct);
            case "episteme.read_part":
                return await svc.ReadPartAsync(spaceId, Jsonx.Plain(args["docId"]),
                    Jsonx.Plain(args["partId"]), args, ct);
            case "episteme.export_notes":
                return await svc.ExportNotesAsync(spaceId, Jsonx.Plain(args["docId"]), args, ctx, ct);
            case "episteme.list_packs": return await svc.ListPacksAsync(ct);
            case "episteme.install_pack": return await svc.InstallPackAsync(spaceId, args, ctx, ct);
            case "episteme.export_pack": return await svc.ExportPackAsync(spaceId, args, ct);
            case "episteme.capabilities": return EpistemeConfig.Describe();
            case "episteme.fetch_web":
                return await svc.FetchWebAsync(spaceId, Jsonx.Plain(args["docId"]), args, ctx, ct);
            case "episteme.upload_pdf_base64":
                return await svc.UploadPdfAsync(spaceId, Jsonx.Plain(args["docId"]), args, ctx, ct);
            case "episteme.rebind_annotation":
                return await svc.RebindAnnotationAsync(spaceId, Jsonx.Plain(args["docId"]),
                    Jsonx.Plain(args["annotationId"]), args, ctx, ct);
            case "episteme.commit":
            {
                var log = await svc.LogAsync(spaceId, new JsonObject { ["limit"] = 1 }, ct);
                var items = log["items"] as JsonArray;
                var head = items is { Count: > 0 } && items[0] is JsonObject first ? Jsonx.Plain(first["id"]) : "";
                return new JsonObject
                {
                    ["noStagedChanges"] = true, ["headCommitId"] = head,
                    ["note"] = "每个写工具一次调用即一个事务一个 Commit，无暂存区"
                };
            }
            case "episteme.log": return await svc.LogAsync(spaceId, args, ct);
            case "episteme.diff": return await svc.DiffAsync(spaceId, args, ct);
            case "episteme.branch":
                return string.IsNullOrWhiteSpace(Jsonx.Plain(args["name"]))
                    ? await svc.RefsAsync(spaceId, ct)
                    : await svc.CreateBranchAsync(spaceId, args, ctx, ct);
            case "episteme.merge": return await svc.MergeAsync(spaceId, args, ctx, ct);
            case "episteme.revert":
            {
                if (string.IsNullOrWhiteSpace(Jsonx.Plain(args["commitId"])))
                {
                    var agentId = Jsonx.Plain(args["agentId"]);
                    if (string.IsNullOrWhiteSpace(agentId))
                        throw new BusinessException("BAD_REQUEST", "revert 需要 commitId 或 agentId", 400);
                    var log = await svc.LogAsync(spaceId, new JsonObject { ["agentId"] = agentId, ["limit"] = 1 }, ct);
                    var items = log["items"] as JsonArray;
                    if (items is not { Count: > 0 } || items[0] is not JsonObject first)
                        return new JsonObject { ["reverted"] = 0, ["note"] = "该 Agent 无提交" };
                    args["commitId"] = Jsonx.Plain(first["id"]);
                }
                return await svc.RevertAsync(spaceId, args, ctx, ct);
            }
            case "episteme.export_as_git": return await svc.ExportGitAsync(spaceId, args, ct);
            default:
                if (name.StartsWith("episteme.query_", StringComparison.Ordinal) ||
                    name.StartsWith("episteme.create_", StringComparison.Ordinal) ||
                    name.StartsWith("episteme.update_", StringComparison.Ordinal))
                    return await DynamicCall(svc, spaceId, name, args, ctx, ct);
                throw new BusinessException("TOOL_NOT_FOUND", $"未知工具：{name}", 404);
        }
    }

    private static async Task<JsonObject> DynamicCall(
        EpistemeService svc, string spaceId, string name, JsonObject args, WriteContext ctx, CancellationToken ct)
    {
        var shortName = name.StartsWith("episteme.", StringComparison.Ordinal) ? name[9..] : name;
        var separator = shortName.IndexOf('_');
        var mode = separator > 0 ? shortName[..separator] : "query";
        var suffix = separator > 0 ? shortName[(separator + 1)..] : shortName;

        var tools = await svc.ToolsListAsync(spaceId, ct);
        var tool = (tools["tools"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                   .FirstOrDefault(t => Jsonx.Plain(t["name"]) == name)
                   ?? throw new BusinessException("TOOL_NOT_FOUND", $"工具未在 tools/list 中：{name}", 404);
        var schema = tool["inputSchema"] as JsonObject;
        var properties = schema?["properties"] as JsonObject ?? new JsonObject();

        var fields = new JsonObject();
        foreach (var kv in properties)
            if (!Reserved.Contains(kv.Key) && args.ContainsKey(kv.Key))
                fields[kv.Key] = args[kv.Key]?.DeepClone();

        var slug = await SlugOfAsync(svc, spaceId, suffix, ct) ?? suffix;
        if (mode == "query")
        {
            var query = new JsonObject
            {
                ["nodeId"] = await NodeIdAsync(svc, spaceId, args, slug, ct),
                ["page"] = args["page"]?.DeepClone() ?? 1,
                ["pageSize"] = args["pageSize"]?.DeepClone() ?? 20
            };
            if (args["ref"] is not null) query["branch"] = Jsonx.Plain(args["ref"]);
            var result = await svc.ListDocumentsAsync(spaceId, query, ct);
            result["contentType"] = slug;
            result["filters"] = fields.DeepClone();
            return result;
        }

        if (mode == "create")
        {
            var nodeId = await NodeIdAsync(svc, spaceId, args, slug, ct);
            var payload = new JsonObject
            {
                ["title"] = Jsonx.Plain(args["title"]),
                ["fields"] = fields
            };
            if (args["parts"] is JsonArray parts) payload["parts"] = parts.DeepClone();
            if (args["tags"] is JsonArray tags) payload["tags"] = tags.DeepClone();
            return await svc.CreateDocumentAsync(spaceId, nodeId, payload, ctx, ct);
        }

        var update = new JsonObject { ["fields"] = fields };
        if (args["title"] is not null) update["title"] = Jsonx.Plain(args["title"]);
        return await svc.UpdateDocumentAsync(spaceId, Jsonx.Plain(args["docId"]), update, ctx, ct);
    }

    private static async Task<string?> SlugOfAsync(EpistemeService svc, string spaceId, string suffix, CancellationToken ct)
    {
        var types = (await svc.ListContentTypesAsync(spaceId, ct))["items"] as JsonArray ?? new JsonArray();
        foreach (var t in types.OfType<JsonObject>())
            if (Jsonx.Plain(t["slug"]) == suffix) return suffix;
        foreach (var t in types.OfType<JsonObject>())
            if (Jsonx.Plain(t["slug"]).StartsWith(suffix, StringComparison.Ordinal)) return Jsonx.Plain(t["slug"]);
        return null;
    }

    private static async Task<string> NodeIdAsync(
        EpistemeService svc, string spaceId, JsonObject args, string? slug, CancellationToken ct)
    {
        var explicitId = Jsonx.Plain(args["nodeId"]);
        if (!string.IsNullOrWhiteSpace(explicitId)) return explicitId;

        var nodes = (await svc.ListNodesAsync(spaceId, ct))["items"] as JsonArray ?? new JsonArray();
        var path = Jsonx.Plain(args["nodePath"]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            var normalized = path.StartsWith('/') ? path : "/" + path;
            foreach (var n in nodes.OfType<JsonObject>())
                if (Jsonx.Plain(n["path"]) == normalized) return Jsonx.Plain(n["id"]);
        }
        if (!string.IsNullOrWhiteSpace(slug))
            foreach (var n in nodes.OfType<JsonObject>())
                if (Jsonx.Plain(n["contentType"]) == slug) return Jsonx.Plain(n["id"]);
        foreach (var n in nodes.OfType<JsonObject>())
            if (!string.IsNullOrWhiteSpace(Jsonx.Plain(n["contentType"]))) return Jsonx.Plain(n["id"]);
        throw new BusinessException("NODE_NOT_FOUND", "未找到可写入的目录节点，请先 create_node 或 define_content_type", 404);
    }
}
