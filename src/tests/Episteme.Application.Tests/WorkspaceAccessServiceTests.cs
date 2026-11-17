using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;

namespace Episteme.Application.Tests;

public sealed class WorkspaceAccessServiceTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private readonly FakeWorkspaceRepository _repository = new();
    private readonly WorkspaceAccessService _service;

    public WorkspaceAccessServiceTests()
    {
        _service = new WorkspaceAccessService(_repository);
        _repository.Snapshot = new WorkspaceAccessSnapshot(
            Workspace.Create(UserId, "Research", "research", WorkspaceType.Research),
            true,
            WorkspaceRole.Reader,
            true);
    }

    [Fact]
    public async Task Reader_with_read_scope_can_read_workspace_metadata()
    {
        var result = await _service.AuthorizeAsync(Request(Scopes("workspaces.read")));

        Assert.True(result.Succeeded);
        Assert.Equal("Research", result.Workspace!.Name);
    }

    [Fact]
    public async Task Missing_scope_denies_access_before_repository_lookup()
    {
        var result = await _service.AuthorizeAsync(Request(Scopes()));

        Assert.Equal(WorkspaceAccessFailure.MissingScope, result.Failure);
        Assert.Equal(0, _repository.LookupCount);
    }

    [Fact]
    public async Task Nonmember_is_denied_even_with_scope()
    {
        _repository.Snapshot = _repository.Snapshot! with { IsMembershipActive = false, Role = null };

        var result = await _service.AuthorizeAsync(Request(Scopes("workspaces.read")));

        Assert.Equal(WorkspaceAccessFailure.MembershipRequired, result.Failure);
    }

    [Fact]
    public async Task Reader_cannot_manage_workspace_even_with_manage_scope()
    {
        var result = await _service.AuthorizeAsync(Request(Scopes("workspaces.manage"), WorkspacePermission.Manage));

        Assert.Equal(WorkspaceAccessFailure.InsufficientRole, result.Failure);
    }

    [Fact]
    public async Task Administrator_still_requires_oauth_scope_but_bypasses_member_role()
    {
        var request = Request(Scopes("workspaces.manage"), WorkspacePermission.Manage) with
        {
            IsSystemAdministrator = true
        };
        _repository.Snapshot = _repository.Snapshot! with { IsMembershipActive = false, Role = null };

        var result = await _service.AuthorizeAsync(request);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Archived_workspace_is_not_exposed()
    {
        var workspace = Workspace.Create(UserId, "Research", "research", WorkspaceType.Research);
        workspace.Archive();
        _repository.Snapshot = new WorkspaceAccessSnapshot(workspace, true, WorkspaceRole.Owner, true);

        var result = await _service.AuthorizeAsync(Request(Scopes("workspaces.read")));

        Assert.Equal(WorkspaceAccessFailure.NotFound, result.Failure);
    }

    [Fact]
    public async Task Disabled_account_cannot_use_an_old_bearer_identity_for_workspace_access()
    {
        _repository.Snapshot = _repository.Snapshot! with { IsUserActive = false };

        var result = await _service.AuthorizeAsync(Request(Scopes("workspaces.read")));

        Assert.Equal(WorkspaceAccessFailure.UserInactive, result.Failure);
    }

    private static WorkspaceAccessRequest Request(
        IReadOnlySet<string> scopes,
        WorkspacePermission permission = WorkspacePermission.Read) =>
        new(WorkspaceId, UserId, scopes, false, permission);

    private static IReadOnlySet<string> Scopes(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private sealed class FakeWorkspaceRepository : IWorkspaceAccessRepository
    {
        public WorkspaceAccessSnapshot? Snapshot { get; set; }
        public int LookupCount { get; private set; }

        public Task<WorkspaceAccessSnapshot?> GetAccessSnapshotAsync(
            Guid workspaceId,
            Guid userId,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            return Task.FromResult(Snapshot);
        }
    }
}
