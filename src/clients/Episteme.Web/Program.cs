using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Episteme.Client;
using Episteme.Web.Components;
using Episteme.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using MudBlazor.Services;
using Refit;

var builder = WebApplication.CreateBuilder(args);

var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority must be configured.");
var clientId = builder.Configuration["Authentication:ClientId"]
    ?? throw new InvalidOperationException("Authentication:ClientId must be configured.");
var apiAudience = builder.Configuration["Authentication:ApiAudience"]
    ?? throw new InvalidOperationException("Authentication:ApiAudience must be configured.");
var metadataAddress = builder.Configuration["Authentication:MetadataAddress"];
var apiBaseUrl = builder.Configuration["Api:BaseUrl"] ?? apiAudience;
if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiUri))
    throw new InvalidOperationException("Api:BaseUrl must be an absolute URL.");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("mcp-token").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    { AllowAutoRedirect = false });
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddScoped<SessionTokenProvider>();
builder.Services.AddScoped<McpTokenClient>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<WebCookieAuthenticationEvents>();
builder.Services.AddScoped(provider => new HttpClient(new ApiTokenHandler(provider.GetRequiredService<SessionTokenProvider>())
{
    InnerHandler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(string.Empty)
})
{
    BaseAddress = apiUri
});
builder.Services.AddScoped(provider => RestService.For<IEpistemeApi>(provider.GetRequiredService<HttpClient>()));

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.EventsType = typeof(WebCookieAuthenticationEvents);
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        if (!string.IsNullOrWhiteSpace(metadataAddress))
            options.MetadataAddress = metadataAddress;
        options.ClientId = clientId;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = true;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.Scope.Clear();
        foreach (var scope in new[]
                 {
                     "openid", "profile", "email", "roles", "offline_access",
                     "workspaces.read", "workspaces.write", "workspaces.manage", "tokens.manage", "users.manage"
                 })
            options.Scope.Add(scope);
        options.Resource = apiAudience;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.AuthorizationEndpoint = authority.TrimEnd('/') + "/connect/authorize";
            context.ProtocolMessage.IssuerAddress = authority.TrimEnd('/') + "/connect/authorize";
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.IssuerAddress = authority.TrimEnd('/') + "/connect/logout";
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
        {
            var accessToken = context.TokenEndpointResponse?.AccessToken;
            if (string.IsNullOrEmpty(accessToken))
                return Task.CompletedTask;
            var jwt = new JwtSecurityToken(accessToken);
            if (context.Principal?.Identity is not ClaimsIdentity identity)
                return Task.CompletedTask;
            foreach (var role in jwt.Claims.Where(claim => claim.Type == "role"))
                identity.AddClaim(new Claim(ClaimTypes.Role, role.Value));
            identity.AddClaim(new Claim(WebTokenClaimNames.AccessToken, accessToken));
            identity.AddClaim(new Claim(WebTokenClaimNames.ExpiresAt,
                jwt.ValidTo.ToString("O", CultureInfo.InvariantCulture)));
            return Task.CompletedTask;
        };
    });

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/authentication/login", async (HttpContext context, string? returnUrl) =>
{
    var properties = new AuthenticationProperties
    {
        RedirectUri = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//")
            ? "/"
            : returnUrl
    };
    await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, properties);
});

app.MapGet("/authentication/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Redirect("/");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program { }
