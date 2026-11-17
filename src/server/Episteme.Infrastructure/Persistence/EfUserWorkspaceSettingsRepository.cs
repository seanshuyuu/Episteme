using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfUserWorkspaceSettingsRepository(EpistemeDbContext dbContext) : IUserWorkspaceSettingsRepository
{
    public Task<string?> GetDirectoryForActiveUserAsync(Guid userId, CancellationToken cancellationToken) =>
        (from user in dbContext.Users.AsNoTracking()
         where user.Id == userId && user.Status == UserAccountStatus.Active
         join settings in dbContext.UserWorkspaceSettings.AsNoTracking()
             on user.Id equals settings.UserId into allSettings
         from settings in allSettings.DefaultIfEmpty()
         select settings == null ? string.Empty : settings.DefaultWorkspaceDirectory)
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> SetDirectoryForActiveUserAsync(Guid userId, string directory, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Lock the active user until the transaction commits so a concurrent deactivation
        // cannot commit between the active-user check and the settings update.
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "UserWorkspaceSettings" ("UserId", "DefaultWorkspaceDirectory", "PreferencesJson")
            SELECT "Id", {{directory}}, '{}'::jsonb
            FROM "AspNetUsers"
            WHERE "Id" = {{userId}} AND "Status" = 'Active'
            FOR UPDATE
            ON CONFLICT ("UserId") DO UPDATE
            SET "DefaultWorkspaceDirectory" = EXCLUDED."DefaultWorkspaceDirectory"
            """, cancellationToken);
        if (affected != 1)
            return false;
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(userId, userId, "user.workspace-settings.updated"));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
