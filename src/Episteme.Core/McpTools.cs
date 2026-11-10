using System.Text.Json.Nodes;

namespace Episteme.Core;

public sealed record ToolDef(string Name, string Description, JsonObject InputSchema);

/// <summary>Static kernel tools + tools derived from the live ContentType set. Rebuilt on every call.</summary>
public static class ToolRegistry
{
    public static IReadOnlyList<ToolDef> StaticTools() => new List<ToolDef>
    {
        T("list_spaces", "列出可访问的空间", ("includeStats", "boolean", "附带文档与提交统计")),
        T("tree", "列出目录树（含节点绑定的内容类型与是否暴露）", ("path", "string", "起始路径"), ("depth", "integer", "深度")),
        T("read", "读取文档：fields + Parts + annotations", ("docId", "string", "文档 ID"), ("ref", "string", "历史 commitId / branch / tag")),
        T("search", "全文 / 字段 / 标签检索", ("q", "string", "关键词"), ("kind", "string", "媒介类型")),
        T("list_content_types", "列出内容类型与版本"),
        T("define_content_type", "新建或更新内容类型 Schema（需要 episteme:schema scope）", ("definition", "object", "ContentTypeDefinition")),
        T("create_node", "新建目录节点", ("path", "string", "物化路径"), ("contentType", "string", "绑定的类型 slug")),
        T("move_node", "移动或重命名目录", ("nodeId", "string", "节点 ID"), ("newPath", "string", "新路径")),
        T("delete_node", "删除目录（需 recursive 显式确认）", ("nodeId", "string", "节点 ID"), ("recursive", "boolean", "是否级联")),
        T("create_document", "通用新建文档", ("nodePath", "string", "目标节点路径"), ("title", "string", "标题"), ("fields", "object", "动态字段"), ("parts", "array", "媒介部件")),
        T("update_document", "通用更新 title / fields / tags", ("docId", "string", "文档 ID"), ("title", "string", "标题"), ("fields", "object", "动态字段")),
        T("append_part", "追加 md / code / pdf / web Part", ("docId", "string", "文档 ID"), ("kind", "string", "媒介类型"), ("content", "string", "正文")),
        T("annotate", "新增标记", ("docId", "string", "文档 ID"), ("kind", "string", "highlight / underline / note"), ("text", "string", "批注内容")),
        T("commit", "显式提交（把暂存改动打包）", ("message", "string", "提交说明")),
        T("log", "提交历史（可按 path / agentId / 时间过滤）", ("agentId", "string", "只看某 Agent"), ("limit", "integer", "条数")),
        T("diff", "两个 Commit / Ref 之间的差异", ("base", "string", "基线"), ("target", "string", "目标"), ("path", "string", "路径前缀")),
        T("branch", "创建或列出分支（需要 episteme:version scope）", ("name", "string", "分支名"), ("from", "string", "起点 commitId")),
        T("merge", "合并分支，返回冲突清单", ("from", "string", "来源分支")),
        T("revert", "反向提交，可按 agentId 批量撤销", ("commitId", "string", "目标提交"), ("agentId", "string", "批量撤销某 Agent"), ("since", "string", "起始时间")),
        T("export_as_git", "导出为真实 git 仓库（本轮返回线性化提交清单）", ("ref", "string", "导出起点"))
    };

    private static ToolDef T(string name, string description, params (string Name, string Type, string Desc)[] props)
    {
        var properties = new JsonObject();
        foreach (var (n, t, d) in props)
            properties[n] = new JsonObject { ["type"] = t, ["description"] = d };
        return new ToolDef("episteme." + name, description,
            new JsonObject { ["type"] = "object", ["properties"] = properties });
    }

    public static IReadOnlyList<ToolDef> DynamicTools(IEnumerable<(ContentTypeDefinition Def, string[] NodePaths)> bound)
    {
        var list = new List<ToolDef>();
        foreach (var (def, paths) in bound)
        {
            if (!def.ExposeToMcp) continue;
            var prefix = string.IsNullOrWhiteSpace(def.McpToolPrefix) ? def.Slug : def.McpToolPrefix;
            var defaultPath = paths.FirstOrDefault() ?? "/";
            var indexed = string.Join(" / ", def.Fields.Where(f => f.Indexed && f.ExposeToMcp).Select(f => f.Key));
            var writable = string.Join(" / ", def.Fields.Where(f => f.ExposeToMcp && f.McpWritable).Select(f => f.Key));

            list.Add(new ToolDef($"episteme.query_{prefix}",
                $"查询「{def.Label}」文档（绑定节点 {string.Join(", ", paths)}）。可筛选字段：{indexed}。",
                SchemaEngine.ToolInputSchema(def, "query", defaultPath)));

            list.Add(new ToolDef($"episteme.create_{prefix}",
                $"在节点 {defaultPath} 下新建「{def.Label}」文档。可写字段：{writable}。写入会产生一条 Commit，可用 episteme.log 查看、episteme.revert 撤销；建议在 agent/ 分支上试写。",
                SchemaEngine.ToolInputSchema(def, "create", defaultPath)));

            list.Add(new ToolDef($"episteme.update_{prefix}",
                $"更新「{def.Label}」文档的可写字段。所有字段均可选；只读字段与未暴露字段不会出现在参数中。",
                SchemaEngine.ToolInputSchema(def, "update", defaultPath)));
        }
        return list;
    }

    public static JsonObject ToMcp(ToolDef tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["inputSchema"] = (JsonObject)tool.InputSchema.DeepClone()
    };
}
