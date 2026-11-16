using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Server;

/// <summary>
/// EPISTEME_AUTH_MODE=users endpoints. Public: config / register / login.
/// Authenticated (login session): me / logout / password / me/mcp-token.
/// Admin-only: /api/v1/admin/*.
///
/// Authorization is ACCOUNT-scoped: every space has exactly one owner, and a user's
/// global role is effective inside every space they own. The v0.5 per-space grant
/// matrix (/admin/members PUT|DELETE) is disabled with an explicit error.
/// In legacy or secure mode these endpoints report AUTH_MODE_UNSUPPORTED.
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
            ["authorizationModel"] = "account",
            ["roles"] = new JsonArray("admin", "owner", "editor", "agent", "viewer"),
            ["jwtTtlSeconds"] = EpistemeConfig.JwtTtlSeconds(),
            ["mcpToken"] = new JsonObject
            {
                ["endpoint"] = "/api/v1/auth/me/mcp-token",
                ["header"] = "X-Episteme-Mcp-Token",
                ["prefix"] = IdentityStore.McpTokenPrefix,
                ["bearer"] = true,
                ["note"] = "每人一个可自助生成/重置/停用的长期 MCP 令牌；仅用于 /mcp 端点，不能调用 /api，且仅登录会话可管理"
            },
            ["product"] = "Episteme", ["version"] = EpistemeConfig.Version
        }));

        auth.MapPost("/register", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => RegisterAsync(http, store, body, ct)));

        auth.MapPost("/login", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => LoginAsync(http, store, body, ct)));

        auth.MapGet("/me", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => MeAsync(http, store, ct)));

        // Per-account UI preferences: the default workspace the homepage opens.
        auth.MapGet("/me/prefs", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => PrefsAsync(http, store, ct)));
        auth.MapPut("/me/prefs", async (HttpContext http, IdentityStore store,
                                        EpistemeService svc, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => UpdatePrefsAsync(http, store, svc, body, ct)));

        auth.MapPost("/logout", () => Results.Json(new JsonObject
        {
            ["loggedOut"] = true,
            ["note"] = "令牌为无状态 JWT，请在客户端清除本地存储；长期 MCP 令牌不受登出影响，可在设置里停用"
        }));

        auth.MapPost("/password", async (HttpContext http, IdentityStore store, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => ChangePasswordAsync(http, store, body, ct)));

        // Per-account MCP credential: view status, generate/reset, revoke. Session only.
        auth.MapGet("/me/mcp-token", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => McpTokenStatusAsync(http, store, ct)));
        auth.MapPost("/me/mcp-token", async (HttpContext http, IdentityStore store, [FromBody] JsonObject? body) =>
            await Endpoints.Run(http, ct => McpTokenRotateAsync(http, store, body ?? new JsonObject(), ct)));
        auth.MapPut("/me/mcp-token", async (HttpContext http, IdentityStore store, [FromBody] JsonObject? body) =>
            await Endpoints.Run(http, ct => McpTokenRotateAsync(http, store, body ?? new JsonObject(), ct)));
        auth.MapDelete("/me/mcp-token", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => McpTokenRevokeAsync(http, store, ct)));

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
        // Account ownership management (replaces the per-space grant matrix).
        admin.MapGet("/owners", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => ListOwnersAsync(http, store, ct)));
        admin.MapPut("/spaces/{spaceId}/owner", async (string spaceId, HttpContext http,
            IdentityStore store, EpistemeService svc, [FromBody] JsonObject body) =>
            await Endpoints.Run(http, ct => TransferOwnerAsync(http, store, svc, spaceId, body, ct)));
        // Legacy paths retained for read-only compatibility; mutations are refused.
        admin.MapGet("/members", async (HttpContext http, IdentityStore store) =>
            await Endpoints.Run(http, ct => ListLegacyMembersAsync(http, store, ct)));
        admin.MapPut("/members", (HttpContext http) => GrantRemoved());
        admin.MapDelete("/members", (HttpContext http) => GrantRemoved());

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

    /// <summary>Credential management must never be reachable with a machine token.</summary>
    private static UserPrincipal RequireSession(HttpContext http)
    {
        RequireUsers();
        var p = RequirePrincipal(http);
        if (p.IsMcpToken)
            throw new BusinessException("MCP_TOKEN_SESSION_REQUIRED",
                "管理或重置 MCP 令牌需要账号登录会话，MCP 令牌本身不能操作自己", 403);
        return p;
    }

    private static void RequireAdmin(HttpContext http)
    {
        RequireUsers();
        var p = RequirePrincipal(http);
        if (!p.IsAdmin || p.IsMcpToken)
            throw new BusinessException("ADMIN_REQUIRED", "需要管理员权限", 403);
    }

    private static UserPrincipal RequireAdminAndReturn(HttpContext http)
    {
        RequireAdmin(http);
        return RequirePrincipal(http);
    }

    /// <summary>
    /// Explicit 409 for old grant-matrix clients. Returned as a plain IResult so the
    /// single-HttpContext lambda cannot bind to the RequestDelegate overload.
    /// </summary>
    private static IResult GrantRemoved() => Results.Json(new JsonObject
    {
        ["code"] = "GRANT_MODEL_REMOVED",
        ["message"] = "已改为按账号授权：空间授权矩阵已停用。请在「管理后台 → 空间归属」转让归属，或让账号自建空间",
        ["authorizationModel"] = "account"
    }, statusCode: 409);

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

    private static string? IsoFromUnix(long unix) => unix == 0 ? null
        : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>Status view never contains the token or its digest.</summary>
    private static JsonObject McpTokenNode(McpTokenRecord? record) => new()
    {
        ["hasToken"] = record is not null,
        ["active"] = record is not null && !record.Revoked,
        ["revoked"] = record?.Revoked ?? false,
        ["hint"] = record?.TokenHint ?? "",
        ["prefix"] = IdentityStore.McpTokenPrefix,
        ["createdAt"] = record is null ? null : JsonValue.Create(record.CreatedAt),
        ["lastUsedAt"] = IsoFromUnix(record?.LastUsedUnix ?? 0) is { } used
            ? JsonValue.Create(used) : null,
        ["endpoint"] = "/api/v1/auth/me/mcp-token",
        ["bearerHeader"] = "Authorization: Bearer <epm_...>",
        ["alternateHeader"] = "X-Episteme-Mcp-Token: <epm_...>",
        ["transport"] = "POST /mcp（Streamable HTTP）, tools/git-sync.py --auth bearer",
        ["note"] = "明文只在生成/重置时返回一次，服务端仅保存 SHA-256 摘要；重置后旧令牌立即失效"
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
            // New self-registered users are ordinary users. They own their own spaces
            // and cannot see anybody else's, nor reach the admin console.
            GlobalRole = IdentityRoles.Editor, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        await store.InsertUserPublicAsync(user, ct);
        await store.TouchLoginAsync(user.Id, ct);
        var fresh = await store.FindByIdAsync(user.Id, ct) ?? user;
        var token = IdentityTokens.Issue(fresh, EpistemeConfig.AuthSecret(), EpistemeConfig.JwtTtlSeconds());
        var result = TokenBundle(fresh, token);
        result["registered"] = true;
        result["authorizationModel"] = "account";
        result["mcpToken"] = McpTokenNode(null);
        result["note"] = "已自动登录；登录后可在「我的 MCP Token」生成专属长期令牌；新建空间自动归属本账号";
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
        result["authorizationModel"] = "account";
        result["spaces"] = await OwnedSpacesNodeAsync(store, user.Id, ct);
        result["defaultSpaceId"] = await store.GetPrefAsync(user.Id, "default_space_id", ct);
        result["mcpToken"] = McpTokenNode(await store.McpTokenAsync(user.Id, ct));
        result["note"] = "登录 JWT 用于浏览器；MCP / CI 建议使用「我的 MCP Token」里的长期令牌";
        return result;
    }

    private static async Task<JsonArray> OwnedSpacesNodeAsync(
        IdentityStore store, string userId, CancellationToken ct)
    {
        var arr = new JsonArray();
        foreach (var spaceId in await store.OwnedSpaceIdsAsync(userId, ct))
            arr.Add(new JsonObject { ["spaceId"] = spaceId, ["role"] = "owner" });
        return arr;
    }

    private static async Task<JsonObject> MeAsync(HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireUsers();
        var p = RequirePrincipal(http);
        var user = await store.FindByIdAsync(p.UserId, ct)
                   ?? throw new BusinessException("UNAUTHORIZED", "账号不存在", 401);
        var owned = new JsonArray();
        foreach (var id in p.OwnedSpaceIds) owned.Add(JsonValue.Create(id));
        return new JsonObject
        {
            ["user"] = UserNode(user), ["isAdmin"] = p.IsAdmin,
            ["authorizationModel"] = "account", ["authChannel"] = p.AuthChannel,
            ["ownedSpaces"] = owned, ["ownedSpaceCount"] = p.OwnedSpaceIds.Count,
            ["mcpToken"] = McpTokenNode(await store.McpTokenAsync(user.Id, ct)),
            ["authMode"] = EpistemeConfig.AuthMode(),
            ["defaultSpaceId"] = await store.GetPrefAsync(user.Id, "default_space_id", ct)
        };
    }

    private static async Task<JsonObject> PrefsAsync(
        HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireUsers();
        var p = RequireSession(http);
        return new JsonObject
        {
            ["defaultSpaceId"] = await store.GetPrefAsync(p.UserId, "default_space_id", ct),
            ["note"] = "默认空间按账号保存在服务端；浏览器刷新或换设备都会自动进入该空间"
        };
    }

    private static async Task<JsonObject> UpdatePrefsAsync(
        HttpContext http, IdentityStore store, EpistemeService svc,
        JsonObject body, CancellationToken ct)
    {
        RequireUsers();
        var p = RequireSession(http);
        var defaultSpaceId = S(body, "defaultSpaceId");
        if (!string.IsNullOrWhiteSpace(defaultSpaceId))
        {
            var spaces = await svc.ListSpacesAsync(ct);
            var visible = spaces["items"] as JsonArray ?? new JsonArray();
            if (!visible.OfType<JsonObject>().Any(o =>
                    string.Equals(Jsonx.Plain(o["id"]), defaultSpaceId, StringComparison.Ordinal)))
                throw new BusinessException("SPACE_NOT_FOUND", "默认空间不在当前账号可见范围内", 404);
        }
        await store.SetPrefAsync(p.UserId, "default_space_id", defaultSpaceId, ct);
        return new JsonObject { ["updated"] = true, ["defaultSpaceId"] = defaultSpaceId };
    }

    private static async Task<JsonObject> ChangePasswordAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        RequireUsers();
        var p = RequireSession(http);
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
        return new JsonObject
        {
            ["changed"] = true,
            ["note"] = "已修改密码；登录 JWT 与 MCP 令牌均不受影响，可在「我的 MCP Token」单独重置"
        };
    }

    // ---------- per-account MCP token (self-service) ----------

    private static async Task<JsonObject> McpTokenStatusAsync(
        HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireUsers();
        var p = RequireSession(http);
        var node = McpTokenNode(await store.McpTokenAsync(p.UserId, ct));
        node["account"] = p.UserName;
        return node;
    }

    private static async Task<JsonObject> McpTokenRotateAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        RequireUsers();
        var p = RequireSession(http);
        var (token, hint, created, rotated) = await store.RotateMcpTokenAsync(p.UserId, ct);
        var result = new JsonObject
        {
            ["generated"] = true,
            ["rotated"] = rotated,
            ["token"] = token,
            ["tokenType"] = "Bearer",
            ["hint"] = hint,
            ["createdAt"] = DateTimeOffset.FromUnixTimeSeconds(created)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["account"] = p.UserName,
            ["authorizationModel"] = "account",
            ["bearerHeader"] = "Authorization: Bearer " + token,
            ["alternateHeader"] = "X-Episteme-Mcp-Token: " + token,
            ["invalidated"] = rotated
                ? "旧令牌已立即失效" : "此前没有令牌，本次为首次生成",
            ["note"] = "明文只在此响应里出现一次，请立即复制保存；下次只能查看状态或再次重置",
            ["usage"] = new JsonObject
            {
                ["mcp"] = "POST /mcp（Streamable HTTP / JSON-RPC tools/list / tools/call）",
                ["gitSync"] = "python3 tools/git-sync.py --auth bearer --token <明文> --space <自己空间的 id>",
                ["scope"] = "只能访问本账号名下的空间；不能管理令牌、不能进管理后台"
            }
        };
        if (S(body, "action") == "reset") result["action"] = "reset";
        return result;
    }

    private static async Task<JsonObject> McpTokenRevokeAsync(
        HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireUsers();
        var p = RequireSession(http);
        await store.RevokeMcpTokenAsync(p.UserId, ct);
        return new JsonObject
        {
            ["revoked"] = true, ["account"] = p.UserName,
            ["note"] = "令牌已停用且立即失效；需要时重新生成即可"
        };
    }

    // ---------- admin ----------

    private static async Task<JsonObject> OverviewAsync(
        HttpContext http, IdentityStore store, EpistemeService svc, CancellationToken ct)
    {
        RequireAdmin(http);
        var users = await store.ListUsersAsync(ct);
        var owners = await store.ListOwnersAsync(ct);
        var spaces = await svc.ListSpacesAsync(ct);
        return new JsonObject
        {
            ["authMode"] = EpistemeConfig.AuthMode(),
            ["authorizationModel"] = "account",
            ["users"] = users.Count,
            ["activeUsers"] = users.Count(u => u.IsActive),
            ["admins"] = users.Count(u => string.Equals(u.GlobalRole, "admin", StringComparison.OrdinalIgnoreCase)),
            ["spaces"] = spaces["total"]?.GetValue<int>() ?? 0,
            ["ownedSpaces"] = owners.Count,
            ["grants"] = 0,
            ["mcpTokens"] = await store.McpTokenCountAsync(ct),
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
        var owners = await store.ListOwnersAsync(ct);
        var items = new JsonArray();
        foreach (var u in users)
        {
            var node = UserNode(u);
            node["spaceCount"] = owners.Count(o => o.UserId == u.Id);
            node["hasMcpToken"] = (await store.McpTokenAsync(u.Id, ct)) is { Revoked: false };
            items.Add(node);
        }
        return new JsonObject { ["items"] = items, ["total"] = items.Count };
    }

    private static async Task<JsonObject> CreateUserAsync(
        HttpContext http, IdentityStore store, JsonObject body, CancellationToken ct)
    {
        RequireAdminAndReturn(http);
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
        // Optional: hand an existing space's OWNERSHIP to the new account. There is no
        // per-space grant any more, so this is a transfer, not an additional share.
        var spaceId = S(body, "spaceId");
        if (!string.IsNullOrWhiteSpace(spaceId))
        {
            await store.SetOwnerAsync(spaceId, user.Id, ct);
            result["ownershipTransferred"] = new JsonObject
            {
                ["spaceId"] = spaceId, ["ownerUserId"] = user.Id
            };
        }
        return result;
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
            ["note"] = "全局角色变更对该账号名下所有空间立即生效；已签发令牌在到期前仍带旧 uid，但服务端每次都重新读取数据库角色"
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
        return new JsonObject
        {
            ["deleted"] = true, ["id"] = id, ["grantsRemoved"] = true,
            ["note"] = "该账号名下空间已自动转交其他管理员，MCP 令牌一并删除"
        };
    }

    private static async Task<JsonObject> AdminSpacesAsync(
        HttpContext http, EpistemeService svc, IdentityStore store, CancellationToken ct)
    {
        RequireAdmin(http);
        var spaces = await svc.ListSpacesAsync(ct);
        var owners = await store.ListOwnersAsync(ct);
        var names = (await store.ListUsersAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        var items = new JsonArray();
        foreach (var s in (spaces["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var node = (JsonObject)s.DeepClone();
            var ownerId = owners.FirstOrDefault(o => o.SpaceId == Jsonx.Plain(s["id"]))?.UserId ?? "";
            node["ownerUserId"] = ownerId;
            node["ownerUserName"] = names.GetValueOrDefault(ownerId, "");
            node["memberCount"] = string.IsNullOrWhiteSpace(ownerId) ? 0 : 1;
            items.Add(node);
        }
        return new JsonObject
        {
            ["authorizationModel"] = "account", ["items"] = items, ["total"] = items.Count
        };
    }

    private static async Task<JsonObject> ListOwnersAsync(
        HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireAdmin(http);
        var owners = await store.ListOwnersAsync(ct);
        var names = (await store.ListUsersAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        var spaces = await store.OwnerMapAsync(ct);
        var items = new JsonArray();
        foreach (var o in owners)
            items.Add(new JsonObject
            {
                ["spaceId"] = o.SpaceId, ["userId"] = o.UserId,
                ["userName"] = names.GetValueOrDefault(o.UserId, o.UserId),
                ["role"] = "owner", ["createdAt"] = o.CreatedAt
            });
        return new JsonObject
        {
            ["authorizationModel"] = "account", ["items"] = items,
            ["total"] = items.Count, ["spaces"] = spaces.Count
        };
    }

    private static async Task<JsonObject> TransferOwnerAsync(
        HttpContext http, IdentityStore store, EpistemeService svc,
        string spaceId, JsonObject body, CancellationToken ct)
    {
        var p = RequireAdminAndReturn(http);
        var userId = S(body, "userId");
        if (string.IsNullOrWhiteSpace(userId))
            throw new BusinessException("BAD_REQUEST", "需要 userId（新归属账号）", 400);
        var user = await store.FindByIdAsync(userId, ct)
                   ?? throw new BusinessException("USER_NOT_FOUND", "用户不存在", 404);
        var space = await svc.GetSpaceAsync(spaceId, ct);
        await store.SetOwnerAsync(Jsonx.Plain(space["id"]), userId, ct);
        return new JsonObject
        {
            ["transferred"] = true, ["spaceId"] = Jsonx.Plain(space["id"]),
            ["ownerUserId"] = userId, ["ownerUserName"] = user.UserName,
            ["by"] = p.UserName,
            ["note"] = "归属转让后，原账号立即失去该空间的读写权限"
        };
    }

    private static async Task<JsonObject> ListLegacyMembersAsync(
        HttpContext http, IdentityStore store, CancellationToken ct)
    {
        RequireAdmin(http);
        // Read-only view of ownership, shaped like the old grant matrix so older
        // clients keep working without ever influencing authorization.
        var owners = await store.ListOwnersAsync(ct);
        var names = (await store.ListUsersAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        var items = new JsonArray();
        foreach (var o in owners)
            items.Add(new JsonObject
            {
                ["id"] = o.SpaceId, ["userId"] = o.UserId,
                ["userName"] = names.GetValueOrDefault(o.UserId, o.UserId),
                ["spaceId"] = o.SpaceId, ["role"] = "owner",
                ["grantedBy"] = "account-ownership", ["createdAt"] = o.CreatedAt
            });
        return new JsonObject
        {
            ["authorizationModel"] = "account", ["deprecated"] = true,
            ["items"] = items, ["total"] = items.Count,
            ["note"] = "已改为按账号授权：这里只展示空间归属，写入请使用 PUT /api/v1/admin/spaces/{id}/owner"
        };
    }
}
