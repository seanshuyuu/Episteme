using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

public enum UserSettingsFailure
{
    Unauthorized,
    InvalidDirectory,
    InactiveUser
}

public sealed record UserSettingsResult(string? Directory, UserSettingsFailure? Failure);

public interface IUserWorkspaceSettingsRepository
{
    Task<string?> GetDirectoryForActiveUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> SetDirectoryForActiveUserAsync(Guid userId, string directory, CancellationToken cancellationToken);
}

public interface IUserWorkspaceSettingsService
{
    Task<UserSettingsResult> GetAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken);
    Task<UserSettingsResult> SetAsync(Guid userId, IReadOnlySet<string> scopes, string? directory, CancellationToken cancellationToken);
}

public sealed class UserWorkspaceSettingsService(IUserWorkspaceSettingsRepository repository) : IUserWorkspaceSettingsService
{
    public async Task<UserSettingsResult> GetAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("workspaces.read"))
            return new(null, UserSettingsFailure.Unauthorized);

        var directory = await repository.GetDirectoryForActiveUserAsync(userId, cancellationToken);
        return directory is null ? new(null, UserSettingsFailure.InactiveUser) : new(directory, null);
    }

    public async Task<UserSettingsResult> SetAsync(Guid userId, IReadOnlySet<string> scopes, string? directory, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("workspaces.manage"))
            return new(null, UserSettingsFailure.Unauthorized);
        if (!UserWorkspaceSettings.IsValidDirectory(directory))
            return new(null, UserSettingsFailure.InvalidDirectory);

        var saved = await repository.SetDirectoryForActiveUserAsync(userId, directory!, cancellationToken);
        return saved ? new(directory, null) : new(null, UserSettingsFailure.InactiveUser);
    }
}
