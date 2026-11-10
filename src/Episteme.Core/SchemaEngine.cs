using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Episteme.Core;

public sealed class FieldDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    public bool Unique { get; set; }
    public bool Indexed { get; set; }
    public bool ExposeToMcp { get; set; } = true;
    public bool McpWritable { get; set; } = true;
    public JsonNode? DefaultValue { get; set; }
    public string? Hint { get; set; }
    public JsonObject? Validation { get; set; }
    public JsonArray Options { get; set; } = new();
    public JsonObject? Ref { get; set; }
    public int Order { get; set; }
}

public sealed class SortDef
{
    public string Field { get; set; } = "updatedAt";
    public bool Desc { get; set; } = true;
}

public sealed class ViewDef
{
    public string Name { get; set; } = "全部";
    public JsonObject Filter { get; set; } = new();
    public List<SortDef> Sort { get; set; } = new();
}

public sealed class ContentTypeDefinition
{
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "file-text";
    public int Version { get; set; } = 1;
    public bool ExposeToMcp { get; set; } = true;
    public string McpToolPrefix { get; set; } = "";
    public string PrimaryField { get; set; } = "title";
    public string TitleTemplate { get; set; } = "{{title}}";
    public List<string> DefaultPartKinds { get; set; } = new() { "md" };
    public List<FieldDef> Fields { get; set; } = new();
    public List<ViewDef> Views { get; set; } = new();
}

/// <summary>Single source of truth for validation, dynamic rendering and MCP tool signatures.</summary>
public static class SchemaEngine
{
    public static readonly string[] KnownTypes =
    {
        "text", "longtext", "markdown", "code", "number", "date", "boolean", "select",
        "multi_select", "tags", "url", "ref", "json", "file", "user"
    };

    private static readonly Regex SlugRe = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled);
    // Field keys follow the camelCase convention used by design/schemas/*.json
    private static readonly Regex KeyRe = new("^[a-z][a-zA-Z0-9_]{0,39}$", RegexOptions.Compiled);

    public static List<string> CheckDefinition(ContentTypeDefinition def)
    {
        var errors = new List<string>();
        if (!SlugRe.IsMatch(def.Slug)) errors.Add($"slug 必须匹配 /{SlugRe}/（小写字母开头，仅 a-z0-9_）");
        if (string.IsNullOrWhiteSpace(def.Label)) errors.Add("label 不能为空");
        if (def.Fields.Count == 0) errors.Add("至少需要一个字段");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in def.Fields)
        {
            if (!KeyRe.IsMatch(f.Key)) errors.Add($"字段 key 非法：{f.Key}");
            if (!seen.Add(f.Key)) errors.Add($"字段 key 重复：{f.Key}");
            if (!KnownTypes.Contains(f.Type)) errors.Add($"字段 {f.Key} 类型未知：{f.Type}");
            if ((f.Type == "select" || f.Type == "multi_select") && f.Options.Count == 0)
                errors.Add($"字段 {f.Key} 为枚举类型，必须提供 options");
        }
        return errors;
    }

    public static void ApplyDefaults(ContentTypeDefinition def, JsonObject fields)
    {
        foreach (var f in def.Fields)
            if (!fields.ContainsKey(f.Key) && f.DefaultValue is not null)
                fields[f.Key] = f.DefaultValue.DeepClone();
    }

    public static List<string> Validate(ContentTypeDefinition def, JsonObject fields)
    {
        var errors = new List<string>();
        foreach (var f in def.Fields)
        {
            var exists = fields.TryGetPropertyValue(f.Key, out var value) && value is not null;
            if (!exists)
            {
                if (f.Required) errors.Add($"{f.Label}（{f.Key}）为必填字段");
                continue;
            }
            var error = CheckValue(f, value!);
            if (error is not null) errors.Add($"{f.Label}（{f.Key}）：{error}");
        }
        return errors;
    }

    private static string? CheckValue(FieldDef f, JsonNode node)
    {
        switch (f.Type)
        {
            case "text":
            case "longtext":
            case "markdown":
            case "url":
            case "date":
            case "ref":
            case "user":
            case "select":
            {
                if (node is not JsonValue jv || !jv.TryGetValue<string>(out var s)) return "应为字符串";
                var max = GetInt(f.Validation, "maxLength");
                if (max is > 0 && s.Length > max) return $"超过最大长度 {max}";
                var pattern = GetString(f.Validation, "pattern");
                if (pattern is not null && !Regex.IsMatch(s, pattern)) return $"不匹配校验规则 {pattern}";
                if (f.Type == "select" && f.Options.Count > 0 &&
                    !f.Options.Any(o => Jsonx.Plain(o) == s)) return "值不在枚举选项中";
                if (f.Type == "url" && !Uri.TryCreate(s, UriKind.Absolute, out _)) return "应为绝对 URL";
                return null;
            }
            case "number":
            {
                if (node is not JsonValue jv || !jv.TryGetValue<double>(out var d)) return "应为数字";
                var min = GetDouble(f.Validation, "min");
                var max = GetDouble(f.Validation, "max");
                if (min is not null && d < min) return $"不能小于 {min}";
                if (max is not null && d > max) return $"不能大于 {max}";
                if (f.Validation is not null && GetBool(f.Validation, "integer") == true && d != Math.Floor(d))
                    return "应为整数";
                return null;
            }
            case "boolean":
                return node is JsonValue bv && bv.TryGetValue<bool>(out _) ? null : "应为布尔值";
            case "tags":
            case "multi_select":
            {
                if (node is not JsonArray arr) return "应为字符串数组";
                for (var i = 0; i < arr.Count; i++)
                {
                    var s = Jsonx.Plain(arr[i]);
                    if (arr[i] is not JsonValue) return $"第 {i + 1} 项应为字符串";
                    if (f.Type == "multi_select" && f.Options.Count > 0 &&
                        !f.Options.Any(o => Jsonx.Plain(o) == s)) return $"第 {i + 1} 项不在枚举选项中";
                }
                return null;
            }
            case "code":
            case "json":
            case "file":
                return node is JsonObject ? null : "应为对象";
            default:
                return null;
        }
    }

    public static string ControlOf(string type) => type switch
    {
        "text" or "url" or "user" or "ref" => "input",
        "longtext" or "markdown" => "textarea",
        "code" or "json" or "file" => "code-json",
        "number" => "number",
        "date" => "date",
        "boolean" => "switch",
        "select" => "select",
        "multi_select" or "tags" => "tags",
        _ => "input"
    };

    /// <summary>Drives list columns, form controls, filters and reader tabs. No hand-written pages.</summary>
    public static JsonObject Render(ContentTypeDefinition def)
    {
        var columns = new JsonArray();
        var controls = new JsonArray();
        var filters = new JsonArray();
        foreach (var f in def.Fields.OrderBy(x => x.Order))
        {
            var column = new JsonObject { ["key"] = f.Key, ["label"] = f.Label, ["type"] = f.Type };
            columns.Add(column.DeepClone());
            if (f.Indexed) filters.Add(column.DeepClone());
            controls.Add(new JsonObject
            {
                ["key"] = f.Key,
                ["label"] = f.Label,
                ["control"] = ControlOf(f.Type),
                ["type"] = f.Type,
                ["required"] = f.Required,
                ["hint"] = f.Hint,
                ["options"] = f.Options.DeepClone(),
                ["exposeToMcp"] = f.ExposeToMcp,
                ["mcpWritable"] = f.McpWritable
            });
        }

        var parts = new JsonArray();
        foreach (var k in def.DefaultPartKinds) parts.Add(JsonValue.Create(k));

        return new JsonObject
        {
            ["contentType"] = def.Slug,
            ["slug"] = def.Slug,
            ["label"] = def.Label,
            ["version"] = def.Version,
            ["columns"] = columns,
            ["controls"] = controls,
            ["filters"] = filters,
            ["partKinds"] = parts,
            ["views"] = JsonSerializer.SerializeToNode(def.Views, Jsonx.Opts) ?? new JsonArray()
        };
    }

    public static JsonObject FieldSchema(FieldDef f)
    {
        var schema = new JsonObject();
        switch (f.Type)
        {
            case "number":
                schema["type"] = GetBool(f.Validation, "integer") == true ? "integer" : "number";
                if (GetDouble(f.Validation, "min") is { } min) schema["minimum"] = min;
                if (GetDouble(f.Validation, "max") is { } max) schema["maximum"] = max;
                break;
            case "boolean":
                schema["type"] = "boolean";
                break;
            case "tags":
            case "multi_select":
                schema["type"] = "array";
                schema["items"] = new JsonObject { ["type"] = "string" };
                break;
            case "select":
                schema["type"] = "string";
                schema["enum"] = f.Options.DeepClone();
                break;
            case "code":
                schema["type"] = "object";
                schema["properties"] = new JsonObject
                {
                    ["lang"] = new JsonObject { ["type"] = "string" },
                    ["code"] = new JsonObject { ["type"] = "string" }
                };
                break;
            case "json":
            case "file":
                schema["type"] = "object";
                break;
            case "date":
                schema["type"] = "string";
                schema["format"] = "date-time";
                break;
            case "url":
                schema["type"] = "string";
                schema["format"] = "uri";
                break;
            default:
                schema["type"] = "string";
                break;
        }

        if (GetInt(f.Validation, "maxLength") is { } maxLen) schema["maxLength"] = maxLen;
        if (GetString(f.Validation, "pattern") is { } pattern) schema["pattern"] = pattern;

        var parts = new List<string> { f.Label };
        if (!string.IsNullOrWhiteSpace(f.Hint)) parts.Add(f.Hint!);
        schema["description"] = string.Join(" · ", parts);
        return schema;
    }

    /// <summary>mode = query | create | update. Applies both exposure switches.</summary>
    public static JsonObject ToolInputSchema(ContentTypeDefinition def, string mode, string defaultNodePath)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        var candidates = def.Fields
            .Where(f => f.ExposeToMcp && (mode == "query" ? f.Indexed : f.McpWritable))
            .OrderBy(f => f.Order);

        foreach (var f in candidates)
        {
            properties[f.Key] = FieldSchema(f);
            if (mode == "create" && f.Required) required.Add(JsonValue.Create(f.Key));
        }

        if (mode == "query")
        {
            properties["nodePath"] = new JsonObject { ["type"] = "string", ["default"] = defaultNodePath };
            properties["ref"] = new JsonObject { ["type"] = "string", ["description"] = "读历史 commitId / branch / tag" };
            properties["page"] = new JsonObject { ["type"] = "integer", ["default"] = 1 };
            properties["pageSize"] = new JsonObject { ["type"] = "integer", ["default"] = 20 };
        }
        else
        {
            properties["title"] = new JsonObject { ["type"] = "string", ["description"] = "文档标题" };
            if (mode == "create") required.Add(JsonValue.Create("title"));
            properties["docId"] = new JsonObject { ["type"] = "string", ["description"] = "目标文档 ID" };
            if (mode == "update") required.Add(JsonValue.Create("docId"));
            properties["nodePath"] = new JsonObject { ["type"] = "string", ["default"] = defaultNodePath };
            properties["parts"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "可选媒介部件",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("md", "code", "pdf", "web", "attachment") },
                        ["content"] = new JsonObject { ["type"] = "string" },
                        ["sourceUri"] = new JsonObject { ["type"] = "string" },
                        ["language"] = new JsonObject { ["type"] = "string" }
                    }
                }
            };
            properties["branch"] = new JsonObject { ["type"] = "string", ["description"] = "目标分支，默认 refs/heads/main" };
            properties["reason"] = new JsonObject { ["type"] = "string", ["description"] = "改动理由，写入 Commit 审计" };
            properties["expectParent"] = new JsonObject { ["type"] = "string", ["description"] = "乐观锁：期望的当前 HEAD commitId" };
            properties["idempotencyKey"] = new JsonObject { ["type"] = "string", ["description"] = "幂等键，重放返回原 Commit" };
            properties["dryRun"] = new JsonObject { ["type"] = "boolean", ["description"] = "仅返回预览，不落 Commit" };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }

    /// <summary>Schema version migration: defaults, archived fields, explicit rejection of type changes.</summary>
    public static (JsonObject Fields, List<string> Errors) Migrate(
        ContentTypeDefinition oldDef, ContentTypeDefinition newDef, JsonObject oldFields)
    {
        var errors = new List<string>();
        var result = (JsonObject)oldFields.DeepClone();
        var oldMap = oldDef.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var newKeys = newDef.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var f in newDef.Fields)
        {
            if (!oldMap.TryGetValue(f.Key, out var oldField))
            {
                if (f.Required && f.DefaultValue is null)
                    errors.Add($"新增必填字段 {f.Key} 必须提供 defaultValue 或 backfill");
                else if (!result.ContainsKey(f.Key) && f.DefaultValue is not null)
                    result[f.Key] = f.DefaultValue.DeepClone();
            }
            else if (oldField.Type != f.Type)
            {
                errors.Add($"字段 {f.Key} 类型 {oldField.Type} → {f.Type} 需显式 cast 规则");
            }
        }

        var archived = new JsonObject();
        foreach (var f in oldDef.Fields.Where(f => !newKeys.Contains(f.Key)))
        {
            if (result.TryGetPropertyValue(f.Key, out var removed) && removed is not null)
            {
                archived[f.Key] = removed.DeepClone();
                result.Remove(f.Key);
            }
        }
        if (archived.Count > 0)
        {
            if (result["_archived_fields"] is JsonObject existing)
                foreach (var kv in archived) existing[kv.Key] = kv.Value?.DeepClone();
            else
                result["_archived_fields"] = archived;
        }

        return (result, errors);
    }

    private static int? GetInt(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static double? GetDouble(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static bool? GetBool(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static string? GetString(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
