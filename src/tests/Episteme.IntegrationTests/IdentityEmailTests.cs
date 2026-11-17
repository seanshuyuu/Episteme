using System.Net;
using System.Text.RegularExpressions;
using Episteme.Auth;
using Episteme.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Episteme.IntegrationTests;

public sealed class IdentityEmailTests
{
    [PostgresFact]
    public async Task Registration_confirmation_and_password_recovery_use_real_identity_tokens()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var sender = new CapturingSender();
        await using var factory = new EmailAuthFactory(connection, sender);
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Episteme-Test-Password-42!";
        const string replacement = "Episteme-New-Password-42!";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        try
        {
            var register = await browser.GetStringAsync("/Identity/Account/Register");
            using var registration = await browser.PostAsync("/Identity/Account/Register", Form(
                register, ("Input.Email", email), ("Input.Password", password), ("Input.ConfirmPassword", password)));
            Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
            Assert.Single(sender.Messages);
            Assert.Equal("Confirm your email", sender.Subjects[0]);
            var user = await users.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.False(user.EmailConfirmed);
            var confirmationPage = await browser.GetStringAsync(registration.Headers.Location!);
            Assert.DoesNotContain("code=", confirmationPage, StringComparison.OrdinalIgnoreCase);

            var login = await browser.GetStringAsync("/Identity/Account/Login");
            using var denied = await browser.PostAsync("/Identity/Account/Login", Form(
                login, ("Input.Email", email), ("Input.Password", password)));
            Assert.Equal(HttpStatusCode.OK, denied.StatusCode);

            var resendPage = await browser.GetStringAsync("/Identity/Account/ResendEmailConfirmation");
            using var resend = await browser.PostAsync("/Identity/Account/ResendEmailConfirmation",
                Form(resendPage, ("Input.Email", email)));
            Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
            Assert.Equal(2, sender.Messages.Count);
            var confirmation = Link(sender.Messages[1]);
            Assert.Equal("auth.example", confirmation.Host);
            using var confirmed = await browser.GetAsync(confirmation);
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var refreshed = await verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
                Assert.True(refreshed!.EmailConfirmed);
            }
            var confirmedLoginPage = await browser.GetStringAsync("/Identity/Account/Login");
            using var confirmedLogin = await browser.PostAsync("/Identity/Account/Login",
                Form(confirmedLoginPage, ("Input.Email", email), ("Input.Password", password)));
            Assert.Equal(HttpStatusCode.Redirect, confirmedLogin.StatusCode);

            var forgot = await browser.GetStringAsync("/Identity/Account/ForgotPassword");
            using var recovery = await browser.PostAsync("/Identity/Account/ForgotPassword", Form(forgot, ("Input.Email", email)));
            Assert.Equal(HttpStatusCode.Redirect, recovery.StatusCode);
            Assert.Equal(3, sender.Messages.Count);
            Assert.Equal("Reset your password", sender.Subjects[2]);
            var resetUrl = Link(sender.Messages[2]);
            var resetPage = await browser.GetStringAsync(resetUrl);
            var encodedCode = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(resetUrl.Query)["code"].ToString();
            var code = System.Text.Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(encodedCode));
            using var reset = await browser.PostAsync("/Identity/Account/ResetPassword", Form(resetPage,
                ("Input.Email", email), ("Input.Password", replacement), ("Input.ConfirmPassword", replacement),
                ("Input.Code", code)));
            Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var manager = verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var refreshed = await manager.FindByEmailAsync(email);
                Assert.True(await manager.CheckPasswordAsync(refreshed!, replacement));
                Assert.False(await manager.CheckPasswordAsync(refreshed!, password));
            }
            var replayPage = await browser.GetStringAsync(resetUrl);
            using var replay = await browser.PostAsync("/Identity/Account/ResetPassword", Form(replayPage,
                ("Input.Email", email), ("Input.Password", password), ("Input.ConfirmPassword", password),
                ("Input.Code", code)));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

            var unknownPage = await browser.GetStringAsync("/Identity/Account/ForgotPassword");
            using var unknown = await browser.PostAsync("/Identity/Account/ForgotPassword",
                Form(unknownPage, ("Input.Email", $"{Guid.NewGuid():N}@example.test")));
            Assert.Equal(recovery.Headers.Location, unknown.Headers.Location);
            Assert.Equal(3, sender.Messages.Count);

            using var poisoned = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/Identity/Account/Register");
            using var poisonedResponse = await browser.SendAsync(poisoned);
            Assert.Equal(HttpStatusCode.BadRequest, poisonedResponse.StatusCode);
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var manager = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await manager.FindByEmailAsync(email);
            if (user is not null)
                Assert.True((await manager.DeleteAsync(user)).Succeeded);
        }
    }

    [Fact]
    public void Smtp_configuration_is_opt_in_and_requires_complete_credentials()
    {
        static IConfiguration Config(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.False(IdentityMailOptions.FromConfiguration(Config([])).Enabled);
        var values = new Dictionary<string, string?>
        {
            ["Authentication:Email:Enabled"] = "true",
            ["Authentication:Email:Host"] = "smtp.example",
            ["Authentication:Email:From"] = "sender@example.test",
            ["Authentication:Email:UserName"] = "user"
        };
        Assert.Throws<InvalidOperationException>(() => IdentityMailOptions.FromConfiguration(Config(values)));
        values["Authentication:Email:Password"] = "secret";
        var options = IdentityMailOptions.FromConfiguration(Config(values));
        Assert.Equal(587, options.Port);
        Assert.DoesNotContain("secret", options.ToString());
        Assert.Contains("&lt;script&gt;", IdentityEmailTemplate.Render("<script>", "test"));
    }

    [PostgresFact]
    public async Task Registration_reports_delivery_failure_and_allows_resending()
    {
        var sender = new CapturingSender { Fail = true };
        await using var factory = new EmailAuthFactory(Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!, sender);
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Episteme-Test-Password-42!";
        try
        {
            var page = await browser.GetStringAsync("/Identity/Account/Register");
            using var registration = await browser.PostAsync("/Identity/Account/Register", Form(page,
                ("Input.Email", email), ("Input.Password", password), ("Input.ConfirmPassword", password)));
            Assert.Equal(HttpStatusCode.InternalServerError, registration.StatusCode);
            sender.Fail = false;
            var resendPage = await browser.GetStringAsync("/Identity/Account/ResendEmailConfirmation");
            using var resend = await browser.PostAsync("/Identity/Account/ResendEmailConfirmation",
                Form(resendPage, ("Input.Email", email)));
            Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
            Assert.Single(sender.Messages);
            using var confirmation = await browser.GetAsync(Link(sender.Messages[0]));
            Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
            await using var scope = factory.Services.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await manager.FindByEmailAsync(email))!.EmailConfirmed);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await manager.FindByEmailAsync(email);
            if (user is not null)
                Assert.True((await manager.DeleteAsync(user)).Succeeded);
        }
    }

    [Fact]
    public async Task Smtp_does_not_send_without_starttls()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 localhost ESMTP");
                Assert.StartsWith("EHLO", await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("250 localhost");
                return await reader.ReadLineAsync(timeout.Token);
            });
            var sender = new SmtpIdentityEmailSender(new IdentityMailOptions(true, "127.0.0.1", port, "sender@example.test", null, null));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sender.SendEmailAsync("receiver@example.test", "Test", "Sensitive test body"));
            Assert.DoesNotContain("Sensitive test body", error.ToString());
            var nextCommand = await server;
            Assert.True(nextCommand is null || nextCommand.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static FormUrlEncodedContent Form(string html, params (string Name, string Value)[] fields)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        var values = fields.ToDictionary(field => field.Name, field => field.Value);
        values["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
        return new(values);
    }

    private static Uri Link(string html)
    {
        var match = Regex.Match(html, "href=['\"]([^'\"]+)['\"]");
        Assert.True(match.Success);
        return new Uri(WebUtility.HtmlDecode(match.Groups[1].Value));
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<string> Messages { get; } = [];
        public List<string> Subjects { get; } = [];
        public bool Fail { get; set; }
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            if (Fail)
                throw new InvalidOperationException("Simulated delivery failure.");
            Messages.Add(htmlMessage);
            Subjects.Add(subject);
            return Task.CompletedTask;
        }
    }

    private sealed class EmailAuthFactory(string connection, CapturingSender sender) : WebApplicationFactory<AuthProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Issuer", "https://auth.example/");
            builder.UseSetting("Authentication:ApiAudience", "https://api.example/");
            builder.UseSetting("Authentication:McpAudience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:Email:Enabled", "true");
            builder.UseSetting("Authentication:Email:Host", "smtp.example");
            builder.UseSetting("Authentication:Email:From", "sender@example.test");
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IEmailSender>(sender)));
        }
    }
}
