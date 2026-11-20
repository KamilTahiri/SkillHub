using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>1.5.0: learning note only on user-authored skills, scope and the always-loaded budget, project workspaces, library Doctor and trigger tester.</summary>
public sealed class LibraryScopeAndProjectsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-scope-" + Guid.NewGuid().ToString("N"));
    private readonly string _projects = Path.Combine(Path.GetTempPath(), "skillhub-projects-" + Guid.NewGuid().ToString("N"));

    public LibraryScopeAndProjectsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "skills"));
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "skills"));
        Directory.CreateDirectory(_projects);
    }

    private string GeminiCopy(string name) => Path.Combine(_root, ".gemini", "skills", name, "SKILL.md");

    private static CanonicalSkillModel Skill(string name, string description, string? importSource = null, string body = "Do the work.", string whenToUse = "")
    {
        var model = new CanonicalSkillModel { Name = name, Title = CanonicalSkillModel.HumanizeSlug(name), Description = description, WhenToUse = whenToUse, InstructionsMarkdown = body };
        if (importSource is not null)
        {
            model.Passthrough["import_source"] = importSource;
            model.Passthrough["import_path"] = "skills/" + name + "/SKILL.md";
        }
        return model;
    }

    private static async Task<JsonNode> Call(McpJsonRpcHandler rpc, string tool, object arguments)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments } });
        var response = JsonNode.Parse((await rpc.ProcessMessageAsync(request))!)!;
        var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? response.ToJsonString();
        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<McpJsonRpcHandler> Initialized(SyncEngine engine)
    {
        var rpc = new McpJsonRpcHandler(engine, null, engine.Discovery);
        await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        return rpc;
    }

    [Fact]
    public async Task LearningNoteGoesOnlyToUserAuthoredSkillsAndIsStrippedFromVendorCopies()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("my-notes", "Takes notes for the user"), "test");
        await engine.CreateSkillAsync(Skill("vendor-audio", "Adds sound to games", "acme/game-skills"), "test");
        // A vendor skill whose provenance was recorded later still carries the old note until the next sync.
        await engine.CreateSkillAsync(Skill("vendor-late", "Adds lighting to games"), "test");
        await engine.SynchronizeAllAsync();
        Assert.Contains(LearningFooter.Start, File.ReadAllText(GeminiCopy("my-notes")));
        Assert.DoesNotContain(LearningFooter.Start, File.ReadAllText(GeminiCopy("vendor-audio")));
        Assert.Contains(LearningFooter.Start, File.ReadAllText(GeminiCopy("vendor-late")));

        await engine.EditSkillAsync("vendor-late", model => model.Passthrough["import_source"] = "acme/game-skills", "test");
        await engine.SynchronizeAllAsync();
        Assert.DoesNotContain(LearningFooter.Start, File.ReadAllText(GeminiCopy("vendor-late")));
        Assert.True(SyncEngine.IsUserAuthored(engine.GetSkill("my-notes")!));
        Assert.False(SyncEngine.IsUserAuthored(engine.GetSkill("vendor-late")!));
        Assert.Equal("user", engine.GetSkill("vendor-late")!.SourceCategory);
    }

    [Fact]
    public async Task ProjectOnlyScopeWithdrawsGlobalCopiesAndShrinksTheBudget()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("web-runtime", "Start web projects on a free port"), "test");
        var bundled = Skill("roblox-ui", new string('r', 600), "acme/roblox");
        bundled.SupportingFiles["references/layout.md"] = "# Layout\n";
        await engine.CreateSkillAsync(bundled, "test");
        await engine.CreateSkillAsync(Skill("roblox-audio", new string('a', 400), "acme/roblox"), "test");
        await engine.SynchronizeAllAsync();
        Assert.True(File.Exists(Path.Combine(_root, ".gemini", "skills", "roblox-ui", "references", "layout.md")));
        var before = ContextBudget.Calculate(engine.ListLibraryWithContents());
        Assert.Equal("acme/roblox", before.Packs[0].Pack);
        Assert.True(before.Packs[0].Tokens > 250);
        Assert.True(File.Exists(GeminiCopy("roblox-ui")));

        var result = await engine.SetScopeAsync(engine.SelectPack(null, "acme/roblox").Select(skill => skill.Name), "project", "test");
        Assert.Equal(["roblox-audio", "roblox-ui"], result.Changed.Order().ToArray());
        Assert.False(File.Exists(GeminiCopy("roblox-ui")));
        // No empty references/ skeleton is left behind in the provider folder.
        Assert.False(Directory.Exists(Path.Combine(_root, ".gemini", "skills", "roblox-ui")));
        Assert.True(File.Exists(GeminiCopy("web-runtime")));
        var after = ContextBudget.Calculate(engine.ListLibraryWithContents());
        Assert.Equal(before.Tokens - before.Packs[0].Tokens, after.Tokens);
        Assert.Equal(2, after.ProjectOnlySkills);
        Assert.Equal("project", engine.GetSkill("roblox-ui")!.Scope);
        Assert.Contains("scope: project", File.ReadAllText(Path.Combine(_root, ".skillhub", "skills", "roblox-ui", "SKILL.md")));

        // Idempotent, and global brings the copies back.
        Assert.Empty((await engine.SetScopeAsync(["roblox-ui"], "project", "test")).Changed);
        await engine.SetScopeAsync(["roblox-ui"], "global", "test");
        Assert.True(File.Exists(GeminiCopy("roblox-ui")));
        Assert.False(engine.GetSkill("roblox-ui")!.Passthrough.ContainsKey(CanonicalSkillModel.ScopeKey));
    }

    [Fact]
    public async Task BudgetWarnsWhenDescriptionsOutgrowTheListingBudget()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        for (var index = 0; index < 18; index++)
            await engine.CreateSkillAsync(Skill("pack-skill-" + index, new string('x', 1000), "acme/big"), "test");
        var budget = ContextBudget.Calculate(engine.ListLibraryWithContents());
        Assert.Equal("over", budget.State);
        Assert.Contains("Project-only", budget.Message);
        await engine.SetScopeAsync(engine.SelectPack(null, "acme/big").Select(skill => skill.Name), "project", "test");
        Assert.Equal("ok", ContextBudget.Calculate(engine.ListLibraryWithContents()).State);
    }

    [Fact]
    public async Task McpScopeBudgetAndListFiltersWork()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("blender-export", "Export Blender scenes to FBX and glTF", "acme/blender"), "test");
        await engine.CreateSkillAsync(Skill("chat-handoff", "Prepare a handoff for a new chat"), "test");
        var rpc = await Initialized(engine);
        var list = await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        foreach (var tool in new[] { "lint_library", "get_context_budget", "set_skill_scope", "test_skill_trigger", "list_projects", "set_project_skills" })
            Assert.Contains("\"" + tool + "\"", list);

        var budget = await Call(rpc, "get_context_budget", new { });
        Assert.Contains(budget["packs"]!.AsArray(), pack => pack!["pack"]!.GetValue<string>() == "acme/blender");
        var scoped = await Call(rpc, "set_skill_scope", new { scope = "project", source = "acme/blender" });
        Assert.Equal("blender-export", scoped["changed"]![0]!.GetValue<string>());
        Assert.True(scoped["always_loaded_tokens_after"]!.GetValue<int>() < scoped["always_loaded_tokens_before"]!.GetValue<int>());
        var projectOnly = await Call(rpc, "list_skills", new { scope = "project" });
        Assert.Equal(1, projectOnly["count"]!.GetValue<int>());
        Assert.Equal("acme/blender", projectOnly["skills"]![0]!["import_source"]!.GetValue<string>());
        var personal = await Call(rpc, "set_skill_scope", new { scope = "off", names = new[] { "chat-handoff" } });
        Assert.Equal("off", personal["scope"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectsWriteSkillsKeepThemOutOfGitAndFlagGlobalDuplicates()
    {
        var project = Path.Combine(_projects, "game");
        Directory.CreateDirectory(Path.Combine(project, ".git", "info"));
        File.WriteAllText(Path.Combine(project, ".git", "info", "exclude"), "# git ls-files --others --exclude-from=.git/info/exclude\n*.log\n");
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("roblox-ui", "Roblox HUD layout", "acme/roblox"), "test");
        await engine.CreateSkillAsync(Skill("roblox-audio", "Roblox sound design", "acme/roblox"), "test");
        await engine.CreateSkillAsync(Skill("chat-handoff", "Prepare a handoff for a new chat"), "test");
        await engine.SynchronizeAllAsync();
        var rpc = await Initialized(engine);

        // Without make_project_only the pack lands in the project and stays global: flagged as duplicates.
        var first = await Call(rpc, "set_project_skills", new { path = project, source = "acme/roblox" });
        Assert.Equal(2, first["project"]!["also_global"]!.AsArray().Count);
        Assert.Contains("make_project_only", first["message"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(project, ".claude", "skills", "roblox-ui", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(project, ".cursor", "rules", "roblox-audio.mdc")));
        var exclude = File.ReadAllText(Path.Combine(project, ".git", "info", "exclude"));
        Assert.Contains("*.log", exclude);
        Assert.Contains(GitExclude.Begin, exclude);
        Assert.Contains("/.claude/skills/roblox-ui/", exclude);
        Assert.Contains("/.cursor/rules/roblox-audio.mdc", exclude);

        // Fix: move the pack to Project-only from the same call.
        var second = await Call(rpc, "set_project_skills", new { path = project, source = "acme/roblox", make_project_only = true });
        Assert.Empty(second["project"]!["also_global"]!.AsArray());
        Assert.False(File.Exists(GeminiCopy("roblox-ui")));
        Assert.True(File.Exists(Path.Combine(project, ".gemini", "skills", "roblox-ui", "SKILL.md")));
        Assert.DoesNotContain(LearningFooter.Start, File.ReadAllText(Path.Combine(project, ".claude", "skills", "roblox-ui", "SKILL.md")));

        var projects = await Call(rpc, "list_projects", new { });
        Assert.Equal(["roblox-audio", "roblox-ui"], projects["projects"]![0]!["skills"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.True(projects["projects"]![0]!["git_repo"]!.GetValue<bool>());

        // Off skills are not written; keep_local false removes the exclude block; removing the project cleans up.
        await engine.SetScopeAsync(["roblox-audio"], "off", "test");
        var workspaces = new WorkspaceService(engine, engine.Discovery);
        var id = workspaces.FindByPath(project)!.Id;
        var status = await workspaces.SyncAsync(id);
        Assert.False(File.Exists(Path.Combine(project, ".claude", "skills", "roblox-audio", "SKILL.md")));
        Assert.Contains("roblox-audio", status.OffSkills!);
        await workspaces.UpdateAsync(id, null, null, null, keepLocal: false);
        Assert.DoesNotContain(GitExclude.Begin, File.ReadAllText(Path.Combine(project, ".git", "info", "exclude")));
        await workspaces.UpdateAsync(id, null, null, null, keepLocal: true);
        Assert.Contains(GitExclude.Begin, File.ReadAllText(Path.Combine(project, ".git", "info", "exclude")));
        await workspaces.RemoveAsync(id, removeFiles: true);
        Assert.Equal("# git ls-files --others --exclude-from=.git/info/exclude\n*.log\n", File.ReadAllText(Path.Combine(project, ".git", "info", "exclude")));
        Assert.False(Directory.Exists(Path.Combine(project, ".claude", "skills", "roblox-ui")));
    }

    [Fact]
    public void GitExcludeHandlesSubfoldersAndWorktreeFiles()
    {
        var repo = Path.Combine(_projects, "mono");
        var app = Path.Combine(repo, "apps", "web");
        Directory.CreateDirectory(app);
        var gitDir = Path.Combine(_projects, "mono.git", "worktrees", "mono");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..");
        File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: " + gitDir);
        GitExclude.Write(app, GitExclude.EntriesFor([".claude/skills/a/SKILL.md", ".claude/skills/a/references/x.md", ".cursor/rules/a.mdc"]));
        var text = File.ReadAllText(Path.Combine(_projects, "mono.git", "info", "exclude"));
        Assert.Contains("/apps/web/.claude/skills/a/\n", text);
        Assert.Contains("/apps/web/.cursor/rules/a.mdc\n", text);
        Assert.DoesNotContain("references", text);
    }

    [Fact]
    public async Task LibraryDoctorFindsCollisionsMissingCompanionsAndLongDescriptions()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("roblox-ui", "Roblox UI with ScreenGui: HUD, shop and settings menus, safe areas and gamepad focus.", "acme/roblox"), "test");
        await engine.CreateSkillAsync(Skill("roblox-ui-layout", "Place Roblox HUD buttons and panels for phone, tablet and console: reserved zones and safe insets.", "acme/roblox",
            "Fonts stay in `roblox-ui-typography`; the sci-fi look is in `roblox-ui-genre-scifi`."), "test");
        await engine.CreateSkillAsync(Skill("roblox-ui-typography", "Fonts and text sizes for Roblox HUD and menus on phones.", "acme/roblox"), "test");
        await engine.CreateSkillAsync(Skill("chat-handoff", "Prepare a durable task handoff and a copyable starting prompt when the user moves to a new chat."), "test");
        await engine.CreateSkillAsync(Skill("long-trigger", new string('d', 700) + " deploy", whenToUse: "Use when " + new string('w', 400)), "test");

        var report = LibraryDoctor.Audit(engine.ListLibraryWithContents());
        var cluster = Assert.Single(report.Collisions);
        Assert.Equal(["roblox-ui", "roblox-ui-layout", "roblox-ui-typography"], cluster.Skills);
        Assert.Contains("hud", cluster.SharedTerms);
        var missing = Assert.Single(report.MissingReferences);
        Assert.Equal("roblox-ui-layout", missing.Skill);
        Assert.Equal(["roblox-ui-genre-scifi"], missing.Missing);
        Assert.Contains(report.Skills.Single(skill => skill.Name == "long-trigger").Findings, finding => finding.Code == "description-combined-length");
        Assert.DoesNotContain(report.Skills.Single(skill => skill.Name == "chat-handoff").Findings, finding => finding.Code == "trigger-overlap");
        Assert.InRange(report.Score, 1, 99);

        // The single-skill Doctor reports the same overlap and missing companion.
        var single = SkillDoctor.Lint(engine.GetSkill("roblox-ui-layout")!, engine.ListSkills());
        Assert.Contains(single.Findings, finding => finding.Code == "trigger-overlap");
        Assert.Contains(single.Findings, finding => finding.Code == "missing-companion" && finding.Message.Contains("roblox-ui-genre-scifi"));

        var rpc = await Initialized(engine);
        var viaMcp = await Call(rpc, "lint_library", new { });
        Assert.Equal(report.Score, viaMcp["score"]!.GetValue<int>());
        Assert.Single(viaMcp["collisions"]!.AsArray());
    }

    [Fact]
    public async Task TriggerTesterRanksTheRightSkillAndFlagsTies()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("web-project-runtime", "Make web projects start reliably with automatic development port selection.", whenToUse: "Use when creating or running web applications."), "test");
        await engine.CreateSkillAsync(Skill("git-autonomous-workflow", "Commit and push to main after tests pass; new projects get a private GitHub repository."), "test");
        await engine.CreateSkillAsync(Skill("roblox-ui", "Roblox HUD, shop and menus.", "acme/roblox"), "test");
        await engine.CreateSkillAsync(Skill("game-ui-ux", "Game HUD and menus for any engine.", "acme/gamedev"), "test");

        var web = TriggerTester.Rank("Start a new web project", engine.ListLibraryWithContents());
        Assert.Equal("web-project-runtime", web.Candidates[0].Name);
        Assert.False(web.Collision);
        var hud = TriggerTester.Rank("Make a HUD with menus", engine.ListLibraryWithContents());
        Assert.True(hud.Collision);
        Assert.Equal(["game-ui-ux", "roblox-ui"], hud.Candidates.Where(candidate => candidate.Verdict == "tie").Select(candidate => candidate.Name).Order().ToArray());

        await engine.SetScopeAsync(["roblox-ui"], "project", "test");
        var global = TriggerTester.Rank("Make a Roblox HUD", engine.ListLibraryWithContents());
        Assert.DoesNotContain(global.Candidates, candidate => candidate.Name == "roblox-ui");
        Assert.Contains("roblox-ui", global.ProjectOnlyMatches);
        var inProject = TriggerTester.Rank("Make a Roblox HUD", engine.ListLibraryWithContents(), includeProjectOnly: true);
        Assert.Equal("roblox-ui", inProject.Candidates[0].Name);

        var rpc = await Initialized(engine);
        var viaMcp = await Call(rpc, "test_skill_trigger", new { prompt = "commit and push my changes" });
        Assert.Equal("git-autonomous-workflow", viaMcp["candidates"]![0]!["name"]!.GetValue<string>());
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _projects })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
