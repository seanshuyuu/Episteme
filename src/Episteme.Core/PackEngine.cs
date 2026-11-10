using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Episteme.Core;

/// <summary>
/// A Pack is pure declarative data: directory tree + content types + views/prompts + optional samples.
/// It never contains executable code. Installing one is a single transaction and a single import Commit.
/// </summary>
public static class PackEngine
{
    public const string SchemaId = "https://episteme.local/schema/pack/v1";
    public const int MaxNodes = 500;
    public const int MaxContentTypes = 200;
    public const int MaxSamples = 200;

    private static readonly Regex IdRe = new("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.Compiled);

    public static string Str(JsonObject o, string key) => Jsonx.Plain(o[key]) ?? "";

    /// <summary>Validate and normalize a Pack. Parents are ordered before children so installation is deterministic.</summary>
    public static (JsonObject Pack, List<string> Errors) Validate(JsonObject? raw)
    {
        var errors = new List<string>();
        var pack = raw?.DeepClone() as JsonObject ?? new JsonObject();
        if (raw is null) errors.Add("Pack 内容为空");

        var id = Str(pack, "id");
        if (!IdRe.IsMatch(id)) errors.Add("id 必须匹配 /^[a-z][a-z0-9_-]{0,63}$/");
        if (string.IsNullOrWhiteSpace(Str(pack, "name"))) errors.Add("name 不能为空");
        if (string.IsNullOrWhiteSpace(Str(pack, "version"))) pack["version"] = "1.0.0";
        if (string.IsNullOrWhiteSpace(Str(pack, "episteme"))) pack["episteme"] = ">=0.1.0";
        pack["$schema"] = SchemaId;

        var typesArray = pack["contentTypes"] as JsonArray ?? new JsonArray();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        if (typesArray.Count > MaxContentTypes)
            errors.Add($"contentTypes 超过上限 {MaxContentTypes}");
        foreach (var node in typesArray)
        {
            if (node is not JsonObject obj) { errors.Add("contentTypes 元素必须是对象"); continue; }
            var def = Jsonx.Obj<ContentTypeDefinition>(obj.ToJsonString());
            if (def is null) { errors.Add("contentType 定义无法解析"); continue; }
            foreach (var e in SchemaEngine.CheckDefinition(def)) errors.Add($"{def.Slug}: {e}");
            if (!slugs.Add(def.Slug)) errors.Add($"contentType slug 重复：{def.Slug}");
        }

        var nodesArray = pack["nodes"] as JsonArray ?? new JsonArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (nodesArray.Count > MaxNodes) errors.Add($"nodes 超过上限 {MaxNodes}");
        var ordered = new List<JsonObject>();
        foreach (var node in nodesArray)
        {
            if (node is not JsonObject obj) { errors.Add("nodes 元素必须是对象"); continue; }
            var path = Str(obj, "path");
            if (!path.StartsWith('/') || path.Contains("//") || path.Contains("..") || path.Trim('/').Length == 0)
            { errors.Add($"节点路径非法：{path}"); continue; }
            path = "/" + path.Trim('/');
            obj["path"] = path;
            if (!paths.Add(path)) { errors.Add($"节点路径重复：{path}"); continue; }
            var ct = Str(obj, "contentType");
            if (!string.IsNullOrWhiteSpace(ct) && !slugs.Contains(ct))
                errors.Add($"节点 {path} 引用了未声明的内容类型：{ct}");
            ordered.Add(obj);
        }
        ordered.Sort((a, b) => Depth(Str(a, "path")).CompareTo(Depth(Str(b, "path"))));
        var sortedNodes = new JsonArray();
        foreach (var n in ordered) sortedNodes.Add(n.DeepClone());
        pack["nodes"] = sortedNodes;

        if (pack["contentTypes"] is null) pack["contentTypes"] = new JsonArray();
        if (pack["views"] is null) pack["views"] = new JsonArray();
        if (pack["prompts"] is null) pack["prompts"] = new JsonArray();
        if (pack["jobs"] is null) pack["jobs"] = new JsonArray();

        var samples = pack["sampleDocuments"] as JsonArray ?? new JsonArray();
        if (samples.Count > MaxSamples) errors.Add($"sampleDocuments 超过上限 {MaxSamples}");
        foreach (var s in samples)
        {
            if (s is not JsonObject so) continue;
            var nodePath = "/" + Str(so, "node").Trim('/');
            var type = Str(so, "type");
            if (!paths.Contains(nodePath))
                errors.Add($"示例文档引用了未声明的节点：{Str(so, "node")}");
            if (!string.IsNullOrWhiteSpace(type) && !slugs.Contains(type))
                errors.Add($"示例文档引用了未声明的内容类型：{type}");
        }
        return (pack, errors);
    }

    private static int Depth(string path) => path.Count(c => c == '/');

    public static JsonObject Summary(JsonObject pack)
    {
        var nodes = pack["nodes"] as JsonArray ?? new JsonArray();
        var types = pack["contentTypes"] as JsonArray ?? new JsonArray();
        var samples = pack["sampleDocuments"] as JsonArray ?? new JsonArray();
        var paths = new JsonArray();
        var slugs = new JsonArray();
        foreach (var n in nodes) if (n is JsonObject no) paths.Add(Str(no, "path"));
        foreach (var t in types) if (t is JsonObject to) slugs.Add(Str(to, "slug"));
        var hasSamples = pack["sampleData"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
        return new JsonObject
        {
            ["id"] = Str(pack, "id"), ["name"] = Str(pack, "name"),
            ["version"] = Str(pack, "version"), ["description"] = Str(pack, "description"),
            ["author"] = Str(pack, "author"), ["episteme"] = Str(pack, "episteme"),
            ["nodeCount"] = nodes.Count, ["contentTypeCount"] = types.Count,
            ["sampleDocumentCount"] = samples.Count, ["sampleData"] = hasSamples,
            ["paths"] = paths, ["slugs"] = slugs
        };
    }
}
