using Episteme.Application.Identity;
using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/admin/users")]
public sealed class UserAdministrationController(IUserAdministrationService service) : ControllerBase
{
    /// <summary>Resets a user's password using Identity's password policy and token providers.</summary>
    [HttpPut("{userId:guid}/password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetPassword(Guid userId, ResetUserPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!Request.IsHttps)
            return Problem(statusCode: 400, title: "HTTPS is required for password operations");
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.ResetPasswordAsync(actor!, userId, new(request.Password), cancellationToken);
        return result.Failure is null ? NoContent() : Failure(result.Failure.Value);
    }

    /// <summary>Lists access and refresh token metadata belonging to a user.</summary>
    [HttpGet("{userId:guid}/tokens")]
    [ProducesResponseType<UserTokenListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListTokens(Guid userId, [FromQuery] int offset = 0, [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.ListTokensAsync(actor!, userId, offset, limit, cancellationToken);
        if (result.Failure is not null)
            return TokenFailure(result.Failure.Value);
        return Ok(new UserTokenListResponse(result.Items!.Select(t => new UserTokenResponse(
            t.Id, t.ClientId, t.Type, t.Status, t.CreatedAt, t.ExpiresAt)).ToArray(), offset, limit, result.HasMore));
    }

    /// <summary>Revokes an access or refresh token belonging to the specified user.</summary>
    [HttpDelete("{userId:guid}/tokens/{tokenId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RevokeToken(Guid userId, string tokenId, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.RevokeTokenAsync(actor!, userId, tokenId, cancellationToken);
        return result.Failure is null ? NoContent() : TokenFailure(result.Failure.Value);
    }

    /// <summary>Creates an active user with an Identity-managed password over HTTPS.</summary>
    [HttpPost]
    [ProducesResponseType<ManagedUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!Request.IsHttps)
            return Problem(statusCode: 400, title: "HTTPS is required for password operations");
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.CreateAsync(actor!, new CreateManagedUser(request.Email, request.Password), cancellationToken);
        return result.Failure is null
            ? CreatedAtAction(nameof(Get), new { userId = result.User!.Id }, ToResponse(result.User))
            : Failure(result.Failure.Value);
    }

    /// <summary>Gets one user account for an active system administrator.</summary>
    [HttpGet("{userId:guid}")]
    [ProducesResponseType<ManagedUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.GetAsync(actor!, userId, cancellationToken);
        return result.Failure is null ? Ok(ToResponse(result.User!)) : Failure(result.Failure.Value);
    }

    /// <summary>Lists user accounts for a currently active system administrator.</summary>
    [HttpGet]
    [ProducesResponseType<ManagedUserListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.ListAsync(actor!, offset, limit, cancellationToken);
        if (result.Failure is not null)
            return Failure(result.Failure.Value);
        return Ok(new ManagedUserListResponse(result.Items!.Select(ToResponse).ToArray(), offset, limit, result.HasMore));
    }

    /// <summary>Enables or disables a user, subject to owner and last-administrator protections.</summary>
    [HttpPut("{userId:guid}/status")]
    [ProducesResponseType<ManagedUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetStatus(Guid userId, SetUserStatusRequest request, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        if (!Enum.TryParse<UserAccountStatus>(request.Status, true, out var status) ||
            !string.Equals(status.ToString(), request.Status, StringComparison.OrdinalIgnoreCase))
            return Failure(UserAdministrationFailure.InvalidInput);
        var result = await service.SetStatusAsync(actor!, userId, status, cancellationToken);
        return result.Failure is null ? Ok(ToResponse(result.User!)) : Failure(result.Failure.Value);
    }

    /// <summary>Grants or removes the Administrator role while preserving an active administrator.</summary>
    [HttpPut("{userId:guid}/administrator")]
    [ProducesResponseType<ManagedUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetAdministrator(Guid userId, SetAdministratorRequest request, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        if (request.IsAdministrator is null)
            return Failure(UserAdministrationFailure.InvalidInput);
        var result = await service.SetAdministratorAsync(actor!, userId, request.IsAdministrator.Value, cancellationToken);
        return result.Failure is null ? Ok(ToResponse(result.User!)) : Failure(result.Failure.Value);
    }

    private static ManagedUserResponse ToResponse(ManagedUser user) =>
        new(user.Id, user.UserName, user.Email, user.Status.ToString(), user.CreatedAt, user.IsAdministrator);

    private ObjectResult TokenFailure(UserTokenFailure failure) => failure switch
    {
        UserTokenFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid token request"),
        UserTokenFailure.Unauthorized => Problem(statusCode: 403, title: "User administration access denied"),
        UserTokenFailure.NotFound => Problem(statusCode: 404, title: "User or token not found"),
        UserTokenFailure.Conflict => Problem(statusCode: 409, title: "Token changed concurrently"),
        _ => Problem(statusCode: 500, title: "Invalid token management result")
    };

    private ObjectResult Failure(UserAdministrationFailure failure) => failure switch
    {
        UserAdministrationFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid user administration request"),
        UserAdministrationFailure.Unauthorized => Problem(statusCode: 403, title: "User administration access denied"),
        UserAdministrationFailure.NotFound => Problem(statusCode: 404, title: "User not found"),
        UserAdministrationFailure.Conflict => Problem(statusCode: 409, title: "User status change conflicts with ownership or administrator requirements"),
        _ => Problem(statusCode: 500, title: "Invalid user administration result")
    };
}

/// <summary>Requests a user account state of Active or Disabled.</summary>
public sealed record SetUserStatusRequest(string Status);
/// <summary>Supplies a new user's email and password; send only over HTTPS.</summary>
public sealed record CreateUserRequest(string Email, string Password)
{
    public override string ToString() => "CreateUserRequest { Password = [REDACTED] }";
}
/// <summary>Supplies a replacement password; send only over HTTPS.</summary>
public sealed record ResetUserPasswordRequest(string Password)
{
    public override string ToString() => "ResetUserPasswordRequest { Password = [REDACTED] }";
}
/// <summary>Specifies whether the user should have the system Administrator role.</summary>
public sealed record SetAdministratorRequest(bool? IsAdministrator);
/// <summary>Describes a managed user without credentials.</summary>
public sealed record ManagedUserResponse(Guid Id, string? UserName, string? Email, string Status,
    DateTimeOffset CreatedAt, bool IsAdministrator);
/// <summary>Contains one page of managed users.</summary>
public sealed record ManagedUserListResponse(IReadOnlyList<ManagedUserResponse> Items, int Offset, int Limit, bool HasMore);
