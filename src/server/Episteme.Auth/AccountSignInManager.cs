using Episteme.Domain.Identity;
using Episteme.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Episteme.Auth;

public sealed class AccountSignInManager(
    UserManager<ApplicationUser> users,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> options,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(users, contextAccessor, claimsFactory, options, logger, schemes, confirmation)
{
    public override Task<bool> CanSignInAsync(ApplicationUser user) =>
        user.Status == UserAccountStatus.Active ? base.CanSignInAsync(user) : Task.FromResult(false);

    public override Task<SignInResult> CheckPasswordSignInAsync(ApplicationUser user, string password, bool lockoutOnFailure) =>
        base.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
}
