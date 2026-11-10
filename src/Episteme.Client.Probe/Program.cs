using System.Text.Json.Nodes;
using Episteme.UI.Avalonia.Api;

// Headless probe for the shared Desktop/Android client layer. It runs inside the .NET SDK
// container against the live Episteme service, so the native client is verified without a GUI.
var pass = 0;
var fail = 0;

void Check(string name, bool ok, object? detail = null)
{
    if (ok) { pass++; Console.WriteLine("  PASS  " + name); return; }
    fail++;
    var text = detail?.ToString() ?? "";
    Console.WriteLine("  FAIL  " + name + " :: " + (text.Length > 300 ? text[..300] : text));
}

static string S(JsonObject o, string k) => EpistemeApiClient.Str(o, k);
static int I(JsonObject o, string k) => EpistemeApiClient.Int(o, k);
static int Count(JsonObject o, string k = "items") => (o[k] as JsonArray)?.Count ?? 0;
static bool B(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

var api = new EpistemeApiClient();
var mcp = new EpistemeMcpClient(api.BaseUrl);
var stamp = DateTime.UtcNow.ToString("HHmmss");

Console.WriteLine("== Episteme P5 客户端层冒烟（共享 Desktop / Android）==");
Console.WriteLine("BASE=" + api.BaseUrl);

try
{
    var health = await api.HealthAsync();
    Check("健康检查", S(health, "status") == "ok", S(health, "version"));

    var packs = await api.PacksAsync();
    var packsValid = (packs["items"] as JsonArray ?? new JsonArray())
        .OfType<JsonObject>().All(p => B(p, "valid"));
    Check("内置 Pack 目录 2 套且均校验通过", Count(packs) == 2 && packsValid, Count(packs));

    var space = await api.CreateSpaceAsync("客户端冒烟 " + stamp, "client-" + stamp);
    var sid = S(space, "id");
    var slug = S(space, "slug");
    Check("创建空间", sid.Length > 0, sid);

    var emptyTree = await api.TreeAsync(sid);
    Check("Empty by default：新空间零目录", I(emptyTree, "total") == 0, I(emptyTree, "total"));

    var dry = await api.InstallPackAsync(sid, new JsonObject
    {
        ["packId"] = "workbench", ["dryRun"] = true, ["source"] = "desktop"
    });
    Check("Pack dryRun 不产生 Commit", B(dry, "dryRun") && dry["commitId"] is null,
        I(dry, "wouldCreateNodes"));

    var installed = await api.InstallPackAsync(sid, new JsonObject
    {
        ["packId"] = "workbench", ["source"] = "desktop"
    });
    Check("安装 workbench Pack：15 目录 / 12 类型 / import Commit",
        I(installed, "createdNodes") == 15 && I(installed, "createdContentTypes") == 12
        && S(installed, "commitSource") == "import", installed.ToJsonString());

    var tree = await api.TreeAsync(sid);
    Check("动态目录树可读", I(tree, "total") == 15, I(tree, "total"));
    var types = await api.ContentTypesAsync(sid);
    Check("动态 Schema 列表可读", I(types, "total") == 12, I(types, "total"));

    var bound = (tree["items"] as JsonArray ?? new JsonArray())
        .OfType<JsonObject>()
        .FirstOrDefault(n => !string.IsNullOrWhiteSpace(S(n, "contentType")));
    if (bound is null) Check("Schema 驱动 /render 可生成控件", false, "没有绑定类型的目录");
    else
    {
        var render = await api.RenderAsync(sid, S(bound, "id"));
        Check("Schema 驱动 /render 可生成控件",
            !string.IsNullOrWhiteSpace(S(render, "contentType")) && Count(render, "controls") > 0,
            S(render, "contentType") + " controls=" + Count(render, "controls"));
    }

    var commits = await api.CommitsAsync(sid);
    Check("类 git 版本历史可读", I(commits, "total") >= 1, I(commits, "total"));

    var tools = await api.McpToolsAsync(sid);
    Check("动态 MCP 工具 26 个", I(tools, "total") >= 26, I(tools, "total"));

    var exported = await api.ExportPackAsync(sid, new JsonObject
    {
        ["name"] = "客户端导出模板", ["source"] = "desktop", ["includeDocuments"] = false
    });
    Check("导出合法 Pack JSON", B(exported, "valid") && S(exported, "json").Length > 100,
        S(exported, "json").Length);

    var space2 = await api.CreateSpaceAsync("客户端导入 " + stamp, "client-" + stamp + "-b");
    var sid2 = S(space2, "id");
    var imported = await api.ImportPackAsync(sid2, new JsonObject
    {
        ["pack"] = JsonNode.Parse(S(exported, "json")) as JsonObject ?? new JsonObject(),
        ["source"] = "desktop"
    });
    Check("导出结果可导入到另一个空间", I(imported, "createdNodes") == 15,
        I(imported, "createdNodes"));

    var init = await mcp.InitializeAsync(slug);
    Check("MCP initialize", S(init, "protocolVersion") == "2025-06-18", S(init, "protocolVersion"));
    var toolList = await mcp.ToolsListAsync(slug);
    Check("MCP tools/list 返回动态工具", Count(toolList, "tools") >= 26, Count(toolList, "tools"));
    var listPacks = EpistemeMcpClient.Structured(
        await mcp.CallToolAsync("episteme.list_packs", new JsonObject(), slug));
    Check("MCP episteme.list_packs 可调用", I(listPacks, "total") == 2, I(listPacks, "total"));

    var space3 = await api.CreateSpaceAsync("客户端阅读 " + stamp, "client-" + stamp + "-c");
    var sid3 = S(space3, "id");
    var sampled = await api.InstallPackAsync(sid3, new JsonObject
    {
        ["packId"] = "pack_reading-desk", ["includeSamples"] = true, ["source"] = "desktop"
    });
    Check("includeSamples 导入示例文档", I(sampled, "createdSampleDocuments") >= 1,
        I(sampled, "createdSampleDocuments"));

    var sampleDocs = await api.DocumentsAsync(sid3);
    var firstDoc = (sampleDocs["items"] as JsonArray ?? new JsonArray())
        .OfType<JsonObject>().FirstOrDefault();
    if (firstDoc is null) Check("示例文档详情 / Viewer / 标注审阅可读", false, "无示例文档");
    else
    {
        var docId = S(firstDoc, "id");
        var detail = await api.DocumentAsync(sid3, docId);
        Check("文档详情可读（含动态字段）", !string.IsNullOrWhiteSpace(S(detail, "title")),
            S(detail, "title"));
        var viewers = await api.ViewersAsync(sid3, docId);
        Check("统一 Viewer 契约可读", viewers.ToJsonString().Length > 40,
            viewers.ToJsonString().Length);
        var annotations = await api.AnnotationsAsync(sid3, docId);
        Check("标注审阅通道可读", annotations["total"] is not null,
            I(annotations, "total"));
    }
}
catch (Exception ex)
{
    Check("无未捕获异常", false, ex.ToString());
}

Console.WriteLine("\n== 结果: " + pass + " PASS / " + fail + " FAIL ==");
return fail == 0 ? 0 : 1;
