using Episteme.Application.Identity;
using Episteme.Application.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/me")]
public sealed class UserProfileController(IUserProfileService service, IHostEnvironment environment) : ControllerBase
{
    /// <summary>Gets the caller's display name, user name and email.</summary>
    [HttpGet("profile")]
    [ProducesResponseType<UserProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.GetProfileAsync(actor!.UserId, actor.Scopes, cancellationToken);
        return result.Failure is null ? Ok(ToResponse(result.Profile!)) : Failure(result.Failure.Value);
    }

    /// <summary>Updates the caller's display name; a null value falls back to the email.</summary>
    [HttpPut("profile")]
    [ProducesResponseType<UserProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProfile(UpdateUserProfileRequest request, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.UpdateDisplayNameAsync(actor!.UserId, actor.Scopes, request.DisplayName, cancellationToken);
        return result.Failure is null ? Ok(ToResponse(result.Profile!)) : Failure(result.Failure.Value);
    }

    /// <summary>Changes the caller's password after verifying the current one.</summary>
    [HttpPut("password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangePassword(ChangeUserPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!PasswordTransportAllowed)
            return Problem(statusCode: 400, title: "HTTPS is required for password operations");
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await service.ChangePasswordAsync(actor!.UserId, actor.Scopes,
            request.CurrentPassword, request.NewPassword, cancellationToken);
        return result.Failure is null ? NoContent() : Failure(result.Failure.Value);
    }

    private static UserProfileResponse ToResponse(UserProfile profile) =>
        new(profile.DisplayName, profile.Email, profile.UserName);

    /// <summary>Passwords travel only over HTTPS, except in Development for the local HTTP trial deployment.</summary>
    private bool PasswordTransportAllowed => Request.IsHttps || environment.IsDevelopment();

    private ObjectResult Failure(UserProfileFailure failure) => failure switch
    {
        UserProfileFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid profile request"),
        UserProfileFailure.NotFound => Problem(statusCode: 404, title: "User not found or inactive"),
        UserProfileFailure.Unauthorized => Problem(statusCode: 403, title: "Profile access denied"),
        _ => Problem(statusCode: 500, title: "Invalid profile result")
    };

    private ObjectResult Failure(PasswordChangeFailure failure) => failure switch
    {
        PasswordChangeFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid password request"),
        PasswordChangeFailure.InvalidCredentials => Problem(statusCode: 400, title: "Current password is incorrect"),
        PasswordChangeFailure.NotFound => Problem(statusCode: 404, title: "User not found or inactive"),
        PasswordChangeFailure.Unauthorized => Problem(statusCode: 403, title: "Password change access denied"),
        PasswordChangeFailure.Conflict => Problem(statusCode: 409, title: "Password changed concurrently"),
        _ => Problem(statusCode: 500, title: "Invalid password change result")
    };
}

/// <summary>Describes the caller's account profile without credentials.</summary>
public sealed record UserProfileResponse(string? DisplayName, string? Email, string? UserName);

/// <summary>Replacement display name; null or empty clears it and falls back to the email.</summary>
public sealed record UpdateUserProfileRequest(string? DisplayName);

/// <summary>Supplies the current password and the replacement password; send only over HTTPS.</summary>
public sealed record ChangeUserPasswordRequest(string CurrentPassword, string NewPassword)
{
    public override string ToString() => "ChangeUserPasswordRequest { Password = [REDACTED] }";
}
