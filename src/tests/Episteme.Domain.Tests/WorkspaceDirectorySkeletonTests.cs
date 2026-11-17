using Episteme.Domain.Workspaces;

namespace Episteme.Domain.Tests;

public sealed class WorkspaceDirectorySkeletonTests
{
    [Fact]
    public void Normalizes_parents_duplicates_and_unicode()
    {
        Assert.True(WorkspaceDirectorySkeleton.TryNormalize(["笔记/草稿", "笔记", "笔记/草稿"], out var paths));
        Assert.Equal(new[] { "笔记", "笔记/草稿" }, paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("a/.git/child")]
    [InlineData("a/.GITKEEP/child")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    public void Rejects_unsafe_paths(string path) =>
        Assert.False(WorkspaceDirectorySkeleton.TryNormalize([path], out _));

    [Fact]
    public void Enforces_directory_count_and_total_utf8_bytes()
    {
        Assert.True(WorkspaceDirectorySkeleton.TryNormalize(
            Enumerable.Range(0, 256).Select(i => $"d{i}").ToArray(), out _));
        Assert.False(WorkspaceDirectorySkeleton.TryNormalize(
            Enumerable.Range(0, 257).Select(i => $"d{i}").ToArray(), out _));
        Assert.False(WorkspaceDirectorySkeleton.TryNormalize(
            Enumerable.Range(0, 70).Select(i => $"{i:D2}{new string('界', 990)}").ToArray(), out _));
        Assert.False(WorkspaceDirectorySkeleton.TryNormalize(["bad\uD800"], out _));
    }
}
