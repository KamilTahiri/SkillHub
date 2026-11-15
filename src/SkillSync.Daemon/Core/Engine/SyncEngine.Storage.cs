using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;
using YamlDotNet.Core;

namespace SkillSync.Daemon.Core.Engine;

public sealed partial class SyncEngine
{
    private void MigrateLedger()
    {
        foreach (var entry in _ledger.Skills.Values)
        {
            if (entry?.Targets is null || entry.CanonicalFiles is null)
            {
                throw new InvalidDataException("The sync ledger contains an invalid skill entry.");
            }
            var targets = new Dictionary<string, TargetFileLedger>(SkillPaths.Comparer);
            foreach (var (key, target) in entry.Targets)
            {
                if (target is null || target.Files is null || target.OwnedFiles is null || target.LastWrittenRawHash is null)
                {
                    throw new InvalidDataException("The sync ledger contains an invalid destination.");
                }
                if (_ledger.Version == 1)
                {
                    if (key == "canonical" || !Path.IsPathFullyQualified(target.Path))
                    {
                        continue;
                    }

                    target.Legacy = true;
                    target.Provider = key == "codex_agents" ? "codex" : key;
                    target.AppliedDescription = key switch
                    {
                        "gemini" => entry.LastSynthesizedGeminiDescription,
                        "codex" or "codex_agents" => entry.LastSynthesizedCodexDescription,
                        "cursor" => entry.LastSynthesizedCursorDescription,
                        _ => null
                    };
                }

                if (Path.IsPathFullyQualified(target.Path))
                {
                    target.Files = new Dictionary<string, string>(target.Files, SkillPaths.Comparer);
                    target.OwnedFiles = new Dictionary<string, string>(target.OwnedFiles, SkillPaths.Comparer);
                    targets[Path.GetFullPath(target.Path)] = target;
                }
            }
            entry.Targets = targets;
            entry.CanonicalFiles = new Dictionary<string, string>(entry.CanonicalFiles, SkillPaths.Comparer);
        }
        _ledger.Skills = new Dictionary<string, SkillLedgerEntry>(_ledger.Skills, StringComparer.OrdinalIgnoreCase);
        _ledger.Version = 2;
    }

    private async Task MigrateLegacyStoreAsync()
    {
        var marker = SkillPaths.Resolve(_discovery.SkillHubRoot, ".legacy-skill-store-migrated");
        if (File.Exists(marker))
        {
            return;
        }

        var legacy = Path.Combine(_discovery.UserHomeRoot, ".skillsync", "skills");
        SkillPaths.AssertNoReparsePoints(legacy);
        if (!Directory.Exists(legacy))
        {
            return;
        }

        var complete = true;
        foreach (var directory in Directory.EnumerateDirectories(legacy, "*", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name == ProviderDiscoveryService.LegacyBootstrapSkillName)
            {
                continue;
            }
            try
            {
                var slug = RequireSlug(name);
                var destination = Path.GetDirectoryName(_storage.CanonicalPath(slug))!;
                if (Directory.Exists(destination) || _ledger.Skills.GetValueOrDefault(slug)?.Deleted == true ||
                    !File.Exists(SkillPaths.Resolve(directory, "SKILL.md")))
                {
                    continue;
                }

                var staging = SkillPaths.Resolve(_discovery.CanonicalSkillsRoot, $".migration-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);
                try
                {
                    foreach (var file in SkillStorage.EnumerateSafeFiles(directory))
                    {
                        SkillPaths.AssertNoReparsePoints(file);
                        var target = SkillPaths.Resolve(staging, Path.GetRelativePath(directory, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target);
                    }
                    Directory.Move(staging, destination);
                }
                finally
                {
                    if (Directory.Exists(staging))
                    {
                        // A failed migration keeps the original legacy files available for retry.
                        PublishActivity(slug, "migration", "error", $"Incomplete migration staged at '{staging}'.");
                    }
                }
            }
            catch (Exception exception) when (IsDiskOrInputFailure(exception))
            {
                complete = false;
                PublishActivity(name, "migration", "error", exception.Message);
            }
        }
        if (complete)
        {
            await AtomicFileWriter.WriteTextAtomicallyAsync(marker, "Migrated legacy canonical skills.\n").ConfigureAwait(false);
        }
    }

    private void LoadCanonicalSkills()
    {
        foreach (var directory in Directory.EnumerateDirectories(_discovery.CanonicalSkillsRoot, "*", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name == ProviderDiscoveryService.LegacyBootstrapSkillName)
            {
                continue;
            }

            try
            {
                var slug = RequireSlug(name);
                if (!string.Equals(name, slug, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Canonical folder names must match their normalized skill slug.");
                }

                var entry = GetEntry(slug);
                if (entry.Deleted)
                {
                    continue;
                }
                var snapshot = SkillStorage.Read(_storage.CanonicalPath(slug));
                if (snapshot.Markdown is not null)
                {
                    var model = ParseCanonical(slug, snapshot, null);
                    PublishSnapshot(model, entry);
                }
            }
            catch (Exception exception) when (IsDiskOrInputFailure(exception))
            {
                PublishActivity(name, "startup", "error", exception.Message);
            }
        }
    }

    private static bool IsDiskOrInputFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or YamlException;

    private CanonicalSkillModel ParseCanonical(string slug, SkillDiskSnapshot snapshot, CanonicalSkillModel? prior)
    {
        var model = _transpilers[ProviderType.Canonical].MergeInbound(slug, snapshot.Markdown!, prior);
        model.SupportingFiles = snapshot.Files;
        if (slug == BootstrapSkillName)
        {
            model.SourceCategory = "system";
            model.OriginProvider = "SkillHub System";
        }
        SkillStorage.ValidateModel(model);
        return model;
    }

    private async Task<CanonicalSkillModel?> LoadCurrentAsync(string slug, SkillLedgerEntry entry)
    {
        if (entry.Deleted)
        {
            return null;
        }

        var path = _storage.CanonicalPath(slug);
        if (entry.PendingCanonical is { } pending)
        {
            await FinishCanonicalWriteAsync(slug, path, entry, pending).ConfigureAwait(false);
        }

        var snapshot = SkillStorage.Read(path);
        if (snapshot.Markdown is null)
        {
            if (entry.CanonicalDiskHash is not null)
            {
                throw new SkillConflictException($"The canonical document for '{slug}' is missing. Restore it before reconciling.");
            }
            return null;
        }

        var hashes = snapshot.FileHashes();
        if (_managed.TryGetValue(slug, out var cached) && snapshot.Hash == entry.CanonicalDiskHash &&
            SameFiles(hashes, entry.CanonicalFiles))
        {
            return cached.Clone();
        }

        var model = ParseCanonical(slug, snapshot, cached);
        var nextSemanticHash = ContentHasher.ComputeSemanticHash(model);
        if (entry.Revision == 0 || nextSemanticHash != entry.CanonicalSemanticHash)
        {
            entry.Revision++;
        }
        entry.CanonicalDiskHash = snapshot.Hash;
        entry.CanonicalFiles = hashes;
        entry.CanonicalSemanticHash = nextSemanticHash;
        await PersistAsync().ConfigureAwait(false);
        PublishSnapshot(model, entry);
        return model;
    }

    private async Task<CanonicalSkillModel> SaveAndProjectAsync(string slug, CanonicalSkillModel model, string reason)
    {
        SkillStorage.ValidateModel(model);
        var entry = GetEntry(slug);
        if (entry.Deleted)
        {
            throw new SkillConflictException($"'{slug}' is in SkillHub trash. Restore it before using that name.");
        }

        await CommitCanonicalAsync(slug, model, entry, reason).ConfigureAwait(false);
        await ReconcileSkillAsync(slug, [], reason, allowInbound: false).ConfigureAwait(false);
        return _managed[slug].Clone();
    }

    private async Task CommitCanonicalAsync(string slug, CanonicalSkillModel model, SkillLedgerEntry entry, string reason)
    {
        var path = _storage.CanonicalPath(slug);
        var directory = Path.GetDirectoryName(path)!;
        model.Name = slug;
        model.Title = model.GetEffectiveTitle();
        model.IsCatalogOnly = false;
        model.SourcePath = path.Replace('\\', '/');
        SkillStorage.PreflightBundle(directory, model.SupportingFiles.Keys);
        var markdown = _transpilers[ProviderType.Canonical].TranspileOutbound(model, directory);
        if (System.Text.Encoding.UTF8.GetByteCount(markdown) > SkillStorage.MaxMarkdownBytes)
        {
            throw new ArgumentException("The complete skill document exceeds the supported size.");
        }
        var expected = ContentHasher.ComputeFileRevisionHash(markdown);
        var expectedFiles = new SkillDiskSnapshot(markdown, model.SupportingFiles).FileHashes();
        if (entry.CanonicalDiskHash == expected && SameFiles(entry.CanonicalFiles, expectedFiles) && entry.CatalogId == model.CatalogId)
        {
            return;
        }

        var pending = new PendingCanonicalWrite(entry.CanonicalDiskHash, expected, entry.Revision + 1, model.Clone(),
            new Dictionary<string, string>(entry.CanonicalFiles, SkillPaths.Comparer));
        entry.PendingCanonical = pending;
        entry.LastUpdatedUtc = DateTimeOffset.UtcNow;
        entry.LastUpdatedBy = reason;
        await PersistAsync().ConfigureAwait(false);
        await FinishCanonicalWriteAsync(slug, path, entry, pending).ConfigureAwait(false);
    }

    private async Task FinishCanonicalWriteAsync(string slug, string path, SkillLedgerEntry entry, PendingCanonicalWrite pending)
    {
        var directory = Path.GetDirectoryName(path)!;
        var model = pending.Model;
        SkillStorage.ValidateModel(model);
        var markdown = _transpilers[ProviderType.Canonical].TranspileOutbound(model, directory);
        var observed = SkillStorage.Read(path);
        var desiredHashes = new SkillDiskSnapshot(markdown, model.SupportingFiles).FileHashes();
        if (ContentHasher.ComputeFileRevisionHash(markdown) != pending.ExpectedHash ||
            observed.Hash != pending.PreviousHash && observed.Hash != pending.ExpectedHash ||
            !CanApplyFiles(directory, observed.FileHashes(), pending.PreviousFiles, desiredHashes))
        {
            throw new SkillConflictException($"A pending write for '{slug}' conflicts with newer files. The pending model and disk files have been preserved.");
        }

        await SkillStorage.ApplyFilesAsync(directory, model.SupportingFiles, pending.PreviousFiles).ConfigureAwait(false);
        await SkillStorage.WriteIfUnchangedAsync(path, markdown, observed.Hash).ConfigureAwait(false);
        entry.PendingCanonical = null;
        entry.Revision = pending.Revision;
        entry.CatalogId = model.CatalogId;
        entry.CanonicalDiskHash = pending.ExpectedHash;
        entry.CanonicalFiles = desiredHashes;
        entry.CanonicalSemanticHash = ContentHasher.ComputeSemanticHash(model);
        try
        {
            await PersistAsync().ConfigureAwait(false);
        }
        catch
        {
            entry.PendingCanonical = pending;
            throw;
        }
        _guards[(slug, path)] = (ProviderType.Canonical, ContentHasher.ComputeRawFileHash(markdown));
        try
        {
            _history.Record(slug, entry.Revision, markdown, model.SupportingFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a failure to record it never blocks a successful write.
            PublishActivity(slug, "history", "error", $"Could not save revision history: {exception.Message}");
        }
        PublishSnapshot(model, entry);
    }

    private void PublishSnapshot(CanonicalSkillModel model, SkillLedgerEntry entry)
    {
        var copy = model.Clone();
        copy.Revision = entry.Revision;
        copy.CatalogId = entry.CatalogId;
        copy.SourcePath = _storage.CanonicalPath(model.Name).Replace('\\', '/');
        copy.Destinations = entry.Targets.Values.Where(target => !string.IsNullOrEmpty(target.Provider))
            .OrderBy(target => target.Provider).ThenBy(target => target.Path, SkillPaths.Comparer)
            .Select(target => new SyncDestinationStatus(target.Provider, target.Path, target.State, target.AppliedRevision, target.Error))
            .ToArray();
        var previous = _managed.TryGetValue(copy.Name, out var before) ? before.Revision : -1;
        _managed[copy.Name] = copy;
        if (previous != copy.Revision) RaiseSkillChanged(copy.Name);
    }

    private static bool SameFiles(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var hash) && hash == pair.Value);

    private static bool CanApplyFiles(string directory, IReadOnlyDictionary<string, string> observed,
        IReadOnlyDictionary<string, string> previous, IReadOnlyDictionary<string, string> desired)
    {
        foreach (var relative in previous.Keys.Union(desired.Keys, SkillPaths.Comparer))
        {
            var path = SkillPaths.Resolve(directory, SkillPaths.SupportingPath(relative));
            var current = observed.GetValueOrDefault(relative);
            var oldHash = previous.GetValueOrDefault(relative);
            var newHash = desired.GetValueOrDefault(relative);
            if ((current is null && File.Exists(path)) || current != oldHash && current != newHash)
            {
                return false;
            }
        }
        return true;
    }

    private async Task FinishDeleteAsync(string slug, SkillLedgerEntry entry)
    {
        var source = Path.GetDirectoryName(_storage.CanonicalPath(slug))!;
        var trash = SkillPaths.Resolve(_discovery.TrashRoot, entry.Tombstone!);
        if (Directory.Exists(source) && !Directory.Exists(trash))
        {
            var observed = SkillStorage.Read(_storage.CanonicalPath(slug));
            if (observed.Hash != entry.CanonicalDiskHash || !SameFiles(observed.FileHashes(), entry.CanonicalFiles))
            {
                entry.Deleted = false;
                entry.Tombstone = null;
                await PersistAsync().ConfigureAwait(false);
                throw new SkillConflictException($"'{slug}' changed before it could be moved to trash.");
            }
            Directory.CreateDirectory(_discovery.TrashRoot);
            Directory.Move(source, trash);
        }
        _managed.TryRemove(slug, out _);
        foreach (var (path, target) in entry.Targets.ToArray())
        {
            if (!target.Owned || target.State == "withdrawn")
            {
                continue;
            }
            try
            {
                var destination = ResolveLedgerDestination(slug, path);
                if (destination is not null)
                {
                    var observedTarget = ReadDestination(destination);
                    await WithdrawAsync(slug, destination, target, observedTarget).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsDiskOrInputFailure(exception))
            {
                await MarkFailureAsync(slug, target, exception).ConfigureAwait(false);
            }
        }
    }
}
