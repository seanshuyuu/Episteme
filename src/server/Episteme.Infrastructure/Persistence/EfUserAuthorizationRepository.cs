using Episteme.Application.Identity;
using Episteme.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfUserAuthorizationRepository(EpistemeDbContext db, IOpenIddictAuthorizationManager authorizations) : IUserAuthorizationRepository
{
    public async Task<UserAuthorizationListResult> ListAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Status == UserAccountStatus.Active, cancellationToken))
            return new(null, false, UserTokenFailure.Unauthorized);
        var subject = userId.ToString();
        var entries = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking()
            .Where(a => a.Subject == subject && a.Type == AuthorizationTypes.Permanent)
            .OrderByDescending(a => a.CreationDate).ThenBy(a => a.Id)
            .Skip(offset).Take(limit + 1)
            .Select(a => new { a.Id, ClientId = a.Application == null ? null : a.Application.ClientId, a.Status, a.CreationDate })
            .ToListAsync(cancellationToken);
        var items = entries.Take(limit).Select(a => new UserAuthorization(
            a.Id ?? throw new InvalidOperationException("Authorization entry is missing its ID."),
            a.ClientId, a.Status ?? throw new InvalidOperationException("Authorization entry is missing its status."),
            a.CreationDate is null ? null : new DateTimeOffset(a.CreationDate.Value))).ToArray();
        return new(items, entries.Count > limit, null);
    }

    public async Task<UserTokenRevokeResult> RevokeAsync(Guid userId, string authorizationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return new(UserTokenFailure.Unauthorized);
        var authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null || await authorizations.GetSubjectAsync(authorization, cancellationToken) != userId.ToString() ||
            await authorizations.GetTypeAsync(authorization, cancellationToken) != AuthorizationTypes.Permanent)
            return new(UserTokenFailure.NotFound);
        if (!await authorizations.HasStatusAsync(authorization, Statuses.Revoked, cancellationToken))
        {
            if (!await authorizations.TryRevokeAsync(authorization, cancellationToken))
                return new(UserTokenFailure.Conflict);
            db.AccountAuditEvents.Add(new AccountAuditEvent(userId, userId, "authorization.revoked", targetAuthorizationId: authorizationId));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(null);
    }
}
