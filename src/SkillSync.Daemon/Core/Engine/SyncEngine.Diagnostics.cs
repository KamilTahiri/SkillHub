using System.Text.Json;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed record ConflictBackup(string Id, string Skill, string Destination, string Revision, bool CanUndo,
    DateTimeOffset? SavedAt = null, string? Reason = null, string? Provider = null, int FileCount = 1, bool SkillExists = false);
public sealed record ConflictPreview(string Id, string Skill, string Destination, string? Provider, string BackupMarkdown,
    IReadOnlyDictionary<string, string> BackupFiles, string? CurrentMarkdown, IReadOnlyDictionary<string, string> CurrentFiles);
public sealed record TrashSkill(string Name, string Title, string? Origin, DateTimeOffset? DeletedAt,
    IReadOnlyList<string> RestoreTargets);
public sealed record SyncDiagnostics(IReadOnlyList<ConflictBackup> Conflicts, IReadOnlyList<TrashSkill> Trash,
    IReadOnlyList<DestinationIssue> Issues);

public sealed partial class SyncEngine
{
    public Task<SyncDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        var backups = new List<ConflictBackup>();
        if (Directory.Exists(_discovery.ConflictsRoot))
        {
            foreach (var file in SkillStorage.EnumerateSafeFiles(_discovery.ConflictsRoot).Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
            {
                try
                {
                    var id = Path.GetRelativePath(_discovery.ConflictsRoot, file).Replace('\\', '/');
                    var content = ReadConflict(id);
                    using var document = JsonDocument.Parse(content);
                    var destination = document.RootElement.GetProperty("destination").GetString();
                    if (destination is not null && document.RootElement.GetProperty("markdown").ValueKind == JsonValueKind.String)
                    {
                        var slug = id.Split('/')[0];
                        var entry = _ledger.Skills.GetValueOrDefault(slug);
                        var target = entry?.Targets.GetValueOrDefault(destination);
                        var canUndo = entry is { Deleted: false } && target is { State: "synced", Replacement: { } replacement } &&
                            replacement.SnapshotId == id && replacement.Revision == entry.Revision;
                        var root = document.RootElement;
                        var savedAt = root.TryGetProperty("saved_at", out var saved) && saved.ValueKind == JsonValueKind.String &&
                            saved.TryGetDateTimeOffset(out var stamp) ? stamp
                            : ParseSnapshotTimestamp(id) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                        var reason = root.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
                        var provider = _discovery.ResolveObservedPath(destination)?.Provider;
                        var fileCount = 1 + (root.TryGetProperty("supporting_files", out var files) && files.ValueKind == JsonValueKind.Object
                            ? files.EnumerateObject().Count() : 0);
                        backups.Add(new ConflictBackup(id, slug, destination, ContentHasher.ComputeFileRevisionHash(content), canUndo,
                            savedAt, ExplainConflict(reason), provider is { } known ? ProviderName(known) : null, fileCount,
                            entry is { Deleted: false } && _managed.ContainsKey(slug)));
                    }
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // Only complete, readable snapshots are recoverable here.
                }
            }
        }
        var installed = InstalledProviderNames();
        var trash = _ledger.Skills.Where(pair => pair.Value.Deleted && pair.Value.Tombstone is not null)
            .Select(pair => ReadTrashSkill(pair.Key, pair.Value, installed)).OrderByDescending(item => item.DeletedAt).ToArray();
        var issues = _managed.Values.SelectMany(skill => GetEntry(skill.Name).Targets.Values
            .Where(target => installed.Contains(target.Provider) && target.State is "conflict" or "missing" or "error")
            .Where(target => ResolveLedgerDestination(skill.Name, target.Path) is { } destination && skill.IsEnabledFor(destination.Provider))
            .Select(target => Issue(skill, target))).OrderBy(issue => issue.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.FromResult(new SyncDiagnostics(backups, trash, issues));
    }, ct);

    private TrashSkill ReadTrashSkill(string slug, SkillLedgerEntry entry, HashSet<string> installed)
    {
        var fallback = new TrashSkill(slug, CanonicalSkillModel.HumanizeSlug(slug), null, entry.DeletedAt, []);
        try
        {
            var path = SkillPaths.Resolve(_discovery.TrashRoot, entry.Tombstone! + "/SKILL.md");
            var text = SkillStorage.ReadText(path, SkillStorage.MaxMarkdownBytes);
            if (text is null) return fallback;
            var model = _transpilers[ProviderType.Canonical].MergeInbound(slug, text, null);
            var targets = _transpilers.Keys.Where(provider => installed.Contains(ProviderName(provider)) && model.IsEnabledFor(provider))
                .Select(ProviderName).ToArray();
            return new(slug, model.GetEffectiveTitle(), model.OriginProvider, entry.DeletedAt, targets);
        }
        catch (Exception error) when (IsDiskOrInputFailure(error)) { return fallback; }
    }

    public Task<CanonicalSkillModel> RecoverConflictCopyAsync(string id, string revision, CancellationToken ct = default) => RunAsync(async () =>
    {
        var raw = ReadConflict(id);
        if (ContentHasher.ComputeFileRevisionHash(raw) != revision)
        {
            throw new SkillConflictException("The backup changed. Reload Diagnostics before recovering it.");
        }
        using var document = JsonDocument.Parse(raw);
        var backup = document.RootElement;
        var markdown = backup.GetProperty("markdown").GetString() ?? throw new InvalidDataException("This backup has no skill document.");
        var observed = _discovery.ResolveObservedPath(backup.GetProperty("destination").GetString() ?? string.Empty)
            ?? throw new InvalidDataException("The backup does not identify a known provider.");
        var slug = RequireSlug(observed.Slug[..Math.Min(observed.Slug.Length, 42)] + "-recovered-" + Guid.NewGuid().ToString("N")[..8]);
        var model = _transpilers[observed.Provider].MergeInbound(slug, LearningFooter.Strip(markdown), null);
        model.Title = CanonicalSkillModel.HumanizeSlug(observed.Slug) + " (Recovered)";
        model.SourceCategory = "user";
        model.OriginProvider = "Recovered conflict";
        model.SupportingFiles = backup.GetProperty("supporting_files").Deserialize<Dictionary<string, string>>() ?? [];
        foreach (var provider in _transpilers.Keys)
        {
            model.SetEnabledFor(provider, false);
        }
        return await SaveAndProjectAsync(slug, model, "conflict-recovery").ConfigureAwait(false);
    }, ct);

    /// <summary>Snapshot ids start with the UTC time they were saved: <c>slug/yyyyMMdd-HHmmssfff-guid.json</c>.</summary>
    private static DateTimeOffset? ParseSnapshotTimestamp(string id)
    {
        var name = Path.GetFileName(id);
        return name.Length >= 18 && DateTimeOffset.TryParseExact(name[..18], "yyyyMMdd-HHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : null;
    }

    /// <summary>Turns the engine's conflict note into a sentence a person can act on.</summary>
    public static string ExplainConflict(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return "This tool's copy was different from SkillHub's version, so SkillHub saved it before syncing.";
        }
        const string unreadable = "The provider edit could not be applied.";
        return details switch
        {
            var d when d.StartsWith("Multiple provider files", StringComparison.Ordinal) => "Two files in this tool use the same skill name. SkillHub kept this one aside.",
            var d when d.StartsWith(unreadable, StringComparison.Ordinal) => ("An edit made in this tool could not be read, so SkillHub saved it instead of losing it. " + d[unreadable.Length..].Trim()).Trim(),
            var d when d.StartsWith("Provider and canonical changes", StringComparison.Ordinal) => "The skill was edited in SkillHub and in this tool at the same time. This is the tool's version.",
            var d when d.StartsWith("The destination already contains an unowned skill", StringComparison.Ordinal) => "This tool already had its own skill with this name. SkillHub saved it before syncing.",
            var d when d.StartsWith("The destination has edits or unowned files", StringComparison.Ordinal) => "This tool's copy had edits or extra files that SkillHub did not write. They were saved first.",
            var d when d.StartsWith("A projection was interrupted", StringComparison.Ordinal) => "A sync was interrupted (for example by a shutdown) and the copy changed afterwards.",
            var d when d.StartsWith("An edited provider file was kept during withdrawal", StringComparison.Ordinal) => "Sync was turned off for this tool, but its copy had edits. SkillHub kept them here.",
            var d when d.StartsWith("Saved before resolving", StringComparison.Ordinal) => "Saved automatically before you resolved a review, so that choice can be undone.",
            var d when d.StartsWith("A snapshot restore was interrupted", StringComparison.Ordinal) => "A restore was interrupted and the copy changed afterwards.",
            _ => details
        };
    }

    public Task<ConflictPreview> GetConflictPreviewAsync(string id, CancellationToken ct = default) => RunAsync(() =>
    {
        using var document = JsonDocument.Parse(ReadConflict(id));
        var backup = document.RootElement;
        var slug = SkillPaths.NormalizeRelative(id).Split('/')[0];
        var destination = backup.GetProperty("destination").GetString() ?? string.Empty;
        var provider = _discovery.ResolveObservedPath(destination)?.Provider;
        var files = backup.TryGetProperty("supporting_files", out var supporting) && supporting.ValueKind == JsonValueKind.Object
            ? supporting.Deserialize<Dictionary<string, string>>() ?? [] : [];
        string? current = null;
        IReadOnlyDictionary<string, string> currentFiles = new Dictionary<string, string>();
        if (_managed.TryGetValue(slug, out var model) && !GetEntry(slug).Deleted)
        {
            current = SkillStorage.ReadText(_storage.CanonicalPath(slug), SkillStorage.MaxMarkdownBytes);
            currentFiles = model.SupportingFiles ?? [];
        }
        return Task.FromResult(new ConflictPreview(id, slug, destination, provider is { } known ? ProviderName(known) : null,
            backup.GetProperty("markdown").GetString() ?? string.Empty, files, current, currentFiles));
    }, ct);

    /// <summary>
    /// Replaces the current skill with a safety backup. The backup is read with its provider's transpiler and merged
    /// with the current skill so metadata that provider cannot express survives; the replaced version stays in History.
    /// </summary>
    public Task<CanonicalSkillModel> RestoreConflictOverCurrentAsync(string id, string revision, CancellationToken ct = default) => RunAsync(async () =>
    {
        var raw = ReadConflict(id);
        if (ContentHasher.ComputeFileRevisionHash(raw) != revision)
        {
            throw new SkillConflictException("The backup changed. Reload Diagnostics before restoring it.");
        }
        using var document = JsonDocument.Parse(raw);
        var backup = document.RootElement;
        var markdown = backup.GetProperty("markdown").GetString() ?? throw new InvalidDataException("This backup has no skill document.");
        var observed = _discovery.ResolveObservedPath(backup.GetProperty("destination").GetString() ?? string.Empty)
            ?? throw new InvalidDataException("The backup does not identify a known provider.");
        var slug = SkillPaths.NormalizeRelative(id).Split('/')[0];
        var entry = GetEntry(slug);
        var current = await LoadCurrentAsync(slug, entry).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The current skill no longer exists. Restore the backup as a new skill instead.");
        var model = _transpilers[observed.Provider].MergeInbound(slug, LearningFooter.Strip(markdown), current);
        var files = backup.TryGetProperty("supporting_files", out var supporting) && supporting.ValueKind == JsonValueKind.Object
            ? supporting.Deserialize<Dictionary<string, string>>() : null;
        if (files is { Count: > 0 })
        {
            model.SupportingFiles = files;
        }
        return await SaveAndProjectAsync(slug, model, "conflict-restore").ConfigureAwait(false);
    }, ct);

    private string ReadConflict(string id)
    {
        var safe = SkillPaths.NormalizeRelative(id);
        if (safe.Split('/').Length != 2 || !safe.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a valid conflict backup.");
        }
        return SkillStorage.ReadText(SkillPaths.Resolve(_discovery.ConflictsRoot, safe), 32 * 1024 * 1024)
            ?? throw new KeyNotFoundException("The backup no longer exists.");
    }
}
