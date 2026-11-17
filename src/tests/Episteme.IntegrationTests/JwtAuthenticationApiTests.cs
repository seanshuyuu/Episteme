using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Episteme.IntegrationTests;

public sealed class JwtAuthenticationApiTests
{
    [PostgresFact]
    public async Task Real_bearer_validation_enforces_signature_issuer_audience_expiry_scope_and_user_status()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"jwt-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "JWT test", $"jwt-{Guid.NewGuid():N}", WorkspaceType.Code);
        db.Users.Add(owner);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(workspace.Id, owner.Id));
        await db.SaveChangesAsync();
        using var key = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        try
        {
            await using var factory = new JwtApiFactory(connection, new RsaSecurityKey(key));
            using var client = factory.CreateClient();
            var url = $"/api/v1/workspaces/{workspace.Id}";
            async Task<HttpStatusCode> GetAsync(string token)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                return response.StatusCode;
            }

            string Token(RSA signingKey, string issuer = "https://issuer.example",
                string audience = "episteme-api", string scope = "workspaces.read", int expirationMinutes = 5)
            {
                var jwt = new JwtSecurityToken(issuer, audience,
                    [new Claim("sub", owner.Id.ToString()), new Claim("scope", scope)],
                    expires: DateTime.UtcNow.AddMinutes(expirationMinutes),
                    signingCredentials: new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
                return new JwtSecurityTokenHandler().WriteToken(jwt);
            }

            Assert.Equal(HttpStatusCode.OK, await GetAsync(Token(key)));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Token(wrongKey)));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Token(key, issuer: "https://wrong.example")));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Token(key, audience: "wrong-api")));
            Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Token(key, expirationMinutes: -10)));
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(Token(key, scope: "workspaces.manage")));
            await db.Users.Where(u => u.Id == owner.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.Status, UserAccountStatus.Disabled));
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(Token(key)));
        }
        finally
        {
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
        }
    }

    private sealed class JwtApiFactory(string connection, SecurityKey key) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.Audience = null;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = "https://issuer.example",
                        ValidateAudience = true,
                        ValidAudience = "episteme-api",
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = key,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero
                    };
                }));
        }
    }
}
