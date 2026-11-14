using System.Text.Json.Nodes;

namespace Episteme.Server;

/// <summary>
/// Single source of truth for the opt-in production switches. Everything defaults to the
/// legacy/dev behaviour so existing REST / MCP / UI regressions keep passing unchanged.
/// </summary>
public static class EpistemeConfig
{
    public const string Version = "0.5.0-polish";

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    public static string AuthMode() => Env("EPISTEME_AUTH_MODE", "legacy").ToLowerInvariant();
    /// <summary>Shared-secret gateway (HS256 JWT / HMAC), no user database.</summary>
    public static bool Secure => AuthMode() == "secure";
    /// <summary>Real accounts: registration, login, global roles, per-space grants, admin console.</summary>
    public static bool Users => AuthMode() == "users";
    /// <summary>Any auth-enabled mode registers the security middleware.</summary>
    public static bool Guarded => Secure || Users;
    public static string DbProvider() => Env("EPISTEME_DB_PROVIDER", "sqlite").ToLowerInvariant();
    public static string AuthSecret() => Env("EPISTEME_AUTH_SECRET", "");
    public static string FetchWorkerUrl() => Env("EPISTEME_FETCH_WORKER_URL", "").TrimEnd('/');

    public static bool AllowRegistration() =>
        !Env("EPISTEME_ALLOW_REGISTRATION", "true").Equals("false", StringComparison.OrdinalIgnoreCase);

    public static string AdminUser() => Env("EPISTEME_ADMIN_USER", "admin");
    public static string AdminPassword() => Env("EPISTEME_ADMIN_PASSWORD", "");
    public static string JwtIssuer() => Env("EPISTEME_JWT_ISSUER", "episteme");
    public static string JwtAudience() => Env("EPISTEME_JWT_AUDIENCE", "episteme-clients");
    public static int JwtTtlSeconds()
    {
        var raw = Env("EPISTEME_JWT_TTL_SECONDS", "86400");
        return int.TryParse(raw, out var n) && n > 0 ? n : 86400;
    }
    public static int LoginRateLimit()
    {
        var raw = Env("EPISTEME_LOGIN_RATE_LIMIT", "30");
        return int.TryParse(raw, out var n) && n > 0 ? n : 30;
    }
    public static int RateLimitPerMinute()
    {
        var raw = Env("EPISTEME_RATE_LIMIT", "600");
        return int.TryParse(raw, out var n) && n > 0 ? n : 600;
    }
    public static int SignatureSkewSeconds()
    {
        var raw = Env("EPISTEME_SIGNATURE_SKEW", "300");
        return int.TryParse(raw, out var n) && n > 0 ? n : 300;
    }

    public static JsonObject Describe() => new()
    {
        ["version"] = Version,
        ["authMode"] = AuthMode(),
        ["dbProvider"] = DbProvider(),
        ["fetchWorker"] = new JsonObject
        {
            ["configured"] = FetchWorkerUrl().Length > 0,
            ["extractor"] = "mozilla-readability 0.6.0",
            ["ssrfGuards"] = new JsonArray("protocol", "port", "dns", "private-ip", "redirect-hop", "allowlist", "size-cap", "timeout"),
            ["note"] = "独立容器，仅 compose 内网可达；离线 /extract 入口用于 HTML 快照"
        },
        ["renderers"] = new JsonObject
        {
            ["pdf"] = "pdf.js 4.10.38 (canvas + text layer)",
            ["code"] = "Monaco 0.52.2 (textarea fallback)",
            ["web"] = "Readability + sanitizer (three states)",
            ["md"] = "markdown preview"
        },
        ["media"] = new JsonObject
        {
            ["binaryPdfBlobStore"] = true,
            ["maxPdfBytes"] = 20 * 1024 * 1024,
            ["interactiveAnchorRebind"] = true
        },
        ["transports"] = new JsonArray("streamable-http", "sse", "stdio"),
        ["security"] = new JsonObject
        {
            ["jwtHs256"] = true,
            ["hmacSha256"] = true,
            ["nonceReplayGuard"] = true,
            ["rateLimitPerMinute"] = RateLimitPerMinute(),
            ["auditLog"] = true,
            ["active"] = Guarded,
            ["accounts"] = Users,
            ["registration"] = Users && AllowRegistration(),
            ["perSpaceGrants"] = Users,
            ["adminConsole"] = Users
        },
        ["auth"] = new JsonObject
        {
            ["mode"] = AuthMode(),
            ["registrationEnabled"] = Users && AllowRegistration(),
            ["roles"] = new JsonArray("admin", "owner", "editor", "agent", "viewer"),
            ["spaceRoles"] = new JsonArray("owner", "editor", "agent", "viewer"),
            ["endpoints"] = new JsonObject
            {
                ["config"] = "/api/v1/auth/config", ["register"] = "/api/v1/auth/register",
                ["login"] = "/api/v1/auth/login", ["me"] = "/api/v1/auth/me",
                ["logout"] = "/api/v1/auth/logout", ["admin"] = "/api/v1/admin/users"
            }
        },
        ["note"] = "生产化能力均为环境变量开关；默认 legacy，不破坏既有契约"
    };
}
