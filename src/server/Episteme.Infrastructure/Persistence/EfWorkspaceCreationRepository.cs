using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Logging;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfWorkspaceCreationRepository(
    EpistemeDbContext db,
    IWorkspaceRepositoryProvisioner provisioner,
    ILogger<EfWorkspaceCreationRepository> logger) : IWorkspaceCreationRepository
{
    public async Task<WorkspaceCreationResult> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken)
    {
        var workspace = Workspace.BeginProvisioning(
            command.Actor.UserId, command.Name, command.Slug, command.Type, command.Description);
        if (command.Configuration is not null)
            workspace.SetConfiguration(command.Configuration);
        if (command.Directories is not null)
            workspace.SetProvisioningDirectories(command.Directories);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var user = await db.Users
                .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {command.Actor.UserId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (user is null || user.Status != UserAccountStatus.Active)
                return new(null, WorkspaceCreationFailure.Unauthorized);

            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(workspace.Id, user.Id));
            db.WorkspaceAuditEvents.Add(new WorkspaceAuditEvent(workspace.Id, user.Id, "workspace.provisioning"));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Workspaces_Slug" })
            {
                return new(null, WorkspaceCreationFailure.Conflict);
            }
        }

        // The committed reservation may outlive this request. Reload state under the
        // provisioning locks rather than reusing tracked user/workspace snapshots.
        db.ChangeTracker.Clear();
        return await CompleteAsync(workspace.Id, command.Actor, cancellationToken);
    }

    public Task<WorkspaceCreationResult> RetryAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken) =>
        CompleteAsync(workspaceId, actor, cancellationToken);

    public async Task<IReadOnlyList<WorkspaceSummary>?> ListPendingAsync(
        Guid userId, int offset, int count, CancellationToken cancellationToken)
    {
        var result = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.Status == UserAccountStatus.Active)
            .Select(user => new
            {
                Items = db.Workspaces.AsNoTracking()
                    .Where(workspace => workspace.Status == WorkspaceStatus.Provisioning &&
                        db.WorkspaceMembers.Any(member => member.WorkspaceId == workspace.Id &&
                            member.UserId == user.Id && member.Status == WorkspaceMemberStatus.Active &&
                            member.Role == WorkspaceRole.Owner))
                    .OrderBy(workspace => workspace.Slug).ThenBy(workspace => workspace.Id)
                    .Skip(offset).Take(count)
                    .Select(workspace => new WorkspaceSummary(workspace.Id, workspace.Name, workspace.Slug,
                        workspace.Type, workspace.Description, workspace.DefaultBranch, workspace.ConcurrencyStamp))
                    .ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        return result?.Items;
    }

    private async Task<WorkspaceCreationResult> CompleteAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Provisioning remains invisible until Git succeeds; the row lock serializes retries
        // across service instances and also protects recovery after a process crash.
        var workspace = await db.Workspaces
            .FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.Status != WorkspaceStatus.Provisioning)
            return new(null, WorkspaceCreationFailure.NotFound);
        var user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {actor.UserId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
            return new(null, WorkspaceCreationFailure.Unauthorized);
        if (!actor.IsSystemAdministrator && !await db.WorkspaceMembers.AnyAsync(member =>
                member.WorkspaceId == workspaceId && member.UserId == actor.UserId &&
                member.Status == WorkspaceMemberStatus.Active && member.Role == WorkspaceRole.Owner, cancellationToken))
            return new(null, WorkspaceCreationFailure.Unauthorized);

        try
        {
            var directories = workspace.ProvisioningDirectoriesJson is null ? null :
                System.Text.Json.JsonSerializer.Deserialize<string[]>(workspace.ProvisioningDirectoriesJson)
                ?? throw new InvalidOperationException("Invalid persisted directory skeleton.");
            await provisioner.InitializeAsync(workspace.StorageKey, workspace.DefaultBranch, cancellationToken, directories);
        }
        catch (WorkspaceProvisioningException exception)
        {
            logger.LogError(exception, "Workspace {WorkspaceId} provisioning failed", workspaceId);
            return new(null, WorkspaceCreationFailure.ProvisioningFailed, workspaceId);
        }
        db.Workspaces.Attach(workspace);
        workspace.CompleteProvisioning();
        db.WorkspaceAuditEvents.Add(new WorkspaceAuditEvent(workspace.Id, user.Id, "workspace.created"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new WorkspaceSummary(workspace.Id, workspace.Name, workspace.Slug, workspace.Type,
            workspace.Description, workspace.DefaultBranch, workspace.ConcurrencyStamp), null);
    }
}
