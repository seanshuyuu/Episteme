namespace Episteme.Application.Workspaces;

public enum WorkspaceListingFailure
{
    Unauthorized,
    InvalidPage,
    InactiveUser
}

public sealed record WorkspaceListingRequest(
    Guid ActorUserId,
    IReadOnlySet<string> Scopes,
    bool IsSystemAdministrator,
    int Offset,
    int Limit,
    bool IsMcp = false);

public sealed record WorkspaceListingResult(
    IReadOnlyList<WorkspaceSummary>? Items,
    bool HasMore,
    WorkspaceListingFailure? Failure);

public interface IWorkspaceListingRepository
{
    // Null represents a missing or inactive user, not an empty workspace list.
    Task<IReadOnlyList<WorkspaceSummary>?> ListForActiveUserAsync(
        Guid userId,
        bool isSystemAdministrator,
        int offset,
        int count,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceSummary>?> ListForMcpUserAsync(
        Guid userId, int offset, int count, CancellationToken cancellationToken) =>
        throw new NotSupportedException("MCP workspace listing is not supported by this repository.");
}

public interface IWorkspaceListingService
{
    Task<WorkspaceListingResult> ListAsync(WorkspaceListingRequest request, CancellationToken cancellationToken);
}

public sealed class WorkspaceListingService(IWorkspaceListingRepository repository) : IWorkspaceListingService
{
    public async Task<WorkspaceListingResult> ListAsync(WorkspaceListingRequest request, CancellationToken cancellationToken)
    {
        if (request.ActorUserId == Guid.Empty || !request.Scopes.Contains("workspaces.read"))
            return new(null, false, WorkspaceListingFailure.Unauthorized);
        if (request.Offset < 0 || request.Offset > int.MaxValue - 101 || request.Limit is < 1 or > 100)
            return new(null, false, WorkspaceListingFailure.InvalidPage);

        var items = request.IsMcp
            ? await repository.ListForMcpUserAsync(request.ActorUserId, request.Offset, request.Limit + 1, cancellationToken)
            : await repository.ListForActiveUserAsync(request.ActorUserId, request.IsSystemAdministrator,
                request.Offset, request.Limit + 1, cancellationToken);
        if (items is null)
            return new(null, false, WorkspaceListingFailure.InactiveUser);

        return new(items.Take(request.Limit).ToArray(), items.Count > request.Limit, null);
    }
}
