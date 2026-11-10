using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Episteme.Core;

/// <summary>JSON helper shared by Core and Server. Web defaults: camelCase + case-insensitive read.</summary>
public static class Jsonx
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Str(object? value) => JsonSerializer.Serialize(value, Opts);

    public static T? Obj<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Opts);

    public static JsonObject Node(string? json) =>
        JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();

    public static JsonArray Array(string? json) =>
        JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray ?? new JsonArray();

    public static string Plain(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";

    public static JsonObject Clone(JsonObject source) => (JsonObject)source.DeepClone();
}

public static class Ids
{
    public static string New(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..20];
}

public sealed class Space
{
    public string Id { get; set; } = "";
    public string WorkspaceId { get; set; } = "default";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string HeadRef { get; set; } = "refs/heads/main";
    public int ToolsVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Node
{
    public string Id { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string? ParentId { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ContentTypeId { get; set; }
    public bool ExposeToMcp { get; set; } = true;
    public string? DefaultPartKind { get; set; }
    public string Icon { get; set; } = "folder";
}

public sealed class ContentType
{
    public string Id { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    public int Version { get; set; } = 1;
    public string DefinitionJson { get; set; } = "{}";
    public bool ExposeToMcp { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Document
{
    public string Id { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string ContentTypeId { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Working branch. Agent writes stay on their own ref and never touch main.</summary>
    public string Branch { get; set; } = "refs/heads/main";
    public string Title { get; set; } = "";
    public string FieldsJson { get; set; } = "{}";
    public string TagsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Part> Parts { get; set; } = new();
}

public sealed class Part
{
    public string Id { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string Kind { get; set; } = "md";
    public int Ord { get; set; }
    public string? Content { get; set; }
    public string? BlobHash { get; set; }
    public string? Mime { get; set; }
    public string? Language { get; set; }
    public string? SourceUri { get; set; }
    public string MetaJson { get; set; } = "{}";
}

public sealed class Annotation
{
    public string Id { get; set; } = "";
    public string PartId { get; set; } = "";
    public string Kind { get; set; } = "highlight";
    public string AnchorJson { get; set; } = "{}";
    public string Text { get; set; } = "";
    public string? Color { get; set; }
    public string Author { get; set; } = "user";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Content-addressed byte store. This round stores text/JSON inline; object storage is a later concern.</summary>
public sealed class Blob
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public string? Mime { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Immutable full snapshot: path -> TreeEntry. Never updated after insert.</summary>
public sealed class Tree
{
    public string Hash { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string EntriesJson { get; set; } = "{}";
    public int EntryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Commit
{
    public string Id { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string ParentsJson { get; set; } = "[]";
    public string TreeHash { get; set; } = "";
    public string Message { get; set; } = "";
    public string AuthorType { get; set; } = "user";
    public string AuthorId { get; set; } = "local";
    public string AuthorName { get; set; } = "local";
    public string Source { get; set; } = "api";
    public string Reason { get; set; } = "";
    public string? AgentId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string StatsJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RefRow
{
    public string SpaceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CommitId { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AuditLog
{
    public string Id { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? AgentId { get; set; }
    public string? CommitId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string DetailJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
