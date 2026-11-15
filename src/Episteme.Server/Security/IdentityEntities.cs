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

/// <summary>
/// LEGACY, read-only compatibility with databases created by v0.5. Since the
/// authorization model moved to accounts (see SpaceOwner), these rows no longer
/// grant access. They are kept only to backfill ownership on upgrade.
/// </summary>
public sealed class SpaceMember
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string Role { get; set; } = "viewer";
    public string GrantedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Account ownership of a space. Exactly one owner per space; this is THE boundary.</summary>
public sealed class SpaceOwner
{
    public string SpaceId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

/// <summary>
/// Per-account MCP credential. Only the SHA-256 digest is stored; the plaintext is
/// shown once when it is generated / reset and can never be recovered afterwards.
/// </summary>
public sealed class McpTokenRecord
{
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string TokenHint { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public long CreatedUnix { get; set; }
    public long LastUsedUnix { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>Authenticated principal attached to HttpContext.Items.</summary>
public sealed record UserPrincipal(
    string UserId,
    string UserName,
    string DisplayName,
    string GlobalRole,
    IReadOnlySet<string> OwnedSpaceIds,
    string AuthChannel = "jwt")
{
    /// <summary>jwt = browser session, mcp-token = per-account machine token, machine = bound HMAC.</summary>
    public bool IsMcpToken => string.Equals(AuthChannel, "mcp-token", StringComparison.Ordinal);
    public bool IsSession => !IsMcpToken;
    public bool IsAdmin => string.Equals(GlobalRole, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Account-scoped ownership check. Admin retains global visibility.</summary>
    public bool Owns(string? spaceId) =>
        IsAdmin || (!string.IsNullOrWhiteSpace(spaceId) && OwnedSpaceIds.Contains(spaceId!));

    /// <summary>
    /// Role that downstream RequireRole sees. Under account authorization there is no
    /// per-space role: the account's global role is effective everywhere the account
    /// is allowed to go, and admins act as owner.
    /// </summary>
    public string EffectiveRole(string? spaceId, bool globalEndpoint)
    {
        if (IsAdmin) return "owner";
        return NormalizeGlobal(GlobalRole);
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
    /// <summary>Kept for compatibility with old clients; no longer used for authorization.</summary>
    public static readonly string[] SpaceRoles = { Owner, Editor, Agent, Viewer };

    public static bool IsValidGlobal(string role) => All.Contains(role, StringComparer.OrdinalIgnoreCase);
    public static bool IsValidSpace(string role) => SpaceRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
    public static string Normalize(string role) => (role ?? "").Trim().ToLowerInvariant();
}
