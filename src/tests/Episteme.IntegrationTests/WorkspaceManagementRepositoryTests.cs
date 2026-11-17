using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Episteme.IntegrationTests;

public sealed class WorkspaceManagementRepositoryTests
{
    [PostgresFact]
    public async Task Only_owner_can_change_metadata_and_archive_with_current_version()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!)
            .UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"owner-{Guid.NewGuid():N}" };
        var reader = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"reader-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "Initial", $"manage-{Guid.NewGuid():N}", WorkspaceType.Code);
        db.Users.AddRange(owner, reader);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(
            WorkspaceMember.CreateOwner(workspace.Id, owner.Id),
            WorkspaceMember.Add(workspace.Id, reader.Id, WorkspaceRole.Reader, owner.Id));
        await db.SaveChangesAsync();
        var originalVersion = workspace.ConcurrencyStamp;
        db.ChangeTracker.Clear();

        try
        {
            var repository = new EfWorkspaceManagementRepository(db);
            var ownerActor = Actor(owner.Id);
            var readerActor = Actor(reader.Id);
            var denied = await repository.UpdateAsync(
                new(workspace.Id, readerActor, originalVersion, "Denied", WorkspaceType.Skill, null), default);
            Assert.Equal(WorkspaceManagementFailure.Unauthorized, denied.Failure);
            db.ChangeTracker.Clear();

            var updated = await repository.UpdateAsync(
                new(workspace.Id, ownerActor, originalVersion, "Changed", WorkspaceType.Research, "notes"), default);
            Assert.Null(updated.Failure);
            Assert.NotEqual(originalVersion, updated.Workspace!.ConcurrencyStamp);
            db.ChangeTracker.Clear();

            var stale = await repository.ArchiveAsync(workspace.Id, ownerActor, originalVersion, default);
            Assert.Equal(WorkspaceManagementFailure.Conflict, stale.Failure);
            db.ChangeTracker.Clear();

            var archived = await repository.ArchiveAsync(
                workspace.Id, ownerActor, updated.Workspace.ConcurrencyStamp, default);
            Assert.Null(archived.Failure);
            db.ChangeTracker.Clear();
            Assert.Equal(WorkspaceStatus.Archived, (await db.Workspaces.SingleAsync(w => w.Id == workspace.Id)).Status);
            var events = await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == workspace.Id)
                .OrderBy(e => e.OccurredAt).Select(e => e.EventType).ToListAsync();
            Assert.Equal(new[] { "workspace.updated", "workspace.archived" }, events);
        }
        finally
        {
            await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == reader.Id).ExecuteDeleteAsync();
        }
    }

    [PostgresFact]
    public async Task Concurrent_updates_with_same_version_have_exactly_one_winner()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!)
            .UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"owner-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "Initial", $"race-{Guid.NewGuid():N}", WorkspaceType.Code);
        db.Users.Add(owner);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(workspace.Id, owner.Id));
        await db.SaveChangesAsync();
        var version = workspace.ConcurrencyStamp;
        db.ChangeTracker.Clear();

        try
        {
            await using var secondContext = new EpistemeDbContext(options);
            var first = new EfWorkspaceManagementRepository(db);
            var second = new EfWorkspaceManagementRepository(secondContext);
            var actor = Actor(owner.Id);
            var results = await Task.WhenAll(
                first.UpdateAsync(new(workspace.Id, actor, version, "One", WorkspaceType.Code, null), default),
                second.UpdateAsync(new(workspace.Id, actor, version, "Two", WorkspaceType.Code, null), default));

            Assert.Single(results, result => result.Failure is null);
            Assert.Single(results, result => result.Failure == WorkspaceManagementFailure.Conflict);
            Assert.Equal(1, await db.WorkspaceAuditEvents.CountAsync(e => e.WorkspaceId == workspace.Id));
        }
        finally
        {
            await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
        }
    }

    private static WorkspaceActor Actor(Guid userId) =>
        new(userId, new HashSet<string>(StringComparer.Ordinal) { "workspaces.manage" }, false);
}
