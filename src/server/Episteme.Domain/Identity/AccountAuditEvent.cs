namespace Episteme.Domain.Identity;

public sealed class AccountAuditEvent
{
    private AccountAuditEvent() { }

    public AccountAuditEvent(Guid actorUserId, Guid targetUserId, string eventType, string? targetTokenId = null,
        string? targetAuthorizationId = null)
    {
        Id = Guid.NewGuid();
        ActorUserId = actorUserId;
        TargetUserId = targetUserId;
        EventType = eventType;
        TargetTokenId = targetTokenId;
        TargetAuthorizationId = targetAuthorizationId;
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid ActorUserId { get; private set; }
    public Guid TargetUserId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string? TargetTokenId { get; private set; }
    public string? TargetAuthorizationId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}
