using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Episteme.UI.Avalonia.Api;

namespace Episteme.UI.Avalonia.ViewModels;

/// <summary>
/// v0.5 desktop completion: real read/write flows on top of the P5 shell.
/// Kept in a separate partial file so the original P5 skeleton stays easy to diff.
/// Android head is intentionally untouched.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private string _editorTitle = "";
    public string EditorTitle { get => _editorTitle; set => Set(ref _editorTitle, value); }

    private string _editorBody = "";
    public string EditorBody { get => _editorBody; set => Set(ref _editorBody, value); }

    private string _editorPartId = "";
    public string EditorPartId { get => _editorPartId; set => Set(ref _editorPartId, value); }

    private string _editorKind = "";
    public string EditorKind { get => _editorKind; set => Set(ref _editorKind, value); }

    private string _webUrl = "";
    public string WebUrl { get => _webUrl; set => Set(ref _webUrl, value); }

    private string _annotationText = "";
    public string AnnotationText { get => _annotationText; set => Set(ref _annotationText, value); }

    private string _annotationQuote = "";
    public string AnnotationQuote { get => _annotationQuote; set => Set(ref _annotationQuote, value); }

    private string _mcpToolName = "episteme.capabilities";
    public string McpToolName { get => _mcpToolName; set => Set(ref _mcpToolName, value); }

    private string _mcpToolArgs = "{}";
    public string McpToolArgs { get => _mcpToolArgs; set => Set(ref _mcpToolArgs, value); }

    private string _diffText = "选中两个提交后查看差异。";
    public string DiffText { get => _diffText; set => Set(ref _diffText, value); }

    private string _capabilitiesText = "点击「能力」读取服务端能力开关。";
    public string CapabilitiesText { get => _capabilitiesText; set => Set(ref _capabilitiesText, value); }

    private string _readerText = "打开文档后可查看 Part 与四类阅读器契约。";
    public string ReaderText { get => _readerText; set => Set(ref _readerText, value); }

    private string _lastAction = "尚无操作";
    public string LastAction { get => _lastAction; set => Set(ref _lastAction, value); }

    private string _partCount = "0";
    public string PartCount { get => _partCount; set => Set(ref _partCount, value); }

    public ObservableCollection<PartItem> Parts { get; } = new();

    private PartItem? _selectedPart;
    public PartItem? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (!Set(ref _selectedPart, value) || value is null) return;
            EditorPartId = value.Id;
            EditorKind = value.Kind;
            EditorBody = value.Content;
            ReaderText = $"{value.Kind} Part · {value.Content.Length} 字符" +
                         (string.IsNullOrWhiteSpace(value.SourceUri) ? "" : " · " + value.SourceUri);
        }
    }

    private AnnotationItem? _selectedAnnotation;
    public AnnotationItem? SelectedAnnotation
    {
        get => _selectedAnnotation;
        set => Set(ref _selectedAnnotation, value);
    }

    public AsyncRelayCommand SaveDocCommand { get; private set; } = null!;
    public AsyncRelayCommand SavePartCommand { get; private set; } = null!;
    public AsyncRelayCommand LoadDiffCommand { get; private set; } = null!;
    public AsyncRelayCommand LoadCapabilitiesCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenReaderCommand { get; private set; } = null!;
    public AsyncRelayCommand AddAnnotationCommand { get; private set; } = null!;
    public AsyncRelayCommand RebindAnnotationCommand { get; private set; } = null!;
    public AsyncRelayCommand FetchWebCommand { get; private set; } = null!;
    public AsyncRelayCommand CallMcpToolCommand { get; private set; } = null!;

    partial void InitializeDesktopCommands()
    {
        void Fail(Exception ex) => Status = "错误：" + ex.Message;
        SaveDocCommand = new AsyncRelayCommand(SaveDocAsync) { OnError = Fail };
        SavePartCommand = new AsyncRelayCommand(SavePartAsync) { OnError = Fail };
        LoadDiffCommand = new AsyncRelayCommand(LoadDiffAsync) { OnError = Fail };
        LoadCapabilitiesCommand = new AsyncRelayCommand(LoadCapabilitiesAsync) { OnError = Fail };
        OpenReaderCommand = new AsyncRelayCommand(OpenReaderAsync) { OnError = Fail };
        AddAnnotationCommand = new AsyncRelayCommand(AddAnnotationAsync) { OnError = Fail };
        RebindAnnotationCommand = new AsyncRelayCommand(RebindAnnotationAsync) { OnError = Fail };
        FetchWebCommand = new AsyncRelayCommand(FetchWebAsync) { OnError = Fail };
        CallMcpToolCommand = new AsyncRelayCommand(CallMcpToolAsync) { OnError = Fail };
    }

    partial void OnDocDetailLoaded(JsonObject full)
    {
        Parts.Clear();
        foreach (var node in Arr(full, "parts"))
            if (node is JsonObject o)
                Parts.Add(new PartItem
                {
                    Id = S(o, "id"), Kind = S(o, "kind", "md"),
                    Ord = EpistemeApiClient.Int(o, "ord"),
                    Content = S(o, "content"), SourceUri = S(o, "sourceUri")
                });
        EditorTitle = S(full, "title", SelectedDoc?.Title ?? "");
        PartCount = Parts.Count.ToString();
        var first = Parts.FirstOrDefault(p => p.Kind is "md" or "code") ?? Parts.FirstOrDefault();
        _selectedPart = null;
        SelectedPart = first;
        if (first is null)
        {
            EditorPartId = "";
            EditorKind = "";
            EditorBody = "";
        }
    }

    private (string SpaceId, string DocId)? Target()
    {
        var space = SelectedSpace;
        var doc = SelectedDoc;
        if (space is null || doc is null) { Status = "请先选择空间与文档"; return null; }
        return (space.Id, doc.Id);
    }

    private async Task SaveDocAsync()
    {
        var target = Target();
        if (target is null) return;
        var fields = new JsonObject();
        foreach (var field in FormFields) fields[field.Key] = field.Value;
        var result = await _api.UpdateDocumentAsync(target.Value.SpaceId, target.Value.DocId,
            new JsonObject { ["title"] = EditorTitle, ["fields"] = fields, ["source"] = "desktop" });
        if (SelectedDoc is not null) SelectedDoc.Title = EditorTitle;
        LastAction = "文档已保存 · Commit " + S(result, "commitId");
        Status = LastAction;
        await LoadSpaceAsync();
    }

    private async Task SavePartAsync()
    {
        var target = Target();
        if (target is null) return;
        if (string.IsNullOrWhiteSpace(EditorPartId)) { Status = "请先选择一个 Part"; return; }
        if (EditorKind == "pdf") { Status = "PDF 原件只读；改动请以标注记录"; return; }
        var result = await _api.UpdatePartAsync(target.Value.SpaceId, target.Value.DocId, EditorPartId,
            new JsonObject { ["content"] = EditorBody, ["source"] = "desktop" });
        LastAction = "Part 已保存 · Commit " + S(result, "commitId");
        Status = LastAction;
        await LoadDocDetailAsync();
    }

    private async Task LoadDiffAsync()
    {
        var space = SelectedSpace;
        if (space is null) { Status = "请先选择空间"; return; }
        if (Commits.Count < 2) { DiffText = "至少需要两条提交才能比较。"; return; }
        var diff = await _api.DiffAsync(space.Id, Commits[1].Id, Commits[0].Id);
        DiffText = diff.ToJsonString(Pretty);
        LastAction = "已比较 " + Commits[1].Id[..Math.Min(8, Commits[1].Id.Length)] +
                     " → " + Commits[0].Id[..Math.Min(8, Commits[0].Id.Length)];
    }

    private async Task LoadCapabilitiesAsync()
    {
        var caps = await _api.CapabilitiesAsync();
        CapabilitiesText = caps.ToJsonString(Pretty);
        LastAction = $"能力 v{S(caps, "version")} · {S(caps, "authMode")} / {S(caps, "dbProvider")}";
        Status = LastAction;
    }

    private async Task OpenReaderAsync()
    {
        var target = Target();
        if (target is null) return;
        var viewers = await _api.ViewerPartsAsync(target.Value.SpaceId, target.Value.DocId);
        ReaderText = viewers.ToJsonString(Pretty);
        LastAction = $"阅读器契约 · {EpistemeApiClient.Int(viewers, "total")} 个 Part · " +
                     $"{EpistemeApiClient.Int(viewers, "anchorLost")} 条锚点待重定位";
    }

    private async Task AddAnnotationAsync()
    {
        var target = Target();
        if (target is null) return;
        if (string.IsNullOrWhiteSpace(EditorPartId)) { Status = "请先选择一个 Part"; return; }
        var anchor = new JsonObject();
        if (EditorKind is "code" or "md") anchor["line"] = 1;
        else if (EditorKind == "pdf")
        {
            anchor["page"] = 1;
            anchor["rect"] = new JsonObject { ["x"] = 72, ["y"] = 96, ["w"] = 240, ["h"] = 14 };
        }
        else anchor["domPath"] = "article/p[1]";
        if (!string.IsNullOrWhiteSpace(AnnotationQuote))
            anchor["textQuote"] = new JsonObject { ["exact"] = AnnotationQuote };

        var result = await _api.AddAnnotationAsync(target.Value.SpaceId, target.Value.DocId,
            new JsonObject
            {
                ["partId"] = EditorPartId, ["kind"] = "note", ["text"] = AnnotationText,
                ["anchor"] = anchor, ["source"] = "desktop"
            });
        LastAction = "标注已写入版本 · Commit " + S(result, "commitId");
        Status = LastAction;
        await LoadDocDetailAsync();
    }

    private async Task RebindAnnotationAsync()
    {
        var target = Target();
        if (target is null) return;
        var annotation = SelectedAnnotation;
        if (annotation is null) { Status = "请先选择一条标注"; return; }
        var anchor = new JsonObject { ["kind"] = EditorKind };
        if (EditorKind == "pdf") anchor["page"] = 1;
        else if (EditorKind is "code" or "md") anchor["line"] = 1;
        else anchor["domPath"] = "article/p[1]";
        if (!string.IsNullOrWhiteSpace(AnnotationQuote))
            anchor["textQuote"] = new JsonObject { ["exact"] = AnnotationQuote };

        var result = await _api.RebindAnnotationAsync(target.Value.SpaceId, target.Value.DocId,
            annotation.Id, new JsonObject { ["anchor"] = anchor, ["source"] = "desktop" });
        LastAction = "标注已重新定位 · Commit " + S(result, "commitId");
        Status = LastAction;
        await LoadDocDetailAsync();
    }

    private async Task FetchWebAsync()
    {
        var target = Target();
        if (target is null) return;
        var body = new JsonObject { ["source"] = "desktop" };
        if (!string.IsNullOrWhiteSpace(WebUrl)) body["url"] = WebUrl;
        else if (!string.IsNullOrWhiteSpace(EditorBody)) body["html"] = EditorBody;
        else { Status = "请填写网页 URL 或在编辑框粘贴 HTML 快照"; return; }

        var result = await _api.FetchWebAsync(target.Value.SpaceId, target.Value.DocId, body);
        LastAction = $"网页正文已入库 · {S(result, "extractor")} · Commit {S(result, "commitId")}";
        Status = LastAction;
        await LoadDocDetailAsync();
    }

    private async Task CallMcpToolAsync()
    {
        JsonObject args;
        try { args = JsonNode.Parse(McpToolArgs) as JsonObject ?? new JsonObject(); }
        catch { Status = "MCP 参数必须是合法 JSON 对象"; return; }
        var result = await _mcp.CallToolAsync(McpToolName, args, SelectedSpace?.Slug);
        McpResult = EpistemeMcpClient.Structured(result).ToJsonString(Pretty);
        LastAction = "MCP 工具 " + McpToolName + " 调用完成";
        Status = LastAction;
    }
}
