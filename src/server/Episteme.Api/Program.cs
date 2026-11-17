using Episteme.Application.Workspaces;
using Episteme.Application.Identity;
using Episteme.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddEpistemeInfrastructure(builder.Configuration);
builder.Services.AddScoped<WorkspaceAccessService>();
builder.Services.AddScoped<IWorkspaceListingService, WorkspaceListingService>();
builder.Services.AddScoped<IWorkspaceManagementService, WorkspaceManagementService>();
builder.Services.AddScoped<IWorkspaceMemberService, WorkspaceMemberService>();
builder.Services.AddScoped<IWorkspaceContentService, WorkspaceContentService>();
builder.Services.AddScoped<IWorkspaceCreationService, WorkspaceCreationService>();
builder.Services.AddScoped<IWorkspaceManifestService, WorkspaceManifestService>();
builder.Services.AddScoped<IUserWorkspaceSettingsService, UserWorkspaceSettingsService>();
builder.Services.AddScoped<IUserTokenService, UserTokenService>();
builder.Services.AddScoped<IUserAuthorizationService, UserAuthorizationService>();
builder.Services.AddScoped<IUserAdministrationService, UserAdministrationService>();

var requireTokenEntry = builder.Configuration.GetValue<bool>("Authentication:RequireTokenEntryValidation");
if (requireTokenEntry)
{
    var authority = builder.Configuration["Authentication:Authority"];
    var audience = builder.Configuration["Authentication:Audience"];
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps ||
        !Uri.TryCreate(audience, UriKind.Absolute, out var audienceUri) || audienceUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("Token entry validation requires an HTTPS authority and audience.");
    builder.Services.AddEpistemeTokenEntryValidation(authority, audience);
    builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"];
        options.Audience = builder.Configuration["Authentication:Audience"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    });
}
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { }
