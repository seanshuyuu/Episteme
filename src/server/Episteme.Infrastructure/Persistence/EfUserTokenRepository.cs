using Episteme.Application.Identity;
using Episteme.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfUserTokenRepository(EpistemeDbContext db, IOpenIddictTokenManager tokens) : IUserTokenRepository
{
    public async Task<UserTokenListResult> ListAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Status == UserAccountStatus.Active, cancellationToken))
            return new(null, false, UserTokenFailure.Unauthorized);
        return await ListForSubjectAsync(db, userId, offset, limit, cancellationToken);
    }

    internal static async Task<UserTokenListResult> ListForSubjectAsync(
        EpistemeDbContext db, Guid userId, int offset, int limit, CancellationToken cancellationToken)
    {
        var subject = userId.ToString();
        var entries = await db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking()
            .Where(t => t.Subject == subject && (t.Type == TokenTypeIdentifiers.AccessToken || t.Type == TokenTypeIdentifiers.RefreshToken))
            .OrderByDescending(t => t.CreationDate).ThenBy(t => t.Id)
            .Skip(offset).Take(limit + 1)
            .Select(t => new
            {
                t.Id,
                ClientId = t.Application == null ? null : t.Application.ClientId,
                t.Type,
                t.Status,
                t.CreationDate,
                t.ExpirationDate
            })
            .ToListAsync(cancellationToken);
        var result = entries.Take(limit).Select(t => new UserToken(
            t.Id ?? throw new InvalidOperationException("Token entry is missing its ID."), t.ClientId,
            t.Type ?? throw new InvalidOperationException("Token entry is missing its type."),
            t.Status ?? throw new InvalidOperationException("Token entry is missing its status."),
            t.CreationDate is null ? null : new DateTimeOffset(t.CreationDate.Value),
            t.ExpirationDate is null ? null : new DateTimeOffset(t.ExpirationDate.Value))).ToArray();
        return new(result, entries.Count > limit, null);
    }

    public async Task<UserTokenRevokeResult> RevokeAsync(Guid userId, string tokenId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return new(UserTokenFailure.Unauthorized);
        var token = await tokens.FindByIdAsync(tokenId, cancellationToken);
        if (token is null || await tokens.GetSubjectAsync(token, cancellationToken) != userId.ToString())
            return new(UserTokenFailure.NotFound);
        var type = await tokens.GetTypeAsync(token, cancellationToken);
        if (type is not (TokenTypeIdentifiers.AccessToken or TokenTypeIdentifiers.RefreshToken))
            return new(UserTokenFailure.NotFound);
        if (!await tokens.HasStatusAsync(token, Statuses.Revoked, cancellationToken))
        {
            if (!await tokens.TryRevokeAsync(token, cancellationToken))
                return new(UserTokenFailure.Conflict);
            db.AccountAuditEvents.Add(new AccountAuditEvent(userId, userId, "token.revoked", tokenId));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(null);
    }
}
