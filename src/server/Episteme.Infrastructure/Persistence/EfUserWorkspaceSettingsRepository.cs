using Episteme.Application.Workspaces;
using Episteme.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfUserWorkspaceSettingsRepository(EpistemeDbContext dbContext) : IUserWorkspaceSettingsRepository
{
    public Task<string?> GetDirectoryForActiveUserAsync(Guid userId, CancellationToken cancellationToken) =>
        GetDirectoryAsync(userId, false, cancellationToken);
    public Task<string?> GetDirectoryForMcpUserAsync(Guid userId, CancellationToken cancellationToken) =>
        GetDirectoryAsync(userId, true, cancellationToken);

    private Task<string?> GetDirectoryAsync(Guid userId, bool isMcp, CancellationToken cancellationToken) =>
        (from user in dbContext.Users.AsNoTracking()
         where user.Id == userId && user.Status == UserAccountStatus.Active &&
             (!isMcp || dbContext.McpUserSettings.Any(item => item.UserId == userId && item.Enabled))
         join settings in dbContext.UserWorkspaceSettings.AsNoTracking()
             on user.Id equals settings.UserId into allSettings
         from settings in allSettings.DefaultIfEmpty()
         select settings == null ? string.Empty : settings.DefaultWorkspaceDirectory)
        .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> SetDirectoryForActiveUserAsync(Guid userId, string directory, CancellationToken cancellationToken) =>
        SetDirectoryAsync(userId, directory, false, cancellationToken);
    public Task<bool> SetDirectoryForMcpUserAsync(Guid userId, string directory, CancellationToken cancellationToken) =>
        SetDirectoryAsync(userId, directory, true, cancellationToken);

    private async Task<bool> SetDirectoryAsync(Guid userId, string directory, bool isMcp, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (isMcp)
        {
            var user = await dbContext.Users.FromSqlInterpolated(
                $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (user is null || user.Status != UserAccountStatus.Active ||
                !await dbContext.McpUserSettings.AsNoTracking().AnyAsync(item => item.UserId == userId && item.Enabled, cancellationToken))
                return false;
        }
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
