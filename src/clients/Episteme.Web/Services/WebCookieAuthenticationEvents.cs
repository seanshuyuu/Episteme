using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Episteme.Web.Services;

public sealed class WebCookieAuthenticationEvents(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        var expiration = context.Properties.GetTokenValue("expires_at");
        if (string.IsNullOrWhiteSpace(refreshToken) ||
            !DateTimeOffset.TryParse(expiration, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiresAt) ||
            expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return;

        var authority = configuration["Authentication:Authority"]
            ?? throw new InvalidOperationException("Authentication:Authority must be configured.");
        var endpoint = configuration["Authentication:TokenEndpoint"] ?? authority.TrimEnd('/') + "/connect/token";
        var clientId = configuration["Authentication:ClientId"]
            ?? throw new InvalidOperationException("Authentication:ClientId must be configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId
            })
        };
        using var response = await httpClientFactory.CreateClient().SendAsync(
            request, context.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var tokens = await response.Content.ReadFromJsonAsync<RefreshedTokenResponse>(
            context.HttpContext.RequestAborted)
            ?? throw new InvalidOperationException("The token endpoint returned an empty refresh response.");
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresIn <= 0)
            throw new InvalidOperationException("The token endpoint returned an invalid refresh response.");

        var expires = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);
        context.Properties.UpdateTokenValue("access_token", tokens.AccessToken);
        context.Properties.UpdateTokenValue("expires_at", expires.ToString("O", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            context.Properties.UpdateTokenValue("refresh_token", tokens.RefreshToken);
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            var oldAccessToken = identity.FindFirst(WebTokenClaimNames.AccessToken);
            if (oldAccessToken is not null)
                identity.RemoveClaim(oldAccessToken);
            identity.AddClaim(new Claim(WebTokenClaimNames.AccessToken, tokens.AccessToken));
            var oldExpiration = identity.FindFirst(WebTokenClaimNames.ExpiresAt);
            if (oldExpiration is not null)
                identity.RemoveClaim(oldExpiration);
            identity.AddClaim(new Claim(WebTokenClaimNames.ExpiresAt,
                expires.ToString("O", CultureInfo.InvariantCulture)));
        }
        context.ShouldRenew = true;
    }
}

internal sealed record RefreshedTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
