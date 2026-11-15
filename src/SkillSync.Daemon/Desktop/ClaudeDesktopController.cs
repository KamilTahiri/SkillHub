using System.Diagnostics;
using SkillSync.Daemon.Core.Engine;

namespace SkillSync.Daemon.Desktop;

/// <summary>Finds, closes and starts the Claude Desktop app. Separate from the controller so tests never touch real processes.</summary>
public interface IClaudeDesktopProcesses
{
    /// <summary>Process ids of the running Claude Desktop app (not the Claude Code CLI, which is also claude.exe).</summary>
    IReadOnlyList<int> Running();
    /// <summary>Asks the app to quit, then ends whatever is left after the grace period.</summary>
    Task CloseAsync(TimeSpan grace, CancellationToken ct);
    /// <summary>Starts Claude Desktop again; false when no installation was found.</summary>
    bool Launch();
}

public sealed record ClaudeRestartResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("was_running")] bool WasRunning,
    [property: System.Text.Json.Serialization.JsonPropertyName("relaunched")] bool Relaunched,
    [property: System.Text.Json.Serialization.JsonPropertyName("registered")] bool Registered,
    [property: System.Text.Json.Serialization.JsonPropertyName("config_path")] string ConfigPath,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error);

/// <summary>
/// Claude Desktop keeps its settings in memory and writes claude_desktop_config.json back when it saves preferences or quits,
/// which drops an MCP entry that was added while it ran. "Restart Claude to apply" closes the app first, writes the entry
/// while nothing else holds the file, checks it, and starts Claude again.
/// </summary>
public sealed class ClaudeDesktopController(InstallManager install, IClaudeDesktopProcesses processes)
{
    public bool IsRunning => processes.Running().Count > 0;

    public async Task<ClaudeRestartResult> RestartAndApplyAsync(CancellationToken ct = default)
    {
        var wasRunning = IsRunning;
        if (wasRunning)
        {
            await processes.CloseAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            for (var attempt = 0; attempt < 40 && IsRunning; attempt++) await Task.Delay(250, ct).ConfigureAwait(false);
            if (IsRunning)
                return new ClaudeRestartResult(true, false, false, string.Empty, "Claude Desktop did not close. Quit it from the tray icon and try again.");
        }
        var target = install.ConfigureClient("claude-desktop", "connect");
        var relaunched = wasRunning && processes.Launch();
        if (relaunched) install.ConfigureClient("claude-desktop", "acknowledge");
        return new ClaudeRestartResult(wasRunning, relaunched, target.IsSkillHubRegistered, target.ConfigFilePath, target.Error);
    }
}

/// <summary>The real Windows processes: the Microsoft Store package (Claude_*) or the classic %LOCALAPPDATA%\AnthropicClaude install.</summary>
public sealed class WindowsClaudeDesktopProcesses : IClaudeDesktopProcesses
{
    private static bool IsDesktopApp(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName ?? string.Empty;
            return path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static List<Process> Find() =>
        Process.GetProcessesByName("Claude").Where(process =>
        {
            if (IsDesktopApp(process)) return true;
            process.Dispose();
            return false;
        }).ToList();

    public IReadOnlyList<int> Running()
    {
        var found = Find();
        try { return found.Select(process => process.Id).ToArray(); }
        finally { found.ForEach(process => process.Dispose()); }
    }

    public async Task CloseAsync(TimeSpan grace, CancellationToken ct)
    {
        var found = Find();
        try
        {
            foreach (var process in found)
            {
                try { if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow(); }
                catch (InvalidOperationException) { }
            }
            var deadline = DateTime.UtcNow + grace;
            while (DateTime.UtcNow < deadline && found.Any(process => { try { return !process.HasExited; } catch { return false; } }))
                await Task.Delay(250, ct).ConfigureAwait(false);
            // Claude minimises to the tray instead of quitting, so whatever is still running is ended.
            foreach (var process in found)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        finally { found.ForEach(process => process.Dispose()); }
    }

    public bool Launch()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Packages");
        var family = Directory.Exists(packages) ? Directory.EnumerateDirectories(packages, "Claude_*").Select(Path.GetFileName).FirstOrDefault() : null;
        var start = family is not null
            // Store apps start through their application user model id; explorer.exe is the documented launcher.
            ? new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { $@"shell:AppsFolder\{family}!Claude" } }
            : File.Exists(Path.Combine(local, "AnthropicClaude", "claude.exe"))
                ? new ProcessStartInfo(Path.Combine(local, "AnthropicClaude", "claude.exe")) { UseShellExecute = false }
                : null;
        if (start is null) return false;
        try { Process.Start(start)?.Dispose(); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
