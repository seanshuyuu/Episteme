namespace Episteme.Application.Identity;

public sealed record UserAuthorization(string Id, string? ClientId, string Status, DateTimeOffset? CreatedAt);
public sealed record UserAuthorizationListResult(IReadOnlyList<UserAuthorization>? Items, bool HasMore, UserTokenFailure? Failure);

public interface IUserAuthorizationRepository
{
    Task<UserAuthorizationListResult> ListAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken);
    Task<UserTokenRevokeResult> RevokeAsync(Guid userId, string authorizationId, CancellationToken cancellationToken);
}

public interface IUserAuthorizationService
{
    Task<UserAuthorizationListResult> ListAsync(Guid userId, IReadOnlySet<string> scopes, int offset, int limit, CancellationToken cancellationToken);
    Task<UserTokenRevokeResult> RevokeAsync(Guid userId, IReadOnlySet<string> scopes, string authorizationId, CancellationToken cancellationToken);
}

public sealed class UserAuthorizationService(IUserAuthorizationRepository repository) : IUserAuthorizationService
{
    public Task<UserAuthorizationListResult> ListAsync(Guid userId, IReadOnlySet<string> scopes, int offset, int limit, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("tokens.manage"))
            return Task.FromResult(new UserAuthorizationListResult(null, false, UserTokenFailure.Unauthorized));
        if (offset < 0 || offset > 10000 || limit is < 1 or > 100)
            return Task.FromResult(new UserAuthorizationListResult(null, false, UserTokenFailure.InvalidInput));
        return repository.ListAsync(userId, offset, limit, cancellationToken);
    }

    public Task<UserTokenRevokeResult> RevokeAsync(Guid userId, IReadOnlySet<string> scopes, string authorizationId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("tokens.manage"))
            return Task.FromResult(new UserTokenRevokeResult(UserTokenFailure.Unauthorized));
        if (!Guid.TryParseExact(authorizationId, "D", out _))
            return Task.FromResult(new UserTokenRevokeResult(UserTokenFailure.InvalidInput));
        return repository.RevokeAsync(userId, authorizationId, cancellationToken);
    }
}
