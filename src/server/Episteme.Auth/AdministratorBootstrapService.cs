using Episteme.Domain.Identity;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Episteme.Auth;

public sealed class AdministratorBootstrapService(IServiceScopeFactory scopeFactory, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuredId = configuration["Authentication:BootstrapAdministrator:UserId"];
        var email = configuration["Authentication:BootstrapAdministrator:Email"];
        var password = configuration["Authentication:BootstrapAdministrator:Password"];
        if (string.IsNullOrWhiteSpace(configuredId) && string.IsNullOrWhiteSpace(email) && string.IsNullOrEmpty(password))
            return;
        var createAccount = string.IsNullOrWhiteSpace(configuredId);
        Guid userId;
        if (createAccount)
        {
            if (string.IsNullOrWhiteSpace(email) || email.Length > 256 || email != email.Trim() ||
                !new EmailAddressAttribute().IsValid(email) || string.IsNullOrEmpty(password) || password.Length > 1024)
                throw new InvalidOperationException("A valid bootstrap email and password are required.");
            userId = Guid.NewGuid();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrEmpty(password) ||
                !Guid.TryParse(configuredId, out userId) || userId == Guid.Empty)
                throw new InvalidOperationException("Configure either a bootstrap user ID or an email and password.");
        }
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var roleId = Guid.NewGuid();
        var concurrencyStamp = Guid.NewGuid().ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
            VALUES ({roleId}, 'Administrator', 'ADMINISTRATOR', {concurrencyStamp})
            ON CONFLICT ("NormalizedName") DO NOTHING
            """, cancellationToken);
        var role = await db.Roles.FromSqlRaw(
                "SELECT * FROM \"AspNetRoles\" WHERE \"NormalizedName\" = 'ADMINISTRATOR' FOR UPDATE")
            .SingleAsync(cancellationToken);
        var existing = await db.UserRoles.Where(assignment => assignment.RoleId == role.Id)
            .Select(assignment => assignment.UserId).ToListAsync(cancellationToken);
        if (existing.Count > 0 && (createAccount || !existing.Contains(userId)))
            throw new InvalidOperationException("Bootstrap is not permitted when a different administrator already exists.");
        if (createAccount)
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByNameAsync(email!) is not null)
                throw new InvalidOperationException("Bootstrap email already exists; use its user ID instead.");
            var user = new ApplicationUser { Id = userId, UserName = email, Email = email };
            var result = await users.CreateAsync(user, password!);
            if (!result.Succeeded)
                throw new InvalidOperationException("Bootstrap account creation failed: " +
                    string.Join(", ", result.Errors.Select(error => error.Code)));
        }
        else
        {
            var target = await db.Users
                .FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (target is null || target.Status != UserAccountStatus.Active)
                throw new InvalidOperationException("Bootstrap administrator must be an existing active user.");
        }
        if (existing.Count == 0)
        {
            db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>
            { UserId = userId, RoleId = role.Id });
            db.AccountAuditEvents.Add(new AccountAuditEvent(userId, userId, "user.administrator.bootstrapped"));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
