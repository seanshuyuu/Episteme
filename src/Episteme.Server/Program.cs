using System.Text.Json;
using System.Text.Json.Serialization;
using Episteme.Server;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Environment.GetEnvironmentVariable("EPISTEME_DATA_DIR")
              ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var sqliteConnection = $"Data Source={Path.Combine(dataDir, "episteme.db")}";

// Production switches are opt-in. Default stays SQLite + legacy role header.
var provider = EpistemeConfig.DbProvider();
if (provider == "postgres")
{
    var pgConnection = Environment.GetEnvironmentVariable("EPISTEME_PG_CONNECTION");
    if (string.IsNullOrWhiteSpace(pgConnection))
        throw new InvalidOperationException("EPISTEME_DB_PROVIDER=postgres 需要 EPISTEME_PG_CONNECTION");
    builder.Services.AddDbContextFactory<EpistemeDbContext>(o => o.UseNpgsql(pgConnection));
}
else
{
    builder.Services.AddDbContextFactory<EpistemeDbContext>(o => o.UseSqlite(sqliteConnection));
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IdentityStore>();
builder.Services.AddScoped<EpistemeService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
EpistemeSecurity.Add(builder);

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<EpistemeDbContext>>()
               .CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}

// Identity tables are created idempotently with portable SQL so an existing database
// (created by an older Episteme version) also gains them without a migration step.
await using (var scope = app.Services.CreateAsyncScope())
{
    var identity = scope.ServiceProvider.GetRequiredService<IdentityStore>();
    await identity.EnsureTablesAsync(CancellationToken.None);
    if (EpistemeConfig.Users)
    {
        var seeded = await identity.SeedAdminAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(seeded))
            app.Logger.LogInformation("Seeded initial admin account: {Admin}", seeded);
        // Account authorization requires every space to have exactly one owner.
        // v0.5 databases only had ep_space_members grants; backfill from the legacy
        // owner grant, otherwise hand the space to the first admin so nothing vanishes.
        var backfilled = await identity.BackfillOwnersAsync(CancellationToken.None);
        if (backfilled > 0)
            app.Logger.LogInformation("Backfilled account ownership for {Count} existing spaces", backfilled);
    }
}

app.UseCors();
EpistemeSecurity.Use(app);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    product = "Episteme",
    version = EpistemeConfig.Version,
    authMode = EpistemeConfig.AuthMode(),
    dbProvider = EpistemeConfig.DbProvider(),
    utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
}));

app.MapEpistemeEndpoints();
app.MapIdentityEndpoints();
app.MapMcpEndpoints();
app.MapSseMcpEndpoints();

app.Run();
