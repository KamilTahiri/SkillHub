using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Desktop;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>Antigravity IDE's global folder, identical built-ins listed once, and the Claude "restart to apply" flow.</summary>
public sealed class ReleaseReadinessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-release-" + Guid.NewGuid().ToString("N"));

    private static CanonicalSkillModel Skill(string name) => new()
    {
        Name = name, Title = CanonicalSkillModel.HumanizeSlug(name), Description = "Does " + name, InstructionsMarkdown = "Body of " + name
    };

    [Fact]
    public async Task GeminiSkillsReachAntigravityIdeGlobalConfigFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "skills"));
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "antigravity"));
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "antigravity-ide"));
        var discovery = new ProviderDiscoveryService(_root);
        Assert.Equal(
            [Path.Combine(_root, ".gemini", "antigravity", "skills", "ide-check"), Path.Combine(_root, ".gemini", "config", "skills", "ide-check")],
            discovery.GetAntigravityMirrorSkillDirectories("ide-check"));

        await using var engine = new SyncEngine(discovery);
        await engine.CreateSkillAsync(Skill("ide-check"), "test");
        await engine.SynchronizeAllAsync();
        var ide = Path.Combine(_root, ".gemini", "config", "skills", "ide-check", "SKILL.md");
        Assert.True(File.Exists(ide), "Antigravity IDE reads ~/.gemini/config/skills");
        Assert.Contains("Body of ide-check", File.ReadAllText(ide));
        Assert.True(File.Exists(Path.Combine(_root, ".gemini", "antigravity", "skills", "ide-check", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_root, ".gemini", "skills", "ide-check", "SKILL.md")));
        Assert.Contains(discovery.GetObservableRoots(), root => root.Root == Path.Combine(_root, ".gemini", "config", "skills"));

        // An edit made in the IDE's folder flows back into the library.
        File.WriteAllText(ide, File.ReadAllText(ide).Replace("Body of ide-check", "Edited in Antigravity IDE"));
        await engine.SynchronizeAllAsync();
        Assert.Contains("Edited in Antigravity IDE", engine.GetSkill("ide-check")!.InstructionsMarkdown);
    }

    [Fact]
    public void AntigravityIdeMcpConfigIsAClientTarget()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "config"));
        var manager = new InstallManager(_root);
        var target = manager.DiscoverMcpClientConfigTargets().Single(item => item.ClientId == "antigravity-ide");
        Assert.Equal(Path.Combine(_root, ".gemini", "config", "mcp_config.json"), target.ConfigFilePath);
        manager.ConfigureClient("antigravity-ide", "connect");
        Assert.True(InstallManager.IsSkillHubPresentInJsonFile(target.ConfigFilePath));
    }

    [Fact]
    public void WindsurfMcpConfigIsAClientTargetWhenWindsurfIsInstalled()
    {
        var manager = new InstallManager(_root);
        var target = manager.DiscoverMcpClientConfigTargets().Single(item => item.ClientId == "windsurf");
        Assert.Equal(Path.Combine(_root, ".codeium", "windsurf", "mcp_config.json"), target.ConfigFilePath);
        Assert.False(manager.CanConfigure(target));
        Directory.CreateDirectory(Path.Combine(_root, ".codeium", "windsurf"));
        manager.ConfigureClient("windsurf", "connect");
        Assert.True(InstallManager.IsSkillHubPresentInJsonFile(target.ConfigFilePath));
    }

    [Fact]
    public void IdenticalBuiltInsFromSeveralInstallsAreListedOnce()
    {
        foreach (var install in new[] { "antigravity", "antigravity-cli", "antigravity-ide" })
        {
            var folder = Path.Combine(_root, ".gemini", install, "builtin", "skills", "agy-customizations");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), "---\nname: agy-customizations\ndescription: Same everywhere\n---\nBody\n");
        }
        var different = Path.Combine(_root, ".gemini", "antigravity-ide", "builtin", "skills", "antigravity-guide");
        Directory.CreateDirectory(different);
        File.WriteAllText(Path.Combine(different, "SKILL.md"), "---\nname: antigravity-guide\ndescription: IDE\n---\nIDE body\n");
        var cli = Path.Combine(_root, ".gemini", "antigravity-cli", "builtin", "skills", "antigravity-guide");
        Directory.CreateDirectory(cli);
        File.WriteAllText(Path.Combine(cli, "SKILL.md"), "---\nname: antigravity-guide\ndescription: CLI\n---\nCLI body\n");

        var found = new ProviderDiscoveryService(_root).DiscoverExternalCatalogSkills();
        var agy = Assert.Single(found, item => item.Slug == "agy-customizations");
        Assert.Contains(Path.Combine(".gemini", "antigravity", "builtin"), agy.SkillMdPath);
        // Different content under the same name stays visible, labelled with the install it came from.
        Assert.Equal(["Antigravity CLI Built-in", "Antigravity IDE Built-in"],
            found.Where(item => item.Slug == "antigravity-guide").Select(item => item.OriginProvider).Order(StringComparer.Ordinal).ToArray());
    }

    private sealed class FakeClaude(bool running, bool closes = true) : IClaudeDesktopProcesses
    {
        public bool Running { get; private set; } = running;
        public int Closed { get; private set; }
        public int Launched { get; private set; }
        public bool ConfigExistedAtLaunch { get; private set; }
        public string? ConfigPath { get; set; }
        IReadOnlyList<int> IClaudeDesktopProcesses.Running() => Running ? [42] : [];
        public Task CloseAsync(TimeSpan grace, CancellationToken ct) { Closed++; if (closes) Running = false; return Task.CompletedTask; }
        public bool Launch()
        {
            Launched++;
            ConfigExistedAtLaunch = ConfigPath is not null && InstallManager.IsSkillHubPresentInJsonFile(ConfigPath);
            Running = true;
            return true;
        }
    }

    private string StoreConfig()
    {
        var data = Path.Combine(_root, "AppData", "Local", "Claude-Data", "claude_pzs8sxrjxfjjc");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), "{}");
        File.WriteAllText(Path.Combine(data, "claude_desktop_config.json"), "{\"preferences\":{\"keep\":true}}");
        return Path.Combine(data, "claude_desktop_config.json");
    }

    [Fact]
    public async Task RestartToApplyClosesClaudeWritesTheConfigThenRelaunches()
    {
        var config = StoreConfig();
        var processes = new FakeClaude(running: true) { ConfigPath = config };
        var controller = new ClaudeDesktopController(new InstallManager(_root), processes);
        var result = await controller.RestartAndApplyAsync();
        Assert.True(result.WasRunning);
        Assert.True(result.Relaunched);
        Assert.True(result.Registered);
        Assert.Equal(1, processes.Closed);
        Assert.True(processes.ConfigExistedAtLaunch, "the entry is written before Claude starts again");
        Assert.Contains("\"keep\": true", File.ReadAllText(config).Replace("\"keep\":true", "\"keep\": true"));
    }

    [Fact]
    public async Task RestartToApplyDoesNotStartClaudeThatWasClosedAndStopsWhenClaudeWillNotQuit()
    {
        var config = StoreConfig();
        var idle = new FakeClaude(running: false) { ConfigPath = config };
        var result = await new ClaudeDesktopController(new InstallManager(_root), idle).RestartAndApplyAsync();
        Assert.False(result.WasRunning);
        Assert.False(result.Relaunched);
        Assert.True(result.Registered);
        Assert.Equal(0, idle.Launched);

        File.WriteAllText(config, "{}");
        var stuck = new FakeClaude(running: true, closes: false) { ConfigPath = config };
        var failed = await new ClaudeDesktopController(new InstallManager(_root), stuck).RestartAndApplyAsync();
        Assert.NotNull(failed.Error);
        Assert.False(InstallManager.IsSkillHubPresentInJsonFile(config), "nothing is written while Claude still runs");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
