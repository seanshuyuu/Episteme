using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Episteme.IntegrationTests;

public sealed class McpWorkspaceAuthorizationTests
{
    [PostgresFact]
    public async Task Workspace_listing_filters_permissions_before_pagination_even_for_administrators()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-list-{Guid.NewGuid():N}" };
        var prefix = $"mcp-{Guid.NewGuid():N}";
        var workspaces = Enumerable.Range(0, 3)
            .Select(i => Workspace.Create(owner.Id, $"Workspace {i}", $"{prefix}-{i}", WorkspaceType.Code)).ToArray();
        db.Users.Add(owner);
        db.Workspaces.AddRange(workspaces);
        db.WorkspaceMembers.AddRange(workspaces.Select(w => WorkspaceMember.CreateOwner(w.Id, owner.Id)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var actor = new WorkspaceActor(owner.Id, new HashSet<string> { "tokens.manage", "workspaces.manage", "workspaces.read" }, true);
        try
        {
            var settings = new EfMcpSettingsRepository(db);
            Assert.Null((await settings.SetPersonalAsync(actor, new(true, false), default)).Failure);
            foreach (var workspace in workspaces)
                Assert.Null((await settings.SetWorkspaceEnabledAsync(actor, workspace.Id, true, default)).Failure);
            foreach (var workspace in workspaces.Skip(1))
                Assert.Null((await settings.SetWorkspaceAsync(actor, workspace.Id, McpPermission.ReadWorkspace, default)).Failure);

            var listing = new WorkspaceListingService(new EfWorkspaceListingRepository(db));
            var first = await listing.ListAsync(new(owner.Id, actor.Scopes, true, 0, 1, IsMcp: true), default);
            var second = await listing.ListAsync(new(owner.Id, actor.Scopes, true, 1, 1, IsMcp: true), default);
            Assert.Null(first.Failure);
            Assert.True(first.HasMore);
            Assert.Equal(workspaces[1].Id, Assert.Single(first.Items!).Id);
            Assert.False(second.HasMore);
            Assert.Equal(workspaces[2].Id, Assert.Single(second.Items!).Id);
            var access = new WorkspaceAccessService(new EfWorkspaceAccessRepository(db));
            Assert.False((await access.AuthorizeAsync(new(workspaces[0].Id, owner.Id, actor.Scopes,
                true, WorkspacePermission.Read, IsMcp: true))).Succeeded);
            Assert.True((await access.AuthorizeAsync(new(workspaces[1].Id, owner.Id, actor.Scopes,
                true, WorkspacePermission.Read, IsMcp: true))).Succeeded);
            Assert.Null((await settings.SetPersonalAsync(actor, new(false, false), default)).Failure);
            Assert.Empty((await listing.ListAsync(new(owner.Id, actor.Scopes, true, 0, 10, IsMcp: true), default)).Items!);
            Assert.False((await access.AuthorizeAsync(new(workspaces[1].Id, owner.Id, actor.Scopes,
                true, WorkspacePermission.Read, IsMcp: true))).Succeeded);
        }
        finally
        {
            var ids = workspaces.Select(w => w.Id).ToArray();
            await db.AccountAuditEvents.Where(a => a.TargetUserId == owner.Id).ExecuteDeleteAsync();
            await db.WorkspaceAuditEvents.Where(a => ids.Contains(a.WorkspaceId)).ExecuteDeleteAsync();
            await db.McpWorkspaceUserSettings.Where(s => ids.Contains(s.WorkspaceId)).ExecuteDeleteAsync();
            await db.McpWorkspaceSettings.Where(s => ids.Contains(s.WorkspaceId)).ExecuteDeleteAsync();
            await db.McpUserSettings.Where(s => s.UserId == owner.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => ids.Contains(m.WorkspaceId)).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => ids.Contains(w.Id)).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
        }
    }

    [PostgresFact]
    public async Task Workspace_and_member_changes_require_their_own_live_mcp_permissions()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-owner-{Guid.NewGuid():N}" };
        var member = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-member-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "Initial", $"mcp-workspace-{Guid.NewGuid():N}", WorkspaceType.Code);
        db.Users.AddRange(owner, member);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(workspace.Id, owner.Id));
        await db.SaveChangesAsync();
        var version = workspace.ConcurrencyStamp;
        db.ChangeTracker.Clear();
        var actor = new WorkspaceActor(owner.Id, new HashSet<string> { "tokens.manage", "workspaces.manage", "workspaces.read" }, false);
        var mcp = actor with { IsMcp = true, IsSystemAdministrator = true };
        try
        {
            var management = new EfWorkspaceManagementRepository(db);
            var members = new EfWorkspaceMemberRepository(db);
            var settings = new EfMcpSettingsRepository(db);
            var baselineAccess = new WorkspaceAccessService(new EfWorkspaceAccessRepository(db));
            Assert.False((await baselineAccess.AuthorizeAsync(new(workspace.Id, member.Id, actor.Scopes,
                false, WorkspacePermission.Read))).Succeeded);
            Assert.Equal(WorkspaceManagementFailure.Unauthorized,
                (await management.UpdateAsync(new(workspace.Id, mcp, version, "Denied", WorkspaceType.Code, null), default)).Failure);
            Assert.Null((await settings.SetPersonalAsync(actor, new(true, false), default)).Failure);
            Assert.Null((await settings.SetWorkspaceEnabledAsync(actor, workspace.Id, true, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(actor, workspace.Id, McpPermission.UpdateWorkspace, default)).Failure);
            var updated = await management.UpdateAsync(new(workspace.Id, mcp, version, "Updated", WorkspaceType.Code, null), default);
            Assert.Null(updated.Failure);
            Assert.Equal(WorkspaceManagementFailure.Unauthorized,
                (await management.UpdateAsync(new(workspace.Id, mcp with { UserId = member.Id },
                    updated.Workspace!.ConcurrencyStamp, "Denied", WorkspaceType.Code, null), default)).Failure);
            Assert.Equal(WorkspaceManagementFailure.Unauthorized,
                (await management.ArchiveAsync(workspace.Id, mcp, updated.Workspace!.ConcurrencyStamp, default)).Failure);
            Assert.Null((await members.SetAsync(workspace.Id, member.Id, WorkspaceRole.Reader, mcp, default)).Failure);
            Assert.Equal(WorkspaceManagementFailure.Unauthorized,
                (await members.ListAsync(workspace.Id, mcp, 0, 10, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(actor, workspace.Id, McpPermission.ReadWorkspace, default)).Failure);
            Assert.Null((await members.ListAsync(workspace.Id, mcp, 0, 10, default)).Failure);
            var memberActor = new WorkspaceActor(member.Id, actor.Scopes, false);
            Assert.Null((await settings.SetPersonalAsync(memberActor, new(true, false), default)).Failure);
            Assert.Null((await settings.SetMemberGrantAsync(actor, workspace.Id, member.Id, McpPermission.ReadWorkspace, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(memberActor, workspace.Id, McpPermission.ReadWorkspace, default)).Failure);
            var listing = new WorkspaceListingService(new EfWorkspaceListingRepository(db));
            var access = new WorkspaceAccessService(new EfWorkspaceAccessRepository(db));
            Assert.Single((await listing.ListAsync(new(member.Id, actor.Scopes, false, 0, 10, IsMcp: true), default)).Items!);
            Assert.True((await access.AuthorizeAsync(new(workspace.Id, member.Id, actor.Scopes, false,
                WorkspacePermission.Read, IsMcp: true))).Succeeded);
            Assert.Null((await settings.SetMemberGrantAsync(actor, workspace.Id, member.Id, McpPermission.None, default)).Failure);
            Assert.Empty((await listing.ListAsync(new(member.Id, actor.Scopes, false, 0, 10, IsMcp: true), default)).Items!);
            Assert.False((await access.AuthorizeAsync(new(workspace.Id, member.Id, actor.Scopes, false,
                WorkspacePermission.Read, IsMcp: true))).Succeeded);
            Assert.Equal(WorkspaceManagementFailure.Unauthorized,
                (await members.RemoveAsync(workspace.Id, member.Id, mcp, default)).Failure);
            Assert.Null((await settings.SetPersonalAsync(actor, new(false, false), default)).Failure);
            Assert.Equal(WorkspaceManagementFailure.Unauthorized,
                (await members.ListAsync(workspace.Id, mcp, 0, 10, default)).Failure);
        }
        finally
        {
            await db.AccountAuditEvents.Where(a => a.TargetUserId == owner.Id || a.TargetUserId == member.Id).ExecuteDeleteAsync();
            await db.WorkspaceAuditEvents.Where(a => a.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceUserSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpUserSettings.Where(s => s.UserId == owner.Id || s.UserId == member.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == member.Id).ExecuteDeleteAsync();
        }
    }
}
