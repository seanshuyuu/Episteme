using System.Text.Json;
using System.Text.Json.Serialization;
using Episteme.Server;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Environment.GetEnvironmentVariable("EPISTEME_DATA_DIR")
              ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var connectionString = $"Data Source={Path.Combine(dataDir, "episteme.db")}";

builder.Services.AddDbContextFactory<EpistemeDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddScoped<EpistemeService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<EpistemeDbContext>>()
               .CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    product = "Episteme",
    version = "0.2.0-p1p2",
    utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
}));

app.MapEpistemeEndpoints();
app.MapMcpEndpoints();

app.Run();
