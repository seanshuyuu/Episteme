using System.Security.Claims;
using Episteme.Domain.Identity;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Episteme.Auth.Controllers;

public sealed class AuthorizationController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations,
    EpistemeDbContext db,
    IConfiguration configuration) : Controller
{
    private const string TokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    [HttpGet("~/connect/authorize")]
    public async Task<IActionResult> AuthorizeRequest()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Missing authorization request.");
        if (request.GetResources().Length != 1)
            return OAuthError(Errors.InvalidTarget, "Exactly one resource must be requested.");
        var user = await users.GetUserAsync(User);
        if (user is null || user.Status != UserAccountStatus.Active || !await signInManager.CanSignInAsync(user))
        {
            if (request.HasPromptValue(PromptValues.None))
                return OAuthError(Errors.LoginRequired, "Authentication is required.");
            await signInManager.SignOutAsync();
            return Challenge(new AuthenticationProperties { RedirectUri = Request.PathBase + Request.Path + Request.QueryString },
                IdentityConstants.ApplicationScheme);
        }
        if (request.HasPromptValue(PromptValues.None))
            return OAuthError(Errors.ConsentRequired, "Interactive consent is required.");
        if (request.HasPromptValue(PromptValues.Login) || request.MaxAge is not null)
            return OAuthError(Errors.LoginRequired, "Reauthentication is not supported by this endpoint.");
        var application = await applications.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The validated client no longer exists.");
        ViewData["ClientName"] = await applications.GetDisplayNameAsync(application) ?? request.ClientId;
        ViewData["Scope"] = request.Scope;
        ViewData["Resource"] = request.GetResources()[0];
        return View("Consent");
    }

    [HttpPost("~/connect/authorize")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AuthorizeConsent([FromForm] string decision)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Missing authorization request.");
        if (request.GetResources().Length != 1)
            return OAuthError(Errors.InvalidTarget, "Exactly one resource must be requested.");
        var user = await users.GetUserAsync(User);
        if (user is null || user.Status != UserAccountStatus.Active || !await signInManager.CanSignInAsync(user))
            return OAuthError(Errors.LoginRequired, "Authentication is required.");
        if (request.HasPromptValue(PromptValues.None))
            return OAuthError(Errors.ConsentRequired, "Interactive consent is required.");
        if (request.HasPromptValue(PromptValues.Login) || request.MaxAge is not null)
            return OAuthError(Errors.LoginRequired, "Reauthentication is not supported by this endpoint.");
        if (decision != "accept")
            return OAuthError(Errors.AccessDenied, "The authorization request was denied.");
        var identity = await CreateIdentityAsync(user);
        identity.SetScopes(request.GetScopes());
        identity.SetResources(request.GetResources());
        var application = await applications.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The validated client no longer exists.");
        var descriptor = new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = await applications.GetIdAsync(application),
            Subject = user.Id.ToString(),
            Status = Statuses.Valid,
            Type = AuthorizationTypes.Permanent
        };
        foreach (var scope in request.GetScopes())
            descriptor.Scopes.Add(scope);
        var authorization = await authorizations.CreateAsync(descriptor);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization));
        identity.SetDestinations(DestinationsForClaim);
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Missing token request.");
        if (request.GrantType == TokenExchangeGrant)
            return await ExchangeMcpTokenAsync(request);
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return OAuthError(Errors.UnsupportedGrantType, "The grant type is not supported.");
        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);
        var user = subject is null ? null : await users.FindByIdAsync(subject);
        if (user is null || user.Status != UserAccountStatus.Active || !await signInManager.CanSignInAsync(user))
            return OAuthError(Errors.InvalidGrant, "The user is no longer allowed to sign in.");
        var stampHash = IdentityClaims.HashSecurityStamp(user.SecurityStamp);
        if (stampHash is null || result.Principal!.GetClaim(IdentityClaims.SecurityStampHash) != stampHash ||
            result.Principal!.GetClaim(IdentityClaims.TokenGeneration) != user.TokenGeneration.ToString("D"))
            return OAuthError(Errors.InvalidGrant, "The user credentials have changed.");
        var identity = new ClaimsIdentity(result.Principal!.Claims,
            TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Name, user.DisplayName ?? user.UserName)
            .SetClaim(IdentityClaims.TokenGeneration, user.TokenGeneration.ToString("D"))
            .SetClaim(IdentityClaims.SecurityStampHash, stampHash)
            .SetClaims(Claims.Role, [.. await users.GetRolesAsync(user)])
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.PreferredUsername, user.DisplayName);
        identity.SetDestinations(DestinationsForClaim);
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<IActionResult> ExchangeMcpTokenAsync(OpenIddictRequest request)
    {
        var subjectToken = (string?)request.GetParameter("subject_token");
        var subjectTokenType = (string?)request.GetParameter("subject_token_type");
        var audience = configuration["Authentication:McpAudience"];
        if (request.ClientId != configuration["Authentication:Client:Id"] ||
            string.IsNullOrEmpty(subjectToken) || subjectToken.Length > 16384 ||
            subjectTokenType != AccessTokenType ||
            request.GetResources().Length != 1 || request.GetResources()[0] != audience ||
            Request.Headers.ContainsKey("Authorization"))
            return OAuthError(Errors.InvalidRequest, "Invalid token exchange request.");

        Request.Headers.Authorization = $"Bearer {subjectToken}";
        var result = await HttpContext.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        Request.Headers.Remove("Authorization");
        var principal = result.Principal;
        if (!result.Succeeded || principal is null || !principal.HasScope("tokens.manage"))
            return OAuthError(Errors.InvalidGrant, "The subject token is invalid or cannot manage tokens.");
        var user = await users.FindByIdAsync(principal.GetClaim(Claims.Subject) ?? "");
        var stampHash = user is null ? null : IdentityClaims.HashSecurityStamp(user.SecurityStamp);
        if (user is null || user.Status != UserAccountStatus.Active || !await signInManager.CanSignInAsync(user) ||
            stampHash is null || principal.GetClaim(IdentityClaims.SecurityStampHash) != stampHash ||
            principal.GetClaim(IdentityClaims.TokenGeneration) != user.TokenGeneration.ToString("D") ||
            !await db.McpUserSettings.AsNoTracking().AnyAsync(setting => setting.UserId == user.Id && setting.Enabled))
            return OAuthError(Errors.InvalidGrant, "The user is no longer allowed to use MCP.");

        var scopes = new[] { "workspaces.read", "workspaces.write", "workspaces.manage" }
            .Where(principal.HasScope).ToArray();
        if (scopes.Length == 0)
            return OAuthError(Errors.InvalidGrant, "The subject token has no workspace permissions.");
        var identity = await CreateIdentityAsync(user);
        identity.SetScopes(scopes);
        identity.SetResources(audience!);
        identity.SetAccessTokenLifetime(TimeSpan.FromDays(30));
        identity.SetDestinations(DestinationsForClaim);
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<ClaimsIdentity> CreateIdentityAsync(ApplicationUser user)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        var stampHash = IdentityClaims.HashSecurityStamp(user.SecurityStamp)
            ?? throw new InvalidOperationException("User security stamp is missing.");
        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Name, user.DisplayName ?? user.UserName)
            .SetClaim(IdentityClaims.TokenGeneration, user.TokenGeneration.ToString("D"))
            .SetClaim(IdentityClaims.SecurityStampHash, stampHash)
            .SetClaims(Claims.Role, [.. await users.GetRolesAsync(user)]);
        if (!string.IsNullOrEmpty(user.Email))
            identity.SetClaim(Claims.Email, user.Email);
        if (!string.IsNullOrEmpty(user.DisplayName))
            identity.SetClaim(Claims.PreferredUsername, user.DisplayName);
        return identity;
    }

    private static IEnumerable<string> DestinationsForClaim(Claim claim)
    {
        if (claim.Type == Claims.Subject)
            return [Destinations.AccessToken, Destinations.IdentityToken];
        if (claim.Type == Claims.Name)
            return claim.Subject!.HasScope(Scopes.Profile)
                ? [Destinations.AccessToken, Destinations.IdentityToken]
                : [Destinations.AccessToken];
        if (claim.Type == Claims.Email)
            return claim.Subject!.HasScope(Scopes.Email)
                ? [Destinations.AccessToken, Destinations.IdentityToken]
                : [];
        if (claim.Type == Claims.PreferredUsername)
            return claim.Subject!.HasScope(Scopes.Profile)
                ? [Destinations.AccessToken, Destinations.IdentityToken]
                : [];
        if (claim.Type == Claims.Role)
            return [Destinations.AccessToken];
        if (claim.Type is IdentityClaims.TokenGeneration or IdentityClaims.SecurityStampHash)
            return [Destinations.AccessToken];
        return [];
    }

    private ForbidResult OAuthError(string error, string description) =>
        Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        }), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}
