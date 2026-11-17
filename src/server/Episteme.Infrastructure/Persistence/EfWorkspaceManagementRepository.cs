using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfWorkspaceManagementRepository(EpistemeDbContext dbContext) : IWorkspaceManagementRepository
{
    public Task<WorkspaceManagementResult> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.WorkspaceId, command.Actor, command.ExpectedVersion,
            workspace => workspace.UpdateDetails(command.Name, command.Type, command.Description),
            "workspace.updated", cancellationToken);

    public Task<WorkspaceManagementResult> ArchiveAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, CancellationToken cancellationToken) =>
        ChangeAsync(workspaceId, actor, expectedVersion, workspace => workspace.Archive(),
            "workspace.archived", cancellationToken);

    public Task<WorkspaceManagementResult> SetConfigurationAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, WorkspaceConfiguration configuration, CancellationToken cancellationToken) =>
        ChangeAsync(workspaceId, actor, expectedVersion, workspace => workspace.SetConfiguration(configuration),
            "workspace.configuration.updated", cancellationToken);

    private async Task<WorkspaceManagementResult> ChangeAsync(
        Guid workspaceId,
        WorkspaceActor actor,
        Guid expectedVersion,
        Action<Workspace> change,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // All workspace-member mutations must take the same workspace lock first; this
        // prevents a permission change racing a metadata or archive operation.
        var workspace = await dbContext.Workspaces
            .FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active)
            return new(null, WorkspaceManagementFailure.NotFound);

        var user = await dbContext.Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.UserId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return new(null, WorkspaceManagementFailure.Unauthorized);

        if (!actor.IsSystemAdministrator)
        {
            var isOwner = await dbContext.WorkspaceMembers.AnyAsync(member =>
                member.WorkspaceId == workspaceId && member.UserId == actor.UserId &&
                member.Status == WorkspaceMemberStatus.Active && member.Role == WorkspaceRole.Owner,
                cancellationToken);
            if (!isOwner)
                return new(null, WorkspaceManagementFailure.Unauthorized);
        }

        if (workspace.ConcurrencyStamp != expectedVersion)
            return new(null, WorkspaceManagementFailure.Conflict);

        change(workspace);
        dbContext.WorkspaceAuditEvents.Add(new WorkspaceAuditEvent(workspaceId, actor.UserId, eventType));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new WorkspaceSummary(
            workspace.Id, workspace.Name, workspace.Slug, workspace.Type,
            workspace.Description, workspace.DefaultBranch, workspace.ConcurrencyStamp), null);
    }
}
