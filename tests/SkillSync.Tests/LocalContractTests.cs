using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkillSync.Daemon.Api;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Desktop;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SkillSync.Tests;

public sealed class LocalContractTests : IAsyncLifetime
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"skillhub-http-{Guid.NewGuid():N}");
    private const string McpToken = "test-only-http-mcp-token-with-32-characters";
    private readonly LocalHttpAccess _access = new(McpToken);
    private ProviderDiscoveryService _discovery = null!;
    private readonly StartupFixture _startup = new();
    private SyncEngine _engine = null!;
    private McpJsonRpcHandler _rpc = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".gemini", "skills"));
        _discovery = new ProviderDiscoveryService(_home);
        _engine = new SyncEngine(_discovery);
        var importer = new SkillImporterService(_engine);
        _rpc = new McpJsonRpcHandler(_engine, importer, _discovery);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _home });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_discovery);
        builder.Services.AddSingleton(_engine);
        builder.Services.AddSingleton(_rpc);
        builder.Services.AddSingleton(new InstallManager(_home, Path.Combine(_home, "appdata")));
        builder.Services.AddSingleton<IDesktopStartup>(_startup);
        builder.Services.AddSingleton(importer);
        builder.Services.AddSingleton<DesktopPreferencesStore>();
        builder.Services.AddSingleton<SkillHubWindowController>();
        builder.Services.AddSingleton<MultiRootFileWatcher>();
        _app = builder.Build();
        _app.Use(_access.InvokeAsync);
        _app.MapSkillRestApi();
        _app.MapMcpEndpoints();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    private HttpRequestMessage Desktop(HttpMethod method, string url) =>
        new(method, url) { Headers = { { LocalHttpAccess.DesktopHeader, _access.DesktopToken } } };

    [Fact]
    public async Task TraversalRejectedByRestAndMcp_WithoutChangingDisk()
    {
        using var request = Desktop(HttpMethod.Post, "/api/v1/skills");
        request.Content = JsonContent.Create(new
        {
            name = "unsafe-rest",
            description = "fixture",
            instructions_markdown = "fixture",
            supporting_files = new Dictionary<string, string> { ["scripts/../../sentinel.txt"] = "must not be written" }
        });
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var reply = await _rpc.ProcessMessageAsync("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"create_skill","arguments":{
              "name":"unsafe-mcp","description":"Path traversal fixture","instructions_markdown":"fixture",
              "supporting_files":[{"relative_path":"scripts/../../sentinel.txt","content":"must not be written"}]
            }}}
            """);
        using var result = JsonDocument.Parse(reply!);
        Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Empty(_engine.ListSkills());
        Assert.False(File.Exists(Path.Combine(_discovery.CanonicalSkillsRoot, "sentinel.txt")));
        Assert.False(File.Exists(_discovery.LedgerFilePath));
    }

    [Fact]
    public async Task LocalHttp_RejectsMissingCredentialsForeignHostAndForeignOrigin()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/status")).StatusCode);
        using var wrongHost = Desktop(HttpMethod.Get, "/api/v1/status");
        wrongHost.Headers.Host = "unexpected.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(wrongHost)).StatusCode);
        using var wrongOrigin = Desktop(HttpMethod.Get, "/api/v1/status");
        wrongOrigin.Headers.Add("Origin", "https://unexpected.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(wrongOrigin)).StatusCode);
        using var trusted = Desktop(HttpMethod.Get, "/api/v1/status");
        trusted.Headers.Add("Origin", _client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        var result = await _client.SendAsync(trusted);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var status = await result.Content.ReadAsStringAsync();
        Assert.DoesNotContain(McpToken, status);
        Assert.DoesNotContain(_access.DesktopToken, status);
        Assert.DoesNotContain("public_mcp_url", status);
        using var tunnel = Desktop(HttpMethod.Post, "/api/v1/tunnel/toggle");
        Assert.Equal(HttpStatusCode.Gone, (await _client.SendAsync(tunnel)).StatusCode);
    }

    [Fact]
    public async Task McpHttp_UsesBearerAndRejectsAnUnknownLegacySession()
    {
        const string ping = "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}";
        using var unauthorized = await _client.PostAsync("/mcp", new StringContent(ping));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add("Authorization", $"Bearer {McpToken}");
        request.Headers.Add("Accept", "application/json, text/event-stream");
        request.Content = new StringContent(ping, System.Text.Encoding.UTF8, "application/json");
        using var reply = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("\"id\":9", await reply.Content.ReadAsStringAsync());

        using var badSession = new HttpRequestMessage(HttpMethod.Post, "/message?sessionId=missing");
        badSession.Headers.Add("Authorization", $"Bearer {McpToken}");
        badSession.Content = new StringContent(ping, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(badSession)).StatusCode);
    }

    [Fact]
    public async Task Activity_StreamsSingleLineSnakeCaseMatchingStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = Desktop(HttpMethod.Get, "/api/v1/events");
        using var stream = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("event: connected", await reader.ReadLineAsync(timeout.Token));
        Assert.StartsWith("data: ", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("", await reader.ReadLineAsync(timeout.Token));

        await _engine.CreateSkillAsync(new CanonicalSkillModel
        {
            Name = "event-probe", Description = "fixture", InstructionsMarkdown = "body"
        }, "test", timeout.Token);
        Assert.StartsWith("id: ", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("event: activity", await reader.ReadLineAsync(timeout.Token));
        var data = await reader.ReadLineAsync(timeout.Token);
        using var activity = JsonDocument.Parse(data!["data: ".Length..]);
        Assert.Equal("event-probe", activity.RootElement.GetProperty("skill_slug").GetString());
        Assert.True(activity.RootElement.TryGetProperty("timestamp_utc", out _));
        Assert.Equal("", await reader.ReadLineAsync(timeout.Token));

        using var status = await _client.SendAsync(Desktop(HttpMethod.Get, "/api/v1/status"), timeout.Token);
        using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token));
        Assert.True(body.RootElement.GetProperty("recent_activity")[0].TryGetProperty("timestamp_utc", out _));
        using var summaries = await _client.SendAsync(Desktop(HttpMethod.Get, "/api/v1/skills"), timeout.Token);
        using var list = JsonDocument.Parse(await summaries.Content.ReadAsStringAsync(timeout.Token));
        Assert.False(list.RootElement[0].TryGetProperty("instructions_markdown", out _));
        Assert.False(list.RootElement[0].TryGetProperty("supporting_files", out _));
    }

    [Fact]
    public async Task Rpc_RejectsNumericMethodsAndNegotiatesSupportedVersions()
    {
        using var invalid = JsonDocument.Parse((await _rpc.ProcessMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":13}"))!);
        Assert.Equal(-32600, invalid.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        using var init = JsonDocument.Parse((await _rpc.ProcessMessageAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2099-01-01\"}}"))!);
        Assert.Equal("2025-06-18", init.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task TagRegistry_IsSharedWithMcpAndPersistsUnassignedTags()
    {
        using var create = await SendDesktopAsync("/api/v1/tags", new { tag = "C# Review" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var tags = JsonDocument.Parse((await _rpc.ProcessMessageAsync("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"manage_skill_tags","arguments":{"action":"list"}}}
            """))!);
        var tag = tags.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("tags")[0];
        Assert.Equal("csharp-review", tag.GetProperty("tag").GetString());
        Assert.Equal(0, tag.GetProperty("skill_count").GetInt32());

        using var rename = await SendDesktopAsync("/api/v1/tags/rename", new { old_tag = "C# Review", new_tag = "Editorial QA" });
        using var changed = JsonDocument.Parse(await rename.Content.ReadAsStringAsync());
        var undo = changed.RootElement.GetProperty("undo_id").GetGuid();
        using var restored = await SendDesktopAsync($"/api/v1/tags/undo/{undo}", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        await _engine.DisposeAsync();
        await using var reloaded = new SyncEngine(_discovery);
        Assert.Contains(reloaded.ListTags(), value => value.Tag == "csharp-review" && value.IsCustom && value.SkillCount == 0);
        Assert.DoesNotContain(reloaded.ListTags(), value => value.Tag == "editorial-qa");
    }

    [Fact]
    public async Task ImportReview_RejectsMismatchedRevisionAndAppliesTheReviewedCopyOnce()
    {
        using var review = await SendDesktopAsync("/api/v1/imports/prepare",
            new { file_name = "SKILL.md", raw_markdown = "---\nname: pinned-copy\ndescription: A local fixture\nlicense: MIT\n---\nOriginal body." });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using var document = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
        var plan = document.RootElement;
        var revision = plan.GetProperty("revision").GetString()!;
        var url = $"/api/v1/imports/{plan.GetProperty("plan_id").GetGuid()}/apply";
        Assert.Equal("MIT", plan.GetProperty("license").GetString());
        Assert.Null(_engine.GetSkill("pinned-copy"));

        using var wrong = await SendDesktopAsync(url, new { revision = "different", providers = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Null(_engine.GetSkill("pinned-copy"));
        using var applied = await SendDesktopAsync(url, new { revision, providers = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var saved = _engine.GetSkill("pinned-copy")!;
        Assert.Contains("Original body.", saved.InstructionsMarkdown);
        Assert.Equal(revision, saved.Passthrough["import_revision"]);
        Assert.False(saved.Targets.Gemini.Enabled);

        using var repeated = await SendDesktopAsync(url, new { revision });
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
    }

    [Fact]
    public async Task StreamableMcp_RequiresInitializationAndValidatesToolSchema()
    {
        const string tools = """
            {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_sync_status","arguments":{}}}
            """;
        using var unsessioned = await SendMcpAsync(HttpMethod.Post, tools);
        Assert.Equal(HttpStatusCode.BadRequest, unsessioned.StatusCode);

        using var hello = await SendMcpAsync(HttpMethod.Post, """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}
            """);
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        Assert.Contains("2025-06-18", await hello.Content.ReadAsStringAsync());
        var session = Assert.Single(hello.Headers.GetValues("Mcp-Session-Id"));
        using var early = await SendMcpAsync(HttpMethod.Post, tools, session);
        Assert.Contains("-32002", await early.Content.ReadAsStringAsync());
        using var initialized = await SendMcpAsync(HttpMethod.Post, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", session);
        Assert.Equal(HttpStatusCode.Accepted, initialized.StatusCode);

        using var invalid = await SendMcpAsync(HttpMethod.Post, """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"toggle_skill_provider","arguments":{"name":"probe","provider":"2","enabled":true}}}
            """, session);
        Assert.Contains("-32602", await invalid.Content.ReadAsStringAsync());
        using var valid = await SendMcpAsync(HttpMethod.Post, tools, session);
        Assert.Contains("structuredContent", await valid.Content.ReadAsStringAsync());
        using var end = await SendMcpAsync(HttpMethod.Delete, null, session);
        Assert.Equal(HttpStatusCode.NoContent, end.StatusCode);
        using var expired = await SendMcpAsync(HttpMethod.Post, tools, session);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task GithubReview_UsesCommitAndExactPathForEveryFile()
    {
        const string commit = "1212121212121212121212121212121212121212";
        using var gitHub = new GithubFixture(commit);
        using var client = new HttpClient(gitHub);
        var importer = new SkillImporterService(_engine, client);
        var plan = await importer.PrepareImportAsync(new SkillImportSelection(
            Repo: "fixture/repo", Path: "skills/two/SKILL.md", GitRef: "release/next"));
        Assert.Equal(commit, plan.Revision);
        Assert.Equal("skills/two/SKILL.md", plan.Path);
        Assert.Equal("MIT", plan.License);
        Assert.Equal("Pinned reference.", plan.SupportingFiles["references/notes.md"]);
        Assert.Contains("Pinned instructions.", plan.InstructionsMarkdown);
        // The fixture has no codeload archive, so the indexer falls back to one Trees API call.
        Assert.Single(gitHub.Requests, uri => uri.Host == "codeload.github.com");
        Assert.Single(gitHub.Requests, uri => uri.AbsolutePath.Contains("/git/trees/"));
        Assert.Single(gitHub.Requests, uri => uri.AbsolutePath.Contains("/commits/"));
        var requests = gitHub.Requests.Count;
        var saved = await importer.ApplyImportAsync(plan.PlanId, new(plan.Revision, null, ["gemini"]));
        Assert.Equal(requests, gitHub.Requests.Count);
        Assert.Equal("Pinned reference.", saved.SupportingFiles["references/notes.md"]);
        Assert.Equal(commit, saved.Passthrough["import_revision"]);
        Assert.True(saved.Targets.Gemini.Enabled);
        Assert.False(saved.Targets.Codex.Enabled);
    }

    [Fact]
    public async Task GithubSearchReadsSkillMetadataAndReusesPinnedPreviews()
    {
        const string commit = "1212121212121212121212121212121212121212";
        using var gitHub = new GithubFixture(commit);
        using var client = new HttpClient(gitHub);
        var importer = new SkillImporterService(_engine, client);
        var repository = "https://github.com/fixture/repo/tree/release%2Fnext";
        var browsed = await importer.BrowseRepositorySkillsAsync(repository);
        Assert.Contains(browsed, row => row.Path == ".cursor/rules/fixture.mdc");
        Assert.All(browsed, row => Assert.Equal(commit, row.GitRef));

        var skill = Assert.Single(await importer.BrowseRepositorySkillsAsync(repository, "one exact revision"));
        Assert.Equal("pinned-github", skill.Slug);
        Assert.Equal("Pinned GitHub Skill", skill.Title);
        Assert.Equal("Imported at one exact revision", skill.Description);
        var requestCount = gitHub.Requests.Count;
        var previews = await Task.WhenAll(Enumerable.Range(0, 18).Select(_ =>
            importer.PreviewCommunitySkillAsync(skill.Repo, skill.Path, skill.GitRef)));
        Assert.All(previews, preview =>
        {
            Assert.Equal(commit, preview.Revision);
            Assert.Contains("Pinned instructions.", preview.InstructionsMarkdown);
            Assert.Equal("references/notes.md", Assert.Single(preview.SupportingPaths));
        });
        Assert.Equal(requestCount, gitHub.Requests.Count);
        Assert.DoesNotContain(gitHub.Requests, uri => uri.AbsolutePath.EndsWith("notes.md", StringComparison.Ordinal));

        var rule = Assert.Single(await importer.BrowseRepositorySkillsAsync(repository, "Cursor rule instructions"));
        Assert.Equal(".cursor/rules/fixture.mdc", rule.Path);
        var plan = await importer.PrepareImportAsync(new(Repo: skill.Repo, Path: skill.Path, GitRef: skill.GitRef));
        Assert.Equal("Pinned reference.", plan.SupportingFiles["references/notes.md"]);
        Assert.Single(gitHub.Requests, uri => uri.AbsolutePath.EndsWith("skills/two/SKILL.md", StringComparison.Ordinal));
        Assert.Equal("Pinned reference.", await importer.ReadPreviewFileAsync(skill.Repo, skill.Path, skill.GitRef,
            "references/notes.md", CancellationToken.None));
    }

    [Fact]
    public async Task PreferencePutRequiresBothSnakeCaseBooleansBeforeChangingAnything()
    {
        async Task<HttpResponseMessage> Put(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/preferences") { Content = JsonContent.Create(body) };
            request.Headers.Add(LocalHttpAccess.DesktopHeader, _access.DesktopToken);
            return await _client.SendAsync(request);
        }
        _startup.IsRegistered = true;
        new DesktopPreferencesStore(_discovery).SetOptions("light", true);
        using var missing = await Put(new { theme = "dark", close_to_tray = false });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("light", new DesktopPreferencesStore(_discovery).Read().Theme);
        Assert.True(new DesktopPreferencesStore(_discovery).Read().CloseToTray);
        Assert.True(_startup.IsRegistered);
        Assert.Equal(0, _startup.Changes);
        using var saved = await Put(new { theme = "dark", close_to_tray = true, auto_start = false });
        saved.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("close_to_tray").GetBoolean());
        Assert.False(result.RootElement.GetProperty("auto_start").GetBoolean());
        Assert.False(_startup.IsRegistered);
        Assert.Equal(1, _startup.Changes);
        Assert.Equal("dark", new DesktopPreferencesStore(_discovery).Read().Theme);
    }

    [Fact]
    public async Task QuickThemeChangesOnlyTheTheme()
    {
        var prefs = new DesktopPreferencesStore(_discovery);
        _startup.IsRegistered = true;
        prefs.SetOptions("light", true);
        var bounds = new DesktopBounds(40, 40, 900, 720, false);
        prefs.SetBounds(bounds);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/preferences/theme")
        {
            Content = JsonContent.Create(new { theme = "dark" })
        };
        request.Headers.Add(LocalHttpAccess.DesktopHeader, _access.DesktopToken);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = new DesktopPreferencesStore(_discovery).Read();
        Assert.Equal("dark", saved.Theme);
        Assert.True(saved.CloseToTray);
        Assert.True(_startup.IsRegistered);
        Assert.Equal(0, _startup.Changes);
        using var settingsRequest = Desktop(HttpMethod.Get, "/api/v1/preferences");
        using var settingsResponse = await _client.SendAsync(settingsRequest);
        using var settings = JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync());
        Assert.True(settings.RootElement.GetProperty("auto_start").GetBoolean());
        Assert.True(settings.RootElement.GetProperty("close_to_tray").GetBoolean());
        Assert.Equal(bounds, saved.Bounds);
    }

    [Fact]
    public void Preferences_KeepThemeAndBoundsInTheProfile()
    {
        var store = new DesktopPreferencesStore(_discovery);
        var bounds = new DesktopBounds(120, 80, 900, 650, false);
        store.SetBounds(bounds);
        store.SetOptions("light", false);
        Assert.Throws<ArgumentException>(() => store.SetOptions("unknown", true));
        Assert.Equal(new DesktopPreferences("light", false, bounds), new DesktopPreferencesStore(_discovery).Read());
    }

    private sealed class StartupFixture : IDesktopStartup
    {
        public bool IsRegistered { get; set; }
        public int Changes { get; private set; }

        public (bool Success, string Method, string Details) RegisterOsAutoStart(string? overrideExecutablePath = null, int port = 45880)
        {
            Changes++;
            IsRegistered = true;
            return (true, "test", string.Empty);
        }

        public (bool Success, string Details) UnregisterOsAutoStart()
        {
            Changes++;
            IsRegistered = false;
            return (true, string.Empty);
        }
    }

    private sealed class GithubFixture(string commit) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            string? content = Uri.UnescapeDataString(uri.AbsolutePath) switch
            {
                "/repos/fixture/repo/commits/release/next" => JsonSerializer.Serialize(new { sha = commit }),
                var path when path == $"/repos/fixture/repo/git/trees/{commit}" => """
                    {"truncated":false,"tree":[
                      {"path":"skills/one/SKILL.md","type":"blob"},
                      {"path":"skills/two/SKILL.md","type":"blob"},
                      {"path":".cursor/rules/fixture.mdc","type":"blob"},
                      {"path":"skills/two/references/notes.md","type":"blob"}
                    ]}
                    """,
                var path when path == $"/fixture/repo/{commit}/skills/two/SKILL.md" => """
                    ---
                    name: pinned-github
                    title: Pinned GitHub Skill
                    description: Imported at one exact revision
                    license: MIT
                    ---
                    Pinned instructions.
                    """,
                var path when path == $"/fixture/repo/{commit}/.cursor/rules/fixture.mdc" => """
                    ---
                    description: Cursor fixture
                    globs: '**/*.cs'
                    alwaysApply: false
                    ---
                    Cursor rule instructions.
                    """,
                var path when path == $"/fixture/repo/{commit}/skills/two/references/notes.md" => "Pinned reference.",
                _ => null
            };
            return Task.FromResult(content is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }

    private async Task<HttpResponseMessage> SendDesktopAsync(string url, object? body)
    {
        using var request = Desktop(HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMcpAsync(HttpMethod method, string? json, string? session = null)
    {
        using var request = new HttpRequestMessage(method, "/mcp");
        request.Headers.Add("Authorization", $"Bearer {McpToken}");
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", session);
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        }
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
