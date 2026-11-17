namespace Episteme.Domain.Identity;

public sealed class OAuthRevocationAuditEvent
{
    private OAuthRevocationAuditEvent() { }

    public OAuthRevocationAuditEvent(string clientId, string? subject, string tokenId)
    {
        Id = Guid.NewGuid();
        ClientId = clientId;
        Subject = subject;
        TokenId = tokenId;
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string ClientId { get; private set; } = string.Empty;
    public string? Subject { get; private set; }
    public string TokenId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
}
