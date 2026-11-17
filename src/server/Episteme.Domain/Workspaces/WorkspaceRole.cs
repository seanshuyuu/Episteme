namespace Episteme.Domain.Workspaces;

public enum WorkspaceRole
{
    Owner,
    Editor,
    Reader
}

public enum WorkspaceMemberStatus
{
    Active,
    Removed
}

public enum WorkspaceStatus
{
    Active,
    Archived,
    Provisioning
}

public enum WorkspaceType
{
    Code,
    Skill,
    Research,
    BusinessNovel,
    Custom
}
