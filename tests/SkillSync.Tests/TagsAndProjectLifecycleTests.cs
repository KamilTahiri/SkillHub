using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>1.6.0: metadata-only tag tools, project lifecycle over MCP, multi-project sharing, umbrella folders and the new rules.</summary>
public sealed class TagsAndProjectLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-tags-" + Guid.NewGuid().ToString("N"));
    private readonly string _projects = Path.Combine(Path.GetTempPath(), "skillhub-lifecycle-" + Guid.NewGuid().ToString("N"));

    public TagsAndProjectLifecycleTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "skills"));
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "skills"));
        Directory.CreateDirectory(_projects);
    }

    private static CanonicalSkillModel Skill(string name, string description, string[] tags, string? importSource = null, string body = "Secret body text.")
    {
        var model = new CanonicalSkillModel
        {
            Name = name, Title = CanonicalSkillModel.HumanizeSlug(name), Description = description, InstructionsMarkdown = body, Tags = [.. tags]
        };
        if (importSource is not null)
        {
            model.Passthrough["import_source"] = importSource;
            model.Passthrough["import_path"] = "skills/" + name + "/SKILL.md";
        }
        return model;
    }

    private static async Task<(McpJsonRpcHandler Rpc, JsonNode Init)> Initialized(SyncEngine engine)
    {
        var rpc = new McpJsonRpcHandler(engine, null, engine.Discovery);
        var init = JsonNode.Parse((await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""))!)!;
        return (rpc, init);
    }

    private static async Task<JsonNode> Call(McpJsonRpcHandler rpc, string tool, object arguments, bool expectError = false)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments } });
        var response = JsonNode.Parse((await rpc.ProcessMessageAsync(request))!)!;
        var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? response.ToJsonString();
        Assert.Equal(expectError, response["result"]?["isError"]?.GetValue<bool>() ?? response["error"] is not null);
        return expectError ? JsonValue.Create(text)! : JsonNode.Parse(text)!;
    }

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

    private async Task<SyncEngine> LibraryAsync()
    {
        var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(Skill("blender-core", "Blender basics", ["blender", "3d"], "acme/blender"), "test");
        await engine.CreateSkillAsync(Skill("blender-sculpt", "Sculpting in Blender", ["blender", "3d-art", "3d-modeling"], "acme/blender"), "test");
        await engine.CreateSkillAsync(Skill("roblox-ui", "Roblox HUD layouts", ["roblox", "ui", "UI"], "acme/roblox"), "test");
        await engine.CreateSkillAsync(Skill("chat-handoff", "Hand a task to a new chat", []), "test");
        await engine.CreateSkillAsync(Skill("game-feel", "Juice and feedback", ["gamedev", "games"]), "test");
        return engine;
    }

    [Fact]
    public void TagHygieneClassifiesNearDuplicates()
    {
        Assert.Equal("separator", TagHygiene.Relation("3d-art", "3dart"));
        Assert.Equal("plural", TagHygiene.Relation("game", "games"));
        Assert.Equal("typo", TagHygiene.Relation("blender", "blendr"));
        Assert.Equal("narrower", TagHygiene.Relation("3d", "3d-modeling"));
        Assert.Null(TagHygiene.Relation("roblox", "blender"));
        Assert.Null(TagHygiene.Relation("ui", "ux"));
    }

    [Fact]
    public async Task ListTagsReturnsMetadataOnlyWithHygieneIssues()
    {
        await using var engine = await LibraryAsync();
        var (rpc, _) = await Initialized(engine);
        var result = await Call(rpc, "list_tags", new { });

        // Never bodies or companion files.
        Assert.DoesNotContain("Secret body text", result.ToJsonString());
        Assert.DoesNotContain("instructions_markdown", result.ToJsonString());

        var tags = result["tags"]!.AsArray();
        var blender = tags.Single(tag => tag!["tag"]!.GetValue<string>() == "blender")!;
        Assert.Equal(2, blender["count"]!.GetValue<int>());
        Assert.Equal(["blender-core", "blender-sculpt"], Strings(blender["skills"]));
        var ui = tags.Single(tag => tag!["tag"]!.GetValue<string>() == "ui")!;
        Assert.Equal(1, ui["count"]!.GetValue<int>());

        var sculpt = result["skills"]!.AsArray().Single(skill => skill!["slug"]!.GetValue<string>() == "blender-sculpt")!;
        Assert.Equal("Blender Sculpt", sculpt["name"]!.GetValue<string>());
        Assert.Equal("global", sculpt["scope"]!.GetValue<string>());
        Assert.Equal("acme/blender", sculpt["source"]!.GetValue<string>());
        Assert.Equal(["blender", "3d-art", "3d-modeling"], Strings(sculpt["tags"]));
        Assert.Equal("personal", result["skills"]!.AsArray().Single(skill => skill!["slug"]!.GetValue<string>() == "game-feel")!["source"]!.GetValue<string>());

        var issues = result["issues"]!;
        Assert.Contains("chat-handoff", Strings(issues["untagged"]));
        Assert.Contains("3d-art", Strings(issues["single_use"]));
        var pairs = issues["near_duplicates"]!.AsArray();
        Assert.Contains(pairs, pair => pair!["tag"]!.GetValue<string>() == "3d" && pair["other"]!.GetValue<string>() == "3d-modeling" && pair["kind"]!.GetValue<string>() == "narrower");
        Assert.Contains(pairs, pair => pair!["kind"]!.GetValue<string>() == "spelling" && pair["tag"]!.GetValue<string>() == "ui");

        var filtered = await Call(rpc, "list_tags", new { tag = "roblox", include_skills = false });
        Assert.Single(filtered["tags"]!.AsArray());
        Assert.Null(filtered["skills"]);
    }

    [Fact]
    public async Task ManageTagsAddsRemovesRenamesMergesAndDeletesInBatch()
    {
        await using var engine = await LibraryAsync();
        var (rpc, _) = await Initialized(engine);
        var revision = engine.GetSkill("blender-core")!.Revision;

        var added = await Call(rpc, "manage_tags", new { action = "add", skills = new[] { "blender-core", "blender-sculpt", "chat-handoff", "missing-skill" }, tags = new[] { "Art Tools" } });
        Assert.Equal(["blender-core", "blender-sculpt", "chat-handoff"], Strings(added["changed"]));
        Assert.Equal("missing-skill", added["skipped"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("art-tools", engine.GetSkill("chat-handoff")!.Tags);
        Assert.True(engine.GetSkill("blender-core")!.Revision > revision);
        // History keeps the previous version for every changed skill.
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_root, ".skillhub", "history", "blender-core")));
        // Provider copies follow.
        Assert.Contains("art-tools", File.ReadAllText(Path.Combine(_root, ".skillhub", "skills", "blender-core", "SKILL.md")));

        var again = await Call(rpc, "manage_tags", new { action = "add", skills = new[] { "blender-core" }, tags = new[] { "art-tools" } });
        Assert.Equal("unchanged", again["status"]!.GetValue<string>());

        var removed = await Call(rpc, "manage_tags", new { action = "remove", source = "acme/blender", tags = new[] { "art-tools" } });
        Assert.Equal(["blender-core", "blender-sculpt"], Strings(removed["changed"]));
        Assert.DoesNotContain("art-tools", engine.GetSkill("blender-sculpt")!.Tags);

        var merged = await Call(rpc, "manage_tags", new { action = "merge", from = new[] { "3d-art", "3d-modeling" }, into = "3d" });
        Assert.True(merged["merged_into_existing"]!.GetValue<bool>());
        Assert.Equal(["blender", "3d"], engine.GetSkill("blender-sculpt")!.Tags.ToArray());

        var renamed = await Call(rpc, "manage_tags", new { action = "rename", from = new[] { "gamedev" }, to = "game-dev" });
        Assert.Equal(1, renamed["updated_skills"]!["gamedev"]!.GetValue<int>());
        Assert.Contains("game-dev", engine.GetSkill("game-feel")!.Tags);

        var deleted = await Call(rpc, "manage_tags", new { action = "delete", tag = "games" });
        Assert.Equal(1, deleted["updated_skills"]!.GetValue<int>());
        Assert.DoesNotContain("games", engine.GetSkill("game-feel")!.Tags);

        var after = await Call(rpc, "list_tags", new { include_skills = false });
        Assert.DoesNotContain("3d-art", after["tags"]!.AsArray().Select(tag => tag!["tag"]!.GetValue<string>()));
        await Call(rpc, "manage_tags", new { action = "explode" }, expectError: true);
    }

    [Fact]
    public async Task OneSkillServesSeveralProjectsIndependently()
    {
        await using var engine = await LibraryAsync();
        var (rpc, _) = await Initialized(engine);
        var modelling = Path.Combine(_projects, "3d-modelling");
        var games = Path.Combine(_projects, "roblox-games");
        Directory.CreateDirectory(modelling);
        Directory.CreateDirectory(games);

        await Call(rpc, "set_project_skills", new { path = modelling, skills = new[] { "blender-core" } });
        await Call(rpc, "set_project_skills", new { path = games, skills = new[] { "blender-core", "roblox-ui" } });
        string Copy(string project) => Path.Combine(project, ".claude", "skills", "blender-core", "SKILL.md");
        Assert.True(File.Exists(Copy(modelling)));
        Assert.True(File.Exists(Copy(games)));

        // Editing the skill reaches every project that uses it.
        await engine.EditSkillAsync("blender-core", model => model.InstructionsMarkdown = "Updated Blender guidance.", "test");
        for (var i = 0; i < 50 && !(File.ReadAllText(Copy(modelling)).Contains("Updated Blender guidance") &&
                                     File.ReadAllText(Copy(games)).Contains("Updated Blender guidance")); i++)
            await Task.Delay(100);
        Assert.Contains("Updated Blender guidance", File.ReadAllText(Copy(modelling)));
        Assert.Contains("Updated Blender guidance", File.ReadAllText(Copy(games)));

        // Removing it from one project leaves the other untouched.
        await Call(rpc, "set_project_skills", new { path = games, skills = new[] { "blender-core" }, mode = "remove" });
        Assert.False(File.Exists(Copy(games)));
        Assert.True(File.Exists(Path.Combine(games, ".claude", "skills", "roblox-ui", "SKILL.md")));
        Assert.True(File.Exists(Copy(modelling)));
        var projects = await Call(rpc, "list_projects", new { });
        var byName = projects["projects"]!.AsArray().ToDictionary(project => project!["name"]!.GetValue<string>(), project => Strings(project!["skills"]));
        Assert.Equal(["blender-core"], byName["3d-modelling"]);
        Assert.Equal(["roblox-ui"], byName["roblox-games"]);
    }

    [Fact]
    public async Task CreateProjectRegistersWithDefaultsAndIsIdempotent()
    {
        await using var engine = await LibraryAsync();
        var (rpc, _) = await Initialized(engine);
        var folder = Path.Combine(_projects, "new-game");
        await Call(rpc, "create_project", new { path = folder }, expectError: true);

        var created = await Call(rpc, "create_project", new { path = folder, create_folder = true, source = "acme/roblox" });
        Assert.Equal("created", created["status"]!.GetValue<string>());
        Assert.Equal("new-game", created["project"]!["name"]!.GetValue<string>());
        Assert.True(created["project"]!["keep_local"]!.GetValue<bool>());
        Assert.Equal(["claude", "codex", "gemini", "cursor"], Strings(created["project"]!["targets"]));
        Assert.True(File.Exists(Path.Combine(folder, ".claude", "skills", "roblox-ui", "SKILL.md")));

        var again = await Call(rpc, "create_project", new { path = folder });
        Assert.Equal("already_registered", again["status"]!.GetValue<string>());
        Assert.Equal(["roblox-ui"], Strings(again["project"]!["skills"]));
        Assert.Single((await Call(rpc, "list_projects", new { }))["projects"]!.AsArray());

        await Call(rpc, "create_project", new { path = _root }, expectError: true);
    }

    [Fact]
    public async Task InspectFindsInheritedSkillsUmbrellasAndGitBoundaries()
    {
        await using var engine = await LibraryAsync();
        await engine.SetScopeAsync(["roblox-ui"], CanonicalSkillModel.ScopeProject, "test");
        var (rpc, _) = await Initialized(engine);
        var umbrella = Path.Combine(_projects, "Roblox Games");
        var obby = Path.Combine(umbrella, "obby");
        var tycoon = Path.Combine(umbrella, "tycoon");
        Directory.CreateDirectory(Path.Combine(obby, ".git", "info"));
        Directory.CreateDirectory(Path.Combine(tycoon, ".git", "info"));
        File.WriteAllText(Path.Combine(tycoon, "default.project.json"), "{}");

        // Before anything is registered: the game folder points at its umbrella and the .git boundary.
        var before = await Call(rpc, "inspect_project_skills", new { path = obby });
        Assert.Empty(before["skills"]!.AsArray());
        Assert.Equal(umbrella, before["umbrella"]!["path"]!.GetValue<string>());
        Assert.Equal(["obby", "tycoon"], Strings(before["umbrella"]!["projects"]));
        Assert.Equal(["obby", "tycoon"], Strings(before["umbrella"]!["separate_git_repos"]));
        Assert.Contains("ask the user", before["umbrella"]!["advice"]!.GetValue<string>());
        Assert.Equal(obby, before["git_root"]!.GetValue<string>());

        // Registering the umbrella warns about the repositories inside it.
        var created = await Call(rpc, "create_project", new { path = umbrella, skills = new[] { "roblox-ui", "blender-core" } });
        Assert.Contains(Strings(created["warnings"]), warning => warning.Contains(".git boundary") && warning.Contains("obby"));

        // A game inside it inherits the umbrella's skills, metadata only, with a boundary warning.
        var inside = await Call(rpc, "inspect_project_skills", new { path = obby });
        Assert.DoesNotContain("Secret body text", inside.ToJsonString());
        var skills = inside["skills"]!.AsArray();
        var ui = skills.Single(skill => skill!["slug"]!.GetValue<string>() == "roblox-ui")!;
        Assert.True(ui["inherited"]!.GetValue<bool>());
        Assert.Equal(umbrella, ui["via"]!.GetValue<string>());
        Assert.Equal("project", ui["scope"]!.GetValue<string>());
        Assert.Equal("Roblox HUD layouts", ui["description"]!.GetValue<string>());
        Assert.Equal(["roblox", "ui"], Strings(ui["tags"]));
        var core = skills.Single(skill => skill!["slug"]!.GetValue<string>() == "blender-core")!;
        Assert.True(core["also_global"]!.GetValue<bool>());
        Assert.Contains(Strings(inside["warnings"]), warning => warning.Contains(".git boundary") && warning.Contains("Antigravity IDE"));
        Assert.Contains(Strings(inside["warnings"]), warning => warning.Contains("also synced globally") && warning.Contains("blender-core"));
        Assert.Contains("ask the user", inside["guidance"]!.GetValue<string>());
        Assert.Contains("blender-core", Strings(inside["global_skills"]));
        Assert.DoesNotContain("roblox-ui", Strings(inside["global_skills"]));

        // The umbrella itself: its own projected files are reported as SkillHub-managed; a hand-made skill is flagged.
        Directory.CreateDirectory(Path.Combine(umbrella, ".claude", "skills", "hand-made"));
        File.WriteAllText(Path.Combine(umbrella, ".claude", "skills", "hand-made", "SKILL.md"), "---\nname: hand-made\n---\nMine.");
        var top = await Call(rpc, "inspect_project_skills", new { path = umbrella });
        Assert.False(top["projects"]![0]!["inherited"]!.GetValue<bool>());
        var onDisk = top["on_disk"]!.AsArray();
        Assert.Contains(onDisk, item => item!["slug"]!.GetValue<string>() == "roblox-ui" && item["managed_by_skill_hub"]!.GetValue<bool>());
        Assert.Contains(onDisk, item => item!["slug"]!.GetValue<string>() == "hand-made" && !item["managed_by_skill_hub"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GenericContainersAreNeverSuggestedAsUmbrellas()
    {
        await using var engine = await LibraryAsync();
        var (rpc, _) = await Initialized(engine);
        var desktop = Path.Combine(_projects, "Desktop");
        foreach (var name in new[] { "app-one", "app-two" })
            Directory.CreateDirectory(Path.Combine(desktop, name, ".git"));
        var result = await Call(rpc, "inspect_project_skills", new { path = Path.Combine(desktop, "app-one") });
        Assert.Null(result["umbrella"]);
    }

    [Fact]
    public async Task TwoProjectsInOneRepositoryKeepTheirOwnExcludeBlocks()
    {
        await using var engine = await LibraryAsync();
        var (rpc, _) = await Initialized(engine);
        var repo = Path.Combine(_projects, "monorepo");
        Directory.CreateDirectory(Path.Combine(repo, ".git", "info"));
        Directory.CreateDirectory(Path.Combine(repo, "game"));
        Directory.CreateDirectory(Path.Combine(repo, "art"));
        await Call(rpc, "set_project_skills", new { path = Path.Combine(repo, "game"), skills = new[] { "roblox-ui" } });
        await Call(rpc, "set_project_skills", new { path = Path.Combine(repo, "art"), skills = new[] { "blender-core" } });
        var exclude = File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude"));
        Assert.Contains("/game/.claude/skills/roblox-ui/", exclude);
        Assert.Contains("/art/.claude/skills/blender-core/", exclude);

        await Call(rpc, "set_project_skills", new { path = Path.Combine(repo, "game"), skills = new[] { "roblox-ui" }, mode = "remove" });
        exclude = File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude"));
        Assert.DoesNotContain("/game/", exclude);
        Assert.Contains("/art/.claude/skills/blender-core/", exclude);
    }

    [Fact]
    public async Task BootstrapAndMcpInstructionsCarryUmbrellaAndAskRules()
    {
        var bootstrap = BootstrapManager.CreateBootstrapSkillModel();
        Assert.Contains("PREFER UMBRELLA FOLDERS", bootstrap.InstructionsMarkdown);
        Assert.Contains("Antigravity IDE", bootstrap.InstructionsMarkdown);
        Assert.Contains("AUDIT RELEVANCE, ASK WHEN UNSURE", bootstrap.InstructionsMarkdown);
        Assert.Contains("Never silently guess", bootstrap.InstructionsMarkdown);
        Assert.Contains("`list_tags`", bootstrap.InstructionsMarkdown);
        Assert.Contains("mcp__skillhub__inspect_project_skills", bootstrap.Targets.Claude.AllowedTools);
        Assert.Contains("mcp__skillhub__manage_tags", bootstrap.Targets.Claude.AllowedTools);

        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        var (rpc, init) = await Initialized(engine);
        var instructions = init["result"]!["instructions"]!.GetValue<string>();
        Assert.Contains("umbrella folder", instructions);
        Assert.Contains(".git", instructions);
        Assert.Contains("ask the user", instructions);
        Assert.Contains("list_tags", instructions);

        var tools = JsonNode.Parse((await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))!)!["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("list_tags", tools);
        Assert.Contains("manage_tags", tools);
        Assert.Contains("create_project", tools);
        Assert.Contains("inspect_project_skills", tools);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _projects })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
