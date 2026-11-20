using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Episteme.Web.Services;

public sealed class McpTokenClient(IHttpClientFactory clients, IConfiguration configuration, SessionTokenProvider session)
{
    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var authority = configuration["Authentication:TokenEndpoint"] ??
            (configuration["Authentication:Authority"]?.TrimEnd('/') + "/connect/token");
        var clientId = configuration["Authentication:ClientId"];
        var audience = configuration["Authentication:McpAudience"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var endpoint) ||
            string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException("MCP token endpoint configuration is missing.");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["client_id"] = clientId,
                ["subject_token"] = await session.GetAccessTokenAsync(),
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["resource"] = audience,
                ["scope"] = "workspaces.read workspaces.write workspaces.manage"
            })
        };
        using var response = await clients.CreateClient("mcp-token").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MCP token creation failed (HTTP {(int)response.StatusCode}).");
        var result = await response.Content.ReadFromJsonAsync<McpTokenResponse>(cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.AccessToken) || result.ExpiresIn <= 0)
            throw new InvalidOperationException("The token endpoint returned an invalid response.");
        return result.AccessToken;
    }

    private sealed record McpTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
