using Episteme.Domain.Identity;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Episteme.IntegrationTests;

public sealed class UserWorkspaceSettingsRepositoryTests
{
    [PostgresFact]
    public async Task Settings_are_upserted_per_active_user_and_preserve_preferences()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!)
            .UseOpenIddict()
            .Options;
        await using var db = new EpistemeDbContext(options);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"test-{Guid.NewGuid():N}" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        try
        {
            var repository = new EfUserWorkspaceSettingsRepository(db);
            Assert.Equal("", await repository.GetDirectoryForActiveUserAsync(user.Id, default));
            Assert.True(await repository.SetDirectoryForActiveUserAsync(user.Id, "first", default));
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"UserWorkspaceSettings\" SET \"PreferencesJson\" = '{{\"theme\":\"dark\"}}'::jsonb WHERE \"UserId\" = {user.Id}");
            Assert.True(await repository.SetDirectoryForActiveUserAsync(user.Id, "notes/论文", default));
            Assert.Equal(2, await db.AccountAuditEvents.CountAsync(a =>
                a.ActorUserId == user.Id && a.TargetUserId == user.Id && a.EventType == "user.workspace-settings.updated"));
            Assert.Equal("notes/论文", await repository.GetDirectoryForActiveUserAsync(user.Id, default));
            var settings = await db.UserWorkspaceSettings.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
            Assert.Contains("\"dark\"", settings.PreferencesJson);

            user.Status = UserAccountStatus.Disabled;
            await db.SaveChangesAsync();
            Assert.Null(await repository.GetDirectoryForActiveUserAsync(user.Id, default));
            Assert.False(await repository.SetDirectoryForActiveUserAsync(user.Id, "denied", default));
        }
        finally
        {
            await db.AccountAuditEvents.Where(a => a.TargetUserId == user.Id).ExecuteDeleteAsync();
            await db.UserWorkspaceSettings.Where(settings => settings.UserId == user.Id).ExecuteDeleteAsync();
            await db.Users.Where(account => account.Id == user.Id).ExecuteDeleteAsync();
        }
    }

    [PostgresFact]
    public async Task Concurrent_first_writes_do_not_create_duplicate_rows()
    {
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!)
            .UseOpenIddict()
            .Options;
        await using var db = new EpistemeDbContext(options);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"test-{Guid.NewGuid():N}" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        try
        {
            await using var secondContext = new EpistemeDbContext(options);
            var first = new EfUserWorkspaceSettingsRepository(db);
            var second = new EfUserWorkspaceSettingsRepository(secondContext);
            var results = await Task.WhenAll(
                first.SetDirectoryForActiveUserAsync(user.Id, "first", default),
                second.SetDirectoryForActiveUserAsync(user.Id, "second", default));

            Assert.All(results, Assert.True);
            Assert.Equal(1, await db.UserWorkspaceSettings.CountAsync(settings => settings.UserId == user.Id));
            Assert.Contains(await first.GetDirectoryForActiveUserAsync(user.Id, default), new[] { "first", "second" });
        }
        finally
        {
            await db.AccountAuditEvents.Where(a => a.TargetUserId == user.Id).ExecuteDeleteAsync();
            await db.UserWorkspaceSettings.Where(settings => settings.UserId == user.Id).ExecuteDeleteAsync();
            await db.Users.Where(account => account.Id == user.Id).ExecuteDeleteAsync();
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")))
            Skip = "Set EPISTEME_TEST_POSTGRES to run PostgreSQL integration tests against a migrated disposable database.";
    }
}
