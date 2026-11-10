using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Server;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapEpistemeEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/episteme");

        // Spaces
        api.MapGet("/spaces", async (HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.ListSpacesAsync(ct)));

        api.MapPost("/spaces", async (HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
            await Run(http, ct => svc.CreateSpaceAsync(body, Ctx(http, body, "ui"), ct)));

        api.MapGet("/spaces/{id}", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.GetSpaceAsync(id, ct)));

        // Tree / nodes
        api.MapGet("/spaces/{id}/tree", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.ListNodesAsync(id, ct)));

        api.MapPost("/spaces/{id}/nodes", async (string id, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
            await Run(http, ct => svc.CreateNodeAsync(id, body, Ctx(http, body), ct)));

        api.MapPatch("/spaces/{id}/nodes/{nodeId}",
            async (string id, string nodeId, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.UpdateNodeAsync(id, nodeId, body, Ctx(http, body), ct)));

        api.MapDelete("/spaces/{id}/nodes/{nodeId}",
            async (string id, string nodeId, HttpContext http, EpistemeService svc) =>
                await Run(http, ct => svc.DeleteNodeAsync(id, nodeId, QueryJson(http.Request), Ctx(http, new JsonObject()), ct)));

        // Schema
        api.MapGet("/spaces/{id}/content-types", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.ListContentTypesAsync(id, ct)));

        api.MapPost("/spaces/{id}/content-types",
            async (string id, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.UpsertContentTypeAsync(id, body, Ctx(http, body), ct)));

        api.MapGet("/spaces/{id}/content-types/{slug}",
            async (string id, string slug, HttpContext http, EpistemeService svc) =>
                await Run(http, ct => svc.GetContentTypeAsync(id, slug, ct)));

        api.MapPut("/spaces/{id}/content-types/{slug}",
            async (string id, string slug, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
            {
                if (body["definition"] is JsonObject def) def["slug"] = slug;
                else body["slug"] = slug;
                return await Run(http, ct => svc.UpsertContentTypeAsync(id, body, Ctx(http, body), ct));
            });

        api.MapPost("/spaces/{id}/content-types/{slug}/validate",
            async (string id, string slug, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.ValidateFieldsAsync(id, slug, body, ct)));

        api.MapGet("/spaces/{id}/nodes/{nodeId}/render",
            async (string id, string nodeId, HttpContext http, EpistemeService svc) =>
                await Run(http, ct => svc.GetRenderAsync(id, nodeId, ct)));

        api.MapGet("/spaces/{id}/nodes/{nodeId}/schema",
            async (string id, string nodeId, HttpContext http, EpistemeService svc) =>
                await Run(http, ct => svc.GetRenderAsync(id, nodeId, ct)));

        // Documents / parts / annotations
        api.MapGet("/spaces/{id}/documents", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.ListDocumentsAsync(id, QueryJson(http.Request), ct)));

        api.MapGet("/spaces/{id}/nodes/{nodeId}/documents",
            async (string id, string nodeId, HttpContext http, EpistemeService svc) =>
            {
                var q = QueryJson(http.Request);
                q["nodeId"] = nodeId;
                return await Run(http, ct => svc.ListDocumentsAsync(id, q, ct));
            });

        api.MapPost("/spaces/{id}/nodes/{nodeId}/documents",
            async (string id, string nodeId, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.CreateDocumentAsync(id, nodeId, body, Ctx(http, body), ct)));

        api.MapGet("/spaces/{id}/documents/{docId}",
            async (string id, string docId, HttpContext http, EpistemeService svc) =>
                await Run(http, ct => svc.GetDocumentAsync(id, docId, QueryJson(http.Request), ct)));

        api.MapPatch("/spaces/{id}/documents/{docId}",
            async (string id, string docId, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.UpdateDocumentAsync(id, docId, body, Ctx(http, body), ct)));

        api.MapDelete("/spaces/{id}/documents/{docId}",
            async (string id, string docId, HttpContext http, EpistemeService svc, [FromBody] JsonObject? body) =>
                await Run(http, ct => svc.DeleteDocumentAsync(id, docId, Ctx(http, body ?? new JsonObject()), ct)));

        api.MapPost("/spaces/{id}/documents/{docId}/parts",
            async (string id, string docId, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.AppendPartAsync(id, docId, body, Ctx(http, body), ct)));

        api.MapPut("/spaces/{id}/documents/{docId}/parts/{partId}",
            async (string id, string docId, string partId, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.UpdatePartAsync(id, docId, partId, body, Ctx(http, body), ct)));

        api.MapGet("/spaces/{id}/documents/{docId}/annotations",
            async (string id, string docId, HttpContext http, EpistemeService svc) =>
                await Run(http, ct => svc.ListAnnotationsAsync(id, docId, ct)));

        api.MapPost("/spaces/{id}/documents/{docId}/annotations",
            async (string id, string docId, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.AddAnnotationAsync(id, docId, body, Ctx(http, body), ct)));

        api.MapGet("/spaces/{id}/search", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.SearchAsync(id, QueryJson(http.Request), ct)));

        // Versions
        api.MapGet("/spaces/{id}/commits", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.LogAsync(id, QueryJson(http.Request), ct)));

        api.MapGet("/spaces/{id}/commits/{commitId}",
            async (string id, string commitId, HttpContext http, EpistemeService svc) =>
                await Run(http, ct => svc.GetCommitAsync(id, commitId, ct)));

        api.MapGet("/spaces/{id}/diff", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.DiffAsync(id, QueryJson(http.Request), ct)));

        api.MapGet("/spaces/{id}/blame", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.BlameAsync(id, Jsonx.Plain(QueryJson(http.Request)["docId"]), ct)));

        api.MapGet("/spaces/{id}/refs", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.RefsAsync(id, ct)));

        api.MapPost("/spaces/{id}/branches",
            async (string id, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.CreateBranchAsync(id, body, Ctx(http, body), ct)));

        api.MapPost("/spaces/{id}/merges",
            async (string id, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.MergeAsync(id, body, Ctx(http, body), ct)));

        api.MapPost("/spaces/{id}/reverts",
            async (string id, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.RevertAsync(id, body, Ctx(http, body), ct)));

        api.MapPost("/spaces/{id}/tags",
            async (string id, HttpContext http, EpistemeService svc, [FromBody] JsonObject body) =>
                await Run(http, ct => svc.CreateTagAsync(id, body, Ctx(http, body), ct)));

        api.MapPost("/spaces/{id}/exports/git",
            async (string id, HttpContext http, EpistemeService svc, [FromBody] JsonObject? body) =>
                await Run(http, ct => svc.ExportGitAsync(id, body ?? new JsonObject(), ct)));

        // MCP debug + audit
        api.MapGet("/spaces/{id}/mcp/tools", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.ToolsListAsync(id, ct)));

        api.MapGet("/mcp/tools", async (HttpContext http, EpistemeService svc) =>
        {
            var spaceId = await ResolveSpaceId(http, svc, new JsonObject(), http.RequestAborted);
            return await Run(http, ct => svc.ToolsListAsync(spaceId, ct));
        });

        api.MapGet("/spaces/{id}/audit", async (string id, HttpContext http, EpistemeService svc) =>
            await Run(http, ct => svc.AuditListAsync(id, ct)));

        return app;
    }

    internal static WriteContext Ctx(HttpContext http, JsonObject body, string source = "api")
    {
        var role = http.Request.Headers["X-Episteme-Role"].FirstOrDefault();
        return WriteContext.From(body, source, string.IsNullOrWhiteSpace(role) ? "owner" : role);
    }

    internal static JsonObject QueryJson(HttpRequest request)
    {
        var result = new JsonObject();
        foreach (var kv in request.Query)
        {
            var value = kv.Value.ToString();
            if (int.TryParse(value, out var number)) result[kv.Key] = number;
            else if (bool.TryParse(value, out var flag)) result[kv.Key] = flag;
            else result[kv.Key] = value;
        }
        return result;
    }

    internal static async Task<string> ResolveSpaceId(
        HttpContext http, EpistemeService svc, JsonObject body, CancellationToken ct)
    {
        var parameters = body["params"] as JsonObject;
        var raw = Jsonx.Plain(parameters?["spaceId"]);
        if (string.IsNullOrWhiteSpace(raw)) raw = http.Request.Query["space"].ToString();
        var spaces = await svc.ListSpacesAsync(ct);
        var items = spaces["items"] as JsonArray ?? new JsonArray();
        if (!string.IsNullOrWhiteSpace(raw))
            foreach (var item in items)
                if (item is JsonObject o && (Jsonx.Plain(o["id"]) == raw || Jsonx.Plain(o["slug"]) == raw))
                    return Jsonx.Plain(o["id"]);
        return items.Count > 0 && items[0] is JsonObject first ? Jsonx.Plain(first["id"]) : "";
    }

    internal static async Task<IResult> Run(HttpContext http, Func<CancellationToken, Task<JsonObject>> body)
    {
        try
        {
            var result = await body(http.RequestAborted);
            if (result["toolsVersion"] is not null)
                http.Response.Headers["X-Tools-Version"] = Jsonx.Plain(result["toolsVersion"]);
            return Results.Json(result, statusCode: 200);
        }
        catch (BusinessException ex)
        {
            return Results.Json(new JsonObject
            {
                ["code"] = ex.Code, ["message"] = ex.Message,
                ["traceId"] = http.TraceIdentifier
            }, statusCode: ex.Status);
        }
        catch (Exception ex)
        {
            return Results.Json(new JsonObject
            {
                ["code"] = "INTERNAL",
                ["message"] = ex.InnerException is null ? ex.Message : ex.Message + " | " + ex.InnerException.Message,
                ["traceId"] = http.TraceIdentifier
            }, statusCode: 500);
        }
    }
}
