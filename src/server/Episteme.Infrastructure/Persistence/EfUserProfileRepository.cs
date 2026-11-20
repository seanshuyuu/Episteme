using Episteme.Application.Identity;
using Episteme.Domain.Identity;
using Episteme.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Episteme.Infrastructure.Persistence;

public sealed class EfUserProfileRepository(EpistemeDbContext db, UserManager<ApplicationUser> users)
    : IUserProfileRepository
{
    public async Task<UserProfileResult> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId && user.Status == UserAccountStatus.Active)
            .Select(user => new UserProfile(user.Id, user.DisplayName, user.UserName, user.Email))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? new(null, UserProfileFailure.NotFound) : new(user, null);
    }

    public async Task<UserProfileResult> UpdateDisplayNameAsync(Guid userId, string? displayName,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users
            .Where(user => user.Id == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(null, UserProfileFailure.NotFound);
        }
        user.DisplayName = displayName;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException("Identity failed to update the display name: " +
                string.Join(", ", result.Errors.Select(error => error.Code)));
        db.AccountAuditEvents.Add(new AccountAuditEvent(userId, userId, "user.display_name.updated"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new UserProfile(user.Id, user.DisplayName, user.UserName, user.Email), null);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string currentPassword,
        string newPassword, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users
            .Where(user => user.Id == userId)
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserAccountStatus.Active)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PasswordChangeFailure.NotFound);
        }
        if (!await users.CheckPasswordAsync(user, currentPassword))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PasswordChangeFailure.InvalidCredentials);
        }
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.All(error => error.Code.StartsWith("Password", StringComparison.Ordinal)))
                return new(PasswordChangeFailure.InvalidInput);
            if (result.Errors.All(error => error.Code == "ConcurrencyFailure"))
                return new(PasswordChangeFailure.Conflict);
            throw new InvalidOperationException("Identity failed to change the password: " +
                string.Join(", ", result.Errors.Select(error => error.Code)));
        }
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.TokenGeneration = Guid.NewGuid();
        db.AccountAuditEvents.Add(new AccountAuditEvent(userId, userId, "user.password.changed"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(null);
    }
}
