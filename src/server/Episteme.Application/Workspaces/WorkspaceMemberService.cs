using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

public sealed record WorkspaceMemberSummary(Guid UserId, WorkspaceRole Role, DateTimeOffset JoinedAt);
public sealed record WorkspaceMemberResult(WorkspaceMemberSummary? Member, WorkspaceManagementFailure? Failure);
public sealed record WorkspaceMemberListResult(
    IReadOnlyList<WorkspaceMemberSummary>? Items, bool HasMore, WorkspaceManagementFailure? Failure);

public interface IWorkspaceMemberRepository
{
    Task<WorkspaceMemberListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken);
    Task<WorkspaceMemberResult> SetAsync(Guid workspaceId, Guid userId, WorkspaceRole role, WorkspaceActor actor, CancellationToken cancellationToken);
    Task<WorkspaceMemberResult> RemoveAsync(Guid workspaceId, Guid userId, WorkspaceActor actor, CancellationToken cancellationToken);
}

public interface IWorkspaceMemberService : IWorkspaceMemberRepository;

public sealed class WorkspaceMemberService(IWorkspaceMemberRepository repository) : IWorkspaceMemberService
{
    public Task<WorkspaceMemberListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!CanManage(actor))
            return Task.FromResult(new WorkspaceMemberListResult(null, false, WorkspaceManagementFailure.Unauthorized));
        if (workspaceId == Guid.Empty || offset < 0 || limit is < 1 or > 100)
            return Task.FromResult(new WorkspaceMemberListResult(null, false, WorkspaceManagementFailure.InvalidInput));
        return repository.ListAsync(workspaceId, actor, offset, limit, cancellationToken);
    }

    public Task<WorkspaceMemberResult> SetAsync(Guid workspaceId, Guid userId, WorkspaceRole role, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        if (!CanManage(actor))
            return Task.FromResult(new WorkspaceMemberResult(null, WorkspaceManagementFailure.Unauthorized));
        if (workspaceId == Guid.Empty || userId == Guid.Empty || !Enum.IsDefined(role))
            return Task.FromResult(new WorkspaceMemberResult(null, WorkspaceManagementFailure.InvalidInput));
        return repository.SetAsync(workspaceId, userId, role, actor, cancellationToken);
    }

    public Task<WorkspaceMemberResult> RemoveAsync(Guid workspaceId, Guid userId, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        if (!CanManage(actor))
            return Task.FromResult(new WorkspaceMemberResult(null, WorkspaceManagementFailure.Unauthorized));
        if (workspaceId == Guid.Empty || userId == Guid.Empty)
            return Task.FromResult(new WorkspaceMemberResult(null, WorkspaceManagementFailure.InvalidInput));
        return repository.RemoveAsync(workspaceId, userId, actor, cancellationToken);
    }

    private static bool CanManage(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.Scopes.Contains("workspaces.manage");
}
