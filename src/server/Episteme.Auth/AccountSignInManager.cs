using Episteme.Domain.Identity;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Episteme.Auth;

public sealed class AccountSignInManager(
    UserManager<ApplicationUser> users,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> options,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    EpistemeDbContext db)
    : SignInManager<ApplicationUser>(users, contextAccessor, claimsFactory, options, logger, schemes, confirmation)
{
    public override Task<bool> CanSignInAsync(ApplicationUser user) =>
        user.Status == UserAccountStatus.Active ? base.CanSignInAsync(user) : Task.FromResult(false);

    public override Task<SignInResult> CheckPasswordSignInAsync(ApplicationUser user, string password, bool lockoutOnFailure) =>
        base.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

    public override async Task SignInWithClaimsAsync(ApplicationUser user, AuthenticationProperties? properties,
        IEnumerable<Claim> additionalClaims)
    {
        await EnsureFirstAdministratorAsync(user);
        await base.SignInWithClaimsAsync(user, properties, additionalClaims);
    }

    private async Task EnsureFirstAdministratorAsync(ApplicationUser user)
    {
        var cancellationToken = Context.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var roleId = Guid.NewGuid();
        var stamp = Guid.NewGuid().ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
            VALUES ({roleId}, 'Administrator', 'ADMINISTRATOR', {stamp})
            ON CONFLICT ("NormalizedName") DO NOTHING
            """, cancellationToken);
        var role = await db.Roles.FromSqlRaw(
            "SELECT * FROM \"AspNetRoles\" WHERE \"NormalizedName\" = 'ADMINISTRATOR' FOR UPDATE")
            .SingleAsync(cancellationToken);
        if (!await db.UserRoles.AnyAsync(assignment => assignment.RoleId == role.Id, cancellationToken) &&
            !await db.AccountAuditEvents.AnyAsync(
                audit => audit.EventType == "user.administrator.bootstrapped" ||
                         audit.EventType == "user.administrator.granted", cancellationToken))
        {
            var current = await db.Users.FromSqlInterpolated(
                $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {user.Id} FOR UPDATE")
                .AsNoTracking().SingleAsync(cancellationToken);
            if (current.Status != UserAccountStatus.Active || current.SecurityStamp != user.SecurityStamp ||
                !await CanSignInAsync(current))
                throw new InvalidOperationException("The account is no longer eligible to sign in.");
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
            db.AccountAuditEvents.Add(new AccountAuditEvent(user.Id, user.Id, "user.administrator.bootstrapped"));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
