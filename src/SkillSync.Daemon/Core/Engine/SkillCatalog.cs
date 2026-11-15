using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Transpilers;
using SkillSync.Daemon.Watchers;
using YamlDotNet.Core;

namespace SkillSync.Daemon.Core.Engine;

internal sealed class SkillCatalog(ProviderDiscoveryService discovery)
{
    private sealed record Entry(CatalogSkillDescriptor Descriptor, CanonicalSkillModel Summary, HashSet<string> InstructionTerms,
        IReadOnlyList<string> Files);
    private IReadOnlyDictionary<string, Entry> _entries = new Dictionary<string, Entry>();
    private readonly CodexTranspiler _parser = new();

    public void Refresh()
    {
        var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var descriptor in discovery.DiscoverExternalCatalogSkills())
        {
            try
            {
                var model = Load(descriptor, includeContents: false);
                if (model is not null)
                {
                    var terms = InstructionTerms(model.InstructionsMarkdown);
                    model.InstructionsMarkdown = string.Empty;
                    next.TryAdd(descriptor.Id, new Entry(descriptor, model, terms, ListBundle(descriptor)));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException or InvalidOperationException or ArgumentException)
            {
                // An unavailable or malformed catalog entry is excluded from this snapshot.
            }
        }
        Volatile.Write(ref _entries, next);
    }

    /// <summary>Companion file paths of a catalog skill, listed without reading them, so the Library can say "bundle" up front.</summary>
    public IReadOnlyList<string> Files(string id) =>
        Volatile.Read(ref _entries).TryGetValue(id, out var entry) ? entry.Files : [];

    private static IReadOnlyList<string> ListBundle(CatalogSkillDescriptor descriptor)
    {
        if (!Path.GetFileName(descriptor.SkillMdPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) return [];
        var directory = Path.GetDirectoryName(descriptor.SkillMdPath)!;
        try
        {
            var files = SkillStorage.EnumerateBundleFileGroups(directory).SelectMany(group => group)
                .Where(file => !AtomicFileWriter.IsTemporaryOrIgnoredPath(file))
                .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
                .Take(SkillStorage.MaxBundleFiles + 1).ToArray();
            // A folder too large to import as one bundle shows only its SKILL.md, matching what Get() loads.
            return files.Length > SkillStorage.MaxBundleFiles ? [] : files;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public IEnumerable<CanonicalSkillModel> Summaries() => Volatile.Read(ref _entries).Values.Select(entry => entry.Summary);

    public bool MatchesInstructions(string id, string query)
    {
        if (!Volatile.Read(ref _entries).TryGetValue(id, out var entry))
        {
            return false;
        }
        var terms = InstructionTerms(query).ToArray();
        return terms.Length > 0 && terms.All(entry.InstructionTerms.Contains);
    }

    private static HashSet<string> InstructionTerms(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in System.Text.RegularExpressions.Regex.EnumerateMatches(text, @"[\p{L}\p{Nd}_-]+"))
        {
            if (match.Length <= 64)
            {
                words.Add(text.Substring(match.Index, match.Length));
                if (words.Count == 2048)
                {
                    break;
                }
            }
        }
        return words;
    }

    public CanonicalSkillModel? Get(string id)
    {
        var entries = Volatile.Read(ref _entries);
        var descriptor = entries.TryGetValue(id, out var exact)
            ? exact.Descriptor
            : entries.Values.Where(entry => entry.Descriptor.Slug.Equals(id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray() is [var unique] ? unique.Descriptor : null;
        return descriptor is null ? null : Load(descriptor, includeContents: true);
    }

    private CanonicalSkillModel? Load(CatalogSkillDescriptor descriptor, bool includeContents)
    {
        var raw = SkillStorage.ReadText(descriptor.SkillMdPath, SkillStorage.MaxMarkdownBytes);
        if (raw is null)
        {
            return null;
        }

        var model = _parser.MergeInbound(descriptor.Slug, raw, null);
        model.Title = CanonicalSkillModel.HumanizeSlug(descriptor.Slug);
        model.SourceCategory = descriptor.SourceCategory;
        model.OriginProvider = descriptor.OriginProvider;
        model.IsCatalogOnly = true;
        model.CatalogId = descriptor.Id;
        model.SourcePath = descriptor.SkillMdPath.Replace('\\', '/');
        model.Tags = model.Tags.Union(descriptor.SuggestedTags, StringComparer.OrdinalIgnoreCase).ToList();
        if (includeContents && Path.GetFileName(descriptor.SkillMdPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                model.SupportingFiles = SkillStorage.ReadBundle(Path.GetDirectoryName(descriptor.SkillMdPath)!);
            }
            catch (InvalidDataException)
            {
                // A plugin folder that is too large to treat as one bundle still shows its SKILL.md.
                model.SupportingFiles = new(SkillPaths.Comparer);
            }
        }
        else
        {
            model.Passthrough.Clear();
        }
        return model;
    }
}
