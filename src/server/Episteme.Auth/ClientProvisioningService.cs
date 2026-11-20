using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Episteme.Auth;

public sealed class ClientProvisioningService(IServiceScopeFactory scopeFactory, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var clientId = configuration["Authentication:Client:Id"];
        var redirect = configuration["Authentication:Client:RedirectUri"];
        if (string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(redirect))
            return;
        if (string.IsNullOrWhiteSpace(clientId) || !Uri.TryCreate(redirect, UriKind.Absolute, out var redirectUri) ||
            (redirectUri.Scheme != Uri.UriSchemeHttps && !(redirectUri.IsLoopback && redirectUri.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(redirectUri.Fragment) || !string.IsNullOrEmpty(redirectUri.UserInfo))
            throw new InvalidOperationException("Configured public client requires an ID and a secure redirect URI.");
        await using var scope = scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = clientId,
            RedirectUris = { redirectUri },
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.Prefixes.GrantType + "urn:ietf:params:oauth:grant-type:token-exchange",
                Permissions.ResponseTypes.Code, Permissions.Scopes.Email, Permissions.Scopes.Profile,
                Permissions.Scopes.Roles, Permissions.Prefixes.Scope + "workspaces.read",
                Permissions.Prefixes.Scope + "workspaces.write", Permissions.Prefixes.Scope + "workspaces.manage",
                Permissions.Prefixes.Scope + "tokens.manage", Permissions.Prefixes.Scope + "users.manage",
                Permissions.Prefixes.Resource + configuration["Authentication:ApiAudience"],
                Permissions.Prefixes.Resource + configuration["Authentication:McpAudience"]
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        };
        var existing = await manager.FindByClientIdAsync(clientId, cancellationToken);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor, cancellationToken);
            return;
        }
        var current = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(current, existing, cancellationToken);
        if (current.ClientType != descriptor.ClientType || current.ConsentType != descriptor.ConsentType ||
            !current.RedirectUris.SetEquals(descriptor.RedirectUris) ||
            !current.Requirements.SetEquals(descriptor.Requirements))
            throw new InvalidOperationException("The configured client differs from the existing registration; update it through a controlled administrative procedure.");
        if (!current.Permissions.SetEquals(descriptor.Permissions))
        {
            var oldPermissions = descriptor.Permissions.ToHashSet(StringComparer.Ordinal);
            oldPermissions.Remove(Permissions.Prefixes.GrantType + "urn:ietf:params:oauth:grant-type:token-exchange");
            if (!current.Permissions.SetEquals(oldPermissions))
                throw new InvalidOperationException("The configured client permissions differ from the expected registration.");
            current.Permissions.Add(Permissions.Prefixes.GrantType + "urn:ietf:params:oauth:grant-type:token-exchange");
            await manager.UpdateAsync(existing, current, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

}
