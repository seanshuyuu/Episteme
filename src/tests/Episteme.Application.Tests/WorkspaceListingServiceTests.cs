using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;

namespace Episteme.Application.Tests;

public sealed class WorkspaceListingServiceTests
{
    private readonly FakeRepository _repository = new();
    private readonly WorkspaceListingService _service;
    private readonly Guid _actor = Guid.NewGuid();

    public WorkspaceListingServiceTests() => _service = new(_repository);

    [Fact]
    public async Task Page_uses_one_extra_item_to_report_more()
    {
        _repository.Items = Enumerable.Range(0, 3)
            .Select(i => new WorkspaceSummary(Guid.NewGuid(), $"Name {i}", $"slug-{i}", WorkspaceType.Custom, null, "main"))
            .ToArray();

        var result = await _service.ListAsync(Request(offset: 4, limit: 2), default);

        Assert.Null(result.Failure);
        Assert.Equal(2, result.Items!.Count);
        Assert.True(result.HasMore);
        Assert.Equal(4, _repository.Offset);
        Assert.Equal(3, _repository.Count);
        Assert.Equal(_actor, _repository.UserId);
    }

    [Fact]
    public async Task Empty_page_is_successful()
    {
        _repository.Items = [];
        var result = await _service.ListAsync(Request(), default);
        Assert.Null(result.Failure);
        Assert.Empty(result.Items!);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Inactive_user_is_not_returned_as_an_empty_list()
    {
        _repository.Items = null;
        var result = await _service.ListAsync(Request(), default);
        Assert.Equal(WorkspaceListingFailure.InactiveUser, result.Failure);
        Assert.Null(result.Items);
    }

    [Fact]
    public async Task Read_scope_is_required_before_database_access_even_for_administrator()
    {
        var request = Request() with { Scopes = new HashSet<string>(), IsSystemAdministrator = true };
        var result = await _service.ListAsync(request, default);
        Assert.Equal(WorkspaceListingFailure.Unauthorized, result.Failure);
        Assert.Equal(0, _repository.Calls);
    }

    [Theory]
    [InlineData(-1, 25)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    [InlineData(int.MaxValue, 1)]
    public async Task Invalid_page_is_rejected_without_database_access(int offset, int limit)
    {
        var result = await _service.ListAsync(Request(offset, limit), default);
        Assert.Equal(WorkspaceListingFailure.InvalidPage, result.Failure);
        Assert.Equal(0, _repository.Calls);
    }

    private WorkspaceListingRequest Request(int offset = 0, int limit = 25) =>
        new(_actor, new HashSet<string>(StringComparer.Ordinal) { "workspaces.read" }, false, offset, limit);

    private sealed class FakeRepository : IWorkspaceListingRepository
    {
        public IReadOnlyList<WorkspaceSummary>? Items { get; set; }
        public int Calls { get; private set; }
        public int Offset { get; private set; }
        public int Count { get; private set; }
        public Guid UserId { get; private set; }

        public Task<IReadOnlyList<WorkspaceSummary>?> ListForActiveUserAsync(
            Guid userId, bool isSystemAdministrator, int offset, int count, CancellationToken cancellationToken)
        {
            Calls++;
            Offset = offset;
            Count = count;
            UserId = userId;
            return Task.FromResult(Items);
        }
    }
}
