using System.Text.Json;
using System.Text.Json.Serialization;

namespace Episteme.Domain.Workspaces;

/// <summary>Versioned, non-sensitive workspace settings; never use these fields for credentials.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceConfiguration(
    int SchemaVersion,
    string? DefaultExtension = null,
    string? Language = null,
    string? SkillFormat = null,
    string? CitationStyle = null,
    string? Genre = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false
    };

    public static WorkspaceConfiguration Empty { get; } = new(1);

    public bool IsValid() => SchemaVersion == 1 &&
        ValidValue(DefaultExtension) && ValidValue(Language) && ValidValue(SkillFormat) &&
        ValidValue(CitationStyle) && ValidValue(Genre);

    public string ToJson()
    {
        if (!IsValid())
            throw new ArgumentException("Invalid workspace configuration.");
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static WorkspaceConfiguration Parse(string json)
    {
        // Existing workspaces store an empty object, before configuration schemas were exposed.
        if (json == "{}")
            return Empty;
        var result = JsonSerializer.Deserialize<WorkspaceConfiguration>(json, JsonOptions);
        if (result is null || !result.IsValid())
            throw new InvalidOperationException("Stored workspace configuration is invalid.");
        return result;
    }

    private static bool ValidValue(string? value) => value is null ||
        (value.Length is >= 1 and <= 80 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+' or '#'));
}
