using System.Security.Claims;
using Episteme.Domain.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Identity;

public sealed class CurrentUserRolesTransformation(EpistemeDbContext db, IHttpContextAccessor httpContext) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var cancellationToken = httpContext.HttpContext?.RequestAborted ?? CancellationToken.None;
        var tokenGeneration = principal.FindFirst(IdentityClaims.TokenGeneration)?.Value;
        var stampHash = principal.FindFirst(IdentityClaims.SecurityStampHash)?.Value;
        if (!Guid.TryParse(subject, out var currentUserId) ||
            !Guid.TryParseExact(tokenGeneration, "D", out var generation) || string.IsNullOrEmpty(stampHash))
            return new ClaimsPrincipal(new ClaimsIdentity());
        var userState = await db.Users.AsNoTracking()
            .Where(user => user.Id == currentUserId && user.Status == UserAccountStatus.Active && user.TokenGeneration == generation)
            .Select(user => new { user.SecurityStamp }).SingleOrDefaultAsync(cancellationToken);
        if (userState is null || IdentityClaims.HashSecurityStamp(userState.SecurityStamp) != stampHash)
            return new ClaimsPrincipal(new ClaimsIdentity());
        var roles = await (from user in db.Users.AsNoTracking()
                           join assignment in db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
                           join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                           where user.Id == currentUserId && user.Status == UserAccountStatus.Active && role.Name != null
                           select role.Name!).ToListAsync(cancellationToken);
        var identities = principal.Identities.Select(identity => new ClaimsIdentity(
            identity.Claims.Where(claim => claim.Type != "role" && claim.Type != ClaimTypes.Role &&
                claim.Type != identity.RoleClaimType),
            identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType)).ToArray();
        var authenticated = identities.FirstOrDefault(identity => identity.IsAuthenticated);
        if (authenticated is not null)
            foreach (var role in roles)
                authenticated.AddClaim(new Claim(authenticated.RoleClaimType, role));
        return new ClaimsPrincipal(identities);
    }
}
