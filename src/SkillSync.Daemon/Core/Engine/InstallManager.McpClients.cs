using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>Discovering AI clients and writing SkillHub's MCP entry into their configuration files.</summary>
public sealed partial class InstallManager
{
    public IReadOnlyList<McpClientConfigTarget> DiscoverMcpClientConfigTargets()
    {
        var candidates = new List<(string Id, string Name, string Path, bool IsToml)>();

        // 1. Claude Desktop
        string claudeDesktopConfigPath;
        if (OperatingSystem.IsWindows())
        {
            // The Microsoft Store app keeps its data outside %APPDATA% (Claude-Data or the package's LocalCache),
            // so resolve the file the installed app actually reads.
            claudeDesktopConfigPath = Watchers.ProviderDiscoveryService.ResolveClaudeDesktopConfigPath(LocalAppDataRoot, _appDataRoot);
        }
        else if (OperatingSystem.IsMacOS())
        {
            claudeDesktopConfigPath = Path.Combine(_userHomeRoot, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        }
        else
        {
            claudeDesktopConfigPath = Path.Combine(_userHomeRoot, ".config", "Claude", "claude_desktop_config.json");
        }
        candidates.Add(("claude-desktop", "Claude Desktop", claudeDesktopConfigPath, false));

        // 2. Claude Code (~/.claude.json)
        candidates.Add(("claude-code", "Claude Code", Path.Combine(_userHomeRoot, ".claude.json"), false));

        // 3. Gemini CLI (~/.gemini/settings.json)
        candidates.Add(("gemini-cli", "Google Gemini CLI", Path.Combine(_userHomeRoot, ".gemini", "settings.json"), false));

        // 4. Google Antigravity IDE (~/.gemini/antigravity/mcp_config.json)
        candidates.Add(("antigravity", "Google Antigravity", Path.Combine(_userHomeRoot, ".gemini", "antigravity", "mcp_config.json"), false));

        // 5. Cursor IDE (~/.cursor/mcp.json)
        candidates.Add(("cursor", "Cursor", Path.Combine(_userHomeRoot, ".cursor", "mcp.json"), false));

        // 6. OpenAI Codex (~/.codex/config.toml)
        candidates.Add(("codex", "ChatGPT / OpenAI Codex", Path.Combine(_userHomeRoot, ".codex", "config.toml"), true));

        var results = new List<McpClientConfigTarget>(candidates.Count);
        foreach (var (id, name, path, isToml) in candidates)
        {
            var parentDir = Path.GetDirectoryName(path);
            var parentExists = !string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir);
            var fileExists = File.Exists(path);
            var isRegistered = fileExists && (isToml ? IsSkillHubPresentInCodexToml(path) : IsSkillHubPresentInJsonFile(path));

            results.Add(new McpClientConfigTarget(
                id,
                name,
                path,
                parentExists,
                fileExists,
                isRegistered));
        }

        return results;
    }

    /// <summary>
    /// Whether SkillHub may write this client's MCP configuration. Claude is checked against real install evidence
    /// (the Store package, ~/.claude.json, session history) so a leftover folder never offers a Connect button.
    /// </summary>
    public bool CanConfigure(McpClientConfigTarget target)
    {
        if (target.ConfigFileExists) return true;
        return target.ClientId switch
        {
            "claude-code" => ClaudeEvidence().ClaudeCode,
            "claude-desktop" => ClaudeEvidence().ClaudeDesktop,
            _ => target.ParentDirectoryExists
        };
    }

    /// <summary>A short explanation of how a client was detected, for the Connections screen.</summary>
    public string? DescribeInstall(string clientId) => clientId switch
    {
        "claude-code" or "claude-desktop" => ClaudeEvidence().Description,
        _ => null
    };

    private string LocalAppDataRoot => _hasCustomHome || _appDataRoot != Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        ? Path.Combine(Path.GetDirectoryName(_appDataRoot) ?? _appDataRoot, "Local")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private Watchers.ClaudeInstallEvidence ClaudeEvidence() =>
        new Watchers.ProviderDiscoveryService(_hasCustomHome ? _userHomeRoot : null).DetectClaude();

    public McpClientConfigTarget ConfigureClient(string id, string action)
    {
        if (action is not ("connect" or "repair" or "remove" or "acknowledge"))
        {
            throw new ArgumentException("Choose connect, repair, remove, or acknowledge.");
        }
        var target = DiscoverMcpClientConfigTargets().SingleOrDefault(item => item.ClientId == id)
            ?? throw new KeyNotFoundException("This AI client is unknown.");
        if (action == "acknowledge")
        {
            _restartPending.TryRemove(id, out _);
            return target;
        }
        if (!CanConfigure(target))
        {
            throw new KeyNotFoundException("This AI client was not detected.");
        }
        try
        {
            if (action == "remove")
            {
                if (id == "codex") RemoveSkillHubMcpEntryFromCodexToml(target.ConfigFilePath);
                else RemoveSkillHubMcpEntryFromFile(target.ConfigFilePath);
            }
            else
            {
                var (command, args) = BuildMcpStdioCommandSpec();
                if (id == "codex") InjectSkillHubMcpEntryIntoCodexToml(target.ConfigFilePath, command, args);
                else InjectSkillHubMcpEntryIntoFile(target.ConfigFilePath, command, args);
            }
            return DiscoverMcpClientConfigTargets().Single(item => item.ClientId == id);
        }
        catch (Exception exception) when (IsConfigError(exception))
        {
            return target with { Error = exception.Message };
        }
    }

    public IReadOnlyList<McpClientConfigTarget> InjectMcpConfigIntoAllDetectedClients(
        string? overrideExecutablePath = null,
        bool createIfParentExists = true)
    {
        var (command, args) = BuildMcpStdioCommandSpec(overrideExecutablePath);
        var targets = DiscoverMcpClientConfigTargets();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var target in targets)
        {
            if (string.Equals(target.ClientId, "claude-code", StringComparison.OrdinalIgnoreCase))
            {
                var claudeDir = Path.Combine(_userHomeRoot, ".claude");
                if (!target.ConfigFileExists && !Directory.Exists(claudeDir))
                {
                    continue;
                }
            }
            else if (!target.ConfigFileExists && (!createIfParentExists || !target.ParentDirectoryExists))
            {
                continue;
            }

            try
            {
                if (target.ClientId == "codex")
                {
                    InjectSkillHubMcpEntryIntoCodexToml(target.ConfigFilePath, command, args);
                }
                else
                {
                    InjectSkillHubMcpEntryIntoFile(target.ConfigFilePath, command, args);
                }
            }
            catch (Exception exception) when (IsConfigError(exception))
            {
                errors[target.ClientId] = exception.Message;
            }
        }
        return DiscoverMcpClientConfigTargets().Select(target => target with { Error = errors.GetValueOrDefault(target.ClientId) }).ToArray();
    }

    public IReadOnlyList<McpClientConfigTarget> RemoveMcpConfigFromAllClients()
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in DiscoverMcpClientConfigTargets().Where(target => target.ConfigFileExists))
        {
            try
            {
                if (target.ClientId == "codex")
                {
                    RemoveSkillHubMcpEntryFromCodexToml(target.ConfigFilePath);
                }
                else
                {
                    RemoveSkillHubMcpEntryFromFile(target.ConfigFilePath);
                }
            }
            catch (Exception exception) when (IsConfigError(exception))
            {
                errors[target.ClientId] = exception.Message;
            }
        }
        return DiscoverMcpClientConfigTargets().Select(target => target with { Error = errors.GetValueOrDefault(target.ClientId) }).ToArray();
    }

    public static void InjectSkillHubMcpEntryIntoFile(string configFilePath, string command, IReadOnlyList<string> args) =>
        ConfigFileEditor.Edit(configFilePath, text => JsonMcpConfigEditor.Update(text, command, args));

    public static bool RemoveSkillHubMcpEntryFromFile(string configFilePath) =>
        ConfigFileEditor.Edit(configFilePath, JsonMcpConfigEditor.RemoveRegistration);

    public static bool IsSkillHubPresentInJsonFile(string configFilePath) =>
        IsPresent(configFilePath, JsonMcpConfigEditor.Contains);

    public static void InjectSkillHubMcpEntryIntoCodexToml(string tomlFilePath, string command, IReadOnlyList<string> args) =>
        ConfigFileEditor.Edit(tomlFilePath, text => TomlMcpConfigEditor.Update(text, command, args));

    public static bool RemoveSkillHubMcpEntryFromCodexToml(string tomlFilePath) =>
        ConfigFileEditor.Edit(tomlFilePath, TomlMcpConfigEditor.Remove);

    public static bool IsSkillHubPresentInCodexToml(string tomlFilePath) =>
        IsPresent(tomlFilePath, TomlMcpConfigEditor.Contains);

    private static bool IsPresent(string path, Func<string, bool> probe)
    {
        try
        {
            return probe(ConfigFileEditor.Read(path));
        }
        catch (Exception exception) when (IsConfigError(exception))
        {
            return false;
        }
    }

    private static bool IsConfigError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException;
}
