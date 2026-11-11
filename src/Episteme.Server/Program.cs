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
app.MapMcpEndpoints();
app.MapSseMcpEndpoints();

app.Run();
