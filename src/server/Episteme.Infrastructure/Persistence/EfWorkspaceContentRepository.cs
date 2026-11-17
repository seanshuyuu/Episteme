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
        var (workspace, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, failure == WorkspaceContentFailure.NotFound
                ? WorkspaceAccessFailure.NotFound : WorkspaceAccessFailure.MembershipRequired);
        var (directories, error) = await reader.GetSkeletonAsync(workspace!.StorageKey, workspace.DefaultBranch, cancellationToken);
        if (error is not null)
            return new(null, error);
        var manifest = new WorkspaceManifest(3, workspace.Name, workspace.Type, workspace.Description,
            WorkspaceConfiguration.Parse(workspace.ConfigurationJson), directories);
        await transaction.CommitAsync(cancellationToken);
        return new(manifest, null);
    }

    public async Task<WorkspaceDirectoryResult> ListAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, null, failure);
        var result = await reader.ListAsync(workspace!.StorageKey, workspace.DefaultBranch, path, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceDocumentResult> ReadAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, failure) = await AuthorizeAsync(workspaceId, actor, false, cancellationToken);
        if (failure is not null)
            return new(null, null, failure);
        var result = await reader.ReadAsync(workspace!.StorageKey, workspace.DefaultBranch, path, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceHistoryResult> HistoryAsync(
        Guid workspaceId, WorkspaceActor actor, string path, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, failure) = await AuthorizeAsync(workspaceId, actor, true, cancellationToken);
        if (failure is not null)
            return new(null, null, false, failure);
        var result = await reader.HistoryAsync(workspace!.StorageKey, workspace.DefaultBranch, path, offset, limit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceDocumentResult> ReadVersionAsync(
        Guid workspaceId, WorkspaceActor actor, string path, string commitId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, failure) = await AuthorizeAsync(workspaceId, actor, true, cancellationToken);
        if (failure is not null)
            return new(null, null, failure);
        var result = await reader.ReadVersionAsync(workspace!.StorageKey, workspace.DefaultBranch, path, commitId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WorkspaceWriteResult> WriteAsync(WorkspaceWriteCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (workspace, failure) = await AuthorizeAsync(command.WorkspaceId, command.Actor, true, cancellationToken);
        if (failure is not null)
            return new(null, failure);
        var result = await reader.WriteAsync(workspace!.StorageKey, workspace.DefaultBranch, command, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<(Workspace?, WorkspaceContentFailure?)> AuthorizeAsync(
        Guid workspaceId, WorkspaceActor actor, bool requireEditor, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces
            .FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Active)
            return (null, WorkspaceContentFailure.NotFound);
        var user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.UserId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return (null, WorkspaceContentFailure.Unauthorized);
        if (!actor.IsSystemAdministrator && !await db.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == workspaceId && m.UserId == actor.UserId && m.Status == WorkspaceMemberStatus.Active &&
                (!requireEditor || m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Editor),
                cancellationToken))
            return (null, WorkspaceContentFailure.Unauthorized);
        return (workspace, null);
    }
}
