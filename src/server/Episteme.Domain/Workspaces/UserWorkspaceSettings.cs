namespace Episteme.Domain.Workspaces;

public sealed class UserWorkspaceSettings
{
    private UserWorkspaceSettings() { }

    public Guid UserId { get; private set; }
    public string DefaultWorkspaceDirectory { get; private set; } = string.Empty;
    public string PreferencesJson { get; private set; } = "{}";

    public static bool IsValidDirectory(string? path)
    {
        if (path is null || path.Length > 1024 || path.StartsWith('/'))
            return false;
        if (path.Length == 0)
            return true;

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Any(character => char.IsControl(character) || character is '\\' or ':'))
                return false;
        }

        return true;
    }
}
