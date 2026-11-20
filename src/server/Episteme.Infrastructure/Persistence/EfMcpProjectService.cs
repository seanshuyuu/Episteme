using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfMcpProjectService(EpistemeDbContext db, IProjectService projects) : IMcpProjectService
{
    public async Task<ProjectListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var failure = await AuthorizeAsync(workspaceId, actor, McpPermission.ReadProject, cancellationToken);
        if (failure is not null)
            return new(null, false, failure);
        var result = await projects.ListAsync(workspaceId, actor, offset, limit, cancellationToken);
        if (result.Failure is null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ProjectResult> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var failure = await AuthorizeAsync(command.WorkspaceId, command.Actor, McpPermission.CreateProject, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        var result = await projects.CreateAsync(command, cancellationToken);
        if (result.Failure is null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ProjectResult> UpdateAsync(UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var workspaceId = await FindWorkspaceAsync(command.ProjectId, cancellationToken);
        var failure = workspaceId is null ? ProjectFailure.NotFound :
            await AuthorizeAsync(workspaceId.Value, command.Actor, McpPermission.UpdateProject, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        var result = await projects.UpdateAsync(command, cancellationToken);
        if (result.Failure is null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ProjectResult> DeleteAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var workspaceId = await FindWorkspaceAsync(projectId, cancellationToken);
        var failure = workspaceId is null ? ProjectFailure.NotFound :
            await AuthorizeAsync(workspaceId.Value, actor, McpPermission.DeleteProject, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        var result = await projects.DeleteAsync(projectId, actor, expectedVersion, cancellationToken);
        if (result.Failure is null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private Task<Guid?> FindWorkspaceAsync(Guid projectId, CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking().Where(project => project.Id == projectId && project.Status != ProjectStatus.Discarded)
            .Select(project => (Guid?)project.WorkspaceId).SingleOrDefaultAsync(cancellationToken);

    private async Task<ProjectFailure?> AuthorizeAsync(Guid workspaceId, WorkspaceActor actor,
        McpPermission required, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.FromSqlInterpolated(
            $"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active)
            return ProjectFailure.NotFound;
        var user = await db.Users.FromSqlInterpolated(
            $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.UserId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return ProjectFailure.Unauthorized;
        var allowed = await EfMcpAuthorization.WorkspacePermissionsAsync(db, workspaceId, actor.UserId, cancellationToken);
        return (allowed & required) == required ? null : ProjectFailure.Unauthorized;
    }
}
