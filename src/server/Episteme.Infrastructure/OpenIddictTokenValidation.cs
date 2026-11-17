using Microsoft.Extensions.DependencyInjection;
using Episteme.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;

namespace Episteme.Infrastructure;

public static class OpenIddictTokenValidation
{
    public static IServiceCollection AddEpistemeTokenEntryValidation(
        this IServiceCollection services, string issuer, string audience)
    {
        services.AddOpenIddict().AddValidation(options =>
        {
            options.SetIssuer(issuer);
            options.AddAudiences(audience);
            options.EnableTokenEntryValidation();
            options.EnableAuthorizationEntryValidation();
            options.UseSystemNetHttp();
            options.UseAspNetCore();
        });
        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, CurrentUserRolesTransformation>();
        return services;
    }
}
