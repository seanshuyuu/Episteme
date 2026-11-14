using Episteme.Core;

namespace Episteme.Server;

/// <summary>
/// Identity records for EPISTEME_AUTH_MODE=users. Table/column names are explicitly
/// mapped to lowercase snake_case so the idempotent CREATE TABLE IF NOT EXISTS DDL
/// works identically on SQLite and PostgreSQL (see IdentityStore.EnsureTablesAsync).
/// </summary>
public sealed class AppUser
{
    public string Id { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Base64 PBKDF2-SHA256 digest; never store a plaintext password.</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>Base64 random 16-byte salt.</summary>
    public string PasswordSalt { get; set; } = "";
    /// <summary>Global role: admin | owner | editor | agent | viewer.</summary>
    public string GlobalRole { get; set; } = "viewer";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Unix seconds of last successful login; 0 means never.</summary>
    public long LastLoginUnix { get; set; }
}

/// <summary>Per-space grant. This is the real "按空间授权" boundary.</summary>
public sealed class SpaceMember
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string SpaceId { get; set; } = "";
    /// <summary>Role inside this space: owner | editor | agent | viewer.</summary>
    public string Role { get; set; } = "viewer";
    public string GrantedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Authenticated principal attached to HttpContext.Items.</summary>
public sealed record UserPrincipal(
    string UserId,
    string UserName,
    string DisplayName,
    string GlobalRole,
    IReadOnlyDictionary<string, string> SpaceRoles)
{
    public bool IsAdmin => string.Equals(GlobalRole, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Role that downstream RequireRole sees for the current space.</summary>
    public string EffectiveRole(string? spaceId, bool globalEndpoint)
    {
        if (IsAdmin) return "owner";
        if (!globalEndpoint && !string.IsNullOrWhiteSpace(spaceId)
            && SpaceRoles.TryGetValue(spaceId, out var role)) return role;
        if (globalEndpoint) return NormalizeGlobal(GlobalRole);
        return "viewer";
    }

    private static string NormalizeGlobal(string role) => role.ToLowerInvariant() switch
    {
        "admin" => "owner", "owner" => "owner", "editor" => "editor",
        "agent" => "agent", "viewer" => "viewer", _ => "viewer"
    };
}

/// <summary>Roles accepted by the admin console, in ascending privilege order.</summary>
public static class IdentityRoles
{
    public const string Admin = "admin";
    public const string Owner = "owner";
    public const string Editor = "editor";
    public const string Agent = "agent";
    public const string Viewer = "viewer";

    public static readonly string[] All = { Admin, Owner, Editor, Agent, Viewer };
    /// <summary>Roles assignable to a SpaceMember (admin is a global-only flag).</summary>
    public static readonly string[] SpaceRoles = { Owner, Editor, Agent, Viewer };

    public static bool IsValidGlobal(string role) => All.Contains(role, StringComparer.OrdinalIgnoreCase);
    public static bool IsValidSpace(string role) => SpaceRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
    public static string Normalize(string role) => (role ?? "").Trim().ToLowerInvariant();
}
