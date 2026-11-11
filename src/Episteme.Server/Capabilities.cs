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
    public static bool Secure => AuthMode() == "secure";
    public static string DbProvider() => Env("EPISTEME_DB_PROVIDER", "sqlite").ToLowerInvariant();
    public static string AuthSecret() => Env("EPISTEME_AUTH_SECRET", "");
    public static string FetchWorkerUrl() => Env("EPISTEME_FETCH_WORKER_URL", "").TrimEnd('/');
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
            ["active"] = Secure
        },
        ["note"] = "生产化能力均为环境变量开关；默认 legacy，不破坏既有契约"
    };
}
