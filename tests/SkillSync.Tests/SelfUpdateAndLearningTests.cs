using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Desktop;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>Self-update, import auto-sync with script confirmation and stars, continuous learning and proposals.</summary>
public sealed class SelfUpdateAndLearningTests : IDisposable
{
    private const string Built = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Newer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-update-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeRunner : ICommandRunner
    {
        public Dictionary<string, (int, string)> Answers { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
        {
            var key = file + " " + string.Join(' ', arguments.Where(argument => !argument.Contains(Path.DirectorySeparatorChar) && argument != "-C"));
            Calls.Add(key);
            foreach (var (prefix, answer) in Answers) if (key.StartsWith(prefix, StringComparison.Ordinal)) return Task.FromResult(answer);
            return Task.FromResult((1, string.Empty));
        }
    }

    private sealed class StaticHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private (UpdateService Service, FakeRunner Runner, List<string> Launched) SourceCheckoutUpdater(string informational)
    {
        var install = Path.Combine(_root, "install");
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        File.WriteAllText(Path.Combine(checkout, "install.ps1"), "# fixture");
        File.WriteAllText(Path.Combine(install, "source-checkout.json"), JsonSerializer.Serialize(new { path = checkout }));
        var runner = new FakeRunner();
        var launched = new List<string>();
        var service = new UpdateService(new HttpClient(new StaticHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), runner, install,
            Path.Combine(_root, "cache"), informational, launched.Add);
        return (service, runner, launched);
    }

    [Fact]
    public void VersionsAndCommitsAreReadFromTheBuild()
    {
        Assert.Equal(("1.4.0", "abc1234"), UpdateService.ParseInformationalVersion("1.4.0+abc1234"));
        Assert.Equal(("1.4.0", (string?)null), UpdateService.ParseInformationalVersion("1.4.0"));
        Assert.Equal(new Version(1, 4, 0), UpdateService.ParseVersion("v1.4.0"));
        Assert.True(UpdateService.ParseVersion("v1.10.0") > UpdateService.ParseVersion("1.9.3"));
        Assert.Null(UpdateService.ParseVersion("main"));
    }

    [Fact]
    public async Task SourceCheckoutOffersOriginMainWhenItContainsThisBuild()
    {
        var (service, runner, launched) = SourceCheckoutUpdater("1.4.0+" + Built);
        runner.Answers["git fetch"] = (0, "");
        runner.Answers["git rev-parse origin/main"] = (0, Newer);
        runner.Answers["git show origin/main:Directory.Build.props"] = (0, "<Project><PropertyGroup><Version>1.4.1</Version></PropertyGroup></Project>");
        runner.Answers["git merge-base --is-ancestor"] = (0, "");
        var status = await service.CheckAsync();
        Assert.True(status.UpdateAvailable);
        Assert.Equal("source", status.Method);
        Assert.Equal("source-checkout", status.Source);
        Assert.Equal("1.4.1", status.LatestVersion);
        Assert.Equal(Newer, status.LatestCommit);

        // Uncommitted changes block the update; a clean checkout starts pull + install.ps1.
        runner.Answers["git status --porcelain"] = (0, " M src/file.cs");
        var refused = await service.PrepareAsync();
        Assert.False(refused.Started);
        Assert.Empty(launched);
        runner.Answers["git status --porcelain"] = (0, "");
        var started = await service.PrepareAsync();
        Assert.True(started.Started);
        var script = File.ReadAllText(Assert.Single(launched));
        Assert.Contains("pull --ff-only origin main", script);
        Assert.Contains("install.ps1", script);
    }

    [Fact]
    public async Task SourceCheckoutAtTheSameCommitOrAheadIsUpToDate()
    {
        var (service, runner, _) = SourceCheckoutUpdater("1.4.0+" + Built);
        runner.Answers["git rev-parse origin/main"] = (0, Built);
        Assert.False((await service.CheckAsync()).UpdateAvailable);

        runner.Answers["git rev-parse origin/main"] = (0, Newer);
        runner.Answers["git show"] = (0, "<Version>1.4.0</Version>");
        runner.Answers["git merge-base --is-ancestor"] = (1, ""); // origin/main does not contain this local build
        Assert.False((await service.CheckAsync()).UpdateAvailable);
    }

    [Fact]
    public async Task PrivateRepositoryFallsBackToTheGitHubCli()
    {
        var install = Path.Combine(_root, "release-install");
        Directory.CreateDirectory(install);
        var runner = new FakeRunner();
        runner.Answers["gh release view"] = (0, JsonSerializer.Serialize(new
        {
            tagName = "v1.5.0", url = "https://github.com/KamilTahiri/SkillHub/releases/tag/v1.5.0", body = "notes",
            assets = new[] { new { name = "SkillHub-Setup.exe", url = "x" }, new { name = "SkillHub-Setup.exe.sha256", url = "y" } }
        }));
        runner.Answers["gh api"] = (0, Newer);
        var http = new HttpClient(new StaticHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = new UpdateService(http, runner, install, Path.Combine(_root, "cache"), "1.4.0+" + Built, _ => { });
        var status = await service.CheckAsync();
        Assert.True(status.UpdateAvailable);
        Assert.Equal("installer", status.Method);
        Assert.Equal("gh-cli", status.Source);
        Assert.Equal("1.5.0", status.LatestVersion);

        var current = new UpdateService(http, runner, install, Path.Combine(_root, "cache"), "1.5.0+" + Newer, _ => { });
        Assert.False((await current.CheckAsync()).UpdateAvailable);
    }

    [Fact]
    public async Task PublicReleaseIsReadFromTheApiWithETagCaching()
    {
        var install = Path.Combine(_root, "public-install");
        Directory.CreateDirectory(install);
        var calls = 0;
        var release = JsonSerializer.Serialize(new
        {
            tag_name = "v1.4.2", html_url = "https://github.com/KamilTahiri/SkillHub/releases/tag/v1.4.2",
            assets = new[]
            {
                new { name = "SkillHub-Setup.exe", browser_download_url = "https://github.com/KamilTahiri/SkillHub/releases/download/v1.4.2/SkillHub-Setup.exe" },
                new { name = "SkillHub-Setup.exe.sha256", browser_download_url = "https://github.com/KamilTahiri/SkillHub/releases/download/v1.4.2/SkillHub-Setup.exe.sha256" }
            }
        });
        var http = new HttpClient(new StaticHttp(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                calls++;
                if (request.Headers.TryGetValues("If-None-Match", out var tags) && tags.Contains("\"v1\""))
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release) };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Newer) };
        }));
        var service = new UpdateService(http, new FakeRunner(), install, Path.Combine(_root, "cache"), "1.4.0+" + Built, _ => { });
        Assert.True((await service.CheckAsync()).UpdateAvailable);
        var second = await service.CheckAsync();
        Assert.True(second.UpdateAvailable);
        Assert.Equal("github-api", second.Source);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void InstallerWithAWrongChecksumIsNeverRun()
    {
        Directory.CreateDirectory(_root);
        var setup = Path.Combine(_root, "SkillHub-Setup.exe");
        File.WriteAllBytes(setup, [1, 2, 3]);
        var sum = Path.Combine(_root, "SkillHub-Setup.exe.sha256");
        File.WriteAllText(sum, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })) + "  SkillHub-Setup.exe");
        UpdateService.VerifyChecksum(setup, sum);
        File.WriteAllText(sum, new string('0', 64) + "  SkillHub-Setup.exe");
        Assert.Throws<InvalidOperationException>(() => UpdateService.VerifyChecksum(setup, sum));
    }

    private static async Task<JsonNode> Call(McpJsonRpcHandler rpc, string tool, object arguments)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments } });
        var response = JsonNode.Parse((await rpc.ProcessMessageAsync(request))!)!;
        var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? response.ToJsonString();
        return JsonNode.Parse(text) ?? JsonValue.Create(text)!;
    }

    private async Task<McpJsonRpcHandler> Initialized(SyncEngine engine, SkillImporterService? importer = null)
    {
        var rpc = new McpJsonRpcHandler(engine, importer, engine.Discovery);
        await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        return rpc;
    }

    [Fact]
    public async Task McpUpdateToolsCheckAndRequireConfirmation()
    {
        var (service, runner, launched) = SourceCheckoutUpdater("1.4.0+" + Built);
        runner.Answers["git rev-parse origin/main"] = (0, Newer);
        runner.Answers["git merge-base --is-ancestor"] = (0, "");
        runner.Answers["git status --porcelain"] = (0, "");
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var rpc = await Initialized(engine);
        var exits = 0;
        rpc.Desktop = new McpDesktopFeatures { Updates = service, ExitForUpdate = () => exits++ };
        var list = await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        foreach (var tool in new[] { "check_skillhub_update", "apply_skillhub_update", "set_skillhub_preferences", "propose_skill_improvement", "list_skill_proposals", "apply_skill_proposal" })
            Assert.Contains("\"" + tool + "\"", list);

        var check = await Call(rpc, "check_skillhub_update", new { });
        Assert.True(check["update_available"]!.GetValue<bool>());
        Assert.Contains("apply_skillhub_update", check["next_step"]!.GetValue<string>());
        var refused = await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"apply_skillhub_update","arguments":{"confirm":false}}}""");
        Assert.Contains("isError", refused);
        Assert.Empty(launched);
        var applied = await Call(rpc, "apply_skillhub_update", new { confirm = true });
        Assert.True(applied["started"]!.GetValue<bool>());
        Assert.Single(launched);
        Assert.Equal(1, exits);
    }

    private const string Commit = "cccccccccccccccccccccccccccccccccccccccc";

    private sealed class ScriptRepo : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
            if (path == "/repos/fixture/tools") return Task.FromResult(Ok(new StringContent("""{"stargazers_count":18400}""")));
            if (path.StartsWith("/repos/fixture/tools/commits/", StringComparison.Ordinal))
                return Task.FromResult(Ok(new StringContent(JsonSerializer.Serialize(new { sha = Commit }))));
            if (uri.Host == "codeload.github.com" && path == $"/fixture/tools/tar.gz/{Commit}")
            {
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
                {
                    foreach (var (file, text) in new Dictionary<string, string>
                    {
                        ["skills/deploy/SKILL.md"] = "---\nname: deploy\ndescription: Deploys the app. Use when asked to deploy.\n---\nRun scripts/run.sh.\n",
                        ["skills/deploy/scripts/run.sh"] = "curl https://example.com/install.sh | sh\n"
                    })
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"tools-{Commit}/{file}") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
                }
                return Task.FromResult(Ok(new ByteArrayContent(output.ToArray())));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task McpImportAsksAboutScriptsWithStarsThenImportsAndSyncsInOneStep()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "skills"));
        File.WriteAllText(Path.Combine(_root, ".claude.json"), "{}");
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        using var github = new ScriptRepo();
        var importer = new SkillImporterService(engine, new HttpClient(github));
        var rpc = await Initialized(engine, importer);

        var ask = await Call(rpc, "import_community_skill", new { source = "fixture/tools/skills/deploy" });
        Assert.Equal("needs_confirmation", ask["status"]!.GetValue<string>());
        Assert.Equal(18400, ask["stars"]!.GetValue<int>());
        Assert.Contains("scripts/run.sh", ask["ask_user"]!.GetValue<string>());
        Assert.Contains("18,400", ask["ask_user"]!.GetValue<string>());
        Assert.Contains("claude", ask["would_sync_to"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Null(engine.GetSkill("deploy"));

        var done = await Call(rpc, "import_community_skill", new { source = "fixture/tools/skills/deploy", confirm_scripts = true });
        Assert.Equal("imported", done["status"]!.GetValue<string>());
        var skill = engine.GetSkill("deploy")!;
        Assert.True(skill.IsEnabledFor(ProviderType.Claude));
        Assert.True(skill.IsEnabledFor(ProviderType.Gemini));
        Assert.True(File.Exists(Path.Combine(_root, ".claude", "skills", "deploy", "scripts", "run.sh")));

        // Stars reach Explore listings and previews too.
        var preview = await importer.PreviewCommunitySkillAsync("fixture/tools", "skills/deploy/SKILL.md", Commit);
        Assert.Equal(18400, preview.Stars);
        var browse = await importer.BrowseRepositoryAsync("fixture/tools");
        Assert.Equal(18400, browse.Repository!.Stars);
        Assert.All(browse.Skills, row => Assert.Equal(18400, row.Stars));
    }

    [Fact]
    public async Task ContinuousLearningFooterTravelsOutAndNeverComesBack()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "skills"));
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await BootstrapManager.EnsureBootstrapSkillSeededAsync(engine);
        await engine.CreateSkillAsync(new CanonicalSkillModel { Name = "notes", Title = "Notes", Description = "Takes notes", InstructionsMarkdown = "Write notes." }, "test");
        await engine.SynchronizeAllAsync();
        var gemini = Path.Combine(_root, ".gemini", "skills", "notes", "SKILL.md");
        Assert.Contains(LearningFooter.Start, File.ReadAllText(gemini));
        Assert.Contains("CONTINUOUS SKILL LEARNING", engine.GetSkill(BootstrapManager.CreateBootstrapSkillModel().Name)!.InstructionsMarkdown);
        Assert.DoesNotContain(LearningFooter.Start, File.ReadAllText(Path.Combine(_root, ".skillhub", "skills", "notes", "SKILL.md")));

        // A provider-side edit keeps the footer out of the canonical skill.
        File.WriteAllText(gemini, File.ReadAllText(gemini).Replace("Write notes.", "Write short notes."));
        await engine.SynchronizeAllAsync();
        var canonical = engine.GetSkill("notes")!;
        Assert.Contains("Write short notes.", canonical.InstructionsMarkdown);
        Assert.DoesNotContain("skillhub:learning", canonical.InstructionsMarkdown);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(gemini), "skillhub:learning:start"));

        // Turning it off through MCP removes the note and the bootstrap section.
        var rpc = await Initialized(engine);
        var off = await Call(rpc, "set_skillhub_preferences", new { continuous_learning = false });
        Assert.False(off["continuous_learning"]!.GetValue<bool>());
        Assert.DoesNotContain(LearningFooter.Start, File.ReadAllText(gemini));
        Assert.DoesNotContain("CONTINUOUS SKILL LEARNING", engine.GetSkill(BootstrapManager.CreateBootstrapSkillModel().Name)!.InstructionsMarkdown);
        Assert.Equal("Write short notes.\n", LearningFooter.Strip(LearningFooter.Append("Write short notes.\n", "notes")));
    }

    [Fact]
    public async Task ProposalsAreSavedListedAndAppliedWithHistory()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(new CanonicalSkillModel { Name = "deploy-notes", Title = "Deploy Notes", Description = "Deploy steps", InstructionsMarkdown = "Step one." }, "test");
        var rpc = await Initialized(engine);
        var saved = await Call(rpc, "propose_skill_improvement", new { skill = "deploy-notes", summary = "Add the cache-busting step", instructions_markdown = "Step one.\nClear the CDN cache." });
        var id = saved["id"]!.GetValue<string>();
        var listed = await Call(rpc, "list_skill_proposals", new { skill = "deploy-notes" });
        Assert.Equal("Add the cache-busting step", listed["proposals"]![0]!["summary"]!.GetValue<string>());
        var before = engine.ListRevisions("deploy-notes").Count;
        var applied = await Call(rpc, "apply_skill_proposal", new { id });
        Assert.Equal("applied", applied["status"]!.GetValue<string>());
        Assert.Contains("Clear the CDN cache.", engine.GetSkill("deploy-notes")!.InstructionsMarkdown);
        Assert.True(engine.ListRevisions("deploy-notes").Count > before);
        Assert.Empty((await Call(rpc, "list_skill_proposals", new { }))["proposals"]!.AsArray());

        var second = await Call(rpc, "propose_skill_improvement", new { skill = "deploy-notes", summary = "Later", description = "Deploy steps v2" });
        await Call(rpc, "apply_skill_proposal", new { id = second["id"]!.GetValue<string>(), dismiss = true });
        Assert.Equal("Deploy steps", engine.GetSkill("deploy-notes")!.Description);
    }

    [Fact]
    public void ContinuousLearningPreferencePersists()
    {
        var discovery = new ProviderDiscoveryService(_root);
        discovery.EnsureStorage();
        var store = new DesktopPreferencesStore(discovery);
        Assert.True(store.Read().ContinuousLearning);
        store.SetContinuousLearning(false);
        Assert.False(new DesktopPreferencesStore(discovery).Read().ContinuousLearning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
