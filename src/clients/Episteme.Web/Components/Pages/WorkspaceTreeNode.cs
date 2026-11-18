namespace Episteme.Web.Components.Pages;

public sealed class WorkspaceTreeNode(string name, string path, string kind)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Kind { get; } = kind;
    public bool IsDirectory => string.Equals(Kind, "Directory", StringComparison.OrdinalIgnoreCase);
    public bool IsExpanded { get; set; }
    public bool IsLoaded { get; set; }
    public List<WorkspaceTreeNode> Children { get; } = [];
}
