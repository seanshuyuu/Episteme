using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/workspaces/{workspaceId:guid}/projects")]
public sealed class ProjectsController(IProjectService projectService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ProjectListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid workspaceId, [FromQuery] int offset = 0,
        [FromQuery] int limit = 25, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out var actor))
            return Unauthorized();
        var result = await projectService.ListAsync(workspaceId, actor, offset, limit, cancellationToken);
        if (result.Failure == ProjectFailure.InvalidInput)
            return Problem(statusCode: 400, title: "Invalid project page");
        if (result.Failure == ProjectFailure.NotFound)
            return Problem(statusCode: 404, title: "Workspace not found");
        if (result.Failure is not null)
            return Problem(statusCode: 403, title: "Project access denied");
        return Ok(new ProjectListResponse(
            result.Items!.Select(ToResponse).ToArray(), offset, limit, result.HasMore));
    }

    [HttpPost]
    [ProducesResponseType<ProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create(Guid workspaceId, CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
            return Unauthorized();
        var result = await projectService.CreateAsync(
            new CreateProjectCommand(workspaceId, actor, request.Name, request.Slug, request.Description),
            cancellationToken);
        return ProjectResponse(result, true);
    }

    [HttpPut("{projectId:guid}")]
    [ProducesResponseType<ProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid workspaceId, Guid projectId,
        UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
            return Unauthorized();
        var result = await projectService.UpdateAsync(new UpdateProjectCommand(
            projectId, actor, request.ExpectedVersion, request.Name, request.Slug, request.Description),
            cancellationToken);
        return ProjectResponse(result, false);
    }

    [HttpPost("{projectId:guid}/status")]
    [ProducesResponseType<ProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetStatus(Guid workspaceId, Guid projectId,
        SetProjectStatusRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
            return Unauthorized();
        if (!Enum.TryParse<ProjectStatus>(request.Status, true, out var status) ||
            !Enum.IsDefined(status) || status == ProjectStatus.Discarded)
            return Problem(statusCode: 400, title: "Invalid project status");
        var result = await projectService.SetStatusAsync(
            projectId, actor, request.ExpectedVersion, status, cancellationToken);
        return ProjectResponse(result, false);
    }

    [HttpDelete("{projectId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid projectId,
        DeleteProjectRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
            return Unauthorized();
        var result = await projectService.DeleteAsync(projectId, actor, request.ExpectedVersion, cancellationToken);
        return result.Failure is null ? NoContent() : ProjectFailureResponse(result.Failure);
    }

    [HttpPost("{projectId:guid}/promote-folder")]
    [ProducesResponseType<ProjectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PromoteFolder(Guid workspaceId, Guid projectId,
        PromoteFolderRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
            return Unauthorized();
        var result = await projectService.PromoteFolderAsync(
            new PromoteProjectCommand(workspaceId, projectId, actor, request.FolderPath),
            cancellationToken);
        return ProjectResponse(result, true);
    }

    [HttpPost("{projectId:guid}/demote")]
    [ProducesResponseType<ProjectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Demote(Guid workspaceId, Guid projectId,
        CancellationToken cancellationToken)
    {
        if (!TryGetActor(out var actor))
            return Unauthorized();
        var result = await projectService.DemoteAsync(
            new DemoteProjectCommand(workspaceId, projectId, actor),
            cancellationToken);
        return ProjectResponse(result, false);
    }

    private IActionResult ProjectResponse(ProjectResult result, bool created) => result.Failure switch
    {
        null when result.Project is not null => created
            ? CreatedAtAction(nameof(List), new { workspaceId = result.Project.WorkspaceId },
                ToResponse(result.Project))
            : Ok(ToResponse(result.Project)),
        _ => ProjectFailureResponse(result.Failure)
    };

    private IActionResult ProjectFailureResponse(ProjectFailure? failure) => failure switch
    {
        ProjectFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid project input"),
        ProjectFailure.NotFound => Problem(statusCode: 404, title: "Workspace or project not found"),
        ProjectFailure.Unauthorized => Problem(statusCode: 403, title: "Project access denied"),
        ProjectFailure.Conflict => Problem(statusCode: 409, title: "Project slug or version conflict"),
        ProjectFailure.ProvisioningFailed => Problem(statusCode: 503, title: "Project repository provisioning failed"),
        _ => Problem(statusCode: 500, title: "Invalid project operation result")
    };

    private bool TryGetActor(out WorkspaceActor actor)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var parsed))
        {
            actor = new WorkspaceActor(Guid.Empty, new HashSet<string>(), false);
            return false;
        }
        actor = parsed!;
        return true;
    }

    private static ProjectResponse ToResponse(ProjectSummary project) =>
        new(project.Id, project.WorkspaceId, project.Name, project.Slug, project.Description,
            project.Status.ToString(), project.DefaultBranch, project.ConcurrencyStamp,
            project.FileCount, project.FolderCount, project.CreatedAt, project.UpdatedAt);
}

public sealed record ProjectResponse(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Slug,
    string? Description,
    string Status,
    string DefaultBranch,
    Guid ConcurrencyStamp,
    int FileCount,
    int FolderCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectListResponse(IReadOnlyList<ProjectResponse> Items, int Offset, int Limit, bool HasMore);
public sealed record CreateProjectRequest(string Name, string Slug, string? Description);
public sealed record UpdateProjectRequest(string Name, string Slug, string? Description, Guid ExpectedVersion);
public sealed record SetProjectStatusRequest(string Status, Guid ExpectedVersion);
public sealed record DeleteProjectRequest(Guid ExpectedVersion);
public sealed record PromoteFolderRequest(string FolderPath);
