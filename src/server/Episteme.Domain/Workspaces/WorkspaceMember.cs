namespace Episteme.Domain.Workspaces;

public sealed class WorkspaceMember
{
    private WorkspaceMember() { } // EF Core materialization.

    private WorkspaceMember(
        Guid workspaceId,
        Guid userId,
        WorkspaceRole role,
        Guid addedByUserId,
        DateTimeOffset joinedAt)
    {
        WorkspaceId = workspaceId;
        UserId = userId;
        Role = role;
        Status = WorkspaceMemberStatus.Active;
        AddedByUserId = addedByUserId;
        JoinedAt = joinedAt;
    }

    public Guid WorkspaceId { get; private set; }
    public Guid UserId { get; private set; }
    public WorkspaceRole Role { get; private set; }
    public WorkspaceMemberStatus Status { get; private set; }
    public Guid AddedByUserId { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }

    public static WorkspaceMember CreateOwner(Guid workspaceId, Guid userId, DateTimeOffset? now = null)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("A workspace is required.", nameof(workspaceId));
        if (userId == Guid.Empty)
            throw new ArgumentException("A user is required.", nameof(userId));

        return new WorkspaceMember(workspaceId, userId, WorkspaceRole.Owner, userId, now ?? DateTimeOffset.UtcNow);
    }

    public static WorkspaceMember Add(
        Guid workspaceId,
        Guid userId,
        WorkspaceRole role,
        Guid addedByUserId,
        DateTimeOffset? now = null)
    {
        if (role is not (WorkspaceRole.Editor or WorkspaceRole.Reader))
            throw new ArgumentException("Additional members must be Editor or Reader.", nameof(role));
        if (workspaceId == Guid.Empty || userId == Guid.Empty || addedByUserId == Guid.Empty)
            throw new ArgumentException("Workspace, user, and adding actor IDs are required.");

        return new WorkspaceMember(workspaceId, userId, role, addedByUserId, now ?? DateTimeOffset.UtcNow);
    }

    public void SetRole(WorkspaceRole role, Guid actorUserId)
    {
        if (!Enum.IsDefined(role) || actorUserId == Guid.Empty)
            throw new ArgumentException("A valid role and actor are required.");
        if (Status == WorkspaceMemberStatus.Removed)
        {
            AddedByUserId = actorUserId;
            JoinedAt = DateTimeOffset.UtcNow;
        }
        Role = role;
        Status = WorkspaceMemberStatus.Active;
    }

    public void Remove() => Status = WorkspaceMemberStatus.Removed;
}
