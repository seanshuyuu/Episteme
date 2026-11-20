namespace Episteme.Application.Workspaces;

public sealed record McpPersonalSettings(bool Enabled, bool AllowCreateWorkspace);
public sealed record McpWorkspaceSettingsView(bool Enabled, McpPermission PersonalPermissions,
    McpPermission GrantedPermissions, bool IsOwner);
public sealed record McpSettingsResult<T>(T? Value, McpSettingsFailure? Failure);
public enum McpSettingsFailure { Unauthorized, NotFound, InvalidInput }

public interface IMcpSettingsRepository
{
    Task<McpSettingsResult<McpPersonalSettings>> GetPersonalAsync(WorkspaceActor actor, CancellationToken cancellationToken);
    Task<McpSettingsResult<McpPersonalSettings>> SetPersonalAsync(WorkspaceActor actor, McpPersonalSettings settings, CancellationToken cancellationToken);
    Task<McpSettingsResult<McpWorkspaceSettingsView>> GetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, CancellationToken cancellationToken);
    Task<McpSettingsResult<McpWorkspaceSettingsView>> SetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, McpPermission permissions, CancellationToken cancellationToken);
    Task<McpSettingsResult<bool>> SetWorkspaceEnabledAsync(WorkspaceActor actor, Guid workspaceId, bool enabled, CancellationToken cancellationToken);
    Task<McpSettingsResult<McpPermission>> GetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, CancellationToken cancellationToken);
    Task<McpSettingsResult<McpPermission>> SetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, McpPermission permissions, CancellationToken cancellationToken);
}

public interface IMcpSettingsService : IMcpSettingsRepository;

public sealed class McpSettingsService(IMcpSettingsRepository repository) : IMcpSettingsService
{
    public Task<McpSettingsResult<McpPersonalSettings>> GetPersonalAsync(WorkspaceActor actor, CancellationToken cancellationToken) =>
        CanManage(actor) ? repository.GetPersonalAsync(actor, cancellationToken) : Denied<McpPersonalSettings>();

    public Task<McpSettingsResult<McpPersonalSettings>> SetPersonalAsync(WorkspaceActor actor, McpPersonalSettings settings, CancellationToken cancellationToken) =>
        CanManage(actor) ? repository.SetPersonalAsync(actor, settings, cancellationToken) : Denied<McpPersonalSettings>();

    public Task<McpSettingsResult<McpWorkspaceSettingsView>> GetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, CancellationToken cancellationToken) =>
        !CanManage(actor) ? Denied<McpWorkspaceSettingsView>() :
        workspaceId == Guid.Empty ? Invalid<McpWorkspaceSettingsView>() :
        repository.GetWorkspaceAsync(actor, workspaceId, cancellationToken);

    public Task<McpSettingsResult<McpWorkspaceSettingsView>> SetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, McpPermission permissions, CancellationToken cancellationToken) =>
        !CanManage(actor) ? Denied<McpWorkspaceSettingsView>() :
        workspaceId == Guid.Empty || !McpAccessPolicy.IsWorkspacePermissionsValid(permissions) ? Invalid<McpWorkspaceSettingsView>() :
        repository.SetWorkspaceAsync(actor, workspaceId, permissions, cancellationToken);

    public Task<McpSettingsResult<bool>> SetWorkspaceEnabledAsync(WorkspaceActor actor, Guid workspaceId, bool enabled, CancellationToken cancellationToken) =>
        !CanManage(actor) ? Denied<bool>() :
        workspaceId == Guid.Empty ? Invalid<bool>() :
        repository.SetWorkspaceEnabledAsync(actor, workspaceId, enabled, cancellationToken);

    public Task<McpSettingsResult<McpPermission>> GetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, CancellationToken cancellationToken) =>
        !CanManage(actor) ? Denied<McpPermission>() :
        workspaceId == Guid.Empty || memberId == Guid.Empty ? Invalid<McpPermission>() :
        repository.GetMemberGrantAsync(actor, workspaceId, memberId, cancellationToken);

    public Task<McpSettingsResult<McpPermission>> SetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, McpPermission permissions, CancellationToken cancellationToken) =>
        !CanManage(actor) ? Denied<McpPermission>() :
        workspaceId == Guid.Empty || memberId == Guid.Empty || !McpAccessPolicy.IsWorkspacePermissionsValid(permissions) ? Invalid<McpPermission>() :
        repository.SetMemberGrantAsync(actor, workspaceId, memberId, permissions, cancellationToken);

    private static bool CanManage(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.Scopes.Contains("tokens.manage");

    private static Task<McpSettingsResult<T>> Denied<T>() =>
        Task.FromResult(new McpSettingsResult<T>(default, McpSettingsFailure.Unauthorized));

    private static Task<McpSettingsResult<T>> Invalid<T>() =>
        Task.FromResult(new McpSettingsResult<T>(default, McpSettingsFailure.InvalidInput));
}
