using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

public sealed record ProjectSummary(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Slug,
    string? Description,
    ProjectStatus Status,
    string DefaultBranch,
    Guid ConcurrencyStamp,
    int FileCount,
    int FolderCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateProjectCommand(
    Guid WorkspaceId,
    WorkspaceActor Actor,
    string Name,
    string Slug,
    string? Description);

public sealed record UpdateProjectCommand(
    Guid ProjectId,
    WorkspaceActor Actor,
    Guid ExpectedVersion,
    string Name,
    string Slug,
    string? Description);

public sealed record PromoteProjectCommand(
    Guid WorkspaceId,
    Guid SourceProjectId,
    WorkspaceActor Actor,
    string FolderPath);

public sealed record DemoteProjectCommand(
    Guid WorkspaceId,
    Guid SourceProjectId,
    WorkspaceActor Actor);

public enum ProjectFailure
{
    Unauthorized,
    NotFound,
    InvalidInput,
    Conflict,
    ProvisioningFailed
}

public sealed record ProjectResult(ProjectSummary? Project, ProjectFailure? Failure);
public sealed record ProjectListResult(IReadOnlyList<ProjectSummary>? Items, bool HasMore, ProjectFailure? Failure);

public interface IProjectRepository
{
    Task<ProjectListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit,
        CancellationToken cancellationToken);
    Task<ProjectResult> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken);
    Task<ProjectResult> UpdateAsync(UpdateProjectCommand command, CancellationToken cancellationToken);
    Task<ProjectResult> SetStatusAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        ProjectStatus status, CancellationToken cancellationToken);
    Task<ProjectResult> DeleteAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        CancellationToken cancellationToken);
    Task<ProjectResult> PromoteFolderAsync(PromoteProjectCommand command,
        CancellationToken cancellationToken);
    Task<ProjectResult> DemoteAsync(DemoteProjectCommand command,
        CancellationToken cancellationToken);
}

public interface IProjectService
{
    Task<ProjectListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit,
        CancellationToken cancellationToken);
    Task<ProjectResult> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken);
    Task<ProjectResult> UpdateAsync(UpdateProjectCommand command, CancellationToken cancellationToken);
    Task<ProjectResult> SetStatusAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        ProjectStatus status, CancellationToken cancellationToken);
    Task<ProjectResult> DeleteAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        CancellationToken cancellationToken);
    Task<ProjectResult> PromoteFolderAsync(PromoteProjectCommand command,
        CancellationToken cancellationToken);
    Task<ProjectResult> DemoteAsync(DemoteProjectCommand command,
        CancellationToken cancellationToken);
}

public sealed class ProjectService(IProjectRepository repository) : IProjectService
{
    public Task<ProjectListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit,
        CancellationToken cancellationToken)
    {
        if (!CanRead(actor) || workspaceId == Guid.Empty || offset < 0 || limit is < 1 or > 100)
            return Task.FromResult(new ProjectListResult(null, false,
                offset < 0 || limit is < 1 or > 100 ? ProjectFailure.InvalidInput : ProjectFailure.Unauthorized));
        return repository.ListAsync(workspaceId, actor, offset, limit, cancellationToken);
    }

    public Task<ProjectResult> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        if (!CanManage(command.Actor) || command.WorkspaceId == Guid.Empty)
            return Task.FromResult(new ProjectResult(null, ProjectFailure.Unauthorized));
        try
        {
            _ = Project.Create(command.WorkspaceId, command.Actor.UserId, command.Name, command.Slug, command.Description);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new ProjectResult(null, ProjectFailure.InvalidInput));
        }
        return repository.CreateAsync(command, cancellationToken);
    }

    public Task<ProjectResult> UpdateAsync(UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        if (!CanManage(command.Actor))
            return Task.FromResult(new ProjectResult(null, ProjectFailure.Unauthorized));
        if (command.ProjectId == Guid.Empty || command.ExpectedVersion == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 120 ||
            command.Description?.Length > 2000)
            return Task.FromResult(new ProjectResult(null, ProjectFailure.InvalidInput));
        return repository.UpdateAsync(command, cancellationToken);
    }

    public Task<ProjectResult> SetStatusAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        ProjectStatus status, CancellationToken cancellationToken)
    {
        if (!CanManage(actor))
            return Task.FromResult(new ProjectResult(null, ProjectFailure.Unauthorized));
        if (projectId == Guid.Empty || expectedVersion == Guid.Empty)
            return Task.FromResult(new ProjectResult(null, ProjectFailure.InvalidInput));
        return repository.SetStatusAsync(projectId, actor, expectedVersion, status, cancellationToken);
    }

    public Task<ProjectResult> DeleteAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!CanManage(actor))
            return Task.FromResult(new ProjectResult(null, ProjectFailure.Unauthorized));
        if (projectId == Guid.Empty || expectedVersion == Guid.Empty)
            return Task.FromResult(new ProjectResult(null, ProjectFailure.InvalidInput));
        return repository.DeleteAsync(projectId, actor, expectedVersion, cancellationToken);
    }

    public Task<ProjectResult> PromoteFolderAsync(PromoteProjectCommand command,
        CancellationToken cancellationToken)
    {
        if (!CanManage(command.Actor) || command.WorkspaceId == Guid.Empty ||
            command.SourceProjectId == Guid.Empty || string.IsNullOrWhiteSpace(command.FolderPath))
            return Task.FromResult(new ProjectResult(null, ProjectFailure.InvalidInput));
        return repository.PromoteFolderAsync(command, cancellationToken);
    }

    public Task<ProjectResult> DemoteAsync(DemoteProjectCommand command,
        CancellationToken cancellationToken)
    {
        if (!CanManage(command.Actor) || command.WorkspaceId == Guid.Empty ||
            command.SourceProjectId == Guid.Empty)
            return Task.FromResult(new ProjectResult(null, ProjectFailure.InvalidInput));
        return repository.DemoteAsync(command, cancellationToken);
    }

    private static bool CanRead(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.Scopes.Contains("workspaces.read");
    private static bool CanManage(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.Scopes.Contains("workspaces.manage");
}
