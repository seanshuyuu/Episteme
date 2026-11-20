using System.ComponentModel.DataAnnotations;
using Episteme.Application.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/mcp/settings")]
public sealed class McpSettingsController(IMcpSettingsService service) : ControllerBase
{
    /// <summary>Gets the caller's personal MCP settings.</summary>
    [HttpGet("personal")]
    [ProducesResponseType<McpPersonalSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPersonal(CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.GetPersonalAsync(actor!, cancellationToken);
        return result.Failure is { } failure ? Failure(failure) : Ok(ToResponse(result.Value!));
    }

    /// <summary>Replaces the caller's personal MCP settings.</summary>
    [HttpPut("personal")]
    [ProducesResponseType<McpPersonalSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetPersonal(SetMcpPersonalSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        if (request.Enabled is null || request.AllowCreateWorkspace is null)
            return Problem(statusCode: 400, title: "Invalid MCP settings");
        var result = await service.SetPersonalAsync(actor!,
            new(request.Enabled.Value, request.AllowCreateWorkspace.Value), cancellationToken);
        return result.Failure is { } failure ? Failure(failure) : Ok(ToResponse(result.Value!));
    }

    /// <summary>Gets the caller's MCP permissions and the workspace switch.</summary>
    [HttpGet("workspaces/{workspaceId:guid}")]
    [ProducesResponseType<McpWorkspaceSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetWorkspace(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.GetWorkspaceAsync(actor!, workspaceId, cancellationToken);
        return result.Failure is { } failure ? Failure(failure) : Ok(ToResponse(result.Value!));
    }

    /// <summary>Replaces the caller's selected permissions for a workspace.</summary>
    [HttpPut("workspaces/{workspaceId:guid}/personal")]
    [ProducesResponseType<McpWorkspaceSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetWorkspace(Guid workspaceId, SetMcpPermissionsRequest request, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        if (!TryParsePermissions(request.Permissions, out var permissions))
            return Problem(statusCode: 400, title: "Invalid MCP permissions");
        var result = await service.SetWorkspaceAsync(actor!, workspaceId, permissions, cancellationToken);
        return result.Failure is { } failure ? Failure(failure) : Ok(ToResponse(result.Value!));
    }

    /// <summary>Enables or disables MCP access to a workspace; requires active owner membership.</summary>
    [HttpPut("workspaces/{workspaceId:guid}/enabled")]
    [ProducesResponseType<McpWorkspaceEnabledResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SetWorkspaceEnabled(Guid workspaceId, SetMcpWorkspaceEnabledRequest request, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        if (request.Enabled is null)
            return Problem(statusCode: 400, title: "Invalid MCP settings");
        var result = await service.SetWorkspaceEnabledAsync(actor!, workspaceId, request.Enabled.Value, cancellationToken);
        return result.Failure is { } failure ? Failure(failure) : Ok(new McpWorkspaceEnabledResponse(result.Value));
    }

    /// <summary>Reads an active non-owner member's MCP grant; requires active owner membership.</summary>
    [HttpGet("workspaces/{workspaceId:guid}/members/{memberId:guid}")]
    [ProducesResponseType<McpPermissionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMemberGrant(Guid workspaceId, Guid memberId, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.GetMemberGrantAsync(actor!, workspaceId, memberId, cancellationToken);
        return result.Failure is { } failure ? Failure(failure) : Ok(new McpPermissionsResponse(Names(result.Value)));
    }

    /// <summary>Replaces an active non-owner member's MCP grant; requires active owner membership.</summary>
    [HttpPut("workspaces/{workspaceId:guid}/members/{memberId:guid}")]
    [ProducesResponseType<McpPermissionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetMemberGrant(Guid workspaceId, Guid memberId, SetMcpPermissionsRequest request, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        if (!TryParsePermissions(request.Permissions, out var permissions))
            return Problem(statusCode: 400, title: "Invalid MCP permissions");
        var result = await service.SetMemberGrantAsync(actor!, workspaceId, memberId, permissions, cancellationToken);
        return result.Failure is { } failure ? Failure(failure) : Ok(new McpPermissionsResponse(Names(result.Value)));
    }

    private static bool TryParsePermissions(string[]? names, out McpPermission permissions)
    {
        permissions = McpPermission.None;
        if (names is null)
            return false;
        foreach (var name in names)
        {
            if (!Enum.TryParse<McpPermission>(name, false, out var parsed) ||
                parsed == McpPermission.None || !Enum.IsDefined(parsed) ||
                !string.Equals(parsed.ToString(), name, StringComparison.Ordinal) ||
                !McpAccessPolicy.IsWorkspacePermissionsValid(parsed))
                return false;
            permissions |= parsed;
        }
        return true;
    }

    private static string[] Names(McpPermission permissions) =>
        Enum.GetValues<McpPermission>().Where(value => value != McpPermission.None &&
            (permissions & value) == value).Select(value => value.ToString()).ToArray();

    private static McpPersonalSettingsResponse ToResponse(McpPersonalSettings value) =>
        new(value.Enabled, value.AllowCreateWorkspace);

    private static McpWorkspaceSettingsResponse ToResponse(McpWorkspaceSettingsView value) =>
        new(value.Enabled, Names(value.PersonalPermissions), Names(value.GrantedPermissions), value.IsOwner);

    private ObjectResult Failure(McpSettingsFailure failure) => failure switch
    {
        McpSettingsFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid MCP settings"),
        McpSettingsFailure.Unauthorized => Problem(statusCode: 403, title: "MCP settings access denied"),
        McpSettingsFailure.NotFound => Problem(statusCode: 404, title: "Workspace member not found"),
        _ => Problem(statusCode: 500, title: "Invalid MCP settings result")
    };
}

/// <summary>Personal MCP switches.</summary>
public sealed record McpPersonalSettingsResponse(bool Enabled, bool AllowCreateWorkspace);

/// <summary>Replacement personal MCP switches.</summary>
public sealed record SetMcpPersonalSettingsRequest(
    [Required] bool? Enabled, [Required] bool? AllowCreateWorkspace);

/// <summary>Workspace MCP switch and caller permissions.</summary>
public sealed record McpWorkspaceSettingsResponse(
    bool Enabled, string[] PersonalPermissions, string[] GrantedPermissions, bool IsOwner);

/// <summary>Replacement workspace MCP switch.</summary>
public sealed record SetMcpWorkspaceEnabledRequest([Required] bool? Enabled);

/// <summary>Current workspace MCP switch.</summary>
public sealed record McpWorkspaceEnabledResponse(bool Enabled);

/// <summary>Replacement set of individual MCP permission names.</summary>
public sealed record SetMcpPermissionsRequest([Required] string[]? Permissions);

/// <summary>Set of individual MCP permission names.</summary>
public sealed record McpPermissionsResponse(string[] Permissions);
