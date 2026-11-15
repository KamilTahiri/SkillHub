using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Engine;

public sealed record UpstreamUpdate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("current_revision")] string CurrentRevision,
    [property: JsonPropertyName("latest_revision")] string LatestRevision,
    [property: JsonPropertyName("local_modified")] bool LocalModified,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt);

public sealed record UpdateFileChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("change")] string Change);

public sealed record UpstreamUpdateDiff(
    [property: JsonPropertyName("update")] UpstreamUpdate Update,
    [property: JsonPropertyName("local")] string Local,
    [property: JsonPropertyName("upstream")] string Upstream,
    [property: JsonPropertyName("files")] IReadOnlyList<UpdateFileChange> Files,
    [property: JsonPropertyName("upstream_files")] IReadOnlyDictionary<string, string> UpstreamFiles,
    [property: JsonPropertyName("local_files")] IReadOnlyDictionary<string, string> LocalFiles);

/// <summary>
/// Upstream update inbox: compares imported skills with their GitHub source. A skill has an update when the source commit
/// moved and the upstream content differs from what was imported (<c>import_hash</c>). Local edits alone do not count.
/// </summary>
public sealed partial class SkillImporterService
{
    private static readonly TimeSpan UpdateCacheLifetime = TimeSpan.FromMinutes(30);
    private readonly object _updatesGate = new();
    private (DateTimeOffset At, IReadOnlyList<UpstreamUpdate> Updates)? _updates;

    internal sealed record UpstreamSkill(CanonicalSkillModel Model, string Raw, string License, IReadOnlyList<string> Skipped, string Hash);

    internal async Task<UpstreamSkill> ReadUpstreamSkillAsync(RepositoryIndex index, IndexedDocument document, CancellationToken ct)
    {
        var raw = await _indexer.ReadTextAsync(index, document.Path, ct).ConfigureAwait(false);
        var model = ParseMarkdownToSkillModel(raw, SkillDocumentClassifier.FallbackName(index.Repo, document.Path), $"GitHub ({index.Owner}/{index.Repo})");
        var skipped = document.ExcludedFiles.Select(file => $"{file} · binary or too large").ToList();
        var prefix = RepositoryIndexer.SkillDirectoryPrefix(document.Path);
        foreach (var relative in document.Files)
        {
            try
            {
                model.SupportingFiles.Add(SkillPaths.SupportingPath(relative), await _indexer.ReadTextAsync(index, prefix + relative, ct).ConfigureAwait(false));
            }
            catch (InvalidDataException exception)
            {
                skipped.Add($"{relative} · {exception.Message}");
            }
        }
        return new UpstreamSkill(model, raw, License(index, document, raw), skipped, SkillHistory.HashOf(raw, model.SupportingFiles));
    }

    private static (string Repo, string Path, string Revision)? ImportOrigin(CanonicalSkillModel skill)
    {
        var repo = skill.Passthrough.GetValueOrDefault("import_source")?.ToString();
        var path = skill.Passthrough.GetValueOrDefault("import_path")?.ToString();
        var revision = skill.Passthrough.GetValueOrDefault("import_revision")?.ToString();
        return repo is not null && path is not null && revision is not null &&
            Regex.IsMatch(repo, @"\A[A-Za-z0-9-]{1,100}/[A-Za-z0-9_.-]{1,100}\z") && Regex.IsMatch(revision, @"\A[0-9a-f]{40}\z")
            ? (repo, path, revision) : null;
    }

    /// <summary>Checks every skill imported from GitHub. Results are cached for 30 minutes unless forced.</summary>
    public async Task<IReadOnlyList<UpstreamUpdate>> CheckUpdatesAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_updatesGate)
        {
            if (!force && _updates is { } cached && DateTimeOffset.UtcNow - cached.At < UpdateCacheLifetime) return cached.Updates;
        }
        // ListSkills returns summaries without passthrough metadata, so each candidate is loaded in full.
        var imported = _engine.ListSkills().Where(skill => !skill.IsCatalogOnly)
            .Select(skill => _engine.GetSkill(skill.Name)).OfType<CanonicalSkillModel>()
            .Where(skill => ImportOrigin(skill) is not null).ToArray();
        var results = new System.Collections.Concurrent.ConcurrentBag<UpstreamUpdate>();
        await Parallel.ForEachAsync(imported, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (skill, token) =>
        {
            try
            {
                if (await FindUpdateAsync(skill, token).ConfigureAwait(false) is { } update) results.Add(update);
            }
            catch (Exception error) when (error is CommunitySourceException or InvalidDataException or KeyNotFoundException or ArgumentException)
            {
                // An unreachable or renamed source is skipped; the next check retries it.
            }
        }).ConfigureAwait(false);
        var list = results.OrderBy(update => update.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_updatesGate) _updates = (DateTimeOffset.UtcNow, list);
        return list;
    }

    private async Task<UpstreamUpdate?> FindUpdateAsync(CanonicalSkillModel skill, CancellationToken ct)
    {
        var (repo, path, revision) = ImportOrigin(skill)!.Value;
        var parts = repo.Split('/');
        var latest = await _indexer.ResolveCommitAsync(parts[0], parts[1], "HEAD", ct).ConfigureAwait(false);
        if (latest == revision || skill.Passthrough.GetValueOrDefault("import_ignored_revision")?.ToString() == latest) return null;
        var index = await _indexer.GetIndexAsync(parts[0], parts[1], latest, ct).ConfigureAwait(false);
        var document = SelectDocument(index, path);
        var upstream = await ReadUpstreamSkillAsync(index, document, ct).ConfigureAwait(false);
        var importedHash = skill.Passthrough.GetValueOrDefault("import_hash")?.ToString();
        if (importedHash is not null && importedHash == upstream.Hash) return null;
        var localHash = skill.Passthrough.GetValueOrDefault("import_content_hash")?.ToString();
        var localModified = localHash is null || localHash != ContentFingerprint(skill);
        return new UpstreamUpdate(skill.Name, skill.GetEffectiveTitle(), repo, document.Path, revision, latest, localModified, DateTimeOffset.UtcNow);
    }

    /// <summary>Fingerprint of the parts an update replaces, stored at import so later local edits can be recognised.</summary>
    internal static string ContentFingerprint(CanonicalSkillModel skill) =>
        SkillHistory.HashOf(Comparable(skill), skill.SupportingFiles.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value), StringComparer.Ordinal));

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    /// <summary>The parts of a skill that an upstream update replaces, in a form that diffs cleanly.</summary>
    private static string Comparable(CanonicalSkillModel skill) =>
        "description: " + skill.Description.Trim() + "\n" +
        (string.IsNullOrWhiteSpace(skill.WhenToUse) ? string.Empty : "when_to_use: " + skill.WhenToUse.Trim() + "\n") +
        "\n" + Normalize(skill.InstructionsMarkdown) + "\n";

    public async Task<UpstreamUpdateDiff> GetUpdateDiffAsync(string name, CancellationToken ct = default)
    {
        var skill = _engine.GetSkill(name) ?? throw new KeyNotFoundException("That skill was not found.");
        var update = await FindUpdateAsync(skill, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("This skill is up to date.");
        var parts = update.Repo.Split('/');
        var index = await _indexer.GetIndexAsync(parts[0], parts[1], update.LatestRevision, ct).ConfigureAwait(false);
        var upstream = await ReadUpstreamSkillAsync(index, SelectDocument(index, update.Path), ct).ConfigureAwait(false);
        var files = upstream.Model.SupportingFiles.Keys.Union(skill.SupportingFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => new UpdateFileChange(path,
                !skill.SupportingFiles.TryGetValue(path, out var local) ? "added"
                : !upstream.Model.SupportingFiles.TryGetValue(path, out var remote) ? "removed"
                : Normalize(local) == Normalize(remote) ? "unchanged" : "changed"))
            .Where(change => change.Change != "unchanged").ToArray();
        return new UpstreamUpdateDiff(update, Comparable(skill), Comparable(upstream.Model), files,
            upstream.Model.SupportingFiles.Where(pair => files.Any(change => change.Path == pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            skill.SupportingFiles.Where(pair => files.Any(change => change.Path == pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    /// <summary>
    /// Applies the upstream version (instructions, description and bundle files) while keeping the local name, tags and
    /// provider choices, or records that the user keeps the local version of this upstream revision.
    /// </summary>
    public async Task<CanonicalSkillModel> ResolveUpdateAsync(string name, string action, string latestRevision, CancellationToken ct = default)
    {
        var skill = _engine.GetSkill(name) ?? throw new KeyNotFoundException("That skill was not found.");
        var origin = ImportOrigin(skill) ?? throw new ArgumentException("This skill was not imported from GitHub.");
        if (!Regex.IsMatch(latestRevision, @"\A[0-9a-f]{40}\z")) throw new ArgumentException("Choose an upstream revision.");
        CanonicalSkillModel saved;
        if (action == "keep")
        {
            saved = await _engine.EditSkillAsync(name, current => current.Passthrough["import_ignored_revision"] = latestRevision,
                "webui:update-keep-local", ct, skill.Revision).ConfigureAwait(false);
        }
        else if (action == "apply")
        {
            var parts = origin.Repo.Split('/');
            var index = await _indexer.GetIndexAsync(parts[0], parts[1], latestRevision, ct).ConfigureAwait(false);
            var upstream = await ReadUpstreamSkillAsync(index, SelectDocument(index, origin.Path), ct).ConfigureAwait(false);
            SkillStorage.ValidateModel(upstream.Model);
            saved = await _engine.EditSkillAsync(name, current =>
            {
                current.Description = upstream.Model.Description;
                current.WhenToUse = upstream.Model.WhenToUse;
                current.InstructionsMarkdown = upstream.Model.InstructionsMarkdown;
                current.SupportingFiles = new Dictionary<string, string>(upstream.Model.SupportingFiles, StringComparer.Ordinal);
                current.Passthrough["import_revision"] = latestRevision;
                current.Passthrough["import_hash"] = upstream.Hash;
                current.Passthrough["import_content_hash"] = ContentFingerprint(current);
                current.Passthrough.Remove("import_ignored_revision");
            }, "webui:update-apply", ct, skill.Revision).ConfigureAwait(false);
        }
        else throw new ArgumentException("Choose apply or keep.");
        lock (_updatesGate) _updates = _updates is { } cached ? (cached.At, cached.Updates.Where(update => update.Name != name).ToArray()) : null;
        return saved;
    }
}
