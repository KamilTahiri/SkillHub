using System.Text.Json;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>Claude Desktop (Microsoft Store) data folders, Anthropic built-in skills, safety backups and the community MCP tools.</summary>
public sealed class ClaudeDesktopAndDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-claude-" + Guid.NewGuid().ToString("N"));

    private string StoreDataRoot => Path.Combine(_root, "AppData", "Local", "Claude-Data", "claude_pzs8sxrjxfjjc");

    private void WriteStoreSkill(string slug, string account = "account-1", string org = "org-1")
    {
        var folder = Path.Combine(StoreDataRoot, "local-agent-mode-sessions", "skills-plugin", account, org, "skills", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {slug}\ndescription: Built-in {slug} skill\n---\n# {slug}\n");
    }

    [Fact]
    public void StoreAppBuiltInSkillsAppearAsClaudeTemplatesWithStableIdentity()
    {
        foreach (var slug in new[] { "google-workspace", "skill-creator", "deep-research", "import-memory", "morning" }) WriteStoreSkill(slug);
        var discovery = new ProviderDiscoveryService(_root);
        var found = discovery.DiscoverExternalCatalogSkills().Where(item => item.OriginProvider == "Claude Built-in").ToArray();
        Assert.Equal(new[] { "deep-research", "google-workspace", "import-memory", "morning", "skill-creator" },
            found.Select(item => item.Slug).Order(StringComparer.Ordinal).ToArray());
        Assert.All(found, item => Assert.Equal("template", item.SourceCategory));

        // A new account or organisation folder does not create a second copy of the same skill.
        var morningId = found.Single(item => item.Slug == "morning").Id;
        Directory.Delete(Path.Combine(StoreDataRoot, "local-agent-mode-sessions"), recursive: true);
        WriteStoreSkill("morning", account: "other-account", org: "other-org");
        Assert.Equal(morningId, discovery.DiscoverExternalCatalogSkills().Single(item => item.Slug == "morning").Id);
    }

    [Fact]
    public async Task CatalogSummariesCountCompanionFilesWithoutLoadingThem()
    {
        WriteStoreSkill("skill-creator");
        WriteStoreSkill("morning");
        var folder = Path.Combine(StoreDataRoot, "local-agent-mode-sessions", "skills-plugin", "account-1", "org-1", "skills", "skill-creator");
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        File.WriteAllText(Path.Combine(folder, "scripts", "run.py"), "print('hi')\n");
        File.WriteAllText(Path.Combine(folder, "reference.md"), "# Reference\n");
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        await engine.SynchronizeAllAsync();
        var summaries = engine.ListSkillSummaries(category: "template");
        var creator = summaries.Single(item => item.Name == "skill-creator");
        Assert.Equal(2, creator.FileCount);
        Assert.Equal(new[] { "reference.md", "scripts/run.py" }, creator.SupportingPaths);
        Assert.Equal(0, summaries.Single(item => item.Name == "morning").FileCount);
    }

    [Fact]
    public void ClaudeDesktopConfigFollowsTheStoreDataFolder()
    {
        var local = Path.Combine(_root, "AppData", "Local");
        var roaming = Path.Combine(_root, "AppData", "Roaming");
        Directory.CreateDirectory(Path.Combine(roaming, "Claude"));
        Assert.Equal(Path.Combine(roaming, "Claude", "claude_desktop_config.json"),
            ProviderDiscoveryService.ResolveClaudeDesktopConfigPath(local, roaming));

        Directory.CreateDirectory(StoreDataRoot);
        File.WriteAllText(Path.Combine(StoreDataRoot, "config.json"), "{}");
        var expected = Path.Combine(StoreDataRoot, "claude_desktop_config.json");
        Assert.Equal(expected, ProviderDiscoveryService.ResolveClaudeDesktopConfigPath(local, roaming));
        Assert.True(new ProviderDiscoveryService(_root).DetectClaude().ClaudeDesktop);

        var manager = new InstallManager(_root);
        var target = manager.DiscoverMcpClientConfigTargets().Single(item => item.ClientId == "claude-desktop");
        Assert.Equal(expected, target.ConfigFilePath);
        manager.ConfigureClient("claude-desktop", "connect");
        Assert.True(InstallManager.IsSkillHubPresentInJsonFile(expected));
        Assert.False(File.Exists(Path.Combine(roaming, "Claude", "claude_desktop_config.json")));
    }

    [Fact]
    public void ConflictReasonsArePlainLanguage()
    {
        Assert.Contains("edited in SkillHub and in this tool", SyncEngine.ExplainConflict("Provider and canonical changes require review."));
        Assert.Contains("already had its own skill", SyncEngine.ExplainConflict("The destination already contains an unowned skill."));
        Assert.Contains("saved it before syncing", SyncEngine.ExplainConflict(null));
        Assert.Equal("Something new.", SyncEngine.ExplainConflict("Something new."));
    }

    [Fact]
    public async Task SafetyBackupsCarryReasonTimeAndCanReplaceTheCurrentSkill()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        var skill = new CanonicalSkillModel { Name = "guarded", Title = "Guarded", Description = "Guarded skill", InstructionsMarkdown = "Original body." };
        await engine.CreateSkillAsync(skill, "test");

        // A legacy snapshot without reason or saved_at still reports a time parsed from its id.
        var folder = Path.Combine(discovery.ConflictsRoot, "guarded");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(_root, ".gemini", "skills", "guarded", "SKILL.md");
        File.WriteAllText(Path.Combine(folder, "20261113-093739918-94b16d1f536741518f7dbc966a21b36d.json"), JsonSerializer.Serialize(new
        {
            destination,
            markdown = "---\nname: guarded\ndescription: Guarded skill\n---\nBackup body.\n",
            supporting_files = new Dictionary<string, string>()
        }));
        var backup = Assert.Single((await engine.GetDiagnosticsAsync()).Conflicts);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 9, 37, 39, 918, TimeSpan.Zero), backup.SavedAt);
        Assert.False(string.IsNullOrWhiteSpace(backup.Reason));
        Assert.True(backup.SkillExists);

        var preview = await engine.GetConflictPreviewAsync(backup.Id);
        Assert.Contains("Backup body.", preview.BackupMarkdown);
        Assert.Contains("Original body.", preview.CurrentMarkdown);

        var restored = await engine.RestoreConflictOverCurrentAsync(backup.Id, backup.Revision);
        Assert.Contains("Backup body.", restored.InstructionsMarkdown);
        Assert.Equal("Guarded", engine.GetSkill("guarded")!.Title);
    }

    [Fact]
    public async Task McpListsTheCommunityDiscoveryTools()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        var rpc = new McpJsonRpcHandler(engine, discovery: discovery);
        await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        var list = await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Contains("\"browse_community_repo\"", list);
        Assert.Contains("\"preview_community_skill\"", list);
        Assert.Contains("skills.sh", list);

        var missing = await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"preview_community_skill","arguments":{"repo":"local/catalog"}}}""");
        Assert.True(missing!.Contains("isError") || missing.Contains("\"error\""), missing);

        var bootstrap = BootstrapManager.CreateBootstrapSkillModel();
        Assert.Contains("preview_community_skill", bootstrap.InstructionsMarkdown);
        Assert.Contains("browse_community_repo", bootstrap.InstructionsMarkdown);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
