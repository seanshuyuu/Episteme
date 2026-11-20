using Episteme.Domain.Workspaces;
using System.Text.Json.Serialization;

namespace Episteme.Application.Workspaces;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceManifest(int FormatVersion, string Name, WorkspaceType Type, string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkspaceConfiguration? Configuration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Directories = null);
public sealed record WorkspaceManifestExportResult(WorkspaceManifest? Manifest, WorkspaceAccessFailure? Failure);

public interface IWorkspaceManifestRepository
{
    Task<WorkspaceManifestExportResult> ExportWithDirectoriesAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken);
}

public interface IWorkspaceManifestService
{
    Task<WorkspaceManifestExportResult> ExportAsync(Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken, int formatVersion = 1);
    Task<WorkspaceCreationResult> ImportAsync(WorkspaceActor actor, string slug, WorkspaceManifest manifest, CancellationToken cancellationToken);
}

public sealed class WorkspaceManifestService(WorkspaceAccessService access, IWorkspaceCreationService creation,
    IWorkspaceManifestRepository manifests)
    : IWorkspaceManifestService
{
    public async Task<WorkspaceManifestExportResult> ExportAsync(
        Guid workspaceId, WorkspaceActor actor, CancellationToken cancellationToken, int formatVersion = 1)
    {
        if (formatVersion is not (1 or 2 or 3))
            return new(null, WorkspaceAccessFailure.InvalidInput);
        if (formatVersion == 3)
        {
            if (workspaceId == Guid.Empty || actor.UserId == Guid.Empty)
                return new(null, WorkspaceAccessFailure.NotFound);
            if (!actor.Scopes.Contains("workspaces.read"))
                return new(null, WorkspaceAccessFailure.MissingScope);
            return await manifests.ExportWithDirectoriesAsync(workspaceId, actor, cancellationToken);
        }
        var result = await access.AuthorizeAsync(new WorkspaceAccessRequest(
            workspaceId, actor.UserId, actor.Scopes, actor.IsSystemAdministrator, WorkspacePermission.Read, actor.IsMcp), cancellationToken);
        if (!result.Succeeded)
            return new(null, result.Failure);
        var workspace = result.Workspace!;
        return new(new WorkspaceManifest(formatVersion, workspace.Name, workspace.Type, workspace.Description,
            formatVersion == 2 ? workspace.Configuration ?? WorkspaceConfiguration.Empty : null), null);
    }

    public Task<WorkspaceCreationResult> ImportAsync(
        WorkspaceActor actor, string slug, WorkspaceManifest manifest, CancellationToken cancellationToken)
    {
        if (actor.UserId == Guid.Empty || !actor.Scopes.Contains("workspaces.manage"))
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.Unauthorized));
        if (manifest is null || manifest.FormatVersion is not (1 or 2 or 3) ||
            (manifest.FormatVersion == 1 && manifest.Configuration is not null) ||
            (manifest.FormatVersion != 3 && manifest.Directories is not null) ||
            (manifest.FormatVersion is 2 or 3 && (manifest.Configuration is null || !manifest.Configuration.IsValid())) ||
            (manifest.FormatVersion == 3 && !WorkspaceDirectorySkeleton.TryNormalize(manifest.Directories, out _)))
            return Task.FromResult(new WorkspaceCreationResult(null, WorkspaceCreationFailure.InvalidInput));
        return creation.CreateAsync(new CreateWorkspaceCommand(
            actor, manifest.Name, slug, manifest.Type, manifest.Description, manifest.Configuration, manifest.Directories), cancellationToken);
    }
}
