using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Episteme.Core;

/// <summary>Stable canonical serialization so identical content always yields the same hash.</summary>
public static class Canonical
{
    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string HashJson(JsonNode? node) => Hash(Normalize(node));

    public static string Normalize(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonObject obj)
        {
            var parts = obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                           .Select(kv => "\"" + kv.Key + "\":" + Normalize(kv.Value));
            return "{" + string.Join(",", parts) + "}";
        }
        if (node is JsonArray arr) return "[" + string.Join(",", arr.Select(Normalize)) + "]";
        return node.ToJsonString();
    }
}

public sealed class TreeEntry
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "document";
    public string? DocumentId { get; set; }
    public string? TypeSlug { get; set; }
    public string Title { get; set; } = "";
    public string BlobHash { get; set; } = "";

    public JsonObject ToNode() => (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(this, Jsonx.Opts)!;
}

public static class TreeStore
{
    public static Dictionary<string, TreeEntry> Parse(string? json)
    {
        var result = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        foreach (var kv in Jsonx.Node(json))
        {
            if (kv.Value is not JsonObject entry) continue;
            var parsed = Jsonx.Obj<TreeEntry>(entry.ToJsonString());
            if (parsed is not null) result[kv.Key] = parsed;
        }
        return result;
    }

    public static (string Json, int Count) Serialize(IEnumerable<TreeEntry> entries)
    {
        var obj = new JsonObject();
        var count = 0;
        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            obj[entry.Path] = entry.ToNode();
            count++;
        }
        return (obj.ToJsonString(), count);
    }
}

public sealed class FieldChange
{
    public string Field { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
}

public sealed class LineChange
{
    public string Change { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class DiffItem
{
    public string Path { get; set; } = "";
    public string Change { get; set; } = "modified";
    public string? Title { get; set; }
    public List<FieldChange> Fields { get; set; } = new();
    public List<LineChange> Lines { get; set; } = new();
}

public static class DiffService
{
    /// <summary>Field-level + line-level comparison between two immutable tree snapshots.</summary>
    public static List<DiffItem> Compare(
        Dictionary<string, TreeEntry> baseTree,
        Dictionary<string, TreeEntry> targetTree,
        Func<string, JsonObject> resolve)
    {
        var items = new List<DiffItem>();
        var paths = baseTree.Keys.Union(targetTree.Keys, StringComparer.Ordinal)
                            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            baseTree.TryGetValue(path, out var before);
            targetTree.TryGetValue(path, out var after);

            if (before is null)
            {
                items.Add(new DiffItem { Path = path, Change = "added", Title = after?.Title });
                continue;
            }
            if (after is null)
            {
                items.Add(new DiffItem { Path = path, Change = "deleted", Title = before.Title });
                continue;
            }
            if (before.BlobHash == after.BlobHash) continue;

            var item = new DiffItem { Path = path, Change = "modified", Title = after.Title };
            var oldSnapshot = resolve(before.BlobHash);
            var newSnapshot = resolve(after.BlobHash);
            var oldFields = oldSnapshot["fields"] as JsonObject ?? new JsonObject();
            var newFields = newSnapshot["fields"] as JsonObject ?? new JsonObject();

            var keys = oldFields.Select(k => k.Key).Union(newFields.Select(k => k.Key), StringComparer.Ordinal)
                               .OrderBy(k => k, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                oldFields.TryGetPropertyValue(key, out var ov);
                newFields.TryGetPropertyValue(key, out var nv);
                var b = Canonical.Normalize(ov);
                var a = Canonical.Normalize(nv);
                if (b != a) item.Fields.Add(new FieldChange { Field = key, Before = b, After = a });
            }

            var (added, removed) = LineDiff(TextOf(oldSnapshot), TextOf(newSnapshot));
            item.Lines.AddRange(removed.Select(t => new LineChange { Change = "removed", Text = t }));
            item.Lines.AddRange(added.Select(t => new LineChange { Change = "added", Text = t }));
            items.Add(item);
        }
        return items;
    }

    private static string TextOf(JsonObject snapshot)
    {
        var sb = new StringBuilder();
        if (snapshot["parts"] is JsonArray parts)
            foreach (var p in parts)
                if (p is JsonObject po && Jsonx.Plain(po["kind"]) is "md" or "code")
                    sb.AppendLine(Jsonx.Plain(po["content"]));
        return sb.ToString();
    }

    /// <summary>LCS based line diff. Small enough for document sized content.</summary>
    public static (List<string> Added, List<string> Removed) LineDiff(string before, string after)
    {
        var a = string.IsNullOrEmpty(before) ? Array.Empty<string>() : before.Replace("\r\n", "\n").Split('\n');
        var b = string.IsNullOrEmpty(after) ? Array.Empty<string>() : after.Replace("\r\n", "\n").Split('\n');
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var added = new List<string>();
        var removed = new List<string>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) removed.Add(a[x++]);
            else added.Add(b[y++]);
        }
        while (x < n) removed.Add(a[x++]);
        while (y < m) added.Add(b[y++]);
        return (added.Where(l => l.Length > 0).ToList(), removed.Where(l => l.Length > 0).ToList());
    }
}
