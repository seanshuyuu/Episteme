using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

public enum WorkspacePermission
{
    Read,
    Manage
}

public enum WorkspaceAccessFailure
{
    MissingScope,
    InvalidInput,
    NotFound,
    UserInactive,
    MembershipRequired,
    InsufficientRole,
    Unsupported,
    TooLarge
}

public sealed record WorkspaceAccessSnapshot(
    Workspace Workspace,
    bool IsMembershipActive,
    WorkspaceRole? Role,
    bool IsUserActive,
    bool McpReadAllowed = false);

public sealed record WorkspaceSummary(
    Guid Id,
    string Name,
    string Slug,
    WorkspaceType Type,
    string? Description,
    string DefaultBranch,
    Guid ConcurrencyStamp = default,
    WorkspaceConfiguration? Configuration = null,
    WorkspaceRole? MemberRole = null,
    string? OwnerName = null,
    WorkspaceStatus Status = WorkspaceStatus.Active,
    DateTimeOffset CreatedAt = default);

public sealed record WorkspaceAccessRequest(
    Guid WorkspaceId,
    Guid ActorUserId,
    IReadOnlySet<string> Scopes,
    bool IsSystemAdministrator,
    WorkspacePermission Permission,
    bool IsMcp = false);

public sealed record WorkspaceAccessResult(
    WorkspaceSummary? Workspace,
    WorkspaceAccessFailure? Failure)
{
    public bool Succeeded => Workspace is not null && Failure is null;

    public static WorkspaceAccessResult Allowed(WorkspaceSummary workspace) => new(workspace, null);
    public static WorkspaceAccessResult Denied(WorkspaceAccessFailure failure) => new(null, failure);
}

public interface IWorkspaceAccessRepository
{
    Task<WorkspaceAccessSnapshot?> GetAccessSnapshotAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<WorkspaceAccessSnapshot?> GetMcpAccessSnapshotAsync(
        Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("MCP workspace access is not supported by this repository.");
}

/// <summary>
/// Central resource authorization used by all transports. OAuth scopes constrain the operation;
/// the member role constrains the resource. Neither check substitutes for the other.
/// </summary>
public sealed class WorkspaceAccessService(IWorkspaceAccessRepository repository)
{
    public async Task<WorkspaceAccessResult> AuthorizeAsync(
        WorkspaceAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WorkspaceId == Guid.Empty || request.ActorUserId == Guid.Empty)
            return WorkspaceAccessResult.Denied(WorkspaceAccessFailure.NotFound);

        var requiredScope = request.Permission == WorkspacePermission.Read
            ? "workspaces.read"
            : "workspaces.manage";
        if (!request.Scopes.Contains(requiredScope))
            return WorkspaceAccessResult.Denied(WorkspaceAccessFailure.MissingScope);

        var snapshot = request.IsMcp
            ? await repository.GetMcpAccessSnapshotAsync(request.WorkspaceId, request.ActorUserId, cancellationToken)
            : await repository.GetAccessSnapshotAsync(request.WorkspaceId, request.ActorUserId, cancellationToken);
        if (snapshot is null || snapshot.Workspace.Status != WorkspaceStatus.Active)
            return WorkspaceAccessResult.Denied(WorkspaceAccessFailure.NotFound);
        if (!snapshot.IsUserActive)
            return WorkspaceAccessResult.Denied(WorkspaceAccessFailure.UserInactive);

        if (request.IsMcp && (request.Permission != WorkspacePermission.Read || !snapshot.McpReadAllowed))
            return WorkspaceAccessResult.Denied(WorkspaceAccessFailure.MembershipRequired);

        if (!request.IsSystemAdministrator || request.IsMcp)
        {
            if (!snapshot.IsMembershipActive || snapshot.Role is null)
                return WorkspaceAccessResult.Denied(WorkspaceAccessFailure.MembershipRequired);
            if (request.Permission == WorkspacePermission.Manage && snapshot.Role != WorkspaceRole.Owner)
                return WorkspaceAccessResult.Denied(WorkspaceAccessFailure.InsufficientRole);
        }

        var workspace = snapshot.Workspace;
        return WorkspaceAccessResult.Allowed(new WorkspaceSummary(
            workspace.Id,
            workspace.Name,
            workspace.Slug,
            workspace.Type,
            workspace.Description,
            workspace.DefaultBranch,
            workspace.ConcurrencyStamp,
            WorkspaceConfiguration.Parse(workspace.ConfigurationJson)));
    }
}
