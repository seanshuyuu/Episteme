using Episteme.Application.Workspaces;

namespace Episteme.Application.Tests;

public sealed class UserWorkspaceSettingsServiceTests
{
    private readonly FakeSettingsRepository _repository = new();
    private readonly UserWorkspaceSettingsService _service;
    private readonly Guid _userId = Guid.NewGuid();

    public UserWorkspaceSettingsServiceTests() => _service = new(_repository);

    [Fact]
    public async Task Active_user_can_save_and_read_own_directory()
    {
        var saved = await _service.SetAsync(_userId, Scopes("workspaces.manage"), "research/papers", default);
        var read = await _service.GetAsync(_userId, Scopes("workspaces.read"), default);

        Assert.Null(saved.Failure);
        Assert.Equal("research/papers", read.Directory);
        Assert.Equal(_userId, _repository.LastUserId);
    }

    [Fact]
    public async Task Invalid_path_is_rejected_without_writing()
    {
        var result = await _service.SetAsync(_userId, Scopes("workspaces.manage"), "../secret", default);

        Assert.Equal(UserSettingsFailure.InvalidDirectory, result.Failure);
        Assert.Equal(0, _repository.Writes);
    }

    [Fact]
    public async Task Scope_is_required_before_repository_access()
    {
        var read = await _service.GetAsync(_userId, Scopes(), default);
        var write = await _service.SetAsync(_userId, Scopes("workspaces.read"), "notes", default);

        Assert.Equal(UserSettingsFailure.Unauthorized, read.Failure);
        Assert.Equal(UserSettingsFailure.Unauthorized, write.Failure);
        Assert.Equal(0, _repository.Reads);
        Assert.Equal(0, _repository.Writes);
    }

    [Fact]
    public async Task Inactive_user_cannot_read_or_write()
    {
        _repository.Active = false;
        var read = await _service.GetAsync(_userId, Scopes("workspaces.read"), default);
        var write = await _service.SetAsync(_userId, Scopes("workspaces.manage"), "", default);

        Assert.Equal(UserSettingsFailure.InactiveUser, read.Failure);
        Assert.Equal(UserSettingsFailure.InactiveUser, write.Failure);
    }

    private static HashSet<string> Scopes(params string[] values) => new(values, StringComparer.Ordinal);

    private sealed class FakeSettingsRepository : IUserWorkspaceSettingsRepository
    {
        public bool Active { get; set; } = true;
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Guid LastUserId { get; private set; }
        private string _directory = "";

        public Task<string?> GetDirectoryForActiveUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            Reads++;
            LastUserId = userId;
            return Task.FromResult<string?>(Active ? _directory : null);
        }

        public Task<bool> SetDirectoryForActiveUserAsync(Guid userId, string directory, CancellationToken cancellationToken)
        {
            Writes++;
            LastUserId = userId;
            if (Active)
                _directory = directory;
            return Task.FromResult(Active);
        }
    }
}
