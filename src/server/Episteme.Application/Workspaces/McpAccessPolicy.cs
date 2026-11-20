using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

[Flags]
public enum McpPermission
{
    None = 0,
    CreateWorkspace = 1 << 0,
    ReadWorkspace = 1 << 1,
    UpdateWorkspace = 1 << 2,
    ArchiveWorkspace = 1 << 3,
    CreateProject = 1 << 4,
    ReadProject = 1 << 5,
    UpdateProject = 1 << 6,
    DeleteProject = 1 << 7,
    CreateDocument = 1 << 8,
    ReadDocument = 1 << 9,
    UpdateDocument = 1 << 10,
    DeleteDocument = 1 << 11
}

public sealed record McpWorkspaceAccess(
    bool Enabled,
    WorkspaceRole? MemberRole,
    bool MembershipActive,
    McpPermission PersonalPermissions,
    McpPermission? SharedMemberPermissions);

public static class McpAccessPolicy
{
    private const McpPermission ReadPermissions =
        McpPermission.ReadWorkspace | McpPermission.ReadProject | McpPermission.ReadDocument;
    private const McpPermission DocumentWritePermissions =
        McpPermission.CreateDocument | McpPermission.UpdateDocument | McpPermission.DeleteDocument;
    private const McpPermission ProjectWritePermissions =
        McpPermission.CreateProject | McpPermission.UpdateProject | McpPermission.DeleteProject;
    private const McpPermission AllPermissions =
        McpPermission.CreateWorkspace | ReadPermissions |
        McpPermission.UpdateWorkspace | McpPermission.ArchiveWorkspace |
        ProjectWritePermissions | DocumentWritePermissions;

    public static bool CanCreateWorkspace(bool userEnabled, McpPermission permissions) =>
        userEnabled && (permissions & McpPermission.CreateWorkspace) != 0;

    public static bool IsWorkspacePermissionsValid(McpPermission permissions) =>
        (permissions & ~AllPermissions) == 0 && (permissions & McpPermission.CreateWorkspace) == 0;

    public static McpPermission PermissionsForRole(WorkspaceRole role) => role switch
    {
        WorkspaceRole.Owner => AllPermissions & ~McpPermission.CreateWorkspace,
        WorkspaceRole.Editor => ReadPermissions | ProjectWritePermissions | DocumentWritePermissions,
        WorkspaceRole.Reader => ReadPermissions,
        _ => McpPermission.None
    };

    public static bool CanAccessWorkspace(
        bool userEnabled, McpWorkspaceAccess? access, McpPermission required)
    {
        if (!userEnabled || access is not { Enabled: true, MembershipActive: true, MemberRole: not null } ||
            required == McpPermission.None || (required & ~AllPermissions) != 0 ||
            (required & McpPermission.CreateWorkspace) != 0)
            return false;

        var allowed = access.PersonalPermissions & PermissionsForRole(access.MemberRole.Value);
        if (access.MemberRole != WorkspaceRole.Owner)
            allowed &= access.SharedMemberPermissions ?? McpPermission.None;
        return (allowed & required) == required;
    }
}
