using Episteme.Application.Workspaces;

namespace Episteme.Application.Tests;

public sealed class WorkspaceContentServiceTests
{
    [Theory]
    [InlineData("../secret")]
    [InlineData(".git/config")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    public async Task Invalid_paths_never_reach_repository(string path)
    {
        var repository = new FakeRepository();
        var service = new WorkspaceContentService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string> { "workspaces.read" }, false);
        Assert.Equal(WorkspaceContentFailure.InvalidInput,
            (await service.ReadAsync(Guid.NewGuid(), actor, path, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.InvalidInput,
            (await service.ReadBinaryAsync(Guid.NewGuid(), actor, path, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.InvalidInput,
            (await service.ListAsync(Guid.NewGuid(), actor, path, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.InvalidInput,
            (await service.HistoryAsync(Guid.NewGuid(), actor, path, 0, 25, default)).Failure);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Scope_is_required_even_for_administrators()
    {
        var repository = new FakeRepository();
        var service = new WorkspaceContentService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string>(), true);
        Assert.Equal(WorkspaceContentFailure.Unauthorized,
            (await service.ListAsync(Guid.NewGuid(), actor, "", default)).Failure);
        Assert.Equal(WorkspaceContentFailure.Unauthorized,
            (await service.ReadAsync(Guid.NewGuid(), actor, "a", default)).Failure);
        Assert.Equal(WorkspaceContentFailure.Unauthorized,
            (await service.ReadBinaryAsync(Guid.NewGuid(), actor, "a", default)).Failure);
        Assert.Equal(WorkspaceContentFailure.Unauthorized,
            (await service.HistoryAsync(Guid.NewGuid(), actor, "a", 0, 25, default)).Failure);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Writes_require_scope_and_validate_input_and_size()
    {
        var repository = new FakeRepository();
        var service = new WorkspaceContentService(repository);
        var id = Guid.NewGuid();
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string> { "workspaces.write" }, false);
        var head = new string('a', 40);
        var denied = new WorkspaceWriteCommand(id, new WorkspaceActor(actor.UserId, new HashSet<string>(), true),
            "a.txt", head, WorkspaceWriteOperation.Put, "text");
        Assert.Equal(WorkspaceContentFailure.Unauthorized, (await service.WriteAsync(denied, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.InvalidInput, (await service.WriteAsync(
            denied with { Actor = actor, Path = "../escape" }, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.InvalidInput, (await service.WriteAsync(
            denied with { Actor = actor, ExpectedHead = "invalid" }, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.InvalidInput, (await service.WriteAsync(
            denied with { Actor = actor, Content = "\ud800" }, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.TooLarge, (await service.WriteAsync(
            denied with { Actor = actor, Content = new string('x', 1024 * 1024 + 1) }, default)).Failure);
        Assert.Equal(0, repository.Calls);
        Assert.Null((await service.WriteAsync(denied with
        {
            Actor = actor,
            Content = new string('x', 1024 * 1024)
        }, default)).Failure);
        Assert.Equal(1, repository.Calls);
    }

    [Fact]
    public async Task Binary_upload_enforces_scope_and_size_limit()
    {
        var repository = new FakeRepository();
        var service = new WorkspaceContentService(repository);
        var actor = new WorkspaceActor(Guid.NewGuid(), new HashSet<string> { "workspaces.write" }, false);
        var command = new WorkspaceWriteCommand(Guid.NewGuid(), actor, "file.bin", new string('a', 40),
            WorkspaceWriteOperation.PutBinary, BinaryContent: new byte[1024 * 1024 + 1]);
        Assert.Equal(WorkspaceContentFailure.TooLarge, (await service.WriteAsync(command, default)).Failure);
        Assert.Equal(WorkspaceContentFailure.InvalidInput, (await service.WriteAsync(command with { BinaryContent = null }, default)).Failure);
        Assert.Equal(0, repository.Calls);
        Assert.Null((await service.WriteAsync(command with { BinaryContent = new byte[1024 * 1024] }, default)).Failure);
        Assert.Equal(1, repository.Calls);
    }

    private sealed class FakeRepository : IWorkspaceContentRepository
    {
        public int Calls { get; private set; }
        public Task<WorkspaceDirectoryResult> ListAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceDirectoryResult("commit", [], null));
        }

        public Task<WorkspaceDocumentResult> ReadAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceDocumentResult("commit", "", null));
        }

        public Task<WorkspaceBinaryDocumentResult> ReadBinaryAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceBinaryDocumentResult("commit", [], null));
        }

        public Task<WorkspaceDocumentResult> ReadVersionAsync(Guid workspaceId, WorkspaceActor actor, string path, string commitId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceDocumentResult(commitId, "", null));
        }

        public Task<WorkspaceHistoryResult> HistoryAsync(Guid workspaceId, WorkspaceActor actor, string path, int offset, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceHistoryResult("commit", [], false, null));
        }

        public Task<WorkspaceWriteResult> WriteAsync(WorkspaceWriteCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new WorkspaceWriteResult("commit", null));
        }
    }
}
