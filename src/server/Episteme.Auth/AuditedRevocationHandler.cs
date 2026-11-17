using Episteme.Domain.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Episteme.Auth;

public sealed class AuditedRevocationHandler(EpistemeDbContext db, IOpenIddictTokenManager tokens)
    : IOpenIddictServerHandler<HandleRevocationRequestContext>
{
    public async ValueTask HandleAsync(HandleRevocationRequestContext context)
    {
        var principal = context.GenericTokenPrincipal
            ?? throw new InvalidOperationException("Validated revocation principal is missing.");
        var identifier = principal.GetTokenId();
        var clientId = context.Request.ClientId;
        if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(clientId))
        {
            context.Reject(error: Errors.UnsupportedTokenType);
            return;
        }
        var cancellationToken = context.Transaction.GetHttpRequest()?.HttpContext.RequestAborted ?? CancellationToken.None;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entry = await db.Set<OpenIddictEntityFrameworkCoreToken>()
            .FromSqlInterpolated($"SELECT * FROM \"OpenIddictTokens\" WHERE \"Id\" = {identifier} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            context.Reject(error: Errors.InvalidToken);
            return;
        }
        // Validation may already have tracked this token before another request revoked it.
        await db.Entry(entry).ReloadAsync(cancellationToken);
        var token = await tokens.FindByIdAsync(identifier, cancellationToken)
            ?? throw new InvalidOperationException("Locked token entry was not found.");
        if (!await tokens.HasStatusAsync(token, Statuses.Revoked, cancellationToken))
        {
            if (!await tokens.TryRevokeAsync(token, cancellationToken))
            {
                context.Reject(error: Errors.UnsupportedTokenType);
                return;
            }
            db.OAuthRevocationAuditEvents.Add(new OAuthRevocationAuditEvent(
                clientId, await tokens.GetSubjectAsync(token, cancellationToken), identifier));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
