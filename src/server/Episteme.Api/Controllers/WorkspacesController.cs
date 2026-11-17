using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/workspaces")]
public sealed class WorkspacesController(
    WorkspaceAccessService accessService,
    IWorkspaceListingService listingService,
    IWorkspaceManagementService managementService,
    IWorkspaceCreationService creationService,
    IWorkspaceMemberService memberService,
    IWorkspaceManifestService manifestService) : ControllerBase
{
    /// <summary>Reads the workspace's non-sensitive configuration and current metadata version.</summary>
    [HttpGet("{workspaceId:guid}/configuration")]
    [ProducesResponseType<WorkspaceConfigurationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConfiguration(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var administrator))
            return Unauthorized();
        var result = await accessService.AuthorizeAsync(new WorkspaceAccessRequest(workspaceId,
            userId, scopes, administrator, WorkspacePermission.Read), cancellationToken);
        if (result.Failure == WorkspaceAccessFailure.NotFound)
            return Problem(statusCode: 404, title: "Workspace not found");
        if (!result.Succeeded)
            return Problem(statusCode: 403, title: "Workspace access denied");
        return Ok(new WorkspaceConfigurationResponse(result.Workspace!.Configuration ?? WorkspaceConfiguration.Empty,
            result.Workspace.ConcurrencyStamp));
    }

    /// <summary>Replaces non-sensitive workspace configuration using the expected metadata version.</summary>
    [HttpPut("{workspaceId:guid}/configuration")]
    [ProducesResponseType<WorkspaceConfigurationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetConfiguration(Guid workspaceId, SetWorkspaceConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var administrator))
            return Unauthorized();
        var result = await managementService.SetConfigurationAsync(workspaceId,
            new WorkspaceActor(userId, scopes, administrator), request.ExpectedVersion, request.Configuration, cancellationToken);
        if (result.Failure is not null)
            return ManagementResponse(result, false);
        return Ok(new WorkspaceConfigurationResponse(request.Configuration, result.Workspace!.ConcurrencyStamp));
    }

    /// <summary>Exports the supported non-sensitive workspace manifest.</summary>
    [HttpGet("{workspaceId:guid}/manifest")]
    [ProducesResponseType<WorkspaceManifestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportManifest(Guid workspaceId, [FromQuery] int formatVersion = 1,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out var userId, out var scopes, out var administrator))
            return Unauthorized();
        var result = await manifestService.ExportAsync(workspaceId,
            new WorkspaceActor(userId, scopes, administrator), cancellationToken, formatVersion);
        if (result.Failure == WorkspaceAccessFailure.InvalidInput)
            return Problem(statusCode: 400, title: "Unsupported manifest version");
        if (result.Failure == WorkspaceAccessFailure.NotFound)
            return Problem(statusCode: 404, title: "Workspace not found");
        if (result.Failure == WorkspaceAccessFailure.TooLarge)
            return Problem(statusCode: 413, title: "Workspace directory tree is too large");
        if (result.Failure == WorkspaceAccessFailure.Unsupported)
            return Problem(statusCode: 422, title: "Workspace directory tree cannot be exported");
        if (result.Failure is not null)
            return Problem(statusCode: 403, title: "Workspace access denied");
        var manifest = result.Manifest!;
        return Ok(new WorkspaceManifestResponse(manifest.FormatVersion, manifest.Name,
            manifest.Type.ToString(), manifest.Description, manifest.Configuration, manifest.Directories));
    }

    /// <summary>Creates a new workspace from a supported manifest and a new unique slug.</summary>
    [HttpPost("import")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ImportManifest(ImportWorkspaceManifestRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var administrator))
            return Unauthorized();
        var input = request.Manifest;
        if (request.AdditionalProperties?.Count > 0 || input is null || input.AdditionalProperties?.Count > 0 ||
            !Enum.TryParse<WorkspaceType>(input.Type, true, out var type) || !Enum.IsDefined(type) ||
            !string.Equals(type.ToString(), input.Type, StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: 400, title: "Invalid workspace manifest");
        var result = await manifestService.ImportAsync(new WorkspaceActor(userId, scopes, administrator), request.Slug,
            new WorkspaceManifest(input.FormatVersion, input.Name, type, input.Description, input.Configuration, input.Directories), cancellationToken);
        return CreationResponse(result, true);
    }

    /// <summary>Lists active members of a workspace. Requires owner access.</summary>
    [HttpGet("{workspaceId:guid}/members")]
    [ProducesResponseType<WorkspaceMemberListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListMembers(Guid workspaceId, [FromQuery] int offset = 0,
        [FromQuery] int limit = 25, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        var result = await memberService.ListAsync(workspaceId,
            new WorkspaceActor(userId, scopes, isAdministrator), offset, limit, cancellationToken);
        if (result.Failure is not null)
            return MemberFailure(result.Failure);
        return Ok(new WorkspaceMemberListResponse(result.Items!.Select(ToMemberResponse).ToArray(), offset, limit, result.HasMore));
    }

    /// <summary>Adds, reactivates or changes the role of a workspace member.</summary>
    [HttpPut("{workspaceId:guid}/members/{memberId:guid}")]
    [ProducesResponseType<WorkspaceMemberResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMember(Guid workspaceId, Guid memberId, SetWorkspaceMemberRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        if (!Enum.TryParse<WorkspaceRole>(request.Role, true, out var role) || !Enum.IsDefined(role) ||
            !string.Equals(role.ToString(), request.Role, StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: 400, title: "Invalid workspace role");
        var result = await memberService.SetAsync(workspaceId, memberId, role,
            new WorkspaceActor(userId, scopes, isAdministrator), cancellationToken);
        return result.Failure is not null ? MemberFailure(result.Failure) : Ok(ToMemberResponse(result.Member!));
    }

    /// <summary>Removes a member while preserving at least one owner.</summary>
    [HttpDelete("{workspaceId:guid}/members/{memberId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMember(Guid workspaceId, Guid memberId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        var result = await memberService.RemoveAsync(workspaceId, memberId,
            new WorkspaceActor(userId, scopes, isAdministrator), cancellationToken);
        return result.Failure is not null ? MemberFailure(result.Failure) : NoContent();
    }

    private IActionResult MemberFailure(WorkspaceManagementFailure? failure) => failure switch
    {
        WorkspaceManagementFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid member input"),
        WorkspaceManagementFailure.Unauthorized => Problem(statusCode: 403, title: "Workspace access denied"),
        WorkspaceManagementFailure.NotFound => Problem(statusCode: 404, title: "Workspace, user or member not found"),
        WorkspaceManagementFailure.Conflict => Problem(statusCode: 409, title: "Workspace must retain an owner"),
        _ => Problem(statusCode: 500, title: "Invalid member operation result")
    };

    private static WorkspaceMemberResponse ToMemberResponse(WorkspaceMemberSummary member) =>
        new(member.UserId, member.Role.ToString(), member.JoinedAt);

    /// <summary>Creates a workspace and initializes its Git repository.</summary>
    [HttpPost]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateWorkspace(CreateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        if (!Enum.TryParse<WorkspaceType>(request.Type, true, out var type) || !Enum.IsDefined(type))
            return Problem(statusCode: 400, title: "Invalid workspace type");
        var result = await creationService.CreateAsync(new CreateWorkspaceCommand(
            new WorkspaceActor(userId, scopes, isAdministrator),
            request.Name, request.Slug, type, request.Description), cancellationToken);
        return CreationResponse(result, true);
    }

    /// <summary>Retries initialization of a workspace left in provisioning state.</summary>
    [HttpPost("{workspaceId:guid}/provision")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RetryProvisioning(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        var result = await creationService.RetryAsync(
            workspaceId, new WorkspaceActor(userId, scopes, isAdministrator), cancellationToken);
        return CreationResponse(result, false);
    }

    private IActionResult CreationResponse(WorkspaceCreationResult result, bool created)
    {
        if (result.Workspace is not null && result.Failure is null)
            return created
                ? CreatedAtAction(nameof(GetWorkspace), new { workspaceId = result.Workspace.Id }, ToResponse(result.Workspace))
                : Ok(ToResponse(result.Workspace));
        if (result.Failure == WorkspaceCreationFailure.ProvisioningFailed)
            return Problem(statusCode: 503, title: "Workspace repository provisioning failed",
                extensions: new Dictionary<string, object?> { ["workspaceId"] = result.PendingWorkspaceId });
        return result.Failure switch
        {
            WorkspaceCreationFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid workspace input"),
            WorkspaceCreationFailure.Unauthorized => Problem(statusCode: 403, title: "Workspace access denied"),
            WorkspaceCreationFailure.NotFound => Problem(statusCode: 404, title: "Pending workspace not found"),
            WorkspaceCreationFailure.Conflict => Problem(statusCode: 409, title: "Workspace slug already exists"),
            _ => Problem(statusCode: 500, title: "Invalid workspace operation result")
        };
    }

    /// <summary>Lists the caller's workspaces awaiting repository provisioning.</summary>
    [HttpGet("provisioning")]
    [ProducesResponseType<WorkspaceListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListPendingWorkspaces(
        [FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        var result = await creationService.ListPendingAsync(
            new WorkspaceActor(userId, scopes, isAdministrator), offset, limit, cancellationToken);
        if (result.Failure == WorkspaceListingFailure.InvalidPage)
            return Problem(statusCode: 400, title: "Invalid workspace page");
        if (result.Failure is not null)
            return Problem(statusCode: 403, title: "Workspace access denied");
        return Ok(new WorkspaceListResponse(result.Items!.Select(ToResponse).ToArray(), offset, limit, result.HasMore));
    }

    /// <summary>Lists active workspaces accessible to the authenticated user.</summary>
    [HttpGet]
    [ProducesResponseType<WorkspaceListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListWorkspaces(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();

        var result = await listingService.ListAsync(
            new WorkspaceListingRequest(userId, scopes, isAdministrator, offset, limit),
            cancellationToken);
        if (result.Failure == WorkspaceListingFailure.InvalidPage)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid workspace page");
        if (result.Failure is not null)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Workspace access denied");

        return Ok(new WorkspaceListResponse(
            result.Items!.Select(ToResponse).ToArray(),
            offset,
            limit,
            result.HasMore));
    }

    /// <summary>Returns metadata for an accessible active workspace.</summary>
    [HttpGet("{workspaceId:guid}")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkspace(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();

        var result = await accessService.AuthorizeAsync(
            new WorkspaceAccessRequest(
                workspaceId,
                userId,
                scopes,
                isAdministrator,
                WorkspacePermission.Read),
            cancellationToken);

        if (result.Succeeded)
        {
            var workspace = result.Workspace!;
            return Ok(ToResponse(workspace));
        }

        return result.Failure switch
        {
            WorkspaceAccessFailure.NotFound => Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Workspace not found"),
            _ => Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Workspace access denied")
        };
    }

    /// <summary>Updates workspace metadata using an expected concurrency stamp.</summary>
    [HttpPut("{workspaceId:guid}")]
    [ProducesResponseType<WorkspaceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateWorkspace(
        Guid workspaceId, UpdateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        if (!Enum.TryParse<WorkspaceType>(request.Type, true, out var type) || !Enum.IsDefined(type))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid workspace type");

        var result = await managementService.UpdateAsync(new UpdateWorkspaceCommand(
            workspaceId, new WorkspaceActor(userId, scopes, isAdministrator),
            request.ExpectedVersion, request.Name, type, request.Description), cancellationToken);
        return ManagementResponse(result, false);
    }

    /// <summary>Archives an active workspace without deleting its repository.</summary>
    [HttpPost("{workspaceId:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ArchiveWorkspace(
        Guid workspaceId, ArchiveWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var userId, out var scopes, out var isAdministrator))
            return Unauthorized();
        var result = await managementService.ArchiveAsync(
            workspaceId, new WorkspaceActor(userId, scopes, isAdministrator), request.ExpectedVersion, cancellationToken);
        return ManagementResponse(result, true);
    }

    private IActionResult ManagementResponse(WorkspaceManagementResult result, bool archived) => result.Failure switch
    {
        null when result.Workspace is not null => archived ? NoContent() : Ok(ToResponse(result.Workspace)),
        WorkspaceManagementFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid workspace input"),
        WorkspaceManagementFailure.NotFound => Problem(statusCode: 404, title: "Workspace not found"),
        WorkspaceManagementFailure.Conflict => Problem(statusCode: 409, title: "Workspace version conflict"),
        WorkspaceManagementFailure.Unauthorized => Problem(statusCode: 403, title: "Workspace access denied"),
        _ => Problem(statusCode: 500, title: "Invalid workspace operation result")
    };

    private bool TryGetActor(out Guid userId, out HashSet<string> scopes, out bool isAdministrator)
    {
        var valid = WorkspaceActorClaims.TryGetActor(User, out var actor);
        userId = actor?.UserId ?? Guid.Empty;
        scopes = actor?.Scopes.ToHashSet(StringComparer.Ordinal) ?? [];
        isAdministrator = actor?.IsSystemAdministrator ?? false;
        return valid;
    }

    private static WorkspaceResponse ToResponse(WorkspaceSummary workspace) => new(
        workspace.Id,
        workspace.Name,
        workspace.Slug,
        workspace.Type.ToString(),
        workspace.Description,
        workspace.DefaultBranch,
        workspace.ConcurrencyStamp);
}

/// <summary>Workspace metadata returned to authorized callers.</summary>
public sealed record WorkspaceResponse(
    Guid Id,
    string Name,
    string Slug,
    string Type,
    string? Description,
    string DefaultBranch,
    Guid ConcurrencyStamp);

/// <summary>A bounded page of workspaces visible to the authenticated caller.</summary>
public sealed record WorkspaceListResponse(
    IReadOnlyList<WorkspaceResponse> Items,
    int Offset,
    int Limit,
    bool HasMore);

/// <summary>Replacement metadata and the version observed by the caller.</summary>
public sealed record UpdateWorkspaceRequest(string Name, string Type, string? Description, Guid ExpectedVersion);

/// <summary>Creates a workspace with a unique slug and an isolated Git repository.</summary>
public sealed record CreateWorkspaceRequest(string Name, string Slug, string Type, string? Description);

/// <summary>The version that must still be current before archiving.</summary>
public sealed record ArchiveWorkspaceRequest(Guid ExpectedVersion);

/// <summary>Versioned, non-sensitive workspace manifest.</summary>
public sealed record WorkspaceManifestResponse(int FormatVersion, string Name, string Type, string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkspaceConfiguration? Configuration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Directories);

/// <summary>Non-sensitive configuration and the current metadata concurrency version.</summary>
public sealed record WorkspaceConfigurationResponse(WorkspaceConfiguration Configuration, Guid ConcurrencyStamp);

/// <summary>Configuration replacement and the metadata version observed by the caller.</summary>
public sealed record SetWorkspaceConfigurationRequest(WorkspaceConfiguration Configuration, Guid ExpectedVersion);

/// <summary>Manifest data accepted by the import endpoint.</summary>
public sealed record WorkspaceManifestInput(int FormatVersion, string Name, string Type, string? Description,
    WorkspaceConfiguration? Configuration, IReadOnlyList<string>? Directories)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

/// <summary>Manifest and unique slug for the newly provisioned workspace.</summary>
public sealed record ImportWorkspaceManifestRequest(string Slug, WorkspaceManifestInput Manifest)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

/// <summary>Requested workspace member role.</summary>
public sealed record SetWorkspaceMemberRequest(string Role);

/// <summary>An active workspace member.</summary>
public sealed record WorkspaceMemberResponse(Guid UserId, string Role, DateTimeOffset JoinedAt);

/// <summary>A bounded page of active workspace members.</summary>
public sealed record WorkspaceMemberListResponse(
    IReadOnlyList<WorkspaceMemberResponse> Items, int Offset, int Limit, bool HasMore);
