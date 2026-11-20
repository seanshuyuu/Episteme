using System.Text;
using Episteme.Domain.Workspaces;

namespace Episteme.Application.Workspaces;

public enum WorkspaceContentFailure
{
    Unauthorized,
    InvalidInput,
    NotFound,
    Unsupported,
    TooLarge,
    Conflict
}

public sealed record WorkspaceContentEntry(string Name, string Kind);
public sealed record WorkspaceDirectoryResult(
    string? CommitId, IReadOnlyList<WorkspaceContentEntry>? Entries, WorkspaceContentFailure? Failure);
public sealed record WorkspaceDocumentResult(string? CommitId, string? Content, WorkspaceContentFailure? Failure);
public sealed record WorkspaceBinaryDocumentResult(string? CommitId, byte[]? Content, WorkspaceContentFailure? Failure);
public sealed record WorkspaceHistoryEntry(
    string CommitId, string Message, string AuthorName, DateTimeOffset CreatedAt);
public sealed record WorkspaceHistoryResult(
    string? HeadCommitId, IReadOnlyList<WorkspaceHistoryEntry>? Commits, bool HasMore, WorkspaceContentFailure? Failure);
public sealed record WorkspaceWriteResult(string? CommitId, WorkspaceContentFailure? Failure);
public enum WorkspaceWriteOperation { Put, Delete, Restore, MoveDocument, CreateDirectory, DeleteDirectory, MoveDirectory, PutBinary }
public sealed record WorkspaceWriteCommand(
    Guid WorkspaceId, WorkspaceActor Actor, string Path, string ExpectedHead,
    WorkspaceWriteOperation Operation, string? Content = null, string? SourceCommit = null,
    string? DestinationPath = null, bool Recursive = false, byte[]? BinaryContent = null,
    string? Comment = null);

public interface IWorkspaceContentRepository
{
    Task<WorkspaceDirectoryResult> ListAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken);
    Task<WorkspaceDocumentResult> ReadAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken);
    Task<WorkspaceBinaryDocumentResult> ReadBinaryAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken);
    Task<WorkspaceDocumentResult> ReadVersionAsync(Guid workspaceId, WorkspaceActor actor, string path, string commitId, CancellationToken cancellationToken);
    Task<WorkspaceHistoryResult> HistoryAsync(Guid workspaceId, WorkspaceActor actor, string path, int offset, int limit, CancellationToken cancellationToken);
    Task<WorkspaceWriteResult> WriteAsync(WorkspaceWriteCommand command, CancellationToken cancellationToken);
}

public interface IWorkspaceContentService : IWorkspaceContentRepository;

public sealed class WorkspaceContentService(IWorkspaceContentRepository repository) : IWorkspaceContentService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public Task<WorkspaceDirectoryResult> ListAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new WorkspaceDirectoryResult(null, null, WorkspaceContentFailure.Unauthorized));
        if (workspaceId == Guid.Empty || !UserWorkspaceSettings.IsValidDirectory(path))
            return Task.FromResult(new WorkspaceDirectoryResult(null, null, WorkspaceContentFailure.InvalidInput));
        return repository.ListAsync(workspaceId, actor, path, cancellationToken);
    }

    public Task<WorkspaceDocumentResult> ReadAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new WorkspaceDocumentResult(null, null, WorkspaceContentFailure.Unauthorized));
        if (workspaceId == Guid.Empty || string.IsNullOrEmpty(path) || !UserWorkspaceSettings.IsValidDirectory(path))
            return Task.FromResult(new WorkspaceDocumentResult(null, null, WorkspaceContentFailure.InvalidInput));
        return repository.ReadAsync(workspaceId, actor, path, cancellationToken);
    }

    public Task<WorkspaceBinaryDocumentResult> ReadBinaryAsync(Guid workspaceId, WorkspaceActor actor, string path, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new WorkspaceBinaryDocumentResult(null, null, WorkspaceContentFailure.Unauthorized));
        if (workspaceId == Guid.Empty || string.IsNullOrEmpty(path) || !UserWorkspaceSettings.IsValidDirectory(path))
            return Task.FromResult(new WorkspaceBinaryDocumentResult(null, null, WorkspaceContentFailure.InvalidInput));
        return repository.ReadBinaryAsync(workspaceId, actor, path, cancellationToken);
    }

    public Task<WorkspaceDocumentResult> ReadVersionAsync(
        Guid workspaceId, WorkspaceActor actor, string path, string commitId, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new WorkspaceDocumentResult(null, null, WorkspaceContentFailure.Unauthorized));
        if (workspaceId == Guid.Empty || string.IsNullOrEmpty(path) || !UserWorkspaceSettings.IsValidDirectory(path) ||
            !IsObjectId(commitId))
            return Task.FromResult(new WorkspaceDocumentResult(null, null, WorkspaceContentFailure.InvalidInput));
        return repository.ReadVersionAsync(workspaceId, actor, path, commitId, cancellationToken);
    }

    public Task<WorkspaceHistoryResult> HistoryAsync(Guid workspaceId, WorkspaceActor actor, string path, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!Authorized(actor))
            return Task.FromResult(new WorkspaceHistoryResult(null, null, false, WorkspaceContentFailure.Unauthorized));
        if (workspaceId == Guid.Empty || string.IsNullOrEmpty(path) || !UserWorkspaceSettings.IsValidDirectory(path) ||
            offset is < 0 or > 10000 || limit is < 1 or > 100)
            return Task.FromResult(new WorkspaceHistoryResult(null, null, false, WorkspaceContentFailure.InvalidInput));
        return repository.HistoryAsync(workspaceId, actor, path, offset, limit, cancellationToken);
    }

    public Task<WorkspaceWriteResult> WriteAsync(WorkspaceWriteCommand command, CancellationToken cancellationToken)
    {
        if (command.Actor.UserId == Guid.Empty || !command.Actor.Scopes.Contains("workspaces.write"))
            return Task.FromResult(new WorkspaceWriteResult(null, WorkspaceContentFailure.Unauthorized));
        if (command.WorkspaceId == Guid.Empty || string.IsNullOrEmpty(command.Path) ||
            !UserWorkspaceSettings.IsValidDirectory(command.Path) || !IsObjectId(command.ExpectedHead) ||
            !Enum.IsDefined(command.Operation) ||
            (command.Operation is WorkspaceWriteOperation.MoveDocument or WorkspaceWriteOperation.MoveDirectory &&
                (string.IsNullOrEmpty(command.DestinationPath) ||
                !UserWorkspaceSettings.IsValidDirectory(command.DestinationPath) ||
                command.DestinationPath == command.Path ||
                command.DestinationPath.StartsWith(command.Path + "/", StringComparison.Ordinal))) ||
            (command.Operation == WorkspaceWriteOperation.CreateDirectory &&
                !UserWorkspaceSettings.IsValidDirectory(command.Path + "/.gitkeep")) ||
            (command.Operation == WorkspaceWriteOperation.Put && (command.Content is null || command.Content.Contains('\0'))) ||
            (command.Operation == WorkspaceWriteOperation.PutBinary && command.BinaryContent is null) ||
            (command.Operation == WorkspaceWriteOperation.Restore && !IsObjectId(command.SourceCommit)) ||
            (command.Comment?.Length > 2000) ||
            (command.Comment?.Contains('\0') == true))
            return Task.FromResult(new WorkspaceWriteResult(null, WorkspaceContentFailure.InvalidInput));
        if (command.Operation == WorkspaceWriteOperation.Put)
        {
            try
            {
                if (StrictUtf8.GetByteCount(command.Content!) > 1024 * 1024)
                    return Task.FromResult(new WorkspaceWriteResult(null, WorkspaceContentFailure.TooLarge));
            }
            catch (EncoderFallbackException)
            {
                return Task.FromResult(new WorkspaceWriteResult(null, WorkspaceContentFailure.InvalidInput));
            }
        }
        if (command.Operation == WorkspaceWriteOperation.PutBinary && command.BinaryContent!.Length > 1024 * 1024)
            return Task.FromResult(new WorkspaceWriteResult(null, WorkspaceContentFailure.TooLarge));
        return repository.WriteAsync(command, cancellationToken);
    }

    private static bool IsObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Authorized(WorkspaceActor actor) =>
        actor.UserId != Guid.Empty && actor.Scopes.Contains("workspaces.read");
}
