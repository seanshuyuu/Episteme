using Episteme.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Episteme");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("ConnectionStrings__Episteme must be configured.");

var options = new DbContextOptionsBuilder<EpistemeDbContext>()
    .UseNpgsql(connection)
    .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
    .UseOpenIddict()
    .Options;
await using var db = new EpistemeDbContext(options);
await db.Database.MigrateAsync();
