using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Episteme.Auth;
using Episteme.Application.Identity;
using Episteme.Domain.Identity;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Episteme.McpServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Abstractions;

namespace Episteme.IntegrationTests;

public sealed class OpenIddictFlowTests
{
    [Fact]
    public async Task Login_page_serves_its_styles_and_available_account_links()
    {
        await using var factory = new StaticAssetsAuthFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Identity/Account/Login");
        await AssertLoginAssetsAsync(client, html);
        Assert.Contains("/Identity/Account/Register", html);
        Assert.DoesNotContain("/Identity/Account/ForgotPassword", html);
        Assert.DoesNotContain("/Identity/Account/ResendEmailConfirmation", html);
    }

    [PostgresFact]
    public async Task Login_retry_preserves_return_url()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var clientId = $"auth-login-retry-{Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Episteme-Test-Password-42!";
        const string returnUrl = "/Identity/Account/Manage?tab=profile&source=login";
        await using var factory = new AuthFactory(connection, clientId);
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://auth.example"),
            AllowAutoRedirect = false
        });
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var users = setup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(user, password);
            Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Code)));
        }
        try
        {
            var loginUrl = QueryHelpers.AddQueryString("/Identity/Account/Login", "returnUrl", returnUrl);
            var loginPage = await browser.GetStringAsync(loginUrl);
            using var failedLogin = await browser.PostAsync(loginUrl, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = "Incorrect-Password-42!",
                    ["__RequestVerificationToken"] = AntiForgeryToken(loginPage)
                }));
            Assert.Equal(HttpStatusCode.OK, failedLogin.StatusCode);
            var failedPage = await failedLogin.Content.ReadAsStringAsync();
            Assert.Contains("Invalid login attempt.", failedPage);
            var loginForm = Regex.Match(failedPage, "<form[^>]*class=\"login-form\"[^>]*>");
            Assert.True(loginForm.Success, "Login form was not found.");
            var action = Regex.Match(loginForm.Value, "action=\"([^\"]+)\"");
            Assert.True(action.Success, "Login form action was not found.");
            var retryUrl = WebUtility.HtmlDecode(action.Groups[1].Value);
            using var login = await browser.PostAsync(retryUrl, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = password,
                    ["__RequestVerificationToken"] = AntiForgeryToken(failedPage)
                }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Equal(returnUrl, login.Headers.Location?.OriginalString);
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var users = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var currentUser = await users.FindByIdAsync(user.Id.ToString());
            if (currentUser is not null)
                Assert.True((await users.DeleteAsync(currentUser)).Succeeded);
            var apps = cleanup.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await apps.FindByClientIdAsync(clientId);
            if (application is not null)
                await apps.DeleteAsync(application);
        }
    }

    [PostgresFact]
    public async Task Bootstrap_can_create_first_administrator_from_configured_credentials()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        await using var setupFactory = new AuthFactory(connection, $"auth-bootstrap-create-{Guid.NewGuid():N}");
        using var setupClient = setupFactory.CreateClient();
        await using var scope = setupFactory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var roleExisted = await roles.FindByNameAsync("Administrator") is not null;
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Episteme-Bootstrap-Password-42!";
        try
        {
            await using var invalid = new BootstrapCredentialsFactory(connection, email, "weak");
            Assert.Throws<InvalidOperationException>(() => { using var unused = invalid.CreateClient(); });
            Assert.Null(await users.FindByNameAsync(email));
            await using var bootstrap = new BootstrapCredentialsFactory(connection, email, password);
            using var bootstrapClient = bootstrap.CreateClient();
            var user = await users.FindByNameAsync(email);
            Assert.NotNull(user);
            Assert.True(await users.IsInRoleAsync(user, "Administrator"));
            Assert.True(await users.CheckPasswordAsync(user, password));
            Assert.NotEqual(password, user.PasswordHash);
            await using var duplicate = new BootstrapCredentialsFactory(connection, email, password);
            Assert.Throws<InvalidOperationException>(() => { using var unused = duplicate.CreateClient(); });
        }
        finally
        {
            var user = await users.FindByNameAsync(email);
            if (user is not null)
            {
                var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
                await db.AccountAuditEvents.Where(a => a.TargetUserId == user.Id).ExecuteDeleteAsync();
                Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            if (!roleExisted)
            {
                var role = await roles.FindByNameAsync("Administrator");
                if (role is not null)
                    Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await apps.FindByClientIdAsync(setupFactory.ClientId);
            if (application is not null)
                await apps.DeleteAsync(application);
        }
    }

    [PostgresFact]
    public async Task Bootstrap_grants_only_the_first_configured_active_administrator()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        await using var setupFactory = new AuthFactory(connection, $"auth-bootstrap-{Guid.NewGuid():N}");
        using var setupClient = setupFactory.CreateClient();
        await using var scope = setupFactory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var existed = await roles.FindByNameAsync("Administrator") is not null;
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@example.test" };
        var other = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@example.test" };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        Assert.True((await users.CreateAsync(other)).Succeeded);
        try
        {
            await using var bootstrap = new BootstrapAuthFactory(connection, user.Id);
            using var bootstrapClient = bootstrap.CreateClient();
            await using var restarted = new BootstrapAuthFactory(connection, user.Id);
            using var restartedClient = restarted.CreateClient();
            Assert.True(await users.IsInRoleAsync(user, "Administrator"));
            var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            Assert.Equal(1, await db.AccountAuditEvents.CountAsync(a =>
                a.TargetUserId == user.Id && a.EventType == "user.administrator.bootstrapped"));
            await using var conflicting = new BootstrapAuthFactory(connection, other.Id);
            Assert.Throws<InvalidOperationException>(() => { using var unused = conflicting.CreateClient(); });
            Assert.False(await users.IsInRoleAsync(other, "Administrator"));
        }
        finally
        {
            var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            await db.AccountAuditEvents.Where(a => a.TargetUserId == user.Id).ExecuteDeleteAsync();
            Assert.True((await users.DeleteAsync(user)).Succeeded);
            Assert.True((await users.DeleteAsync(other)).Succeeded);
            if (!existed)
            {
                var role = await roles.FindByNameAsync("Administrator");
                Assert.NotNull(role);
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await apps.FindByClientIdAsync(setupFactory.ClientId);
            if (application is not null)
                await apps.DeleteAsync(application);
        }
    }

    [PostgresFact]
    public async Task Concurrent_administrator_removals_cannot_remove_the_last_administrator()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        await using var factory = new AuthFactory(connection, $"auth-admin-race-{Guid.NewGuid():N}");
        using var browser = factory.CreateClient();
        await using var setup = factory.Services.CreateAsyncScope();
        var users = setup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = setup.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = await roles.FindByNameAsync("Administrator");
        var createdRole = role is null;
        if (role is null)
        {
            role = new IdentityRole<Guid>("Administrator");
            Assert.True((await roles.CreateAsync(role)).Succeeded);
        }
        var first = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@example.test" };
        var second = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@example.test" };
        Assert.True((await users.CreateAsync(first)).Succeeded);
        Assert.True((await users.CreateAsync(second)).Succeeded);
        try
        {
            Assert.True((await users.AddToRoleAsync(first, "Administrator")).Succeeded);
            Assert.True((await users.AddToRoleAsync(second, "Administrator")).Succeeded);
            await using var firstScope = factory.Services.CreateAsyncScope();
            await using var secondScope = factory.Services.CreateAsyncScope();
            var firstRepository = firstScope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            var secondRepository = secondScope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            var result = await Task.WhenAll(
                new Episteme.Infrastructure.Persistence.EfUserAdministrationRepository(firstRepository,
                    firstScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
                    firstScope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>())
                    .SetAdministratorAsync(first.Id, second.Id, false, default),
                new Episteme.Infrastructure.Persistence.EfUserAdministrationRepository(secondRepository,
                    secondScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
                    secondScope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>())
                    .SetAdministratorAsync(second.Id, first.Id, false, default));
            Assert.Single(result, item => item.Failure is null);
            Assert.Single(result, item => item.Failure == UserAdministrationFailure.Unauthorized);
            var db = setup.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            Assert.Equal(1, await db.UserRoles.CountAsync(r => r.RoleId == role.Id &&
                (r.UserId == first.Id || r.UserId == second.Id)));
        }
        finally
        {
            var db = setup.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            await db.AccountAuditEvents.Where(a => a.ActorUserId == first.Id || a.ActorUserId == second.Id).ExecuteDeleteAsync();
            await using var cleanup = factory.Services.CreateAsyncScope();
            var cleanupUsers = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var id in new[] { first.Id, second.Id })
            {
                var user = await cleanupUsers.FindByIdAsync(id.ToString());
                if (user is not null)
                    Assert.True((await cleanupUsers.DeleteAsync(user)).Succeeded);
            }
            if (createdRole)
            {
                var cleanupRoles = cleanup.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
                var currentRole = await cleanupRoles.FindByIdAsync(role.Id.ToString());
                if (currentRole is not null)
                    Assert.True((await cleanupRoles.DeleteAsync(currentRole)).Succeeded);
            }
            var applications = setup.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await applications.FindByClientIdAsync(factory.ClientId);
            if (application is not null)
                await applications.DeleteAsync(application);
        }
    }

    [PostgresFact]
    public async Task Nondevelopment_identity_allows_registration_but_disables_password_recovery_without_mail()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var certificatePath = Path.GetTempFileName();
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"episteme-auth-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyRingPath);
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Episteme Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx, "test-only"));
            await using var factory = new NonDevelopmentAuthFactory(connection, certificatePath, keyRingPath);
            using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
            using var loginPage = await browser.GetAsync("/Identity/Account/Login");
            Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
            var html = await loginPage.Content.ReadAsStringAsync();
            await AssertLoginAssetsAsync(browser, html);
            Assert.Contains("/Identity/Account/Register", html);
            Assert.DoesNotContain("/Identity/Account/ForgotPassword", html);
            Assert.DoesNotContain("/Identity/Account/ResendEmailConfirmation", html);
            var antiforgery = AntiForgeryToken(html);
            var cookie = loginPage.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
                .Split(';')[0];
            Assert.NotEmpty(Directory.GetFiles(keyRingPath, "*.xml"));
            Assert.DoesNotContain("masterKey", await File.ReadAllTextAsync(Directory.GetFiles(keyRingPath, "*.xml").Single()),
                StringComparison.OrdinalIgnoreCase);
            await using var secondFactory = new NonDevelopmentAuthFactory(connection, certificatePath, keyRingPath);
            using var secondBrowser = secondFactory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
            using var loginPost = new HttpRequestMessage(HttpMethod.Post, "/Identity/Account/Login");
            loginPost.Headers.Add("Cookie", cookie);
            loginPost.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = "missing@example.test",
                ["Input.Password"] = "not-a-password",
                ["__RequestVerificationToken"] = antiforgery
            });
            Assert.Equal(HttpStatusCode.OK, (await secondBrowser.SendAsync(loginPost)).StatusCode);
            var registerPage = await browser.GetStringAsync("/Identity/Account/Register");
            var email = $"{Guid.NewGuid():N}@example.test";
            try
            {
                using var registered = await browser.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["Input.Email"] = email,
                        ["Input.Password"] = "Episteme-Test-Password-42!",
                        ["Input.ConfirmPassword"] = "Episteme-Test-Password-42!",
                        ["__RequestVerificationToken"] = AntiForgeryToken(registerPage)
                    }));
                Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
                Assert.Contains(registered.Headers.GetValues("Set-Cookie"),
                    value => value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
            }
            finally
            {
                await using var cleanup = factory.Services.CreateAsyncScope();
                var users = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByEmailAsync(email);
                if (user is not null)
                {
                    var db = cleanup.ServiceProvider.GetRequiredService<EpistemeDbContext>();
                    await db.AccountAuditEvents.Where(a => a.ActorUserId == user.Id).ExecuteDeleteAsync();
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
                }
            }
            foreach (var path in new[] { "ForgotPassword", "ResetPassword", "ResendEmailConfirmation" })
                Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/Identity/Account/{path}")).StatusCode);
        }
        finally
        {
            File.Delete(certificatePath);
            Directory.Delete(keyRingPath, recursive: true);
        }
    }

    [PostgresFact]
    public async Task Login_post_requests_are_rate_limited_per_host()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        await using var factory = new AuthFactory(connection, $"auth-rate-{Guid.NewGuid():N}");
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
        await using var scope = factory.Services.CreateAsyncScope();
        try
        {
            for (var i = 0; i < 20; i++)
                Assert.NotEqual(HttpStatusCode.TooManyRequests,
                    (await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent([]))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent([]))).StatusCode);
        }
        finally
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await apps.FindByClientIdAsync(factory.ClientId);
            if (application is not null)
                await apps.DeleteAsync(application);
        }
    }

    [PostgresFact]
    public async Task Identity_registers_new_users_with_hashed_passwords()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        await using var factory = new AuthFactory(connection, $"auth-register-{Guid.NewGuid():N}");
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Episteme-Test-Password-42!";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        try
        {
            const string returnUrl = "/Identity/Account/Manage?tab=profile&source=register";
            var registerUrl = QueryHelpers.AddQueryString("/Identity/Account/Register", "returnUrl", returnUrl);
            var register = await browser.GetStringAsync(registerUrl);
            Assert.Contains("/css/auth-login.css", register);
            Assert.Contains("注册 Episteme", register);
            using var invalid = await browser.PostAsync(registerUrl, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = password,
                    ["Input.ConfirmPassword"] = "mismatch",
                    ["__RequestVerificationToken"] = AntiForgeryToken(register)
                }));
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
            var failedPage = await invalid.Content.ReadAsStringAsync();
            var form = Regex.Match(failedPage, "<form[^>]*class=\"login-form\"[^>]*>");
            Assert.True(form.Success);
            var action = Regex.Match(form.Value, "action=\"([^\"]+)\"");
            Assert.True(action.Success);
            using var response = await browser.PostAsync(WebUtility.HtmlDecode(action.Groups[1].Value), new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = password,
                    ["Input.ConfirmPassword"] = password,
                    ["__RequestVerificationToken"] = AntiForgeryToken(failedPage)
                }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(returnUrl, response.Headers.Location?.OriginalString);
            var user = await users.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.NotEqual(password, user.PasswordHash);
            Assert.True(await users.CheckPasswordAsync(user, password));
        }
        finally
        {
            var user = await users.FindByEmailAsync(email);
            if (user is not null)
                Assert.True((await users.DeleteAsync(user)).Succeeded);
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await apps.FindByClientIdAsync(factory.ClientId);
            if (application is not null)
                await apps.DeleteAsync(application);
        }
    }

    [PostgresFact]
    public async Task Repeated_failed_identity_logins_lock_the_account()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        await using var factory = new AuthFactory(connection, $"auth-lockout-{Guid.NewGuid():N}");
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.IsType<AccountSignInManager>(scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>());
        var email = $"{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        Assert.True((await users.CreateAsync(user, "Episteme-Test-Password-42!")).Succeeded);
        try
        {
            var token = AntiForgeryToken(await browser.GetStringAsync("/Identity/Account/Login"));
            for (var i = 0; i < 5; i++)
            {
                using var response = await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["Input.Email"] = email,
                        ["Input.Password"] = "incorrect",
                        ["__RequestVerificationToken"] = token
                    }));
                if (i < 4)
                    Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
                else
                    Assert.Contains("/Identity/Account/Lockout", response.Headers.Location?.ToString());
            }
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyUsers = verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var storedUser = await verifyUsers.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(storedUser);
            Assert.True(await verifyUsers.IsLockedOutAsync(storedUser));
        }
        finally
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var id = factory.ClientId;
            var application = await apps.FindByClientIdAsync(id);
            if (application is not null)
                await apps.DeleteAsync(application);
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupUsers = cleanupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cleanupUser = await cleanupUsers.FindByIdAsync(user.Id.ToString());
            if (cleanupUser is not null)
                Assert.True((await cleanupUsers.DeleteAsync(cleanupUser)).Succeeded);
        }
    }

    [PostgresFact]
    public async Task Mcp_token_exchange_is_limited_to_enabled_users_and_immediately_revocable()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var clientId = $"auth-mcp-{Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Episteme-Test-Password-42!";
        await using var authFactory = new AuthFactory(connection, clientId);
        using var browser = authFactory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://auth.example"), AllowAutoRedirect = false });
        await using var scope = authFactory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        try
        {
            var loginPage = await browser.GetStringAsync("/Identity/Account/Login");
            using var login = await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password,
                    ["__RequestVerificationToken"] = AntiForgeryToken(loginPage) }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var request = new Dictionary<string, string?>
            {
                ["client_id"] = clientId, ["redirect_uri"] = "https://client.example/callback",
                ["response_type"] = "code", ["scope"] = "openid workspaces.read tokens.manage",
                ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256", ["resource"] = "https://api.example/"
            };
            var consent = await browser.GetStringAsync(QueryHelpers.AddQueryString("/connect/authorize", request));
            var form = request.ToDictionary(kv => kv.Key, kv => kv.Value!);
            form["decision"] = "accept";
            form["__RequestVerificationToken"] = AntiForgeryToken(consent);
            using var approved = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
            var code = QueryHelpers.ParseQuery(approved.Headers.Location!.Query)["code"].ToString();
            using var apiExchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = clientId,
                    ["redirect_uri"] = "https://client.example/callback", ["code"] = code, ["code_verifier"] = verifier }));
            var apiBody = await apiExchange.Content.ReadAsStringAsync();
            Assert.True(apiExchange.IsSuccessStatusCode, apiBody);
            using var apiJson = JsonDocument.Parse(apiBody);
            var accessToken = apiJson.RootElement.GetProperty("access_token").GetString()!;
            await using var api = new ManagedApiFactory(connection, authFactory);
            using var apiClient = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://api.example") });
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var exchangeRequest = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["client_id"] = clientId, ["subject_token"] = accessToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["resource"] = "https://mcp.example/mcp", ["scope"] = "workspaces.read"
            };
            using (var disabled = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(exchangeRequest)))
                Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await apiClient.PutAsJsonAsync("/api/v1/mcp/settings/personal",
                new { enabled = true, allowCreateWorkspace = false })).StatusCode);
            var wrongResource = new Dictionary<string, string>(exchangeRequest) { ["resource"] = "https://api.example/" };
            using (var deniedResource = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(wrongResource)))
                Assert.Equal(HttpStatusCode.BadRequest, deniedResource.StatusCode);
            using var mcpExchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(exchangeRequest));
            var body = await mcpExchange.Content.ReadAsStringAsync();
            Assert.True(mcpExchange.IsSuccessStatusCode, body);
            using var token = JsonDocument.Parse(body);
            Assert.InRange(token.RootElement.GetProperty("expires_in").GetInt32(), 30 * 24 * 3600 - 10, 30 * 24 * 3600);
            Assert.False(token.RootElement.TryGetProperty("refresh_token", out _));
            var longToken = token.RootElement.GetProperty("access_token").GetString()!;
            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(longToken.Split('.')[1]));
            Assert.Equal("https://mcp.example/mcp", payload.RootElement.GetProperty("aud").GetString());
            var tokenId = payload.RootElement.GetProperty("oi_tkn_id").GetString();
            Assert.NotNull(tokenId);
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", longToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await apiClient.GetAsync("/api/v1/me/tokens")).StatusCode);
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var replayExchange = new Dictionary<string, string>(exchangeRequest) { ["subject_token"] = longToken };
            using (var deniedReplay = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(replayExchange)))
                Assert.Equal(HttpStatusCode.BadRequest, deniedReplay.StatusCode);
            await using var mcp = new ManagedMcpFactory(connection, authFactory);
            using var mcpClient = mcp.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://mcp.example") });
            mcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", longToken);
            mcpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using var before = await mcpClient.PostAsJsonAsync("/mcp",
                new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } });
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await apiClient.DeleteAsync($"/api/v1/me/tokens/{tokenId}")).StatusCode);
            using var after = await mcpClient.PostAsJsonAsync("/mcp",
                new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }
        finally
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await apps.FindByClientIdAsync(clientId);
            if (application is not null) await apps.DeleteAsync(application);
            var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            await db.AccountAuditEvents.Where(a => a.ActorUserId == user.Id).ExecuteDeleteAsync();
            await db.McpUserSettings.Where(s => s.UserId == user.Id).ExecuteDeleteAsync();
            await using var cleanup = authFactory.Services.CreateAsyncScope();
            var cleanupUsers = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var current = await cleanupUsers.FindByIdAsync(user.Id.ToString());
            if (current is not null) Assert.True((await cleanupUsers.DeleteAsync(current)).Succeeded);
        }
    }

    [PostgresFact]
    public async Task Authorization_code_with_pkce_issues_jwt_and_rotates_and_revokes_refresh_tokens()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var clientId = $"auth-test-{Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Episteme-Test-Password-42!";
        var auditFailure = new AuditFailureInterceptor();
        await using var authFactory = new AuthFactory(connection, clientId, auditFailure);
        using var browser = authFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://auth.example"),
            AllowAutoRedirect = false
        });
        await using var scope = authFactory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Code)));
        try
        {
            var loginPage = await browser.GetStringAsync("/Identity/Account/Login");
            var loginToken = AntiForgeryToken(loginPage);
            using var login = await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = password,
                    ["__RequestVerificationToken"] = loginToken
                }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

            var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var request = new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = "https://client.example/callback",
                ["response_type"] = "code",
                ["scope"] = "openid offline_access workspaces.read tokens.manage users.manage",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "test-state",
                ["resource"] = "https://api.example/"
            };
            var authorizationUrl = QueryHelpers.AddQueryString("/connect/authorize", request);
            var noPkce = new Dictionary<string, string?>(request);
            noPkce.Remove("code_challenge");
            noPkce.Remove("code_challenge_method");
            using (var missingPkce = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", noPkce)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingPkce.StatusCode);
                Assert.Contains("invalid_request", await missingPkce.Content.ReadAsStringAsync());
            }
            var plainPkce = new Dictionary<string, string?>(request) { ["code_challenge_method"] = "plain" };
            using (var plain = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", plainPkce)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
                Assert.Contains("invalid_request", await plain.Content.ReadAsStringAsync());
            }
            var consent = await browser.GetStringAsync(authorizationUrl);
            Assert.Contains("授权应用访问", consent);
            Assert.Contains("读取工作空间和文档", WebUtility.HtmlDecode(consent));
            Assert.Contains("workspaces.read", consent);
            Assert.Contains("https://api.example/", consent);
            var form = request.ToDictionary(kv => kv.Key, kv => kv.Value!);
            form["decision"] = "accept";
            using (var noCsrf = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(form)))
                Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            form["__RequestVerificationToken"] = AntiForgeryToken(consent);
            using var approved = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
            var responseQuery = QueryHelpers.ParseQuery(approved.Headers.Location!.Query);
            Assert.Equal("test-state", responseQuery["state"].ToString());
            var code = responseQuery["code"].ToString();
            Assert.NotEmpty(code);
            var tokenRequest = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["redirect_uri"] = "https://client.example/callback",
                ["code"] = code,
                ["code_verifier"] = verifier
            };
            var badVerifier = new Dictionary<string, string>(tokenRequest) { ["code_verifier"] = new string('a', 43) };
            using (var invalidProof = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(badVerifier)))
                Assert.Equal(HttpStatusCode.BadRequest, invalidProof.StatusCode);
            using var exchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(tokenRequest));
            var tokenBody = await exchange.Content.ReadAsStringAsync();
            Assert.True(exchange.IsSuccessStatusCode, tokenBody);
            using var token = JsonDocument.Parse(tokenBody);
            var accessToken = token.RootElement.GetProperty("access_token").GetString()!;
            var refreshToken = token.RootElement.GetProperty("refresh_token").GetString()!;
            await using var api = new ApiFactory(connection, authFactory);
            using var apiClient = api.CreateClient();
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var apiResponse = await apiClient.GetAsync("/api/v1/workspaces");
            Assert.True(apiResponse.StatusCode == HttpStatusCode.OK, api.Failure?.ToString() ?? apiResponse.StatusCode.ToString());
            await using var managedApi = new ManagedApiFactory(connection, authFactory);
            using var managedApiClient = managedApi.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://api.example") });
            managedApiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            Assert.Equal(HttpStatusCode.OK, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var adminRole = await roleManager.FindByNameAsync("Administrator");
            var createdRole = adminRole is null;
            if (adminRole is null)
            {
                adminRole = new IdentityRole<Guid>("Administrator");
                Assert.True((await roleManager.CreateAsync(adminRole)).Succeeded);
            }
            var otherUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@example.test" };
            Assert.True((await users.CreateAsync(otherUser)).Succeeded);
            var managedUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@example.test" };
            Assert.True((await users.CreateAsync(managedUser)).Succeeded);
            Guid? createdUserId = null;
            var db = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            var otherWorkspace = Workspace.Create(otherUser.Id, "Other", $"other-{Guid.NewGuid():N}", WorkspaceType.Research);
            db.Workspaces.Add(otherWorkspace);
            db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(otherWorkspace.Id, otherUser.Id));
            await db.SaveChangesAsync();
            try
            {
                var otherPath = $"/api/v1/workspaces/{otherWorkspace.Id}";
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync(otherPath)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync("/api/v1/admin/users")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync($"/api/v1/admin/users/{managedUser.Id}/tokens")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/password", new { password = "Episteme-Recovery-Password-42!" })).StatusCode);
                Assert.True((await users.AddToRoleAsync(user, "Administrator")).Succeeded);
                Assert.Equal(HttpStatusCode.OK, (await managedApiClient.GetAsync(otherPath)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await managedApiClient.GetAsync("/api/v1/admin/users?limit=1")).StatusCode);
                var administrativeTokenManager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
                var targetToken = await administrativeTokenManager.CreateAsync(new OpenIddictTokenDescriptor
                {
                    Subject = managedUser.Id.ToString(),
                    Type = OpenIddictConstants.TokenTypeIdentifiers.AccessToken,
                    Status = OpenIddictConstants.Statuses.Valid
                });
                try
                {
                    var targetTokenId = await administrativeTokenManager.GetIdAsync(targetToken);
                    using var targetTokens = await managedApiClient.GetAsync($"/api/v1/admin/users/{managedUser.Id}/tokens");
                    Assert.Equal(HttpStatusCode.OK, targetTokens.StatusCode);
                    var targetPage = await targetTokens.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Contains(targetPage.GetProperty("items").EnumerateArray(),
                        item => item.GetProperty("id").GetString() == targetTokenId);
                    Assert.Equal(HttpStatusCode.NotFound, (await managedApiClient.DeleteAsync(
                        $"/api/v1/admin/users/{otherUser.Id}/tokens/{targetTokenId}")).StatusCode);
                    Assert.Equal(HttpStatusCode.NoContent, (await managedApiClient.DeleteAsync(
                        $"/api/v1/admin/users/{managedUser.Id}/tokens/{targetTokenId}")).StatusCode);
                    Assert.Equal(HttpStatusCode.NoContent, (await managedApiClient.DeleteAsync(
                        $"/api/v1/admin/users/{managedUser.Id}/tokens/{targetTokenId}")).StatusCode);
                    Assert.Equal(1, await db.AccountAuditEvents.CountAsync(a => a.ActorUserId == user.Id &&
                        a.TargetUserId == managedUser.Id && a.EventType == "token.revoked" && a.TargetTokenId == targetTokenId));
                }
                finally
                {
                    await using var tokenCleanup = authFactory.Services.CreateAsyncScope();
                    var freshTokens = tokenCleanup.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
                    var currentToken = await freshTokens.FindByIdAsync((await administrativeTokenManager.GetIdAsync(targetToken))!);
                    if (currentToken is not null)
                        await freshTokens.DeleteAsync(currentToken);
                }
                var newEmail = $"{Guid.NewGuid():N}@example.test";
                const string newPassword = "Episteme-Created-Password-42!";
                using var createdUser = await managedApiClient.PostAsJsonAsync("/api/v1/admin/users",
                    new { email = newEmail, password = newPassword });
                Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);
                var createdUserBody = await createdUser.Content.ReadFromJsonAsync<JsonElement>();
                createdUserId = createdUserBody.GetProperty("id").GetGuid();
                Assert.NotNull(createdUser.Headers.Location);
                Assert.Equal(HttpStatusCode.OK, (await managedApiClient.GetAsync(createdUser.Headers.Location)).StatusCode);
                var storedUser = await users.FindByIdAsync(createdUserId.Value.ToString());
                Assert.NotNull(storedUser);
                Assert.NotEqual(newPassword, storedUser.PasswordHash);
                Assert.True(await users.CheckPasswordAsync(storedUser, newPassword));
                Assert.Equal(HttpStatusCode.Conflict, (await managedApiClient.PostAsJsonAsync("/api/v1/admin/users",
                    new { email = newEmail, password = newPassword })).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await managedApiClient.PostAsJsonAsync("/api/v1/admin/users",
                    new { email = $"{Guid.NewGuid():N}@example.test", password = "weak" })).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await apiClient.PostAsJsonAsync("/api/v1/admin/users",
                    new { email = "plain@example.test", password = newPassword })).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/password", new { password = "weak" })).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await apiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/password", new { password = newPassword })).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/password", new { password = newPassword })).StatusCode);
                await using (var verifyScope = authFactory.Services.CreateAsyncScope())
                {
                    var verifyUsers = verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                    var resetUser = await verifyUsers.FindByIdAsync(managedUser.Id.ToString());
                    Assert.NotNull(resetUser);
                    Assert.True(await verifyUsers.CheckPasswordAsync(resetUser, newPassword));
                }
                Assert.Equal(HttpStatusCode.Conflict, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{otherUser.Id}/status", new { status = "Disabled" })).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{user.Id}/status", new { status = "Disabled" })).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{user.Id}/administrator", new { isAdministrator = false })).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{user.Id}/administrator", new { })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/status", new { status = "Disabled" })).StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/administrator", new { isAdministrator = true })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/status", new { status = "Active" })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/administrator", new { isAdministrator = true })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{managedUser.Id}/administrator", new { isAdministrator = false })).StatusCode);
                Assert.Equal(6, await db.AccountAuditEvents.CountAsync(a => a.TargetUserId == managedUser.Id));
                Assert.True((await users.RemoveFromRoleAsync(user, "Administrator")).Succeeded);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync(otherPath)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync("/api/v1/admin/users")).StatusCode);
            }
            finally
            {
                var membership = await db.WorkspaceMembers.FindAsync(otherWorkspace.Id, otherUser.Id);
                if (membership is not null)
                    db.WorkspaceMembers.Remove(membership);
                db.Workspaces.Remove(otherWorkspace);
                await db.SaveChangesAsync();
                await db.AccountAuditEvents.Where(a => a.TargetUserId == managedUser.Id).ExecuteDeleteAsync();
                Assert.True((await users.DeleteAsync(otherUser)).Succeeded);
                await using var cleanupScope = authFactory.Services.CreateAsyncScope();
                var cleanupUsers = cleanupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                if (createdUserId is { } id)
                {
                    await db.AccountAuditEvents.Where(a => a.TargetUserId == id).ExecuteDeleteAsync();
                    var createdAccount = await cleanupUsers.FindByIdAsync(id.ToString());
                    Assert.NotNull(createdAccount);
                    Assert.True((await cleanupUsers.DeleteAsync(createdAccount)).Succeeded);
                }
                var currentManagedUser = await cleanupUsers.FindByIdAsync(managedUser.Id.ToString());
                Assert.NotNull(currentManagedUser);
                Assert.True((await cleanupUsers.DeleteAsync(currentManagedUser)).Succeeded);
                if (createdRole)
                    Assert.True((await roleManager.DeleteAsync(adminRole)).Succeeded);
            }
            using var tokenListing = await managedApiClient.GetAsync("/api/v1/me/tokens?limit=1");
            Assert.Equal(HttpStatusCode.OK, tokenListing.StatusCode);
            var tokenPage = await tokenListing.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(tokenPage.GetProperty("hasMore").GetBoolean(), tokenPage.ToString());
            Assert.Single(tokenPage.GetProperty("items").EnumerateArray());
            Assert.DoesNotContain(accessToken, tokenPage.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, tokenPage.ToString(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, (await managedApiClient.GetAsync("/api/v1/me/tokens?limit=101")).StatusCode);
            var tokenManager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var foreignToken = await tokenManager.CreateAsync(new OpenIddictTokenDescriptor
            {
                Subject = Guid.NewGuid().ToString(),
                Type = OpenIddictConstants.TokenTypeIdentifiers.AccessToken,
                Status = OpenIddictConstants.Statuses.Valid
            });
            try
            {
                var foreignId = await tokenManager.GetIdAsync(foreignToken);
                Assert.Equal(HttpStatusCode.NotFound, (await managedApiClient.DeleteAsync($"/api/v1/me/tokens/{foreignId}")).StatusCode);
                Assert.True(await tokenManager.HasStatusAsync(foreignToken, OpenIddictConstants.Statuses.Valid));
            }
            finally
            {
                await tokenManager.DeleteAsync(foreignToken);
            }
            using var allTokens = await managedApiClient.GetAsync("/api/v1/me/tokens");
            using var allTokenBody = JsonDocument.Parse(await allTokens.Content.ReadAsStringAsync());
            var accessId = allTokenBody.RootElement.GetProperty("items").EnumerateArray()
                .Single(t => t.GetProperty("type").GetString() == OpenIddictConstants.TokenTypeIdentifiers.AccessToken)
                .GetProperty("id").GetString();
            var refreshRequest = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken
            };
            using var refreshed = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(refreshRequest));
            var refreshBody = await refreshed.Content.ReadAsStringAsync();
            Assert.True(refreshed.IsSuccessStatusCode, refreshBody);
            using var rotated = JsonDocument.Parse(refreshBody);
            var rotatedToken = rotated.RootElement.GetProperty("refresh_token").GetString()!;
            Assert.NotEqual(refreshToken, rotatedToken);
            user.Status = UserAccountStatus.Disabled;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, (await apiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await apiClient.GetAsync("/api/v1/me/tokens")).StatusCode);
            refreshRequest["refresh_token"] = rotatedToken;
            using (var disabled = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(refreshRequest)))
                Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
            user.Status = UserAccountStatus.Active;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, (await apiClient.PostAsJsonAsync("/api/v1/workspaces",
                new { name = "Denied", slug = "denied", type = "Code" })).StatusCode);
            var applicationManager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var otherClientId = $"auth-other-{Guid.NewGuid():N}";
            var otherClient = await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = otherClientId,
                ClientType = OpenIddictConstants.ClientTypes.Public,
                Permissions = { OpenIddictConstants.Permissions.Endpoints.Revocation }
            });
            try
            {
                using var foreignRevoke = await browser.PostAsync("/connect/revoke", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["client_id"] = otherClientId, ["token"] = rotatedToken }));
                Assert.Equal(HttpStatusCode.OK, foreignRevoke.StatusCode);
                Assert.Equal(0, await db.OAuthRevocationAuditEvents.CountAsync(a => a.ClientId == otherClientId));
            }
            finally
            {
                await applicationManager.DeleteAsync(otherClient);
            }
            var beforeAuditFailure = await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>()
                .AsNoTracking().Where(t => t.Subject == user.Id.ToString() &&
                    t.Type == OpenIddictConstants.TokenTypeIdentifiers.RefreshToken &&
                    t.Status == OpenIddictConstants.Statuses.Valid)
                .Select(t => t.Id).SingleAsync();
            auditFailure.Enabled = true;
            try
            {
                using var failedRevoke = await browser.PostAsync("/connect/revoke", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["client_id"] = clientId, ["token"] = rotatedToken }));
                Assert.Equal(HttpStatusCode.InternalServerError, failedRevoke.StatusCode);
                Assert.True(auditFailure.Invoked);
            }
            finally
            {
                auditFailure.Enabled = false;
            }
            var afterAuditFailure = await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>()
                .AsNoTracking().SingleAsync(t => t.Id == beforeAuditFailure);
            Assert.Equal(OpenIddictConstants.Statuses.Valid, afterAuditFailure.Status);
            Assert.Equal(0, await db.OAuthRevocationAuditEvents.CountAsync(a => a.ClientId == clientId));
            async Task<HttpStatusCode> RevokeRotatedAsync()
            {
                using var response = await browser.PostAsync("/connect/revoke", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["client_id"] = clientId, ["token"] = rotatedToken }));
                return response.StatusCode;
            }
            var concurrentRevocations = await Task.WhenAll(RevokeRotatedAsync(), RevokeRotatedAsync());
            Assert.All(concurrentRevocations, status => Assert.Equal(HttpStatusCode.OK, status));
            Assert.Equal(1, await db.OAuthRevocationAuditEvents.CountAsync(a =>
                a.ClientId == clientId && a.Subject == user.Id.ToString()));
            using var repeatedRevoke = await browser.PostAsync("/connect/revoke", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["client_id"] = clientId, ["token"] = rotatedToken }));
            Assert.Equal(HttpStatusCode.OK, repeatedRevoke.StatusCode);
            using var unknownRevoke = await browser.PostAsync("/connect/revoke", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["client_id"] = clientId, ["token"] = "not-a-token" }));
            Assert.Equal(HttpStatusCode.OK, unknownRevoke.StatusCode);
            Assert.Equal(1, await db.OAuthRevocationAuditEvents.CountAsync(a =>
                a.ClientId == clientId && a.Subject == user.Id.ToString()));
            using var revokeAccess = await managedApiClient.DeleteAsync($"/api/v1/me/tokens/{accessId}");
            Assert.Equal(HttpStatusCode.NoContent, revokeAccess.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await apiClient.DeleteAsync($"/api/v1/me/tokens/{accessId}")).StatusCode);
            Assert.Equal(1, await db.AccountAuditEvents.CountAsync(a =>
                a.ActorUserId == user.Id && a.EventType == "token.revoked" && a.TargetTokenId == accessId));
            refreshRequest["refresh_token"] = rotatedToken;
            using var revokedRefresh = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(refreshRequest));
            Assert.Equal(HttpStatusCode.BadRequest, revokedRefresh.StatusCode);
            refreshRequest["refresh_token"] = refreshToken;
            using var replayRefresh = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(refreshRequest));
            Assert.Equal(HttpStatusCode.BadRequest, replayRefresh.StatusCode);
            using var replayCode = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(tokenRequest));
            Assert.Equal(HttpStatusCode.BadRequest, replayCode.StatusCode);

            request["resource"] = "https://mcp.example/mcp";
            var mcpConsent = await browser.GetStringAsync(QueryHelpers.AddQueryString("/connect/authorize", request));
            var mcpForm = request.ToDictionary(kv => kv.Key, kv => kv.Value!);
            mcpForm["decision"] = "accept";
            mcpForm["__RequestVerificationToken"] = AntiForgeryToken(mcpConsent);
            using var mcpApproved = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(mcpForm));
            Assert.Equal(HttpStatusCode.Redirect, mcpApproved.StatusCode);
            tokenRequest["code"] = QueryHelpers.ParseQuery(mcpApproved.Headers.Location!.Query)["code"].ToString();
            using var mcpExchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(tokenRequest));
            Assert.Equal(HttpStatusCode.OK, mcpExchange.StatusCode);
            using var mcpTokenBody = JsonDocument.Parse(await mcpExchange.Content.ReadAsStringAsync());
            var mcpToken = mcpTokenBody.RootElement.GetProperty("access_token").GetString()!;
            using var mcpPayload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(mcpToken.Split('.')[1]));
            Assert.True(mcpPayload.RootElement.TryGetProperty("oi_au_id", out var mcpAuthorizationClaim));
            var mcpAuthorizationId = mcpAuthorizationClaim.GetString();
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcpToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await apiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            managedApiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcpToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            await using var mcpFactory = new McpFactory(connection, authFactory);
            using var mcpClient = mcpFactory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://mcp.example") });
            mcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcpToken);
            mcpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            mcpClient.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-03-26");
            using var mcpResult = await mcpClient.PostAsJsonAsync("/mcp",
                new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } });
            Assert.Equal(HttpStatusCode.OK, mcpResult.StatusCode);
            await using var managedMcp = new ManagedMcpFactory(connection, authFactory);
            using var managedMcpClient = managedMcp.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://mcp.example") });
            managedMcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcpToken);
            managedMcpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            managedMcpClient.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-03-26");
            var mcpRequest = new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } };
            Assert.Equal(HttpStatusCode.OK, (await managedMcpClient.PostAsJsonAsync("/mcp", mcpRequest)).StatusCode);
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            Assert.Equal(HttpStatusCode.BadRequest, (await apiClient.GetAsync("/api/v1/me/authorizations?limit=101")).StatusCode);
            using var authorizationList = await apiClient.GetAsync("/api/v1/me/authorizations");
            Assert.Equal(HttpStatusCode.OK, authorizationList.StatusCode);
            var authorizationPage = await authorizationList.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(authorizationPage.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetString() == mcpAuthorizationId);
            var authorizationManager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var foreignAuthorization = await authorizationManager.CreateAsync(new OpenIddictAuthorizationDescriptor
            {
                Subject = Guid.NewGuid().ToString(),
                Type = OpenIddictConstants.AuthorizationTypes.Permanent,
                Status = OpenIddictConstants.Statuses.Valid
            });
            try
            {
                var foreignId = await authorizationManager.GetIdAsync(foreignAuthorization);
                Assert.Equal(HttpStatusCode.NotFound, (await apiClient.DeleteAsync($"/api/v1/me/authorizations/{foreignId}")).StatusCode);
                Assert.True(await authorizationManager.HasStatusAsync(foreignAuthorization, OpenIddictConstants.Statuses.Valid));
            }
            finally
            {
                await authorizationManager.DeleteAsync(foreignAuthorization);
            }
            Assert.Equal(HttpStatusCode.NoContent, (await apiClient.DeleteAsync($"/api/v1/me/authorizations/{mcpAuthorizationId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await apiClient.DeleteAsync($"/api/v1/me/authorizations/{mcpAuthorizationId}")).StatusCode);
            Assert.Equal(1, await db.AccountAuditEvents.CountAsync(a =>
                a.ActorUserId == user.Id && a.EventType == "authorization.revoked" && a.TargetAuthorizationId == mcpAuthorizationId));
            Assert.Equal(HttpStatusCode.Unauthorized, (await managedMcpClient.PostAsJsonAsync("/mcp", mcpRequest)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await mcpClient.PostAsJsonAsync("/mcp", mcpRequest)).StatusCode);
            using var revokedAuthorizationRefresh = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = clientId,
                    ["refresh_token"] = mcpTokenBody.RootElement.GetProperty("refresh_token").GetString()!
                }));
            Assert.Equal(HttpStatusCode.BadRequest, revokedAuthorizationRefresh.StatusCode);
            using var revokeMcp = await browser.PostAsync("/connect/revoke", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["client_id"] = clientId, ["token"] = mcpToken }));
            Assert.Equal(HttpStatusCode.OK, revokeMcp.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await managedMcpClient.PostAsJsonAsync("/mcp", mcpRequest)).StatusCode);

            request["resource"] = "https://api.example/";
            var apiConsent = await browser.GetStringAsync(QueryHelpers.AddQueryString("/connect/authorize", request));
            var apiForm = request.ToDictionary(kv => kv.Key, kv => kv.Value!);
            apiForm["decision"] = "accept";
            apiForm["__RequestVerificationToken"] = AntiForgeryToken(apiConsent);
            using var apiApproved = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(apiForm));
            tokenRequest["code"] = QueryHelpers.ParseQuery(apiApproved.Headers.Location!.Query)["code"].ToString();
            using var apiExchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(tokenRequest));
            Assert.Equal(HttpStatusCode.OK, apiExchange.StatusCode);
            using var newApiTokenBody = JsonDocument.Parse(await apiExchange.Content.ReadAsStringAsync());
            var newApiToken = newApiTokenBody.RootElement.GetProperty("access_token").GetString()!;
            using var apiPayload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(newApiToken.Split('.')[1]));
            var apiAuthorizationId = apiPayload.RootElement.GetProperty("oi_au_id").GetString();
            managedApiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newApiToken);
            Assert.Equal(HttpStatusCode.OK, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            var freshRole = await roleManager.FindByNameAsync("Administrator");
            var ownsRole = freshRole is null;
            if (freshRole is null)
            {
                freshRole = new IdentityRole<Guid>("Administrator");
                Assert.True((await roleManager.CreateAsync(freshRole)).Succeeded);
            }
            var secondAdmin = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@example.test" };
            Assert.True((await users.CreateAsync(secondAdmin)).Succeeded);
            try
            {
                Assert.True((await users.AddToRoleAsync(secondAdmin, "Administrator")).Succeeded);
                await using var adminScope = managedApi.Services.CreateAsyncScope();
                var adminRepository = adminScope.ServiceProvider.GetRequiredService<IUserAdministrationRepository>();
                Assert.Null((await adminRepository.SetStatusAsync(secondAdmin.Id, user.Id, UserAccountStatus.Disabled, default)).Failure);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
                Assert.Null((await adminRepository.SetStatusAsync(secondAdmin.Id, user.Id, UserAccountStatus.Active, default)).Failure);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
                using var staleRefresh = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["refresh_token"] = newApiTokenBody.RootElement.GetProperty("refresh_token").GetString()!
                    }));
                Assert.Equal(HttpStatusCode.BadRequest, staleRefresh.StatusCode);
            }
            finally
            {
                await db.AccountAuditEvents.Where(a => a.ActorUserId == secondAdmin.Id).ExecuteDeleteAsync();
                Assert.True((await users.DeleteAsync(secondAdmin)).Succeeded);
                if (ownsRole)
                    Assert.True((await roleManager.DeleteAsync(freshRole)).Succeeded);
            }

            using (var staleCookie = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", request)))
                Assert.Equal(HttpStatusCode.Redirect, staleCookie.StatusCode);
            var renewedLoginPage = await browser.GetStringAsync("/Identity/Account/Login");
            using var renewedLogin = await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = password,
                    ["__RequestVerificationToken"] = AntiForgeryToken(renewedLoginPage)
                }));
            Assert.Equal(HttpStatusCode.Redirect, renewedLogin.StatusCode);
            var renewedConsent = await browser.GetStringAsync(QueryHelpers.AddQueryString("/connect/authorize", request));
            apiForm["__RequestVerificationToken"] = AntiForgeryToken(renewedConsent);
            using var renewedApproval = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(apiForm));
            tokenRequest["code"] = QueryHelpers.ParseQuery(renewedApproval.Headers.Location!.Query)["code"].ToString();
            using var renewedExchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(tokenRequest));
            Assert.Equal(HttpStatusCode.OK, renewedExchange.StatusCode);
            using var renewedBody = JsonDocument.Parse(await renewedExchange.Content.ReadAsStringAsync());
            var renewedToken = renewedBody.RootElement.GetProperty("access_token").GetString()!;
            using var renewedPayload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(renewedToken.Split('.')[1]));
            apiAuthorizationId = renewedPayload.RootElement.GetProperty("oi_au_id").GetString();
            managedApiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewedToken);
            Assert.Equal(HttpStatusCode.OK, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            await using (var passwordScope = authFactory.Services.CreateAsyncScope())
            {
                var passwordUsers = passwordScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var currentUser = await passwordUsers.FindByIdAsync(user.Id.ToString());
                Assert.NotNull(currentUser);
                var passwordChange = await passwordUsers.ChangePasswordAsync(currentUser, password, "Episteme-Changed-Password-42!");
                Assert.True(passwordChange.Succeeded, string.Join(", ", passwordChange.Errors.Select(e => e.Code)));
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
            using var oldPasswordRefresh = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = clientId,
                    ["refresh_token"] = renewedBody.RootElement.GetProperty("refresh_token").GetString()!
                }));
            Assert.Equal(HttpStatusCode.BadRequest, oldPasswordRefresh.StatusCode);
            using (var oldPasswordCookie = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", request)))
                Assert.Equal(HttpStatusCode.Redirect, oldPasswordCookie.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await apiClient.DeleteAsync($"/api/v1/me/authorizations/{apiAuthorizationId}")).StatusCode);
            const string changedPassword = "Episteme-Changed-Password-42!";
            const string resetPassword = "Episteme-Recovered-Password-42!";
            var changedLoginPage = await browser.GetStringAsync("/Identity/Account/Login");
            using var changedLogin = await browser.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = changedPassword,
                    ["__RequestVerificationToken"] = AntiForgeryToken(changedLoginPage)
                }));
            Assert.Equal(HttpStatusCode.Redirect, changedLogin.StatusCode);
            var changedConsent = await browser.GetStringAsync(QueryHelpers.AddQueryString("/connect/authorize", request));
            apiForm["__RequestVerificationToken"] = AntiForgeryToken(changedConsent);
            using var changedApproval = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(apiForm));
            tokenRequest["code"] = QueryHelpers.ParseQuery(changedApproval.Headers.Location!.Query)["code"].ToString();
            using var changedExchange = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(tokenRequest));
            Assert.Equal(HttpStatusCode.OK, changedExchange.StatusCode);
            using var changedBody = JsonDocument.Parse(await changedExchange.Content.ReadAsStringAsync());
            managedApiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                changedBody.RootElement.GetProperty("access_token").GetString());
            await using var selfAdminScope = authFactory.Services.CreateAsyncScope();
            var selfAdminRoles = selfAdminScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var selfAdminUsers = selfAdminScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var selfAdminRole = await selfAdminRoles.FindByNameAsync("Administrator");
            var ownsSelfAdminRole = selfAdminRole is null;
            if (selfAdminRole is null)
            {
                selfAdminRole = new IdentityRole<Guid>("Administrator");
                Assert.True((await selfAdminRoles.CreateAsync(selfAdminRole)).Succeeded);
            }
            try
            {
                var selfAdmin = await selfAdminUsers.FindByIdAsync(user.Id.ToString());
                Assert.NotNull(selfAdmin);
                Assert.True((await selfAdminUsers.AddToRoleAsync(selfAdmin, "Administrator")).Succeeded);
                Assert.Equal(HttpStatusCode.NoContent, (await managedApiClient.PutAsJsonAsync(
                    $"/api/v1/admin/users/{user.Id}/password", new { password = resetPassword })).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await managedApiClient.GetAsync("/api/v1/workspaces")).StatusCode);
                using var resetRefresh = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = clientId,
                        ["refresh_token"] = changedBody.RootElement.GetProperty("refresh_token").GetString()!
                    }));
                Assert.Equal(HttpStatusCode.BadRequest, resetRefresh.StatusCode);
                await using var resetScope = authFactory.Services.CreateAsyncScope();
                var resetUsers = resetScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var recovered = await resetUsers.FindByIdAsync(user.Id.ToString());
                Assert.NotNull(recovered);
                Assert.True(await resetUsers.CheckPasswordAsync(recovered, resetPassword));
                Assert.False(await resetUsers.CheckPasswordAsync(recovered, changedPassword));
            }
            finally
            {
                await using var roleCleanupScope = authFactory.Services.CreateAsyncScope();
                var cleanupUserManager = roleCleanupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var cleanupRoleManager = roleCleanupScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
                var currentUser = await cleanupUserManager.FindByIdAsync(user.Id.ToString());
                Assert.NotNull(currentUser);
                Assert.True((await cleanupUserManager.RemoveFromRoleAsync(currentUser, "Administrator")).Succeeded);
                if (ownsSelfAdminRole)
                {
                    var currentRole = await cleanupRoleManager.FindByIdAsync(selfAdminRole.Id.ToString());
                    Assert.NotNull(currentRole);
                    Assert.True((await cleanupRoleManager.DeleteAsync(currentRole)).Succeeded);
                }
            }
        }
        finally
        {
            var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await apps.FindByClientIdAsync(clientId);
            if (application is not null)
                await apps.DeleteAsync(application);
            var cleanupDb = scope.ServiceProvider.GetRequiredService<EpistemeDbContext>();
            await cleanupDb.OAuthRevocationAuditEvents.Where(a => a.ClientId == clientId).ExecuteDeleteAsync();
            await cleanupDb.AccountAuditEvents.Where(a => a.ActorUserId == user.Id).ExecuteDeleteAsync();
            await using var finalCleanup = authFactory.Services.CreateAsyncScope();
            var finalUsers = finalCleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var currentUser = await finalUsers.FindByIdAsync(user.Id.ToString());
            if (currentUser is not null)
                Assert.True((await finalUsers.DeleteAsync(currentUser)).Succeeded);
        }
    }

    private static string AntiForgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Anti-forgery token was not found.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task AssertLoginAssetsAsync(HttpClient client, string html)
    {
        var assetPaths = Regex.Matches(html, @"/css/auth-login\.css")
            .Select(match => match.Value)
            .Distinct()
            .ToArray();
        Assert.Single(assetPaths);
        foreach (var path in assetPaths)
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {response.StatusCode}.");
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    private sealed class StaticAssetsAuthFactory : WebApplicationFactory<AuthProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Authentication:Issuer", "https://auth.example/");
            builder.UseSetting("Authentication:ApiAudience", "https://api.example/");
            builder.UseSetting("Authentication:McpAudience", "https://mcp.example/mcp");
        }
    }

    private sealed class AuthFactory(string connection, string clientId, AuditFailureInterceptor? auditFailure = null) : WebApplicationFactory<AuthProgram>
    {
        public string ClientId => clientId;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Issuer", "https://auth.example/");
            builder.UseSetting("Authentication:ApiAudience", "https://api.example/");
            builder.UseSetting("Authentication:McpAudience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:Client:Id", clientId);
            builder.UseSetting("Authentication:Client:RedirectUri", "https://client.example/callback");
            if (auditFailure is not null)
                builder.ConfigureTestServices(services =>
                    services.AddDbContext<EpistemeDbContext>(options => options.AddInterceptors(auditFailure)));
        }
    }

    private sealed class AuditFailureInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Invoked { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<OAuthRevocationAuditEvent>()
                .Any(entry => entry.State == EntityState.Added))
            {
                Invoked = true;
                throw new InvalidOperationException("Simulated OAuth audit failure.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class NonDevelopmentAuthFactory(string connection, string certificatePath, string keyRingPath) : WebApplicationFactory<AuthProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseStaticWebAssets();
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Issuer", "https://auth.example/");
            builder.UseSetting("Authentication:ApiAudience", "https://api.example/");
            builder.UseSetting("Authentication:McpAudience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:SigningCertificate:Path", certificatePath);
            builder.UseSetting("Authentication:SigningCertificate:Password", "test-only");
            builder.UseSetting("Authentication:EncryptionCertificate:Path", certificatePath);
            builder.UseSetting("Authentication:EncryptionCertificate:Password", "test-only");
            builder.UseSetting("Authentication:DataProtection:KeyRingPath", keyRingPath);
        }
    }

    private sealed class BootstrapAuthFactory(string connection, Guid userId) : WebApplicationFactory<AuthProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Issuer", "https://auth.example/");
            builder.UseSetting("Authentication:ApiAudience", "https://api.example/");
            builder.UseSetting("Authentication:McpAudience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:BootstrapAdministrator:UserId", userId.ToString());
        }
    }

    private sealed class BootstrapCredentialsFactory(string connection, string email, string password) : WebApplicationFactory<AuthProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Issuer", "https://auth.example/");
            builder.UseSetting("Authentication:ApiAudience", "https://api.example/");
            builder.UseSetting("Authentication:McpAudience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:BootstrapAdministrator:Email", email);
            builder.UseSetting("Authentication:BootstrapAdministrator:Password", password);
        }
    }

    private sealed class ApiFactory(string connection, AuthFactory auth) : WebApplicationFactory<Program>
    {
        public Exception? Failure { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Authority", "https://auth.example/");
            builder.UseSetting("Authentication:Audience", "https://api.example/");
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Backchannel = auth.CreateClient(new WebApplicationFactoryClientOptions
                    { BaseAddress = new Uri("https://auth.example") });
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        "https://auth.example/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(options.Backchannel));
                    options.Events = new JwtBearerEvents
                    {
                        OnAuthenticationFailed = context =>
                        {
                            Failure = context.Exception;
                            return Task.CompletedTask;
                        }
                    };
                }));
        }
    }

    private static void ConfigureManagedValidation(IServiceCollection services, AuthFactory auth)
    {
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.AdditionalHandlers.Add(new DiscoveryForwarder(new HttpMessageInvoker(auth.Server.CreateHandler())))));
    }

    private sealed class DiscoveryForwarder(HttpMessageInvoker client) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            client.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                client.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ManagedApiFactory(string connection, AuthFactory auth) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Authority", "https://auth.example/");
            builder.UseSetting("Authentication:Audience", "https://api.example/");
            builder.UseSetting("Authentication:RequireTokenEntryValidation", "true");
            builder.ConfigureTestServices(services => ConfigureManagedValidation(services, auth));
        }
    }

    private sealed class ManagedMcpFactory(string connection, AuthFactory auth) : WebApplicationFactory<McpProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Authority", "https://auth.example/");
            builder.UseSetting("Authentication:Audience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:RequireTokenEntryValidation", "true");
            builder.ConfigureTestServices(services => ConfigureManagedValidation(services, auth));
        }
    }

    private sealed class McpFactory(string connection, AuthFactory auth) : WebApplicationFactory<McpProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("Authentication:Authority", "https://auth.example/");
            builder.UseSetting("Authentication:Audience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:RequireTokenEntryValidation", "false");
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Backchannel = auth.CreateClient(new WebApplicationFactoryClientOptions
                    { BaseAddress = new Uri("https://auth.example") });
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        "https://auth.example/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(options.Backchannel));
                }));
        }
    }
}
