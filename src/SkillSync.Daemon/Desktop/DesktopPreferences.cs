using System.Text.Json.Serialization;
using System.Text.Json;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Desktop;

public sealed record DesktopBounds(int X, int Y, int Width, int Height, bool Maximized);
public sealed record DesktopPreferences(string Theme = "system", bool CloseToTray = true, DesktopBounds? Bounds = null,
    bool OnboardingCompleted = false, bool ContinuousLearning = true);
public sealed record DesktopPreferenceRequest(string Theme,
    [property: JsonPropertyName("close_to_tray")] bool? CloseToTray,
    [property: JsonPropertyName("auto_start")] bool? AutoStart);

public sealed class DesktopPreferencesStore(ProviderDiscoveryService discovery)
{
    private readonly object _gate = new();
    private DesktopPreferences? _cached;

    public DesktopPreferences Read()
    {
        lock (_gate)
        {
            if (_cached is not null) return _cached;
            try
            {
                var raw = SkillStorage.ReadText(GetPath(), 32 * 1024);
                _cached = raw is null ? new() : JsonSerializer.Deserialize<DesktopPreferences>(raw) ?? new();
                return _cached;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new();
            }
        }
    }

    public DesktopPreferences SetOptions(string theme, bool closeToTray)
    {
        ValidateTheme(theme);
        return Update(current => current with { Theme = theme, CloseToTray = closeToTray });
    }

    public DesktopPreferences SetTheme(string theme)
    {
        ValidateTheme(theme);
        return Update(current => current with { Theme = theme });
    }

    public DesktopPreferences SetCloseToTray(bool enabled) => Update(current => current with { CloseToTray = enabled });

    public DesktopPreferences SetOnboardingCompleted(bool completed) => Update(current => current with { OnboardingCompleted = completed });

    public DesktopPreferences SetContinuousLearning(bool enabled) => Update(current => current with { ContinuousLearning = enabled });

    private static void ValidateTheme(string theme)
    {
        if (theme is not ("system" or "light" or "dark"))
        {
            throw new ArgumentException("Choose System, Light, or Dark.");
        }
    }

    public void SetBounds(DesktopBounds bounds) => Update(current => current with { Bounds = bounds });

    private string GetPath() => SkillPaths.Resolve(discovery.SkillHubRoot, "desktop.json");

    private DesktopPreferences Update(Func<DesktopPreferences, DesktopPreferences> update)
    {
        lock (_gate)
        {
            var current = Read();
            var next = update(current);
            if (current != next)
            {
                AtomicFileWriter.WriteTextAtomicallyAsync(GetPath(), JsonSerializer.Serialize(next) + "\n").GetAwaiter().GetResult();
                _cached = next;
            }
            return next;
        }
    }
}
