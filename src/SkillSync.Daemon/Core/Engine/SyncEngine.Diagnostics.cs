using System.Text.Json;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed record ConflictBackup(string Id, string Skill, string Destination, string Revision);
public sealed record SyncDiagnostics(IReadOnlyList<ConflictBackup> Conflicts, IReadOnlyList<string> Trash);

public sealed partial class SyncEngine
{
    public Task<SyncDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        var backups = new List<ConflictBackup>();
        if (Directory.Exists(_discovery.ConflictsRoot))
        {
            foreach (var file in SkillStorage.EnumerateSafeFiles(_discovery.ConflictsRoot).Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderDescending(StringComparer.Ordinal).Take(100))
            {
                try
                {
                    var id = Path.GetRelativePath(_discovery.ConflictsRoot, file).Replace('\\', '/');
                    var content = ReadConflict(id);
                    using var document = JsonDocument.Parse(content);
                    var destination = document.RootElement.GetProperty("destination").GetString();
                    if (destination is not null && document.RootElement.GetProperty("markdown").ValueKind == JsonValueKind.String)
                    {
                        backups.Add(new ConflictBackup(id, id.Split('/')[0], destination, ContentHasher.ComputeFileRevisionHash(content)));
                    }
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // Only complete, readable snapshots are recoverable here.
                }
            }
        }
        var trash = _ledger.Skills.Where(pair => pair.Value.Deleted && pair.Value.Tombstone is not null)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        return Task.FromResult(new SyncDiagnostics(backups, trash));
    }, ct);

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
        var model = _transpilers[observed.Provider].MergeInbound(slug, markdown, null);
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
