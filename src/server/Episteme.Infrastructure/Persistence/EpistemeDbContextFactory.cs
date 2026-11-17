using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Episteme.Infrastructure.Persistence;

/// <summary>Creates a context for EF migration generation without starting the web host.</summary>
public sealed class EpistemeDbContextFactory : IDesignTimeDbContextFactory<EpistemeDbContext>
{
    public EpistemeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Episteme");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set ConnectionStrings__Episteme before using EF Core tools.");

        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(connectionString)
            .UseOpenIddict()
            .Options;
        return new EpistemeDbContext(options);
    }
}
