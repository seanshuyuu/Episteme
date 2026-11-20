using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Episteme.Application.Workspaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Episteme.IntegrationTests;

public sealed class McpSettingsApiTests
{
    [Fact]
    public async Task Settings_endpoints_require_authentication_scope_and_valid_permissions()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var workspaceId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var url = $"/api/v1/mcp/settings/workspaces/{workspaceId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{url}")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Subject", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "tokens.manage");
        var invalidNumeric = await client.PutAsJsonAsync($"{url}/personal",
            new { permissions = new[] { "2048" } });
        Assert.True(invalidNumeric.StatusCode == HttpStatusCode.BadRequest,
            await invalidNumeric.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/personal",
            new { permissions = new[] { "ReadDocument, DeleteDocument" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/personal",
            new { permissions = new[] { "CreateWorkspace" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/enabled", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/mcp/settings/personal",
            new { enabled = true })).StatusCode);

        var granted = await client.PutAsJsonAsync($"{url}/members/{memberId}",
            new { permissions = new[] { "ReadDocument", "ReadWorkspace" } });
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var response = await granted.Content.ReadFromJsonAsync<PermissionResult>();
        Assert.Equal(new[] { "ReadWorkspace", "ReadDocument" }, response!.Permissions);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}/members/{Guid.Empty}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/members/{memberId}")).StatusCode);
    }

    private sealed record PermissionResult(string[] Permissions);

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMcpSettingsRepository>();
                services.AddSingleton<IMcpSettingsRepository, FakeRepository>();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            });
        }
    }

    private sealed class FakeRepository : IMcpSettingsRepository
    {
        public Task<McpSettingsResult<McpPersonalSettings>> GetPersonalAsync(WorkspaceActor actor, CancellationToken cancellationToken) =>
            Task.FromResult(new McpSettingsResult<McpPersonalSettings>(new(false, false), null));
        public Task<McpSettingsResult<McpPersonalSettings>> SetPersonalAsync(WorkspaceActor actor, McpPersonalSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(new McpSettingsResult<McpPersonalSettings>(settings, null));
        public Task<McpSettingsResult<McpWorkspaceSettingsView>> GetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, CancellationToken cancellationToken) =>
            Task.FromResult(new McpSettingsResult<McpWorkspaceSettingsView>(new(false, McpPermission.None, McpPermission.None, true), null));
        public Task<McpSettingsResult<McpWorkspaceSettingsView>> SetWorkspaceAsync(WorkspaceActor actor, Guid workspaceId, McpPermission permissions, CancellationToken cancellationToken) =>
            Task.FromResult(new McpSettingsResult<McpWorkspaceSettingsView>(new(false, permissions, McpPermission.None, true), null));
        public Task<McpSettingsResult<bool>> SetWorkspaceEnabledAsync(WorkspaceActor actor, Guid workspaceId, bool enabled, CancellationToken cancellationToken) =>
            Task.FromResult(new McpSettingsResult<bool>(enabled, null));
        public Task<McpSettingsResult<McpPermission>> GetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, CancellationToken cancellationToken) =>
            Task.FromResult(new McpSettingsResult<McpPermission>(McpPermission.None, McpSettingsFailure.NotFound));
        public Task<McpSettingsResult<McpPermission>> SetMemberGrantAsync(WorkspaceActor actor, Guid workspaceId, Guid memberId, McpPermission permissions, CancellationToken cancellationToken) =>
            Task.FromResult(new McpSettingsResult<McpPermission>(permissions, null));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestMcpSettings";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Subject", out var subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("sub", subject.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Scope", out var scope))
                claims.Add(new Claim("scope", scope.ToString()));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
