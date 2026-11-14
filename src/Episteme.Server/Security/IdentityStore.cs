using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Episteme.Core;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

/// <summary>
/// Identity persistence for EPISTEME_AUTH_MODE=users.
///
/// Uses portable raw SQL (TEXT / INTEGER / CREATE TABLE IF NOT EXISTS) executed through
/// the EF DbConnection, so it works on both SQLite and PostgreSQL and — critically — on
/// databases created by an older Episteme version where EnsureCreated would never add
/// the new tables. No migrations, no new NuGet packages.
/// </summary>
public sealed class IdentityStore(IDbContextFactory<EpistemeDbContext> factory)
{
    private const int Pbkdf2Iterations = 120_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static string Iso(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static async Task<DbConnection> OpenAsync(EpistemeDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        return conn;
    }

    private static DbCommand Cmd(DbConnection conn, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static string? Str(DbDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetValue(i)?.ToString();

    private static int Int(DbDataReader r, int i) =>
        r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

    private static long Long(DbDataReader r, int i) =>
        r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    // ---------- schema + bootstrap ----------

    /// <summary>Idempotent DDL. Safe to run on every startup and against legacy databases.</summary>
    public async Task EnsureTablesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);

        var ddls = new[]
        {
            @"CREATE TABLE IF NOT EXISTS ep_users (
                id TEXT NOT NULL PRIMARY KEY,
                user_name TEXT NOT NULL,
                email TEXT NOT NULL DEFAULT '',
                display_name TEXT NOT NULL DEFAULT '',
                password_hash TEXT NOT NULL,
                password_salt TEXT NOT NULL,
                global_role TEXT NOT NULL DEFAULT 'viewer',
                is_active INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                last_login_unix INTEGER NOT NULL DEFAULT 0)",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ix_ep_users_user_name ON ep_users (user_name)",
            @"CREATE TABLE IF NOT EXISTS ep_space_members (
                id TEXT NOT NULL PRIMARY KEY,
                user_id TEXT NOT NULL,
                space_id TEXT NOT NULL,
                role TEXT NOT NULL DEFAULT 'viewer',
                granted_by TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL)",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ix_ep_members_user_space
                ON ep_space_members (user_id, space_id)",
            @"CREATE INDEX IF NOT EXISTS ix_ep_members_space ON ep_space_members (space_id)"
        };
        foreach (var sql in ddls)
        {
            await using var cmd = Cmd(conn, sql);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> AnyUserAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn, "SELECT COUNT(*) FROM ep_users");
        var raw = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(raw ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Seeds the first admin from EPISTEME_ADMIN_USER / EPISTEME_ADMIN_PASSWORD.
    /// Runs only on an empty user table, so it never overwrites a password that the
    /// administrator has since changed in the console.
    /// </summary>
    public async Task<string> SeedAdminAsync(CancellationToken ct)
    {
        var name = EpistemeConfig.AdminUser().Trim();
        var password = EpistemeConfig.AdminPassword();
        if (await AnyUserAsync(ct)) return "";
        var (hash, salt) = HashPassword(password);
        var user = new AppUser
        {
            Id = Ids.New("usr"), UserName = name, DisplayName = "系统管理员",
            Email = "", PasswordHash = hash, PasswordSalt = salt,
            GlobalRole = IdentityRoles.Admin, IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        await InsertUserPublicAsync(user, ct);
        return name;
    }

    // ---------- password ----------

    public static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? ""), salt,
            Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string hash, string salt)
    {
        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password ?? ""), saltBytes,
                Pbkdf2Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    // ---------- users ----------

    private const string UserColumns =
        "id, user_name, email, display_name, password_hash, password_salt, global_role, is_active, created_at, last_login_unix";

    private static AppUser ReadUser(DbDataReader r) => new()
    {
        Id = Str(r, 0) ?? "", UserName = Str(r, 1) ?? "", Email = Str(r, 2) ?? "",
        DisplayName = Str(r, 3) ?? "", PasswordHash = Str(r, 4) ?? "",
        PasswordSalt = Str(r, 5) ?? "", GlobalRole = Str(r, 6) ?? "viewer",
        IsActive = Int(r, 7) != 0,
        CreatedAt = DateTime.TryParse(Str(r, 8), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var created) ? created : DateTime.UtcNow,
        LastLoginUnix = Long(r, 9)
    };

    public async Task InsertUserPublicAsync(AppUser u, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "INSERT INTO ep_users (" + UserColumns + ") VALUES " +
            "(@id,@name,@email,@display,@hash,@salt,@role,@active,@created,@login)",
            ("@id", u.Id), ("@name", u.UserName), ("@email", u.Email),
            ("@display", u.DisplayName), ("@hash", u.PasswordHash), ("@salt", u.PasswordSalt),
            ("@role", u.GlobalRole), ("@active", u.IsActive ? 1 : 0),
            ("@created", Iso(u.CreatedAt)), ("@login", u.LastLoginUnix));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AppUser?> FindByNameAsync(string name, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "SELECT " + UserColumns + " FROM ep_users WHERE user_name = @name", ("@name", name));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUser(r) : null;
    }

    public async Task<AppUser?> FindByIdAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "SELECT " + UserColumns + " FROM ep_users WHERE id = @id", ("@id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUser(r) : null;
    }

    public async Task<List<AppUser>> ListUsersAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "SELECT " + UserColumns + " FROM ep_users ORDER BY created_at, user_name");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AppUser>();
        while (await r.ReadAsync(ct)) list.Add(ReadUser(r));
        return list;
    }

    public async Task<bool> UpdateUserAsync(AppUser u, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "UPDATE ep_users SET email=@email, display_name=@display, global_role=@role, " +
            "is_active=@active, password_hash=@hash, password_salt=@salt WHERE id=@id",
            ("@email", u.Email), ("@display", u.DisplayName), ("@role", u.GlobalRole),
            ("@active", u.IsActive ? 1 : 0), ("@hash", u.PasswordHash),
            ("@salt", u.PasswordSalt), ("@id", u.Id));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteUserAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var del = Cmd(conn, "DELETE FROM ep_space_members WHERE user_id=@id", ("@id", id));
        await del.ExecuteNonQueryAsync(ct);
        await using var cmd = Cmd(conn, "DELETE FROM ep_users WHERE id=@id", ("@id", id));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task TouchLoginAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "UPDATE ep_users SET last_login_unix=@now WHERE id=@id",
            ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- space grants ----------

    public async Task<Dictionary<string, string>> SpaceRolesAsync(string userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "SELECT space_id, role FROM ep_space_members WHERE user_id=@id", ("@id", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct)) map[Str(r, 0) ?? ""] = Str(r, 1) ?? "viewer";
        return map;
    }

    public async Task<List<SpaceMember>> ListMembersAsync(string? spaceId, string? userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        var sql = "SELECT id, user_id, space_id, role, granted_by, created_at FROM ep_space_members WHERE 1=1";
        var ps = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(spaceId)) { sql += " AND space_id=@space"; ps.Add(("@space", spaceId)); }
        if (!string.IsNullOrWhiteSpace(userId)) { sql += " AND user_id=@user"; ps.Add(("@user", userId)); }
        sql += " ORDER BY created_at";
        await using var cmd = Cmd(conn, sql, ps.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SpaceMember>();
        while (await r.ReadAsync(ct))
            list.Add(new SpaceMember
            {
                Id = Str(r, 0) ?? "", UserId = Str(r, 1) ?? "", SpaceId = Str(r, 2) ?? "",
                Role = Str(r, 3) ?? "viewer", GrantedBy = Str(r, 4) ?? "",
                CreatedAt = DateTime.TryParse(Str(r, 5), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.UtcNow
            });
        return list;
    }

    public async Task GrantAsync(string userId, string spaceId, string role, string grantedBy, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        // Portable upsert: delete-then-insert inside the same connection keeps both
        // providers identical without relying on ON CONFLICT (SQLite/PG syntax differs).
        await using (var del = Cmd(conn,
            "DELETE FROM ep_space_members WHERE user_id=@u AND space_id=@s",
            ("@u", userId), ("@s", spaceId)))
            await del.ExecuteNonQueryAsync(ct);
        await using var cmd = Cmd(conn,
            "INSERT INTO ep_space_members (id,user_id,space_id,role,granted_by,created_at) " +
            "VALUES (@id,@u,@s,@role,@by,@created)",
            ("@id", Ids.New("mem")), ("@u", userId), ("@s", spaceId), ("@role", role),
            ("@by", grantedBy), ("@created", Iso(DateTime.UtcNow)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RevokeAsync(string userId, string spaceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = await OpenAsync(db, ct);
        await using var cmd = Cmd(conn,
            "DELETE FROM ep_space_members WHERE user_id=@u AND space_id=@s",
            ("@u", userId), ("@s", spaceId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<UserPrincipal> PrincipalAsync(AppUser user, CancellationToken ct) =>
        new(user.Id, user.UserName, user.DisplayName, user.GlobalRole,
            await SpaceRolesAsync(user.Id, ct));
}

/// <summary>Server-issued HS256 JWT for the users auth mode.</summary>
public static class IdentityTokens
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string B64Url(byte[] raw) => Convert.ToBase64String(raw)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        text += (text.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(text);
    }

    private static string Sign(string signingInput, string secret) => B64Url(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signingInput)));

    public static string Issue(AppUser user, string secret, int ttlSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["uid"] = user.Id, ["sub"] = user.UserName, ["name"] = user.DisplayName,
            ["role"] = user.GlobalRole, ["iss"] = EpistemeConfig.JwtIssuer(),
            ["aud"] = EpistemeConfig.JwtAudience(), ["iat"] = now,
            ["exp"] = now + ttlSeconds, ["jti"] = Guid.NewGuid().ToString("N")
        }, Json));
        var input = header + "." + payload;
        return input + "." + Sign(input, secret);
    }

    /// <summary>Returns (ok, code, userId, role). Server-issued tokens require iss/aud/uid.</summary>
    public static (bool Ok, string Code, string UserId, string Role) Verify(string token, string secret)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return (false, "JWT_INVALID", "", "");
            using var header = JsonDocument.Parse(FromB64Url(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256")
                return (false, "JWT_ALG_INVALID", "", "");
            var expected = FromB64Url(Sign(parts[0] + "." + parts[1], secret));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromB64Url(parts[2])))
                return (false, "JWT_SIGNATURE_INVALID", "", "");

            using var payload = JsonDocument.Parse(FromB64Url(parts[1]));
            var root = payload.RootElement;
            if (root.TryGetProperty("exp", out var exp) &&
                exp.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return (false, "JWT_EXPIRED", "", "");
            if (!root.TryGetProperty("iss", out var iss) || iss.GetString() != EpistemeConfig.JwtIssuer())
                return (false, "JWT_ISSUER_INVALID", "", "");
            if (!root.TryGetProperty("aud", out var aud) || aud.GetString() != EpistemeConfig.JwtAudience())
                return (false, "JWT_AUDIENCE_INVALID", "", "");
            var uid = root.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(uid)) return (false, "JWT_SUBJECT_INVALID", "", "");
            var role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "viewer" : "viewer";
            return (true, "", uid, role);
        }
        catch (Exception) { return (false, "JWT_INVALID", "", ""); }
    }
}
