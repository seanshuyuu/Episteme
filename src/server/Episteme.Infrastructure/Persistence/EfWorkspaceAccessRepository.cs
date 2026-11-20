using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Episteme.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfWorkspaceAccessRepository(EpistemeDbContext dbContext) : IWorkspaceAccessRepository
{
    public Task<WorkspaceAccessSnapshot?> GetAccessSnapshotAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken cancellationToken) =>
        GetSnapshotAsync(workspaceId, userId, false, cancellationToken);

    public Task<WorkspaceAccessSnapshot?> GetMcpAccessSnapshotAsync(
        Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        GetSnapshotAsync(workspaceId, userId, true, cancellationToken);

    private Task<WorkspaceAccessSnapshot?> GetSnapshotAsync(
        Guid workspaceId, Guid userId, bool isMcp, CancellationToken cancellationToken)
    {
        // A left join returns the workspace and membership decision from one database statement,
        // avoiding an avoidable authorization race between separate workspace/member reads.
        return (
            from workspace in dbContext.Workspaces
            where workspace.Id == workspaceId
            join member in dbContext.WorkspaceMembers.Where(member => member.UserId == userId)
                on workspace.Id equals member.WorkspaceId into memberships
            from member in memberships.DefaultIfEmpty()
            join user in dbContext.Users on userId equals user.Id into accounts
            from user in accounts.DefaultIfEmpty()
            select new WorkspaceAccessSnapshot(
                workspace,
                member != null && member.Status == WorkspaceMemberStatus.Active,
                member == null ? null : member.Role,
                user != null && user.Status == UserAccountStatus.Active,
                isMcp && member != null && member.Status == WorkspaceMemberStatus.Active &&
                (member.Role == WorkspaceRole.Owner || member.Role == WorkspaceRole.Editor || member.Role == WorkspaceRole.Reader) &&
                dbContext.McpUserSettings.Any(setting => setting.UserId == userId && setting.Enabled) &&
                dbContext.McpWorkspaceSettings.Any(setting => setting.WorkspaceId == workspaceId && setting.Enabled) &&
                dbContext.McpWorkspaceUserSettings.Any(setting =>
                    setting.WorkspaceId == workspaceId && setting.UserId == userId &&
                    (setting.PersonalPermissions & (int)McpPermission.ReadWorkspace) != 0 &&
                    (member.Role == WorkspaceRole.Owner ||
                     (setting.GrantedPermissions & (int)McpPermission.ReadWorkspace) != 0))))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
