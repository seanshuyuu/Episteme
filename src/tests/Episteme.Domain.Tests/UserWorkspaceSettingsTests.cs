using Episteme.Domain.Workspaces;

namespace Episteme.Domain.Tests;

public sealed class UserWorkspaceSettingsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("research")]
    [InlineData("research/论文")]
    public void Accepts_workspace_relative_paths(string path) =>
        Assert.True(UserWorkspaceSettings.IsValidDirectory(path));

    [Theory]
    [InlineData(null)]
    [InlineData("/etc")]
    [InlineData("../other")]
    [InlineData("one/../two")]
    [InlineData("one/./two")]
    [InlineData("one//two")]
    [InlineData("one/")]
    [InlineData(".git/config")]
    [InlineData("folder/.GIT/config")]
    [InlineData("C:/Windows")]
    [InlineData(@"folder\file")]
    [InlineData("folder/\nfile")]
    public void Rejects_unsafe_paths(string? path) =>
        Assert.False(UserWorkspaceSettings.IsValidDirectory(path));

    [Fact]
    public void Rejects_paths_over_database_limit() =>
        Assert.False(UserWorkspaceSettings.IsValidDirectory(new string('a', 1025)));
}
