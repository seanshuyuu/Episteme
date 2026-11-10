namespace Episteme.UI.Avalonia.ViewModels;

public sealed class SpaceItem
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Display => string.IsNullOrWhiteSpace(Name) ? Slug : Name;
}

public sealed class NodeItem
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ContentTypeId { get; set; }
    public int Depth => Path.Count(c => c == '/');
    public string Indent => new('　', Math.Max(0, Depth - 1) * 2);
    public string Display => Indent + (string.IsNullOrWhiteSpace(Name) ? Path : Name);
}

public sealed class DocItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string TypeSlug { get; set; } = "";
    public string Display => (string.IsNullOrWhiteSpace(Title) ? Id : Title) +
                             (string.IsNullOrWhiteSpace(TypeSlug) ? "" : " · " + TypeSlug);
}

public sealed class CommitItem
{
    public string Id { get; set; } = "";
    public string Message { get; set; } = "";
    public string Source { get; set; } = "";
    public string Display => (Message.Length > 34 ? Message[..34] + "…" : Message) +
                             " · " + Source + " · " + Id[..Math.Min(8, Id.Length)];
}

public sealed class PackItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int NodeCount { get; set; }
    public int ContentTypeCount { get; set; }
    public bool Valid { get; set; }
    public string Display => (string.IsNullOrWhiteSpace(Name) ? Id : Name) +
                             $" · {NodeCount} 目录 / {ContentTypeCount} 类型" + (Valid ? "" : " (校验失败)");
}

public sealed class TypeItem
{
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    public int FieldCount { get; set; }
    public string Display => (string.IsNullOrWhiteSpace(Label) ? Slug : Label) + $" · {FieldCount} 字段";
}

public sealed class ToolItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Display => Name;
}

public sealed class AnnotationItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string Display => $"[{Kind}] " + (Text.Length > 40 ? Text[..40] + "…" : Text);
}
