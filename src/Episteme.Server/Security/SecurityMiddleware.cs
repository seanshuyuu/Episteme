using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.RateLimiting;
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

    private static (bool Ok, int Status, string Code, string Message, string Role) Ok(string role) =>
        (true, 200, "", "", role);

    private static (bool Ok, int Status, string Code, string Message, string Role) Fail(
        int status, string code, string message) => (false, status, code, message, "");

    public static void Add(WebApplicationBuilder builder)
    {
        if (!EpistemeConfig.Secure) return;
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
        if (!EpistemeConfig.Secure) return;
        app.UseWhen(ctx => IsProtected(ctx.Request), branch =>
        {
            branch.UseRateLimiter();
            branch.Use(async (ctx, next) =>
            {
                var result = await AuthenticateAsync(ctx);
                if (!result.Ok)
                {
                    await Reject(ctx, result.Status, result.Code, result.Message);
                    return;
                }
                // Downstream (Endpoints.Ctx / ToolsCall) keeps reading X-Episteme-Role.
                ctx.Request.Headers["X-Episteme-Role"] = result.Role;
                ctx.Items["EpistemeRole"] = result.Role;
                await next();
            });
        });
    }

    private static bool IsProtected(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
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
