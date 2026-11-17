using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;

namespace Episteme.Application.Tests;

public sealed class WorkspaceManagementServiceTests
{
    [Fact]
    public async Task Missing_scope_denies_even_administrator_without_calling_repository()
    {
        var repository = new FakeRepository();
        var service = new WorkspaceManagementService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string>(), true);

        var result = await service.UpdateAsync(
            new(Guid.NewGuid(), actor, Guid.NewGuid(), "Name", WorkspaceType.Code, null), default);

        Assert.Equal(WorkspaceManagementFailure.Unauthorized, result.Failure);
        Assert.False(repository.Called);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_without_calling_repository()
    {
        var repository = new FakeRepository();
        var service = new WorkspaceManagementService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string> { "workspaces.manage" }, false);

        var result = await service.UpdateAsync(
            new(Guid.NewGuid(), actor, Guid.NewGuid(), " ", WorkspaceType.Code, null), default);

        Assert.Equal(WorkspaceManagementFailure.InvalidInput, result.Failure);
        Assert.False(repository.Called);
    }

    private sealed class FakeRepository : IWorkspaceManagementRepository
    {
        public bool Called { get; private set; }

        public Task<WorkspaceManagementResult> UpdateAsync(UpdateWorkspaceCommand command, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.Conflict));
        }

        public Task<WorkspaceManagementResult> ArchiveAsync(
            Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.Conflict));
        }

        public Task<WorkspaceManagementResult> SetConfigurationAsync(
            Guid workspaceId, WorkspaceActor actor, Guid expectedVersion, WorkspaceConfiguration configuration, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new WorkspaceManagementResult(null, WorkspaceManagementFailure.Conflict));
        }
    }
}
