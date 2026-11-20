using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Git;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Episteme.McpServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Episteme.IntegrationTests;

public sealed class McpProtocolTests
{
    [PostgresFact]
    public async Task Content_tool_enforces_live_policy_for_project_id()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var root = Path.Combine(Path.GetTempPath(), $"episteme-mcp-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WorkspaceStorageRoot"] = root }).Build();
        var options = new DbContextOptionsBuilder<EpistemeDbContext>().UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-policy-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "MCP", $"mcp-policy-{Guid.NewGuid():N}", WorkspaceType.Code);
        var project = Project.Create(workspace.Id, owner.Id, "Project", "project", null);
        db.Users.Add(owner);
        db.Workspaces.Add(workspace);
        db.Projects.Add(project);
        db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(workspace.Id, owner.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var key = RSA.Create(2048);
        try
        {
            await new GitWorkspaceProvisioner(configuration).InitializeAsync(project.StorageKey, project.DefaultBranch, default);
            await using var factory = new McpFactory(connection, root, new RsaSecurityKey(key));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new Uri("https://mcp.example") });
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-03-26");
            var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                "https://issuer.example", "https://mcp.example/mcp",
                [new Claim("sub", owner.Id.ToString()), new Claim("scope", "workspaces.read")],
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            async Task<JsonElement> ListAsync()
            {
                using var response = await client.PostAsJsonAsync("/mcp", new
                {
                    jsonrpc = "2.0", id = 1, method = "tools/call",
                    @params = new { name = "list_directory", arguments = new { workspaceId = project.Id, path = "" } }
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    body = string.Join("\n", body.Split('\n')
                        .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                        .Select(line => line[5..].TrimStart()));
                return JsonDocument.Parse(body).RootElement.GetProperty("result").Clone();
            }

            Assert.True((await ListAsync()).GetProperty("isError").GetBoolean());
            var settings = new EfMcpSettingsRepository(db);
            var actor = new WorkspaceActor(owner.Id, new HashSet<string> { "tokens.manage" }, false);
            Assert.Null((await settings.SetPersonalAsync(actor, new(true, false), default)).Failure);
            Assert.Null((await settings.SetWorkspaceEnabledAsync(actor, workspace.Id, true, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(actor, workspace.Id, McpPermission.ReadDocument, default)).Failure);
            Assert.False((await ListAsync()).TryGetProperty("isError", out var error) && error.GetBoolean());
            Assert.Null((await settings.SetWorkspaceEnabledAsync(actor, workspace.Id, false, default)).Failure);
            Assert.True((await ListAsync()).GetProperty("isError").GetBoolean());
        }
        finally
        {
            await db.AccountAuditEvents.Where(a => a.TargetUserId == owner.Id).ExecuteDeleteAsync();
            await db.WorkspaceAuditEvents.Where(a => a.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceUserSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpWorkspaceSettings.Where(s => s.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.McpUserSettings.Where(s => s.UserId == owner.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    [PostgresFact]
    public async Task Streamable_http_authenticates_each_request_and_applies_workspace_permissions()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var root = Path.Combine(Path.GetTempPath(), $"episteme-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<EpistemeDbContext>().UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-{Guid.NewGuid():N}" };
        var outsider = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mcp-{Guid.NewGuid():N}" };
        db.Users.AddRange(owner, outsider);
        await db.SaveChangesAsync();
        Guid? workspaceId = null;
        Guid? importedWorkspaceId = null;
        using var key = RSA.Create(2048);
        try
        {
            await using var factory = new McpFactory(connection, root, new RsaSecurityKey(key));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://mcp.example")
            });
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-03-26");
            async Task<HttpResponseMessage> SendAsync(string method, object parameters) =>
                await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters });
            string Token(Guid subject, string scopes, string audience = "https://mcp.example/mcp") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                "https://issuer.example", audience, [new Claim("sub", subject.ToString()), new Claim("scope", scopes)],
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
            void UseToken(Guid subject, string scopes) =>
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject, scopes));
            async Task<JsonElement> ReadMessageAsync(HttpResponseMessage response)
            {
                var body = await response.Content.ReadAsStringAsync();
                if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    body = string.Join("\n", body.Split('\n')
                        .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                        .Select(line => line[5..].TrimStart()));
                return JsonDocument.Parse(body).RootElement.Clone();
            }
            async Task<JsonElement> CallAsync(string name, object arguments)
            {
                using var response = await SendAsync("tools/call", new { name, arguments });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var json = await ReadMessageAsync(response);
                return json.GetProperty("result");
            }

            using (var anonymous = await SendAsync("tools/list", new { }))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                Assert.Contains("https://mcp.example/.well-known/oauth-protected-resource/mcp",
                    anonymous.Headers.WwwAuthenticate.ToString());
            }
            var metadata = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
            Assert.Equal("https://mcp.example/mcp", metadata.GetProperty("resource").GetString());
            Assert.Equal("https://issuer.example", metadata.GetProperty("authorization_servers")[0].GetString());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                Token(owner.Id, "workspaces.read", "https://other.example/mcp"));
            using (var wrongAudience = await SendAsync("tools/list", new { }))
                Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
            UseToken(owner.Id, "workspaces.manage workspaces.read workspaces.write");
            using (var initialized = await SendAsync("initialize", new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "episteme-test", version = "1" }
            }))
                Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            using (var listed = await SendAsync("tools/list", new { }))
            {
                Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
                var list = await ReadMessageAsync(listed);
                var tools = list.GetProperty("result").GetProperty("tools");
                Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "put_document");
                Assert.DoesNotContain("principal", tools.GetRawText(), StringComparison.OrdinalIgnoreCase);
            }

            var deniedCreation = await CallAsync("create_workspace",
                new { name = "MCP", slug = $"mcp-denied-{Guid.NewGuid():N}", type = "Code" });
            Assert.True(deniedCreation.GetProperty("isError").GetBoolean());
            Assert.True((await CallAsync("get_workspace_settings", new { })).GetProperty("isError").GetBoolean());
            Assert.True((await CallAsync("set_workspace_settings", new { directory = "notes" })).GetProperty("isError").GetBoolean());
            var settings = new EfMcpSettingsRepository(db);
            var ownerActor = new WorkspaceActor(owner.Id, new HashSet<string> { "tokens.manage" }, false);
            var outsiderActor = new WorkspaceActor(outsider.Id, new HashSet<string> { "tokens.manage" }, false);
            Assert.Null((await settings.SetPersonalAsync(ownerActor, new(true, true), default)).Failure);
            var created = await CallAsync("create_workspace",
                new { name = "MCP", slug = $"mcp-{Guid.NewGuid():N}", type = "Code" });
            Assert.False(created.TryGetProperty("isError", out var createError) && createError.GetBoolean());
            var createdData = JsonDocument.Parse(created.GetProperty("content")[0].GetProperty("text").GetString()!);
            workspaceId = createdData.RootElement.GetProperty("id").GetGuid();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["WorkspaceStorageRoot"] = root }).Build();
            var project = Project.Create(workspaceId.Value, owner.Id, "Main", "main", null);
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            await new GitWorkspaceProvisioner(configuration).InitializeAsync(project.StorageKey, project.DefaultBranch, default);
            Assert.Null((await settings.SetWorkspaceEnabledAsync(ownerActor, workspaceId.Value, true, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, workspaceId.Value,
                McpPermission.ReadWorkspace | McpPermission.UpdateWorkspace |
                McpPermission.ReadDocument | McpPermission.CreateDocument | McpPermission.UpdateDocument, default)).Failure);
            Assert.True((await CallAsync("list_projects", new { workspaceId })).GetProperty("isError").GetBoolean());
            Assert.True((await CallAsync("create_project", new { workspaceId, name = "Other", slug = "other" })).GetProperty("isError").GetBoolean());
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, workspaceId.Value,
                McpPermission.ReadWorkspace | McpPermission.UpdateWorkspace | McpPermission.ReadProject |
                McpPermission.CreateProject | McpPermission.UpdateProject | McpPermission.DeleteProject |
                McpPermission.ReadDocument | McpPermission.CreateDocument | McpPermission.UpdateDocument, default)).Failure);
            var projectList = await CallAsync("list_projects", new { workspaceId });
            Assert.False(projectList.TryGetProperty("isError", out var listError) && listError.GetBoolean());
            var projectCreated = await CallAsync("create_project", new { workspaceId, name = "Other", slug = "other" });
            Assert.False(projectCreated.TryGetProperty("isError", out var projectError) && projectError.GetBoolean());
            using var projectJson = JsonDocument.Parse(projectCreated.GetProperty("content")[0].GetProperty("text").GetString()!);
            var newProjectId = projectJson.RootElement.GetProperty("id").GetGuid();
            var projectVersion = projectJson.RootElement.GetProperty("concurrencyStamp").GetGuid();
            var projectUpdated = await CallAsync("update_project", new { projectId = newProjectId, expectedVersion = projectVersion, name = "Renamed", slug = "other" });
            Assert.False(projectUpdated.TryGetProperty("isError", out var updateError) && updateError.GetBoolean());
            using var updatedProjectJson = JsonDocument.Parse(projectUpdated.GetProperty("content")[0].GetProperty("text").GetString()!);
            var newProjectVersion = updatedProjectJson.RootElement.GetProperty("concurrencyStamp").GetGuid();
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, workspaceId.Value,
                McpPermission.ReadWorkspace | McpPermission.UpdateWorkspace |
                McpPermission.ReadDocument | McpPermission.CreateDocument | McpPermission.UpdateDocument, default)).Failure);
            Assert.True((await CallAsync("delete_project", new { projectId = newProjectId, expectedVersion = newProjectVersion })).GetProperty("isError").GetBoolean());
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, workspaceId.Value,
                McpPermission.ReadWorkspace | McpPermission.UpdateWorkspace | McpPermission.DeleteProject |
                McpPermission.ReadDocument | McpPermission.CreateDocument | McpPermission.UpdateDocument, default)).Failure);
            var projectDeleted = await CallAsync("delete_project", new { projectId = newProjectId, expectedVersion = newProjectVersion });
            Assert.False(projectDeleted.TryGetProperty("isError", out var deleteError) && deleteError.GetBoolean());
            var version = createdData.RootElement.GetProperty("concurrencyStamp").GetGuid();
            var setConfig = await CallAsync("set_workspace_configuration",
                new { workspaceId, expectedVersion = version, configuration = new { schemaVersion = 1, language = "csharp" } });
            Assert.False(setConfig.TryGetProperty("isError", out var configError) && configError.GetBoolean());
            var configExport = await CallAsync("export_workspace_config", new { workspaceId, formatVersion = 2 });
            using var configJson = JsonDocument.Parse(configExport.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal(2, configJson.RootElement.GetProperty("formatVersion").GetInt32());
            Assert.Equal("csharp", configJson.RootElement.GetProperty("configuration").GetProperty("language").GetString());
            var imported = await CallAsync("import_workspace_config", new
            {
                slug = $"mcp-import-{Guid.NewGuid():N}",
                manifest = new
                {
                    formatVersion = 3,
                    name = "MCP copy",
                    type = "Code",
                    configuration = new { schemaVersion = 1, language = "csharp" },
                    directories = new[] { "notes/drafts" }
                }
            });
            Assert.False(imported.TryGetProperty("isError", out var importError) && importError.GetBoolean());
            using var importedData = JsonDocument.Parse(imported.GetProperty("content")[0].GetProperty("text").GetString()!);
            importedWorkspaceId = importedData.RootElement.GetProperty("id").GetGuid();
            var importedProject = Project.Create(importedWorkspaceId.Value, owner.Id, "Main", "main", null);
            db.Projects.Add(importedProject);
            await db.SaveChangesAsync();
            await new GitWorkspaceProvisioner(configuration).InitializeAsync(importedProject.StorageKey,
                importedProject.DefaultBranch, default, ["notes/drafts"]);
            var deniedSkeleton = await CallAsync("export_workspace_config", new { workspaceId = importedWorkspaceId, formatVersion = 3 });
            Assert.True(deniedSkeleton.GetProperty("isError").GetBoolean());
            Assert.Null((await settings.SetWorkspaceEnabledAsync(ownerActor, importedWorkspaceId.Value, true, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(ownerActor, importedWorkspaceId.Value,
                McpPermission.ReadWorkspace | McpPermission.ReadProject | McpPermission.ReadDocument, default)).Failure);
            var exportedSkeleton = await CallAsync("export_workspace_config", new { workspaceId = importedWorkspaceId, formatVersion = 3 });
            Assert.False(exportedSkeleton.TryGetProperty("isError", out var skeletonError) && skeletonError.GetBoolean());
            using var skeletonJson = JsonDocument.Parse(exportedSkeleton.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal(new[] { "notes", "notes/drafts" },
                skeletonJson.RootElement.GetProperty("directories").EnumerateArray().Select(p => p.GetString()).ToArray());
            var configConflict = await CallAsync("set_workspace_configuration",
                new { workspaceId, expectedVersion = version, configuration = new { schemaVersion = 1, language = "go" } });
            Assert.True(configConflict.GetProperty("isError").GetBoolean());
            var directory = await CallAsync("list_directory", new { workspaceId, path = "" });
            var directoryData = JsonDocument.Parse(directory.GetProperty("content")[0].GetProperty("text").GetString()!);
            var head = directoryData.RootElement.GetProperty("commitId").GetString();
            var put = await CallAsync("put_document", new { workspaceId, path = "note.md", expectedHead = head, content = "hello" });
            Assert.False(put.TryGetProperty("isError", out var putError) && putError.GetBoolean());
            var putData = JsonDocument.Parse(put.GetProperty("content")[0].GetProperty("text").GetString()!);
            var putHead = putData.RootElement.GetProperty("commitId").GetString();
            var read = await CallAsync("read_document", new { workspaceId, path = "note.md" });
            Assert.Contains("hello", read.GetProperty("content")[0].GetProperty("text").GetString());
            var history = await CallAsync("get_document_history", new { workspaceId, path = "note.md" });
            Assert.Contains(putHead!, history.GetProperty("content")[0].GetProperty("text").GetString());
            var stale = await CallAsync("put_document", new { workspaceId, path = "note.md", expectedHead = head, content = "stale" });
            Assert.True(stale.GetProperty("isError").GetBoolean());
            Assert.Contains("Conflict", stale.GetProperty("content")[0].GetProperty("text").GetString());
            var traversal = await CallAsync("read_document", new { workspaceId, path = "../secret" });
            Assert.True(traversal.GetProperty("isError").GetBoolean());
            var invalidManifest = await CallAsync("import_workspace_config", new
            {
                slug = "invalid",
                manifest = new { formatVersion = 1, name = "bad", type = "Code", secret = "ignored" }
            });
            Assert.True(invalidManifest.GetProperty("isError").GetBoolean());
            UseToken(owner.Id, "workspaces.read");
            var deniedWrite = await CallAsync("put_document", new { workspaceId, path = "note.md", expectedHead = head, content = "denied" });
            Assert.True(deniedWrite.GetProperty("isError").GetBoolean());
            UseToken(outsider.Id, "workspaces.read");
            var deniedRead = await CallAsync("read_document", new { workspaceId, path = "note.md" });
            Assert.True(deniedRead.GetProperty("isError").GetBoolean());
            var deniedExport = await CallAsync("export_workspace_config", new { workspaceId, formatVersion = 2 });
            Assert.True(deniedExport.GetProperty("isError").GetBoolean());
            UseToken(owner.Id, "workspaces.manage workspaces.read");
            var setMember = await CallAsync("set_workspace_member", new { workspaceId, memberId = outsider.Id, role = "Reader" });
            Assert.False(setMember.TryGetProperty("isError", out var memberError) && memberError.GetBoolean());
            Assert.Null((await settings.SetPersonalAsync(outsiderActor, new(true, false), default)).Failure);
            Assert.Null((await settings.SetMemberGrantAsync(ownerActor, workspaceId.Value, outsider.Id,
                McpPermission.ReadDocument, default)).Failure);
            Assert.Null((await settings.SetWorkspaceAsync(outsiderActor, workspaceId.Value, McpPermission.ReadDocument, default)).Failure);
            var setting = await CallAsync("set_workspace_settings", new { directory = "notes" });
            Assert.False(setting.TryGetProperty("isError", out var settingError) && settingError.GetBoolean());
            var getSetting = await CallAsync("get_workspace_settings", new { });
            Assert.Contains("notes", getSetting.GetProperty("content")[0].GetProperty("text").GetString());
            UseToken(outsider.Id, "workspaces.read workspaces.write");
            var readerRead = await CallAsync("read_document", new { workspaceId, path = "note.md" });
            Assert.False(readerRead.TryGetProperty("isError", out var readerError) && readerError.GetBoolean());
            var readerWrite = await CallAsync("put_document", new { workspaceId, path = "note.md", expectedHead = putHead, content = "denied" });
            Assert.True(readerWrite.GetProperty("isError").GetBoolean());
        }
        finally
        {
            if (importedWorkspaceId is not null)
            {
                await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == importedWorkspaceId).ExecuteDeleteAsync();
                await db.McpWorkspaceUserSettings.Where(s => s.WorkspaceId == importedWorkspaceId).ExecuteDeleteAsync();
                await db.McpWorkspaceSettings.Where(s => s.WorkspaceId == importedWorkspaceId).ExecuteDeleteAsync();
                await db.WorkspaceMembers.Where(m => m.WorkspaceId == importedWorkspaceId).ExecuteDeleteAsync();
                await db.Projects.Where(p => p.WorkspaceId == importedWorkspaceId).ExecuteDeleteAsync();
                await db.Workspaces.Where(w => w.Id == importedWorkspaceId).ExecuteDeleteAsync();
            }
            if (workspaceId is not null)
            {
                await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.McpWorkspaceUserSettings.Where(s => s.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.McpWorkspaceSettings.Where(s => s.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.Projects.Where(p => p.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteDeleteAsync();
            }
            await db.AccountAuditEvents.Where(a => a.TargetUserId == owner.Id || a.TargetUserId == outsider.Id).ExecuteDeleteAsync();
            await db.McpUserSettings.Where(s => s.UserId == owner.Id || s.UserId == outsider.Id).ExecuteDeleteAsync();
            await db.UserWorkspaceSettings.Where(s => s.UserId == owner.Id || s.UserId == outsider.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == outsider.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed class McpFactory(string connection, string root, SecurityKey key) : WebApplicationFactory<McpProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connection);
            builder.UseSetting("WorkspaceStorageRoot", root);
            builder.UseSetting("Authentication:Authority", "https://issuer.example");
            builder.UseSetting("Authentication:Audience", "https://mcp.example/mcp");
            builder.UseSetting("Authentication:RequireTokenEntryValidation", "false");
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
                        ValidAudience = "https://mcp.example/mcp",
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = key,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero
                    };
                }));
        }
    }
}
