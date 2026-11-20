using System.Globalization;
using System.Text;
using CliWrap;
using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Microsoft.Extensions.Configuration;

namespace Episteme.Infrastructure.Git;

public sealed class GitWorkspaceReader(IConfiguration configuration)
{
    private const int MaxOutputBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task InitializeSkeletonAsync(string repository, string branch, IReadOnlyList<string> directories,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceDirectorySkeleton.TryNormalize(directories, out var normalized))
            throw new InvalidOperationException("Invalid directory skeleton.");
        var directory = Directory.CreateTempSubdirectory("episteme-index-");
        var index = Path.Combine(directory.FullName, "index");
        try
        {
            await RunAsync(repository, ["read-tree", "--empty"], 128, cancellationToken, index);
            var blob = StrictUtf8.GetString(await RunAsync(repository, ["hash-object", "-w", "--stdin"], 128,
                cancellationToken, input: "")).Trim();
            if (!IsObjectId(blob))
                throw new InvalidOperationException("Git returned an invalid object ID.");
            var entries = new StringBuilder();
            foreach (var path in normalized)
                entries.Append("100644 ").Append(blob).Append('\t').Append(path).Append("/.gitkeep\0");
            await RunAsync(repository, ["update-index", "-z", "--index-info"], 128, cancellationToken, index, entries.ToString());
            var tree = StrictUtf8.GetString(await RunAsync(repository, ["write-tree"], 128, cancellationToken, index)).Trim();
            if (!IsObjectId(tree))
                throw new InvalidOperationException("Git returned an invalid tree ID.");
            var reference = await RunResultAsync(repository, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], 128, cancellationToken);
            if (reference.ExitCode == 0)
            {
                var existing = await RunAsync(repository, ["rev-parse", "--verify", $"refs/heads/{branch}^{{tree}}"], 128, cancellationToken);
                if (StrictUtf8.GetString(existing).Trim() != tree)
                    throw new InvalidOperationException("Existing repository tree does not match the pending skeleton.");
                return;
            }
            if (reference.ExitCode != 1)
                throw new InvalidOperationException($"Git could not inspect the initial branch (exit {reference.ExitCode}).");
            var commit = StrictUtf8.GetString(await RunAsync(repository,
                ["-c", "user.name=Episteme", "-c", "user.email=episteme@localhost", "commit-tree", tree],
                128, cancellationToken, index, "Initialize workspace\n", "provisioner")).Trim();
            if (!IsObjectId(commit))
                throw new InvalidOperationException("Git returned an invalid commit ID.");
            await RunAsync(repository, ["update-ref", $"refs/heads/{branch}", commit, new string('0', commit.Length)],
                128, cancellationToken);
        }
        finally
        {
            if (File.Exists(index))
                File.Delete(index);
            directory.Delete();
        }
    }

    public async Task<(IReadOnlyList<string>? Directories, WorkspaceAccessFailure? Failure)> GetSkeletonAsync(
        string storageKey, string branch, CancellationToken cancellationToken)
    {
        var repository = RepositoryPath(storageKey, branch);
        var head = await HeadAsync(repository, branch, cancellationToken);
        try
        {
            var data = await RunAsync(repository, ["ls-tree", "-r", "-t", "-z", head], MaxOutputBytes, cancellationToken);
            var directories = ParseEntries(data).Where(entry => entry.Mode == "040000").Select(entry => entry.Name).ToArray();
            if (!WorkspaceDirectorySkeleton.TryNormalize(directories, out var normalized))
                return (null, WorkspaceAccessFailure.Unsupported);
            return (normalized, null);
        }
        catch (GitOutputLimitException)
        {
            return (null, WorkspaceAccessFailure.TooLarge);
        }
        catch (DecoderFallbackException)
        {
            return (null, WorkspaceAccessFailure.Unsupported);
        }
    }

    public async Task<(int FileCount, int FolderCount)> GetStatsAsync(
        string storageKey, string branch, CancellationToken cancellationToken)
    {
        var repository = RepositoryPath(storageKey, branch);
        var head = await HeadAsync(repository, branch, cancellationToken);
        var data = await RunAsync(repository, ["ls-tree", "-r", "-t", "-z", head], MaxOutputBytes, cancellationToken);
        var entries = ParseEntries(data);
        var fileCount = entries.Count(entry =>
            entry.Mode is "100644" or "100755" &&
            !entry.Name.EndsWith("/.gitkeep", StringComparison.OrdinalIgnoreCase) &&
            !entry.Name.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase));
        var folderCount = entries
            .Where(entry => entry.Mode == "040000")
            .Select(entry => entry.Name)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return (fileCount, folderCount);
    }

    public async Task<string?> CopyFolderAsync(
        string sourceStorageKey,
        string sourceBranch,
        string sourceFolder,
        string targetStorageKey,
        string targetBranch,
        string targetFolder,
        Guid targetProjectId,
        WorkspaceActor actor,
        CancellationToken cancellationToken)
    {
        var sourceRepository = RepositoryPath(sourceStorageKey, sourceBranch);
        var sourceHead = await HeadAsync(sourceRepository, sourceBranch, cancellationToken);
        var raw = await RunAsync(sourceRepository,
            ["ls-tree", "-r", "-t", "-z", sourceHead, "--", sourceFolder],
            MaxOutputBytes, cancellationToken);
        var entries = ParseEntries(raw);
        var targetHead = await HeadAsync(RepositoryPath(targetStorageKey, targetBranch), targetBranch, cancellationToken);

        foreach (var entry in entries.Where(entry => entry.Mode == "040000"))
        {
            var suffix = sourceFolder.Length == 0
                ? entry.Name
                : (entry.Name.Length > sourceFolder.Length
                    ? entry.Name[(sourceFolder.Length + 1)..]
                    : "");
            if (suffix.Length == 0)
                continue;
            var targetPath = targetFolder.Length == 0 ? suffix : $"{targetFolder}/{suffix}";
            var result = await WriteAsync(targetStorageKey, targetBranch,
                new WorkspaceWriteCommand(targetProjectId, actor, targetPath, targetHead,
                    WorkspaceWriteOperation.CreateDirectory), cancellationToken);
            if (result.CommitId is null)
                return null;
            targetHead = result.CommitId;
        }

        foreach (var entry in entries.Where(entry =>
                     entry.Mode is "100644" or "100755"))
        {
            if (entry.Name.EndsWith("/.gitkeep", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = sourceFolder.Length == 0
                ? entry.Name
                : (entry.Name.Length > sourceFolder.Length
                    ? entry.Name[(sourceFolder.Length + 1)..]
                    : entry.Name);
            var targetPath = targetFolder.Length == 0 ? suffix : $"{targetFolder}/{suffix}";
            var content = await RunAsync(sourceRepository,
                ["cat-file", "blob", entry.ObjectId], MaxOutputBytes, cancellationToken);
            var result = await WriteAsync(targetStorageKey, targetBranch,
                new WorkspaceWriteCommand(targetProjectId, actor, targetPath, targetHead,
                    WorkspaceWriteOperation.PutBinary, BinaryContent: content),
                cancellationToken);
            if (result.CommitId is null)
                return null;
            targetHead = result.CommitId;
        }
        return targetHead;
    }
    public async Task<WorkspaceDirectoryResult> ListAsync(
        string storageKey, string branch, string path, CancellationToken cancellationToken)
    {
        var repository = RepositoryPath(storageKey, branch);
        var commit = await HeadAsync(repository, branch, cancellationToken);
        var tree = commit;
        if (path.Length > 0)
        {
            var entry = await FindAsync(repository, commit, path, cancellationToken);
            if (entry is null)
                return new(null, null, WorkspaceContentFailure.NotFound);
            if (entry.Mode != "040000")
                return new(null, null, WorkspaceContentFailure.Unsupported);
            tree = entry.ObjectId;
        }

        try
        {
            var data = await RunAsync(repository, ["ls-tree", "-z", tree], MaxOutputBytes, cancellationToken);
            var entries = ParseEntries(data)
                .Select(e => new WorkspaceContentEntry(e.Name, !UserWorkspaceSettings.IsValidDirectory(e.Name)
                    ? "unsupported" : e.Mode switch
                    {
                        "040000" => "directory",
                        "100644" or "100755" => "document",
                        "120000" => "symlink",
                        _ => "unsupported"
                    })).ToArray();
            return new(commit, entries, null);
        }
        catch (GitOutputLimitException)
        {
            return new(null, null, WorkspaceContentFailure.TooLarge);
        }
    }

    public async Task<WorkspaceDocumentResult> ReadAsync(
        string storageKey, string branch, string path, CancellationToken cancellationToken)
    {
        var repository = RepositoryPath(storageKey, branch);
        var commit = await HeadAsync(repository, branch, cancellationToken);
        return await ReadAtAsync(repository, commit, path, cancellationToken);
    }

    public async Task<WorkspaceBinaryDocumentResult> ReadBinaryAsync(
        string storageKey, string branch, string path, CancellationToken cancellationToken)
    {
        var repository = RepositoryPath(storageKey, branch);
        var commit = await HeadAsync(repository, branch, cancellationToken);
        return await ReadBinaryAtAsync(repository, commit, path, cancellationToken);
    }

    public async Task<WorkspaceDocumentResult> ReadVersionAsync(
        string storageKey, string branch, string path, string commit, CancellationToken cancellationToken)
    {
        var repository = RepositoryPath(storageKey, branch);
        var head = await HeadAsync(repository, branch, cancellationToken);
        if (!await IsReachableCommitAsync(repository, commit, head, cancellationToken))
            return new(null, null, WorkspaceContentFailure.NotFound);
        return await ReadAtAsync(repository, commit, path, cancellationToken);
    }

    private static async Task<WorkspaceDocumentResult> ReadAtAsync(
        string repository, string commit, string path, CancellationToken cancellationToken)
    {
        var result = await ReadBinaryAtAsync(repository, commit, path, cancellationToken);
        if (result.Failure is not null)
            return new(null, null, result.Failure);
        try
        {
            var text = StrictUtf8.GetString(result.Content!);
            if (text.Contains('\0'))
                return new(null, null, WorkspaceContentFailure.Unsupported);
            return new(commit, text, null);
        }
        catch (DecoderFallbackException)
        {
            return new(null, null, WorkspaceContentFailure.Unsupported);
        }
    }

    private static async Task<WorkspaceBinaryDocumentResult> ReadBinaryAtAsync(
        string repository, string commit, string path, CancellationToken cancellationToken)
    {
        var entry = await FindAsync(repository, commit, path, cancellationToken);
        if (entry is null)
            return new(null, null, WorkspaceContentFailure.NotFound);
        if (entry.Mode is not ("100644" or "100755"))
            return new(null, null, WorkspaceContentFailure.Unsupported);
        var sizeText = StrictUtf8.GetString(await RunAsync(repository, ["cat-file", "-s", entry.ObjectId], 128, cancellationToken)).Trim();
        if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
            throw new InvalidOperationException("Git returned an invalid object size.");
        if (size > MaxOutputBytes)
            return new(null, null, WorkspaceContentFailure.TooLarge);
        var content = await RunAsync(repository, ["cat-file", "blob", entry.ObjectId], MaxOutputBytes, cancellationToken);
        return new(commit, content, null);
    }

    public async Task<WorkspaceHistoryResult> HistoryAsync(
        string storageKey, string branch, string path, int offset, int limit, CancellationToken cancellationToken)
    {
        var repository = RepositoryPath(storageKey, branch);
        var commit = await HeadAsync(repository, branch, cancellationToken);
        var data = await RunAsync(repository,
            ["--literal-pathspecs", "log", "--format=%H%x00%an%x00%aI%x00%s%x00", $"--skip={offset}", $"--max-count={limit + 1}",
                "--no-renames", commit, "--", path], 8192, cancellationToken);
        var fields = StrictUtf8.GetString(data).Split('\0');
        var entries = new List<WorkspaceHistoryEntry>((fields.Length + 3) / 4);
        for (var index = 0; index + 3 < fields.Length; index += 4)
        {
            var objectId = fields[index].Trim();
            var author = fields[index + 1].Trim();
            var dateText = fields[index + 2].Trim();
            var subject = fields[index + 3].Trim();
            if (!IsObjectId(objectId) || string.IsNullOrWhiteSpace(author) ||
                !DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
                throw new InvalidOperationException("Git returned an invalid history.");
            entries.Add(new(objectId, subject, author, createdAt));
        }
        return new(commit, entries.Take(limit).ToArray(), entries.Count > limit, null);
    }

    public async Task<WorkspaceWriteResult> WriteAsync(
        string storageKey, string branch, WorkspaceWriteCommand command, CancellationToken cancellationToken,
        McpPermission? mcpPermissions = null)
    {
        var repository = RepositoryPath(storageKey, branch);
        var head = await HeadAsync(repository, branch, cancellationToken);
        if (head != command.ExpectedHead)
            return new(null, WorkspaceContentFailure.Conflict);

        var segments = command.Path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var parent = await FindAsync(repository, head, string.Join('/', segments.Take(i)), cancellationToken);
            if (parent is null)
            {
                if (command.Operation == WorkspaceWriteOperation.Restore)
                    continue;
                return new(null, WorkspaceContentFailure.NotFound);
            }
            if (parent.Mode != "040000")
                return new(null, WorkspaceContentFailure.Unsupported);
        }

        var current = await FindAsync(repository, head, command.Path, cancellationToken);
        if (mcpPermissions is { } allowed)
        {
            var required = command.Operation switch
            {
                WorkspaceWriteOperation.Put or WorkspaceWriteOperation.PutBinary or WorkspaceWriteOperation.Restore =>
                    current is null ? McpPermission.CreateDocument : McpPermission.UpdateDocument,
                WorkspaceWriteOperation.Delete or WorkspaceWriteOperation.DeleteDirectory => McpPermission.DeleteDocument,
                WorkspaceWriteOperation.CreateDirectory => McpPermission.CreateDocument,
                WorkspaceWriteOperation.MoveDocument or WorkspaceWriteOperation.MoveDirectory =>
                    McpPermission.CreateDocument | McpPermission.DeleteDocument,
                _ => McpPermission.None
            };
            if (required == McpPermission.None || (allowed & required) != required)
                return new(null, WorkspaceContentFailure.Unauthorized);
        }
        var isDirectory = command.Operation is WorkspaceWriteOperation.CreateDirectory or
            WorkspaceWriteOperation.DeleteDirectory or WorkspaceWriteOperation.MoveDirectory;
        if (command.Operation is WorkspaceWriteOperation.CreateDirectory or WorkspaceWriteOperation.PutBinary && current is not null)
            return new(null, WorkspaceContentFailure.Conflict);
        if (command.Operation is WorkspaceWriteOperation.Delete or WorkspaceWriteOperation.DeleteDirectory or
            WorkspaceWriteOperation.MoveDocument or WorkspaceWriteOperation.MoveDirectory && current is null)
            return new(null, WorkspaceContentFailure.NotFound);
        if (current is not null && (isDirectory ? current.Mode != "040000" : current.Mode is not ("100644" or "100755")))
            return new(null, WorkspaceContentFailure.Unsupported);

        if (command.Operation is WorkspaceWriteOperation.MoveDocument or WorkspaceWriteOperation.MoveDirectory)
        {
            var destination = command.DestinationPath!;
            var parts = destination.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var parent = await FindAsync(repository, head, string.Join('/', parts.Take(i)), cancellationToken);
                if (parent is null)
                    return new(null, WorkspaceContentFailure.NotFound);
                if (parent.Mode != "040000")
                    return new(null, WorkspaceContentFailure.Unsupported);
            }
            if (await FindAsync(repository, head, destination, cancellationToken) is not null)
                return new(null, WorkspaceContentFailure.Conflict);
        }

        IReadOnlyList<TreeEntry> descendants = [];
        if (command.Operation is WorkspaceWriteOperation.DeleteDirectory or WorkspaceWriteOperation.MoveDirectory)
        {
            try
            {
                var raw = await RunAsync(repository, ["ls-tree", "-r", "-z", current!.ObjectId], MaxOutputBytes, cancellationToken);
                descendants = ParseEntries(raw);
            }
            catch (GitOutputLimitException)
            {
                return new(null, WorkspaceContentFailure.TooLarge);
            }
            if (command.Operation == WorkspaceWriteOperation.DeleteDirectory && !command.Recursive &&
                descendants.Any(e => e.Name != ".gitkeep"))
                return new(null, WorkspaceContentFailure.Conflict);
            if (descendants.Any(e => !UserWorkspaceSettings.IsValidDirectory(e.Name) ||
                    e.Mode is not ("100644" or "100755" or "120000" or "160000")))
                return new(null, WorkspaceContentFailure.Unsupported);
        }

        TreeEntry? source = null;
        if (command.Operation == WorkspaceWriteOperation.Restore)
        {
            if (!await IsReachableCommitAsync(repository, command.SourceCommit!, head, cancellationToken))
                return new(null, WorkspaceContentFailure.NotFound);
            source = await FindAsync(repository, command.SourceCommit!, command.Path, cancellationToken);
            if (source is null)
                return new(null, WorkspaceContentFailure.NotFound);
            if (source.Mode is not ("100644" or "100755"))
                return new(null, WorkspaceContentFailure.Unsupported);
        }

        var directory = Directory.CreateTempSubdirectory("episteme-index-");
        var index = Path.Combine(directory.FullName, "index");
        try
        {
            await RunAsync(repository, ["read-tree", head], 128, cancellationToken, index);
            if (command.Operation is WorkspaceWriteOperation.DeleteDirectory or WorkspaceWriteOperation.MoveDirectory)
            {
                var changes = new StringBuilder();
                foreach (var entry in descendants)
                {
                    var oldPath = $"{command.Path}/{entry.Name}";
                    changes.Append("0 ").Append('0', head.Length).Append('\t').Append(oldPath).Append('\0');
                    if (command.Operation == WorkspaceWriteOperation.MoveDirectory)
                    {
                        var newPath = $"{command.DestinationPath}/{entry.Name}";
                        if (!UserWorkspaceSettings.IsValidDirectory(newPath))
                            return new(null, WorkspaceContentFailure.InvalidInput);
                        changes.Append(entry.Mode).Append(' ').Append(entry.ObjectId).Append('\t').Append(newPath).Append('\0');
                    }
                }
                await RunAsync(repository, ["update-index", "-z", "--index-info"],
                    128, cancellationToken, index, changes.ToString());
            }
            else if (command.Operation == WorkspaceWriteOperation.MoveDocument)
            {
                await RunAsync(repository, ["--literal-pathspecs", "update-index", "--force-remove", "--", command.Path],
                    128, cancellationToken, index);
                await RunAsync(repository, ["update-index", "--add", "--cacheinfo",
                    $"{current!.Mode},{current.ObjectId},{command.DestinationPath}"], 128, cancellationToken, index);
            }
            else if (command.Operation == WorkspaceWriteOperation.Delete)
            {
                await RunAsync(repository, ["--literal-pathspecs", "update-index", "--force-remove", "--", command.Path],
                    128, cancellationToken, index);
            }
            else
            {
                var blob = source?.ObjectId;
                if (blob is null)
                {
                    var output = await RunAsync(repository, ["hash-object", "-w", "--stdin"], 128,
                        cancellationToken, input: command.Operation == WorkspaceWriteOperation.CreateDirectory ? "" : command.Content,
                        binaryInput: command.BinaryContent);
                    blob = StrictUtf8.GetString(output).Trim();
                    if (!IsObjectId(blob))
                        throw new InvalidOperationException("Git returned an invalid object ID.");
                }
                await RunAsync(repository, ["update-index", "--add", "--cacheinfo",
                    $"{source?.Mode ?? current?.Mode ?? "100644"},{blob},{(command.Operation == WorkspaceWriteOperation.CreateDirectory ? command.Path + "/.gitkeep" : command.Path)}"], 128, cancellationToken, index);
            }
            var tree = StrictUtf8.GetString(await RunAsync(repository, ["write-tree"], 128, cancellationToken, index)).Trim();
            if (!IsObjectId(tree))
                throw new InvalidOperationException("Git returned an invalid tree ID.");
            var author = command.Actor.UserId.ToString("N");
            var comment = string.IsNullOrWhiteSpace(command.Comment)
                ? $"Episteme document {command.Operation.ToString().ToLowerInvariant()}"
                : command.Comment.Trim();
            var created = StrictUtf8.GetString(await RunAsync(repository,
                ["-c", $"user.name=Episteme {author}", "-c", $"user.email={author}@episteme.invalid",
                    "commit-tree", tree, "-p", head], 128, cancellationToken, index,
                $"{comment}\n", author)).Trim();
            if (!IsObjectId(created))
                throw new InvalidOperationException("Git returned an invalid commit ID.");
            var update = await RunResultAsync(repository, ["update-ref", $"refs/heads/{branch}", created, head],
                128, cancellationToken);
            if (update.ExitCode != 0)
            {
                if (await HeadAsync(repository, branch, cancellationToken) != head)
                    return new(null, WorkspaceContentFailure.Conflict);
                throw new InvalidOperationException("Git could not update the workspace branch.");
            }
            return new(created, null);
        }
        finally
        {
            if (File.Exists(index))
                File.Delete(index);
            directory.Delete();
        }
    }

    private static async Task<bool> IsReachableCommitAsync(
        string repository, string commit, string head, CancellationToken cancellationToken)
    {
        var source = StrictUtf8.GetString(await RunAsync(repository,
            ["cat-file", "--batch-check=%(objectname) %(objecttype)"], 256, cancellationToken,
            input: $"{commit}\n")).Trim();
        if (source == $"{commit} missing")
            return false;
        if (source == $"{commit} blob" || source == $"{commit} tree" || source == $"{commit} tag")
            return false;
        if (source != $"{commit} commit")
            throw new InvalidOperationException("Git returned an invalid object type.");
        var ancestor = await RunResultAsync(repository, ["merge-base", "--is-ancestor", commit, head], 128, cancellationToken);
        return ancestor.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException("Git could not validate the source commit.")
        };
    }

    private string RepositoryPath(string storageKey, string branch)
    {
        var root = configuration["WorkspaceStorageRoot"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
            !Guid.TryParseExact(storageKey, "N", out _) || branch != "main")
            throw new InvalidOperationException("Invalid workspace repository configuration.");
        var rootInfo = new DirectoryInfo(root);
        var repo = new DirectoryInfo(Path.Combine(rootInfo.FullName, storageKey));
        var metadata = new DirectoryInfo(Path.Combine(repo.FullName, ".git"));
        if (!rootInfo.Exists || rootInfo.LinkTarget is not null || !repo.Exists || repo.LinkTarget is not null ||
            !metadata.Exists || metadata.LinkTarget is not null)
            throw new InvalidOperationException("Workspace repository is unavailable or unsafe.");
        return repo.FullName;
    }

    private static async Task<string> HeadAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        var bytes = await RunAsync(repository, ["rev-parse", "--verify", $"refs/heads/{branch}^{{commit}}"], 128, cancellationToken);
        var hash = StrictUtf8.GetString(bytes).Trim();
        if (!IsObjectId(hash))
            throw new InvalidOperationException("Git returned an invalid commit ID.");
        return hash;
    }

    private static async Task<TreeEntry?> FindAsync(string repository, string commit, string path, CancellationToken cancellationToken)
    {
        var data = await RunAsync(repository, ["--literal-pathspecs", "ls-tree", "-z", commit, "--", path], MaxOutputBytes, cancellationToken);
        return ParseEntries(data).SingleOrDefault(e => e.Name == path);
    }

    private static IReadOnlyList<TreeEntry> ParseEntries(byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        if (text.Length != 0 && text[^1] != '\0')
            throw new InvalidOperationException("Git returned an incomplete tree.");
        var result = new List<TreeEntry>();
        foreach (var line in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0)
                throw new InvalidOperationException("Git returned an invalid tree.");
            var metadata = line[..tab].Split(' ');
            if (metadata.Length != 3 || !IsObjectId(metadata[2]))
                throw new InvalidOperationException("Git returned an invalid tree entry.");
            result.Add(new TreeEntry(metadata[0], metadata[2], line[(tab + 1)..]));
        }
        return result;
    }

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<byte[]> RunAsync(string repository, string[] arguments, int limit,
        CancellationToken cancellationToken, string? index = null, string? input = null, string? author = null, byte[]? binaryInput = null)
    {
        var result = await RunResultAsync(repository, arguments, limit, cancellationToken, index, input, author, binaryInput);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Git workspace operation failed.");
        return result.Output;
    }

    private static async Task<(int ExitCode, byte[] Output)> RunResultAsync(string repository, string[] arguments, int limit,
        CancellationToken cancellationToken, string? index = null, string? input = null, string? author = null, byte[]? binaryInput = null)
    {
        await using var output = new BoundedStream(limit);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var command = Cli.Wrap("git").WithWorkingDirectory(repository)
                .WithArguments(["-c", "core.hooksPath=/dev/null", .. arguments])
                .WithEnvironmentVariables(env => env
                    .Set("GIT_CONFIG_NOSYSTEM", "1").Set("GIT_CONFIG_GLOBAL", "/dev/null")
                    .Set("GIT_CONFIG_COUNT", "0").Set("GIT_CONFIG_PARAMETERS", null)
                    .Set("GIT_CONFIG", null).Set("GIT_TERMINAL_PROMPT", "0")
                    .Set("GIT_DIR", null).Set("GIT_WORK_TREE", null).Set("GIT_INDEX_FILE", index)
                    .Set("GIT_OBJECT_DIRECTORY", null).Set("GIT_ALTERNATE_OBJECT_DIRECTORIES", null)
                    .Set("GIT_COMMON_DIR", null).Set("GIT_NAMESPACE", null).Set("GIT_SHALLOW_FILE", null)
                    .Set("GIT_REPLACE_REF_BASE", "refs/disabled-replacements/")
                    .Set("GIT_NO_REPLACE_OBJECTS", "1")
                    .Set("GIT_AUTHOR_NAME", author is null ? null : $"Episteme {author}")
                    .Set("GIT_AUTHOR_EMAIL", author is null ? null : $"{author}@episteme.invalid")
                    .Set("GIT_COMMITTER_NAME", author is null ? null : $"Episteme {author}")
                    .Set("GIT_COMMITTER_EMAIL", author is null ? null : $"{author}@episteme.invalid")
                    .Set("GIT_AUTHOR_DATE", null).Set("GIT_COMMITTER_DATE", null))
                .WithStandardOutputPipe(PipeTarget.ToStream(output))
                .WithStandardErrorPipe(PipeTarget.Null)
                .WithValidation(CommandResultValidation.None);
            if (input is not null)
                command = command.WithStandardInputPipe(PipeSource.FromString(input, StrictUtf8));
            else if (binaryInput is not null)
                command = command.WithStandardInputPipe(PipeSource.FromBytes(binaryInput));
            var result = await command.ExecuteAsync(timeout.Token);
            return (result.ExitCode, output.ToArray());
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Git read timed out.", exception);
        }
    }

    private sealed record TreeEntry(string Mode, string ObjectId, string Name);

    private sealed class GitOutputLimitException : IOException;

    private sealed class BoundedStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit)
                throw new GitOutputLimitException();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit)
                throw new GitOutputLimitException();
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length + buffer.Length > limit)
                throw new GitOutputLimitException();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
