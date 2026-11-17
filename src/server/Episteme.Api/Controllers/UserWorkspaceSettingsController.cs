using System.Security.Claims;
using Episteme.Application.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/me/workspace-settings")]
public sealed class UserWorkspaceSettingsController(IUserWorkspaceSettingsService service) : ControllerBase
{
    /// <summary>Gets the authenticated user's default workspace directory.</summary>
    [HttpGet]
    [ProducesResponseType<UserWorkspaceSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        return ToResponse(await service.GetAsync(userId, GetScopes(), cancellationToken));
    }

    /// <summary>Updates the authenticated user's default workspace directory.</summary>
    [HttpPut]
    [ProducesResponseType<UserWorkspaceSettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(UpdateUserWorkspaceSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        return ToResponse(await service.SetAsync(userId, GetScopes(), request.DefaultWorkspaceDirectory, cancellationToken));
    }

    private IActionResult ToResponse(UserSettingsResult result) => result.Failure switch
    {
        null => Ok(new UserWorkspaceSettingsResponse(result.Directory!)),
        UserSettingsFailure.InvalidDirectory => Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid workspace directory"),
        _ => Problem(statusCode: StatusCodes.Status403Forbidden, title: "Workspace settings access denied")
    };

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out userId)
        && userId != Guid.Empty;

    private HashSet<string> GetScopes() => User.FindAll("scope")
        .Concat(User.FindAll("scp"))
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToHashSet(StringComparer.Ordinal);
}

/// <summary>Updates the caller's default workspace-relative directory; an empty string selects the workspace root.</summary>
public sealed record UpdateUserWorkspaceSettingsRequest(string? DefaultWorkspaceDirectory);

/// <summary>Describes the caller's default workspace-relative directory.</summary>
public sealed record UserWorkspaceSettingsResponse(string DefaultWorkspaceDirectory);
