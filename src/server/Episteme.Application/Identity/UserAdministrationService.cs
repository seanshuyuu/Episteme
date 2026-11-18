using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using System.ComponentModel.DataAnnotations;

namespace Episteme.Application.Identity;

public sealed record ManagedUser(Guid Id, string? UserName, string? Email, UserAccountStatus Status,
    DateTimeOffset CreatedAt, bool IsAdministrator, int OwnedWorkspaceCount = 0);
public sealed record AdminOwnedWorkspace(Guid Id, string Name, string Type, string Status, DateTimeOffset CreatedAt);
public sealed record AdminOwnedWorkspaceListResult(
    IReadOnlyList<AdminOwnedWorkspace>? Items, bool HasMore, UserAdministrationFailure? Failure);
public sealed record UserAdministrationOverview(
    int TotalUsers, int ActiveUsers, int TotalWorkspaces, int ProvisioningWorkspaces,
    int AdministratorCount, IReadOnlyList<ManagedUser> RecentUsers);
public sealed record UserAdministrationOverviewResult(
    UserAdministrationOverview? Overview, UserAdministrationFailure? Failure);
public enum UserAdministrationFailure { Unauthorized, InvalidInput, NotFound, Conflict }
public sealed record ManagedUserListResult(IReadOnlyList<ManagedUser>? Items, bool HasMore, UserAdministrationFailure? Failure);
public sealed record ManagedUserResult(ManagedUser? User, UserAdministrationFailure? Failure);
public sealed record CreateManagedUser(string Email, string Password)
{
    public override string ToString() => "CreateManagedUser { Password = [REDACTED] }";
}
public sealed record ResetManagedUserPassword(string Password)
{
    public override string ToString() => "ResetManagedUserPassword { Password = [REDACTED] }";
}

public interface IUserAdministrationRepository
{
    Task<ManagedUserListResult> ListAsync(Guid actorId, int offset, int limit, CancellationToken cancellationToken);
    Task<AdminOwnedWorkspaceListResult> ListOwnedWorkspacesAsync(
        Guid actorId, Guid userId, int offset, int limit, CancellationToken cancellationToken);
    Task<UserAdministrationOverviewResult> GetOverviewAsync(Guid actorId, CancellationToken cancellationToken);
    Task<ManagedUserResult> SetStatusAsync(Guid actorId, Guid userId, UserAccountStatus status, CancellationToken cancellationToken);
    Task<ManagedUserResult> SetAdministratorAsync(Guid actorId, Guid userId, bool isAdministrator, CancellationToken cancellationToken);
    Task<ManagedUserResult> CreateAsync(Guid actorId, CreateManagedUser request, CancellationToken cancellationToken);
    Task<ManagedUserResult> GetAsync(Guid actorId, Guid userId, CancellationToken cancellationToken);
    Task<UserTokenListResult> ListTokensAsync(Guid actorId, Guid userId, int offset, int limit, CancellationToken cancellationToken);
    Task<UserTokenRevokeResult> RevokeTokenAsync(Guid actorId, Guid userId, string tokenId, CancellationToken cancellationToken);
    Task<ManagedUserResult> ResetPasswordAsync(Guid actorId, Guid userId, ResetManagedUserPassword request, CancellationToken cancellationToken);
}

public interface IUserAdministrationService
{
    Task<ManagedUserListResult> ListAsync(WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken);
    Task<AdminOwnedWorkspaceListResult> ListOwnedWorkspacesAsync(
        WorkspaceActor actor, Guid userId, int offset, int limit, CancellationToken cancellationToken);
    Task<UserAdministrationOverviewResult> GetOverviewAsync(WorkspaceActor actor, CancellationToken cancellationToken);
    Task<ManagedUserResult> SetStatusAsync(WorkspaceActor actor, Guid userId, UserAccountStatus status, CancellationToken cancellationToken);
    Task<ManagedUserResult> SetAdministratorAsync(WorkspaceActor actor, Guid userId, bool isAdministrator, CancellationToken cancellationToken);
    Task<ManagedUserResult> CreateAsync(WorkspaceActor actor, CreateManagedUser request, CancellationToken cancellationToken);
    Task<ManagedUserResult> GetAsync(WorkspaceActor actor, Guid userId, CancellationToken cancellationToken);
    Task<UserTokenListResult> ListTokensAsync(WorkspaceActor actor, Guid userId, int offset, int limit, CancellationToken cancellationToken);
    Task<UserTokenRevokeResult> RevokeTokenAsync(WorkspaceActor actor, Guid userId, string tokenId, CancellationToken cancellationToken);
    Task<ManagedUserResult> ResetPasswordAsync(WorkspaceActor actor, Guid userId, ResetManagedUserPassword request, CancellationToken cancellationToken);
}

public sealed class UserAdministrationService(IUserAdministrationRepository repository) : IUserAdministrationService
{
    public Task<ManagedUserResult> ResetPasswordAsync(WorkspaceActor actor, Guid userId, ResetManagedUserPassword request, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.Unauthorized));
        if (userId == Guid.Empty || request is null || string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.InvalidInput));
        return repository.ResetPasswordAsync(actor.UserId, userId, request, cancellationToken);
    }

    public Task<UserTokenListResult> ListTokensAsync(WorkspaceActor actor, Guid userId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new UserTokenListResult(null, false, UserTokenFailure.Unauthorized));
        if (userId == Guid.Empty || offset < 0 || offset > 10000 || limit is < 1 or > 100)
            return Task.FromResult(new UserTokenListResult(null, false, UserTokenFailure.InvalidInput));
        return repository.ListTokensAsync(actor.UserId, userId, offset, limit, cancellationToken);
    }

    public Task<UserTokenRevokeResult> RevokeTokenAsync(WorkspaceActor actor, Guid userId, string tokenId, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new UserTokenRevokeResult(UserTokenFailure.Unauthorized));
        if (userId == Guid.Empty || !Guid.TryParseExact(tokenId, "D", out _))
            return Task.FromResult(new UserTokenRevokeResult(UserTokenFailure.InvalidInput));
        return repository.RevokeTokenAsync(actor.UserId, userId, tokenId, cancellationToken);
    }
    public Task<ManagedUserListResult> ListAsync(WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new ManagedUserListResult(null, false, UserAdministrationFailure.Unauthorized));
        if (offset < 0 || offset > 10000 || limit is < 1 or > 100)
            return Task.FromResult(new ManagedUserListResult(null, false, UserAdministrationFailure.InvalidInput));
        return repository.ListAsync(actor.UserId, offset, limit, cancellationToken);
    }

    public Task<AdminOwnedWorkspaceListResult> ListOwnedWorkspacesAsync(
        WorkspaceActor actor, Guid userId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new AdminOwnedWorkspaceListResult(null, false, UserAdministrationFailure.Unauthorized));
        if (userId == Guid.Empty || offset < 0 || offset > 10000 || limit is < 1 or > 100)
            return Task.FromResult(new AdminOwnedWorkspaceListResult(null, false, UserAdministrationFailure.InvalidInput));
        return repository.ListOwnedWorkspacesAsync(actor.UserId, userId, offset, limit, cancellationToken);
    }

    public Task<UserAdministrationOverviewResult> GetOverviewAsync(
        WorkspaceActor actor, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new UserAdministrationOverviewResult(null, UserAdministrationFailure.Unauthorized));
        return repository.GetOverviewAsync(actor.UserId, cancellationToken);
    }

    public Task<ManagedUserResult> SetStatusAsync(WorkspaceActor actor, Guid userId, UserAccountStatus status, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.Unauthorized));
        if (userId == Guid.Empty || status is not (UserAccountStatus.Active or UserAccountStatus.Disabled))
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.InvalidInput));
        return repository.SetStatusAsync(actor.UserId, userId, status, cancellationToken);
    }

    private static bool Authorized(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.IsSystemAdministrator && actor.Scopes.Contains("users.manage");

    public Task<ManagedUserResult> SetAdministratorAsync(WorkspaceActor actor, Guid userId, bool isAdministrator, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.Unauthorized));
        if (userId == Guid.Empty)
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.InvalidInput));
        return repository.SetAdministratorAsync(actor.UserId, userId, isAdministrator, cancellationToken);
    }

    public Task<ManagedUserResult> CreateAsync(WorkspaceActor actor, CreateManagedUser request, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.Unauthorized));
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256 ||
            request.Email != request.Email.Trim() || !new EmailAddressAttribute().IsValid(request.Email) ||
            string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.InvalidInput));
        return repository.CreateAsync(actor.UserId, request, cancellationToken);
    }

    public Task<ManagedUserResult> GetAsync(WorkspaceActor actor, Guid userId, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.Unauthorized));
        if (userId == Guid.Empty)
            return Task.FromResult(new ManagedUserResult(null, UserAdministrationFailure.InvalidInput));
        return repository.GetAsync(actor.UserId, userId, cancellationToken);
    }
}
