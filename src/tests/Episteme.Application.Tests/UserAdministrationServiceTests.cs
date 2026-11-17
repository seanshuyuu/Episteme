using Episteme.Application.Identity;
using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;

namespace Episteme.Application.Tests;

public sealed class UserAdministrationServiceTests
{
    [Fact]
    public async Task Admin_operations_require_scope_and_role_before_repository_access()
    {
        var repository = new FakeRepository();
        var service = new UserAdministrationService(repository);
        var user = Guid.NewGuid();
        var missingScope = new WorkspaceActor(user, new HashSet<string>(), true);
        var missingRole = new WorkspaceActor(user, new HashSet<string> { "users.manage" }, false);
        Assert.Equal(UserAdministrationFailure.Unauthorized, (await service.ListAsync(missingScope, 0, 25, default)).Failure);
        Assert.Equal(UserAdministrationFailure.Unauthorized,
            (await service.SetStatusAsync(missingRole, user, UserAccountStatus.Disabled, default)).Failure);
        Assert.Equal(UserAdministrationFailure.Unauthorized,
            (await service.SetAdministratorAsync(missingRole, user, true, default)).Failure);
        Assert.Equal(UserAdministrationFailure.Unauthorized,
            (await service.CreateAsync(missingRole, new("user@example.test", "Test-Password-42!"), default)).Failure);
        Assert.Equal(UserTokenFailure.Unauthorized, (await service.ListTokensAsync(missingRole, user, 0, 25, default)).Failure);
        Assert.Equal(UserTokenFailure.Unauthorized, (await service.RevokeTokenAsync(missingScope, user, Guid.NewGuid().ToString(), default)).Failure);
        Assert.Equal(UserAdministrationFailure.Unauthorized, (await service.ResetPasswordAsync(missingRole, user, new("valid"), default)).Failure);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_repository_access()
    {
        var repository = new FakeRepository();
        var service = new UserAdministrationService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string> { "users.manage" }, true);
        Assert.Equal(UserAdministrationFailure.InvalidInput, (await service.ListAsync(actor, 0, 101, default)).Failure);
        Assert.Equal(UserAdministrationFailure.InvalidInput, (await service.ListAsync(actor, 10001, 1, default)).Failure);
        Assert.Equal(UserAdministrationFailure.InvalidInput,
            (await service.SetStatusAsync(actor, Guid.NewGuid(), (UserAccountStatus)999, default)).Failure);
        Assert.Equal(UserAdministrationFailure.InvalidInput,
            (await service.SetAdministratorAsync(actor, Guid.Empty, true, default)).Failure);
        Assert.Equal(UserAdministrationFailure.InvalidInput,
            (await service.CreateAsync(actor, new("invalid", "Test-Password-42!"), default)).Failure);
        Assert.Equal(UserTokenFailure.InvalidInput, (await service.ListTokensAsync(actor, Guid.Empty, 0, 25, default)).Failure);
        Assert.Equal(UserTokenFailure.InvalidInput, (await service.RevokeTokenAsync(actor, Guid.NewGuid(), "invalid", default)).Failure);
        Assert.Equal(UserAdministrationFailure.InvalidInput, (await service.ResetPasswordAsync(actor, Guid.Empty, new("valid"), default)).Failure);
        Assert.Equal(0, repository.Calls);
    }

    private sealed class FakeRepository : IUserAdministrationRepository
    {
        public int Calls { get; private set; }

        public Task<ManagedUserResult> ResetPasswordAsync(Guid actorId, Guid userId, ResetManagedUserPassword request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ManagedUserResult(null, null));
        }
        public Task<UserTokenListResult> ListTokensAsync(Guid actorId, Guid userId, int offset, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new UserTokenListResult([], false, null));
        }

        public Task<UserTokenRevokeResult> RevokeTokenAsync(Guid actorId, Guid userId, string tokenId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new UserTokenRevokeResult(null));
        }

        public Task<ManagedUserListResult> ListAsync(Guid actorId, int offset, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ManagedUserListResult([], false, null));
        }

        public Task<ManagedUserResult> SetStatusAsync(Guid actorId, Guid userId, UserAccountStatus status, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ManagedUserResult(null, null));
        }

        public Task<ManagedUserResult> SetAdministratorAsync(Guid actorId, Guid userId, bool isAdministrator, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ManagedUserResult(null, null));
        }

        public Task<ManagedUserResult> CreateAsync(Guid actorId, CreateManagedUser request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ManagedUserResult(null, null));
        }

        public Task<ManagedUserResult> GetAsync(Guid actorId, Guid userId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.NotFound));
        }
    }
}
