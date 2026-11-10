using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Episteme.UI.Avalonia.Api;

namespace Episteme.UI.Avalonia.ViewModels;

/// <summary>A single Schema-driven input row. Controls are generated from /render, not hard-coded.</summary>
public sealed class FieldInput : ObservableObject
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Control { get; set; } = "text";
    public string Hint { get; set; } = "";
    public bool Required { get; set; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public string DisplayLabel => (string.IsNullOrWhiteSpace(Label) ? Key : Label) + (Required ? " *" : "");
}

/// <summary>
/// P5 shared ViewModel for Desktop + Android. Everything is loaded from the live Episteme REST/MCP
/// contract: dynamic tree, Schema-driven form, version log, annotation review and Pack templates.
/// No domain vocabulary is hard-coded into the shell (Empty by default).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly EpistemeApiClient _api;
    private readonly EpistemeMcpClient _mcp;
    private string _primaryField = "";

    public ObservableCollection<SpaceItem> Spaces { get; } = new();
    public ObservableCollection<NodeItem> Nodes { get; } = new();
    public ObservableCollection<DocItem> Documents { get; } = new();
    public ObservableCollection<CommitItem> Commits { get; } = new();
    public ObservableCollection<PackItem> Packs { get; } = new();
    public ObservableCollection<TypeItem> Types { get; } = new();
    public ObservableCollection<ToolItem> Tools { get; } = new();
    public ObservableCollection<AnnotationItem> Annotations { get; } = new();
    public ObservableCollection<FieldInput> FormFields { get; } = new();

    public string Title => "Episteme · 知书";

    private string _status = "正在连接服务…";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _detail = "选择一个目录或文档查看详情。";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private string _mcpResult = "点击「调用 MCP」验证动态工具通道。";
    public string McpResult { get => _mcpResult; set => Set(ref _mcpResult, value); }

    private string _packJson = "";
    public string PackJson { get => _packJson; set => Set(ref _packJson, value); }

    private string _formTitle = "未命名文档";
    public string FormTitle { get => _formTitle; set => Set(ref _formTitle, value); }

    private string _formBody = "";
    public string FormBody { get => _formBody; set => Set(ref _formBody, value); }

    private string _renderLabel = "动态表单 · 未选择目录";
    public string RenderLabel { get => _renderLabel; set => Set(ref _renderLabel, value); }

    private IBrush _appBackground = new SolidColorBrush(Color.FromRgb(242, 247, 243));
    public IBrush AppBackground { get => _appBackground; set => Set(ref _appBackground, value); }

    private IBrush _cardBackground = new SolidColorBrush(Colors.White);
    public IBrush CardBackground { get => _cardBackground; set => Set(ref _cardBackground, value); }

    private IBrush _mutedBrush = new SolidColorBrush(Color.FromRgb(107, 127, 116));
    public IBrush MutedBrush { get => _mutedBrush; set => Set(ref _mutedBrush, value); }

    private IBrush _borderBrush = new SolidColorBrush(Color.FromRgb(220, 233, 224));
    public IBrush BorderBrush { get => _borderBrush; set => Set(ref _borderBrush, value); }

    private SpaceItem? _selectedSpace;
    public SpaceItem? SelectedSpace
    {
        get => _selectedSpace;
        set { if (Set(ref _selectedSpace, value)) _ = LoadSpaceAsync(); }
    }

    private NodeItem? _selectedNode;
    public NodeItem? SelectedNode
    {
        get => _selectedNode;
        set { if (Set(ref _selectedNode, value)) _ = LoadNodeAsync(); }
    }

    private DocItem? _selectedDoc;
    public DocItem? SelectedDoc
    {
        get => _selectedDoc;
        set { if (Set(ref _selectedDoc, value)) _ = LoadDocDetailAsync(); }
    }

    private PackItem? _selectedPack;
    public PackItem? SelectedPack { get => _selectedPack; set => Set(ref _selectedPack, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ThemeCommand { get; }
    public AsyncRelayCommand PreviewPackCommand { get; }
    public AsyncRelayCommand InstallPackCommand { get; }
    public AsyncRelayCommand ExportPackCommand { get; }
    public AsyncRelayCommand ImportPackCommand { get; }
    public AsyncRelayCommand CreateDocCommand { get; }
    public AsyncRelayCommand ReviewCommand { get; }
    public AsyncRelayCommand McpCommand { get; }

    public MainViewModel(EpistemeApiClient api, EpistemeMcpClient? mcp = null)
    {
        _api = api;
        _mcp = mcp ?? new EpistemeMcpClient(api.BaseUrl);

        void Fail(Exception ex) => Status = "错误：" + ex.Message;

        RefreshCommand = new AsyncRelayCommand(LoadAsync) { OnError = Fail };
        ThemeCommand = new AsyncRelayCommand(p =>
        {
            ApplyTheme(p as string ?? "mint");
            return Task.CompletedTask;
        }) { OnError = Fail };
        PreviewPackCommand = new AsyncRelayCommand(p => PreviewPackAsync(p as PackItem)) { OnError = Fail };
        InstallPackCommand = new AsyncRelayCommand(p => InstallPackAsync(p as PackItem)) { OnError = Fail };
        ExportPackCommand = new AsyncRelayCommand(ExportPackAsync) { OnError = Fail };
        ImportPackCommand = new AsyncRelayCommand(ImportPackAsync) { OnError = Fail };
        CreateDocCommand = new AsyncRelayCommand(CreateDocAsync) { OnError = Fail };
        ReviewCommand = new AsyncRelayCommand(ReviewAsync) { OnError = Fail };
        McpCommand = new AsyncRelayCommand(McpProbeAsync) { OnError = Fail };
    }

    private void ApplyTheme(string key)
    {
        Color bg, card, muted, border;
        switch (key)
        {
            case "sky":
                bg = Color.FromRgb(241, 246, 251); card = Colors.White;
                muted = Color.FromRgb(107, 127, 146); border = Color.FromRgb(216, 230, 242);
                break;
            case "lilac":
                bg = Color.FromRgb(246, 243, 251); card = Colors.White;
                muted = Color.FromRgb(124, 113, 145); border = Color.FromRgb(230, 220, 242);
                break;
            case "blush":
                bg = Color.FromRgb(251, 243, 244); card = Colors.White;
                muted = Color.FromRgb(146, 118, 123); border = Color.FromRgb(240, 221, 224);
                break;
            default:
                bg = Color.FromRgb(242, 247, 243); card = Colors.White;
                muted = Color.FromRgb(107, 127, 116); border = Color.FromRgb(220, 233, 224);
                key = "mint";
                break;
        }
        AppBackground = new SolidColorBrush(bg);
        CardBackground = new SolidColorBrush(card);
        MutedBrush = new SolidColorBrush(muted);
        BorderBrush = new SolidColorBrush(border);
        Status = "主题已切换：" + key;
    }

    private static JsonArray Arr(JsonObject obj, string key) => obj[key] as JsonArray ?? new JsonArray();
    private static string S(JsonObject obj, string key, string fallback = "") =>
        EpistemeApiClient.Str(obj, key, fallback);
    private static bool Bool(JsonObject obj, string key, bool fallback = false) =>
        obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public async Task LoadAsync()
    {
        Status = "连接 " + _api.BaseUrl + " …";
        var health = await _api.HealthAsync();
        var spaces = await _api.SpacesAsync();
        Spaces.Clear();
        foreach (var n in Arr(spaces, "items"))
            if (n is JsonObject o)
                Spaces.Add(new SpaceItem { Id = S(o, "id"), Slug = S(o, "slug"), Name = S(o, "name") });

        if (Spaces.Count == 0)
        {
            var created = await _api.CreateSpaceAsync("我的资料库", "default-library");
            Spaces.Add(new SpaceItem { Id = S(created, "id"), Slug = S(created, "slug"), Name = S(created, "name") });
        }

        var packs = await _api.PacksAsync();
        Packs.Clear();
        foreach (var n in Arr(packs, "items"))
            if (n is JsonObject o)
                Packs.Add(new PackItem
                {
                    Id = S(o, "id"), Name = S(o, "name"),
                    NodeCount = EpistemeApiClient.Int(o, "nodeCount"),
                    ContentTypeCount = EpistemeApiClient.Int(o, "contentTypeCount"),
                    Valid = Bool(o, "valid", true)
                });

        SelectedSpace = Spaces[0];
        SelectedPack = Packs.FirstOrDefault();
        Status = $"已连接 v{S(health, "version")} · {Spaces.Count} 空间 · {Packs.Count} 套模板";
    }

    private async Task LoadSpaceAsync()
    {
        var space = SelectedSpace;
        if (space is null) return;
        try
        {
            var tree = await _api.TreeAsync(space.Id);
            Nodes.Clear();
            foreach (var n in Arr(tree, "items"))
                if (n is JsonObject o)
                {
                    var path = S(o, "path");
                    Nodes.Add(new NodeItem
                    {
                        Id = S(o, "id"), Path = path, Name = S(o, "name"),
                        ContentTypeId = S(o, "contentType")
                    });
                }

            var types = await _api.ContentTypesAsync(space.Id);
            Types.Clear();
            foreach (var n in Arr(types, "items"))
                if (n is JsonObject o)
                {
                    var def = o["definition"] as JsonObject ?? o;
                    Types.Add(new TypeItem
                    {
                        Slug = S(o, "slug", S(def, "slug")),
                        Label = S(o, "label", S(def, "label")),
                        FieldCount = Arr(def, "fields").Count
                    });
                }

            await FillDocuments(await _api.DocumentsAsync(space.Id));

            var commits = await _api.CommitsAsync(space.Id);
            Commits.Clear();
            foreach (var n in Arr(commits, "items"))
                if (n is JsonObject o)
                    Commits.Add(new CommitItem
                    {
                        Id = S(o, "id"), Message = S(o, "message"), Source = S(o, "source")
                    });

            var tools = await _api.McpToolsAsync(space.Id);
            Tools.Clear();
            foreach (var n in Arr(tools, "tools"))
                if (n is JsonObject o)
                    Tools.Add(new ToolItem { Name = S(o, "name"), Description = S(o, "description") });

            _selectedNode = null;
            SelectedNode = Nodes.FirstOrDefault();
            Detail = $"空间 {space.Display}\n{Nodes.Count} 目录 · {Types.Count} 内容类型 · " +
                     $"{Documents.Count} 文档 · {Commits.Count} 提交 · {Tools.Count} MCP 工具";
        }
        catch (Exception ex) { Status = "加载空间失败：" + ex.Message; }
    }

    private Task FillDocuments(JsonObject result)
    {
        Documents.Clear();
        foreach (var n in Arr(result, "items"))
            if (n is JsonObject o)
                Documents.Add(new DocItem
                {
                    Id = S(o, "id"), Title = S(o, "title"), NodeId = S(o, "nodeId"),
                    TypeSlug = S(o, "contentType", S(o, "typeSlug"))
                });
        return Task.CompletedTask;
    }

    private async Task LoadNodeAsync()
    {
        var node = SelectedNode;
        var space = SelectedSpace;
        if (node is null || space is null) return;
        try
        {
            var render = await _api.RenderAsync(space.Id, node.Id);
            var slug = S(render, "contentType");
            FormFields.Clear();
            _primaryField = "";
            if (slug.Length > 0)
            {
                RenderLabel = $"动态表单 · {S(render, "label", slug)} v{S(render, "version")}";
                foreach (var c in Arr(render, "controls"))
                    if (c is JsonObject o)
                    {
                        var key = S(o, "key");
                        if (Bool(o, "required") && _primaryField.Length == 0) _primaryField = key;
                        FormFields.Add(new FieldInput
                        {
                            Key = key, Label = S(o, "label"), Control = S(o, "control", "text"),
                            Hint = S(o, "hint"), Required = Bool(o, "required")
                        });
                    }
            }
            else
            {
                RenderLabel = "动态表单 · 该目录未绑定内容类型";
            }
            await FillDocuments(await _api.NodeDocumentsAsync(space.Id, node.Id));
            Detail = $"目录 {node.Path}\n绑定类型：{(slug.Length > 0 ? slug : "无")}\n" +
                     $"Schema 字段 {FormFields.Count} 个 · 文档 {Documents.Count} 篇";
        }
        catch (Exception ex) { Status = "加载目录失败：" + ex.Message; }
    }

    private async Task LoadDocDetailAsync()
    {
        var doc = SelectedDoc;
        var space = SelectedSpace;
        if (doc is null || space is null) return;
        try
        {
            var full = await _api.DocumentAsync(space.Id, doc.Id);
            var parts = Arr(full, "parts");
            var fields = full["fields"] as JsonObject ?? new JsonObject();
            Detail = $"文档 {S(full, "title", doc.Title)}\n分支 {S(full, "branch", "main")}\n" +
                     $"Part {parts.Count} 个 · 字段 {fields.Count} 个\n\n" + fields.ToJsonString();

            var annotations = await _api.AnnotationsAsync(space.Id, doc.Id);
            Annotations.Clear();
            foreach (var n in Arr(annotations, "items"))
                if (n is JsonObject o)
                    Annotations.Add(new AnnotationItem
                    {
                        Id = S(o, "id"), Kind = S(o, "kind"),
                        Text = S(o, "text", S(o, "body", S(o, "note")))
                    });
            Status = $"审阅：{doc.Display} · {Annotations.Count} 条标注";
        }
        catch (Exception ex) { Status = "加载文档失败：" + ex.Message; }
    }

    private async Task CreateDocAsync()
    {
        var node = SelectedNode;
        var space = SelectedSpace;
        if (node is null || space is null) { Status = "请先选择一个目录"; return; }
        var fields = new JsonObject();
        foreach (var f in FormFields)
        {
            if (f.Value.Length == 0) continue;
            fields[f.Key] = f.Value;
        }
        if (_primaryField.Length > 0 && !fields.ContainsKey(_primaryField))
            fields[_primaryField] = FormTitle;
        var parts = new JsonArray();
        parts.Add(new JsonObject { ["kind"] = "md", ["content"] = FormBody });
        var payload = new JsonObject
        {
            ["title"] = FormTitle, ["fields"] = fields, ["parts"] = parts, ["source"] = "desktop"
        };
        var result = await _api.CreateDocumentAsync(space.Id, node.Id, payload);
        Status = "已创建文档 · Commit " + S(result, "commitId");
        await LoadSpaceAsync();
    }

    private async Task PreviewPackAsync(PackItem? pack)
    {
        pack ??= SelectedPack;
        var space = SelectedSpace;
        if (pack is null || space is null) { Status = "请先选择模板"; return; }
        var result = await _api.InstallPackAsync(space.Id, new JsonObject
        {
            ["packId"] = pack.Id, ["dryRun"] = true, ["source"] = "desktop"
        });
        Detail = $"模板预览 {pack.Id}\n将创建 {EpistemeApiClient.Int(result, "wouldCreateNodes")} 目录 / " +
                 $"{EpistemeApiClient.Int(result, "wouldCreateContentTypes")} 内容类型 / " +
                 $"{EpistemeApiClient.Int(result, "wouldCreateSampleDocuments")} 示例文档\n" +
                 $"冲突 {Arr(result, "conflicts").Count} 项";
        Status = "dryRun 预览完成，未写入任何数据";
    }

    private async Task InstallPackAsync(PackItem? pack)
    {
        pack ??= SelectedPack;
        var space = SelectedSpace;
        if (pack is null || space is null) { Status = "请先选择模板"; return; }
        var result = await _api.InstallPackAsync(space.Id, new JsonObject
        {
            ["packId"] = pack.Id, ["source"] = "desktop"
        });
        Status = $"已安装 {pack.Id} · {EpistemeApiClient.Int(result, "createdNodes")} 目录 · Commit " +
                 S(result, "commitId");
        await LoadSpaceAsync();
    }

    private async Task ExportPackAsync()
    {
        var space = SelectedSpace;
        if (space is null) return;
        var result = await _api.ExportPackAsync(space.Id, new JsonObject
        {
            ["name"] = space.Display + " 模板", ["source"] = "desktop", ["includeDocuments"] = true
        });
        PackJson = S(result, "json");
        Status = $"已导出 Pack JSON（{PackJson.Length} 字符），可编辑后导入";
    }

    private async Task ImportPackAsync()
    {
        var space = SelectedSpace;
        if (space is null) return;
        var parsed = JsonNode.Parse(PackJson) as JsonObject
                     ?? throw new EpistemeApiException(422, "PACK_INVALID", "Pack JSON 不是合法对象");
        var result = await _api.ImportPackAsync(space.Id, new JsonObject
        {
            ["pack"] = parsed, ["source"] = "desktop", ["overwrite"] = true
        });
        Status = $"已导入 Pack · {EpistemeApiClient.Int(result, "createdNodes")} 目录 · Commit " +
                 S(result, "commitId");
        await LoadSpaceAsync();
    }

    private async Task ReviewAsync()
    {
        var doc = SelectedDoc;
        var space = SelectedSpace;
        if (doc is null || space is null) { Status = "请先选择文档"; return; }
        await LoadDocDetailAsync();
    }

    private async Task McpProbeAsync()
    {
        var space = SelectedSpace?.Slug;
        var init = await _mcp.InitializeAsync(space);
        var tools = await _mcp.ToolsListAsync(space);
        var names = Arr(tools, "tools");
        var packs = EpistemeMcpClient.Structured(
            await _mcp.CallToolAsync("episteme.list_packs", new JsonObject(), space));
        McpResult = $"MCP {S(init, "protocolVersion")} · {names.Count} 个动态工具\n" +
                    $"episteme.list_packs → {EpistemeApiClient.Int(packs, "total")} 套模板\n" +
                    (names.Count > 0 ? "首项：" + S(names[0] as JsonObject ?? new JsonObject(), "name") : "");
    }
}
