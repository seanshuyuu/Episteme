using Episteme.Application.Identity;

namespace Episteme.Application.Tests;

public sealed class UserTokenServiceTests
{
    [Fact]
    public async Task Token_management_requires_its_own_scope_before_accessing_storage()
    {
        var repository = new FakeRepository();
        var service = new UserTokenService(repository);
        var scopes = new HashSet<string> { "workspaces.manage" };
        var list = await service.ListAsync(Guid.NewGuid(), scopes, 0, 25, default);
        var revoke = await service.RevokeAsync(Guid.NewGuid(), scopes, Guid.NewGuid().ToString(), default);
        Assert.Equal(UserTokenFailure.Unauthorized, list.Failure);
        Assert.Equal(UserTokenFailure.Unauthorized, revoke.Failure);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Invalid_page_and_token_id_do_not_reach_storage()
    {
        var repository = new FakeRepository();
        var service = new UserTokenService(repository);
        var scopes = new HashSet<string> { "tokens.manage" };
        Assert.Equal(UserTokenFailure.InvalidInput,
            (await service.ListAsync(Guid.NewGuid(), scopes, 10001, 1, default)).Failure);
        Assert.Equal(UserTokenFailure.InvalidInput,
            (await service.ListAsync(Guid.NewGuid(), scopes, 0, 101, default)).Failure);
        Assert.Equal(UserTokenFailure.InvalidInput,
            (await service.RevokeAsync(Guid.NewGuid(), scopes, "invalid", default)).Failure);
        Assert.Equal(0, repository.Calls);
    }

    private sealed class FakeRepository : IUserTokenRepository
    {
        public int Calls { get; private set; }

        public Task<UserTokenListResult> ListAsync(Guid userId, int offset, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new UserTokenListResult([], false, null));
        }

        public Task<UserTokenRevokeResult> RevokeAsync(Guid userId, string tokenId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new UserTokenRevokeResult(null));
        }
    }
}
