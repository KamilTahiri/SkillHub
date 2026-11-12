using System.Collections.Concurrent;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Watchers;

public sealed class MultiRootFileWatcher : BackgroundService
{
    private readonly ProviderDiscoveryService _discovery;
    private readonly SyncEngine _syncEngine;
    private readonly ILogger<MultiRootFileWatcher> _logger;
    private readonly int _debounceMs;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _directoryWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(ProviderType Provider, string SkillSlug), PendingDebounceItem> _debounceMap = new();
    private FileSystemWatcher? _homeDirectoryWatcher;

    public MultiRootFileWatcher(
        ProviderDiscoveryService discovery,
        SyncEngine syncEngine,
        ILogger<MultiRootFileWatcher> logger,
        int debounceMs = 250)
    {
        _discovery = discovery;
        _syncEngine = syncEngine;
        _logger = logger;
        _debounceMs = debounceMs;
    }

    public void StartWatchers()
    {
        RefreshProviderWatchers();
        StartHomeDirectoryWatcher();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await BootstrapManager.EnsureBootstrapSkillSeededAsync(_syncEngine, stoppingToken).ConfigureAwait(false);
        await _syncEngine.SynchronizeAllAsync(stoppingToken).ConfigureAwait(false);
        StartWatchers();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void RefreshProviderWatchers()
    {
        foreach (var info in _discovery.DiscoverProviders())
        {
            if (!info.IsInstalled && info.Provider != ProviderType.Canonical)
            {
                continue;
            }

            if (info.Provider == ProviderType.Codex)
            {
                // Watch BOTH ~/.codex/skills and ~/.agents/skills when Codex is detected
                foreach (var codexSkillsRoot in _discovery.GetAllCodexSkillsRoots())
                {
                    EnsureWatcherForDirectory(ProviderType.Codex, codexSkillsRoot);
                }
            }
            else
            {
                EnsureWatcherForDirectory(info.Provider, info.SkillsRootDirectory);

                if (info.Provider == ProviderType.Gemini)
                {
                    var antigravityRoot = Path.Combine(_discovery.UserHomeRoot, ".gemini", "antigravity");
                    if (Directory.Exists(antigravityRoot))
                    {
                        EnsureWatcherForDirectory(ProviderType.Gemini, Path.Combine(antigravityRoot, "skills"));
                    }
                }
            }
        }
    }

    private void EnsureWatcherForDirectory(ProviderType provider, string skillsRootDirectory)
    {
        var fullDir = Path.GetFullPath(skillsRootDirectory);
        if (_directoryWatchers.ContainsKey(fullDir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(fullDir);
            var watcher = new FileSystemWatcher(fullDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            watcher.Created += (sender, e) => OnFileSystemEvent(e.FullPath, SyncChangeKind.CreatedOrModified);
            watcher.Changed += (sender, e) => OnFileSystemEvent(e.FullPath, SyncChangeKind.CreatedOrModified);
            watcher.Deleted += (sender, e) => OnFileSystemEvent(e.FullPath, SyncChangeKind.Deleted);
            watcher.Renamed += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.OldFullPath) &&
                    !AtomicFileWriter.IsTemporaryOrIgnoredPath(e.OldFullPath) &&
                    _discovery.TryResolveSkillFromFilePath(e.OldFullPath, out var oldProv, out var oldSlug) &&
                    (!_discovery.TryResolveSkillFromFilePath(e.FullPath, out _, out var newSlug) ||
                     !string.Equals(oldSlug, newSlug, StringComparison.OrdinalIgnoreCase)))
                {
                    ScheduleDebouncedEvent(oldProv, oldSlug, SyncChangeKind.Deleted, e.OldFullPath);
                }

                OnFileSystemEvent(e.FullPath, SyncChangeKind.CreatedOrModified);
            };

            if (_directoryWatchers.TryAdd(fullDir, watcher))
            {
                _logger.LogInformation("Watching {Provider} skills directory: {Path}", provider, fullDir);
            }
            else
            {
                watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start FileSystemWatcher for {Provider} at {Path}", provider, fullDir);
        }
    }

    private void StartHomeDirectoryWatcher()
    {
        if (_homeDirectoryWatcher is not null || !Directory.Exists(_discovery.UserHomeRoot))
        {
            return;
        }

        try
        {
            _homeDirectoryWatcher = new FileSystemWatcher(_discovery.UserHomeRoot)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _homeDirectoryWatcher.Created += (_, e) =>
            {
                var dirName = Path.GetFileName(e.FullPath);
                if (string.Equals(dirName, ".claude", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, ".gemini", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, ".codex", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, ".agents", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, ".cursor", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Detected newly installed AI provider root: {Path}", e.FullPath);
                    RefreshProviderWatchers();
                    _ = _syncEngine.SynchronizeAllAsync();
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch user home directory for new providers.");
        }
    }

    private void OnFileSystemEvent(string fullPath, SyncChangeKind kind)
    {
        if (AtomicFileWriter.IsTemporaryOrIgnoredPath(fullPath))
        {
            return;
        }

        if (_discovery.TryResolveSkillFromFilePath(fullPath, out var provider, out var skillSlug))
        {
            ScheduleDebouncedEvent(provider, skillSlug, kind, fullPath);
            return;
        }

        if (kind == SyncChangeKind.Deleted)
        {
            var watchedDirs = new List<(ProviderType Provider, string Dir)>
            {
                (ProviderType.Canonical, _discovery.CanonicalSkillsRoot),
                (ProviderType.Claude, Path.Combine(_discovery.UserHomeRoot, ".claude", "skills")),
                (ProviderType.Gemini, Path.Combine(_discovery.UserHomeRoot, ".gemini", "skills")),
                (ProviderType.Codex, Path.Combine(_discovery.UserHomeRoot, ".codex", "skills")),
                (ProviderType.Codex, Path.Combine(_discovery.UserHomeRoot, ".agents", "skills"))
            };

            foreach (var (prov, rootDir) in watchedDirs)
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (parent is not null &&
                    string.Equals(
                        Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    var folderSlug = ProviderDiscoveryService.NormalizeSlug(Path.GetFileName(fullPath));
                    if (!string.IsNullOrEmpty(folderSlug))
                    {
                        var primaryPath = Path.Combine(fullPath, "SKILL.md");
                        ScheduleDebouncedEvent(prov, folderSlug, SyncChangeKind.Deleted, primaryPath);
                    }
                }
            }
        }
    }

    private void ScheduleDebouncedEvent(ProviderType provider, string skillSlug, SyncChangeKind kind, string fullPath)
    {
        var key = (provider, skillSlug.ToLowerInvariant());
        var delay = kind == SyncChangeKind.Deleted ? _debounceMs * 2 : _debounceMs;

        _debounceMap.AddOrUpdate(
            key,
            _ => new PendingDebounceItem(provider, skillSlug, kind, fullPath, delay, FlushDebouncedItem),
            (_, existing) =>
            {
                existing.Update(kind, fullPath, delay);
                return existing;
            });
    }

    private void FlushDebouncedItem(PendingDebounceItem item)
    {
        var key = (item.Provider, item.SkillSlug.ToLowerInvariant());
        _debounceMap.TryRemove(key, out _);

        var checkPath = !string.IsNullOrWhiteSpace(item.FullPath)
            ? item.FullPath
            : _discovery.GetSkillPrimaryFilePath(item.Provider, item.SkillSlug);

        var effectiveKind = item.Kind;

        if (effectiveKind == SyncChangeKind.Deleted && File.Exists(checkPath))
        {
            effectiveKind = SyncChangeKind.CreatedOrModified;
        }
        else if (effectiveKind == SyncChangeKind.CreatedOrModified && !File.Exists(checkPath))
        {
            effectiveKind = SyncChangeKind.Deleted;
        }

        _syncEngine.EnqueueFileSystemEvent(item.Provider, item.SkillSlug, effectiveKind, checkPath);
    }

    public override void Dispose()
    {
        _homeDirectoryWatcher?.Dispose();
        foreach (var watcher in _directoryWatchers.Values)
        {
            watcher.Dispose();
        }

        foreach (var pending in _debounceMap.Values)
        {
            pending.Dispose();
        }

        base.Dispose();
    }

    private sealed class PendingDebounceItem : IDisposable
    {
        private readonly Action<PendingDebounceItem> _callback;
        private readonly System.Threading.Timer _timer;
        private readonly object _sync = new();

        public PendingDebounceItem(
            ProviderType provider,
            string skillSlug,
            SyncChangeKind kind,
            string fullPath,
            int delayMs,
            Action<PendingDebounceItem> callback)
        {
            Provider = provider;
            SkillSlug = skillSlug;
            Kind = kind;
            FullPath = fullPath;
            _callback = callback;
            _timer = new System.Threading.Timer(OnTimerElapsed, null, delayMs, Timeout.Infinite);
        }

        public ProviderType Provider { get; }
        public string SkillSlug { get; }
        public SyncChangeKind Kind { get; private set; }
        public string FullPath { get; private set; }

        public void Update(SyncChangeKind newKind, string newFullPath, int delayMs)
        {
            lock (_sync)
            {
                if (newKind == SyncChangeKind.CreatedOrModified || Kind != SyncChangeKind.CreatedOrModified)
                {
                    Kind = newKind;
                }

                FullPath = newFullPath;
                _timer.Change(delayMs, Timeout.Infinite);
            }
        }

        private void OnTimerElapsed(object? state)
        {
            _callback(this);
            Dispose();
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
