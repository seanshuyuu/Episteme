using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Episteme.IntegrationTests;

public sealed class McpSettingsRepositoryTests
{
    [PostgresFact]
    public async Task Shared_member_requires_owner_grant_and_grant_is_cleared_on_removal()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!)
            .UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-owner-{Guid.NewGuid():N}" };
        var reader = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-reader-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "MCP", $"mcp-settings-{Guid.NewGuid():N}", WorkspaceType.Code);
        db.Users.AddRange(owner, reader);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(WorkspaceMember.CreateOwner(workspace.Id, owner.Id),
            WorkspaceMember.Add(workspace.Id, reader.Id, WorkspaceRole.Reader, owner.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ownerActor = Actor(owner.Id);
        var readerActor = Actor(reader.Id);
        try
        {
            var repository = new EfMcpSettingsRepository(db);
            Assert.False((await repository.GetPersonalAsync(ownerActor, default)).Value!.Enabled);
            Assert.False((await repository.GetWorkspaceAsync(ownerActor, workspace.Id, default)).Value!.Enabled);
            Assert.Equal(McpSettingsFailure.Unauthorized,
                (await repository.SetWorkspaceAsync(readerActor, workspace.Id, McpPermission.ReadDocument, default)).Failure);
            Assert.Equal(McpSettingsFailure.Unauthorized,
                (await repository.SetWorkspaceEnabledAsync(readerActor, workspace.Id, true, default)).Failure);
            Assert.Equal(McpSettingsFailure.InvalidInput,
                (await repository.SetMemberGrantAsync(ownerActor, workspace.Id, reader.Id, McpPermission.DeleteDocument, default)).Failure);
            Assert.Equal(McpPermission.None, (await repository.GetMemberGrantAsync(ownerActor, workspace.Id, reader.Id, default)).Value);
            Assert.Equal(McpSettingsFailure.Unauthorized,
                (await repository.GetMemberGrantAsync(readerActor, workspace.Id, owner.Id, default)).Failure);
            Assert.Null((await repository.SetWorkspaceEnabledAsync(ownerActor, workspace.Id, true, default)).Failure);
            Assert.Null((await repository.SetMemberGrantAsync(ownerActor, workspace.Id, reader.Id, McpPermission.ReadDocument, default)).Failure);
            Assert.Equal(McpPermission.ReadDocument,
                (await repository.GetMemberGrantAsync(ownerActor, workspace.Id, reader.Id, default)).Value);
            Assert.Null((await repository.SetWorkspaceAsync(readerActor, workspace.Id, McpPermission.ReadDocument, default)).Failure);
            db.ChangeTracker.Clear();

            var members = new EfWorkspaceMemberRepository(db);
            Assert.Null((await members.RemoveAsync(workspace.Id, reader.Id, ownerActor, default)).Failure);
            Assert.Null((await members.SetAsync(workspace.Id, reader.Id, WorkspaceRole.Reader, ownerActor, default)).Failure);
            db.ChangeTracker.Clear();
            var view = (await repository.GetWorkspaceAsync(readerActor, workspace.Id, default)).Value!;
            Assert.Equal(McpPermission.None, view.GrantedPermissions);
            Assert.False(McpAccessPolicy.CanAccessWorkspace(true,
                new(view.Enabled, WorkspaceRole.Reader, true, view.PersonalPermissions, view.GrantedPermissions),
                McpPermission.ReadDocument));
            await db.Users.Where(u => u.Id == owner.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.Status, UserAccountStatus.Disabled));
            Assert.Equal(McpSettingsFailure.Unauthorized,
                (await repository.SetWorkspaceEnabledAsync(ownerActor, workspace.Id, false, default)).Failure);
            Assert.Equal(McpSettingsFailure.Unauthorized,
                (await repository.SetMemberGrantAsync(ownerActor, workspace.Id, reader.Id, McpPermission.ReadDocument, default)).Failure);
        }
        finally
        {
            await db.AccountAuditEvents.Where(a => a.TargetUserId == owner.Id || a.TargetUserId == reader.Id).ExecuteDeleteAsync();
            await db.WorkspaceAuditEvents.Where(a => a.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceUserSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpUserSettings.Where(s => s.UserId == owner.Id || s.UserId == reader.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == reader.Id).ExecuteDeleteAsync();
        }
    }

    private static WorkspaceActor Actor(Guid userId) =>
        new(userId, new HashSet<string>(StringComparer.Ordinal) { "tokens.manage", "workspaces.manage" }, false);
}
