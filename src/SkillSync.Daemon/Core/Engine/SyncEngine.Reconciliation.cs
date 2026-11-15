using System.Collections.Concurrent;
using System.Text.Json;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Core.Transpilers;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed partial class SyncEngine
{
    private sealed record Destination(ProviderType Provider, string Path)
    {
        public bool HasBundle => System.IO.Path.GetFileName(Path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);
        public string Directory => System.IO.Path.GetDirectoryName(Path)!;
    }

    private sealed record Observation(Destination Destination, TargetFileLedger Target, SkillDiskSnapshot Snapshot);
    private readonly ConcurrentDictionary<(string Slug, string Path), (ProviderType Provider, string Hash)> _guards = new();
    private long _projectionCount;

    public bool IsLoopGuardMatch(ProviderType provider, string skillSlug, string normalizedRawHash) =>
        _guards.Any(pair => pair.Key.Slug == skillSlug && pair.Value == (provider, normalizedRawHash));

    internal bool IsExpectedWithdrawal(string path) => _expectedWithdrawals.ContainsKey(Path.GetFullPath(path));

    public void EnqueueFileSystemEvent(ProviderType provider, string skillSlug, SyncChangeKind kind, string fullPath)
    {
        _ = ObserveFileSystemChangesAsync([fullPath]);
    }

    internal Task<bool> ObserveFileSystemChangesAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var bySkill = new Dictionary<string, List<ObservedSkillPath>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                try
                {
                    var location = _discovery.ResolveObservedPath(path);
                    if (location is null)
                    {
                        continue;
                    }
                    if (File.Exists(path))
                    {
                        _expectedWithdrawals.TryRemove(path, out _);
                    }
                    else if (IsExpectedWithdrawal(path))
                    {
                        continue;
                    }
                    if (!bySkill.TryGetValue(location.Slug, out var locations))
                    {
                        bySkill[location.Slug] = locations = [];
                    }
                    locations.Add(location);
                }
                catch (Exception exception) when (IsDiskOrInputFailure(exception))
                {
                    PublishActivity(string.Empty, "watcher", "error", exception.Message);
                }
            }

            foreach (var (slug, locations) in bySkill)
            {
                await ReconcileSafelyAsync(slug, locations, "watcher").ConfigureAwait(false);
            }
            return true;
        }, ct);

    public Task<SyncReconciliationReport> SynchronizeAllAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        var previousProjectionCount = _projectionCount;
        await MigrateLegacyStoreAsync().ConfigureAwait(false);
        var bySkill = DiscoverSkillPaths();
        foreach (var slug in bySkill.Keys.Concat(_ledger.Skills.Keys).Concat(_managed.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileSafelyAsync(slug, bySkill.GetValueOrDefault(slug) ?? [], "resync").ConfigureAwait(false);
        }
        _catalog.Refresh();
        var installed = InstalledProviderNames();
        var destinations = _managed.Values.SelectMany(skill => skill.Destinations)
            .Where(target => installed.Contains(target.Provider)).ToArray();
        var changed = checked((int)(_projectionCount - previousProjectionCount));
        return new SyncReconciliationReport(Guid.NewGuid(), _managed.Count, changed,
            destinations.Count(target => target.State == "synced"), destinations.Count(target => target.State == "conflict"),
            destinations.Count(target => target.State == "error"), destinations.Count(target => target.State == "missing"));
    }, ct);

    public Task<CanonicalSkillModel> RepairProviderAsync(string name, ProviderType provider, CancellationToken ct = default)
    {
        var slug = RequireSlug(name);
        if (!Enum.IsDefined(provider) || provider == ProviderType.Canonical)
        {
            throw new ArgumentException("An external provider is required.");
        }
        return RunAsync(async () =>
        {
            var entry = GetEntry(slug);
            var current = await LoadCurrentAsync(slug, entry).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Managed skill '{slug}' was not found.");
            if (!current.IsEnabledFor(provider))
            {
                throw new SkillConflictException("Enable the destination before repairing it.");
            }
            foreach (var destination in GetDestinations(slug, entry, []).Where(item => item.Provider == provider))
            {
                if (entry.Targets.TryGetValue(destination.Path, out var target) && target.Owned && target.State == "missing")
                {
                    var snapshot = ReadDestination(destination);
                    if (snapshot.Markdown is not null)
                    {
                        throw new SkillConflictException("A file appeared at the missing destination. Reconcile it before repair.");
                    }
                    await ProjectAsync(slug, current, entry, new Observation(destination, target, snapshot), allowMissingRepair: true)
                        .ConfigureAwait(false);
                }
            }
            PublishSnapshot(current, entry);
            return _managed[slug].Clone();
        }, ct);
    }

    private Dictionary<string, List<ObservedSkillPath>> DiscoverSkillPaths()
    {
        var bySkill = new Dictionary<string, List<ObservedSkillPath>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, root) in _discovery.GetObservableRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in SkillStorage.EnumerateSafeFiles(root).Where(file =>
                    Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(file).Equals(".mdc", StringComparison.OrdinalIgnoreCase)))
                {
                    var location = _discovery.ResolveObservedPath(file);
                    if (location is null || location.SupportingPath is not null)
                    {
                        continue;
                    }
                    if (!bySkill.TryGetValue(location.Slug, out var paths))
                    {
                        bySkill[location.Slug] = paths = [];
                    }
                    paths.Add(location);
                }
            }
            catch (Exception exception) when (IsDiskOrInputFailure(exception))
            {
                PublishActivity(string.Empty, "discovery", "error", exception.Message);
            }
        }
        return bySkill;
    }

    private async Task ReconcileSafelyAsync(string slug, IReadOnlyList<ObservedSkillPath> observed, string source)
    {
        try
        {
            await ReconcileSkillAsync(slug, observed, source).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDiskOrInputFailure(exception))
        {
            PublishActivity(slug, source, "error", exception.Message);
        }
    }

    private IReadOnlyList<Destination> GetDestinations(string slug, SkillLedgerEntry entry, IReadOnlyList<ObservedSkillPath> hints)
    {
        var destinations = new Dictionary<string, Destination>(SkillPaths.Comparer);
        void Add(ProviderType provider, string path)
        {
            var absolutePath = Path.GetFullPath(path);
            destinations.TryAdd(absolutePath, new Destination(provider, absolutePath));
        }

        var installed = _discovery.DiscoverProviders()
            .Where(info => info.Provider != ProviderType.Canonical && info.IsInstalled)
            .ToDictionary(info => info.Provider);
        foreach (var info in installed.Values)
        {
            if (info.Provider == ProviderType.Codex)
            {
                foreach (var directory in _discovery.GetAllCodexSkillDirectories(slug))
                {
                    Add(info.Provider, Path.Combine(directory, "SKILL.md"));
                }
            }
            else
            {
                Add(info.Provider, _discovery.GetSkillPrimaryFilePath(info.Provider, slug));
            }
            if (info.Provider == ProviderType.Gemini)
            {
                foreach (var mirror in _discovery.GetAntigravityMirrorSkillDirectories(slug))
                {
                    Add(info.Provider, Path.Combine(mirror, "SKILL.md"));
                }
            }
        }

        foreach (var hint in hints.Where(hint => installed.ContainsKey(hint.Provider)))
        {
            Add(hint.Provider, hint.PrimaryPath);
        }

        foreach (var path in entry.Targets.Keys)
        {
            var destination = ResolveLedgerDestination(slug, path);
            if (destination is not null && installed.ContainsKey(destination.Provider))
            {
                Add(destination.Provider, path);
            }
        }

        return destinations.Values.ToArray();
    }

    private Destination? ResolveLedgerDestination(string slug, string path)
    {
        var observed = _discovery.ResolveObservedPath(path);
        return observed is not null && observed.Provider != ProviderType.Canonical && observed.SupportingPath is null &&
               observed.Slug == slug && SkillPaths.Comparer.Equals(observed.PrimaryPath, path)
            ? new Destination(observed.Provider, path) : null;
    }

    private static SkillDiskSnapshot ReadDestination(Destination destination) =>
        SkillStorage.Read(destination.Path, destination.HasBundle);

    private async Task ReconcileSkillAsync(string slug, IReadOnlyList<ObservedSkillPath> hints, string source, bool allowInbound = true)
    {
        RequireSlug(slug);
        var entry = GetEntry(slug);
        if (entry.Deleted)
        {
            await FinishDeleteAsync(slug, entry).ConfigureAwait(false);
            return;
        }

        var model = await LoadCurrentAsync(slug, entry).ConfigureAwait(false);
        var observations = new List<Observation>();
        foreach (var destination in GetDestinations(slug, entry, hints))
        {
            if (!entry.Targets.TryGetValue(destination.Path, out var target))
            {
                target = new TargetFileLedger { Path = destination.Path, Provider = ProviderName(destination.Provider) };
                entry.Targets.Add(destination.Path, target);
            }
            try
            {
                var observed = ReadDestination(destination);
                if (target.Legacy)
                {
                    await MigrateTargetAsync(target, observed, entry).ConfigureAwait(false);
                }
                if (target.Pending is not null && model is not null)
                {
                    await RecoverProjectionAsync(slug, model, entry, destination, target, observed).ConfigureAwait(false);
                    observed = ReadDestination(destination);
                }
                observations.Add(new Observation(destination, target, observed));
            }
            catch (Exception exception) when (IsDiskOrInputFailure(exception))
            {
                await MarkFailureAsync(slug, target, exception).ConfigureAwait(false);
            }
        }

        if (model is null)
        {
            var native = observations.FirstOrDefault(item => item.Snapshot.Markdown is not null);
            if (native is null || slug == BootstrapSkillName)
            {
                return;
            }

            model = ReadInbound(slug, native, null);
            model.OriginProvider = native.Destination.Provider.ToString();
            await CommitCanonicalAsync(slug, model, entry, "discovery").ConfigureAwait(false);
            await RecordAppliedAsync(slug, entry, model, native, owned: false).ConfigureAwait(false);
            foreach (var other in observations.Where(item => item != native && item.Snapshot.Markdown is not null))
            {
                await SaveConflictAsync(slug, other, "Multiple provider files use this skill name.").ConfigureAwait(false);
            }
        }
        else if (slug != BootstrapSkillName)
        {
            var changed = observations.Where(item => item.Target.AcknowledgedHash is null && HasExternalChange(item)).ToList();
            if (changed.Count == 1 && allowInbound && model.IsEnabledFor(changed[0].Destination.Provider) &&
                changed[0].Target.AppliedRevision == entry.Revision && changed[0].Target.LastWrittenRawHash.Length > 0)
            {
                var inbound = changed[0];
                try
                {
                    var merged = ReadInbound(slug, inbound, model);
                    await CommitCanonicalAsync(slug, merged, entry, source).ConfigureAwait(false);
                    await RecordAppliedAsync(slug, entry, merged, inbound, inbound.Target.Owned).ConfigureAwait(false);
                    model = merged;
                    PublishActivity(slug, source, "ingested", $"Imported changes from '{inbound.Destination.Path}'.");
                    changed.Clear();
                }
                catch (Exception exception) when (IsDiskOrInputFailure(exception))
                {
                    await SaveConflictAsync(slug, inbound, $"The provider edit could not be applied. {exception.Message}").ConfigureAwait(false);
                }
            }
            foreach (var item in changed)
            {
                await SaveConflictAsync(slug, item, "Provider and canonical changes require review.").ConfigureAwait(false);
            }
        }

        foreach (var item in observations)
        {
            try
            {
                if (!model.IsEnabledFor(item.Destination.Provider))
                {
                    await WithdrawAsync(slug, item.Destination, item.Target, item.Snapshot).ConfigureAwait(false);
                }
                else
                {
                    await ProjectAsync(slug, model, entry, item).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsDiskOrInputFailure(exception))
            {
                await MarkFailureAsync(slug, item.Target, exception).ConfigureAwait(false);
            }
        }
        PublishSnapshot(model, entry);
    }

    private CanonicalSkillModel ReadInbound(string slug, Observation item, CanonicalSkillModel? prior)
    {
        var target = item.Target;
        var baseline = new SkillLedgerEntry
        {
            LastSynthesizedCodexDescription = target.AppliedDescription,
            LastSynthesizedGeminiDescription = target.AppliedDescription,
            LastSynthesizedCursorDescription = target.AppliedDescription
        };
        var model = item.Snapshot.Hash == target.LastWrittenRawHash && prior is not null
            ? prior.Clone()
            : _transpilers[item.Destination.Provider].MergeInbound(slug, item.Snapshot.Markdown!, prior, baseline,
                Path.GetDirectoryName(_storage.CanonicalPath(slug)));
        if (item.Destination.HasBundle)
        {
            var files = prior?.SupportingFiles ?? new Dictionary<string, string>(SkillPaths.Comparer);
            model.SupportingFiles = new Dictionary<string, string>(files, SkillPaths.Comparer);
            foreach (var (relative, hash) in target.Files)
            {
                var path = SkillPaths.Resolve(item.Destination.Directory, SkillPaths.SupportingPath(relative));
                if (!item.Snapshot.Files.ContainsKey(relative) && !File.Exists(path))
                {
                    model.SupportingFiles.Remove(relative);
                }
            }
            foreach (var (relative, content) in item.Snapshot.Files)
            {
                if (!target.Files.TryGetValue(relative, out var hash) || ContentHasher.ComputeFileRevisionHash(content) != hash)
                {
                    model.SupportingFiles[relative] = content;
                }
            }
        }
        return model;
    }

    private static bool HasExternalChange(Observation item) => item.Snapshot.Markdown is not null &&
        item.Target.LastWrittenRawHash.Length > 0 && item.Target.State != "withdrawn" &&
        (item.Snapshot.Hash != item.Target.LastWrittenRawHash || !SameFiles(item.Snapshot.FileHashes(), item.Target.Files));

    private async Task MigrateTargetAsync(TargetFileLedger target, SkillDiskSnapshot observed, SkillLedgerEntry entry)
    {
        target.Legacy = false;
        if (observed.Markdown is not null &&
            ContentHasher.ComputeRawFileHash(observed.Markdown) == target.LastWrittenRawHash)
        {
            target.LastWrittenRawHash = observed.Hash!;
            target.Owned = true;
            target.AppliedRevision = entry.Revision;
            // A v1 ledger established primary ownership only.
            target.Files = observed.FileHashes();
        }
        await PersistAsync().ConfigureAwait(false);
    }

    private async Task ProjectAsync(string slug, CanonicalSkillModel model, SkillLedgerEntry entry, Observation item,
        bool allowMissingRepair = false)
    {
        var (destination, target, observed) = item;
        if (target.AcknowledgedHash is not null && target.AcknowledgedHash == SnapshotHash(observed))
        {
            await SetStateAsync(target, "external", null).ConfigureAwait(false);
            return;
        }
        var intended = IntendedProjection(model, destination);
        if (await TryRecognizeLegacyMirrorAsync(model, entry, item, intended).ConfigureAwait(false)) return;
        if (target.State == "conflict" && target.ConflictHash == SnapshotHash(observed)) return;
        if (observed.Markdown is null && target.LastWrittenRawHash.Length != 0 && target.State != "withdrawn" && !allowMissingRepair)
        {
            await SetStateAsync(target, "missing", "The provider copy was removed outside SkillHub.").ConfigureAwait(false);
            return;
        }

        var expected = intended.Hash!;
        var desired = intended.FileHashes();
        var observedFiles = observed.FileHashes();
        if (observed.Markdown is not null && !target.Owned)
        {
            if (target.AppliedRevision == entry.Revision && target.LastWrittenRawHash == observed.Hash && SameFiles(observed.FileHashes(), target.Files))
            {
                await SetStateAsync(target, "external", null).ConfigureAwait(false);
                return;
            }

            await SaveConflictAsync(slug, item, "The destination already contains an unowned skill.").ConfigureAwait(false);
            return;
        }

        if (observed.Hash == expected && desired.All(pair => observedFiles.GetValueOrDefault(pair.Key) == pair.Value) &&
            target.Owned && target.OwnedFiles.Keys.All(desired.ContainsKey))
        {
            if (target.AppliedRevision != entry.Revision || target.State != "synced" || target.Pending is not null)
            {
                await RecordAppliedAsync(slug, entry, model, item, owned: true).ConfigureAwait(false);
            }
            return;
        }
        if (target.AcknowledgedHash is not null || HasExternalChange(item) || !CanApplyFiles(destination.Directory, observedFiles, target.OwnedFiles, desired))
        {
            await SaveConflictAsync(slug, item, "The destination has edits or unowned files.").ConfigureAwait(false);
            return;
        }

        var ownership = desired.Where(pair => target.OwnedFiles.ContainsKey(pair.Key) || !observedFiles.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, SkillPaths.Comparer);
        target.Pending = new PendingProjection(expected, desired, Withdraw: false, ownership, observed.Hash);
        target.Owned = true;
        await PersistAsync().ConfigureAwait(false);
        await ApplyProjectionAsync(slug, model, entry, destination, target, intended).ConfigureAwait(false);
    }

    private async Task ApplyProjectionAsync(string slug, CanonicalSkillModel model, SkillLedgerEntry entry,
        Destination destination, TargetFileLedger target, SkillDiskSnapshot intended)
    {
        var ownership = target.Pending!.OwnedFiles ?? intended.FileHashes();
        if (destination.HasBundle)
        {
            await SkillStorage.ApplyFilesAsync(destination.Directory, intended.Files, target.OwnedFiles,
                path => RegisterWithdrawal(slug, path)).ConfigureAwait(false);
        }
        _guards[(slug, destination.Path)] = (destination.Provider, ContentHasher.ComputeRawFileHash(intended.Markdown!));
        await SkillStorage.WriteIfUnchangedAsync(destination.Path, intended.Markdown!, target.Pending!.PreviousRawHash).ConfigureAwait(false);
        _expectedWithdrawals.TryRemove(destination.Path, out _);
        target.OwnedFiles = ownership;
        var written = ReadDestination(destination);
        var writtenFiles = written.FileHashes();
        if (written.Hash != intended.Hash || intended.FileHashes().Any(pair => writtenFiles.GetValueOrDefault(pair.Key) != pair.Value))
        {
            throw new SkillConflictException($"'{destination.Path}' changed during projection.");
        }
        await RecordAppliedAsync(slug, entry, model, new Observation(destination, target, written), owned: true).ConfigureAwait(false);
        _projectionCount++;
        PublishActivity(slug, target.Provider, "synced", $"Applied revision {entry.Revision} to '{destination.Path}'.");
    }

    private async Task RecoverProjectionAsync(string slug, CanonicalSkillModel model, SkillLedgerEntry entry,
        Destination destination, TargetFileLedger target, SkillDiskSnapshot observed)
    {
        var pending = target.Pending!;
        if (pending.RestoreSnapshotId is { } snapshotId)
        {
            await RestoreReviewedDestinationAsync(slug, model, entry, destination, target, observed, snapshotId).ConfigureAwait(false);
            return;
        }
        if (pending.Withdraw)
        {
            await WithdrawAsync(slug, destination, target, observed).ConfigureAwait(false);
            return;
        }

        var markdown = _transpilers[destination.Provider].TranspileOutbound(model, Path.GetDirectoryName(_storage.CanonicalPath(slug)));
        var intended = new SkillDiskSnapshot(markdown, destination.HasBundle ? model.SupportingFiles : new(SkillPaths.Comparer));
        if (pending.RawHash == intended.Hash && SameFiles(pending.Files, intended.FileHashes()) &&
            (observed.Hash == pending.RawHash || observed.Hash == pending.PreviousRawHash) &&
            CanApplyFiles(destination.Directory, observed.FileHashes(), target.OwnedFiles, pending.Files))
        {
            await ApplyProjectionAsync(slug, model, entry, destination, target, intended).ConfigureAwait(false);
            return;
        }
        target.Pending = null;
        await SaveConflictAsync(slug, new Observation(destination, target, observed),
            "A projection was interrupted and newer changes require review.").ConfigureAwait(false);
    }

    private async Task WithdrawAsync(string slug, Destination destination, TargetFileLedger target, SkillDiskSnapshot observed)
    {
        if (!target.Owned)
        {
            await SetStateAsync(target, "disabled", null).ConfigureAwait(false);
            return;
        }
        if (target.State == "withdrawn")
        {
            return;
        }

        if ((observed.Hash is not null && observed.Hash != target.LastWrittenRawHash) ||
            !CanApplyFiles(destination.Directory, observed.FileHashes(), target.OwnedFiles, new Dictionary<string, string>()))
        {
            await SaveConflictAsync(slug, new Observation(destination, target, observed),
                "An edited provider file was kept during withdrawal.").ConfigureAwait(false);
            return;
        }
        if (target.Pending?.Withdraw != true)
        {
            target.Pending = new PendingProjection(null, new(SkillPaths.Comparer), Withdraw: true);
            await PersistAsync().ConfigureAwait(false);
        }

        if (destination.HasBundle)
        {
            await SkillStorage.ApplyFilesAsync(destination.Directory, new(SkillPaths.Comparer), target.OwnedFiles,
                path => RegisterWithdrawal(slug, path)).ConfigureAwait(false);
        }
        SkillStorage.DeleteOwnedFile(destination.Path, target.LastWrittenRawHash, path => RegisterWithdrawal(slug, path));
        target.Pending = null;
        target.LastWrittenRawHash = string.Empty;
        target.Files.Clear();
        target.OwnedFiles.Clear();
        target.AppliedRevision = 0;
        target.State = "withdrawn";
        target.Error = null;
        target.ConflictHash = null;
        target.AcknowledgedHash = null;
        _guards.TryRemove((slug, destination.Path), out _);
        await PersistAsync().ConfigureAwait(false);
        PublishActivity(slug, target.Provider, "withdrawn", $"Withdrew the owned copy at '{destination.Path}'.");
    }

    private void RegisterWithdrawal(string slug, string path)
    {
        _expectedWithdrawals[Path.GetFullPath(path)] = slug;
        if (_expectedWithdrawals.Count > 4096)
        {
            foreach (var stale in _expectedWithdrawals.Keys.Take(1024))
            {
                _expectedWithdrawals.TryRemove(stale, out _);
            }
        }
    }

    private async Task RecordAppliedAsync(string slug, SkillLedgerEntry entry, CanonicalSkillModel model, Observation item, bool owned,
        string? acknowledgedHash = null)
    {
        var (destination, target, snapshot) = item;
        target.LastWrittenRawHash = snapshot.Hash!;
        target.Files = snapshot.FileHashes();
        target.OwnedFiles = target.OwnedFiles.Keys.Where(target.Files.ContainsKey)
            .ToDictionary(relative => relative, relative => target.Files[relative], SkillPaths.Comparer);
        target.LastWriteTimestampUtc = DateTimeOffset.UtcNow;
        target.AppliedRevision = entry.Revision;
        target.Owned = owned;
        target.Pending = null;
        target.State = owned ? "synced" : "external";
        target.Error = null;
        target.ConflictHash = null;
        target.AcknowledgedHash = acknowledgedHash;
        target.AppliedDescription = destination.Provider switch
        {
            ProviderType.Codex => CodexTranspiler.BuildConciseDescription(model),
            ProviderType.Gemini => GeminiTranspiler.BuildSingleLineDescription(model.Description, model.WhenToUse),
            ProviderType.Cursor => CursorMdcTranspiler.BuildCursorDescription(model),
            _ => null
        };
        await PersistAsync().ConfigureAwait(false);
        if (owned)
        {
            _guards[(slug, destination.Path)] = (destination.Provider, ContentHasher.ComputeRawFileHash(snapshot.Markdown!));
        }
    }

    private async Task<string?> SaveConflictAsync(string slug, Observation item, string details, bool forceSnapshot = false)
    {
        var hash = SnapshotHash(item.Snapshot);
        if (forceSnapshot || item.Target.ConflictHash != hash)
        {
            var relative = $"{slug}/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
            var path = SkillPaths.Resolve(_discovery.ConflictsRoot, relative);
            await AtomicFileWriter.WriteTextAtomicallyAsync(path, JsonSerializer.Serialize(new
            {
                destination = item.Destination.Path,
                markdown = item.Snapshot.Markdown,
                supporting_files = item.Snapshot.Files,
                reason = details,
                saved_at = DateTimeOffset.UtcNow
            })).ConfigureAwait(false);
            item.Target.ConflictHash = hash;
            item.Target.State = "conflict";
            item.Target.Error = $"{details} Snapshot saved at '{path}'.";
            await PersistAsync().ConfigureAwait(false);
            PublishActivity(slug, item.Target.Provider, "conflict", item.Target.Error);
            return relative;
        }
        return null;
    }

    private static string SnapshotHash(SkillDiskSnapshot snapshot) =>
        ContentHasher.ComputeFileRevisionHash(JsonSerializer.Serialize(new
        {
            snapshot.Hash,
            files = snapshot.FileHashes().OrderBy(pair => pair.Key, SkillPaths.Comparer)
        }));

    private async Task MarkFailureAsync(string slug, TargetFileLedger target, Exception exception)
    {
        await SetStateAsync(target, exception is SkillConflictException ? "conflict" : "error", exception.Message).ConfigureAwait(false);
        PublishActivity(slug, target.Provider, "error", exception.Message);
    }

    private async Task SetStateAsync(TargetFileLedger target, string state, string? error)
    {
        if (target.State != state || target.Error != error)
        {
            target.State = state;
            target.Error = error;
            await PersistAsync().ConfigureAwait(false);
        }
    }
}
