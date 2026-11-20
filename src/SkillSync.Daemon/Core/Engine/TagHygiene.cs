using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>Metadata-only view of one skill for tag work: never bodies or companion files.</summary>
public sealed record TagSkillInfo(string Slug, string Name, string Scope, string Source, IReadOnlyList<string> Tags);

public sealed record TagUsage(string Tag, int Count, IReadOnlyList<string> Skills, bool Registered);

/// <summary>Two tags that probably mean the same thing. Kind: spelling, separator, plural, typo or narrower.</summary>
public sealed record TagPair(string Tag, string Other, string Kind, string Suggestion);

public sealed record TagReport(
    IReadOnlyList<TagUsage> Tags,
    IReadOnlyList<TagSkillInfo> Skills,
    IReadOnlyList<string> Untagged,
    IReadOnlyList<TagPair> NearDuplicates,
    IReadOnlyList<string> SingleUse,
    IReadOnlyList<string> Unused);

/// <summary>Pure tag analysis over the library (no I/O), used by <c>list_tags</c>.</summary>
public static class TagHygiene
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "system", "skillhub", "meta-skill" };

    public static TagReport Analyze(IEnumerable<CanonicalSkillModel> library, IEnumerable<string>? registeredTags = null)
    {
        var skills = library.Where(skill => skill.SourceCategory != "system" && !skill.IsCatalogOnly)
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var registered = new HashSet<string>((registeredTags ?? []).Select(ProviderDiscoveryService.NormalizeSlug), StringComparer.OrdinalIgnoreCase);
        var infos = new List<TagSkillInfo>();
        var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            var tags = new List<string>();
            foreach (var raw in skill.Tags)
            {
                var tag = ProviderDiscoveryService.NormalizeSlug(raw);
                if (tag.Length == 0 || Reserved.Contains(tag)) continue;
                if (!spellings.TryGetValue(tag, out var forms)) spellings[tag] = forms = new(StringComparer.Ordinal);
                forms.Add(raw.Trim());
                if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
                tags.Add(tag);
                if (!usage.TryGetValue(tag, out var list)) usage[tag] = list = [];
                list.Add(skill.Name);
            }
            infos.Add(new TagSkillInfo(skill.Name, skill.Title, skill.Scope, ContextBudget.PackOf(skill), tags));
        }

        var all = usage.Keys.Union(registered, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var tagUsage = all.Select(tag => new TagUsage(tag, usage.GetValueOrDefault(tag)?.Count ?? 0,
            usage.GetValueOrDefault(tag) ?? [], registered.Contains(tag))).ToList();

        var pairs = new List<TagPair>();
        foreach (var (tag, forms) in spellings.Where(item => item.Value.Count > 1))
            pairs.Add(new TagPair(tag, string.Join(" / ", forms.Order(StringComparer.Ordinal)), "spelling",
                $"Skills write #{tag} as {string.Join(", ", forms.Order(StringComparer.Ordinal))}; SkillHub treats them as one tag."));
        var used = usage.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < used.Count; i++)
        for (var j = i + 1; j < used.Count; j++)
        {
            var (a, b) = (used[i], used[j]);
            var kind = Relation(a, b);
            if (kind is null) continue;
            var (keep, drop) = usage[a].Count >= usage[b].Count ? (a, b) : (b, a);
            if (kind == "narrower")
            {
                (keep, drop) = a.Length <= b.Length ? (a, b) : (b, a);
                pairs.Add(new TagPair(a, b, kind, $"#{drop} is a narrower form of #{keep}. Merge only if the distinction is not useful; ask the user when unsure."));
            }
            else pairs.Add(new TagPair(a, b, kind, $"Probably the same tag: merge #{drop} into #{keep}."));
        }

        return new TagReport(
            tagUsage,
            infos,
            infos.Where(info => info.Tags.Count == 0).Select(info => info.Slug).ToList(),
            pairs,
            tagUsage.Where(item => item.Count == 1).Select(item => item.Tag).ToList(),
            tagUsage.Where(item => item.Count == 0).Select(item => item.Tag).ToList());
    }

    /// <summary>How two normalized tags relate, or null when they look unrelated.</summary>
    public static string? Relation(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return null;
        if (a.Replace("-", string.Empty) == b.Replace("-", string.Empty)) return "separator";
        if (a + "s" == b || b + "s" == a || a + "es" == b || b + "es" == a) return "plural";
        if (Math.Min(a.Length, b.Length) >= 5 && Distance(a, b) == 1) return "typo";
        if (b.StartsWith(a + "-", StringComparison.Ordinal) || a.StartsWith(b + "-", StringComparison.Ordinal) ||
            b.EndsWith("-" + a, StringComparison.Ordinal) || a.EndsWith("-" + b, StringComparison.Ordinal)) return "narrower";
        return null;
    }

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
