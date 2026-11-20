using System.Net;
using System.Text.RegularExpressions;
using Episteme.Auth;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Episteme.IntegrationTests;

public sealed class FirstAdministratorTests
{
    [PostgresFact]
    public async Task Concurrent_registrations_assign_one_administrator_and_do_not_rebootstrap()
    {
        await using var environment = await TestEnvironment.CreateAsync(false);
        using var first = environment.CreateClient();
        using var second = environment.CreateClient();
        var firstPage = await first.GetStringAsync("/Identity/Account/Register");
        var secondPage = await second.GetStringAsync("/Identity/Account/Register");
        var results = await Task.WhenAll(
            first.PostAsync("/Identity/Account/Register", Form(firstPage, "first@example.test")),
            second.PostAsync("/Identity/Account/Register", Form(secondPage, "second@example.test")));
        Assert.All(results, result => Assert.Equal(HttpStatusCode.Redirect, result.StatusCode));

        await using (var scope = environment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            var role = await db.Roles.SingleAsync(r => r.NormalizedName == "ADMINISTRATOR");
            var assignment = await db.UserRoles.SingleAsync(r => r.RoleId == role.Id);
            Assert.Equal(1, await db.AccountAuditEvents.CountAsync(a => a.EventType == "user.administrator.bootstrapped"));
            db.UserRoles.Remove(assignment);
            await db.SaveChangesAsync();
        }

        using var third = environment.CreateClient();
        var page = await third.GetStringAsync("/Identity/Account/Register");
        using var registration = await third.PostAsync("/Identity/Account/Register", Form(page, "third@example.test"));
        Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        await using var verify = environment.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<EpistemeDbContext>();
        Assert.Empty(await verifyDb.UserRoles.ToListAsync());
    }

    [PostgresFact]
    public async Task Email_confirmation_is_required_before_first_login_and_administrator_assignment()
    {
        await using var environment = await TestEnvironment.CreateAsync(true);
        using var client = environment.CreateClient();
        var page = await client.GetStringAsync("/Identity/Account/Register");
        using var registration = await client.PostAsync("/Identity/Account/Register", Form(page, "mail@example.test"));
        Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        Assert.Single(environment.Sender.Messages);
        var login = await client.GetStringAsync("/Identity/Account/Login");
        using var denied = await client.PostAsync("/Identity/Account/Login", LoginForm(login, "mail@example.test"));
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        await using (var scope = environment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            Assert.Empty(await db.UserRoles.ToListAsync());
            var user = await db.Users.SingleAsync();
            Assert.False(user.EmailConfirmed);
        }

        var match = Regex.Match(environment.Sender.Messages[0], "href=['\"]([^'\"]+)['\"]");
        Assert.True(match.Success);
        using var confirmation = await client.GetAsync(WebUtility.HtmlDecode(match.Groups[1].Value));
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        login = await client.GetStringAsync("/Identity/Account/Login");
        using var accepted = await client.PostAsync("/Identity/Account/Login", LoginForm(login, "mail@example.test"));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        await using var verify = environment.Factory.Services.CreateAsyncScope();
        var users = verify.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var confirmed = await users.FindByEmailAsync("mail@example.test");
        Assert.NotNull(confirmed);
        Assert.True(confirmed.EmailConfirmed);
        Assert.True(await users.IsInRoleAsync(confirmed, "Administrator"));
    }

    private static FormUrlEncodedContent Form(string html, string email) =>
        Fields(html, ("Input.Email", email), ("Input.Password", "Episteme-Test-Password-42!"),
            ("Input.ConfirmPassword", "Episteme-Test-Password-42!"));

    private static FormUrlEncodedContent LoginForm(string html, string email) =>
        Fields(html, ("Input.Email", email), ("Input.Password", "Episteme-Test-Password-42!"));

    private static FormUrlEncodedContent Fields(string html, params (string Name, string Value)[] fields)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        var values = fields.ToDictionary(field => field.Name, field => field.Value);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
        return new(values);
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<string> Messages { get; } = [];
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            Messages.Add(htmlMessage);
            return Task.CompletedTask;
        }
    }

    private sealed class AuthFactory(string connection, bool email, CapturingSender sender) : WebApplicationFactory<AuthProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Issuer", "https://auth.example/");
            builder.UseSetting("Authentication:ApiAudience", "https://api.example/");
            builder.UseSetting("Authentication:McpAudience", "https://mcp.example/mcp");
            if (email)
            {
                builder.UseSetting("Authentication:Email:Enabled", "true");
                builder.UseSetting("Authentication:Email:Host", "smtp.example");
                builder.UseSetting("Authentication:Email:From", "sender@example.test");
                builder.ConfigureTestServices(services =>
                    services.Replace(ServiceDescriptor.Singleton<IEmailSender>(sender)));
            }
        }
    }

    private sealed class TestEnvironment(string connection, string schema, AuthFactory factory, CapturingSender sender) : IAsyncDisposable
    {
        public AuthFactory Factory { get; } = factory;
        public CapturingSender Sender { get; } = sender;

        public static async Task<TestEnvironment> CreateAsync(bool email)
        {
            var original = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
            var schema = "first_admin_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(original))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
                await create.ExecuteNonQueryAsync();
            }
            var builder = new NpgsqlConnectionStringBuilder(original) { SearchPath = schema };
            var sender = new CapturingSender();
            var factory = new AuthFactory(builder.ConnectionString, email, sender);
            await using (var scope = factory.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<EpistemeDbContext>().Database.MigrateAsync();
            return new TestEnvironment(original, schema, factory, sender);
        }

        public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await using var db = new NpgsqlConnection(connection);
            await db.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", db);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
