using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;

namespace Episteme.Application.Tests;

public sealed class McpAccessPolicyTests
{
    private const McpPermission AllDocumentAccess =
        McpPermission.ReadDocument | McpPermission.CreateDocument |
        McpPermission.UpdateDocument | McpPermission.DeleteDocument;

    [Fact]
    public void Workspace_creation_requires_personal_switch_and_explicit_permission()
    {
        Assert.False(McpAccessPolicy.CanCreateWorkspace(false, McpPermission.CreateWorkspace));
        Assert.False(McpAccessPolicy.CanCreateWorkspace(true, McpPermission.None));
        Assert.True(McpAccessPolicy.CanCreateWorkspace(true, McpPermission.CreateWorkspace));
    }

    [Fact]
    public void Owner_access_requires_both_switches_and_selected_permission()
    {
        var access = new McpWorkspaceAccess(true, WorkspaceRole.Owner, true, AllDocumentAccess, null);
        Assert.True(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.UpdateDocument));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(false, access, McpPermission.UpdateDocument));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access with { Enabled = false }, McpPermission.UpdateDocument));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access with { MembershipActive = false }, McpPermission.UpdateDocument));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.DeleteProject));
    }

    [Fact]
    public void Shared_editor_needs_explicit_owner_grant_and_personal_permission()
    {
        var access = new McpWorkspaceAccess(true, WorkspaceRole.Editor, true, AllDocumentAccess, null);
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.ReadDocument));
        access = access with { SharedMemberPermissions = McpPermission.ReadDocument };
        Assert.True(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.ReadDocument));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.UpdateDocument));
        access = access with { PersonalPermissions = McpPermission.None };
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.ReadDocument));
    }

    [Fact]
    public void Shared_grant_cannot_exceed_current_member_role()
    {
        var access = new McpWorkspaceAccess(true, WorkspaceRole.Reader, true, AllDocumentAccess, AllDocumentAccess);
        Assert.True(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.ReadDocument));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.DeleteDocument));
    }

    [Fact]
    public void Shared_editor_can_manage_projects_when_both_sides_allow_it()
    {
        var access = new McpWorkspaceAccess(true, WorkspaceRole.Editor, true,
            McpPermission.CreateProject, McpPermission.CreateProject);
        Assert.True(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.CreateProject));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.UpdateProject));
    }

    [Fact]
    public void Missing_membership_and_unknown_permissions_are_denied()
    {
        var access = new McpWorkspaceAccess(true, null, true, AllDocumentAccess, AllDocumentAccess);
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, null, McpPermission.ReadDocument));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.ReadDocument));
        access = access with { MemberRole = WorkspaceRole.Owner };
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.None));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, (McpPermission)(1 << 20)));
        Assert.False(McpAccessPolicy.CanAccessWorkspace(true, access, McpPermission.CreateWorkspace));
    }
}
