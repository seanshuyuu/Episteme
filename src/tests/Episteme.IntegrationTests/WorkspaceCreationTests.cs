using CliWrap;
using CliWrap.Buffered;
using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Git;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Episteme.IntegrationTests;

public sealed class WorkspaceCreationTests
{
    [PostgresFact]
    public async Task Creates_owner_database_audit_and_isolated_git_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"episteme-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = Options();
        await using var db = new EpistemeDbContext(options);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"create-{Guid.NewGuid():N}" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Guid? workspaceId = null;
        try
        {
            var service = Service(db, Provisioner(root));
            var actor = Actor(user.Id);
            var result = await service.CreateAsync(new(actor, "Research", $"research-{Guid.NewGuid():N}",
                WorkspaceType.Research, null), default);
            Assert.Null(result.Failure);
            workspaceId = result.Workspace!.Id;
            db.ChangeTracker.Clear();
            var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
            Assert.Equal(WorkspaceStatus.Active, workspace.Status);
            Assert.Equal(WorkspaceRole.Owner, (await db.WorkspaceMembers.SingleAsync(m => m.WorkspaceId == workspaceId)).Role);
            Assert.Equal(2, await db.WorkspaceAuditEvents.CountAsync(e => e.WorkspaceId == workspaceId));
            var repository = Path.Combine(root, workspace.StorageKey);
            var git = await Cli.Wrap("git").WithWorkingDirectory(repository)
                .WithArguments(["rev-parse", "--verify", "HEAD"]).ExecuteBufferedAsync();
            Assert.Equal(40, git.StandardOutput.Trim().Length);
            await Provisioner(root).InitializeAsync(workspace.StorageKey, "main", default);
            var again = await Cli.Wrap("git").WithWorkingDirectory(repository)
                .WithArguments(["rev-parse", "--verify", "HEAD"]).ExecuteBufferedAsync();
            Assert.Equal(git.StandardOutput, again.StandardOutput);
        }
        finally
        {
            if (workspaceId is not null)
                await Cleanup(db, workspaceId.Value);
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    [PostgresFact]
    public async Task Failed_provisioning_is_invisible_recoverable_and_scoped_to_owner()
    {
        var root = Path.Combine(Path.GetTempPath(), $"episteme-test-{Guid.NewGuid():N}");
        var options = Options();
        await using var db = new EpistemeDbContext(options);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"create-{Guid.NewGuid():N}" };
        var other = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"create-{Guid.NewGuid():N}" };
        db.Users.AddRange(user, other);
        await db.SaveChangesAsync();
        Guid? workspaceId = null;
        try
        {
            var service = Service(db, Provisioner(root));
            var result = await service.CreateAsync(new(Actor(user.Id), "Notes", $"notes-{Guid.NewGuid():N}",
                WorkspaceType.Custom, null, Directories: ["notes/drafts"]), default);
            Assert.Equal(WorkspaceCreationFailure.ProvisioningFailed, result.Failure);
            workspaceId = result.PendingWorkspaceId;
            Assert.NotNull(workspaceId);
            db.ChangeTracker.Clear();
            Assert.Equal(WorkspaceStatus.Provisioning, (await db.Workspaces.SingleAsync(w => w.Id == workspaceId)).Status);
            Assert.Single((await service.ListPendingAsync(Actor(user.Id), 0, 25, default)).Items!);
            Assert.Empty((await service.ListPendingAsync(Actor(other.Id), 0, 25, default)).Items!);
            var denied = await service.RetryAsync(workspaceId.Value, Actor(other.Id), default);
            Assert.Equal(WorkspaceCreationFailure.Unauthorized, denied.Failure);
            db.ChangeTracker.Clear();
            Directory.CreateDirectory(root);
            var retried = await service.RetryAsync(workspaceId.Value, Actor(user.Id), default);
            Assert.Null(retried.Failure);
            Assert.Equal(workspaceId, retried.Workspace!.Id);
            db.ChangeTracker.Clear();
            var restored = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
            Assert.Null(restored.ProvisioningDirectoriesJson);
            var repository = Path.Combine(root, restored.StorageKey);
            var initial = await Cli.Wrap("git").WithWorkingDirectory(repository)
                .WithArguments(["rev-parse", "HEAD"]).ExecuteBufferedAsync();
            await Provisioner(root).InitializeAsync(restored.StorageKey, "main", default, ["notes/drafts"]);
            var afterRetry = await Cli.Wrap("git").WithWorkingDirectory(repository)
                .WithArguments(["rev-parse", "HEAD"]).ExecuteBufferedAsync();
            Assert.Equal(initial.StandardOutput, afterRetry.StandardOutput);
            var tree = await Cli.Wrap("git").WithWorkingDirectory(repository)
                .WithArguments(["ls-tree", "-r", "--name-only", "HEAD"]).ExecuteBufferedAsync();
            Assert.Contains("notes/drafts/.gitkeep", tree.StandardOutput);
            await Assert.ThrowsAsync<WorkspaceProvisioningException>(() =>
                Provisioner(root).InitializeAsync(restored.StorageKey, "main", default, ["different"]));
        }
        finally
        {
            if (workspaceId is not null)
                await Cleanup(db, workspaceId.Value);
            await db.Users.Where(u => u.Id == user.Id || u.Id == other.Id).ExecuteDeleteAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Initial_skeleton_can_be_created_in_git()
    {
        var root = Path.Combine(Path.GetTempPath(), $"episteme-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var provisioner = Provisioner(root);
            var key = Guid.NewGuid().ToString("N");
            await provisioner.InitializeAsync(key, "main", default, ["notes/drafts"]);
            await Assert.ThrowsAsync<WorkspaceProvisioningException>(() =>
                provisioner.InitializeAsync(key, "main", default, []));
            var emptyKey = Guid.NewGuid().ToString("N");
            await provisioner.InitializeAsync(emptyKey, "main", default, []);
            await provisioner.InitializeAsync(emptyKey, "main", default, []);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Invalid_storage_key_is_rejected_without_accessing_filesystem()
    {
        var provisioner = Provisioner(Path.GetTempPath());
        await Assert.ThrowsAsync<WorkspaceProvisioningException>(() =>
            provisioner.InitializeAsync("../escape", "main", default));
    }

    [PostgresFact]
    public async Task Disabled_account_cannot_create_or_leave_pending_workspace()
    {
        var options = Options();
        await using var db = new EpistemeDbContext(options);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"disabled-{Guid.NewGuid():N}",
            Status = UserAccountStatus.Disabled
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        try
        {
            var service = Service(db, Provisioner(Path.GetTempPath()));
            var result = await service.CreateAsync(new(Actor(user.Id), "Notes", $"disabled-{Guid.NewGuid():N}",
                WorkspaceType.Custom, null), default);
            Assert.Equal(WorkspaceCreationFailure.Unauthorized, result.Failure);
            Assert.False(await db.Workspaces.AnyAsync(w => w.CreatedByUserId == user.Id));
        }
        finally
        {
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Existing_nonempty_directory_is_not_taken_over()
    {
        var root = Path.Combine(Path.GetTempPath(), $"episteme-test-{Guid.NewGuid():N}");
        var key = Guid.NewGuid().ToString("N");
        var repository = Path.Combine(root, key);
        Directory.CreateDirectory(repository);
        var file = Path.Combine(repository, "preserve.txt");
        await File.WriteAllTextAsync(file, "existing");
        try
        {
            await Assert.ThrowsAsync<WorkspaceProvisioningException>(() =>
                Provisioner(root).InitializeAsync(key, "main", default));
            Assert.Equal("existing", await File.ReadAllTextAsync(file));
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(repository);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Repository_symlink_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"episteme-test-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var key = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(root, key), target);
        try
        {
            await Assert.ThrowsAsync<WorkspaceProvisioningException>(() =>
                Provisioner(root).InitializeAsync(key, "main", default));
            Assert.False(Directory.Exists(Path.Combine(target, ".git")));
        }
        finally
        {
            Directory.Delete(Path.Combine(root, key));
            Directory.Delete(target);
            Directory.Delete(root);
        }
    }

    private static DbContextOptions<EpistemeDbContext> Options() =>
        new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!)
            .UseOpenIddict().Options;

    private static WorkspaceActor Actor(Guid id) =>
        new(id, new HashSet<string>(StringComparer.Ordinal) { "workspaces.manage" }, false);

    private static GitWorkspaceProvisioner Provisioner(string root) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkspaceStorageRoot"] = root }).Build());

    private static WorkspaceCreationService Service(EpistemeDbContext db, IWorkspaceRepositoryProvisioner provisioner) =>
        new(new EfWorkspaceCreationRepository(db, provisioner, NullLogger<EfWorkspaceCreationRepository>.Instance));

    private static async Task Cleanup(EpistemeDbContext db, Guid id)
    {
        await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == id).ExecuteDeleteAsync();
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == id).ExecuteDeleteAsync();
        await db.Workspaces.Where(w => w.Id == id).ExecuteDeleteAsync();
    }
}
