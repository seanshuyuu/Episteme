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
    Task<string?> GetDirectoryForMcpUserAsync(Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("MCP user settings are not supported by this repository.");
    Task<bool> SetDirectoryForMcpUserAsync(Guid userId, string directory, CancellationToken cancellationToken) =>
        throw new NotSupportedException("MCP user settings are not supported by this repository.");
}

public interface IUserWorkspaceSettingsService
{
    Task<UserSettingsResult> GetAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken);
    Task<UserSettingsResult> SetAsync(Guid userId, IReadOnlySet<string> scopes, string? directory, CancellationToken cancellationToken);
    Task<UserSettingsResult> GetMcpAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken);
    Task<UserSettingsResult> SetMcpAsync(Guid userId, IReadOnlySet<string> scopes, string? directory, CancellationToken cancellationToken);
}

public sealed class UserWorkspaceSettingsService(IUserWorkspaceSettingsRepository repository) : IUserWorkspaceSettingsService
{
    public Task<UserSettingsResult> GetAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken) =>
        GetCoreAsync(userId, scopes, false, cancellationToken);
    public Task<UserSettingsResult> GetMcpAsync(Guid userId, IReadOnlySet<string> scopes, CancellationToken cancellationToken) =>
        GetCoreAsync(userId, scopes, true, cancellationToken);

    private async Task<UserSettingsResult> GetCoreAsync(Guid userId, IReadOnlySet<string> scopes, bool isMcp, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("workspaces.read"))
            return new(null, UserSettingsFailure.Unauthorized);

        var directory = isMcp
            ? await repository.GetDirectoryForMcpUserAsync(userId, cancellationToken)
            : await repository.GetDirectoryForActiveUserAsync(userId, cancellationToken);
        return directory is null ? new(null, UserSettingsFailure.InactiveUser) : new(directory, null);
    }

    public Task<UserSettingsResult> SetAsync(Guid userId, IReadOnlySet<string> scopes, string? directory, CancellationToken cancellationToken) =>
        SetCoreAsync(userId, scopes, directory, false, cancellationToken);
    public Task<UserSettingsResult> SetMcpAsync(Guid userId, IReadOnlySet<string> scopes, string? directory, CancellationToken cancellationToken) =>
        SetCoreAsync(userId, scopes, directory, true, cancellationToken);

    private async Task<UserSettingsResult> SetCoreAsync(Guid userId, IReadOnlySet<string> scopes, string? directory, bool isMcp, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || !scopes.Contains("workspaces.manage"))
            return new(null, UserSettingsFailure.Unauthorized);
        if (!UserWorkspaceSettings.IsValidDirectory(directory))
            return new(null, UserSettingsFailure.InvalidDirectory);

        var saved = isMcp
            ? await repository.SetDirectoryForMcpUserAsync(userId, directory!, cancellationToken)
            : await repository.SetDirectoryForActiveUserAsync(userId, directory!, cancellationToken);
        return saved ? new(directory, null) : new(null, UserSettingsFailure.InactiveUser);
    }
}
