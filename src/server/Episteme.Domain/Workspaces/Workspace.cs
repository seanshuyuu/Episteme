namespace Episteme.Domain.Workspaces;

public sealed class Workspace
{
    private Workspace() { } // EF Core materialization.

    private Workspace(
        Guid id,
        Guid createdByUserId,
        string name,
        string slug,
        WorkspaceType type,
        string? description,
        string storageKey,
        string defaultBranch,
        DateTimeOffset createdAt)
    {
        Id = id;
        CreatedByUserId = createdByUserId;
        Name = name;
        Slug = slug;
        Type = type;
        Description = description;
        StorageKey = storageKey;
        DefaultBranch = defaultBranch;
        Status = WorkspaceStatus.Active;
        ConfigurationSchemaVersion = 1;
        ConfigurationJson = "{}";
        ConcurrencyStamp = Guid.NewGuid();
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public WorkspaceType Type { get; private set; }
    public string? Description { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string DefaultBranch { get; private set; } = "main";
    public WorkspaceStatus Status { get; private set; }
    public int ConfigurationSchemaVersion { get; private set; }
    public string ConfigurationJson { get; private set; } = "{}";
    public string? ProvisioningDirectoriesJson { get; private set; }
    public Guid ConcurrencyStamp { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Workspace Create(
        Guid createdByUserId,
        string name,
        string slug,
        WorkspaceType type,
        string? description = null,
        DateTimeOffset? now = null)
    {
        if (createdByUserId == Guid.Empty)
            throw new ArgumentException("A creator is required.", nameof(createdByUserId));
        ValidateDetails(name, type, description);
        if (!IsValidSlug(slug))
            throw new ArgumentException("Slug must be lowercase ASCII words separated by single hyphens.", nameof(slug));

        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new Workspace(
            Guid.NewGuid(),
            createdByUserId,
            name.Trim(),
            slug,
            type,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Guid.NewGuid().ToString("N"),
            "main",
            timestamp);
    }

    public static Workspace BeginProvisioning(Guid userId, string name, string slug, WorkspaceType type, string? description)
    {
        var workspace = Create(userId, name, slug, type, description);
        workspace.Status = WorkspaceStatus.Provisioning;
        return workspace;
    }

    public void CompleteProvisioning()
    {
        if (Status != WorkspaceStatus.Provisioning)
            throw new InvalidOperationException("Workspace is not being provisioned.");
        Status = WorkspaceStatus.Active;
        ProvisioningDirectoriesJson = null;
        Touch(null);
    }

    public void SetProvisioningDirectories(IReadOnlyList<string> directories)
    {
        if (Status != WorkspaceStatus.Provisioning ||
            !WorkspaceDirectorySkeleton.TryNormalize(directories, out var normalized))
            throw new ArgumentException("Invalid provisioning directory skeleton.", nameof(directories));
        ProvisioningDirectoriesJson = System.Text.Json.JsonSerializer.Serialize(normalized);
    }

    public void Archive(DateTimeOffset? now = null)
    {
        if (Status == WorkspaceStatus.Archived)
            return;

        Status = WorkspaceStatus.Archived;
        Touch(now);
    }

    public void UpdateDetails(string name, WorkspaceType type, string? description, DateTimeOffset? now = null)
    {
        if (Status != WorkspaceStatus.Active)
            throw new InvalidOperationException("Archived workspaces cannot be modified.");
        ValidateDetails(name, type, description);
        Name = name.Trim();
        Type = type;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Touch(now);
    }

    public void SetConfiguration(WorkspaceConfiguration configuration, DateTimeOffset? now = null)
    {
        if (Status is not (WorkspaceStatus.Active or WorkspaceStatus.Provisioning))
            throw new InvalidOperationException("Archived workspaces cannot be modified.");
        ArgumentNullException.ThrowIfNull(configuration);
        ConfigurationJson = configuration.ToJson();
        ConfigurationSchemaVersion = configuration.SchemaVersion;
        Touch(now);
    }

    private static void ValidateDetails(string name, WorkspaceType type, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new ArgumentException("Workspace name must contain 1-120 characters.", nameof(name));
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type), "Workspace type is not supported.");
        if (description?.Length > 2000)
            throw new ArgumentException("Description cannot exceed 2000 characters.", nameof(description));
    }

    private void Touch(DateTimeOffset? now)
    {
        UpdatedAt = now ?? DateTimeOffset.UtcNow;
        // EF Core uses this application-managed token as an optimistic concurrency check.
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
