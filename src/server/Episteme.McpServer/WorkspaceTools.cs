using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Episteme.McpServer;

[McpServerToolType]
public sealed class WorkspaceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "list_workspaces"), Description("List workspaces accessible to the authenticated user.")]
    public static async Task<CallToolResult> ListWorkspaces(
        ClaimsPrincipal principal, IWorkspaceListingService service, int offset = 0, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ListAsync(new(actor!.UserId, actor.Scopes, actor.IsSystemAdministrator, offset, limit), cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "get_workspace"), Description("Get metadata for an accessible workspace.")]
    public static async Task<CallToolResult> GetWorkspace(
        Guid workspaceId, ClaimsPrincipal principal, WorkspaceAccessService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.AuthorizeAsync(new(workspaceId, actor!.UserId, actor.Scopes,
            actor.IsSystemAdministrator, WorkspacePermission.Read), cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result.Workspace);
    }

    [McpServerTool(Name = "create_workspace"), Description("Create a workspace and initialize its Git repository.")]
    public static async Task<CallToolResult> CreateWorkspace(
        string name, string slug, WorkspaceType type, ClaimsPrincipal principal, IWorkspaceCreationService service,
        string? description = null, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.CreateAsync(new(actor!, name, slug, type, description), cancellationToken);
        return CreationResult(result);
    }

    [McpServerTool(Name = "list_pending_workspaces"), Description("List the caller's workspaces awaiting Git provisioning.")]
    public static async Task<CallToolResult> ListPendingWorkspaces(
        ClaimsPrincipal principal, IWorkspaceCreationService service, int offset = 0, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ListPendingAsync(actor!, offset, limit, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "retry_workspace_provisioning"), Description("Retry Git provisioning for a pending workspace.")]
    public static async Task<CallToolResult> RetryProvisioning(
        Guid workspaceId, ClaimsPrincipal principal, IWorkspaceCreationService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        return CreationResult(await service.RetryAsync(workspaceId, actor!, cancellationToken));
    }

    [McpServerTool(Name = "update_workspace"), Description("Update workspace metadata using its expected concurrency version.")]
    public static async Task<CallToolResult> UpdateWorkspace(
        Guid workspaceId, Guid expectedVersion, string name, WorkspaceType type, ClaimsPrincipal principal,
        IWorkspaceManagementService service, string? description = null, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.UpdateAsync(new(workspaceId, actor!, expectedVersion, name, type, description), cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result.Workspace);
    }

    [McpServerTool(Name = "delete_workspace"), Description("Archive a workspace using its expected concurrency version.")]
    public static async Task<CallToolResult> ArchiveWorkspace(
        Guid workspaceId, Guid expectedVersion, ClaimsPrincipal principal, IWorkspaceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ArchiveAsync(workspaceId, actor!, expectedVersion, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(new { archived = true });
    }

    [McpServerTool(Name = "list_workspace_members"), Description("List active workspace members; requires workspace management permission.")]
    public static async Task<CallToolResult> ListMembers(Guid workspaceId, ClaimsPrincipal principal,
        IWorkspaceMemberService service, int offset = 0, int limit = 25, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ListAsync(workspaceId, actor!, offset, limit, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "set_workspace_member"), Description("Add or update a workspace member's role.")]
    public static async Task<CallToolResult> SetMember(Guid workspaceId, Guid memberId, WorkspaceRole role,
        ClaimsPrincipal principal, IWorkspaceMemberService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.SetAsync(workspaceId, memberId, role, actor!, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result.Member);
    }

    [McpServerTool(Name = "remove_workspace_member"), Description("Remove a workspace member while preserving at least one owner.")]
    public static async Task<CallToolResult> RemoveMember(Guid workspaceId, Guid memberId,
        ClaimsPrincipal principal, IWorkspaceMemberService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.RemoveAsync(workspaceId, memberId, actor!, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(new { removed = true });
    }

    [McpServerTool(Name = "get_workspace_settings"), Description("Get the authenticated user's logical default working directory.")]
    public static async Task<CallToolResult> GetSettings(ClaimsPrincipal principal, IUserWorkspaceSettingsService service,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.GetAsync(actor!.UserId, actor.Scopes, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "set_workspace_settings"), Description("Set the authenticated user's logical default working directory.")]
    public static async Task<CallToolResult> SetSettings(string directory, ClaimsPrincipal principal,
        IUserWorkspaceSettingsService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.SetAsync(actor!.UserId, actor.Scopes, directory, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "list_directory"), Description("List a committed workspace directory; empty path means root.")]
    public static async Task<CallToolResult> ListDirectory(
        Guid workspaceId, string path, ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ListAsync(workspaceId, actor!, path, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "read_document"), Description("Read the current committed UTF-8 document.")]
    public static async Task<CallToolResult> ReadDocument(
        Guid workspaceId, string path, ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ReadAsync(workspaceId, actor!, path, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "read_document_version"), Description("Read a document from an ancestor commit.")]
    public static async Task<CallToolResult> ReadDocumentVersion(
        Guid workspaceId, string path, string commitId, ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ReadVersionAsync(workspaceId, actor!, path, commitId, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "get_document_history"), Description("List commit IDs affecting a document path.")]
    public static async Task<CallToolResult> DocumentHistory(
        Guid workspaceId, string path, ClaimsPrincipal principal, IWorkspaceContentService service,
        int offset = 0, int limit = 25, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.HistoryAsync(workspaceId, actor!, path, offset, limit, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    [McpServerTool(Name = "put_document"), Description("Create or replace a document, requiring the current Git HEAD.")]
    public static Task<CallToolResult> PutDocument(Guid workspaceId, string path, string expectedHead, string content,
        ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken) =>
        Write(workspaceId, path, expectedHead, WorkspaceWriteOperation.Put, principal, service, cancellationToken, content: content);

    [McpServerTool(Name = "delete_document"), Description("Delete a document, requiring the current Git HEAD.")]
    public static Task<CallToolResult> DeleteDocument(Guid workspaceId, string path, string expectedHead,
        ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken) =>
        Write(workspaceId, path, expectedHead, WorkspaceWriteOperation.Delete, principal, service, cancellationToken);

    [McpServerTool(Name = "restore_document_version"), Description("Restore a document from an ancestor commit as a new commit.")]
    public static Task<CallToolResult> RestoreDocument(Guid workspaceId, string path, string expectedHead, string sourceCommit,
        ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken) =>
        Write(workspaceId, path, expectedHead, WorkspaceWriteOperation.Restore, principal, service, cancellationToken, sourceCommit: sourceCommit);

    [McpServerTool(Name = "move_document"), Description("Move a document, requiring the current Git HEAD.")]
    public static Task<CallToolResult> MoveDocument(Guid workspaceId, string path, string expectedHead, string destinationPath,
        ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken) =>
        Write(workspaceId, path, expectedHead, WorkspaceWriteOperation.MoveDocument, principal, service, cancellationToken, destinationPath: destinationPath);

    [McpServerTool(Name = "create_directory"), Description("Create an empty directory, requiring the current Git HEAD.")]
    public static Task<CallToolResult> CreateDirectory(Guid workspaceId, string path, string expectedHead,
        ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken) =>
        Write(workspaceId, path, expectedHead, WorkspaceWriteOperation.CreateDirectory, principal, service, cancellationToken);

    [McpServerTool(Name = "delete_directory"), Description("Delete a directory; recursive must be true to remove nonempty directories.")]
    public static Task<CallToolResult> DeleteDirectory(Guid workspaceId, string path, string expectedHead, bool recursive,
        ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken) =>
        Write(workspaceId, path, expectedHead, WorkspaceWriteOperation.DeleteDirectory, principal, service, cancellationToken, recursive: recursive);

    [McpServerTool(Name = "move_directory"), Description("Move a directory, requiring the current Git HEAD.")]
    public static Task<CallToolResult> MoveDirectory(Guid workspaceId, string path, string expectedHead, string destinationPath,
        ClaimsPrincipal principal, IWorkspaceContentService service, CancellationToken cancellationToken) =>
        Write(workspaceId, path, expectedHead, WorkspaceWriteOperation.MoveDirectory, principal, service, cancellationToken, destinationPath: destinationPath);

    [McpServerTool(Name = "export_workspace_config"), Description("Export a versioned non-sensitive workspace manifest.")]
    public static async Task<CallToolResult> ExportConfig(Guid workspaceId, ClaimsPrincipal principal,
        IWorkspaceManifestService service, CancellationToken cancellationToken, int formatVersion = 1)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.ExportAsync(workspaceId, actor!, cancellationToken, formatVersion);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result.Manifest);
    }

    [McpServerTool(Name = "set_workspace_configuration"), Description("Replace non-sensitive workspace settings using the expected metadata version.")]
    public static async Task<CallToolResult> SetConfiguration(Guid workspaceId, Guid expectedVersion,
        WorkspaceConfiguration configuration, ClaimsPrincipal principal, IWorkspaceManagementService service,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.SetConfigurationAsync(workspaceId, actor!, expectedVersion, configuration, cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString())
            : Success(new { configuration, concurrencyStamp = result.Workspace!.ConcurrencyStamp });
    }

    [McpServerTool(Name = "import_workspace_config"), Description("Create a workspace from a supported non-sensitive manifest.")]
    public static async Task<CallToolResult> ImportConfig(string slug, WorkspaceManifest manifest, ClaimsPrincipal principal,
        IWorkspaceManifestService service, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        return CreationResult(await service.ImportAsync(actor!, slug, manifest, cancellationToken));
    }

    private static async Task<CallToolResult> Write(Guid workspaceId, string path, string expectedHead,
        WorkspaceWriteOperation operation, ClaimsPrincipal principal, IWorkspaceContentService service,
        CancellationToken cancellationToken, string? content = null, string? sourceCommit = null,
        string? destinationPath = null, bool recursive = false)
    {
        if (!WorkspaceActorClaims.TryGetActor(principal, out var actor))
            return Error("Unauthenticated");
        var result = await service.WriteAsync(new(workspaceId, actor!, path, expectedHead, operation,
            content, sourceCommit, destinationPath, recursive), cancellationToken);
        return result.Failure is { } failure ? Error(failure.ToString()) : Success(result);
    }

    private static CallToolResult CreationResult(WorkspaceCreationResult result) =>
        result.Failure is { } failure
            ? Error(failure.ToString(), result.PendingWorkspaceId)
            : Success(result.Workspace);

    private static CallToolResult Error(string code, Guid? pendingWorkspaceId = null) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { code, pendingWorkspaceId }, JsonOptions) }] };

    private static CallToolResult Success<T>(T value) =>
        new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, JsonOptions) }] };
}
