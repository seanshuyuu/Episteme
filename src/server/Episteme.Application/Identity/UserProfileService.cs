namespace Episteme.Application.Identity;

/// <summary>Self-service account profile of the authenticated caller.</summary>
public sealed record UserProfile(Guid UserId, string? DisplayName, string? UserName, string? Email);

public enum UserProfileFailure { Unauthorized, InvalidInput, NotFound }

public sealed record UserProfileResult(UserProfile? Profile, UserProfileFailure? Failure);

public enum PasswordChangeFailure { Unauthorized, InvalidInput, NotFound, InvalidCredentials, Conflict }

public sealed record PasswordChangeResult(PasswordChangeFailure? Failure);

public interface IUserProfileRepository
{
    Task<UserProfileResult> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
    Task<UserProfileResult> UpdateDisplayNameAsync(Guid userId, string? displayName, CancellationToken cancellationToken);
    Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword,
        CancellationToken cancellationToken);
}

public interface IUserProfileService
{
    Task<UserProfileResult> GetProfileAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken);
    Task<UserProfileResult> UpdateDisplayNameAsync(Guid userId, IReadOnlySet<string> scopes, string? displayName,
        CancellationToken cancellationToken);
    Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, IReadOnlySet<string> scopes, string currentPassword,
        string newPassword, CancellationToken cancellationToken);
}

public sealed class UserProfileService(IUserProfileRepository repository) : IUserProfileService
{
    public const int MaxDisplayNameLength = 50;
    public const int MinPasswordLength = 8;

    public Task<UserProfileResult> GetProfileAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("profile"))
            return Task.FromResult(new UserProfileResult(null, UserProfileFailure.Unauthorized));
        return repository.GetProfileAsync(userId, cancellationToken);
    }

    public Task<UserProfileResult> UpdateDisplayNameAsync(Guid userId, IReadOnlySet<string> scopes, string? displayName,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("profile"))
            return Task.FromResult(new UserProfileResult(null, UserProfileFailure.Unauthorized));
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (name is { Length: > MaxDisplayNameLength })
            return Task.FromResult(new UserProfileResult(null, UserProfileFailure.InvalidInput));
        return repository.UpdateDisplayNameAsync(userId, name, cancellationToken);
    }

    public Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, IReadOnlySet<string> scopes,
        string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("profile"))
            return Task.FromResult(new PasswordChangeResult(PasswordChangeFailure.Unauthorized));
        if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword) ||
            newPassword.Length is < MinPasswordLength or > 1024)
            return Task.FromResult(new PasswordChangeResult(PasswordChangeFailure.InvalidInput));
        return repository.ChangePasswordAsync(userId, currentPassword, newPassword, cancellationToken);
    }
}
