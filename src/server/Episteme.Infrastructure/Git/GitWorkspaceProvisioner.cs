using CliWrap;
using CliWrap.Buffered;
using System.ComponentModel;
using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Microsoft.Extensions.Configuration;

namespace Episteme.Infrastructure.Git;

public sealed class GitWorkspaceProvisioner(IConfiguration configuration) : IWorkspaceRepositoryProvisioner
{
    public async Task InitializeAsync(string storageKey, string defaultBranch, CancellationToken cancellationToken,
        IReadOnlyList<string>? directories = null)
    {
        var root = configuration["WorkspaceStorageRoot"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new WorkspaceProvisioningException("WorkspaceStorageRoot must be an absolute, service-owned directory.");
        if (!Guid.TryParseExact(storageKey, "N", out _) || defaultBranch != "main")
            throw new WorkspaceProvisioningException("Invalid repository identity.");
        if (directories is not null && !WorkspaceDirectorySkeleton.TryNormalize(directories, out _))
            throw new WorkspaceProvisioningException("Invalid directory skeleton.");

        try
        {
            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists || rootInfo.LinkTarget is not null)
                throw new WorkspaceProvisioningException("Workspace storage root must exist and must not be a symlink.");
            var repository = Path.Combine(rootInfo.FullName, storageKey);
            var directory = new DirectoryInfo(repository);
            if (directory.Exists && directory.LinkTarget is not null)
                throw new WorkspaceProvisioningException("Repository directory must not be a symlink.");
            directory.Create();
            var gitDirectory = new DirectoryInfo(Path.Combine(repository, ".git"));
            if (gitDirectory.LinkTarget is not null)
                throw new WorkspaceProvisioningException("Git metadata must not be a symlink.");
            if (!gitDirectory.Exists)
            {
                if (directory.EnumerateFileSystemInfos().Any())
                    throw new WorkspaceProvisioningException("Repository directory is not empty.");
                await RunAsync(repository, ["init", "--initial-branch=main", "--template="], cancellationToken);
            }

            var branch = await RunAsync(repository, ["symbolic-ref", "--short", "HEAD"], cancellationToken);
            if (branch.StandardOutput.Trim() != defaultBranch)
                throw new WorkspaceProvisioningException("Repository branch is not the expected default branch.");
            if (directories is not null)
            {
                try
                {
                    await GitWorkspaceReader.InitializeSkeletonAsync(repository, defaultBranch, directories, cancellationToken);
                }
                catch (InvalidOperationException exception)
                {
                    throw new WorkspaceProvisioningException("Git directory skeleton initialization failed.", exception);
                }
                return;
            }
            var head = await RunAsync(repository, ["rev-parse", "--verify", "HEAD"], cancellationToken, false);
            if (head.ExitCode == 0)
                return;
            if (head.ExitCode != 128)
                throw new WorkspaceProvisioningException("Could not inspect repository HEAD.");

            await RunAsync(repository,
                ["-c", "user.name=Episteme", "-c", "user.email=episteme@localhost",
                 "-c", "core.hooksPath=/dev/null", "commit", "--allow-empty", "-m", "Initialize workspace"],
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or CliWrap.Exceptions.CommandExecutionException)
        {
            throw new WorkspaceProvisioningException("Git repository initialization failed.", exception);
        }
    }

    private static async Task<BufferedCommandResult> RunAsync(
        string repository, string[] arguments, CancellationToken cancellationToken, bool validate = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await Cli.Wrap("git")
                .WithWorkingDirectory(repository)
                .WithArguments(arguments)
                .WithEnvironmentVariables(environment => environment
                    .Set("GIT_CONFIG_NOSYSTEM", "1")
                    .Set("GIT_CONFIG_GLOBAL", "/dev/null")
                    .Set("GIT_TERMINAL_PROMPT", "0")
                    .Set("GIT_CONFIG_COUNT", "0")
                    .Set("GIT_DIR", null)
                    .Set("GIT_WORK_TREE", null)
                    .Set("GIT_INDEX_FILE", null)
                    .Set("GIT_OBJECT_DIRECTORY", null)
                    .Set("GIT_ALTERNATE_OBJECT_DIRECTORIES", null)
                    .Set("GIT_AUTHOR_NAME", "Episteme")
                    .Set("GIT_AUTHOR_EMAIL", "episteme@localhost")
                    .Set("GIT_COMMITTER_NAME", "Episteme")
                    .Set("GIT_COMMITTER_EMAIL", "episteme@localhost"))
                .WithValidation(validate ? CommandResultValidation.ZeroExitCode : CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkspaceProvisioningException("Git repository initialization timed out.", exception);
        }
    }
}
