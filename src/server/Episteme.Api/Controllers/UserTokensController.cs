using Episteme.Application.Identity;
using Episteme.Application.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/me/tokens")]
public sealed class UserTokensController(IUserTokenService service) : ControllerBase
{
    /// <summary>Lists the caller's access and refresh token metadata without token values.</summary>
    [HttpGet]
    [ProducesResponseType<UserTokenListResponse>(StatusCodes.Status200OK)]
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
        return Ok(new UserTokenListResponse(result.Items!.Select(t => new UserTokenResponse(
            t.Id, t.ClientId, t.Type, t.Status, t.CreatedAt, t.ExpiresAt)).ToArray(), offset, limit, result.HasMore));
    }

    /// <summary>Revokes one of the caller's access or refresh tokens.</summary>
    [HttpDelete("{tokenId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(string tokenId, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.RevokeAsync(actor!.UserId, actor.Scopes, tokenId, cancellationToken);
        return result.Failure is null ? NoContent() : Failure(result.Failure.Value);
    }

    private ObjectResult Failure(UserTokenFailure failure) => failure switch
    {
        UserTokenFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid token request"),
        UserTokenFailure.Unauthorized => Problem(statusCode: 403, title: "Token management access denied"),
        UserTokenFailure.NotFound => Problem(statusCode: 404, title: "Token not found"),
        UserTokenFailure.Conflict => Problem(statusCode: 409, title: "Token changed concurrently"),
        _ => Problem(statusCode: 500, title: "Invalid token management result")
    };
}

/// <summary>Contains a page of token metadata for the authenticated user.</summary>
public sealed record UserTokenListResponse(IReadOnlyList<UserTokenResponse> Items, int Offset, int Limit, bool HasMore);

/// <summary>Describes a token entry without exposing its secret or payload.</summary>
public sealed record UserTokenResponse(string Id, string? ClientId, string Type, string Status,
    DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt);
