using SkillSync.Daemon.Desktop;

namespace SkillSync.Daemon.Mcp;

/// <summary>Desktop-only abilities the MCP tools can use: self-update and persisted preferences. Absent in tests and headless hosts.</summary>
public sealed class McpDesktopFeatures
{
    public UpdateService? Updates { get; init; }
    /// <summary>Exits SkillHub shortly after an update was started, so setup can replace its files.</summary>
    public Action? ExitForUpdate { get; init; }
    public DesktopPreferencesStore? Preferences { get; init; }
}
