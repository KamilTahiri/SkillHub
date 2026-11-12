using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Engine;

public sealed record McpClientConfigTarget(
    string ClientId,
    string DisplayName,
    string ConfigFilePath,
    bool ParentDirectoryExists,
    bool ConfigFileExists,
    bool IsSkillHubRegistered
);

public sealed record InstallStatusReport(
    bool IsAutoStartRegistered,
    string AutoStartMethod,
    string AutoStartTarget,
    string ExecutablePath,
    IReadOnlyList<McpClientConfigTarget> McpClientConfigs
);

public sealed partial class InstallManager
{
    public const string McpServerKey = "skillhub";
    public const string LegacyMcpServerKey = "skillsync";

    private const string WindowsRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsRunValueName = "SkillHub";
    private const string LegacyWindowsRunValueName = "SkillSyncDaemon";
    private const string MacLaunchAgentFileName = "com.skillhub.daemon.plist";
    private const string LinuxSystemdServiceName = "skillhub.service";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly string _userHomeRoot;
    private readonly string _appDataRoot;

    public InstallManager(string? customUserHomeRoot = null, string? customAppDataRoot = null)
    {
        _userHomeRoot = string.IsNullOrWhiteSpace(customUserHomeRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(customUserHomeRoot);

        _appDataRoot = string.IsNullOrWhiteSpace(customAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.GetFullPath(customAppDataRoot);
    }

    public static string GetDefaultLocalInstallDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SkillHub");
    }

    public static void AttachParentConsoleIfAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = AttachConsole(-1); // ATTACH_PARENT_PROCESS
            }
            catch
            {
            }
        }
    }

    public static void DetachConsoleIfBackgroundOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = FreeConsole();
            }
            catch
            {
            }
        }
    }

    public string ResolveExecutablePath(string? overrideExecutablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideExecutablePath))
        {
            return Path.GetFullPath(overrideExecutablePath);
        }

        var currentProcess = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcess) &&
            !currentProcess.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !currentProcess.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(currentProcess);
        }

        var entryAssemblyLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entryAssemblyLocation))
        {
            var dir = Path.GetDirectoryName(entryAssemblyLocation)!;
            var exeName = OperatingSystem.IsWindows() ? "SkillHub.exe" : "SkillHub";
            var candidateExe = Path.Combine(dir, exeName);
            if (File.Exists(candidateExe))
            {
                return Path.GetFullPath(candidateExe);
            }

            return Path.GetFullPath(entryAssemblyLocation);
        }

        return "SkillHub";
    }

    public (string Command, string[] Args) BuildMcpStdioCommandSpec(string? overrideExecutablePath = null)
    {
        var resolved = ResolveExecutablePath(overrideExecutablePath);
        if (resolved.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return ("dotnet", [resolved, "mcp-stdio"]);
        }

        return (resolved, ["mcp-stdio"]);
    }

    public string BuildDaemonLaunchCommandLine(string? overrideExecutablePath = null, int port = 45880)
    {
        var resolved = ResolveExecutablePath(overrideExecutablePath);
        if (resolved.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"dotnet \"{resolved}\" --background --port {port}";
        }

        return $"\"{resolved}\" --background --port {port}";
    }

    public IReadOnlyList<McpClientConfigTarget> DiscoverMcpClientConfigTargets()
    {
        var candidates = new List<(string Id, string Name, string Path, bool IsToml)>();

        // 1. Claude Desktop
        string claudeDesktopConfigPath;
        if (OperatingSystem.IsWindows())
        {
            claudeDesktopConfigPath = Path.Combine(_appDataRoot, "Claude", "claude_desktop_config.json");
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

    public IReadOnlyList<McpClientConfigTarget> InjectMcpConfigIntoAllDetectedClients(
        string? overrideExecutablePath = null,
        bool createIfParentExists = true)
    {
        var (command, args) = BuildMcpStdioCommandSpec(overrideExecutablePath);
        var targets = DiscoverMcpClientConfigTargets();

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

            if (string.Equals(target.ClientId, "codex", StringComparison.OrdinalIgnoreCase))
            {
                InjectSkillHubMcpEntryIntoCodexToml(target.ConfigFilePath, command, args);
            }
            else
            {
                InjectSkillHubMcpEntryIntoFile(target.ConfigFilePath, command, args);
            }
        }

        return DiscoverMcpClientConfigTargets();
    }

    public IReadOnlyList<McpClientConfigTarget> RemoveMcpConfigFromAllClients()
    {
        foreach (var target in DiscoverMcpClientConfigTargets())
        {
            if (!target.ConfigFileExists)
            {
                continue;
            }

            if (string.Equals(target.ClientId, "codex", StringComparison.OrdinalIgnoreCase))
            {
                RemoveSkillHubMcpEntryFromCodexToml(target.ConfigFilePath);
            }
            else
            {
                RemoveSkillHubMcpEntryFromFile(target.ConfigFilePath);
            }
        }

        return DiscoverMcpClientConfigTargets();
    }

    /// <summary>
    /// Non-destructively merges `"mcpServers": { "skillhub": { "command": ..., "args": [...] } }`
    /// into an existing or new JSON configuration file while preserving all other properties and servers,
    /// and cleans up any legacy `"skillsync"` entry.
    /// </summary>
    public static void InjectSkillHubMcpEntryIntoFile(string configFilePath, string command, IReadOnlyList<string> args)
    {
        var directory = Path.GetDirectoryName(configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonObject rootObj;
        if (File.Exists(configFilePath))
        {
            var raw = File.ReadAllText(configFilePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                rootObj = new JsonObject();
            }
            else
            {
                var parsed = JsonNode.Parse(
                    raw,
                    nodeOptions: null,
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                rootObj = parsed as JsonObject ?? new JsonObject();
            }
        }
        else
        {
            rootObj = new JsonObject();
        }

        if (rootObj["mcpServers"] is not JsonObject mcpServersObj)
        {
            mcpServersObj = new JsonObject();
            rootObj["mcpServers"] = mcpServersObj;
        }

        // Remove legacy "skillsync" entry if present
        mcpServersObj.Remove(LegacyMcpServerKey);

        var argsArray = new JsonArray();
        foreach (var a in args)
        {
            argsArray.Add(a);
        }

        mcpServersObj[McpServerKey] = new JsonObject
        {
            ["command"] = command,
            ["args"] = argsArray
        };

        var updatedJson = ContentHasher.NormalizeLineEndings(rootObj.ToJsonString(PrettyJsonOptions)) + "\n";
        var tempFile = Path.Combine(directory ?? ".", $".{Path.GetFileName(configFilePath)}.{Guid.NewGuid():N}.skillsync-tmp");
        File.WriteAllText(tempFile, updatedJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempFile, configFilePath, overwrite: true);
    }

    /// <summary>
    /// Non-destructively removes `"skillhub"` (and legacy `"skillsync"`) from `"mcpServers"` in the given JSON file,
    /// leaving all other MCP servers and settings untouched.
    /// </summary>
    public static bool RemoveSkillHubMcpEntryFromFile(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(configFilePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var parsed = JsonNode.Parse(
                raw,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            if (parsed is not JsonObject rootObj || rootObj["mcpServers"] is not JsonObject mcpServersObj)
            {
                return false;
            }

            var removedHub = mcpServersObj.Remove(McpServerKey);
            var removedLegacy = mcpServersObj.Remove(LegacyMcpServerKey);
            if (!removedHub && !removedLegacy)
            {
                return false;
            }

            var directory = Path.GetDirectoryName(configFilePath);
            var updatedJson = ContentHasher.NormalizeLineEndings(rootObj.ToJsonString(PrettyJsonOptions)) + "\n";
            var tempFile = Path.Combine(directory ?? ".", $".{Path.GetFileName(configFilePath)}.{Guid.NewGuid():N}.skillsync-tmp");
            File.WriteAllText(tempFile, updatedJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempFile, configFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSkillHubPresentInJsonFile(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(configFilePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var parsed = JsonNode.Parse(
                raw,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            return parsed is JsonObject rootObj
                && rootObj["mcpServers"] is JsonObject mcpServers
                && mcpServers[McpServerKey] is JsonObject;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Non-destructively injects `[mcp_servers.skillhub]` into OpenAI Codex's `~/.codex/config.toml`
    /// while preserving all existing TOML tables and settings.
    /// </summary>
    public static void InjectSkillHubMcpEntryIntoCodexToml(string tomlFilePath, string command, IReadOnlyList<string> args)
    {
        var directory = Path.GetDirectoryName(tomlFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existingToml = File.Exists(tomlFilePath)
            ? ContentHasher.NormalizeLineEndings(File.ReadAllText(tomlFilePath))
            : string.Empty;

        var cleaned = StripCodexMcpTomlSection(existingToml, "skillhub");
        cleaned = StripCodexMcpTomlSection(cleaned, "skillsync").TrimEnd();

        var formattedArgs = string.Join(", ", args.Select(a => $"\"{a.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""));
        var section = $"\n\n[mcp_servers.skillhub]\ncommand = '{command}'\nargs = [{formattedArgs}]\n";

        var finalToml = string.IsNullOrWhiteSpace(cleaned)
            ? section.TrimStart()
            : cleaned + section;

        var tempFile = Path.Combine(directory ?? ".", $".{Path.GetFileName(tomlFilePath)}.{Guid.NewGuid():N}.skillsync-tmp");
        File.WriteAllText(tempFile, finalToml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempFile, tomlFilePath, overwrite: true);
    }

    public static bool RemoveSkillHubMcpEntryFromCodexToml(string tomlFilePath)
    {
        if (!File.Exists(tomlFilePath))
        {
            return false;
        }

        try
        {
            var existingToml = ContentHasher.NormalizeLineEndings(File.ReadAllText(tomlFilePath));
            var cleaned = StripCodexMcpTomlSection(existingToml, "skillhub");
            cleaned = StripCodexMcpTomlSection(cleaned, "skillsync");

            if (string.Equals(existingToml, cleaned, StringComparison.Ordinal))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(tomlFilePath);
            var tempFile = Path.Combine(directory ?? ".", $".{Path.GetFileName(tomlFilePath)}.{Guid.NewGuid():N}.skillsync-tmp");
            File.WriteAllText(tempFile, cleaned.TrimEnd() + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempFile, tomlFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSkillHubPresentInCodexToml(string tomlFilePath)
    {
        if (!File.Exists(tomlFilePath))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(tomlFilePath);
            return text.Contains("[mcp_servers.skillhub]", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string StripCodexMcpTomlSection(string tomlContent, string serverName)
    {
        if (string.IsNullOrWhiteSpace(tomlContent))
        {
            return string.Empty;
        }

        var pattern = $@"(?ms)^\[mcp_servers\.{Regex.Escape(serverName)}\]\s*\n(?:(?!^\[).*\n?)*";
        return Regex.Replace(tomlContent, pattern, string.Empty);
    }

    public (bool Success, string Method, string Details) RegisterOsAutoStart(string? overrideExecutablePath = null, int port = 45880)
    {
        var exePath = ResolveExecutablePath(overrideExecutablePath);

        if (OperatingSystem.IsWindows())
        {
            return RegisterWindowsAutoStart(exePath, port);
        }

        if (OperatingSystem.IsMacOS())
        {
            return RegisterMacLaunchAgent(exePath, port);
        }

        return RegisterLinuxSystemdService(exePath, port);
    }

    public (bool Success, string Details) UnregisterOsAutoStart()
    {
        if (OperatingSystem.IsWindows())
        {
            return UnregisterWindowsAutoStart();
        }

        if (OperatingSystem.IsMacOS())
        {
            return UnregisterMacLaunchAgent();
        }

        return UnregisterLinuxSystemdService();
    }

    [SupportedOSPlatform("windows")]
    public static void CreateWindowsShortcuts(string targetExecutablePath)
    {
        try
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

            var shortcutPaths = new List<string>();
            if (!string.IsNullOrEmpty(desktopDir) && Directory.Exists(desktopDir))
            {
                shortcutPaths.Add(Path.Combine(desktopDir, "SkillHub.lnk"));
            }

            if (!string.IsNullOrEmpty(startMenuDir) && Directory.Exists(startMenuDir))
            {
                shortcutPaths.Add(Path.Combine(startMenuDir, "SkillHub.lnk"));
            }

            // Remove any old "SkillSync.lnk" shortcuts
            foreach (var dir in new[] { desktopDir, startMenuDir })
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    var oldLnk = Path.Combine(dir, "SkillSync.lnk");
                    if (File.Exists(oldLnk))
                    {
                        try { File.Delete(oldLnk); } catch { }
                    }
                }
            }

            var workDir = Path.GetDirectoryName(targetExecutablePath) ?? "";
            var oldIconPath = Path.Combine(workDir, "skillhub.ico");
            if (File.Exists(oldIconPath))
            {
                try { File.Delete(oldIconPath); } catch { }
            }

            var iconPath = Path.Combine(workDir, "skillhub-v2.ico");
            var iconLocation = File.Exists(iconPath) ? $"{iconPath},0" : $"{targetExecutablePath},0";

            foreach (var lnkPath in shortcutPaths)
            {
                if (File.Exists(lnkPath))
                {
                    try { File.Delete(lnkPath); } catch { }
                }

                CreateShortcutViaWScript(lnkPath, targetExecutablePath, workDir, iconLocation);
            }

            FlushWindowsIconCache();
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    public static void FlushWindowsIconCache()
    {
        try
        {
            // Notify Windows Shell that file/shortcut associations & icons changed (SHCNE_ASSOCCHANGED = 0x08000000)
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    public static void RemoveWindowsShortcuts()
    {
        try
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

            foreach (var dir in new[] { desktopDir, startMenuDir })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var name in new[] { "SkillHub.lnk", "SkillSync.lnk" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p))
                    {
                        try { File.Delete(p); } catch { }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static void CreateShortcutViaWScript(string shortcutPath, string targetPath, string workingDirectory, string iconLocation)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.Description = "SkillHub — AI Agent Skills Manager";
            shortcut.IconLocation = iconLocation;
            shortcut.Save();
        }
        catch
        {
        }
    }

    public InstallStatusReport GetStatusReport(string? overrideExecutablePath = null)
    {
        var exePath = ResolveExecutablePath(overrideExecutablePath);
        var (isReg, method, target) = CheckAutoStartRegistration();
        var clients = DiscoverMcpClientConfigTargets();

        return new InstallStatusReport(
            isReg,
            method,
            target,
            exePath,
            clients);
    }

    public static bool SpawnDetachedBackgroundDaemon(string? overrideExecutablePath = null, int port = 45880, bool showWindow = false)
    {
        try
        {
            var processPath = overrideExecutablePath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            var modeFlag = showWindow ? "" : "--background ";
            var args = $"daemon {modeFlag}--port {port}";
            if (processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(entryAssembly))
                {
                    args = $"\"{entryAssembly}\" daemon {modeFlag}--port {port}";
                }
            }
            else if (processPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                args = $"\"{processPath}\" daemon {modeFlag}--port {port}";
                processPath = "dotnet";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            var proc = Process.Start(startInfo);
            return proc is not null;
        }
        catch
        {
            return false;
        }
    }

    public static int StopRunningDaemonProcesses()
    {
        var currentPid = Environment.ProcessId;
        var stoppedCount = 0;

        foreach (var procName in new[] { "SkillHub", "skillsync" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    using (proc)
                    {
                        if (proc.Id == currentPid)
                        {
                            continue;
                        }

                        try
                        {
                            proc.Kill(entireProcessTree: true);
                            stoppedCount++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        return stoppedCount;
    }

    public string GenerateMacLaunchAgentPlistContent(string executablePath, int port = 45880)
    {
        var isDll = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var programArgsXml = isDll
            ? $"""
                  <string>dotnet</string>
                  <string>{SecurityElementEscape(executablePath)}</string>
                  <string>daemon</string>
                  <string>--background</string>
                  <string>--port</string>
                  <string>{port}</string>
              """
            : $"""
                  <string>{SecurityElementEscape(executablePath)}</string>
                  <string>daemon</string>
                  <string>--background</string>
                  <string>--port</string>
                  <string>{port}</string>
              """;

        var logDir = Path.Combine(_userHomeRoot, ".skillhub", "logs");
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>com.skillhub.daemon</string>
              <key>ProgramArguments</key>
              <array>
            {programArgsXml}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <true/>
              <key>StandardOutPath</key>
              <string>{SecurityElementEscape(Path.Combine(logDir, "daemon.out.log"))}</string>
              <key>StandardErrorPath</key>
              <string>{SecurityElementEscape(Path.Combine(logDir, "daemon.err.log"))}</string>
            </dict>
            </plist>
            """;
    }

    public static string GenerateLinuxSystemdUnitContent(string executablePath, int port = 45880)
    {
        var execStart = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"/usr/bin/env dotnet \"{executablePath}\" daemon --background --port {port}"
            : $"\"{executablePath}\" daemon --background --port {port}";

        return $"""
            [Unit]
            Description=SkillHub AI Skill Manager and Local MCP Server
            After=network.target

            [Service]
            Type=simple
            ExecStart={execStart}
            Restart=always
            RestartSec=3

            [Install]
            WantedBy=default.target
            """;
    }

    [SupportedOSPlatform("windows")]
    private (bool Success, string Method, string Details) RegisterWindowsAutoStart(string exePath, int port)
    {
        try
        {
            var commandLine = BuildDaemonLaunchCommandLine(exePath, port);
            using var key = Registry.CurrentUser.CreateSubKey(WindowsRunRegistryPath, writable: true);
            key.DeleteValue(LegacyWindowsRunValueName, throwOnMissingValue: false);
            key.SetValue(WindowsRunValueName, commandLine, RegistryValueKind.String);
            return (true, "Windows Registry (HKCU\\...\\Run)", commandLine);
        }
        catch (Exception ex)
        {
            return (false, "Windows Registry", ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static (bool Success, string Details) UnregisterWindowsAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunRegistryPath, writable: true);
            key?.DeleteValue(WindowsRunValueName, throwOnMissingValue: false);
            key?.DeleteValue(LegacyWindowsRunValueName, throwOnMissingValue: false);
            return (true, $"Removed '{WindowsRunValueName}' from HKCU\\{WindowsRunRegistryPath}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool Success, string Method, string Details) RegisterMacLaunchAgent(string exePath, int port)
    {
        try
        {
            var launchAgentsDir = Path.Combine(_userHomeRoot, "Library", "LaunchAgents");
            var logDir = Path.Combine(_userHomeRoot, ".skillhub", "logs");
            Directory.CreateDirectory(launchAgentsDir);
            Directory.CreateDirectory(logDir);

            var plistPath = Path.Combine(launchAgentsDir, MacLaunchAgentFileName);
            var content = GenerateMacLaunchAgentPlistContent(exePath, port);
            File.WriteAllText(plistPath, content, new UTF8Encoding(false));

            TryRunProcess("launchctl", $"unload \"{plistPath}\"");
            TryRunProcess("launchctl", $"load -w \"{plistPath}\"");

            return (true, "macOS LaunchAgent", plistPath);
        }
        catch (Exception ex)
        {
            return (false, "macOS LaunchAgent", ex.Message);
        }
    }

    private (bool Success, string Details) UnregisterMacLaunchAgent()
    {
        try
        {
            var plistPath = Path.Combine(_userHomeRoot, "Library", "LaunchAgents", MacLaunchAgentFileName);
            if (File.Exists(plistPath))
            {
                TryRunProcess("launchctl", $"unload \"{plistPath}\"");
                File.Delete(plistPath);
            }

            return (true, $"Removed {plistPath}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool Success, string Method, string Details) RegisterLinuxSystemdService(string exePath, int port)
    {
        try
        {
            var systemdDir = Path.Combine(_userHomeRoot, ".config", "systemd", "user");
            Directory.CreateDirectory(systemdDir);

            var unitPath = Path.Combine(systemdDir, LinuxSystemdServiceName);
            var content = GenerateLinuxSystemdUnitContent(exePath, port);
            File.WriteAllText(unitPath, content, new UTF8Encoding(false));

            TryRunProcess("systemctl", "--user daemon-reload");
            TryRunProcess("systemctl", $"--user enable --now {LinuxSystemdServiceName}");

            return (true, "Linux systemd user service", unitPath);
        }
        catch (Exception ex)
        {
            return (false, "Linux systemd user service", ex.Message);
        }
    }

    private (bool Success, string Details) UnregisterLinuxSystemdService()
    {
        try
        {
            var unitPath = Path.Combine(_userHomeRoot, ".config", "systemd", "user", LinuxSystemdServiceName);
            TryRunProcess("systemctl", $"--user disable --now {LinuxSystemdServiceName}");
            if (File.Exists(unitPath))
            {
                File.Delete(unitPath);
            }

            TryRunProcess("systemctl", "--user daemon-reload");
            return (true, $"Removed {unitPath}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool IsRegistered, string Method, string Target) CheckAutoStartRegistration()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsRunRegistryPath, writable: false);
                var val = key?.GetValue(WindowsRunValueName) as string;
                return (!string.IsNullOrWhiteSpace(val), "Windows Registry (HKCU\\...\\Run)", val ?? "Not registered");
            }
            catch
            {
                return (false, "Windows Registry", "Not registered");
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var plistPath = Path.Combine(_userHomeRoot, "Library", "LaunchAgents", MacLaunchAgentFileName);
            return (File.Exists(plistPath), "macOS LaunchAgent", plistPath);
        }

        var unitPath = Path.Combine(_userHomeRoot, ".config", "systemd", "user", LinuxSystemdServiceName);
        return (File.Exists(unitPath), "Linux systemd user service", unitPath);
    }

    private static void TryRunProcess(string fileName, string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(3000);
        }
        catch
        {
        }
    }

    private static string SecurityElementEscape(string input)
    {
        return input
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
