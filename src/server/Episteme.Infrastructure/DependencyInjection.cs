using Episteme.Application.Workspaces;
using Episteme.Application.Identity;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Episteme.Infrastructure.Git;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Episteme.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEpistemeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Episteme");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Episteme must be configured.");

        services.AddDbContext<EpistemeDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });

        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<EpistemeDbContext>()
            .AddDefaultTokenProviders();

        // Register OpenIddict's supported persistence stores now. Token endpoint/client
        // registration and key management are intentionally owned by the future auth host.
        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore()
                .UseDbContext<EpistemeDbContext>());

        services.AddScoped<IWorkspaceAccessRepository, EfWorkspaceAccessRepository>();
        services.AddScoped<IWorkspaceListingRepository, EfWorkspaceListingRepository>();
        services.AddScoped<IWorkspaceManagementRepository, EfWorkspaceManagementRepository>();
        services.AddScoped<IWorkspaceMemberRepository, EfWorkspaceMemberRepository>();
        services.AddScoped<IWorkspaceContentRepository, EfWorkspaceContentRepository>();
        services.AddScoped<IWorkspaceManifestRepository, EfWorkspaceContentRepository>();
        services.AddScoped<GitWorkspaceReader>();
        services.AddScoped<IWorkspaceCreationRepository, EfWorkspaceCreationRepository>();
        services.AddScoped<IProjectRepository, EfProjectRepository>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IWorkspaceRepositoryProvisioner, GitWorkspaceProvisioner>();
        services.AddScoped<IUserWorkspaceSettingsRepository, EfUserWorkspaceSettingsRepository>();
        services.AddScoped<IUserTokenRepository, EfUserTokenRepository>();
        services.AddScoped<IUserAuthorizationRepository, EfUserAuthorizationRepository>();
        services.AddScoped<IUserAdministrationRepository, EfUserAdministrationRepository>();
        return services;
    }
}
