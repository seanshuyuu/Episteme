using Episteme.Application.Identity;

namespace Episteme.Application.Tests;

public sealed class UserAuthorizationServiceTests
{
    [Fact]
    public async Task Validates_scope_pagination_and_identifier_before_accessing_repository()
    {
        var repository = new FakeRepository();
        var service = new UserAuthorizationService(repository);
        var user = Guid.NewGuid();
        var scopes = new HashSet<string> { "tokens.manage" };
        Assert.Equal(UserTokenFailure.Unauthorized, (await service.ListAsync(user, new HashSet<string>(), 0, 25, default)).Failure);
        Assert.Equal(UserTokenFailure.Unauthorized, (await service.RevokeAsync(Guid.Empty, scopes, Guid.NewGuid().ToString(), default)).Failure);
        Assert.Equal(UserTokenFailure.InvalidInput, (await service.ListAsync(user, scopes, 0, 101, default)).Failure);
        Assert.Equal(UserTokenFailure.InvalidInput, (await service.RevokeAsync(user, scopes, "invalid", default)).Failure);
        Assert.Equal(0, repository.Calls);
        Assert.Null((await service.ListAsync(user, scopes, 0, 25, default)).Failure);
        Assert.Null((await service.RevokeAsync(user, scopes, Guid.NewGuid().ToString(), default)).Failure);
        Assert.Equal(2, repository.Calls);
    }

    private sealed class FakeRepository : IUserAuthorizationRepository
    {
        public int Calls { get; private set; }
        public Task<UserAuthorizationListResult> ListAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new UserAuthorizationListResult([], false, null));
        }

        public Task<UserTokenRevokeResult> RevokeAsync(Guid userId, string authorizationId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new UserTokenRevokeResult(null));
        }
    }
}
