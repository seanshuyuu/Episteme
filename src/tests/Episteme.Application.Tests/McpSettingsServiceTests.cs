using Episteme.Application.Workspaces;

namespace Episteme.Application.Tests;

public sealed class McpSettingsServiceTests
{
    [Fact]
    public async Task Invalid_ids_and_permissions_are_rejected_before_repository_access()
    {
        var service = new McpSettingsService(new UnusedRepository());
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string> { "tokens.manage" }, false);
        var workspaceId = Guid.NewGuid();

        Assert.Equal(McpSettingsFailure.InvalidInput, (await service.GetWorkspaceAsync(actor, Guid.Empty, default)).Failure);
        Assert.Equal(McpSettingsFailure.InvalidInput, (await service.SetWorkspaceEnabledAsync(actor, Guid.Empty, true, default)).Failure);
        Assert.Equal(McpSettingsFailure.InvalidInput, (await service.SetWorkspaceAsync(actor, workspaceId, McpPermission.CreateWorkspace, default)).Failure);
        Assert.Equal(McpSettingsFailure.InvalidInput, (await service.SetMemberGrantAsync(actor, workspaceId, Guid.Empty, McpPermission.ReadDocument, default)).Failure);
        Assert.Equal(McpSettingsFailure.InvalidInput, (await service.SetMemberGrantAsync(actor, workspaceId, Guid.NewGuid(), (McpPermission)(1 << 20), default)).Failure);
    }

    [Fact]
    public async Task Missing_scope_is_unauthorized_even_when_input_is_invalid()
    {
        var service = new McpSettingsService(new UnusedRepository());
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string>(), false);

        Assert.Equal(McpSettingsFailure.Unauthorized, (await service.GetWorkspaceAsync(actor, Guid.Empty, default)).Failure);
        Assert.Equal(McpSettingsFailure.Unauthorized, (await service.SetMemberGrantAsync(actor, Guid.Empty, Guid.Empty, McpPermission.None, default)).Failure);
    }

    private sealed class UnusedRepository : IMcpSettingsRepository
    {
        public Task<McpSettingsResult<McpPersonalSettings>> GetPersonalAsync(WorkspaceActor actor, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<McpSettingsResult<McpPersonalSettings>> SetPersonalAsync(WorkspaceActor actor, McpPersonalSettings settings, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<McpSettingsResult<McpWorkspaceSettingsView>> GetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<McpSettingsResult<McpWorkspaceSettingsView>> SetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, McpPermission permissions, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<McpSettingsResult<bool>> SetWorkspaceEnabledAsync(WorkspaceActor actor, Guid workspaceId, bool enabled, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<McpSettingsResult<McpPermission>> GetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<McpSettingsResult<McpPermission>> SetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, McpPermission permissions, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
