using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfMcpSettingsRepository(EpistemeDbContext db) : IMcpSettingsRepository
{
    public async Task<McpSettingsResult<McpPersonalSettings>> GetPersonalAsync(
        WorkspaceActor actor, CancellationToken cancellationToken)
    {
        if (!await IsActiveAsync(actor.UserId, cancellationToken))
            return new(null, McpSettingsFailure.Unauthorized);
        var settings = await db.McpUserSettings.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == actor.UserId, cancellationToken);
        return new(new(settings?.Enabled ?? false, settings?.AllowCreateWorkspace ?? false), null);
    }

    public async Task<McpSettingsResult<McpPersonalSettings>> SetPersonalAsync(
        WorkspaceActor actor, McpPersonalSettings settings, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.FromSqlInterpolated(
            $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.UserId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return new(null, McpSettingsFailure.Unauthorized);
        var current = await db.McpUserSettings.SingleOrDefaultAsync(item => item.UserId == actor.UserId, cancellationToken);
        if (current is null)
        {
            current = new McpUserSettings { UserId = actor.UserId };
            db.McpUserSettings.Add(current);
        }
        current.Enabled = settings.Enabled;
        current.AllowCreateWorkspace = settings.AllowCreateWorkspace;
        db.AccountAuditEvents.Add(new AccountAuditEvent(actor.UserId, actor.UserId, "mcp.settings.updated"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(settings, null);
    }

    public async Task<McpSettingsResult<McpWorkspaceSettingsView>> GetWorkspaceAsync(
        WorkspaceActor actor, Guid workspaceId, CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(actor.UserId, workspaceId, cancellationToken);
        if (role is null)
            return new(null, McpSettingsFailure.Unauthorized);
        return new(await GetViewAsync(actor.UserId, workspaceId, role.Value, cancellationToken), null);
    }

    public async Task<McpSettingsResult<McpWorkspaceSettingsView>> SetWorkspaceAsync(
        WorkspaceActor actor, Guid workspaceId, McpPermission permissions, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockWorkspaceAsync(workspaceId, cancellationToken);
        await LockUsersAsync(actor.UserId, actor.UserId, cancellationToken);
        var role = await GetRoleAsync(actor.UserId, workspaceId, cancellationToken);
        if (role is null)
            return new(null, McpSettingsFailure.Unauthorized);
        var current = await db.McpWorkspaceUserSettings.SingleOrDefaultAsync(
            item => item.WorkspaceId == workspaceId && item.UserId == actor.UserId, cancellationToken);
        var maximum = McpAccessPolicy.PermissionsForRole(role.Value);
        if (role != WorkspaceRole.Owner)
            maximum &= (McpPermission)(current?.GrantedPermissions ?? 0);
        if ((permissions & ~maximum) != 0)
            return new(null, McpSettingsFailure.Unauthorized);
        if (current is null)
        {
            current = new McpWorkspaceUserSettings { WorkspaceId = workspaceId, UserId = actor.UserId };
            db.McpWorkspaceUserSettings.Add(current);
        }
        current.PersonalPermissions = (int)permissions;
        db.AccountAuditEvents.Add(new AccountAuditEvent(actor.UserId, actor.UserId, "mcp.workspace-settings.updated"));
        await db.SaveChangesAsync(cancellationToken);
        var view = await GetViewAsync(actor.UserId, workspaceId, role.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(view, null);
    }

    public async Task<McpSettingsResult<bool>> SetWorkspaceEnabledAsync(
        WorkspaceActor actor, Guid workspaceId, bool enabled, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockWorkspaceAsync(workspaceId, cancellationToken);
        await LockUsersAsync(actor.UserId, actor.UserId, cancellationToken);
        if (await GetRoleAsync(actor.UserId, workspaceId, cancellationToken) != WorkspaceRole.Owner)
            return new(false, McpSettingsFailure.Unauthorized);
        var current = await db.McpWorkspaceSettings.SingleOrDefaultAsync(
            item => item.WorkspaceId == workspaceId, cancellationToken);
        if (current is null)
        {
            current = new McpWorkspaceSettings { WorkspaceId = workspaceId };
            db.McpWorkspaceSettings.Add(current);
        }
        current.Enabled = enabled;
        db.WorkspaceAuditEvents.Add(new WorkspaceAuditEvent(workspaceId, actor.UserId, "mcp.workspace-switch.updated"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(enabled, null);
    }

    public async Task<McpSettingsResult<McpPermission>> GetMemberGrantAsync(
        WorkspaceActor actor, Guid workspaceId, Guid memberId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockWorkspaceAsync(workspaceId, cancellationToken);
        await LockUsersAsync(actor.UserId, memberId, cancellationToken);
        if (await GetRoleAsync(actor.UserId, workspaceId, cancellationToken) != WorkspaceRole.Owner)
            return new(McpPermission.None, McpSettingsFailure.Unauthorized);
        var targetRole = await GetRoleAsync(memberId, workspaceId, cancellationToken);
        if (targetRole is null)
            return new(McpPermission.None, McpSettingsFailure.NotFound);
        if (targetRole == WorkspaceRole.Owner)
            return new(McpPermission.None, McpSettingsFailure.InvalidInput);
        var permissions = await db.McpWorkspaceUserSettings.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.UserId == memberId)
            .Select(item => item.GrantedPermissions).SingleOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new((McpPermission)permissions, null);
    }

    public async Task<McpSettingsResult<McpPermission>> SetMemberGrantAsync(
        WorkspaceActor actor, Guid workspaceId, Guid memberId, McpPermission permissions, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockWorkspaceAsync(workspaceId, cancellationToken);
        await LockUsersAsync(actor.UserId, memberId, cancellationToken);
        if (await GetRoleAsync(actor.UserId, workspaceId, cancellationToken) != WorkspaceRole.Owner)
            return new(McpPermission.None, McpSettingsFailure.Unauthorized);
        var targetRole = await GetRoleAsync(memberId, workspaceId, cancellationToken);
        if (targetRole is null)
            return new(McpPermission.None, McpSettingsFailure.NotFound);
        if (targetRole == WorkspaceRole.Owner || (permissions & ~McpAccessPolicy.PermissionsForRole(targetRole.Value)) != 0)
            return new(McpPermission.None, McpSettingsFailure.InvalidInput);
        var current = await db.McpWorkspaceUserSettings.SingleOrDefaultAsync(
            item => item.WorkspaceId == workspaceId && item.UserId == memberId, cancellationToken);
        if (current is null)
        {
            current = new McpWorkspaceUserSettings { WorkspaceId = workspaceId, UserId = memberId };
            db.McpWorkspaceUserSettings.Add(current);
        }
        current.GrantedPermissions = (int)permissions;
        db.WorkspaceAuditEvents.Add(new WorkspaceAuditEvent(workspaceId, actor.UserId, "mcp.member-grant.updated", memberId, targetRole));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(permissions, null);
    }

    private Task<bool> IsActiveAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.Status == UserAccountStatus.Active, cancellationToken);

    private Task<WorkspaceRole?> GetRoleAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken) =>
        (from member in db.WorkspaceMembers.AsNoTracking()
         join workspace in db.Workspaces.AsNoTracking() on member.WorkspaceId equals workspace.Id
         join user in db.Users.AsNoTracking() on member.UserId equals user.Id
         where member.UserId == userId && workspace.Id == workspaceId &&
               member.Status == WorkspaceMemberStatus.Active && workspace.Status == WorkspaceStatus.Active &&
               user.Status == UserAccountStatus.Active
         select (WorkspaceRole?)member.Role).SingleOrDefaultAsync(cancellationToken);

    private async Task LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        _ = await db.Workspaces.FromSqlInterpolated(
            $"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    private async Task LockUsersAsync(Guid actorId, Guid targetId, CancellationToken cancellationToken) =>
        _ = await db.Users.FromSqlInterpolated(
            $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actorId} OR \"Id\" = {targetId} ORDER BY \"Id\" FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);

    private async Task<McpWorkspaceSettingsView> GetViewAsync(
        Guid userId, Guid workspaceId, WorkspaceRole role, CancellationToken cancellationToken)
    {
        var enabled = await db.McpWorkspaceSettings.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId)
            .Select(item => item.Enabled).SingleOrDefaultAsync(cancellationToken);
        var settings = await db.McpWorkspaceUserSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.WorkspaceId == workspaceId, cancellationToken);
        return new(enabled, (McpPermission)(settings?.PersonalPermissions ?? 0),
            (McpPermission)(settings?.GrantedPermissions ?? 0), role == WorkspaceRole.Owner);
    }
}
