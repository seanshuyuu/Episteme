namespace Episteme.Application.Identity;

public sealed record UserToken(string Id, string? ClientId, string Type, string Status,
    DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt);

public enum UserTokenFailure { Unauthorized, InvalidInput, NotFound, Conflict }

public sealed record UserTokenListResult(IReadOnlyList<UserToken>? Items, bool HasMore, UserTokenFailure? Failure);
public sealed record UserTokenRevokeResult(UserTokenFailure? Failure);

public interface IUserTokenRepository
{
    Task<UserTokenListResult> ListAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken);
    Task<UserTokenRevokeResult> RevokeAsync(Guid userId, string tokenId, CancellationToken cancellationToken);
}

public interface IUserTokenService
{
    Task<UserTokenListResult> ListAsync(Guid userId, IReadOnlySet<string> scopes, int offset, int limit, CancellationToken cancellationToken);
    Task<UserTokenRevokeResult> RevokeAsync(Guid userId, IReadOnlySet<string> scopes, string tokenId, CancellationToken cancellationToken);
}

public sealed class UserTokenService(IUserTokenRepository repository) : IUserTokenService
{
    public Task<UserTokenListResult> ListAsync(Guid userId, IReadOnlySet<string> scopes, int offset, int limit, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("tokens.manage"))
            return Task.FromResult(new UserTokenListResult(null, false, UserTokenFailure.Unauthorized));
        if (offset < 0 || offset > 10000 || limit is < 1 or > 100)
            return Task.FromResult(new UserTokenListResult(null, false, UserTokenFailure.InvalidInput));
        return repository.ListAsync(userId, offset, limit, cancellationToken);
    }

    public Task<UserTokenRevokeResult> RevokeAsync(Guid userId, IReadOnlySet<string> scopes, string tokenId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("tokens.manage"))
            return Task.FromResult(new UserTokenRevokeResult(UserTokenFailure.Unauthorized));
        if (!Guid.TryParseExact(tokenId, "D", out _))
            return Task.FromResult(new UserTokenRevokeResult(UserTokenFailure.InvalidInput));
        return repository.RevokeAsync(userId, tokenId, cancellationToken);
    }
}
