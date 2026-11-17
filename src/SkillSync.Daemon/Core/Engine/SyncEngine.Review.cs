using System.Security.Cryptography;
using System.Text;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Engine;

public sealed record DestinationIssue(string Skill, string Title, string Provider, string Label, string Path,
    string State, string? Detail);
public sealed record ReviewFile(string Path, string Change, string? SkillHub, string? Provider);
public sealed record DestinationReview(DestinationIssue Issue, string Version, string Reason, IReadOnlyList<ReviewFile> Files,
    IReadOnlyList<string> Actions);

public sealed partial class SyncEngine
{
    private sealed record ReviewState(CanonicalSkillModel Model, SkillLedgerEntry Entry, Observation Observation,
        SkillDiskSnapshot Intended, Dictionary<string, string> RawFiles, DestinationReview Review);

    private static string DestinationLabel(string provider, string path) =>
        provider == "gemini" && path.Replace('\\', '/').Contains("/.gemini/config/skills/", StringComparison.OrdinalIgnoreCase)
            ? "Antigravity IDE"
            : provider == "gemini" && path.Split('/', '\\').Contains("antigravity", StringComparer.OrdinalIgnoreCase)
            ? "Antigravity" : provider switch { "gemini" => "Gemini", "codex" => "Codex", "claude" => "Claude", _ => "Cursor" };

    private static DestinationIssue Issue(CanonicalSkillModel model, TargetFileLedger target) =>
        new(model.Name, model.GetEffectiveTitle(), target.Provider, DestinationLabel(target.Provider, target.Path),
            target.Path, target.State, target.Error);

    private SkillDiskSnapshot IntendedProjection(CanonicalSkillModel model, Destination destination) =>
        new(Outbound(destination.Provider, model, Path.GetDirectoryName(_storage.CanonicalPath(model.Name))),
            destination.HasBundle ? model.SupportingFiles : new Dictionary<string, string>(SkillPaths.Comparer));

    private static string HashBytes(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ContentHasher.NormalizeLineEndings(text))));

    private static Dictionary<string, string> ReadRawManifest(Destination destination, SkillDiskSnapshot snapshot)
    {
        var result = new Dictionary<string, string>(SkillPaths.Comparer);
        void Add(string relative, string path, string normalized)
        {
            SkillPaths.AssertNoReparsePoints(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > SkillStorage.MaxMarkdownBytes)
                throw new InvalidDataException("A reviewed file grew beyond the supported limit.");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            if (ContentHasher.NormalizeLineEndings(Encoding.UTF8.GetString(bytes)) != normalized)
                throw new SkillConflictException("The provider file changed while it was being reviewed.");
            result.Add(relative, Convert.ToHexString(SHA256.HashData(bytes)));
        }

        if (snapshot.Markdown is not null) Add("SKILL.md", destination.Path, snapshot.Markdown);
        foreach (var (path, content) in snapshot.Files)
            Add(path, SkillPaths.Resolve(destination.Directory, path), content);
        return result;
    }

    private static string RawManifestHash(Dictionary<string, string> manifest) =>
        ContentHasher.ComputeFileRevisionHash(System.Text.Json.JsonSerializer.Serialize(manifest.OrderBy(file => file.Key, SkillPaths.Comparer)));

    private static bool HasUnreviewedFiles(Destination destination, IReadOnlyDictionary<string, string> raw)
    {
        if (!destination.HasBundle) return false;
        foreach (var group in SkillStorage.EnumerateBundleFileGroups(destination.Directory))
        {
            if (group.Any(file => !AtomicFileWriter.IsTemporaryOrIgnoredPath(file) &&
                !raw.ContainsKey(Path.GetRelativePath(destination.Directory, file).Replace('\\', '/'))))
                return true;
        }
        return false;
    }

    private static bool IsByteIdentical(SkillDiskSnapshot intended, IReadOnlyDictionary<string, string> observed) =>
        observed.Count == intended.Files.Count + 1 &&
        observed.GetValueOrDefault("SKILL.md") == HashBytes(intended.Markdown!) &&
        intended.Files.All(file => observed.GetValueOrDefault(file.Key) == HashBytes(file.Value));

    private async Task<bool> TryRecognizeLegacyMirrorAsync(CanonicalSkillModel model, SkillLedgerEntry entry,
        Observation observation, SkillDiskSnapshot intended)
    {
        if (observation.Destination.Provider != ProviderType.Gemini ||
            DestinationLabel("gemini", observation.Destination.Path) != "Antigravity" ||
            observation.Target.Owned || observation.Snapshot.Markdown is null) return false;
        var primary = _discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, model.Name);
        if (!entry.Targets.TryGetValue(primary, out var sibling) || !sibling.Owned ||
            sibling.LastWrittenRawHash.Length == 0) return false;

        var bytes = ReadRawManifest(observation.Destination, observation.Snapshot);
        if (HasUnreviewedFiles(observation.Destination, bytes)) return false;
        if (!IsByteIdentical(intended, bytes))
        {
            // Some old Gemini projections predate the current renderer. Require the verified owned primary
            // to have the complete same bytes before recognizing that historical mirror.
            var primaryDestination = new Destination(ProviderType.Gemini, primary);
            var primarySnapshot = ReadDestination(primaryDestination);
            if (sibling.AppliedRevision != entry.Revision ||
                primarySnapshot.Hash != sibling.LastWrittenRawHash ||
                !SameFiles(primarySnapshot.FileHashes(), sibling.Files) ||
                HasUnreviewedFiles(primaryDestination, bytes) ||
                !SameFiles(bytes, ReadRawManifest(primaryDestination, primarySnapshot))) return false;
        }

        if (HasUnreviewedFiles(observation.Destination, bytes) ||
            !SameFiles(bytes, ReadRawManifest(observation.Destination, ReadDestination(observation.Destination))))
            throw new SkillConflictException("The Antigravity mirror changed during recognition.");
        observation.Target.OwnedFiles = observation.Snapshot.FileHashes()
            .Where(file => sibling.OwnedFiles.ContainsKey(file.Key))
            .ToDictionary(file => file.Key, file => file.Value, SkillPaths.Comparer);
        await RecordAppliedAsync(model.Name, entry, model, observation, owned: true).ConfigureAwait(false);
        PublishActivity(model.Name, "gemini", "recognized", "Recognized the matching Antigravity mirror.");
        return true;
    }

    public Task<DestinationReview> ReviewDestinationAsync(string slug, string path, CancellationToken ct = default) =>
        RunAsync(() => Task.FromResult(ReadReview(slug, path).Review), ct);

    private ReviewState ReadReview(string slug, string path)
    {
        slug = RequireSlug(slug);
        if (!_ledger.Skills.TryGetValue(slug, out var entry) || entry.Deleted || !_managed.TryGetValue(slug, out var model))
            throw new KeyNotFoundException("This skill is no longer available.");
        var canonical = SkillStorage.Read(_storage.CanonicalPath(slug));
        if (entry.PendingCanonical is not null || canonical.Hash != entry.CanonicalDiskHash ||
            !SameFiles(canonical.FileHashes(), entry.CanonicalFiles))
            throw new SkillConflictException("The canonical skill changed on disk. Reconcile before opening a fresh comparison.");
        var destination = ResolveLedgerDestination(slug, path);
        if (destination is null || !entry.Targets.TryGetValue(destination.Path, out var target) ||
            !InstalledProviderNames().Contains(target.Provider) || !model.IsEnabledFor(destination.Provider))
            throw new KeyNotFoundException("This destination is no longer active.");
        if (target.State is not ("conflict" or "missing" or "error"))
            throw new SkillConflictException("This destination no longer needs review.");

        var intended = IntendedProjection(model, destination);
        SkillDiskSnapshot observed;
        try { observed = ReadDestination(destination); }
        catch (Exception error) when (target.State == "error" && IsDiskOrInputFailure(error))
        {
            observed = new SkillDiskSnapshot(null, new(SkillPaths.Comparer));
        }
        var raw = ReadRawManifest(destination, observed);
        var actions = new List<string>();
        var reason = target.State;
        var unreviewed = HasUnreviewedFiles(destination, raw);
        var identical = !unreviewed && IsByteIdentical(intended, raw);
        if (target.State is "missing" or "error" || observed.Markdown is null)
        {
            if (observed.Markdown is null && target.State != "error") reason = "missing";
            actions.Add("retry");
        }
        else if (observed.Markdown is not null)
        {
            reason = identical ? "identical" : "different";
            if (identical) actions.Add("recognize");
            if (!identical) actions.Add("keep");
            if (!identical && model.SourceCategory != "system" && slug != BootstrapSkillName) actions.Add("accept");
            if (model.SourceCategory != "system") actions.Add("copy");
            actions.Add("leave");
        }

        var files = new List<ReviewFile>();
        void Add(string relative, string? expected, string? actual) =>
            files.Add(new(relative, expected is null ? "Provider only" : actual is null ? "Missing" :
                raw.GetValueOrDefault(relative) == HashBytes(expected) ? "Identical" : "Different", expected, actual));

        Add("SKILL.md", intended.Markdown, observed.Markdown);
        foreach (var file in intended.Files.Keys.Union(observed.Files.Keys, SkillPaths.Comparer).Order(SkillPaths.Comparer))
            Add(file, intended.Files.GetValueOrDefault(file), observed.Files.GetValueOrDefault(file));

        if (unreviewed) files.Add(new("Other companion files", "Outside the managed text bundle", null, null));

        var fingerprint = ContentHasher.ComputeFileRevisionHash(System.Text.Json.JsonSerializer.Serialize(new
        {
            entry.Revision, target.State, target.Owned, target.ConflictHash, destination.Path,
            expected = SnapshotHash(intended), unreviewed, actual = raw.OrderBy(file => file.Key, SkillPaths.Comparer)
        }));
        var observation = new Observation(destination, target, observed);
        return new(model, entry, observation, intended, raw, new(Issue(model, target), fingerprint, reason, files, actions));
    }

    public Task<CanonicalSkillModel> ResolveDestinationAsync(string slug, string path, string version, string action,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        var state = ReadReview(slug, path);
        if (state.Review.Version != version || !state.Review.Actions.Contains(action))
            throw new SkillConflictException("The destination changed. Open a fresh review.");
        var (model, entry, observation, intended, raw, _) = state;
        var (destination, target, observed) = observation;

        if (action == "retry")
        {
            await ReconcileSkillAsync(model.Name, [], "destination-retry", allowInbound: false).ConfigureAwait(false);
            if (target.State is "missing" or "conflict" && target.Owned && ReadDestination(destination) is { Markdown: null } missing)
            {
                await SetStateAsync(target, "missing", "The provider copy is missing.").ConfigureAwait(false);
                await ProjectAsync(model.Name, model, entry, new(destination, target, missing), allowMissingRepair: true).ConfigureAwait(false);
            }
            if (target.State is "conflict" or "missing" or "error")
                throw new SkillConflictException(target.Error ?? "The destination still needs review.");
        }
        else
        {
            if (!SameFiles(raw, ReadRawManifest(destination, ReadDestination(destination))))
                throw new SkillConflictException("Provider files changed. Open a fresh review.");
            if (action == "copy")
            {
                var copy = _transpilers[destination.Provider].MergeInbound(model.Name, LearningFooter.Strip(observed.Markdown!), null);
                copy.Name = model.Name[..Math.Min(model.Name.Length, 42)] + "-provider-" + Guid.NewGuid().ToString("N")[..8];
                copy.Title = model.GetEffectiveTitle() + " (Provider copy)";
                copy.SourceCategory = "user";
                copy.OriginProvider = "Conflict review";
                copy.SupportingFiles = observed.Files;
                foreach (var provider in _transpilers.Keys) copy.SetEnabledFor(provider, false);
                return await SaveAndProjectAsync(copy.Name, copy, "destination-copy").ConfigureAwait(false);
            }
            if (action == "leave")
            {
                await RecordProviderManagedAsync(model.Name, entry, model, observation).ConfigureAwait(false);
            }
            else if (action == "recognize")
            {
                // Recognition is an explicit adoption of the primary. Companion ownership stays limited
                // to files already proven to be owned.
                await RecordAppliedAsync(model.Name, entry, model, observation, owned: true).ConfigureAwait(false);
            }
            else
            {
                var snapshotId = await SaveConflictAsync(model.Name, observation, "Saved before resolving the destination.",
                    forceSnapshot: action == "keep").ConfigureAwait(false);
                if (action == "accept")
                {
                    model = ReadInbound(model.Name, observation, model);
                    await CommitCanonicalAsync(model.Name, model, entry, "accept-provider").ConfigureAwait(false);
                    await RecordAppliedAsync(model.Name, entry, model, observation, target.Owned).ConfigureAwait(false);
                    await ReconcileSkillAsync(model.Name, [], "accept-provider", allowInbound: false).ConfigureAwait(false);
                }
                else
                {
                    var previous = observed.FileHashes().Where(file => intended.Files.ContainsKey(file.Key) ||
                        target.OwnedFiles.GetValueOrDefault(file.Key) == file.Value)
                        .ToDictionary(file => file.Key, file => file.Value, SkillPaths.Comparer);
                    target.OwnedFiles = previous;
                    target.Pending = new PendingProjection(intended.Hash, intended.FileHashes(), false, intended.FileHashes(), observed.Hash);
                    await PersistAsync().ConfigureAwait(false);
                    await ApplyProjectionAsync(model.Name, model, entry, destination, target, intended).ConfigureAwait(false);
                    var written = ReadDestination(destination);
                    if (written.Hash != target.LastWrittenRawHash || !SameFiles(written.FileHashes(), target.Files))
                        throw new SkillConflictException("The provider changed after the replacement. The saved snapshot is still available.");
                    var applied = ReadRawManifest(destination, written);
                    target.Replacement = new ReviewedReplacement(snapshotId!, entry.Revision, RawManifestHash(applied));
                    await PersistAsync().ConfigureAwait(false);
                }
            }
        }
        PublishSnapshot(model, entry);
        PublishActivity(model.Name, target.Provider, "review", $"Reviewed {DestinationLabel(target.Provider, target.Path)}: {action}.");
        return _managed[model.Name].Clone();
    }, ct);

    private Task RecordProviderManagedAsync(string slug, SkillLedgerEntry entry, CanonicalSkillModel model, Observation observation)
    {
        observation.Target.OwnedFiles.Clear();
        return RecordAppliedAsync(slug, entry, model, observation, owned: false, acknowledgedHash: SnapshotHash(observation.Snapshot));
    }

    public Task<CanonicalSkillModel> UndoDestinationReplacementAsync(string id, string revision, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var raw = ReadConflict(id);
            if (ContentHasher.ComputeFileRevisionHash(raw) != revision)
                throw new SkillConflictException("The snapshot changed. Reload Diagnostics.");
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var path = document.RootElement.GetProperty("destination").GetString()!;
            var slug = RequireSlug(id.Split('/')[0]);
            if (!_ledger.Skills.TryGetValue(slug, out var entry) || entry.Deleted ||
                !_managed.TryGetValue(slug, out var model) || ResolveLedgerDestination(slug, path) is not { } destination ||
                !model.IsEnabledFor(destination.Provider) || !entry.Targets.TryGetValue(path, out var target) ||
                target.Replacement is not { } replacement || replacement.SnapshotId != id || target.State != "synced" ||
                entry.Revision != replacement.Revision || entry.PendingCanonical is not null)
                throw new SkillConflictException("This destination no longer matches the reviewed replacement.");
            var canonical = SkillStorage.Read(_storage.CanonicalPath(slug));
            if (canonical.Hash != entry.CanonicalDiskHash || !SameFiles(canonical.FileHashes(), entry.CanonicalFiles))
                throw new SkillConflictException("The canonical skill changed after the replacement.");

            var observed = ReadDestination(destination);
            if (RawManifestHash(ReadRawManifest(destination, observed)) != replacement.AppliedManifest)
                throw new SkillConflictException("The provider files changed after the replacement. Their newer contents were kept.");
            var snapshot = ReadReviewedSnapshot(id, destination);
            var files = snapshot.FileHashes();
            target.Pending = new PendingProjection(snapshot.Hash, files, false, null, observed.Hash, id);
            await PersistAsync().ConfigureAwait(false);
            await RestoreReviewedDestinationAsync(slug, model, entry, destination, target, observed, id).ConfigureAwait(false);
            PublishSnapshot(model, entry);
            PublishActivity(slug, target.Provider, "review", $"Restored the reviewed snapshot for {DestinationLabel(target.Provider, path)}.");
            return _managed[slug].Clone();
        }, ct);

    private SkillDiskSnapshot ReadReviewedSnapshot(string id, Destination destination)
    {
        using var document = System.Text.Json.JsonDocument.Parse(ReadConflict(id));
        var data = document.RootElement;
        if (!SkillPaths.Comparer.Equals(data.GetProperty("destination").GetString(), destination.Path) ||
            data.GetProperty("markdown").GetString() is not { } markdown)
            throw new SkillConflictException("The snapshot no longer identifies this destination.");
        var files = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(data.GetProperty("supporting_files"))!;
        return new(markdown, new(files, SkillPaths.Comparer));
    }

    private async Task RestoreReviewedDestinationAsync(string slug, CanonicalSkillModel model, SkillLedgerEntry entry,
        Destination destination, TargetFileLedger target, SkillDiskSnapshot observed, string id)
    {
        var pending = target.Pending!;
        var snapshot = ReadReviewedSnapshot(id, destination);
        if (pending.RawHash != snapshot.Hash || !SameFiles(pending.Files, snapshot.FileHashes()) ||
            (observed.Hash != pending.RawHash && observed.Hash != pending.PreviousRawHash) ||
            (destination.HasBundle && !CanApplyFiles(destination.Directory, observed.FileHashes(), target.OwnedFiles, pending.Files)))
        {
            target.Pending = null;
            await SaveConflictAsync(slug, new(destination, target, observed),
                "A snapshot restore was interrupted and requires review.", forceSnapshot: true).ConfigureAwait(false);
            throw new SkillConflictException("The destination changed during restoration.");
        }
        if (destination.HasBundle)
            await SkillStorage.ApplyFilesAsync(destination.Directory, snapshot.Files, target.OwnedFiles,
                path => RegisterWithdrawal(slug, path)).ConfigureAwait(false);
        await SkillStorage.WriteIfUnchangedAsync(destination.Path, snapshot.Markdown!, pending.PreviousRawHash).ConfigureAwait(false);
        var restored = ReadDestination(destination);
        if (SnapshotHash(restored) != SnapshotHash(snapshot))
            throw new SkillConflictException("The provider changed during restoration.");
        target.Replacement = null;
        await RecordProviderManagedAsync(slug, entry, model, new(destination, target, restored)).ConfigureAwait(false);
    }
}
