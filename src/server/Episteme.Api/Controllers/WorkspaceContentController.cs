using Episteme.Application.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Episteme.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/workspaces/{workspaceId:guid}")]
public sealed class WorkspaceContentController(IWorkspaceContentService content) : ControllerBase
{
    /// <summary>Creates a directory with a Git placeholder.</summary>
    [HttpPut("directories")]
    [ProducesResponseType<WorkspaceCommitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> CreateDirectory(Guid workspaceId, [FromQuery] string path,
        DeleteWorkspaceDocumentRequest request, CancellationToken cancellationToken) =>
        Write(workspaceId, path, request.ExpectedHead, WorkspaceWriteOperation.CreateDirectory, null, null, cancellationToken);

    /// <summary>Deletes a directory; nonempty directories require explicit recursive deletion.</summary>
    [HttpDelete("directories")]
    [ProducesResponseType<WorkspaceCommitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> DeleteDirectory(Guid workspaceId, [FromQuery] string path,
        DeleteWorkspaceDirectoryRequest request, CancellationToken cancellationToken) =>
        Write(workspaceId, path, request.ExpectedHead, WorkspaceWriteOperation.DeleteDirectory, null, null,
            cancellationToken, recursive: request.Recursive);

    /// <summary>Moves a directory in a single commit.</summary>
    [HttpPost("directories/move")]
    [ProducesResponseType<WorkspaceCommitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> MoveDirectory(Guid workspaceId, [FromQuery] string path,
        MoveWorkspacePathRequest request, CancellationToken cancellationToken) =>
        Write(workspaceId, path, request.ExpectedHead, WorkspaceWriteOperation.MoveDirectory, null, null,
            cancellationToken, request.DestinationPath);

    /// <summary>Moves a document in a single commit.</summary>
    [HttpPost("documents/move")]
    [ProducesResponseType<WorkspaceCommitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> MoveDocument(Guid workspaceId, [FromQuery] string path,
        MoveWorkspacePathRequest request, CancellationToken cancellationToken) =>
        Write(workspaceId, path, request.ExpectedHead, WorkspaceWriteOperation.MoveDocument, null, null,
            cancellationToken, request.DestinationPath);

    /// <summary>Reads a document from a commit reachable from the current branch.</summary>
    [HttpGet("documents/version")]
    [ProducesResponseType<WorkspaceDocumentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReadVersion(Guid workspaceId, [FromQuery] string path,
        [FromQuery] string commitId, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await content.ReadVersionAsync(workspaceId, actor!, path, commitId, cancellationToken);
        if (result.Failure is not null)
            return Failure(result.Failure);
        return Ok(new WorkspaceDocumentResponse(result.CommitId!, result.Content!));
    }

    /// <summary>Creates or replaces a document as a new commit on the expected branch head.</summary>
    [HttpPut("documents")]
    [ProducesResponseType<WorkspaceCommitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> PutDocument(Guid workspaceId, [FromQuery] string path,
        PutWorkspaceDocumentRequest request, CancellationToken cancellationToken) =>
        Write(workspaceId, path, request.ExpectedHead, WorkspaceWriteOperation.Put, request.Content, null, cancellationToken);

    /// <summary>Deletes a document in a new commit on the expected branch head.</summary>
    [HttpDelete("documents")]
    [ProducesResponseType<WorkspaceCommitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> DeleteDocument(Guid workspaceId, [FromQuery] string path,
        DeleteWorkspaceDocumentRequest request, CancellationToken cancellationToken) =>
        Write(workspaceId, path, request.ExpectedHead, WorkspaceWriteOperation.Delete, null, null, cancellationToken);

    /// <summary>Restores a document from an ancestor commit by creating a new commit.</summary>
    [HttpPost("documents/restore")]
    [ProducesResponseType<WorkspaceCommitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> RestoreDocument(Guid workspaceId, [FromQuery] string path,
        RestoreWorkspaceDocumentRequest request, CancellationToken cancellationToken) =>
        Write(workspaceId, path, request.ExpectedHead, WorkspaceWriteOperation.Restore, null, request.SourceCommit, cancellationToken);

    private async Task<IActionResult> Write(Guid workspaceId, string path, string expectedHead,
        WorkspaceWriteOperation operation, string? body, string? sourceCommit, CancellationToken cancellationToken,
        string? destination = null, bool recursive = false)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await content.WriteAsync(new WorkspaceWriteCommand(
            workspaceId, actor!, path, expectedHead, operation, body, sourceCommit, destination, recursive), cancellationToken);
        return result.Failure is not null ? Failure(result.Failure) : Ok(new WorkspaceCommitResponse(result.CommitId!));
    }

    /// <summary>Lists commits that changed a document path, newest first.</summary>
    [HttpGet("documents/history")]
    [ProducesResponseType<WorkspaceDocumentHistoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DocumentHistory(Guid workspaceId, [FromQuery] string path,
        [FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await content.HistoryAsync(workspaceId, actor!, path, offset, limit, cancellationToken);
        if (result.Failure is not null)
            return Failure(result.Failure);
        return Ok(new WorkspaceDocumentHistoryResponse(result.HeadCommitId!, result.Commits!, offset, limit, result.HasMore));
    }

    /// <summary>Lists a directory in the workspace's current committed tree.</summary>
    [HttpGet("directories")]
    [ProducesResponseType<WorkspaceDirectoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ListDirectory(Guid workspaceId, [FromQuery] string path = "",
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await content.ListAsync(workspaceId, actor!, path, cancellationToken);
        if (result.Failure is not null)
            return Failure(result.Failure);
        return Ok(new WorkspaceDirectoryResponse(result.CommitId!, result.Entries!
            .Select(e => new WorkspaceContentEntryResponse(e.Name, e.Kind)).ToArray()));
    }

    /// <summary>Reads a UTF-8 document from the workspace's current committed tree.</summary>
    [HttpGet("documents")]
    [ProducesResponseType<WorkspaceDocumentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReadDocument(Guid workspaceId, [FromQuery] string path, CancellationToken cancellationToken)
    {
        if (!WorkspaceActorClaims.TryGetActor(User, out var actor))
            return Unauthorized();
        var result = await content.ReadAsync(workspaceId, actor!, path, cancellationToken);
        if (result.Failure is not null)
            return Failure(result.Failure);
        return Ok(new WorkspaceDocumentResponse(result.CommitId!, result.Content!));
    }

    private IActionResult Failure(WorkspaceContentFailure? failure) => failure switch
    {
        WorkspaceContentFailure.InvalidInput => Problem(statusCode: 400, title: "Invalid workspace path"),
        WorkspaceContentFailure.Unauthorized => Problem(statusCode: 403, title: "Workspace access denied"),
        WorkspaceContentFailure.NotFound => Problem(statusCode: 404, title: "Workspace content not found"),
        WorkspaceContentFailure.Conflict => Problem(statusCode: 409, title: "Workspace content conflict"),
        WorkspaceContentFailure.TooLarge => Problem(statusCode: 413, title: "Workspace content exceeds the size limit"),
        WorkspaceContentFailure.Unsupported => Problem(statusCode: 422, title: "Unsupported workspace content"),
        _ => Problem(statusCode: 500, title: "Invalid workspace content result")
    };

}

/// <summary>A committed workspace directory entry.</summary>
public sealed record WorkspaceContentEntryResponse(string Name, string Kind);

/// <summary>A directory snapshot at a Git commit.</summary>
public sealed record WorkspaceDirectoryResponse(string CommitId, IReadOnlyList<WorkspaceContentEntryResponse> Entries);

/// <summary>A UTF-8 document snapshot at a Git commit.</summary>
public sealed record WorkspaceDocumentResponse(string CommitId, string Content);

/// <summary>Commit IDs that changed a document path, relative to a pinned branch head.</summary>
public sealed record WorkspaceDocumentHistoryResponse(
    string HeadCommitId, IReadOnlyList<string> Commits, int Offset, int Limit, bool HasMore);

/// <summary>UTF-8 document content and the branch head observed before writing.</summary>
public sealed record PutWorkspaceDocumentRequest(string ExpectedHead, string Content);
/// <summary>The branch head observed before deleting a document.</summary>
public sealed record DeleteWorkspaceDocumentRequest(string ExpectedHead);
/// <summary>The source commit and branch head observed before restoring a document.</summary>
public sealed record RestoreWorkspaceDocumentRequest(string ExpectedHead, string SourceCommit);
/// <summary>The commit produced by a workspace mutation.</summary>
public sealed record WorkspaceCommitResponse(string CommitId);
/// <summary>Directory deletion options and the expected branch head.</summary>
public sealed record DeleteWorkspaceDirectoryRequest(string ExpectedHead, bool Recursive);
/// <summary>Destination relative path and expected branch head for a move.</summary>
public sealed record MoveWorkspacePathRequest(string ExpectedHead, string DestinationPath);
