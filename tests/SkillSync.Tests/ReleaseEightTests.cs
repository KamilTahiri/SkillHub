using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>1.8.0: the description summary trap, umbrella folder mode, npx skills links and the visible bootstrap skill.</summary>
public sealed class ReleaseEightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-v18-" + Guid.NewGuid().ToString("N"));
    private readonly string _projects = Path.Combine(Path.GetTempPath(), "skillhub-v18-projects-" + Guid.NewGuid().ToString("N"));

    public ReleaseEightTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "skills"));
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "skills"));
        Directory.CreateDirectory(_projects);
    }

    private const string GuardedBody = """
        # Git workflow
        - Never force-push to main.
        - Push without asking only if the repository is private.
        - You must run the tests before committing.
        """;

    private static CanonicalSkillModel Skill(string name, string description, string body = GuardedBody, string whenToUse = "") => new()
    {
        Name = name, Title = CanonicalSkillModel.HumanizeSlug(name), Description = description, WhenToUse = whenToUse, InstructionsMarkdown = body
    };

    [Fact]
    public void DoctorFlagsADescriptionThatSummarizesGuardrails()
    {
        var summary = Skill("git-flow", "The user's Git defaults: commit to main and push without asking when private, no branches, never force-push.");
        Assert.Contains(SkillDoctor.Lint(summary).Findings, finding => finding.Code == "description-summary" && finding.Severity == "warning");

        // A trigger + pointer passes, whether the pointer sits in the description or in "When to use".
        var pointer = Skill("git-flow", "Git and release rules. Use whenever you commit, push, tag or release. Read the full SKILL.md before running any git command.");
        Assert.DoesNotContain(SkillDoctor.Lint(pointer).Findings, finding => finding.Code == "description-summary");
        var split = Skill("git-flow", "Never push without the user's rules.", whenToUse: "Use before running any git command; load the full skill first.");
        Assert.DoesNotContain(SkillDoctor.Lint(split).Findings, finding => finding.Code == "description-summary");

        // No strict guardrails in the body: a descriptive sentence is fine.
        var plain = Skill("css-notes", "Always prefer CSS grid for page layouts. Use when styling pages.", "Use grid for two-dimensional layouts.");
        Assert.DoesNotContain(SkillDoctor.Lint(plain).Findings, finding => finding.Code == "description-summary");
        // Guardrails quoted in inline code are examples, not rules.
        var quoted = Skill("shell-tips", "Shell snippets and when they help. Never guess flags.", "Examples: `never` `must` `only if` in shell docs.");
        Assert.DoesNotContain(SkillDoctor.Lint(quoted).Findings, finding => finding.Code == "description-summary");
    }

    [Fact]
    public void BootstrapAndToolSchemasTeachTriggerPlusPointerDescriptions()
    {
        var bootstrap = BootstrapManager.CreateBootstrapSkillModel(continuousLearning: true);
        Assert.Contains("trigger + pointer, never a summary of the rules", bootstrap.InstructionsMarkdown);
        Assert.Contains("umbrella: true", bootstrap.InstructionsMarkdown);
        Assert.Contains("Read the full SKILL.md", bootstrap.WhenToUse);
        Assert.Equal("system", bootstrap.SourceCategory);
        Assert.False(SyncEngine.IsUserAuthored(bootstrap));
        Assert.DoesNotContain(SkillDoctor.Lint(bootstrap).Findings, finding => finding.Code == "description-summary");

        var tools = McpToolDefinitions.GetToolsJsonArray();
        foreach (var name in new[] { "create_skill", "update_skill" })
        {
            var description = tools.Single(tool => tool!["name"]!.GetValue<string>() == name)!["inputSchema"]!["properties"]!["description"]!["description"]!.GetValue<string>();
            Assert.Contains("trigger + pointer", description);
            Assert.Contains("MUST read the full SKILL.md", description);
        }
        foreach (var name in new[] { "create_project", "set_project_skills" })
            Assert.NotNull(tools.Single(tool => tool!["name"]!.GetValue<string>() == name)!["inputSchema"]!["properties"]!["umbrella"]);
    }

    [Fact]
    public async Task UmbrellaModeWritesIntoEachChildNeverTheRootAndPicksUpNewSubfolders()
    {
        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.CreateSkillAsync(new CanonicalSkillModel
        {
            Name = "roblox-ui", Title = "Roblox UI", Description = "Roblox HUD layouts. Use when building Roblox UI.", InstructionsMarkdown = "Body."
        }, "test");
        var umbrella = Path.Combine(_projects, "Roblox Games");
        var obby = Path.Combine(umbrella, "obby");
        var tycoon = Path.Combine(umbrella, "tycoon");
        Directory.CreateDirectory(Path.Combine(obby, ".git", "info"));
        Directory.CreateDirectory(Path.Combine(tycoon, ".git", "info"));
        foreach (var ignored in new[] { ".vs", ".git", "node_modules", ".idea" }) Directory.CreateDirectory(Path.Combine(umbrella, ignored));

        var rpc = new McpJsonRpcHandler(engine, null, engine.Discovery);
        await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        var created = await Call(rpc, "create_project", new { path = umbrella, skills = new[] { "roblox-ui" }, umbrella = true });
        Assert.True(created["project"]!["umbrella"]!.GetValue<bool>());
        Assert.Equal(["obby", "tycoon"], Strings(created["project"]!["children"]));

        foreach (var child in new[] { obby, tycoon })
        {
            Assert.True(File.Exists(Path.Combine(child, ".claude", "skills", "roblox-ui", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(child, ".cursor", "rules", "roblox-ui.mdc")));
            var exclude = File.ReadAllText(Path.Combine(child, ".git", "info", "exclude"));
            Assert.Contains("/.claude/skills/roblox-ui/", exclude);
            Assert.DoesNotContain(Path.GetFileName(child) + "/.claude", exclude);
        }
        // Zero files in the umbrella root or in hidden/tooling folders.
        Assert.Empty(Directory.EnumerateFiles(umbrella, "*", SearchOption.TopDirectoryOnly));
        foreach (var folder in new[] { ".claude", ".agents", ".gemini", ".cursor" }) Assert.False(Directory.Exists(Path.Combine(umbrella, folder)));
        foreach (var ignored in new[] { ".vs", "node_modules", ".idea" }) Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(umbrella, ignored)));

        // inspect_project_skills shows the umbrella and its children, and a child sees its skills without a .git boundary warning.
        var inspected = await Call(rpc, "inspect_project_skills", new { path = umbrella });
        Assert.True(inspected["projects"]![0]!["umbrella"]!.GetValue<bool>());
        Assert.Equal(["obby", "tycoon"], Strings(inspected["projects"]![0]!["children"]));
        var inside = await Call(rpc, "inspect_project_skills", new { path = obby });
        Assert.DoesNotContain(Strings(inside["warnings"]), warning => warning.Contains(".git boundary"));
        Assert.Contains(inside["on_disk"]!.AsArray(), item => item!["slug"]!.GetValue<string>() == "roblox-ui" && item["managed_by_skill_hub"]!.GetValue<bool>());

        // A new subfolder gets the skills from the folder watcher, without a manual sync.
        var racer = Path.Combine(umbrella, "racer");
        Directory.CreateDirectory(racer);
        var deployed = Path.Combine(racer, ".claude", "skills", "roblox-ui", "SKILL.md");
        for (var waited = 0; waited < 15000 && !File.Exists(deployed); waited += 100) await Task.Delay(100);
        Assert.True(File.Exists(deployed), "the watcher did not deploy into the new subfolder");

        // Back to a single folder: the children's copies and exclude blocks go, the root gets one copy.
        var single = await Call(rpc, "set_project_skills", new { path = umbrella, skills = Array.Empty<string>(), umbrella = false });
        Assert.False(single["project"]!["umbrella"]!.GetValue<bool>());
        Assert.False(Directory.Exists(Path.Combine(obby, ".claude", "skills", "roblox-ui")));
        Assert.DoesNotContain("roblox-ui", File.ReadAllText(Path.Combine(obby, ".git", "info", "exclude")));
        Assert.True(File.Exists(Path.Combine(umbrella, ".claude", "skills", "roblox-ui", "SKILL.md")));
    }

    [Fact]
    public void UmbrellaChildrenSkipHiddenSystemAndToolingFolders()
    {
        var folder = Path.Combine(_projects, "games");
        foreach (var name in new[] { "alpha", "Beta", ".git", ".vs", "node_modules", "bin", "obj", "$RECYCLE.BIN" }) Directory.CreateDirectory(Path.Combine(folder, name));
        var hidden = Directory.CreateDirectory(Path.Combine(folder, "secret"));
        hidden.Attributes |= FileAttributes.Hidden;
        Assert.Equal(["alpha", "Beta"], WorkspaceService.UmbrellaChildren(folder));
    }

    [Theory]
    [InlineData("vercel-labs/agent-skills", "vercel-labs/agent-skills")]
    [InlineData("https://github.com/vercel-labs/agent-skills.git", "vercel-labs/agent-skills")]
    [InlineData("github:anthropics/skills", "anthropics/skills")]
    [InlineData("git@github.com:anthropics/skills.git", "anthropics/skills")]
    [InlineData("not a repo", null)]
    public void SkillsCliSourcesNormalizeToOwnerRepo(string source, string? expected) =>
        Assert.Equal(expected, SkillsCliLock.NormalizeSource(source));

    [Fact]
    public async Task SkillsInstalledByNpxSkillsAreLinkedToTheirUpstream()
    {
        var agents = Path.Combine(_root, ".agents");
        Directory.CreateDirectory(Path.Combine(_root, ".codex", "skills"));
        var folder = Path.Combine(agents, "skills", "frontend-design");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"),
            "---\nname: frontend-design\ndescription: Distinctive frontend design. Use when building web UI.\n---\n\nDesign carefully.\n");
        await File.WriteAllTextAsync(Path.Combine(agents, ".skill-lock.json"), """
            {"version": 3, "skills": {"frontend-design": {"source": "anthropics/skills", "sourceType": "github",
              "sourceUrl": "https://github.com/anthropics/skills.git", "skillPath": "skills/frontend-design/SKILL.md"}}}
            """);

        await using var engine = new SyncEngine(new ProviderDiscoveryService(_root));
        await engine.SynchronizeAllAsync();
        var skill = Assert.IsType<CanonicalSkillModel>(engine.GetSkill("frontend-design"));
        Assert.Equal("anthropics/skills", skill.ImportSource);
        Assert.Equal("skills/frontend-design/SKILL.md", skill.Passthrough["import_path"]?.ToString());
        Assert.False(SyncEngine.IsUserAuthored(skill));
    }

    private static async Task<JsonNode> Call(McpJsonRpcHandler rpc, string tool, object arguments)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments } });
        var response = JsonNode.Parse((await rpc.ProcessMessageAsync(request))!)!;
        var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? response.ToJsonString();
        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? response["error"] is not null, text);
        return JsonNode.Parse(text)!;
    }

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

    public void Dispose()
    {
        foreach (var path in new[] { _root, _projects })
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
