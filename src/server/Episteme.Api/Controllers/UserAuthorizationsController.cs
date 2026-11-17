using Episteme.Application.Identity;
using Episteme.Application.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/me/authorizations")]
public sealed class UserAuthorizationsController(IUserAuthorizationService service) : ControllerBase
{
    /// <summary>Lists the caller's OAuth authorizations without exposing token values.</summary>
    [HttpGet]
    [ProducesResponseType<UserAuthorizationListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.ListAsync(actor!.UserId, actor.Scopes, offset, limit, cancellationToken);
        if (result.Failure is not null)
            return Failure(result.Failure.Value);
        return Ok(new UserAuthorizationListResponse(result.Items!.Select(a => new UserAuthorizationResponse(
            a.Id, a.ClientId, a.Status, a.CreatedAt)).ToArray(), offset, limit, result.HasMore));
    }

    /// <summary>Revokes one of the caller's OAuth authorizations.</summary>
    [HttpDelete("{authorizationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(string authorizationId, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.RevokeAsync(actor!.UserId, actor.Scopes, authorizationId, cancellationToken);
        return result.Failure is null ? NoContent() : Failure(result.Failure.Value);
    }

    private ObjectResult Failure(UserTokenFailure failure) => failure switch
    {
        UserTokenFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid authorization request"),
        UserTokenFailure.Unauthorized => Problem(statusCode: 403, title: "Authorization management access denied"),
        UserTokenFailure.NotFound => Problem(statusCode: 404, title: "Authorization not found"),
        UserTokenFailure.Conflict => Problem(statusCode: 409, title: "Authorization changed concurrently"),
        _ => Problem(statusCode: 500, title: "Invalid authorization management result")
    };
}

/// <summary>Contains a page of OAuth authorizations belonging to the caller.</summary>
public sealed record UserAuthorizationListResponse(IReadOnlyList<UserAuthorizationResponse> Items, int Offset, int Limit, bool HasMore);

/// <summary>Describes an OAuth authorization without exposing token values.</summary>
public sealed record UserAuthorizationResponse(string Id, string? ClientId, string Status, DateTimeOffset? CreatedAt);
