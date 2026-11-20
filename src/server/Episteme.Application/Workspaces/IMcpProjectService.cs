namespace Episteme.Application.Workspaces;

public interface IMcpProjectService
{
    Task<ProjectListResult> ListAsync(Guid workspaceId, WorkspaceActor actor, int offset, int limit, CancellationToken cancellationToken);
    Task<ProjectResult> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken);
    Task<ProjectResult> UpdateAsync(UpdateProjectCommand command, CancellationToken cancellationToken);
    Task<ProjectResult> DeleteAsync(Guid projectId, WorkspaceActor actor, Guid expectedVersion, CancellationToken cancellationToken);
}
