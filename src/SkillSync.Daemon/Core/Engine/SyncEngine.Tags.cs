using System.Text.Json;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed record SkillTag(string Tag, int SkillCount, bool IsCustom);
public sealed record TagMutation(int UpdatedSkills, Guid? UndoId);
public sealed record TagBatchResult(IReadOnlyList<string> Changed, IReadOnlyList<string> Unchanged, IReadOnlyList<ScopeSkip> Skipped);

public sealed partial class SyncEngine
{
    private sealed record TagFile(int Version, string[] Tags);
    private sealed record TagEdit(string Slug, long Revision, List<string> PreviousTags);
    private sealed record TagUndo(Guid Id, string[] Before, string[] After, List<TagEdit> Edits);
    private string[] _customTags = [];
    private TagUndo? _tagUndo;
    private string? _tagRegistryError;

    private string TagFilePath => SkillPaths.Resolve(_discovery.SkillHubRoot, "tags.json");

    private void LoadTags()
    {
        try
        {
            var content = SkillStorage.ReadText(TagFilePath, 256 * 1024);
            if (content is null) return;
            var file = JsonSerializer.Deserialize<TagFile>(content);
            if (file is not { Version: 1, Tags: not null })
            {
                throw new InvalidDataException("Unsupported tag registry.");
            }
            _customTags = file.Tags.Select(NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _tagRegistryError = "SkillHub could not read tags.json. Repair the file or restore a backup, then restart SkillHub.";
            PublishActivity(string.Empty, "tags", "error", _tagRegistryError);
        }
    }

    public IReadOnlyList<SkillTag> ListTags()
    {
        var custom = Volatile.Read(ref _customTags);
        var counts = _managed.Values.Where(skill => skill.SourceCategory != "system")
            .SelectMany(skill => skill.Tags.Select(ProviderDiscoveryService.NormalizeSlug)
                .Where(tag => tag.Length > 0 && tag is not ("system" or "skillhub" or "meta-skill"))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return custom.Union(counts.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(tag => new SkillTag(tag, counts.GetValueOrDefault(tag), custom.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    public Task<string> CreateTagAsync(string value, CancellationToken ct = default)
    {
        var tag = NormalizeTag(value);
        return RunAsync(async () =>
        {
            var next = _customTags.Append(tag).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            if (!next.SequenceEqual(_customTags))
            {
                await SaveTagsAsync(next).ConfigureAwait(false);
            }
            return tag;
        }, ct);
    }

    private async Task SaveTagsAsync(string[] tags)
    {
        if (_tagRegistryError is not null) throw new InvalidDataException(_tagRegistryError);
        await AtomicFileWriter.WriteTextAtomicallyAsync(TagFilePath, JsonSerializer.Serialize(new TagFile(1, tags)) + "\n")
            .ConfigureAwait(false);
        Volatile.Write(ref _customTags, tags);
    }

    public Task<TagMutation> ChangeTagAsync(string input, string? replacement, CancellationToken ct = default)
    {
        var oldTag = NormalizeTag(input);
        var newTag = replacement is null ? null : NormalizeTag(replacement);
        return RunAsync(async () =>
        {
            if (_tagRegistryError is not null) throw new InvalidDataException(_tagRegistryError);
            if (oldTag == newTag)
            {
                return new TagMutation(0, null);
            }

            var before = _customTags;
            var after = before.Where(tag => tag != oldTag).Concat(newTag is null ? [] : new[] { newTag })
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            var edits = new List<TagEdit>();
            foreach (var slug in _managed.Keys.Where(name => name != BootstrapSkillName).ToArray())
            {
                var current = await LoadCurrentAsync(slug, GetEntry(slug)).ConfigureAwait(false);
                if (current is null || current.SourceCategory == "system" || !current.Tags.Any(value => ProviderDiscoveryService.NormalizeSlug(value) == oldTag))
                {
                    continue;
                }
                var previousTags = current.Tags.ToList();
                current.Tags = current.Tags.Where(tag => ProviderDiscoveryService.NormalizeSlug(tag) != oldTag)
                    .Concat(newTag is null ? [] : new[] { newTag }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var saved = await SaveAndProjectAsync(slug, current, "tag-edit").ConfigureAwait(false);
                edits.Add(new TagEdit(slug, saved.Revision, previousTags));
            }

            if (edits.Count == 0 && before.SequenceEqual(after))
            {
                return new TagMutation(0, null);
            }
            await SaveTagsAsync(after).ConfigureAwait(false);
            var undo = new TagUndo(Guid.NewGuid(), before, after, edits);
            _tagUndo = undo;
            PublishActivity(string.Empty, "tags", "updated", $"Updated #{oldTag} on {edits.Count} skills.");
            return new TagMutation(edits.Count, undo.Id);
        }, ct);
    }

    public Task<int> UndoTagChangeAsync(Guid id, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (_tagUndo is not { } undo || undo.Id != id || !_customTags.SequenceEqual(undo.After))
        {
            throw new SkillConflictException("That tag change can no longer be undone.");
        }
        var models = new List<(CanonicalSkillModel Model, TagEdit Edit)>();
        foreach (var edit in undo.Edits)
        {
            var model = await LoadCurrentAsync(edit.Slug, GetEntry(edit.Slug)).ConfigureAwait(false);
            if (model is null || model.Revision != edit.Revision)
            {
                throw new SkillConflictException($"'{edit.Slug}' changed after the tag operation.");
            }
            models.Add((model, edit));
        }
        foreach (var (model, edit) in models)
        {
            model.Tags = edit.PreviousTags;
            await SaveAndProjectAsync(edit.Slug, model, "tag-undo").ConfigureAwait(false);
        }
        await SaveTagsAsync(undo.Before).ConfigureAwait(false);
        _tagUndo = null;
        PublishActivity(string.Empty, "tags", "updated", $"Restored tags on {models.Count} skills.");
        return models.Count;
    }, ct);

    /// <summary>Metadata-only tag report for the library (tags, per-skill tags, hygiene findings).</summary>
    public TagReport TagReport() => TagHygiene.Analyze(ListSkills(), Volatile.Read(ref _customTags));

    /// <summary>
    /// Adds and removes tags on several skills in one call. Each changed skill is saved once (one History revision and
    /// one sync); skills whose tags would not change are left untouched. System and catalog-only skills are skipped.
    /// </summary>
    public async Task<TagBatchResult> EditTagsAsync(IEnumerable<string> skills, IReadOnlyList<string> add, IReadOnlyList<string> remove,
        string originator, CancellationToken ct = default)
    {
        var adding = add.Select(NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var removing = remove.Select(NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (adding.Count + removing.Count == 0) throw new ArgumentException("Give at least one tag to add or remove.");
        var changed = new List<string>();
        var unchanged = new List<string>();
        var skipped = new List<ScopeSkip>();
        foreach (var name in skills.Select(ProviderDiscoveryService.NormalizeSlug).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var skill = GetSkill(name);
            if (skill is null) { skipped.Add(new(name, "not in the library")); continue; }
            if (skill.SourceCategory == "system") { skipped.Add(new(name, "system skill")); continue; }
            if (skill.IsCatalogOnly) { skipped.Add(new(name, "built-in that is not active; activate it first")); continue; }
            var current = skill.Tags.Select(ProviderDiscoveryService.NormalizeSlug).ToList();
            var next = current.Where(tag => !removing.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Concat(adding).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (next.SequenceEqual(current, StringComparer.OrdinalIgnoreCase) &&
                skill.Tags.SequenceEqual(current, StringComparer.Ordinal)) { unchanged.Add(skill.Name); continue; }
            await EditSkillAsync(skill.Name, model => model.Tags = next, originator, ct).ConfigureAwait(false);
            changed.Add(skill.Name);
        }
        if (changed.Count > 0)
            PublishActivity(string.Empty, "tags", "updated", $"Updated tags on {changed.Count} skills.");
        return new TagBatchResult(changed, unchanged, skipped);
    }

    public Task<CanonicalSkillModel> SetSkillTagsAsync(string name, string tag, bool add, CancellationToken ct = default)
    {
        var normalized = NormalizeTag(tag);
        return EditSkillAsync(name, model =>
        {
            model.Tags = model.Tags.Where(value => ProviderDiscoveryService.NormalizeSlug(value) != normalized).ToList();
            if (add)
            {
                model.Tags.Add(normalized);
            }
        }, "tag-edit", ct);
    }
}
