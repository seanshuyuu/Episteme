namespace Episteme.Infrastructure.Persistence;

public sealed class McpUserSettings
{
    public Guid UserId { get; set; }
    public bool Enabled { get; set; }
    public bool AllowCreateWorkspace { get; set; }
}

public sealed class McpWorkspaceSettings
{
    public Guid WorkspaceId { get; set; }
    public bool Enabled { get; set; }
}

public sealed class McpWorkspaceUserSettings
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public int PersonalPermissions { get; set; }
    public int GrantedPermissions { get; set; }
}
