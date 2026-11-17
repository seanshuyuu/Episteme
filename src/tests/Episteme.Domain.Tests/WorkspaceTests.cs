using Episteme.Domain.Workspaces;

namespace Episteme.Domain.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void Create_assigns_server_storage_key_and_active_defaults()
    {
        var userId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var workspace = Workspace.Create(userId, "  Research Notes  ", "research-notes", WorkspaceType.Research, now: createdAt);

        Assert.Equal("Research Notes", workspace.Name);
        Assert.Equal("research-notes", workspace.Slug);
        Assert.Equal(userId, workspace.CreatedByUserId);
        Assert.Equal(WorkspaceStatus.Active, workspace.Status);
        Assert.Equal("main", workspace.DefaultBranch);
        Assert.Equal("{}", workspace.ConfigurationJson);
        Assert.Equal(createdAt, workspace.CreatedAt);
        Assert.Equal(32, workspace.StorageKey.Length);
        Assert.DoesNotContain('/', workspace.StorageKey);
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("Mixed-Case")]
    [InlineData("two--words")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    public void Create_rejects_invalid_slugs(string slug)
    {
        Assert.Throws<ArgumentException>(() =>
            Workspace.Create(Guid.NewGuid(), "Workspace", slug, WorkspaceType.Custom));
    }

    [Fact]
    public void CreateOwner_creates_active_owner_member()
    {
        var member = WorkspaceMember.CreateOwner(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(WorkspaceRole.Owner, member.Role);
        Assert.Equal(WorkspaceMemberStatus.Active, member.Status);
        Assert.Equal(member.UserId, member.AddedByUserId);
    }

    [Fact]
    public void Add_rejects_an_additional_owner_without_owner_transfer_workflow()
    {
        Assert.Throws<ArgumentException>(() =>
            WorkspaceMember.Add(Guid.NewGuid(), Guid.NewGuid(), WorkspaceRole.Owner, Guid.NewGuid()));
    }
}
