using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;

namespace Episteme.Application.Tests;

public sealed class WorkspaceCreationServiceTests
{
    [Fact]
    public async Task Missing_scope_cannot_create_or_retry()
    {
        var repository = new FakeRepository();
        var service = new WorkspaceCreationService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string>(), true);
        var result = await service.CreateAsync(new(actor, "Name", "slug", WorkspaceType.Code, null), default);
        var retry = await service.RetryAsync(Guid.NewGuid(), actor, default);
        Assert.Equal(WorkspaceCreationFailure.Unauthorized, result.Failure);
        Assert.Equal(WorkspaceCreationFailure.Unauthorized, retry.Failure);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Invalid_domain_input_does_not_reserve_workspace()
    {
        var repository = new FakeRepository();
        var service = new WorkspaceCreationService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string> { "workspaces.manage" }, false);
        var result = await service.CreateAsync(new(actor, "Name", "../bad", WorkspaceType.Code, null), default);
        Assert.Equal(WorkspaceCreationFailure.InvalidInput, result.Failure);
        Assert.Equal(0, repository.Calls);
    }

    private sealed class FakeRepository : IWorkspaceCreationRepository
    {
        public int Calls { get; private set; }
        public Task<WorkspaceCreationResult> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.Conflict));
        }
        public Task<WorkspaceCreationResult> RetryAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.NotFound));
        }
        public Task<IReadOnlyList<WorkspaceSummary>?> ListPendingAsync(Guid userId, int offset, int count, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<WorkspaceSummary>?>([]);
        }
    }
}
