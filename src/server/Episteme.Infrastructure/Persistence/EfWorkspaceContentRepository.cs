using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Git;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfWorkspaceContentRepository(EpistemeDbContext db, GitWorkspaceReader reader)
    : IWorkspaceContentRepository, IWorkspaceManifestRepository
{
    public async Task<WorkspaceManifestExportResult> ExportWithDirectoriesAsync(
        Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, project, permissions, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, failure == WorkspaceContentFailure.NotFound
                ? WorkspaceAccessFailure.NotFound : WorkspaceAccessFailure.MembershipRequired);
        const McpPermission required = McpPermission.ReadWorkspace | McpPermission.ReadProject | McpPermission.ReadDocument;
        if (actor.IsMcp && (permissions & required) != required)
            return new(null, WorkspaceAccessFailure.MembershipRequired);
        var (directories, error) = await reader.GetSkeletonAsync(project!.StorageKey, project.DefaultBranch, cancellationToken);
        if (error is not null)
            return new(null, error);
        var manifest = new WorkspaceManifest(3, project.Name, workspace!.Type, project.Description,
            WorkspaceConfiguration.Parse(workspace.ConfigurationJson), directories);
        await transaction.CommitAsync(cancellationToken);
        return new(manifest, null);
    }

    public async Task<WorkspaceDirectoryResult> ListAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, project, permissions, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, null, failure);
        if (actor.IsMcp && (permissions & McpPermission.ReadDocument) == 0)
            return new(null, null, WorkspaceContentFailure.Unauthorized);
        var result = await reader.ListAsync(project!.StorageKey, project.DefaultBranch, path, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceDocumentResult> ReadAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, project, permissions, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, null, failure);
        if (actor.IsMcp && (permissions & McpPermission.ReadDocument) == 0)
            return new(null, null, WorkspaceContentFailure.Unauthorized);
        var result = await reader.ReadAsync(project!.StorageKey, project.DefaultBranch, path, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceBinaryDocumentResult> ReadBinaryAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, project, permissions, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, null, failure);
        if (actor.IsMcp && (permissions & McpPermission.ReadDocument) == 0)
            return new(null, null, WorkspaceContentFailure.Unauthorized);
        var result = await reader.ReadBinaryAsync(project!.StorageKey, project.DefaultBranch, path, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceHistoryResult> HistoryAsync(
        Guid workspaceId, WorkspaceActor actor, string path, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, project, permissions, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, null, false, failure);
        if (actor.IsMcp && (permissions & McpPermission.ReadDocument) == 0)
            return new(null, null, false, WorkspaceContentFailure.Unauthorized);
        var result = await reader.HistoryAsync(project!.StorageKey, project.DefaultBranch, path, offset, limit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceDocumentResult> ReadVersionAsync(
        Guid workspaceId, WorkspaceActor actor, string path, string commitId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, project, permissions, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, null, failure);
        if (actor.IsMcp && (permissions & McpPermission.ReadDocument) == 0)
            return new(null, null, WorkspaceContentFailure.Unauthorized);
        var result = await reader.ReadVersionAsync(project!.StorageKey, project.DefaultBranch, path, commitId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceWriteResult> WriteAsync(WorkspaceWriteCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, project, permissions, failure) = await AuthorizeAsync(command.WorkspaceId, command.Actor, true, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        if (command.Actor.IsMcp && permissions == McpPermission.None)
            return new(null, WorkspaceContentFailure.Unauthorized);
        var result = await reader.WriteAsync(project!.StorageKey, project.DefaultBranch, command, cancellationToken,
            command.Actor.IsMcp ? permissions : null);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<(Workspace?, Project?, McpPermission, WorkspaceContentFailure?)> AuthorizeAsync(
        Guid workspaceId, WorkspaceActor actor, bool requireEditor, CancellationToken cancellationToken)
    {
        Project? project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == workspaceId && item.Status != ProjectStatus.Discarded, cancellationToken);
        Guid resolvedWorkspaceId;
        if (project is not null)
        {
            resolvedWorkspaceId = project.WorkspaceId;
        }
        else
        {
            project = await db.Projects.AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId && item.Status != ProjectStatus.Discarded)
                .OrderBy(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            resolvedWorkspaceId = workspaceId;
        }
        var workspace = await db.Workspaces
            .FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {resolvedWorkspaceId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active || project is null)
            return (null, null, McpPermission.None, WorkspaceContentFailure.NotFound);
        var user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.UserId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return (null, null, McpPermission.None, WorkspaceContentFailure.Unauthorized);
        if ((!actor.IsSystemAdministrator || actor.IsMcp) && !await db.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == resolvedWorkspaceId && m.UserId == actor.UserId && m.Status == WorkspaceMemberStatus.Active &&
                (!requireEditor || m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Editor),
                cancellationToken))
            return (null, null, McpPermission.None, WorkspaceContentFailure.Unauthorized);
        var permissions = actor.IsMcp
            ? await EfMcpAuthorization.WorkspacePermissionsAsync(db, resolvedWorkspaceId, actor.UserId, cancellationToken)
            : McpPermission.None;
        return (workspace, project, permissions, null);
    }
}
