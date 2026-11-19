namespace Episteme.Domain.Workspaces;

public sealed class Project
{
    private Project() { }

    private Project(Guid id, Guid workspaceId, Guid createdByUserId, string name, string slug,
        string? description, string storageKey, DateTimeOffset now)
    {
        Id = id;
        WorkspaceId = workspaceId;
        CreatedByUserId = createdByUserId;
        Name = name;
        Slug = slug;
        Description = description;
        StorageKey = storageKey;
        DefaultBranch = "main";
        Status = ProjectStatus.Working;
        ConcurrencyStamp = Guid.NewGuid();
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string DefaultBranch { get; private set; } = "main";
    public ProjectStatus Status { get; private set; }
    public Guid ConcurrencyStamp { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Project Create(Guid workspaceId, Guid createdByUserId, string name, string slug,
        string? description, DateTimeOffset? now = null)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        if (createdByUserId == Guid.Empty)
            throw new ArgumentException("Creator is required.", nameof(createdByUserId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new ArgumentException("Project name must contain 1-120 characters.", nameof(name));
        if (!IsValidSlug(slug))
            throw new ArgumentException("Project slug must be lowercase ASCII words separated by single hyphens.", nameof(slug));
        if (description?.Length > 2000)
            throw new ArgumentException("Description cannot exceed 2000 characters.", nameof(description));

        return new Project(Guid.NewGuid(), workspaceId, createdByUserId, name.Trim(), slug,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Guid.NewGuid().ToString("N"), now ?? DateTimeOffset.UtcNow);
    }

    public void Update(string name, string slug, string? description, DateTimeOffset? now = null)
    {
        if (Status == ProjectStatus.Discarded)
            throw new InvalidOperationException("Discarded projects cannot be modified.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new ArgumentException("Project name must contain 1-120 characters.", nameof(name));
        if (!IsValidSlug(slug))
            throw new ArgumentException("Project slug must be lowercase ASCII words separated by single hyphens.", nameof(slug));
        if (description?.Length > 2000)
            throw new ArgumentException("Description cannot exceed 2000 characters.", nameof(description));
        Name = name.Trim();
        Slug = slug;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Touch(now);
    }

    public void SetStatus(ProjectStatus status, DateTimeOffset? now = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        Touch(now);
    }

    public void Discard(DateTimeOffset? now = null) => SetStatus(ProjectStatus.Discarded, now);
    public void Archive(DateTimeOffset? now = null) => SetStatus(ProjectStatus.Archived, now);
    public void Restore(DateTimeOffset? now = null) => SetStatus(ProjectStatus.Working, now);

    private void Touch(DateTimeOffset? now)
    {
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
        ConcurrencyStamp = Guid.NewGuid();
    }

    private static bool IsValidSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 80 || slug[0] == '-' || slug[^1] == '-')
            return false;
        var previousWasHyphen = false;
        foreach (var character in slug)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                    return false;
                previousWasHyphen = true;
                continue;
            }
            if (character is < 'a' or > 'z' && character is < '0' or > '9')
                return false;
            previousWasHyphen = false;
        }
        return true;
    }
}
