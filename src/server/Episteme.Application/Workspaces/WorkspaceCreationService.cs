using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

public sealed record CreateWorkspaceCommand(WorkspaceActor Actor, string Name, string Slug, WorkspaceType Type, string? Description,
    WorkspaceConfiguration? Configuration = null, IReadOnlyList<string>? Directories = null);

public enum WorkspaceCreationFailure { Unauthorized, InvalidInput, Conflict, NotFound, ProvisioningFailed }

public sealed record WorkspaceCreationResult(WorkspaceSummary? Workspace, WorkspaceCreationFailure? Failure, Guid? PendingWorkspaceId = null);

public sealed class WorkspaceProvisioningException(string message, Exception? inner = null) : Exception(message, inner);

public interface IWorkspaceCreationRepository
{
    Task<WorkspaceCreationResult> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken);
    Task<WorkspaceCreationResult> RetryAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkspaceSummary>?> ListPendingAsync(Guid userId, int offset, int count, CancellationToken cancellationToken);
}

public interface IWorkspaceRepositoryProvisioner
{
    Task InitializeAsync(string storageKey, string defaultBranch, CancellationToken cancellationToken, IReadOnlyList<string>? directories = null);
}

public interface IWorkspaceCreationService
{
    Task<WorkspaceCreationResult> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken);
    Task<WorkspaceCreationResult> RetryAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken);
    Task<WorkspaceListingResult> ListPendingAsync(WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken);
}

public sealed class WorkspaceCreationService(IWorkspaceCreationRepository repository) : IWorkspaceCreationService
{
    public Task<WorkspaceCreationResult> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken)
    {
        if (!Authorized(command.Actor))
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.Unauthorized));
        if (command.Configuration is not null && !command.Configuration.IsValid())
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.InvalidInput));
        if (command.Directories is not null && !WorkspaceDirectorySkeleton.TryNormalize(command.Directories, out _))
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.InvalidInput));
        try
        {
            // Use the domain factory for the same validation as the persisted entity.
            Workspace.Create(command.Actor.UserId, command.Name, command.Slug, command.Type, command.Description);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.InvalidInput));
        }
        return repository.CreateAsync(command, cancellationToken);
    }

    public Task<WorkspaceCreationResult> RetryAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.Unauthorized));
        if (workspaceId == Guid.Empty)
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.InvalidInput));
        return repository.RetryAsync(workspaceId, actor, cancellationToken);
    }

    public async Task<WorkspaceListingResult> ListPendingAsync(
        WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return new(null, false, WorkspaceListingFailure.Unauthorized);
        if (offset < 0 || offset > int.MaxValue - 101 || limit is < 1 or > 100)
            return new(null, false, WorkspaceListingFailure.InvalidPage);
        var items = await repository.ListPendingAsync(actor.UserId, offset, limit + 1, cancellationToken);
        return items is null
            ? new(null, false, WorkspaceListingFailure.InactiveUser)
            : new(items.Take(limit).ToArray(), items.Count > limit, null);
    }

    private static bool Authorized(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.Scopes.Contains("workspaces.manage");
}
