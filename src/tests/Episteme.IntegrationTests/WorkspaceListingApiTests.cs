using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CliWrap;
using CliWrap.Buffered;
using Episteme.Application.Workspaces;
using Episteme.Domain.Workspaces;
using Episteme.Infrastructure.Identity;
using Episteme.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Episteme.IntegrationTests;

public sealed class WorkspaceListingApiTests
{
    [PostgresFact]
    public async Task Workspace_manifest_roundtrip_enforces_authorization_and_rejects_unsupported_fields()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var root = Path.Combine(Path.GetTempPath(), $"episteme-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"manifest-{Guid.NewGuid():N}" };
        var outsider = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"manifest-{Guid.NewGuid():N}" };
        db.Users.AddRange(owner, outsider);
        await db.SaveChangesAsync();
        var ids = new List<Guid>();
        try
        {
            await using var factory = new WorkspaceApiFactory(connection, root);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage workspaces.read workspaces.write");
            var create = await client.PostAsJsonAsync("/api/v1/workspaces",
                new { name = "Research", slug = $"manifest-{Guid.NewGuid():N}", type = "Research", description = "notes" });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var sourceId = (await create.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
            ids.Add(sourceId);
            var url = $"/api/v1/workspaces/{sourceId}/manifest";
            var exported = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
            var manifest = await exported.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(new[] { "description", "formatVersion", "name", "type" },
                manifest.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
            Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
            var import = await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = $"manifest-{Guid.NewGuid():N}", manifest });
            Assert.Equal(HttpStatusCode.Created, import.StatusCode);
            var targetId = (await import.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
            ids.Add(targetId);
            Assert.EndsWith($"/api/v1/workspaces/{targetId}", import.Headers.Location!.ToString());
            var importedManifest = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/api/v1/workspaces/{targetId}/manifest");
            Assert.Equal(manifest.GetRawText(), importedManifest.GetRawText());
            var configUrl = $"/api/v1/workspaces/{sourceId}/configuration";
            var initial = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(configUrl);
            var expectedVersion = initial.GetProperty("concurrencyStamp").GetGuid();
            var configuration = new { schemaVersion = 1, defaultExtension = ".md", citationStyle = "APA" };
            using var update = await client.PutAsJsonAsync(configUrl, new { expectedVersion, configuration });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(configUrl, new { expectedVersion, configuration })).StatusCode);
            var changed = await update.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var currentVersion = changed.GetProperty("concurrencyStamp").GetGuid();
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(configUrl,
                new { expectedVersion = currentVersion, configuration = new { schemaVersion = 2 } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(configUrl,
                new { expectedVersion = currentVersion, configuration = new { schemaVersion = 1, password = "secret" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(configUrl,
                new { expectedVersion = currentVersion, configuration = new { schemaVersion = 1, genre = new string('a', 81) } })).StatusCode);
            var v2 = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{url}?formatVersion=2");
            Assert.Equal(2, v2.GetProperty("formatVersion").GetInt32());
            Assert.Equal("APA", v2.GetProperty("configuration").GetProperty("citationStyle").GetString());
            var importV2 = await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = $"manifest-{Guid.NewGuid():N}", manifest = v2 });
            Assert.Equal(HttpStatusCode.Created, importV2.StatusCode);
            var targetV2 = (await importV2.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
            ids.Add(targetV2);
            var copied = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/api/v1/workspaces/{targetV2}/manifest?formatVersion=2");
            Assert.Equal(v2.GetRawText(), copied.GetRawText());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}?formatVersion=4")).StatusCode);
            var skeletonManifest = new
            {
                formatVersion = 3,
                name = "Skeleton",
                type = "Research",
                configuration,
                directories = new[] { "notes/drafts", "papers", "笔记" }
            };
            using var skeletonImport = await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = $"manifest-{Guid.NewGuid():N}", manifest = skeletonManifest });
            Assert.Equal(HttpStatusCode.Created, skeletonImport.StatusCode);
            var skeletonId = (await skeletonImport.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
            ids.Add(skeletonId);
            var rootListing = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/api/v1/workspaces/{skeletonId}/directories");
            var head = rootListing.GetProperty("commitId").GetString();
            using var documentWrite = await client.PutAsJsonAsync(
                $"/api/v1/workspaces/{skeletonId}/documents?path=notes/drafts/private.md",
                new { expectedHead = head, content = "private document contents" });
            Assert.Equal(HttpStatusCode.OK, documentWrite.StatusCode);
            var skeletonExport = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/api/v1/workspaces/{skeletonId}/manifest?formatVersion=3");
            Assert.Equal(new[] { "notes", "notes/drafts", "papers", "笔记" },
                skeletonExport.GetProperty("directories").EnumerateArray().Select(p => p.GetString()).ToArray());
            Assert.DoesNotContain(".gitkeep", skeletonExport.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("private", skeletonExport.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = "invalid", manifest = new { formatVersion = 3, name = "x", type = "Code", configuration, directories = new[] { "../escape" } } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = "invalid", manifest = new { formatVersion = 3, name = "x", type = "Code", configuration, directories = new[] { "a/.gitkeep/child" } } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = "invalid", manifest = new { formatVersion = 3, name = "x", type = "Code", configuration, directories = Enumerable.Range(0, 257).Select(i => $"d{i}").ToArray() } })).StatusCode);
            Assert.Equal(1, await db.WorkspaceAuditEvents.CountAsync(a =>
                a.WorkspaceId == sourceId && a.EventType == "workspace.configuration.updated"));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = "invalid", manifest = new { formatVersion = 2, name = "x", type = "Code" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = "invalid", manifest = new { formatVersion = 1, name = "x", type = "Code", token = "ignored" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = "invalid", manifest = new { formatVersion = 1, name = "x", type = "1" } })).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Scope");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.read");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/workspaces/import",
                new { slug = "denied", manifest })).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", outsider.Id.ToString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{url}?formatVersion=3")).StatusCode);
        }
        finally
        {
            await db.WorkspaceAuditEvents.Where(e => ids.Contains(e.WorkspaceId)).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => ids.Contains(m.WorkspaceId)).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => ids.Contains(w.Id)).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == outsider.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    [PostgresFact]
    public async Task Member_management_checks_authorization_and_preserves_an_owner()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"member-{Guid.NewGuid():N}" };
        var other = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"member-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "Initial", $"member-{Guid.NewGuid():N}", WorkspaceType.Code);
        db.Users.AddRange(owner, other);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(workspace.Id, owner.Id));
        await db.SaveChangesAsync();
        try
        {
            await using var factory = new WorkspaceApiFactory(connection);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage");
            var basePath = $"/api/v1/workspaces/{workspace.Id}/members";
            client.DefaultRequestHeaders.Remove("X-Test-Scope");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(basePath)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage");
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{basePath}/{owner.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{basePath}/{other.Id}", new { role = "Editor" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{basePath}/{other.Id}", new { role = "1" })).StatusCode);
            var page = await client.GetFromJsonAsync<MemberListResponse>($"{basePath}?limit=1");
            Assert.NotNull(page);
            Assert.Single(page.Items);
            Assert.True(page.HasMore);
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", other.Id.ToString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{basePath}/{owner.Id}")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{basePath}/{other.Id}", new { role = "Owner" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{basePath}/{owner.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(basePath)).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", other.Id.ToString());
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{basePath}/{other.Id}")).StatusCode);
            Assert.Equal(3, await db.WorkspaceAuditEvents.CountAsync(a => a.WorkspaceId == workspace.Id));
            Assert.True(await db.WorkspaceAuditEvents.AnyAsync(a => a.WorkspaceId == workspace.Id &&
                a.EventType == "workspace.member.removed" && a.TargetUserId == owner.Id && a.TargetRole == WorkspaceRole.Owner));
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{basePath}/{owner.Id}", new { role = "Owner" })).StatusCode);
            using var ownerClient = factory.CreateClient();
            ownerClient.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            ownerClient.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage");
            var concurrent = await Task.WhenAll(
                ownerClient.DeleteAsync($"{basePath}/{owner.Id}"),
                client.DeleteAsync($"{basePath}/{other.Id}"));
            Assert.Single(concurrent, response => response.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(concurrent, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(1, await db.WorkspaceMembers.AsNoTracking().CountAsync(m => m.WorkspaceId == workspace.Id &&
                m.Status == WorkspaceMemberStatus.Active && m.Role == WorkspaceRole.Owner));
        }
        finally
        {
            await db.WorkspaceAuditEvents.Where(a => a.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id || u.Id == other.Id).ExecuteDeleteAsync();
        }
    }

    private sealed record MemberListResponse(IReadOnlyList<MemberResponse> Items, bool HasMore);
    private sealed record MemberResponse(Guid UserId, string Role);

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/workspaces");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_scope_returns_a_bounded_page()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.read");

        var response = await client.GetAsync("/api/v1/workspaces?offset=0&limit=1");
        var page = await response.Content.ReadFromJsonAsync<ListResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(page);
        Assert.Single(page.Items);
        Assert.True(page.HasMore);
        Assert.Equal("research", page.Items[0].Slug);
        Assert.Equal("Research", page.Items[0].Type);
    }

    [Fact]
    public async Task Invalid_page_and_missing_scope_are_rejected()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", Guid.NewGuid().ToString());

        var forbidden = await client.GetAsync("/api/v1/workspaces");
        client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.read");
        var invalid = await client.GetAsync("/api/v1/workspaces?limit=101");

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Development_openapi_describes_workspace_listing()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"/api/v1/workspaces\"", json);
        Assert.Contains("\"WorkspaceListResponse\"", json);
        Assert.Contains("\"/api/v1/workspaces/{workspaceId}/directories\"", json);
        Assert.Contains("\"/api/v1/workspaces/{workspaceId}/documents\"", json);
        Assert.Contains("\"/api/v1/workspaces/{workspaceId}/documents/history\"", json);
    }

    [PostgresFact]
    public async Task Metadata_update_and_archive_use_database_authorization_and_expected_version()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"http-{Guid.NewGuid():N}" };
        var workspace = Workspace.Create(owner.Id, "Initial", $"http-{Guid.NewGuid():N}", WorkspaceType.Code);
        db.Users.Add(owner);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(WorkspaceMember.CreateOwner(workspace.Id, owner.Id));
        await db.SaveChangesAsync();

        try
        {
            await using var factory = new WorkspaceApiFactory(connection);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage");
            var oldVersion = workspace.ConcurrencyStamp;
            var url = $"/api/v1/workspaces/{workspace.Id}";
            var updated = await client.PutAsJsonAsync(url, new
            {
                name = "Updated",
                type = "Research",
                description = "notes",
                expectedVersion = oldVersion
            });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var response = await updated.Content.ReadFromJsonAsync<UpdatedResponse>();
            Assert.NotNull(response);
            Assert.NotEqual(oldVersion, response.ConcurrencyStamp);

            var stale = await client.PostAsJsonAsync($"{url}/archive", new { expectedVersion = oldVersion });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var archived = await client.PostAsJsonAsync($"{url}/archive", new { expectedVersion = response.ConcurrencyStamp });
            Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        }
        finally
        {
            await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
        }
    }

    [PostgresFact]
    public async Task Create_workspace_initializes_git_and_returns_location()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var root = Path.Combine(Path.GetTempPath(), $"episteme-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"http-{Guid.NewGuid():N}" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        Guid? workspaceId = null;
        try
        {
            await using var factory = new WorkspaceApiFactory(connection, root);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage workspaces.read");
            var slug = $"http-{Guid.NewGuid():N}";
            var request = new { name = "Research", slug, type = "Research", description = "notes" };
            var response = await client.PostAsJsonAsync("/api/v1/workspaces", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
            Assert.NotNull(created);
            workspaceId = created.Id;
            Assert.EndsWith($"/api/v1/workspaces/{workspaceId}", response.Headers.Location!.ToString());
            var fetched = await client.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
            var duplicate = await client.PostAsJsonAsync("/api/v1/workspaces", request);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            var stored = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
            Assert.True(Directory.Exists(Path.Combine(root, stored.StorageKey, ".git")));
        }
        finally
        {
            if (workspaceId is not null)
            {
                await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteDeleteAsync();
            }
            await db.Users.Where(u => u.Id == owner.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    [PostgresFact]
    public async Task Content_reads_committed_git_tree_with_authorization_and_path_guards()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var root = Path.Combine(Path.GetTempPath(), $"episteme-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<EpistemeDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"content-{Guid.NewGuid():N}" };
        var reader = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"content-{Guid.NewGuid():N}" };
        var outsider = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"content-{Guid.NewGuid():N}" };
        db.Users.AddRange(owner, reader, outsider);
        await db.SaveChangesAsync();
        Guid? workspaceId = null;
        try
        {
            await using var factory = new WorkspaceApiFactory(connection, root);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage workspaces.read workspaces.write");
            var createdResponse = await client.PostAsJsonAsync("/api/v1/workspaces",
                new { name = "Content", slug = $"content-{Guid.NewGuid():N}", type = "Research" });
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
            workspaceId = (await createdResponse.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
            var stored = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId);
            var repo = Path.Combine(root, stored.StorageKey);
            Directory.CreateDirectory(Path.Combine(repo, "notes"));
            await File.WriteAllTextAsync(Path.Combine(repo, "notes", "a.txt"), "committed 文档");
            await File.WriteAllBytesAsync(Path.Combine(repo, "large.bin"), new byte[1024 * 1024 + 1]);
            await File.WriteAllBytesAsync(Path.Combine(repo, "binary"), [0xff]);
            await File.WriteAllTextAsync(Path.Combine(repo, "boundary.txt"), new string('x', 1024 * 1024));
            File.CreateSymbolicLink(Path.Combine(repo, "escape"), "/etc/passwd");
            await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(["add", "--all"]).ExecuteBufferedAsync();
            await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(
                ["-c", "user.name=Test", "-c", "user.email=test@localhost", "-c", "core.hooksPath=/dev/null", "commit", "-m", "Test content"]).ExecuteBufferedAsync();
            await File.WriteAllTextAsync(Path.Combine(repo, "notes", "a.txt"), "committed 文档");
            await File.AppendAllTextAsync(Path.Combine(repo, "notes", "a.txt"), " updated");
            await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(["add", "--", "notes/a.txt"]).ExecuteBufferedAsync();
            await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(
                ["-c", "user.name=Test", "-c", "user.email=test@localhost", "-c", "core.hooksPath=/dev/null", "commit", "-m", "Update content"]).ExecuteBufferedAsync();
            await File.WriteAllTextAsync(Path.Combine(repo, "notes", "a.txt"), "uncommitted");
            db.WorkspaceMembers.Add(WorkspaceMember.Add(workspaceId.Value, reader.Id, WorkspaceRole.Reader, owner.Id));
            await db.SaveChangesAsync();
            var url = $"/api/v1/workspaces/{workspaceId}";
            var rootResult = await client.GetFromJsonAsync<DirectoryResponse>($"{url}/directories");
            Assert.NotNull(rootResult);
            Assert.Contains(rootResult.Entries, x => x.Name == "notes" && x.Kind == "directory");
            Assert.Contains(rootResult.Entries, x => x.Name == "escape" && x.Kind == "symlink");
            var nested = await client.GetFromJsonAsync<DirectoryResponse>($"{url}/directories?path=notes");
            Assert.Contains(nested!.Entries, x => x.Name == "a.txt" && x.Kind == "document");
            var document = await client.GetFromJsonAsync<DocumentResponse>($"{url}/documents?path=notes%2Fa.txt");
            Assert.Equal("committed 文档 updated", document!.Content);
            Assert.Equal(rootResult.CommitId, document.CommitId);
            var history = await client.GetFromJsonAsync<HistoryResponse>($"{url}/documents/history?path=notes%2Fa.txt&limit=1");
            Assert.NotNull(history);
            Assert.Equal(rootResult.CommitId, history.HeadCommitId);
            Assert.Equal(rootResult.CommitId, Assert.Single(history.Commits));
            Assert.True(history.HasMore);
            var previous = await client.GetFromJsonAsync<HistoryResponse>($"{url}/documents/history?path=notes%2Fa.txt&limit=1&offset=1");
            Assert.NotEqual(rootResult.CommitId, Assert.Single(previous!.Commits));
            Assert.False(previous.HasMore);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}/documents/history?path=notes%2Fa.txt&limit=101")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}/documents?path=..%2Fsecret")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{url}/documents?path=.git%2Fconfig")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/documents?path=notes%2Fmissing")).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{url}/documents?path=escape")).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"{url}/documents?path=escape",
                new { expectedHead = rootResult.CommitId, content = "denied" })).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"{url}/documents?path=escape%2Fpasswd",
                new { expectedHead = rootResult.CommitId, content = "denied" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/documents?path=escape%2Fpasswd")).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{url}/documents?path=binary")).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"{url}/documents?path=notes")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/documents?path=boundary.txt")).StatusCode);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.GetAsync($"{url}/documents?path=large.bin")).StatusCode);
            var blob = (await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(["hash-object", "-w", "--stdin"])
                .WithStandardInputPipe(PipeSource.FromString("x")).ExecuteBufferedAsync()).StandardOutput.Trim();
            var treeInput = string.Concat(Enumerable.Range(0, 16000)
                .Select(i => $"100644 blob {blob}\tentry-{i:D6}-012345678901234567890123456789\n"));
            var tree = (await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(["mktree"])
                .WithStandardInputPipe(PipeSource.FromString(treeInput)).ExecuteBufferedAsync()).StandardOutput.Trim();
            var commit = (await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(
                ["-c", "user.name=Test", "-c", "user.email=test@localhost", "commit-tree", tree, "-m", "Large tree"])
                .ExecuteBufferedAsync()).StandardOutput.Trim();
            await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(["update-ref", "refs/heads/main", commit])
                .ExecuteBufferedAsync();
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.GetAsync($"{url}/directories")).StatusCode);
            await Cli.Wrap("git").WithWorkingDirectory(repo).WithArguments(["update-ref", "refs/heads/main", rootResult.CommitId])
                .ExecuteBufferedAsync();
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", reader.Id.ToString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/documents?path=notes%2Fa.txt")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/documents/history?path=notes%2Fa.txt")).StatusCode);
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId && m.UserId == reader.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, WorkspaceRole.Editor));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/documents/history?path=notes%2Fa.txt")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", outsider.Id.ToString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{url}/directories")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Scope");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{url}/directories")).StatusCode);
        }
        finally
        {
            if (workspaceId is not null)
            {
                await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).ExecuteDeleteAsync();
                await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteDeleteAsync();
            }
            await db.Users.Where(u => u.Id == owner.Id || u.Id == reader.Id || u.Id == outsider.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed record DirectoryResponse(string CommitId, List<ContentEntry> Entries);
    private sealed record ContentEntry(string Name, string Kind);
    private sealed record DocumentResponse(string CommitId, string Content);
    private sealed record HistoryResponse(string HeadCommitId, IReadOnlyList<string> Commits, bool HasMore);
    [PostgresFact]
    public async Task Document_writes_are_authorized_versioned_and_restorable()
    {
        var connection = Environment.GetEnvironmentVariable("EPISTEME_TEST_POSTGRES")!;
        var root = Path.Combine(Path.GetTempPath(), $"episteme-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<EpistemeDbContext>().UseNpgsql(connection).UseOpenIddict().Options;
        await using var db = new EpistemeDbContext(options);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"write-{Guid.NewGuid():N}" };
        var reader = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"write-{Guid.NewGuid():N}" };
        db.Users.AddRange(owner, reader);
        await db.SaveChangesAsync();
        Guid? id = null;
        try
        {
            await using var factory = new WorkspaceApiFactory(connection, root);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.manage workspaces.read workspaces.write");
            var create = await client.PostAsJsonAsync("/api/v1/workspaces",
                new { name = "Write", slug = $"write-{Guid.NewGuid():N}", type = "Code" });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            id = (await create.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
            db.WorkspaceMembers.Add(WorkspaceMember.Add(id.Value, reader.Id, WorkspaceRole.Reader, owner.Id));
            await db.SaveChangesAsync();
            var url = $"/api/v1/workspaces/{id}";
            var initial = (await client.GetFromJsonAsync<DirectoryResponse>($"{url}/directories"))!.CommitId;
            var put = await client.PutAsJsonAsync($"{url}/documents?path=note.md", new { expectedHead = initial, content = "first" });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var first = (await put.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            Assert.NotEqual(initial, first);
            var stored = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == id);
            var repo = Path.Combine(root, stored.StorageKey);
            var author = await Cli.Wrap("git").WithWorkingDirectory(repo)
                .WithArguments(["show", "-s", "--format=%an <%ae>", first]).ExecuteBufferedAsync();
            Assert.Contains(owner.Id.ToString("N"), author.StandardOutput);
            Assert.Equal("first", (await client.GetFromJsonAsync<DocumentResponse>($"{url}/documents?path=note.md"))!.Content);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{url}/documents?path=note.md",
                new { expectedHead = initial, content = "stale" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/documents?path=..%2Fsecret",
                new { expectedHead = first, content = "invalid" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{url}/documents?path=absent%2Fnote.md",
                new { expectedHead = first, content = "missing parent" })).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", reader.Id.ToString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{url}/documents?path=note.md",
                new { expectedHead = first, content = "denied" })).StatusCode);
            var readerVersion = await client.GetAsync($"{url}/documents/version?path=note.md&commitId={first}");
            Assert.Equal(HttpStatusCode.OK, readerVersion.StatusCode);
            Assert.Equal("first", (await readerVersion.Content.ReadFromJsonAsync<DocumentResponse>())!.Content);
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == id && m.UserId == reader.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, WorkspaceRole.Editor));
            client.DefaultRequestHeaders.Remove("X-Test-Scope");
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.read");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{url}/documents?path=note.md",
                new { expectedHead = first, content = "denied" })).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Scope");
            client.DefaultRequestHeaders.Add("X-Test-Scope", "workspaces.read workspaces.write");
            var editorPut = await client.PutAsJsonAsync($"{url}/documents?path=note.md",
                new { expectedHead = first, content = "editor" });
            Assert.Equal(HttpStatusCode.OK, editorPut.StatusCode);
            var editorHead = (await editorPut.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", owner.Id.ToString());
            var competing = await Task.WhenAll(
                client.PutAsJsonAsync($"{url}/documents?path=note.md", new { expectedHead = editorHead, content = "second" }),
                client.PutAsJsonAsync($"{url}/documents?path=note.md", new { expectedHead = editorHead, content = "other" }));
            Assert.Single(competing, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(competing, r => r.StatusCode == HttpStatusCode.Conflict);
            var second = (await competing.Single(r => r.StatusCode == HttpStatusCode.OK).Content
                .ReadFromJsonAsync<CommitResponse>())!.CommitId;
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{url}/documents?path=note.md")
            { Content = JsonContent.Create(new { expectedHead = second }) };
            var removed = await client.SendAsync(delete);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            var deleted = (await removed.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/documents?path=note.md")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{url}/documents/restore?path=note.md",
                new { expectedHead = deleted, sourceCommit = new string('a', 40) })).StatusCode);
            var restoredResponse = await client.PostAsJsonAsync($"{url}/documents/restore?path=note.md",
                new { expectedHead = deleted, sourceCommit = first });
            Assert.Equal(HttpStatusCode.OK, restoredResponse.StatusCode);
            var restored = (await restoredResponse.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            Assert.NotEqual(first, restored);
            Assert.Equal("first", (await client.GetFromJsonAsync<DocumentResponse>($"{url}/documents?path=note.md"))!.Content);
            var oldVersion = await client.GetFromJsonAsync<DocumentResponse>($"{url}/documents/version?path=note.md&commitId={editorHead}");
            Assert.Equal(editorHead, oldVersion!.CommitId);
            Assert.Equal("editor", oldVersion.Content);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/documents/version?path=note.md&commitId={new string('a', 40)}")).StatusCode);
            var history = await client.GetFromJsonAsync<HistoryResponse>($"{url}/documents/history?path=note.md");
            Assert.Equal(5, history!.Commits.Count);
            Assert.Equal(restored, history.Commits[0]);
            var mkdir = await client.PutAsJsonAsync($"{url}/directories?path=folder", new { expectedHead = restored });
            Assert.Equal(HttpStatusCode.OK, mkdir.StatusCode);
            var dirHead = (await mkdir.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PutAsJsonAsync($"{url}/directories?path=folder", new { expectedHead = dirHead })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{url}/documents/move?path=note.md",
                new { expectedHead = dirHead, destinationPath = "folder" })).StatusCode);
            var moved = await client.PostAsJsonAsync($"{url}/documents/move?path=note.md",
                new { expectedHead = dirHead, destinationPath = "folder/note.md" });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            var movedHead = (await moved.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            Assert.Equal("first", (await client.GetFromJsonAsync<DocumentResponse>($"{url}/documents?path=folder%2Fnote.md"))!.Content);
            using var nonrecursive = new HttpRequestMessage(HttpMethod.Delete, $"{url}/directories?path=folder")
            { Content = JsonContent.Create(new { expectedHead = movedHead, recursive = false }) };
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(nonrecursive)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{url}/directories/move?path=folder",
                new { expectedHead = movedHead, destinationPath = "folder/child" })).StatusCode);
            var movedDir = await client.PostAsJsonAsync($"{url}/directories/move?path=folder",
                new { expectedHead = movedHead, destinationPath = "other" });
            Assert.Equal(HttpStatusCode.OK, movedDir.StatusCode);
            var renamedHead = (await movedDir.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            Assert.Equal("first", (await client.GetFromJsonAsync<DocumentResponse>($"{url}/documents?path=other%2Fnote.md"))!.Content);
            using var recursive = new HttpRequestMessage(HttpMethod.Delete, $"{url}/directories?path=other")
            { Content = JsonContent.Create(new { expectedHead = renamedHead, recursive = true }) };
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(recursive)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{url}/directories?path=other")).StatusCode);
            var afterDelete = (await client.GetFromJsonAsync<DirectoryResponse>($"{url}/directories"))!.CommitId;
            var emptyDir = await client.PutAsJsonAsync($"{url}/directories?path=empty", new { expectedHead = afterDelete });
            var emptyHead = (await emptyDir.Content.ReadFromJsonAsync<CommitResponse>())!.CommitId;
            using var emptyDelete = new HttpRequestMessage(HttpMethod.Delete, $"{url}/directories?path=empty")
            { Content = JsonContent.Create(new { expectedHead = emptyHead, recursive = false }) };
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(emptyDelete)).StatusCode);
        }
        finally
        {
            if (id is not null)
            {
                await db.WorkspaceAuditEvents.Where(e => e.WorkspaceId == id).ExecuteDeleteAsync();
                await db.WorkspaceMembers.Where(m => m.WorkspaceId == id).ExecuteDeleteAsync();
                await db.Workspaces.Where(w => w.Id == id).ExecuteDeleteAsync();
            }
            await db.Users.Where(u => u.Id == owner.Id || u.Id == reader.Id).ExecuteDeleteAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed record CommitResponse(string CommitId);
    private sealed record ListResponse(List<WorkspaceResponse> Items, int Offset, int Limit, bool HasMore);
    private sealed record WorkspaceResponse(Guid Id, string Slug, string Type);
    private sealed record UpdatedResponse(Guid ConcurrencyStamp);

    private sealed class WorkspaceApiFactory(string? connectionString = null, string? storageRoot = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Episteme", connectionString ?? "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            if (storageRoot is not null)
                builder.UseSetting("WorkspaceStorageRoot", storageRoot);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWorkspaceListingRepository>();
                services.AddSingleton<IWorkspaceListingRepository, FakeRepository>();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            });
        }
    }

    private sealed class FakeRepository : IWorkspaceListingRepository
    {
        public Task<IReadOnlyList<WorkspaceSummary>?> ListForActiveUserAsync(
            Guid userId, bool isSystemAdministrator, int offset, int count, CancellationToken cancellationToken)
        {
            IReadOnlyList<WorkspaceSummary> items = new[]
            {
                new WorkspaceSummary(Guid.NewGuid(), "Research", "research", WorkspaceType.Research, null, "main"),
                new WorkspaceSummary(Guid.NewGuid(), "Skill", "skill", WorkspaceType.Skill, null, "main")
            }.Skip(offset).Take(count).ToArray();
            return Task.FromResult<IReadOnlyList<WorkspaceSummary>?>(items);
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

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
