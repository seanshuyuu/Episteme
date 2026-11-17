using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfWorkspaceMemberRepository(EpistemeDbContext db) : IWorkspaceMemberRepository
{
    public async Task<WorkspaceMemberListResult> ListAsync(
        Guid workspaceId, WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var failure = await AuthorizeAsync(workspaceId, actor, null, false, cancellationToken);
        if (failure is not null)
            return new(null, false, failure);
        var members = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.Status == WorkspaceMemberStatus.Active)
            .OrderBy(m => m.UserId).Skip(offset).Take(limit + 1)
            .Select(m => new WorkspaceMemberSummary(m.UserId, m.Role, m.JoinedAt))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(members.Take(limit).ToArray(), members.Count > limit, null);
    }

    public Task<WorkspaceMemberResult> SetAsync(
        Guid workspaceId, Guid userId, WorkspaceRole role, WorkspaceActor actor, CancellationToken cancellationToken) =>
        ChangeAsync(workspaceId, userId, role, actor, cancellationToken);

    public Task<WorkspaceMemberResult> RemoveAsync(
        Guid workspaceId, Guid userId, WorkspaceActor actor, CancellationToken cancellationToken) =>
        ChangeAsync(workspaceId, userId, null, actor, cancellationToken);

    private async Task<WorkspaceMemberResult> ChangeAsync(
        Guid workspaceId, Guid userId, WorkspaceRole? role, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var failure = await AuthorizeAsync(workspaceId, actor, userId, role is not null, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        var member = await db.WorkspaceMembers.SingleOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);
        if (role is null && (member is null || member.Status != WorkspaceMemberStatus.Active))
            return new(null, WorkspaceManagementFailure.NotFound);

        if (member is { Status: WorkspaceMemberStatus.Active, Role: WorkspaceRole.Owner } && role != WorkspaceRole.Owner)
        {
            var otherOwnerExists = await db.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == workspaceId && m.UserId != userId &&
                m.Status == WorkspaceMemberStatus.Active && m.Role == WorkspaceRole.Owner, cancellationToken);
            if (!otherOwnerExists)
                return new(null, WorkspaceManagementFailure.Conflict);
        }

        if (role is null)
        {
            member!.Remove();
        }
        else if (member is null)
        {
            member = WorkspaceMember.Add(workspaceId, userId, WorkspaceRole.Reader, actor.UserId);
            member.SetRole(role.Value, actor.UserId);
            db.WorkspaceMembers.Add(member);
        }
        else
        {
            member.SetRole(role.Value, actor.UserId);
        }

        db.WorkspaceAuditEvents.Add(new WorkspaceAuditEvent(workspaceId, actor.UserId,
            role is null ? "workspace.member.removed" : "workspace.member.updated", userId, member.Role));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new WorkspaceMemberSummary(member.UserId, member.Role, member.JoinedAt), null);
    }

    private async Task<WorkspaceManagementFailure?> AuthorizeAsync(
        Guid workspaceId, WorkspaceActor actor, Guid? targetUserId, bool requireActiveTarget, CancellationToken cancellationToken)
    {
        // All workspace mutations lock the workspace before checking membership.
        var workspace = await db.Workspaces
            .FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active)
            return WorkspaceManagementFailure.NotFound;

        var target = targetUserId ?? actor.UserId;
        var users = await db.Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.UserId} OR \"Id\" = {target} ORDER BY \"Id\" FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);
        if (!users.Any(u => u.Id == actor.UserId && u.Status == UserAccountStatus.Active))
            return WorkspaceManagementFailure.Unauthorized;
        if (!actor.IsSystemAdministrator && !await db.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == workspaceId && m.UserId == actor.UserId &&
                m.Status == WorkspaceMemberStatus.Active && m.Role == WorkspaceRole.Owner, cancellationToken))
            return WorkspaceManagementFailure.Unauthorized;
        if (targetUserId is not null && !users.Any(u => u.Id == target &&
                (!requireActiveTarget || u.Status == UserAccountStatus.Active)))
            return WorkspaceManagementFailure.NotFound;
        return null;
    }
}
