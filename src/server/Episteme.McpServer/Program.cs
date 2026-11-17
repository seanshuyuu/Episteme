using Episteme.Application.Workspaces;
using Episteme.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.AspNetCore.Authentication;
using OpenIddict.Validation.AspNetCore;

namespace Episteme.McpServer;

public sealed class McpProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // SDK transport diagnostics may contain complete JSON-RPC payloads.
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
        builder.Services.AddEpistemeInfrastructure(builder.Configuration);
        builder.Services.AddScoped<WorkspaceAccessService>();
        builder.Services.AddScoped<IWorkspaceListingService, WorkspaceListingService>();
        builder.Services.AddScoped<IWorkspaceManagementService, WorkspaceManagementService>();
        builder.Services.AddScoped<IWorkspaceMemberService, WorkspaceMemberService>();
        builder.Services.AddScoped<IWorkspaceCreationService, WorkspaceCreationService>();
        builder.Services.AddScoped<IWorkspaceContentService, WorkspaceContentService>();
        builder.Services.AddScoped<IWorkspaceManifestService, WorkspaceManifestService>();
        builder.Services.AddScoped<IUserWorkspaceSettingsService, UserWorkspaceSettingsService>();
        var authority = builder.Configuration["Authentication:Authority"];
        var resource = builder.Configuration["Authentication:Audience"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
            !Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) ||
            resourceUri.AbsolutePath != "/mcp" ||
            !string.IsNullOrEmpty(authorityUri.UserInfo) || !string.IsNullOrEmpty(resourceUri.UserInfo) ||
            !string.IsNullOrEmpty(authorityUri.Query) || !string.IsNullOrEmpty(authorityUri.Fragment) ||
            !string.IsNullOrEmpty(resourceUri.Query) || !string.IsNullOrEmpty(resourceUri.Fragment) ||
            (!builder.Environment.IsDevelopment() &&
                (authorityUri.Scheme != Uri.UriSchemeHttps || resourceUri.Scheme != Uri.UriSchemeHttps)))
            throw new InvalidOperationException("Authentication:Authority must be an absolute issuer URL and Authentication:Audience must be the absolute /mcp URL; HTTPS is required in production.");
        var requireTokenEntry = builder.Configuration.GetValue<bool>("Authentication:RequireTokenEntryValidation");
        if (requireTokenEntry)
        {
            if (authorityUri.Scheme != Uri.UriSchemeHttps || resourceUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Token entry validation requires an HTTPS authority and audience.");
            builder.Services.AddEpistemeTokenEntryValidation(authority!, resource!);
        }
        var authentication = builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = requireTokenEntry
                    ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                    : JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            });
        if (!requireTokenEntry)
            authentication.AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = resource;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            });
        authentication.AddMcp(options =>
            {
                options.ResourceMetadataUri = new Uri(resourceUri, "/.well-known/oauth-protected-resource" + resourceUri.AbsolutePath);
                options.ResourceMetadata = new()
                {
                    Resource = resource,
                    AuthorizationServers = { authority },
                    ScopesSupported = ["workspaces.read", "workspaces.write", "workspaces.manage"]
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<WorkspaceTools>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp("/mcp").RequireAuthorization();
        app.Run();
    }
}
