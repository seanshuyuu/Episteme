using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Episteme.Core;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

namespace Episteme.Server;

/// <summary>
/// Opt-in production security. EPISTEME_AUTH_MODE=secure enables:
///   - JWT (HS256) bearer tokens, or API-key HMAC-SHA256 request signatures
///   - timestamp skew check + single-use nonce replay guard
///   - fixed-window rate limiting on /api and /mcp
/// EPISTEME_AUTH_MODE=legacy (default) registers nothing at all, so all existing
/// contracts and regressions keep working unchanged.
/// </summary>
public static class EpistemeSecurity
{
    private static readonly ConcurrentDictionary<string, long> UsedNonces = new(StringComparer.Ordinal);

    private static readonly string[] ProtectedPrefixes = { "/api/", "/mcp" };

    /// <summary>Public auth endpoints. They must be reachable before a login token exists.</summary>
    private static readonly string[] PublicAuthPaths =
    {
        "/api/v1/auth/config", "/api/v1/auth/register", "/api/v1/auth/login"
    };

    private static (bool Ok, int Status, string Code, string Message, string Role) Ok(string role) =>
        (true, 200, "", "", role);

    private static (bool Ok, int Status, string Code, string Message, string Role) Fail(
        int status, string code, string message) => (false, status, code, message, "");

    public static void Add(WebApplicationBuilder builder)
    {
        if (!EpistemeConfig.Guarded) return;
        // IdentityStore / IHttpContextAccessor are registered unconditionally in Program.cs
        // because EpistemeService depends on them in every auth mode.
        var permits = EpistemeConfig.RateLimitPerMinute();
        var window = new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        };
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("episteme-secure", limiter =>
            {
                limiter.PermitLimit = permits;
                limiter.Window = window.Window;
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
            // A named policy alone does nothing until it is attached to an endpoint via
            // RequireRateLimiting(). UseWhen() branches have no endpoint metadata yet,
            // so install the same window as the global limiter instead.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetFixedWindowLimiter("episteme-global", __ => window));
            options.OnRejected = async (ctx, token) =>
            {
                ctx.HttpContext.Response.StatusCode = 429;
                ctx.HttpContext.Response.Headers["Retry-After"] = "60";
                await ctx.HttpContext.Response.WriteAsync(
                    "{\"code\":\"RATE_LIMITED\",\"message\":\"请求过于频繁，请稍后重试\"}", token);
            };
        });
    }

    public static void Use(WebApplication app)
    {
        if (!EpistemeConfig.Guarded) return;
        app.UseWhen(ctx => IsProtected(ctx.Request), branch =>
        {
            branch.UseRateLimiter();
            branch.Use(async (ctx, next) =>
            {
                var result = EpistemeConfig.Users
                    ? await AuthenticateUsersAsync(ctx)
                    : await AuthenticateAsync(ctx);
                if (!result.Ok)
                {
                    await Reject(ctx, result.Status, result.Code, result.Message);
                    return;
                }
                // Downstream (Endpoints.Ctx / ToolsCall) keeps reading X-Episteme-Role.
                // In users mode the role is derived from the server-side principal, never
                // from a client-supplied header, so a forged X-Episteme-Role cannot escalate.
                ctx.Request.Headers["X-Episteme-Role"] = result.Role;
                ctx.Items["EpistemeRole"] = result.Role;
                await next();
            });
        });
    }

    private static bool IsProtected(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        foreach (var pub in PublicAuthPaths)
            if (path.Equals(pub, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var prefix in ProtectedPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task Reject(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new JsonObject
        {
            ["code"] = code, ["message"] = message, ["traceId"] = ctx.TraceIdentifier
        });
    }

    /// <summary>
    /// users mode is ACCOUNT-scoped. Three credential channels all resolve to a live
    /// server-side principal:
    ///   - login JWT (browser session; the only channel allowed to manage credentials)
    ///   - per-account MCP token, epm_... (MCP / git-sync / CI; account-owned spaces only)
    ///   - HMAC, only when explicitly bound to an account via EPISTEME_MACHINE_USER
    /// The role written downstream is always derived from the account, never from a
    /// client-supplied header or a per-space grant.
    /// </summary>
    private static async Task<(bool Ok, int Status, string Code, string Message, string Role)>
        AuthenticateUsersAsync(HttpContext ctx)
    {
        var secret = EpistemeConfig.AuthSecret();
        if (string.IsNullOrWhiteSpace(secret))
            return Fail(500, "AUTH_NOT_CONFIGURED", "users 模式需要配置 EPISTEME_AUTH_SECRET");

        var path = ctx.Request.Path.Value ?? "";
        // Capability metadata is public so the SPA can decide whether to show a login wall.
        if (path == "/api/v1/episteme/capabilities" && ctx.Request.Method == "GET")
            return Ok("viewer");

        var store = ctx.RequestServices.GetRequiredService<IdentityStore>();
        var ct = ctx.RequestAborted;

        // 1) HMAC machine channel. In users mode an identity-less super-user credential
        //    contradicts account authorization, so it is off unless explicitly bound.
        var keyId = ctx.Request.Headers["X-Episteme-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(keyId))
            return await AuthenticateMachineAsync(ctx, keyId, secret, store);

        var authorization = ctx.Request.Headers.Authorization.ToString();
        var bearer = !string.IsNullOrWhiteSpace(authorization) &&
                     authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim() : "";
        var headerToken = ctx.Request.Headers["X-Episteme-Mcp-Token"].FirstOrDefault()?.Trim() ?? "";

        // 2) Per-account MCP token: Bearer epm_... or the dedicated header.
        var mcpToken = bearer.StartsWith(IdentityStore.McpTokenPrefix, StringComparison.Ordinal)
            ? bearer
            : (headerToken.StartsWith(IdentityStore.McpTokenPrefix, StringComparison.Ordinal)
                ? headerToken : "");
        if (!string.IsNullOrWhiteSpace(mcpToken))
            return await AuthenticateMcpTokenAsync(ctx, mcpToken, store);

        // 3) Login JWT (browser session).
        if (string.IsNullOrWhiteSpace(bearer))
            return Fail(401, "UNAUTHORIZED",
                "需要 Authorization: Bearer <登录 JWT 或 epm_ MCP 令牌>");

        var (valid, code, userId, _) = IdentityTokens.Verify(bearer, secret);
        if (!valid) return Fail(401, code, "登录令牌无效或已过期，请重新登录");

        var user = await store.FindByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
            return Fail(401, "USER_DISABLED", "账号不存在或已被停用");

        var principal = await store.PrincipalAsync(user, ct, "jwt");
        ctx.Items["EpistemePrincipal"] = principal;
        ctx.Items["EpistemeUserId"] = user.Id;
        ctx.Items["EpistemeUserName"] = user.UserName;
        ctx.Items["EpistemeAuthChannel"] = "jwt";
        return await CompleteUsersAuthAsync(ctx, principal, store);
    }

    /// <summary>
    /// Opaque per-account MCP token. Reset/revoke takes effect on the very next request
    /// because the stored digest is replaced / flagged; no revocation list is needed.
    /// </summary>
    private static async Task<(bool Ok, int Status, string Code, string Message, string Role)>
        AuthenticateMcpTokenAsync(HttpContext ctx, string token, IdentityStore store)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase))
            return Fail(403, "ADMIN_REQUIRED", "MCP 令牌不能访问管理后台，请用账号登录");
        // A leaked machine credential must not be able to rotate itself and lock the
        // account owner out: credential management requires a login session.
        if (path.StartsWith("/api/v1/auth/me/mcp-token", StringComparison.OrdinalIgnoreCase))
            return Fail(403, "MCP_TOKEN_SESSION_REQUIRED",
                "管理或重置 MCP 令牌需要账号登录会话，MCP 令牌本身不能操作自己");

        var user = await store.FindUserByMcpTokenAsync(token, ctx.RequestAborted);
        if (user is null)
            return Fail(401, "MCP_TOKEN_INVALID", "MCP 令牌无效、已重置或已停用，请在账号设置里重新生成");

        var principal = await store.PrincipalAsync(user, ctx.RequestAborted, "mcp-token");
        ctx.Items["EpistemePrincipal"] = principal;
        ctx.Items["EpistemeUserId"] = user.Id;
        ctx.Items["EpistemeUserName"] = user.UserName;
        ctx.Items["EpistemeAuthChannel"] = "mcp-token";
        await store.TouchMcpTokenAsync(user.Id, ctx.RequestAborted);
        return await CompleteUsersAuthAsync(ctx, principal, store);
    }

    /// <summary>
    /// Legacy HMAC channel in users mode. Only available when EPISTEME_MACHINE_USER names
    /// an existing account; the request is then scoped to THAT account's spaces.
    /// </summary>
    private static async Task<(bool Ok, int Status, string Code, string Message, string Role)>
        AuthenticateMachineAsync(HttpContext ctx, string keyId, string secret, IdentityStore store)
    {
        var machineUser = EpistemeConfig.MachineUser();
        if (string.IsNullOrWhiteSpace(machineUser))
            return Fail(401, "MACHINE_CHANNEL_DISABLED",
                "users 模式已改为按账号授权：无账号身份的 HMAC 通道已停用。请在「我的 MCP Token」生成令牌并以 Bearer 使用，或设置 EPISTEME_MACHINE_USER 显式绑定账号");

        var machine = await VerifyHmacAsync(ctx, keyId, secret);
        if (!machine.Ok) return machine;

        var user = await store.FindByNameAsync(machineUser, ctx.RequestAborted);
        if (user is null || !user.IsActive)
            return Fail(401, "MACHINE_USER_INVALID", "EPISTEME_MACHINE_USER 指向的账号不存在或已停用");
        var principal = await store.PrincipalAsync(user, ctx.RequestAborted, "machine");
        ctx.Items["EpistemePrincipal"] = principal;
        ctx.Items["EpistemeUserId"] = user.Id;
        ctx.Items["EpistemeUserName"] = user.UserName;
        ctx.Items["EpistemeAuthChannel"] = "machine";

        var scoped = await MachineSpaceIdsAsync(ctx);
        if (scoped is not null)
        {
            ctx.Items["EpistemeMachineSpaceIds"] = scoped;
            var (_, resolved) = await SpaceIdForRequestAsync(ctx, store);
            if (!string.IsNullOrWhiteSpace(resolved) &&
                !Array.Exists(scoped, s => string.Equals(s, resolved, StringComparison.OrdinalIgnoreCase)))
                return Fail(403, "MACHINE_SPACE_FORBIDDEN",
                    "机器密钥被限定在指定空间内，拒绝访问该空间");
        }
        // The declared X-Episteme-Role is intentionally ignored: the account's global
        // role is the only authority under account-based authorization.
        return Ok(principal.EffectiveRole(null, true));
    }

    /// <summary>Shared post-authentication checks: target-space ownership + admin guard.</summary>
    private static async Task<(bool Ok, int Status, string Code, string Message, string Role)>
        CompleteUsersAuthAsync(HttpContext ctx, UserPrincipal principal, IdentityStore store)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase) &&
            (!principal.IsAdmin || principal.IsMcpToken))
            return Fail(403, "ADMIN_REQUIRED", "需要管理员权限");
        if (path.StartsWith("/api/v1/auth/me/mcp-token", StringComparison.OrdinalIgnoreCase) &&
            principal.IsMcpToken)
            return Fail(403, "MCP_TOKEN_SESSION_REQUIRED",
                "管理或重置 MCP 令牌需要账号登录会话，MCP 令牌本身不能操作自己");

        var (rawSpace, resolvedSpace) = await SpaceIdForRequestAsync(ctx, store);
        if (!string.IsNullOrWhiteSpace(rawSpace) && !principal.Owns(resolvedSpace))
            return Fail(403, "ACCOUNT_FORBIDDEN",
                "该空间归属其他账号；按账号授权，只能访问自己名下的空间");
        return Ok(principal.EffectiveRole(resolvedSpace, string.IsNullOrWhiteSpace(rawSpace)));
    }

    /// <summary>
    /// Optional scope for HMAC machine credentials in users mode. Accepts space ids or
    /// slugs; null means unrestricted (keeps the existing git-sync contract unchanged).
    /// </summary>
    private static async Task<string[]?> MachineSpaceIdsAsync(HttpContext ctx)
    {
        var raw = Environment.GetEnvironmentVariable("EPISTEME_MACHINE_SPACES") ?? "";
        var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return null;
        await using var db = await ctx.RequestServices
            .GetRequiredService<IDbContextFactory<EpistemeDbContext>>()
            .CreateDbContextAsync(ctx.RequestAborted);
        var ids = new List<string>();
        foreach (var token in tokens)
        {
            var row = await db.Spaces.FirstOrDefaultAsync(
                s => s.Id == token || s.Slug == token, ctx.RequestAborted);
            ids.Add(row?.Id ?? token);
        }
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Resolve the target space (id or slug) from path, ?space= or an MCP params body.</summary>
    private static async Task<(string Raw, string Resolved)> SpaceIdForRequestAsync(
        HttpContext ctx, IdentityStore store)
    {
        var path = ctx.Request.Path.Value ?? "";
        var marker = "/spaces/";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        string raw = "";
        if (at >= 0)
        {
            var rest = path[(at + marker.Length)..];
            raw = rest.Split('/')[0];
        }
        if (string.IsNullOrWhiteSpace(raw)) raw = ctx.Request.Query["space"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) raw = await McpSpaceFromBodyAsync(ctx);
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");

        // Resolve slug -> id so grants (stored by id) match. Fall back to the raw value
        // so an unknown space still yields 404/403 rather than silently becoming public.
        await using var db = await ctx.RequestServices
            .GetRequiredService<IDbContextFactory<EpistemeDbContext>>()
            .CreateDbContextAsync(ctx.RequestAborted);
        var space = await db.Spaces.FirstOrDefaultAsync(
            s => s.Id == raw || s.Slug == raw, ctx.RequestAborted);
        return (raw, space?.Id ?? raw);
    }

    /// <summary>
    /// MCP tools/call may carry spaceId in the JSON-RPC params rather than the URL.
    /// Buffering lets us inspect it without consuming the body for the endpoint.
    /// </summary>
    private static async Task<string> McpSpaceFromBodyAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return "";
        if (ctx.Request.ContentLength is 0) return "";
        try
        {
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ctx.RequestAborted);
            ctx.Request.Body.Position = 0;
            if (string.IsNullOrWhiteSpace(text)) return "";
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("params", out var p)) return "";
            if (p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("spaceId", out var s)
                    && s.ValueKind == JsonValueKind.String) return s.GetString() ?? "";
                // Some MCP clients put spaceId inside arguments instead of params.
                // Honour it so the request is rejected explicitly instead of being
                // silently re-routed to the caller's default space.
                if (p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    && a.TryGetProperty("spaceId", out var s2) && s2.ValueKind == JsonValueKind.String)
                    return s2.GetString() ?? "";
            }
            return "";
        }
        catch (Exception)
        {
            try { ctx.Request.Body.Position = 0; } catch { /* body already consumed */ }
            return "";
        }
    }

    private static async Task<(bool Ok, int Status, string Code, string Message, string Role)> AuthenticateAsync(
        HttpContext ctx)
    {
        var secret = EpistemeConfig.AuthSecret();
        if (string.IsNullOrWhiteSpace(secret))
            return Fail(500, "AUTH_NOT_CONFIGURED", "secure 模式需要配置 EPISTEME_AUTH_SECRET");

        // Public, non-sensitive capability metadata stays readable for health checks.
        var path = ctx.Request.Path.Value ?? "";
        if (path == "/api/v1/episteme/capabilities" && ctx.Request.Method == "GET")
            return Ok("viewer");

        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return VerifyJwt(authorization[7..].Trim(), secret);

        var keyId = ctx.Request.Headers["X-Episteme-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(keyId)) return await VerifyHmacAsync(ctx, keyId, secret);

        return Fail(401, "UNAUTHORIZED", "需要 Authorization: Bearer <JWT> 或 X-Episteme-* 签名头");
    }

    private static byte[] Base64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        text += (text.Length % 4) switch
        {
            2 => "==", 3 => "=", 0 => "",
            _ => throw new FormatException("非法 base64url")
        };
        return Convert.FromBase64String(text);
    }

    private static (bool Ok, int Status, string Code, string Message, string Role) VerifyJwt(
        string token, string secret)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return Fail(401, "JWT_INVALID", "JWT 必须是三段式");
            using var header = JsonDocument.Parse(Base64Url(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256")
                return Fail(401, "JWT_ALG_INVALID", "只允许 HS256");

            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Base64Url(parts[2])))
                return Fail(401, "JWT_SIGNATURE_INVALID", "JWT 签名校验失败");

            using var payload = JsonDocument.Parse(Base64Url(parts[1]));
            if (payload.RootElement.TryGetProperty("exp", out var exp) &&
                exp.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Fail(401, "JWT_EXPIRED", "JWT 已过期");
            var role = payload.RootElement.TryGetProperty("role", out var r)
                ? r.GetString() ?? "viewer" : "viewer";
            return Ok(role);
        }
        catch (Exception)
        {
            return Fail(401, "JWT_INVALID", "JWT 解析或校验失败");
        }
    }

    private static Dictionary<string, string> ApiKeys()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = Environment.GetEnvironmentVariable("EPISTEME_API_KEYS") ?? "";
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = pair.IndexOf(':');
            if (colon <= 0) continue;
            map[pair[..colon].Trim()] = pair[(colon + 1)..].Trim();
        }
        return map;
    }

    private static async Task<(bool Ok, int Status, string Code, string Message, string Role)> VerifyHmacAsync(
        HttpContext ctx, string keyId, string secret)
    {
        var timestampRaw = ctx.Request.Headers["X-Episteme-Timestamp"].FirstOrDefault();
        var nonce = ctx.Request.Headers["X-Episteme-Nonce"].FirstOrDefault();
        var signature = ctx.Request.Headers["X-Episteme-Signature"].FirstOrDefault();
        var role = ctx.Request.Headers["X-Episteme-Role"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(role)) role = "editor";

        if (!long.TryParse(timestampRaw, out var timestamp))
            return Fail(401, "SIGNATURE_MISSING", "缺少合法的 X-Episteme-Timestamp");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skew = EpistemeConfig.SignatureSkewSeconds();
        if (Math.Abs(now - timestamp) > skew)
            return Fail(401, "SIGNATURE_EXPIRED", "请求时间戳超出允许偏差");
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signature))
            return Fail(401, "SIGNATURE_MISSING", "缺少 X-Episteme-Nonce / X-Episteme-Signature");

        var keys = ApiKeys();
        if (keys.Count > 0)
        {
            if (!keys.TryGetValue(keyId, out var configuredRole))
                return Fail(401, "API_KEY_UNKNOWN", "API Key 未在 EPISTEME_API_KEYS 中登记");
            if (configuredRole != role)
                return Fail(403, "ROLE_MISMATCH", "API Key 绑定的角色与请求声明不一致");
        }

        ctx.Request.EnableBuffering();
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
            body = buffer.ToArray();
        }
        ctx.Request.Body.Position = 0;

        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join("\n",
            ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Request.QueryString.Value ?? "",
            timestampRaw, nonce, role, bodyHash);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));

        byte[] provided;
        try { provided = Convert.FromHexString(signature); }
        catch
        {
            try { provided = Convert.FromBase64String(signature); }
            catch { return Fail(401, "SIGNATURE_INVALID", "签名不是合法 hex / base64"); }
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
            return Fail(401, "SIGNATURE_INVALID", "HMAC-SHA256 签名校验失败");

        PurgeNonces(now, skew);
        if (!UsedNonces.TryAdd(nonce, now))
            return Fail(409, "NONCE_REPLAYED", "Nonce 已使用，拒绝重放");
        return Ok(role);
    }

    private static void PurgeNonces(long now, int skew)
    {
        foreach (var entry in UsedNonces)
            if (now - entry.Value > skew) UsedNonces.TryRemove(entry.Key, out _);
    }
}
