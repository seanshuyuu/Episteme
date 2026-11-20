using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Git;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Episteme.IntegrationTests;

public sealed class McpContentAuthorizationTests
{
    [PostgresFact]
    public async Task Content_uses_workspace_policy_for_project_ids_and_distinguishes_create_from_update()
    {
        var root = Path.Combine(Path.GetTempPath(), $"episteme-mcp-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkspaceStorageRoot"] = root }).Build();
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-content-{Guid.NewGuid():N}" };
        var editor = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-content-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "MCP", $"mcp-content-{Guid.NewGuid():N}", WorkspaceType.Code);
        var project = Project.Create(workspace.Id, owner.Id, "Project", "project", null);
        db.Users.AddRange(owner, editor);
        db.Workspaces.Add(workspace);
        db.Projects.Add(project);
        db.WorkspaceMembers.AddRange(WorkspaceMember.CreateOwner(workspace.Id, owner.Id),
            WorkspaceMember.Add(workspace.Id, editor.Id, WorkspaceRole.Editor, owner.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ownerActor = Actor(owner.Id);
        var editorActor = Actor(editor.Id);
        var ownerMcp = ownerActor with { IsMcp = true };
        var editorMcp = editorActor with { IsMcp = true };
        try
        {
            await new GitWorkspaceProvisioner(configuration).InitializeAsync(project.StorageKey, project.DefaultBranch, default);
            var settings = new EfMcpSettingsRepository(db);
            var content = new EfWorkspaceContentRepository(db, new GitWorkspaceReader(configuration));
            Assert.Equal(WorkspaceContentFailure.Unauthorized,
                (await content.ListAsync(project.Id, ownerMcp, "", default)).Failure);
            Assert.Null((await settings.SetPersonalAsync(ownerActor, new(true, false), default)).Failure);
            Assert.Null((await settings.SetWorkspaceEnabledAsync(ownerActor, workspace.Id, true, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, workspace.Id,
                McpPermission.ReadDocument | McpPermission.CreateDocument, default)).Failure);
            Assert.Equal(WorkspaceAccessFailure.MembershipRequired,
                (await content.ExportWithDirectoriesAsync(project.Id, ownerMcp, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, workspace.Id,
                McpPermission.ReadWorkspace | McpPermission.ReadProject | McpPermission.ReadDocument | McpPermission.CreateDocument, default)).Failure);
            Assert.Null((await content.ExportWithDirectoriesAsync(project.Id, ownerMcp, default)).Failure);
            var head = (await content.ListAsync(project.Id, ownerMcp, "", default)).CommitId!;
            var created = await content.WriteAsync(new(project.Id, ownerMcp, "note.md", head,
                WorkspaceWriteOperation.Put, "first"), default);
            Assert.Null(created.Failure);
            Assert.Equal(WorkspaceContentFailure.Unauthorized,
                (await content.WriteAsync(new(project.Id, ownerMcp, "note.md", created.CommitId!,
                    WorkspaceWriteOperation.Put, "denied"), default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, workspace.Id,
                McpPermission.ReadDocument | McpPermission.UpdateDocument, default)).Failure);
            Assert.Equal(WorkspaceContentFailure.Unauthorized,
                (await content.WriteAsync(new(project.Id, ownerMcp, "other.md", created.CommitId!,
                    WorkspaceWriteOperation.Put, "denied"), default)).Failure);
            var updated = await content.WriteAsync(new(project.Id, ownerMcp, "note.md", created.CommitId!,
                WorkspaceWriteOperation.Put, "second"), default);
            Assert.Null(updated.Failure);

            Assert.Null((await settings.SetPersonalAsync(editorActor, new(true, false), default)).Failure);
            Assert.Null((await settings.SetMemberGrantAsync(ownerActor, workspace.Id, editor.Id,
                McpPermission.ReadDocument, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(editorActor, workspace.Id, McpPermission.ReadDocument, default)).Failure);
            Assert.Equal("second", (await content.ReadAsync(project.Id, editorMcp, "note.md", default)).Content);
            Assert.Equal(WorkspaceContentFailure.Unauthorized,
                (await content.WriteAsync(new(project.Id, editorMcp with { IsSystemAdministrator = true }, "note.md", updated.CommitId!,
                    WorkspaceWriteOperation.Put, "denied"), default)).Failure);
            Assert.Null((await settings.SetMemberGrantAsync(ownerActor, workspace.Id, editor.Id, McpPermission.None, default)).Failure);
            Assert.Equal(WorkspaceContentFailure.Unauthorized,
                (await content.ReadAsync(project.Id, editorMcp, "note.md", default)).Failure);
            Assert.Null((await settings.SetPersonalAsync(ownerActor, new(false, false), default)).Failure);
            Assert.Equal(WorkspaceContentFailure.Unauthorized,
                (await content.ReadAsync(project.Id, ownerMcp, "note.md", default)).Failure);
            Assert.Equal("second", (await content.ReadAsync(project.Id, ownerActor, "note.md", default)).Content);
            Assert.Null((await settings.SetPersonalAsync(ownerActor, new(true, false), default)).Failure);
            Assert.Null((await settings.SetWorkspaceEnabledAsync(ownerActor, workspace.Id, false, default)).Failure);
            Assert.Equal(WorkspaceContentFailure.Unauthorized,
                (await content.ReadAsync(project.Id, ownerMcp, "note.md", default)).Failure);
        }
        finally
        {
            await db.AccountAuditEvents.Where(a => a.TargetUserId == owner.Id || a.TargetUserId == editor.Id).ExecuteDeleteAsync();
            await db.WorkspaceAuditEvents.Where(a => a.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceUserSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpUserSettings.Where(s => s.UserId == owner.Id || s.UserId == editor.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == editor.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    private static WorkspaceActor Actor(Guid userId) =>
        new(userId, new HashSet<string>(StringComparer.Ordinal)
            { "tokens.manage", "workspaces.manage", "workspaces.read", "workspaces.write" }, false);
}
