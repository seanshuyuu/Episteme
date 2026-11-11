using System.Text.Json.Nodes;
using Episteme.UI.Avalonia.Api;
using Episteme.UI.Avalonia.ViewModels;

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

    // ---- v0.5 desktop completion: real read / write / diff / media / VM paths ----
    var caps = await api.CapabilitiesAsync();
    Check("桌面端可读 capabilities", S(caps, "version").StartsWith("0.5"), S(caps, "version"));
    var contracts = await api.ViewerContractsAsync();
    Check("桌面端可读统一 Viewer 契约", Count(contracts, "items") >= 5, Count(contracts, "items"));

    var editDocs = await api.DocumentsAsync(sid3);
    var editDoc = (editDocs["items"] as JsonArray ?? new JsonArray())
        .OfType<JsonObject>().FirstOrDefault();
    if (editDoc is null) Check("示例文档可用于桌面读写回归", false, "无示例文档");
    else
    {
        var editDocId = S(editDoc, "id");
        var renamed = await api.UpdateDocumentAsync(sid3, editDocId, new JsonObject
        {
            ["title"] = "桌面端改名", ["source"] = "desktop"
        });
        Check("桌面端更新文档标题生成 Commit",
            !string.IsNullOrWhiteSpace(S(renamed, "commitId")), S(renamed, "commitId"));

        var detail = await api.DocumentAsync(sid3, editDocId);
        var part = (detail["parts"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>().FirstOrDefault(p => S(p, "kind") is "md" or "code" or "web");
        if (part is null) Check("示例文档含可编辑 Part", false, "无可编辑 Part");
        else
        {
            var partId = S(part, "id");
            var kind = S(part, "kind");
            var saved = await api.UpdatePartAsync(sid3, editDocId, partId, new JsonObject
            {
                ["content"] = "# 桌面端编辑\n已保存", ["source"] = "desktop"
            });
            Check("桌面端保存 Part 生成 Commit",
                !string.IsNullOrWhiteSpace(S(saved, "commitId")), S(saved, "commitId"));

            var anchor = new JsonObject { ["kind"] = kind };
            if (kind == "web") anchor["domPath"] = "article/p[1]";
            else anchor["line"] = 1;
            var ann = await api.AddAnnotationAsync(sid3, editDocId, new JsonObject
            {
                ["partId"] = partId, ["kind"] = "note", ["text"] = "桌面端标注",
                ["anchor"] = anchor, ["source"] = "desktop"
            });
            Check("桌面端新增标注写进版本",
                !string.IsNullOrWhiteSpace(S(ann, "commitId")), S(ann, "commitId"));
            var rebound = await api.RebindAnnotationAsync(sid3, editDocId,
                S(ann, "annotationId"), new JsonObject
                {
                    // JsonNode instances cannot be attached to two parents; clone the anchor.
                    ["anchor"] = anchor.DeepClone(), ["text"] = "重新定位", ["source"] = "desktop"
                });
            Check("桌面端可交互式重定位标注",
                B(rebound["anchor"] as JsonObject ?? new JsonObject(), "rebound")
                && !string.IsNullOrWhiteSpace(S(rebound, "commitId")), S(rebound, "commitId"));
        }

        var captured = await api.FetchWebAsync(sid3, editDocId, new JsonObject
        {
            // Readability needs a full document (head/title/body), not a bare <article>
            // fragment; otherwise the worker honestly falls back to plain extraction.
            ["html"] = "<!doctype html><html><head><title>桌面端抓取验收</title></head><body>"
                       + "<nav>导航噪声</nav><article><h1>桌面端真实阅读器</h1>"
                       + "<p>第一段正文，用于验证独立 fetch-worker 的 Readability 抽取与白名单清洗。</p>"
                       + "<p>第二段包含 <a href=\"/next\">相对链接</a> 与足够长度的正文内容，确保抽取器不会退化到 fallback。</p>"
                       + "<script>alert('xss')</script></article></body></html>",
            ["source"] = "desktop"
        });
        Check("桌面端离线 HTML 提取走 fetch-worker",
            S(captured, "extractor") == "mozilla-readability"
            && !string.IsNullOrWhiteSpace(S(captured, "commitId")), S(captured, "extractor"));

        var commits3 = await api.CommitsAsync(sid3);
        var ids = (commits3["items"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>().Select(c => S(c, "id")).ToList();
        if (ids.Count >= 2)
        {
            var diff = await api.DiffAsync(sid3, ids[1], ids[0]);
            Check("桌面端可读取提交差异",
                diff["total"] is not null || diff["items"] is not null, I(diff, "total"));
        }
        else Check("桌面端可读取提交差异", true, "提交不足 2 条，跳过");

        var vm = new MainViewModel(api, mcp);
        Check("桌面 ViewModel 新增读写命令已注册",
            vm.SaveDocCommand is not null && vm.SavePartCommand is not null
            && vm.LoadDiffCommand is not null && vm.LoadCapabilitiesCommand is not null
            && vm.CallMcpToolCommand is not null, vm.LastAction);
        await vm.LoadAsync();
        Check("桌面 ViewModel 无 GUI 加载空间与模板",
            vm.Spaces.Count >= 1 && vm.Packs.Count == 2,
            "spaces=" + vm.Spaces.Count + " packs=" + vm.Packs.Count);
    }
}
catch (Exception ex)
{
    Check("无未捕获异常", false, ex.ToString());
}

Console.WriteLine("\n== 结果: " + pass + " PASS / " + fail + " FAIL ==");
return fail == 0 ? 0 : 1;
