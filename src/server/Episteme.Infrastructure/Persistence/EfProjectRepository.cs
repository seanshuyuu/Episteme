using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Git;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfProjectRepository(
    EpistemeDbContext db,
    IWorkspaceRepositoryProvisioner provisioner,
    GitWorkspaceReader reader) : IProjectRepository
{
    public async Task<ProjectListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeWorkspaceAsync(workspaceId, actor, false, cancellationToken);
        if (authorization.Failure is not null)
            return new(null, false, authorization.Failure);

        var projects = await db.Projects.AsNoTracking()
            .Where(project => project.WorkspaceId == workspaceId && project.Status != ProjectStatus.Discarded)
            .OrderBy(project => project.Status == ProjectStatus.Working ? 0 :
                               project.Status == ProjectStatus.Archived ? 1 : 2)
            .ThenBy(project => project.Name)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var summaries = new List<ProjectSummary>(projects.Count);
        foreach (var project in projects.Take(limit))
        {
            var stats = await reader.GetStatsAsync(project.StorageKey, project.DefaultBranch, cancellationToken);
            summaries.Add(ToSummary(project, stats.FileCount, stats.FolderCount));
        }
        return new(summaries, projects.Count > limit, null);
    }

    public async Task<ProjectResult> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeWorkspaceAsync(command.WorkspaceId, command.Actor, true, cancellationToken);
        if (authorization.Failure is not null)
            return new(null, authorization.Failure);

        Project project;
        try
        {
            project = Project.Create(command.WorkspaceId, command.Actor.UserId, command.Name, command.Slug, command.Description);
        }
        catch (ArgumentException)
        {
            return new(null, ProjectFailure.InvalidInput);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(null, ProjectFailure.Conflict);
        }

        try
        {
            await provisioner.InitializeAsync(project.StorageKey, project.DefaultBranch, cancellationToken,
                ["docs"]);
        }
        catch (WorkspaceProvisioningException)
        {
            db.Projects.Remove(project);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new(null, ProjectFailure.ProvisioningFailed);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(ToSummary(project, 0, 1), null);
    }

    public async Task<ProjectResult> UpdateAsync(UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(item => item.Id == command.ProjectId, cancellationToken);
        if (project is null)
            return new(null, ProjectFailure.NotFound);
        var authorization = await AuthorizeWorkspaceAsync(project.WorkspaceId, command.Actor, true, cancellationToken);
        if (authorization.Failure is not null)
            return new(null, authorization.Failure);
        if (project.ConcurrencyStamp != command.ExpectedVersion)
            return new(null, ProjectFailure.Conflict);
        try
        {
            project.Update(command.Name, command.Slug, command.Description);
        }
        catch (ArgumentException)
        {
            return new(null, ProjectFailure.InvalidInput);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(null, ProjectFailure.Conflict);
        }
        var stats = await reader.GetStatsAsync(project.StorageKey, project.DefaultBranch, cancellationToken);
        return new(ToSummary(project, stats.FileCount, stats.FolderCount), null);
    }

    public Task<ProjectResult> SetStatusAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        ProjectStatus status, CancellationToken cancellationToken)
    {
        if (status == ProjectStatus.Discarded)
            return DeleteAsync(projectId, actor, expectedVersion, cancellationToken);
        return SetStatusInternalAsync(projectId, actor, expectedVersion, status, cancellationToken);
    }

    private async Task<ProjectResult> SetStatusInternalAsync(Guid projectId, WorkspaceActor actor,
        Guid expectedVersion, ProjectStatus status, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null)
            return new(null, ProjectFailure.NotFound);
        var authorization = await AuthorizeWorkspaceAsync(project.WorkspaceId, actor, true, cancellationToken);
        if (authorization.Failure is not null)
            return new(null, authorization.Failure);
        if (project.ConcurrencyStamp != expectedVersion)
            return new(null, ProjectFailure.Conflict);
        project.SetStatus(status);
        await db.SaveChangesAsync(cancellationToken);
        var stats = await reader.GetStatsAsync(project.StorageKey, project.DefaultBranch, cancellationToken);
        return new(ToSummary(project, stats.FileCount, stats.FolderCount), null);
    }

    public async Task<ProjectResult> DeleteAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null)
            return new(null, ProjectFailure.NotFound);
        var authorization = await AuthorizeWorkspaceAsync(project.WorkspaceId, actor, true, cancellationToken);
        if (authorization.Failure is not null)
            return new(null, authorization.Failure);
        if (project.ConcurrencyStamp != expectedVersion)
            return new(null, ProjectFailure.Conflict);
        project.Discard();
        await db.SaveChangesAsync(cancellationToken);
        return new(ToSummary(project, 0, 0), null);
    }

    public async Task<ProjectResult> PromoteFolderAsync(PromoteProjectCommand command,
        CancellationToken cancellationToken)
    {
        var source = await db.Projects.SingleOrDefaultAsync(project =>
            project.Id == command.SourceProjectId &&
            project.WorkspaceId == command.WorkspaceId &&
            project.Status != ProjectStatus.Discarded, cancellationToken);
        if (source is null)
            return new(null, ProjectFailure.NotFound);
        var authorization = await AuthorizeWorkspaceAsync(command.WorkspaceId, command.Actor, true, cancellationToken);
        if (!authorization.Succeeded)
            return new(null, authorization.Failure);

        var folderName = command.FolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(folderName) ||
            !UserWorkspaceSettings.IsValidDirectory(command.FolderPath))
            return new(null, ProjectFailure.InvalidInput);

        Project project;
        try
        {
            project = Project.Create(command.WorkspaceId, command.Actor.UserId,
                folderName, $"project-{folderName.ToLowerInvariant().Replace(' ', '-')}",
                $"由项目文件夹升级：/{command.FolderPath}");
        }
        catch (ArgumentException)
        {
            return new(null, ProjectFailure.InvalidInput);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await provisioner.InitializeAsync(project.StorageKey, project.DefaultBranch, cancellationToken,
                ["docs"]);
            var targetHead = await reader.CopyFolderAsync(
                source.StorageKey, source.DefaultBranch, command.FolderPath,
                project.StorageKey, project.DefaultBranch, "",
                project.Id, command.Actor, cancellationToken);
            if (targetHead is null)
                throw new InvalidOperationException("Folder copy failed.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new(null, ProjectFailure.Conflict);
        }
        catch (Exception exception) when (exception is WorkspaceProvisioningException or InvalidOperationException)
        {
            db.Projects.Remove(project);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new(null, ProjectFailure.ProvisioningFailed);
        }
        await transaction.CommitAsync(cancellationToken);
        var stats = await reader.GetStatsAsync(project.StorageKey, project.DefaultBranch, cancellationToken);
        return new(ToSummary(project, stats.FileCount, stats.FolderCount), null);
    }

    public async Task<ProjectResult> DemoteAsync(DemoteProjectCommand command,
        CancellationToken cancellationToken)
    {
        var source = await db.Projects.SingleOrDefaultAsync(project =>
            project.Id == command.SourceProjectId &&
            project.WorkspaceId == command.WorkspaceId &&
            project.Status != ProjectStatus.Discarded, cancellationToken);
        if (source is null)
            return new(null, ProjectFailure.NotFound);
        var authorization = await AuthorizeWorkspaceAsync(command.WorkspaceId, command.Actor, true, cancellationToken);
        if (!authorization.Succeeded)
            return new(null, authorization.Failure);

        var target = await db.Projects
            .Where(project => project.WorkspaceId == command.WorkspaceId &&
                              project.Status != ProjectStatus.Discarded)
            .OrderBy(project => project.Id == command.WorkspaceId ? 0 : 1)
            .ThenBy(project => project.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null)
            return new(null, ProjectFailure.NotFound);
        if (target.Id == source.Id)
            return new(null, ProjectFailure.InvalidInput);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var targetHead = await reader.CopyFolderAsync(
            source.StorageKey, source.DefaultBranch, "",
            target.StorageKey, target.DefaultBranch, source.Slug,
            target.Id, command.Actor, cancellationToken);
        if (targetHead is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(null, ProjectFailure.ProvisioningFailed);
        }
        source.Archive();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var stats = await reader.GetStatsAsync(target.StorageKey, target.DefaultBranch, cancellationToken);
        return new(ToSummary(target, stats.FileCount, stats.FolderCount), null);
    }

    private async Task<(bool Succeeded, ProjectFailure? Failure)> AuthorizeWorkspaceAsync(
        Guid workspaceId, WorkspaceActor actor, bool requireEditor, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == workspaceId, cancellationToken);
        if (workspace is null)
            return (false, ProjectFailure.NotFound);
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.UserId, cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return (false, ProjectFailure.Unauthorized);
        if (actor.IsSystemAdministrator)
            return (true, null);
        var role = await db.WorkspaceMembers.AsNoTracking()
            .Where(member => member.WorkspaceId == workspaceId &&
                             member.UserId == actor.UserId &&
                             member.Status == WorkspaceMemberStatus.Active)
            .Select(member => (WorkspaceRole?)member.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (role is null)
            return (false, ProjectFailure.Unauthorized);
        if (requireEditor && role != WorkspaceRole.Owner && role != WorkspaceRole.Editor)
            return (false, ProjectFailure.Unauthorized);
        return (true, null);
    }

    private static ProjectSummary ToSummary(Project project, int fileCount, int folderCount) =>
        new(project.Id, project.WorkspaceId, project.Name, project.Slug, project.Description,
            project.Status, project.DefaultBranch, project.ConcurrencyStamp,
            fileCount, folderCount, project.CreatedAt, project.UpdatedAt);
}
