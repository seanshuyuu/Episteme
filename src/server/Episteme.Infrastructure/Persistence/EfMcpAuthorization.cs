using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

internal static class EfMcpAuthorization
{
    // Caller holds the workspace and account row locks for the operation's entire transaction.
    public static async Task<McpPermission> WorkspacePermissionsAsync(
        EpistemeDbContext db, Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("MCP authorization requires an active transaction.");

        var userEnabled = await db.McpUserSettings.AsNoTracking()
            .AnyAsync(item => item.UserId == userId && item.Enabled, cancellationToken);
        var enabled = await db.McpWorkspaceSettings.AsNoTracking()
            .AnyAsync(item => item.WorkspaceId == workspaceId && item.Enabled, cancellationToken);
        var role = await db.WorkspaceMembers.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.UserId == userId &&
                item.Status == WorkspaceMemberStatus.Active)
            .Select(item => (WorkspaceRole?)item.Role).SingleOrDefaultAsync(cancellationToken);
        var settings = await db.McpWorkspaceUserSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.UserId == userId, cancellationToken);
        if (!userEnabled || !enabled || role is null || settings is null)
            return McpPermission.None;

        var access = new McpWorkspaceAccess(enabled, role, true,
            (McpPermission)settings.PersonalPermissions, (McpPermission)settings.GrantedPermissions);
        var result = McpPermission.None;
        foreach (var permission in Enum.GetValues<McpPermission>())
            if (McpAccessPolicy.CanAccessWorkspace(userEnabled, access, permission))
                result |= permission;
        return result;
    }
}
