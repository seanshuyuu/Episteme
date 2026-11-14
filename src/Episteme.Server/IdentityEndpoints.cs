using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Server;

/// <summary>
/// EPISTEME_AUTH_MODE=users endpoints. Public: config / register / login.
/// Authenticated: me / logout / password. Admin-only: /api/v1/admin/*.
/// In legacy or secure mode these endpoints report AUTH_MODE_UNSUPPORTED rather than
/// silently exposing an unprotected user database.
/// </summary>
public static class IdentityEndpoints
{
    private static readonly string[] ReservedNames = { "system", "root", "episteme", "admin" };

    // Per-source-IP + account fixed window for credential guessing. The global rate
    // limiter is deliberately generous (600/min); login deserves its own tighter budget.
    private static readonly ConcurrentDictionary<string, (long Window, int Count)> LoginAttempts =
        new(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapGet("/config", () => Results.Json(new JsonObject
        {
            ["mode"] = EpistemeConfig.AuthMode(),
            ["accountsEnabled"] = EpistemeConfig.Users,
            ["registrationEnabled"] = EpistemeConfig.Users && EpistemeConfig.AllowRegistration(),
            ["roles"] = new JsonArray("admin", "owner", "editor", "agent", "viewer"),
            ["jwtTtlSeconds"] = EpistemeConfig.JwtTtlSeconds(),
            ["product"] = "Episteme", ["version"] = EpistemeConfig.Version
        }));

        auth.MapPost("/register", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => RegisterAsync(http, store, body, ct)));

        auth.MapPost("/login", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => LoginAsync(http, store, body, ct)));

        auth.MapGet("/me", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => MeAsync(http, store, ct)));

        auth.MapPost("/logout", () => Results.Json(new JsonObject
        {
            ["loggedOut"] = true,
            ["note"] = "令牌为无状态 JWT，请在客户端清除本地存储；服务端不需要会话表"
        }));

        auth.MapPost("/password", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => ChangePasswordAsync(http, store, body, ct)));

        var admin = app.MapGroup("/api/v1/admin");
        admin.MapGet("/overview", async (HttpContext http, IdentityStore store, EpistemeService svc) =>
            await Endpoints.Run(http, ct => OverviewAsync(http, store, svc, ct)));
        admin.MapGet("/users", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => ListUsersAsync(http, store, ct)));
        admin.MapPost("/users", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => CreateUserAsync(http, store, body, ct)));
        admin.MapPatch("/users/{id}", async (string id, HttpContext http, IdentityStore store,
                                            [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => UpdateUserAsync(http, store, id, body, ct)));
        admin.MapDelete("/users/{id}", async (string id, HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => DeleteUserAsync(http, store, id, ct)));
        admin.MapGet("/spaces", async (HttpContext http, EpistemeService svc, IdentityStore store) =>
            await Endpoints.Run(http, ct => AdminSpacesAsync(http, svc, store, ct)));
        admin.MapGet("/members", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => ListMembersAsync(http, store, ct)));
        admin.MapPut("/members", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => GrantAsync(http, store, body, ct)));
        admin.MapDelete("/members", async (HttpContext http, IdentityStore store, [FromBody] JsonObject? body) =>
            await Endpoints.Run(http, ct => RevokeAsync(http, store, body ?? new JsonObject(), ct)));

        return app;
    }

    // ---------- helpers ----------

    private static string S(JsonObject body, string key) => Jsonx.Plain(body[key]).Trim();

    private static void RequireUsers()
    {
        if (!EpistemeConfig.Users)
            throw new BusinessException("AUTH_MODE_UNSUPPORTED",
                "账号体系需要 EPISTEME_AUTH_MODE=users", 400);
    }

    private static UserPrincipal RequirePrincipal(HttpContext http)
    {
        if (http.Items["EpistemePrincipal"] is UserPrincipal p) return p;
        throw new BusinessException("UNAUTHORIZED", "需要登录后访问", 401);
    }

    private static void RequireAdmin(HttpContext http)
    {
        RequireUsers();
        var p = RequirePrincipal(http);
        if (!p.IsAdmin)
            throw new BusinessException("ADMIN_REQUIRED", "需要管理员权限", 403);
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 3 or > 64)
            throw new BusinessException("BAD_REQUEST", "用户名长度需为 3–64", 400);
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                throw new BusinessException("BAD_REQUEST", "用户名只能包含字母、数字、_ - .", 400);
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length < 8)
            throw new BusinessException("WEAK_PASSWORD", "密码至少 8 位", 400);
    }

    private static void CheckLoginRate(HttpContext http, string userName)
    {
        var limit = EpistemeConfig.LoginRateLimit();
        if (limit <= 0) return; // 0 disables the extra guard
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = ip + "|" + userName.ToLowerInvariant();
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var attempt = LoginAttempts.AddOrUpdate(key,
            _ => (window, 1),
            (_, old) => old.Window == window ? (window, old.Count + 1) : (window, 1));
        if (attempt.Count > limit)
            throw new BusinessException("LOGIN_RATE_LIMITED",
                $"登录尝试过于频繁，请稍后重试（每分钟最多 {limit} 次）", 429);
        if (LoginAttempts.Count > 4096)
            foreach (var pair in LoginAttempts)
                if (pair.Value.Window != window) LoginAttempts.TryRemove(pair.Key, out _);
    }

    private static JsonObject UserNode(AppUser u) => new()
    {
        ["id"] = u.Id, ["userName"] = u.UserName, ["email"] = u.Email,
        ["displayName"] = u.DisplayName, ["globalRole"] = u.GlobalRole,
        ["isActive"] = u.IsActive, ["createdAt"] = u.CreatedAt,
        ["lastLoginAt"] = u.LastLoginUnix == 0
            ? null
            : JsonValue.Create(DateTimeOffset.FromUnixTimeSeconds(u.LastLoginUnix)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))
    };

    private static JsonObject TokenBundle(AppUser user, string token)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new JsonObject
        {
            ["token"] = token, ["tokenType"] = "Bearer",
            ["expiresIn"] = EpistemeConfig.JwtTtlSeconds(),
            ["expiresAt"] = now + EpistemeConfig.JwtTtlSeconds(),
            ["user"] = UserNode(user)
        };
    }

    // ---------- public ----------

    private static async Task<JsonObject> RegisterAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        RequireUsers();
        if (!EpistemeConfig.AllowRegistration())
            throw new BusinessException("REGISTRATION_DISABLED",
                "管理员已关闭自助注册（EPISTEME_ALLOW_REGISTRATION=false）", 403);

        var name = S(body, "userName");
        var password = Jsonx.Plain(body["password"]);
        ValidateName(name);
        ValidatePassword(password);
        if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException("USERNAME_RESERVED", "该用户名为系统保留", 400);
        if (await store.FindByNameAsync(name, ct) is not null)
            throw new BusinessException("USER_EXISTS", $"用户名已存在：{name}", 409);

        var (hash, salt) = IdentityStore.HashPassword(password);
        var user = new AppUser
        {
            Id = Ids.New("usr"), UserName = name,
            Email = S(body, "email"), DisplayName = S(body, "displayName") is { Length: > 0 } d ? d : name,
            PasswordHash = hash, PasswordSalt = salt,
            // New self-registered users are ordinary users. They may create their own
            // space (and become its owner) but cannot touch other spaces or the console.
            GlobalRole = IdentityRoles.Editor, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        await store.InsertUserPublicAsync(user, ct);
        await store.TouchLoginAsync(user.Id, ct);
        var fresh = await store.FindByIdAsync(user.Id, ct) ?? user;
        var token = IdentityTokens.Issue(fresh, EpistemeConfig.AuthSecret(), EpistemeConfig.JwtTtlSeconds());
        var result = TokenBundle(fresh, token);
        result["registered"] = true;
        result["note"] = "已自动登录；新建的空间会自动授予你 owner 权限";
        return result;
    }

    private static async Task<JsonObject> LoginAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        RequireUsers();
        var name = S(body, "userName");
        var password = Jsonx.Plain(body["password"]);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
            throw new BusinessException("BAD_REQUEST", "需要 userName 与 password", 400);
        CheckLoginRate(http, name);
        var user = await store.FindByNameAsync(name, ct);
        if (user is null || !IdentityStore.VerifyPassword(password, user.PasswordHash, user.PasswordSalt))
            throw new BusinessException("INVALID_CREDENTIALS", "用户名或密码错误", 401);
        if (!user.IsActive)
            throw new BusinessException("USER_DISABLED", "该账号已被停用，请联系管理员", 401);

        await store.TouchLoginAsync(user.Id, ct);
        user.LastLoginUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = IdentityTokens.Issue(user, EpistemeConfig.AuthSecret(), EpistemeConfig.JwtTtlSeconds());
        var result = TokenBundle(user, token);
        result["spaces"] = await VisibleSpacesAsync(store, user, ct);
        return result;
    }

    private static async Task<JsonArray> VisibleSpacesAsync(IdentityStore store, AppUser user, CancellationToken ct)
    {
        var grants = await store.SpaceRolesAsync(user.Id, ct);
        var arr = new JsonArray();
        foreach (var (spaceId, role) in grants)
            arr.Add(new JsonObject { ["spaceId"] = spaceId, ["role"] = role });
        return arr;
    }

    private static async Task<JsonObject> MeAsync(HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireUsers();
        var p = RequirePrincipal(http);
        var user = await store.FindByIdAsync(p.UserId, ct)
                   ?? throw new BusinessException("UNAUTHORIZED", "账号不存在", 401);
        var roles = new JsonObject();
        foreach (var (spaceId, role) in p.SpaceRoles) roles[spaceId] = role;
        return new JsonObject
        {
            ["user"] = UserNode(user), ["isAdmin"] = p.IsAdmin,
            ["spaceRoles"] = roles, ["spaceCount"] = p.SpaceRoles.Count,
            ["authMode"] = EpistemeConfig.AuthMode()
        };
    }

    private static async Task<JsonObject> ChangePasswordAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        RequireUsers();
        var p = RequirePrincipal(http);
        var user = await store.FindByIdAsync(p.UserId, ct)
                   ?? throw new BusinessException("UNAUTHORIZED", "账号不存在", 401);
        var oldPassword = Jsonx.Plain(body["oldPassword"]);
        var newPassword = Jsonx.Plain(body["newPassword"]);
        if (!IdentityStore.VerifyPassword(oldPassword, user.PasswordHash, user.PasswordSalt))
            throw new BusinessException("INVALID_CREDENTIALS", "原密码不正确", 401);
        ValidatePassword(newPassword);
        var (hash, salt) = IdentityStore.HashPassword(newPassword);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        await store.UpdateUserAsync(user, ct);
        return new JsonObject { ["changed"] = true, ["note"] = "已修改密码，现有令牌仍然有效" };
    }

    // ---------- admin ----------

    private static async Task<JsonObject> OverviewAsync(
        HttpContext http, IdentityStore store, EpistemeService svc, CancellationToken ct)
    {
        RequireAdmin(http);
        var users = await store.ListUsersAsync(ct);
        var members = await store.ListMembersAsync(null, null, ct);
        var spaces = await svc.ListSpacesAsync(ct);
        return new JsonObject
        {
            ["authMode"] = EpistemeConfig.AuthMode(),
            ["users"] = users.Count,
            ["activeUsers"] = users.Count(u => u.IsActive),
            ["admins"] = users.Count(u => string.Equals(u.GlobalRole, "admin", StringComparison.OrdinalIgnoreCase)),
            ["spaces"] = spaces["total"]?.GetValue<int>() ?? 0,
            ["grants"] = members.Count,
            ["documents"] = (spaces["items"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>().Sum(o => o["documentCount"]?.GetValue<int>() ?? 0),
            ["registrationEnabled"] = EpistemeConfig.AllowRegistration()
        };
    }

    private static async Task<JsonObject> ListUsersAsync(
        HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireAdmin(http);
        var users = await store.ListUsersAsync(ct);
        var members = await store.ListMembersAsync(null, null, ct);
        var items = new JsonArray();
        foreach (var u in users)
        {
            var node = UserNode(u);
            node["spaceCount"] = members.Count(m => m.UserId == u.Id);
            items.Add(node);
        }
        return new JsonObject { ["items"] = items, ["total"] = items.Count };
    }

    private static async Task<JsonObject> CreateUserAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        var admin = RequireAdminAndReturn(http);
        var name = S(body, "userName");
        var password = Jsonx.Plain(body["password"]);
        ValidateName(name);
        ValidatePassword(password);
        if (await store.FindByNameAsync(name, ct) is not null)
            throw new BusinessException("USER_EXISTS", $"用户名已存在：{name}", 409);
        var role = IdentityRoles.Normalize(S(body, "globalRole"));
        if (string.IsNullOrWhiteSpace(role)) role = IdentityRoles.Viewer;
        if (!IdentityRoles.IsValidGlobal(role))
            throw new BusinessException("BAD_REQUEST", "globalRole 必须是 admin/owner/editor/agent/viewer", 400);

        var (hash, salt) = IdentityStore.HashPassword(password);
        var user = new AppUser
        {
            Id = Ids.New("usr"), UserName = name, Email = S(body, "email"),
            DisplayName = S(body, "displayName") is { Length: > 0 } d ? d : name,
            PasswordHash = hash, PasswordSalt = salt, GlobalRole = role,
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        await store.InsertUserPublicAsync(user, ct);
        var result = new JsonObject { ["created"] = true, ["user"] = UserNode(user) };
        // Optional first grant so the console can create a user + authorize in one step.
        var spaceId = S(body, "spaceId");
        var spaceRole = IdentityRoles.Normalize(S(body, "spaceRole"));
        if (!string.IsNullOrWhiteSpace(spaceId))
        {
            if (!IdentityRoles.IsValidSpace(spaceRole)) spaceRole = IdentityRoles.Viewer;
            await store.GrantAsync(user.Id, spaceId, spaceRole, admin.UserName, ct);
            result["granted"] = new JsonObject { ["spaceId"] = spaceId, ["role"] = spaceRole };
        }
        return result;
    }

    private static UserPrincipal RequireAdminAndReturn(HttpContext http)
    {
        RequireAdmin(http);
        return RequirePrincipal(http);
    }

    private static async Task<JsonObject> UpdateUserAsync(
        HttpContext http, IdentityStore store, string id, JsonObject body, CancellationToken ct)
    {
        RequireAdmin(http);
        var user = await store.FindByIdAsync(id, ct)
                   ?? throw new BusinessException("USER_NOT_FOUND", "用户不存在", 404);
        var changed = new JsonArray();

        if (body.ContainsKey("globalRole"))
        {
            var role = IdentityRoles.Normalize(S(body, "globalRole"));
            if (!IdentityRoles.IsValidGlobal(role))
                throw new BusinessException("BAD_REQUEST", "globalRole 非法", 400);
            user.GlobalRole = role;
            changed.Add("globalRole");
        }
        if (body["isActive"] is JsonValue active)
        {
            user.IsActive = active.TryGetValue<bool>(out var b) ? b : S(body, "isActive") == "1";
            changed.Add("isActive");
        }
        if (body.ContainsKey("displayName")) { user.DisplayName = S(body, "displayName"); changed.Add("displayName"); }
        if (body.ContainsKey("email")) { user.Email = S(body, "email"); changed.Add("email"); }
        if (body.ContainsKey("password"))
        {
            var password = Jsonx.Plain(body["password"]);
            ValidatePassword(password);
            var (hash, salt) = IdentityStore.HashPassword(password);
            user.PasswordHash = hash; user.PasswordSalt = salt;
            changed.Add("password");
        }
        await store.UpdateUserAsync(user, ct);
        return new JsonObject
        {
            ["updated"] = true, ["changed"] = changed, ["user"] = UserNode(user),
            ["note"] = "角色变更对该用户后续请求立即生效；已签发的令牌在到期前仍携带旧 uid，但服务端每次都会重新读取数据库角色"
        };
    }

    private static async Task<JsonObject> DeleteUserAsync(
        HttpContext http, IdentityStore store, string id, CancellationToken ct)
    {
        var p = RequireAdminAndReturn(http);
        if (id == p.UserId)
            throw new BusinessException("SELF_DELETE_FORBIDDEN", "不能删除当前登录的管理员账号", 400);
        if (!await store.DeleteUserAsync(id, ct))
            throw new BusinessException("USER_NOT_FOUND", "用户不存在", 404);
        return new JsonObject { ["deleted"] = true, ["id"] = id, ["grantsRemoved"] = true };
    }

    private static async Task<JsonObject> AdminSpacesAsync(
        HttpContext http, EpistemeService svc, IdentityStore store, CancellationToken ct)
    {
        RequireAdmin(http);
        var spaces = await svc.ListSpacesAsync(ct);
        var members = await store.ListMembersAsync(null, null, ct);
        var items = new JsonArray();
        foreach (var s in (spaces["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var node = (JsonObject)s.DeepClone();
            node["memberCount"] = members.Count(m => m.SpaceId == Jsonx.Plain(s["id"]));
            items.Add(node);
        }
        return new JsonObject { ["items"] = items, ["total"] = items.Count };
    }

    private static async Task<JsonObject> ListMembersAsync(
        HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireAdmin(http);
        var spaceId = http.Request.Query["spaceId"].ToString();
        var userId = http.Request.Query["userId"].ToString();
        var members = await store.ListMembersAsync(
            string.IsNullOrWhiteSpace(spaceId) ? null : spaceId,
            string.IsNullOrWhiteSpace(userId) ? null : userId, ct);
        var names = (await store.ListUsersAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        var items = new JsonArray();
        foreach (var m in members)
            items.Add(new JsonObject
            {
                ["id"] = m.Id, ["userId"] = m.UserId,
                ["userName"] = names.GetValueOrDefault(m.UserId, m.UserId),
                ["spaceId"] = m.SpaceId, ["role"] = m.Role,
                ["grantedBy"] = m.GrantedBy, ["createdAt"] = m.CreatedAt
            });
        return new JsonObject { ["items"] = items, ["total"] = items.Count };
    }

    private static async Task<JsonObject> GrantAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        var p = RequireAdminAndReturn(http);
        var userId = S(body, "userId");
        var spaceId = S(body, "spaceId");
        var role = IdentityRoles.Normalize(S(body, "role"));
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(spaceId))
            throw new BusinessException("BAD_REQUEST", "需要 userId 与 spaceId", 400);
        if (!IdentityRoles.IsValidSpace(role))
            throw new BusinessException("BAD_REQUEST", "role 必须是 owner/editor/agent/viewer", 400);
        if (await store.FindByIdAsync(userId, ct) is null)
            throw new BusinessException("USER_NOT_FOUND", "用户不存在", 404);
        await store.GrantAsync(userId, spaceId, role, p.UserName, ct);
        return new JsonObject
        {
            ["granted"] = true, ["userId"] = userId, ["spaceId"] = spaceId,
            ["role"] = role, ["grantedBy"] = p.UserName
        };
    }

    private static async Task<JsonObject> RevokeAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        RequireAdmin(http);
        var userId = S(body, "userId");
        if (string.IsNullOrWhiteSpace(userId)) userId = http.Request.Query["userId"].ToString();
        var spaceId = S(body, "spaceId");
        if (string.IsNullOrWhiteSpace(spaceId)) spaceId = http.Request.Query["spaceId"].ToString();
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(spaceId))
            throw new BusinessException("BAD_REQUEST", "需要 userId 与 spaceId", 400);
        var removed = await store.RevokeAsync(userId, spaceId, ct);
        return new JsonObject { ["revoked"] = removed, ["userId"] = userId, ["spaceId"] = spaceId };
    }
}
