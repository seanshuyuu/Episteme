namespace Episteme.Domain.Workspaces;

public sealed class WorkspaceAuditEvent
{
    private WorkspaceAuditEvent() { }

    public WorkspaceAuditEvent(Guid workspaceId, Guid actorUserId, string eventType,
        Guid? targetUserId = null, WorkspaceRole? targetRole = null)
    {
        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        ActorUserId = actorUserId;
        EventType = eventType;
        TargetUserId = targetUserId;
        TargetRole = targetRole;
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public Guid? TargetUserId { get; private set; }
    public WorkspaceRole? TargetRole { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
