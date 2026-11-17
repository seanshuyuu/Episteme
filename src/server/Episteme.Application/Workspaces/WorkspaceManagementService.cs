using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

public sealed record WorkspaceActor(Guid UserId, IReadOnlySet<string> Scopes, bool IsSystemAdministrator);

public sealed record UpdateWorkspaceCommand(
    Guid WorkspaceId,
    WorkspaceActor Actor,
    Guid ExpectedVersion,
    string Name,
    WorkspaceType Type,
    string? Description);

public enum WorkspaceManagementFailure
{
    Unauthorized,
    NotFound,
    InvalidInput,
    Conflict
}

public sealed record WorkspaceManagementResult(WorkspaceSummary? Workspace, WorkspaceManagementFailure? Failure);

public interface IWorkspaceManagementRepository
{
    Task<WorkspaceManagementResult> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken cancellationToken);
    Task<WorkspaceManagementResult> ArchiveAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, CancellationToken cancellationToken);
    Task<WorkspaceManagementResult> SetConfigurationAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, WorkspaceConfiguration configuration, CancellationToken cancellationToken);
}

public interface IWorkspaceManagementService
{
    Task<WorkspaceManagementResult> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken cancellationToken);
    Task<WorkspaceManagementResult> ArchiveAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, CancellationToken cancellationToken);
    Task<WorkspaceManagementResult> SetConfigurationAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, WorkspaceConfiguration configuration, CancellationToken cancellationToken);
}

public sealed class WorkspaceManagementService(IWorkspaceManagementRepository repository) : IWorkspaceManagementService
{
    public Task<WorkspaceManagementResult> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken cancellationToken)
    {
        if (!CanManage(command.Actor))
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.Unauthorized));
        if (command.WorkspaceId == Guid.Empty || command.ExpectedVersion == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 120 ||
            command.Description?.Length > 2000 || !Enum.IsDefined(command.Type))
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.InvalidInput));
        return repository.UpdateAsync(command, cancellationToken);
    }

    public Task<WorkspaceManagementResult> ArchiveAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, CancellationToken cancellationToken)
    {
        if (!CanManage(actor))
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.Unauthorized));
        if (workspaceId == Guid.Empty || expectedVersion == Guid.Empty)
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.InvalidInput));
        return repository.ArchiveAsync(workspaceId, actor, expectedVersion, cancellationToken);
    }

    private static bool CanManage(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.Scopes.Contains("workspaces.manage");

    public Task<WorkspaceManagementResult> SetConfigurationAsync(
        Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, WorkspaceConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!CanManage(actor))
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.Unauthorized));
        if (workspaceId == Guid.Empty || expectedVersion == Guid.Empty || configuration is null || !configuration.IsValid())
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.InvalidInput));
        return repository.SetConfigurationAsync(workspaceId, actor, expectedVersion, configuration, cancellationToken);
    }
}
