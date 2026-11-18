using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Episteme.Web.Services;

public sealed class SessionTokenProvider(AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<string> GetAccessTokenAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var token = state.User.FindFirstValue(WebTokenClaimNames.AccessToken);
        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new InvalidOperationException("The authenticated session does not contain an API access token.");
    }
}
