using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Episteme.IntegrationTests;

public sealed class WorkspaceListingRepositoryTests
{
    [PostgresFact]
    public async Task Listing_enforces_membership_active_user_and_workspace_status_with_pagination()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!)
            .UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"list-{Guid.NewGuid():N}" };
        var other = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"list-{Guid.NewGuid():N}" };
        var prefix = Guid.NewGuid().ToString("N");
        var first = Workspace.Create(user.Id, "first", $"{prefix}-a", WorkspaceType.Research);
        var second = Workspace.Create(user.Id, "second", $"{prefix}-b", WorkspaceType.Skill);
        var archived = Workspace.Create(user.Id, "archived", $"{prefix}-c", WorkspaceType.Code);
        var removed = Workspace.Create(user.Id, "removed", $"{prefix}-d", WorkspaceType.Code);
        var hidden = Workspace.Create(other.Id, "hidden", $"{prefix}-e", WorkspaceType.Code);
        archived.Archive();
        db.Users.AddRange(user, other);
        db.Workspaces.AddRange(first, second, archived, removed, hidden);
        db.WorkspaceMembers.AddRange(
            WorkspaceMember.CreateOwner(first.Id, user.Id),
            WorkspaceMember.Add(second.Id, user.Id, WorkspaceRole.Reader, other.Id),
            WorkspaceMember.CreateOwner(archived.Id, user.Id),
            WorkspaceMember.Add(removed.Id, user.Id, WorkspaceRole.Reader, other.Id));
        await db.SaveChangesAsync();

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"WorkspaceMembers\" SET \"Status\" = 'Removed' WHERE \"WorkspaceId\" = {removed.Id} AND \"UserId\" = {user.Id}");
            var repository = new EfWorkspaceListingRepository(db);
            var page = await repository.ListForActiveUserAsync(user.Id, false, 0, 1, default);
            var next = await repository.ListForActiveUserAsync(user.Id, false, 1, 1, default);
            var empty = await repository.ListForActiveUserAsync(user.Id, false, 2, 1, default);
            Assert.Equal(first.Id, Assert.Single(page!).Id);
            Assert.Equal(second.Id, Assert.Single(next!).Id);
            Assert.Empty(empty!);

            var administrator = await repository.ListForActiveUserAsync(user.Id, true, 0, 100, default);
            var ids = administrator!.Select(item => item.Id).ToHashSet();
            Assert.Contains(hidden.Id, ids);
            Assert.Contains(removed.Id, ids);
            Assert.DoesNotContain(archived.Id, ids);

            user.Status = UserAccountStatus.Disabled;
            await db.SaveChangesAsync();
            Assert.Null(await repository.ListForActiveUserAsync(user.Id, true, 0, 10, default));
            Assert.Null(await repository.ListForActiveUserAsync(Guid.NewGuid(), false, 0, 10, default));
        }
        finally
        {
            var workspaceIds = new[] { first.Id, second.Id, archived.Id, removed.Id, hidden.Id };
            await db.WorkspaceMembers.Where(member => workspaceIds.Contains(member.WorkspaceId)).ExecuteDeleteAsync();
            await db.Workspaces.Where(workspace => workspaceIds.Contains(workspace.Id)).ExecuteDeleteAsync();
            await db.Users.Where(account => account.Id == user.Id || account.Id == other.Id).ExecuteDeleteAsync();
        }
    }
}
