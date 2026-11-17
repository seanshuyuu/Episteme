namespace Episteme.Domain.Identity;

public enum UserAccountStatus
{
    Active,
    Disabled
}

/// <summary>
/// Domain view of an account. Identity's persistence and password fields stay in Infrastructure.
/// </summary>
public sealed record UserAccount(
    Guid Id,
    string UserName,
    string Email,
    UserAccountStatus Status,
    DateTimeOffset CreatedAt);
