using System.Collections.Concurrent;
using System.Threading.Channels;
using SkillSync.Daemon.Core.Engine;

namespace SkillSync.Daemon.Watchers;

public sealed class MultiRootFileWatcher : BackgroundService
{
    private const int MaxPendingPaths = 4096;
    private readonly ProviderDiscoveryService _discovery;
    private readonly SyncEngine _engine;
    private readonly ILogger<MultiRootFileWatcher> _logger;
    private readonly TimeSpan _debounce;
    private readonly object _watcherLock = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(SkillPaths.Comparer);
    private readonly ConcurrentDictionary<string, bool> _paths = new(SkillPaths.Comparer);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _scanRequested;
    private bool _disposed;
    private string? _lastError;

    public MultiRootFileWatcher(ProviderDiscoveryService discovery, SyncEngine syncEngine,
        ILogger<MultiRootFileWatcher> logger, int debounceMs = 250)
    {
        _discovery = discovery;
        _engine = syncEngine;
        _logger = logger;
        _debounce = TimeSpan.FromMilliseconds(debounceMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshProviderWatchers();
        await _engine.SynchronizeAllAsync(stoppingToken).ConfigureAwait(false);
        await BootstrapManager.EnsureBootstrapSkillSeededAsync(_engine, stoppingToken).ConfigureAwait(false);
        StartWatchers();

        using var cancellation = stoppingToken.Register(_stop.Cancel);
        await _pump!.ConfigureAwait(false);
    }

    public void StartWatchers()
    {
        RefreshProviderWatchers();
        lock (_watcherLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pump ??= PumpAsync(_stop.Token);
        }
    }

    public (bool Running, int Roots, string? LastError) GetHealth()
    {
        lock (_watcherLock)
        {
            return (_pump is { IsCompleted: false }, _watchers.Count, Volatile.Read(ref _lastError));
        }
    }

    public void RefreshProviderWatchers()
    {
        if (RefreshRoots())
        {
            RequestScan();
        }
    }

    private bool RefreshRoots()
    {
        var roots = _discovery.GetObservableRoots().Select(item => item.Root).Where(Directory.Exists).ToHashSet(SkillPaths.Comparer);
        if (Directory.Exists(_discovery.UserHomeRoot))
        {
            roots.Add(_discovery.UserHomeRoot);
        }
        lock (_watcherLock)
        {
            if (_disposed)
            {
                return false;
            }

            var changed = false;
            foreach (var stale in _watchers.Keys.Where(path => !roots.Contains(path)).ToArray())
            {
                _watchers[stale].Dispose();
                _watchers.Remove(stale);
                changed = true;
            }
            foreach (var root in roots.Where(root => !_watchers.ContainsKey(root)))
            {
                try
                {
                    SkillPaths.AssertNoReparsePoints(root);
                    var isHome = SkillPaths.Comparer.Equals(root, _discovery.UserHomeRoot);
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = !isHome,
                        NotifyFilter = isHome ? NotifyFilters.DirectoryName :
                            NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    if (isHome)
                    {
                        watcher.Created += (_, _) => RequestScan();
                        watcher.Deleted += (_, _) => RequestScan();
                        watcher.Renamed += (_, _) => RequestScan();
                    }
                    else
                    {
                        watcher.Created += (_, item) => Observe(item.FullPath, structural: true);
                        watcher.Changed += (_, item) => Observe(item.FullPath);
                        watcher.Deleted += (_, item) => Observe(item.FullPath, structural: true);
                        watcher.Renamed += (_, item) =>
                        {
                            Observe(item.OldFullPath, structural: true);
                            Observe(item.FullPath, structural: true);
                        };
                    }

                    watcher.Error += (_, item) =>
                    {
                        Volatile.Write(ref _lastError, item.GetException().Message);
                        _logger.LogWarning(item.GetException(), "Watcher lost events at {Root}. Scheduling reconciliation.", root);
                        RequestScan();
                    };
                    try
                    {
                        watcher.EnableRaisingEvents = true;
                        _watchers.Add(root, watcher);
                        changed = true;
                    }
                    catch
                    {
                        watcher.Dispose();
                        throw;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Volatile.Write(ref _lastError, exception.Message);
                    _logger.LogWarning(exception, "Could not watch {Root}", root);
                }
            }

            return changed;
        }
    }

    private void Observe(string path, bool structural = false)
    {
        if (AtomicFileWriter.IsTemporaryOrIgnoredPath(path))
        {
            return;
        }

        if (_paths.Count >= MaxPendingPaths)
        {
            RequestScan();
            return;
        }
        _paths.AddOrUpdate(path, structural, (_, prior) => prior || structural);
        _wake.Writer.TryWrite(true);
    }

    private void RequestScan()
    {
        Interlocked.Exchange(ref _scanRequested, 1);
        _wake.Writer.TryWrite(true);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            var wake = _wake.Reader.WaitToReadAsync(ct).AsTask();
            while (!ct.IsCancellationRequested)
            {
                await Task.WhenAny(tick, wake).ConfigureAwait(false);
                var scan = false;
                if (tick.IsCompleted)
                {
                    await tick.ConfigureAwait(false);
                    scan = RefreshRoots();
                    tick = timer.WaitForNextTickAsync(ct).AsTask();
                }
                if (wake.IsCompleted)
                {
                    if (!await wake.ConfigureAwait(false))
                    {
                        return;
                    }
                    await Task.Delay(_debounce, ct).ConfigureAwait(false);
                    while (_wake.Reader.TryRead(out _))
                    {
                    }
                    wake = _wake.Reader.WaitToReadAsync(ct).AsTask();
                }

                var paths = new List<string>();
                var structuralPaths = new List<string>();
                foreach (var path in _paths.Keys)
                {
                    if (_paths.TryRemove(path, out var structural))
                    {
                        paths.Add(path);
                        if (structural)
                        {
                            structuralPaths.Add(path);
                        }
                    }
                }
                scan |= Interlocked.Exchange(ref _scanRequested, 0) != 0;
                try
                {
                    var hasDirectoryChange = structuralPaths.Any(path => _discovery.ResolveObservedPath(path) is null &&
                        (Directory.Exists(path) || !File.Exists(path)));
                    if (scan || hasDirectoryChange)
                    {
                        await _engine.SynchronizeAllAsync(ct).ConfigureAwait(false);
                    }
                    else if (paths.Count > 0)
                    {
                        await _engine.ObserveFileSystemChangesAsync(paths, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    _logger.LogWarning(exception, "Could not reconcile watcher changes.");
                    Interlocked.Exchange(ref _scanRequested, 1);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        lock (_watcherLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stop.Cancel();
            _wake.Writer.TryComplete();
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
        }
        _pump?.GetAwaiter().GetResult();
        _stop.Dispose();
        base.Dispose();
    }
}
