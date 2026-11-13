using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using Xunit;

namespace SkillSync.Tests;

public sealed class InstallManagerTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string _tempAppData;

    public InstallManagerTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"skillhub-install-tests-{Guid.NewGuid():N}");
        _tempHome = Path.Combine(baseDir, "home");
        _tempAppData = Path.Combine(baseDir, "appdata");

        Directory.CreateDirectory(_tempHome);
        Directory.CreateDirectory(_tempAppData);
    }

    [Fact]
    public void InjectMcpConfig_PreservesExistingServersAndRootSettings_AcrossAllProvidersIncludingCodexToml()
    {
        // 1. Seed existing provider config files with pre-existing MCP servers, legacy "skillsync", & custom user settings
        var claudeDesktopDir = OperatingSystem.IsWindows()
            ? Path.Combine(_tempAppData, "Claude")
            : (OperatingSystem.IsMacOS()
                ? Path.Combine(_tempHome, "Library", "Application Support", "Claude")
                : Path.Combine(_tempHome, ".config", "Claude"));
        Directory.CreateDirectory(claudeDesktopDir);
        var claudeDesktopPath = Path.Combine(claudeDesktopDir, "claude_desktop_config.json");
        File.WriteAllText(claudeDesktopPath, """
        {
          "globalShortcut": "Ctrl+Space",
          "mcpServers": {
            "filesystem": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]
            },
            "skillsync": {
              "command": "old-skillsync",
              "args": ["mcp-stdio"]
            }
          }
        }
        """);

        // Claude Code (~/.claude.json)
        var claudeCodePath = Path.Combine(_tempHome, ".claude.json");
        File.WriteAllText(claudeCodePath, """
        {
          "numStartups": 42,
          "theme": "dark",
          "mcpServers": {
            "github": {
              "command": "github-mcp-server",
              "args": ["stdio"]
            }
          }
        }
        """);

        // Gemini CLI (~/.gemini/settings.json)
        var geminiDir = Path.Combine(_tempHome, ".gemini");
        Directory.CreateDirectory(geminiDir);
        var geminiSettingsPath = Path.Combine(geminiDir, "settings.json");
        File.WriteAllText(geminiSettingsPath, """
        {
          "selectedAuthType": "oauth-personal",
          "mcpServers": {
            "postgres": {
              "command": "pg-mcp",
              "args": []
            }
          }
        }
        """);

        // Antigravity (~/.gemini/antigravity/mcp_config.json)
        var antigravityDir = Path.Combine(_tempHome, ".gemini", "antigravity");
        Directory.CreateDirectory(antigravityDir);
        var antigravityPath = Path.Combine(antigravityDir, "mcp_config.json");
        File.WriteAllText(antigravityPath, """
        {
          "mcpServers": {
            "google-calendar": {
              "command": "gcal-mcp",
              "args": []
            }
          }
        }
        """);

        // Cursor (~/.cursor/mcp.json) - parent directory exists, file does not exist yet
        var cursorDir = Path.Combine(_tempHome, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var cursorMcpPath = Path.Combine(cursorDir, "mcp.json");

        // OpenAI Codex (~/.codex/config.toml)
        var codexDir = Path.Combine(_tempHome, ".codex");
        Directory.CreateDirectory(codexDir);
        var codexTomlPath = Path.Combine(codexDir, "config.toml");
        File.WriteAllText(codexTomlPath, """
        model = "gpt-6.1-sol"

        [desktop]
        sansFontSize = 14

        [mcp_servers.node_repl]
        command = "node_repl.exe"
        args = []
        """);

        var manager = new InstallManager(_tempHome, _tempAppData);
        var fakeExe = Path.Combine(_tempHome, ".skillhub", "bin", OperatingSystem.IsWindows() ? "SkillHub.exe" : "SkillHub");

        // 2. Inject across all 6 detected clients
        var results = manager.InjectMcpConfigIntoAllDetectedClients(fakeExe);

        Assert.Equal(6, results.Count(r => r.IsSkillHubRegistered));

        // Verify Claude Desktop preserved 'globalShortcut' and 'filesystem' server, removed legacy 'skillsync', and added 'skillhub'
        var cdRoot = JsonNode.Parse(File.ReadAllText(claudeDesktopPath))!.AsObject();
        Assert.Equal("Ctrl+Space", cdRoot["globalShortcut"]!.GetValue<string>());
        Assert.NotNull(cdRoot["mcpServers"]!["filesystem"]);
        Assert.Null(cdRoot["mcpServers"]!["skillsync"]);
        Assert.Equal(fakeExe, cdRoot["mcpServers"]!["skillhub"]!["command"]!.GetValue<string>());
        Assert.Equal("mcp-stdio", cdRoot["mcpServers"]!["skillhub"]!["args"]![0]!.GetValue<string>());

        // Verify Claude Code preserved 'numStartups', 'theme', and 'github' server
        var ccRoot = JsonNode.Parse(File.ReadAllText(claudeCodePath))!.AsObject();
        Assert.Equal(42, ccRoot["numStartups"]!.GetValue<int>());
        Assert.Equal("dark", ccRoot["theme"]!.GetValue<string>());
        Assert.NotNull(ccRoot["mcpServers"]!["github"]);
        Assert.NotNull(ccRoot["mcpServers"]!["skillhub"]);

        // Verify Gemini CLI preserved 'selectedAuthType' and 'postgres' server
        var gemRoot = JsonNode.Parse(File.ReadAllText(geminiSettingsPath))!.AsObject();
        Assert.Equal("oauth-personal", gemRoot["selectedAuthType"]!.GetValue<string>());
        Assert.NotNull(gemRoot["mcpServers"]!["postgres"]);
        Assert.NotNull(gemRoot["mcpServers"]!["skillhub"]);

        // Verify Antigravity preserved 'google-calendar'
        var agRoot = JsonNode.Parse(File.ReadAllText(antigravityPath))!.AsObject();
        Assert.NotNull(agRoot["mcpServers"]!["google-calendar"]);
        Assert.NotNull(agRoot["mcpServers"]!["skillhub"]);

        // Verify Cursor mcp.json was created cleanly
        Assert.True(File.Exists(cursorMcpPath));
        var curRoot = JsonNode.Parse(File.ReadAllText(cursorMcpPath))!.AsObject();
        Assert.NotNull(curRoot["mcpServers"]!["skillhub"]);

        // Verify Codex config.toml preserved existing settings and added [mcp_servers.skillhub]
        var codexToml = File.ReadAllText(codexTomlPath);
        Assert.Contains("[mcp_servers.node_repl]", codexToml);
        Assert.Contains("[mcp_servers.skillhub]", codexToml);

        // 3. Now test Uninstall removal: must remove 'skillhub' while preserving all other MCP servers & settings
        var afterRemoval = manager.RemoveMcpConfigFromAllClients();
        Assert.Equal(0, afterRemoval.Count(r => r.IsSkillHubRegistered));

        var cdAfter = JsonNode.Parse(File.ReadAllText(claudeDesktopPath))!.AsObject();
        Assert.Equal("Ctrl+Space", cdAfter["globalShortcut"]!.GetValue<string>());
        Assert.NotNull(cdAfter["mcpServers"]!["filesystem"]);
        Assert.Null(cdAfter["mcpServers"]!["skillhub"]);

        var codexTomlAfter = File.ReadAllText(codexTomlPath);
        Assert.Contains("[mcp_servers.node_repl]", codexTomlAfter);
        Assert.DoesNotContain("[mcp_servers.skillhub]", codexTomlAfter);
    }

    [Fact]
    public void GenerateStartupConfigs_ProducesValidMacPlistAndLinuxSystemdUnit()
    {
        var manager = new InstallManager(_tempHome, _tempAppData);
        var plist = manager.GenerateMacLaunchAgentPlistContent("/Users/dev/.skillhub/bin/SkillHub", 45880);
        Assert.Contains("<string>com.skillhub.daemon</string>", plist);
        Assert.Contains("<string>--background</string>", plist);

        var systemdUnit = InstallManager.GenerateLinuxSystemdUnitContent("/home/dev/.skillhub/bin/SkillHub", 45880);
        Assert.Contains("ExecStart=\"/home/dev/.skillhub/bin/SkillHub\" daemon --background --port 45880", systemdUnit);
        Assert.Contains("WantedBy=default.target", systemdUnit);
    }


    [Fact]
    public void RepairCodexToml_PreservesFollowingTablesCommentsOverridesAndExactBackup()
    {
        var config = Path.Combine(_tempHome, "config.toml");
        var preamble = "# leading comment\r\n";
        var section = "[ mcp_servers . 'skillhub' ] # preserve header\r\n" +
            "command = 'old.exe' # preserve command comment\r\n" +
            "args = [\r\n  'mcp-stdio',\r\n] # preserve args comment\r\n" +
            "startup_timeout_sec = 40 # preserve override\r\n";
        var following = "\r\n[mcp_servers.skillhub.env]\r\nRUNTIME = 'custom'\r\n\r\n" +
            "[unrelated]\r\ntext = \"\"\"\r\n[mcp_servers.skillhub]\r\nthis is literal text\r\n\"\"\"\r\n" +
            "setting = 42\r\n";
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(config, preamble + section + following, utf8);
        var original = File.ReadAllBytes(config);
        const string command = "C:\\Test Folder\\O'Brien\\SkillHub.exe";

        InstallManager.InjectSkillHubMcpEntryIntoCodexToml(config, command, new[] { "mcp-stdio" });

        var edited = File.ReadAllText(config);
        Assert.StartsWith(preamble, edited);
        Assert.EndsWith(following, edited);
        Assert.Contains("command = \"C:\\\\Test Folder\\\\O'Brien\\\\SkillHub.exe\" # preserve command comment", edited);
        Assert.Contains("args = [\"mcp-stdio\"] # preserve args comment", edited);
        Assert.Contains("startup_timeout_sec = 40 # preserve override\r\n", edited);
        Assert.Equal(original, File.ReadAllBytes(config + ".bak"));
        Assert.True(InstallManager.IsSkillHubPresentInCodexToml(config));

        InstallManager.InjectSkillHubMcpEntryIntoCodexToml(config, command, new[] { "mcp-stdio" });
        Assert.Equal(original, File.ReadAllBytes(config + ".bak"));
    }

    [Fact]
    public void RepairJson_PreservesCommentsUserFieldsAndExactBackup()
    {
        var config = Path.Combine(_tempHome, "client.json");
        var original = "{\r\n  // This comment stays.\r\n  \"mcpServers\": { \"skillhub\": { \"command\": \"old\", " +
            "\"args\": [\"mcp-stdio\"], \"env\": {\"KEY\": \"preserved\"} }, \"custom\": { \"command\": \"untouched\" } },\r\n" +
            "  \"theme\": \"dark\"\r\n}";
        File.WriteAllText(config, original);

        InstallManager.InjectSkillHubMcpEntryIntoFile(config, "new", new[] { "mcp-stdio" });

        Assert.Equal(original.Replace("\"command\": \"old\"", "\"command\": \"new\""), File.ReadAllText(config));
        Assert.Equal(original, File.ReadAllText(config + ".bak"));
    }

    [Theory]
    [InlineData("{ \"mcpServers\": [] }")]
    [InlineData("{ \"mcpServers\": { \"skillhub\": null } }")]
    [InlineData("{ \"mcpServers\": ")]
    public void RepairJson_InvalidConfigRemainsByteIdentical(string original)
    {
        var config = Path.Combine(_tempHome, "client.json");
        File.WriteAllText(config, original);
        Assert.ThrowsAny<Exception>(() => InstallManager.InjectSkillHubMcpEntryIntoFile(config, "new", new[] { "mcp-stdio" }));
        Assert.Equal(original, File.ReadAllText(config));
        Assert.False(File.Exists(config + ".bak"));
    }

    [Fact]
    public void RepairCodexToml_RefusesAmbiguousInlineRegistrationWithoutWriting()
    {
        var config = Path.Combine(_tempHome, "config.toml");
        const string original = "[mcp_servers]\nskillhub = { command = \"existing\", args = [] }\n";
        File.WriteAllText(config, original);
        Assert.Throws<InvalidDataException>(() => InstallManager.InjectSkillHubMcpEntryIntoCodexToml(config, "new", new[] { "mcp-stdio" }));
        Assert.Equal(original, File.ReadAllText(config));
        Assert.False(File.Exists(config + ".bak"));
    }

    public void Dispose()
    {
        try
        {
            var parent = Path.GetDirectoryName(_tempHome);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
        catch
        {
        }
    }
}
