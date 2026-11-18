using Episteme.Auth;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Threading.RateLimiting;
using System.Security.Cryptography.X509Certificates;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Episteme.Auth;

public sealed class AuthProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var connection = builder.Configuration.GetConnectionString("Episteme");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ConnectionStrings:Episteme must be configured.");
        var issuer = builder.Configuration["Authentication:Issuer"];
        var apiAudience = builder.Configuration["Authentication:ApiAudience"];
        var mcpAudience = builder.Configuration["Authentication:McpAudience"];
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ||
            !Uri.TryCreate(apiAudience, UriKind.Absolute, out var apiUri) ||
            !Uri.TryCreate(mcpAudience, UriKind.Absolute, out var mcpUri) ||
            apiUri == mcpUri ||
            (issuerUri.Scheme != Uri.UriSchemeHttps && issuerUri.Scheme != Uri.UriSchemeHttp) ||
            (apiUri.Scheme != Uri.UriSchemeHttps && apiUri.Scheme != Uri.UriSchemeHttp) ||
            (mcpUri.Scheme != Uri.UriSchemeHttps && mcpUri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(issuerUri.UserInfo) || !string.IsNullOrEmpty(issuerUri.Query) || !string.IsNullOrEmpty(issuerUri.Fragment) ||
            !string.IsNullOrEmpty(apiUri.UserInfo) || !string.IsNullOrEmpty(apiUri.Query) || !string.IsNullOrEmpty(apiUri.Fragment) ||
            !string.IsNullOrEmpty(mcpUri.UserInfo) || !string.IsNullOrEmpty(mcpUri.Query) || !string.IsNullOrEmpty(mcpUri.Fragment) ||
            mcpUri.AbsolutePath != "/mcp" ||
            (!builder.Environment.IsDevelopment() &&
             (issuerUri.Scheme != Uri.UriSchemeHttps || apiUri.Scheme != Uri.UriSchemeHttps || mcpUri.Scheme != Uri.UriSchemeHttps)))
            throw new InvalidOperationException("Issuer and distinct resource audiences must be absolute URLs without credentials, query or fragment; MCP uses /mcp and HTTPS is required in production.");

        var mail = IdentityMailOptions.FromConfiguration(builder.Configuration);
        if (mail.Enabled && (issuerUri.Scheme != Uri.UriSchemeHttps || issuerUri.AbsolutePath != "/"))
            throw new InvalidOperationException("Email account flows require an HTTPS issuer at the host root.");
        if (mail.Enabled)
        {
            builder.Services.AddSingleton(mail);
            builder.Services.AddTransient<IEmailSender, SmtpIdentityEmailSender>();
        }
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path;
                var limited = HttpMethods.IsPost(context.Request.Method) &&
                    (path.StartsWithSegments("/Identity/Account/Login") ||
                     path.StartsWithSegments("/Identity/Account/Register") ||
                     path.StartsWithSegments("/Identity/Account/ForgotPassword") ||
                     path.StartsWithSegments("/Identity/Account/ResetPassword") ||
                     path.StartsWithSegments("/Identity/Account/ResendEmailConfirmation") ||
                     path.StartsWithSegments("/connect/token"));
                return limited
                    ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 20,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        })
                    : RateLimitPartition.GetNoLimiter("unlimited");
            });
        });
        builder.Services.AddDbContext<EpistemeDbContext>(options =>
        {
            options.UseNpgsql(connection);
            options.UseOpenIddict();
        });
        builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            {
                options.SignIn.RequireConfirmedAccount = mail.Enabled;
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<EpistemeDbContext>()
            .AddDefaultTokenProviders()
            .AddDefaultUI();
        builder.Services.Replace(ServiceDescriptor.Scoped<SignInManager<ApplicationUser>, AccountSignInManager>());
        builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        if (!builder.Environment.IsDevelopment())
        {
            var keyRingPath = builder.Configuration["Authentication:DataProtection:KeyRingPath"];
            var encryptionPath = builder.Configuration["Authentication:EncryptionCertificate:Path"];
            var encryptionPassword = builder.Configuration["Authentication:EncryptionCertificate:Password"];
            if (string.IsNullOrWhiteSpace(keyRingPath) || !Path.IsPathFullyQualified(keyRingPath) ||
                !Directory.Exists(keyRingPath) || new DirectoryInfo(keyRingPath).LinkTarget is not null ||
                string.IsNullOrWhiteSpace(encryptionPath))
                throw new InvalidOperationException("Production requires an existing absolute, non-symlink Data Protection key directory and an encryption certificate.");
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                encryptionPath, encryptionPassword, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException("The encryption certificate must include a private key.");
            builder.Services.AddDataProtection().SetApplicationName("Episteme.Auth")
                .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
                .ProtectKeysWithCertificate(certificate);
        }
        builder.Services.AddControllersWithViews();
        builder.Services.AddRazorPages();
        builder.Services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<EpistemeDbContext>())
            .AddServer(options =>
            {
                options.RemoveEventHandler(OpenIddict.Server.OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor);
                options.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.HandleRevocationRequestContext>(builder =>
                    builder.UseScopedHandler<AuditedRevocationHandler>()
                        .SetOrder(OpenIddict.Server.OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor.Order));
                options.SetIssuer(issuerUri);
                options.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke");
                options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
                options.RequireProofKeyForCodeExchange();
                options.Configure(settings =>
                {
                    settings.CodeChallengeMethods.Clear();
                    settings.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256);
                });
                options.RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.Roles,
                    "workspaces.read", "workspaces.write", "workspaces.manage", "tokens.manage", "users.manage");
                options.RegisterResources(apiAudience!, mcpAudience!);
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(7));
                options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                options.DisableAccessTokenEncryption();
                if (builder.Environment.IsDevelopment())
                    options.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
                else
                {
                    var signingPath = builder.Configuration["Authentication:SigningCertificate:Path"];
                    var signingPassword = builder.Configuration["Authentication:SigningCertificate:Password"];
                    var encryptionPath = builder.Configuration["Authentication:EncryptionCertificate:Path"];
                    var encryptionPassword = builder.Configuration["Authentication:EncryptionCertificate:Password"];
                    if (string.IsNullOrEmpty(signingPath) || string.IsNullOrEmpty(encryptionPath))
                        throw new InvalidOperationException("Production signing and encryption certificate paths must be configured.");
                    using var signingStream = File.OpenRead(signingPath);
                    using var encryptionStream = File.OpenRead(encryptionPath);
                    options.AddSigningCertificate(signingStream, signingPassword)
                        .AddEncryptionCertificate(encryptionStream, encryptionPassword);
                }
                var host = options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
                if (builder.Environment.IsDevelopment())
                    host.DisableTransportSecurityRequirement();
            });
        builder.Services.AddHostedService<ClientProvisioningService>();
        builder.Services.AddHostedService<AdministratorBootstrapService>();

        var app = builder.Build();
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/Identity/Account"))
            {
                if (!mail.Enabled &&
                    ((!app.Environment.IsDevelopment() && path.StartsWithSegments("/Identity/Account/Register")) ||
                     path.StartsWithSegments("/Identity/Account/ForgotPassword") ||
                    path.StartsWithSegments("/Identity/Account/ResetPassword") ||
                     path.StartsWithSegments("/Identity/Account/ResendEmailConfirmation")))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (mail.Enabled && (!context.Request.IsHttps ||
                    !string.Equals(context.Request.Host.Host, issuerUri.Host, StringComparison.OrdinalIgnoreCase) ||
                    (context.Request.Host.Port ?? 443) != issuerUri.Port))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
            await next();
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStaticAssets();
        app.MapControllers();
        app.MapRazorPages();
        app.Run();
    }
}
