using Episteme.Application.Identity;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfUserAdministrationRepository(EpistemeDbContext db, UserManager<ApplicationUser> users,
    IOpenIddictTokenManager tokens) : IUserAdministrationRepository
{
    public async Task<ManagedUserResult> ResetPasswordAsync(Guid actorId, Guid userId,
        ResetManagedUserPassword request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (role, target, failure) = await LockAndAuthorizeAsync(actorId, userId, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        if (target is null || role is null)
            throw new InvalidOperationException("Authorized target or administrator role is missing.");
        var resetToken = await users.GeneratePasswordResetTokenAsync(target);
        var result = await users.ResetPasswordAsync(target, resetToken, request.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.All(error => error.Code.StartsWith("Password", StringComparison.Ordinal)))
                return new(null, UserAdministrationFailure.InvalidInput);
            if (result.Errors.All(error => error.Code == "ConcurrencyFailure"))
                return new(null, UserAdministrationFailure.Conflict);
            throw new InvalidOperationException("Identity failed to reset the password: " +
                string.Join(", ", result.Errors.Select(error => error.Code)));
        }
        target.TokenGeneration = Guid.NewGuid();
        db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, userId, "user.password.reset"));
        await db.SaveChangesAsync(cancellationToken);
        var isAdministrator = await db.UserRoles.AnyAsync(
            assignment => assignment.UserId == userId && assignment.RoleId == role.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new ManagedUser(target.Id, target.UserName, target.Email, target.Status, target.CreatedAt,
            isAdministrator, await OwnedWorkspaceCountAsync(userId, cancellationToken), target.DisplayName), null);
    }

    public async Task<UserTokenListResult> ListTokensAsync(Guid actorId, Guid userId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!await IsActiveAdministratorAsync(actorId, cancellationToken))
            return new(null, false, UserTokenFailure.Unauthorized);
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == userId, cancellationToken))
            return new(null, false, UserTokenFailure.NotFound);
        return await EfUserTokenRepository.ListForSubjectAsync(db, userId, offset, limit, cancellationToken);
    }

    public async Task<UserTokenRevokeResult> RevokeTokenAsync(Guid actorId, Guid userId, string tokenId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (_, _, failure) = await LockAndAuthorizeAsync(actorId, userId, cancellationToken);
        if (failure is not null)
            return new(failure == UserAdministrationFailure.NotFound ? UserTokenFailure.NotFound : UserTokenFailure.Unauthorized);
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
            db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, userId, "token.revoked", tokenId));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(null);
    }
    public async Task<ManagedUserResult> GetAsync(Guid actorId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsActiveAdministratorAsync(actorId, cancellationToken))
            return new(null, UserAdministrationFailure.Unauthorized);
        var user = await db.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new ManagedUser(user.Id, user.UserName, user.Email, user.Status, user.CreatedAt,
                (from assignment in db.UserRoles
                 join role in db.Roles on assignment.RoleId equals role.Id
                 where assignment.UserId == user.Id && role.NormalizedName == "ADMINISTRATOR"
                 select assignment.UserId).Any(),
                db.WorkspaceMembers.Count(member => member.UserId == user.Id &&
                    member.Status == WorkspaceMemberStatus.Active && member.Role == WorkspaceRole.Owner),
                user.DisplayName))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? new(null, UserAdministrationFailure.NotFound) : new(user, null);
    }

    public async Task<ManagedUserResult> CreateAsync(Guid actorId, CreateManagedUser request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (_, actor, failure) = await LockAndAuthorizeAsync(actorId, actorId, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        if (actor is null)
            throw new InvalidOperationException("Authorized administrator is missing.");
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = request.Email, Email = request.Email };
        IdentityResult result;
        try
        {
            result = await users.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" })
        {
            return new(null, UserAdministrationFailure.Conflict);
        }
        if (!result.Succeeded)
        {
            if (result.Errors.Any(error => error.Code is "DuplicateUserName" or "DuplicateEmail"))
                return new(null, UserAdministrationFailure.Conflict);
            if (result.Errors.All(error => error.Code.StartsWith("Password", StringComparison.Ordinal) ||
                error.Code is "InvalidUserName" or "InvalidEmail"))
                return new(null, UserAdministrationFailure.InvalidInput);
            throw new InvalidOperationException("Identity failed to create the account: " +
                string.Join(", ", result.Errors.Select(error => error.Code)));
        }
        db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, user.Id, "user.created"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new ManagedUser(user.Id, user.UserName, user.Email, user.Status, user.CreatedAt, false, 0, user.DisplayName), null);
    }

    public async Task<ManagedUserListResult> ListAsync(Guid actorId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!await IsActiveAdministratorAsync(actorId, cancellationToken))
            return new(null, false, UserAdministrationFailure.Unauthorized);
        var users = await db.Users.AsNoTracking().OrderBy(user => user.Id).Skip(offset).Take(limit + 1)
            .Select(user => new ManagedUser(user.Id, user.UserName, user.Email, user.Status, user.CreatedAt,
                (from assignment in db.UserRoles
                 join role in db.Roles on assignment.RoleId equals role.Id
                 where assignment.UserId == user.Id && role.NormalizedName == "ADMINISTRATOR"
                 select assignment.UserId).Any(),
                db.WorkspaceMembers.Count(member => member.UserId == user.Id &&
                    member.Status == WorkspaceMemberStatus.Active && member.Role == WorkspaceRole.Owner),
                user.DisplayName))
            .ToListAsync(cancellationToken);
        return new(users.Take(limit).ToArray(), users.Count > limit, null);
    }

    public async Task<AdminOwnedWorkspaceListResult> ListOwnedWorkspacesAsync(
        Guid actorId, Guid userId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!await IsActiveAdministratorAsync(actorId, cancellationToken))
            return new(null, false, UserAdministrationFailure.Unauthorized);
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == userId, cancellationToken))
            return new(null, false, UserAdministrationFailure.NotFound);

        var owned = db.Workspaces.AsNoTracking()
            .Where(workspace => db.WorkspaceMembers.Any(member =>
                member.WorkspaceId == workspace.Id && member.UserId == userId &&
                member.Status == WorkspaceMemberStatus.Active && member.Role == WorkspaceRole.Owner))
            .OrderByDescending(workspace => workspace.CreatedAt)
            .ThenBy(workspace => workspace.Id)
            .Skip(offset)
            .Take(limit + 1);
        var items = await owned.Select(workspace => new AdminOwnedWorkspace(
            workspace.Id, workspace.Name, workspace.Type.ToString(), workspace.Status.ToString(), workspace.CreatedAt))
            .ToListAsync(cancellationToken);
        return new(items.Take(limit).ToArray(), items.Count > limit, null);
    }

    public async Task<UserAdministrationOverviewResult> GetOverviewAsync(
        Guid actorId, CancellationToken cancellationToken)
    {
        if (!await IsActiveAdministratorAsync(actorId, cancellationToken))
            return new(null, UserAdministrationFailure.Unauthorized);

        var totalUsers = await db.Users.CountAsync(cancellationToken);
        var activeUsers = await db.Users.CountAsync(user => user.Status == UserAccountStatus.Active, cancellationToken);
        var totalWorkspaces = await db.Workspaces.CountAsync(cancellationToken);
        var provisioningWorkspaces = await db.Workspaces.CountAsync(
            workspace => workspace.Status == WorkspaceStatus.Provisioning, cancellationToken);
        var administratorCount = await (from assignment in db.UserRoles
                                        join role in db.Roles on assignment.RoleId equals role.Id
                                        join user in db.Users on assignment.UserId equals user.Id
                                        where role.NormalizedName == "ADMINISTRATOR" &&
                                            user.Status == UserAccountStatus.Active
                                        select assignment.UserId).CountAsync(cancellationToken);
        var recentUsers = await db.Users.AsNoTracking()
            .OrderByDescending(user => user.CreatedAt)
            .ThenBy(user => user.Id)
            .Take(3)
            .Select(user => new ManagedUser(user.Id, user.UserName, user.Email, user.Status, user.CreatedAt,
                (from assignment in db.UserRoles
                 join role in db.Roles on assignment.RoleId equals role.Id
                 where assignment.UserId == user.Id && role.NormalizedName == "ADMINISTRATOR"
                 select assignment.UserId).Any(),
                db.WorkspaceMembers.Count(member => member.UserId == user.Id &&
                    member.Status == WorkspaceMemberStatus.Active && member.Role == WorkspaceRole.Owner),
                user.DisplayName))
            .ToArrayAsync(cancellationToken);

        return new(new UserAdministrationOverview(totalUsers, activeUsers, totalWorkspaces,
            provisioningWorkspaces, administratorCount, recentUsers), null);
    }

    public async Task<ManagedUserResult> SetStatusAsync(
        Guid actorId, Guid userId, UserAccountStatus status, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (roleResult, targetResult, failure) = await LockAndAuthorizeAsync(actorId, userId, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        var administratorRole = roleResult ?? throw new InvalidOperationException("Authorized administrator role is missing.");
        var target = targetResult ?? throw new InvalidOperationException("Authorized target user is missing.");
        var isAdministrator = await db.UserRoles.AnyAsync(
            r => r.UserId == userId && r.RoleId == administratorRole.Id, cancellationToken);
        if (target.Status != status && status == UserAccountStatus.Disabled)
        {
            if (await (from member in db.WorkspaceMembers
                       join workspace in db.Workspaces on member.WorkspaceId equals workspace.Id
                       where member.UserId == userId && member.Status == WorkspaceMemberStatus.Active &&
                           member.Role == WorkspaceRole.Owner && workspace.Status != WorkspaceStatus.Archived
                       select member.UserId).AnyAsync(cancellationToken))
                return new(null, UserAdministrationFailure.Conflict);
            if (isAdministrator && !await HasOtherActiveAdministratorAsync(administratorRole.Id, userId, cancellationToken))
                return new(null, UserAdministrationFailure.Conflict);
        }

        if (target.Status != status)
        {
            target.Status = status;
            if (status == UserAccountStatus.Disabled)
            {
                target.TokenGeneration = Guid.NewGuid();
                target.SecurityStamp = Guid.NewGuid().ToString();
            }
            target.ConcurrencyStamp = Guid.NewGuid().ToString();
            db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, userId,
                status == UserAccountStatus.Active ? "user.enabled" : "user.disabled"));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new(null, UserAdministrationFailure.Conflict);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(new ManagedUser(target.Id, target.UserName, target.Email, target.Status, target.CreatedAt,
            isAdministrator, await OwnedWorkspaceCountAsync(userId, cancellationToken), target.DisplayName), null);
    }

    public async Task<ManagedUserResult> SetAdministratorAsync(
        Guid actorId, Guid userId, bool isAdministrator, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (roleResult, targetResult, failure) = await LockAndAuthorizeAsync(actorId, userId, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        var role = roleResult ?? throw new InvalidOperationException("Authorized administrator role is missing.");
        var target = targetResult ?? throw new InvalidOperationException("Authorized target user is missing.");
        var assignment = await db.UserRoles.SingleOrDefaultAsync(
            r => r.UserId == userId && r.RoleId == role.Id, cancellationToken);
        if (isAdministrator && target.Status != UserAccountStatus.Active)
            return new(null, UserAdministrationFailure.Conflict);
        if (!isAdministrator && assignment is not null && target.Status == UserAccountStatus.Active &&
            !await HasOtherActiveAdministratorAsync(role.Id, userId, cancellationToken))
            return new(null, UserAdministrationFailure.Conflict);
        if ((assignment is not null) != isAdministrator)
        {
            if (isAdministrator)
                db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = role.Id });
            else
                db.UserRoles.Remove(assignment!);
            target.ConcurrencyStamp = Guid.NewGuid().ToString();
            db.AccountAuditEvents.Add(new AccountAuditEvent(actorId, userId,
                isAdministrator ? "user.administrator.granted" : "user.administrator.revoked"));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new(null, UserAdministrationFailure.Conflict);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(new ManagedUser(target.Id, target.UserName, target.Email, target.Status, target.CreatedAt,
            isAdministrator, await OwnedWorkspaceCountAsync(userId, cancellationToken), target.DisplayName), null);
    }

    private Task<int> OwnedWorkspaceCountAsync(Guid userId, CancellationToken cancellationToken) =>
        db.WorkspaceMembers.CountAsync(member => member.UserId == userId &&
            member.Status == WorkspaceMemberStatus.Active && member.Role == WorkspaceRole.Owner, cancellationToken);

    private async Task<(IdentityRole<Guid>? Role, ApplicationUser? Target, UserAdministrationFailure? Failure)> LockAndAuthorizeAsync(
        Guid actorId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await db.Roles
            .FromSqlRaw("SELECT * FROM \"AspNetRoles\" WHERE \"NormalizedName\" = 'ADMINISTRATOR' FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (role is null)
            return (null, null, UserAdministrationFailure.Unauthorized);
        var locked = await db.Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actorId} OR \"Id\" = {userId} ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        var actor = locked.FirstOrDefault(u => u.Id == actorId);
        var target = locked.FirstOrDefault(u => u.Id == userId);
        if (actor is null || actor.Status != UserAccountStatus.Active ||
            !await db.UserRoles.AnyAsync(r => r.UserId == actorId && r.RoleId == role.Id, cancellationToken))
            return (null, null, UserAdministrationFailure.Unauthorized);
        return target is null ? (null, null, UserAdministrationFailure.NotFound) : (role, target, null);
    }

    private Task<bool> HasOtherActiveAdministratorAsync(Guid roleId, Guid userId, CancellationToken cancellationToken) =>
        (from assignment in db.UserRoles
         join other in db.Users on assignment.UserId equals other.Id
         where assignment.RoleId == roleId && other.Id != userId && other.Status == UserAccountStatus.Active
         select other.Id).AnyAsync(cancellationToken);

    private Task<bool> IsActiveAdministratorAsync(Guid userId, CancellationToken cancellationToken) =>
        (from user in db.Users.AsNoTracking()
         join assignment in db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
         join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
         where user.Id == userId && user.Status == UserAccountStatus.Active && role.NormalizedName == "ADMINISTRATOR"
         select user.Id).AnyAsync(cancellationToken);
}
