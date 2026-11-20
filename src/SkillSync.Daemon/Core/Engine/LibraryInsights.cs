using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Engine;

public sealed record ScopeSkip(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record ScopeChangeResult(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("changed")] IReadOnlyList<string> Changed,
    [property: JsonPropertyName("unchanged")] IReadOnlyList<string> Unchanged,
    [property: JsonPropertyName("skipped")] IReadOnlyList<ScopeSkip> Skipped);

public sealed record BudgetSkill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("tokens")] int Tokens,
    [property: JsonPropertyName("scope")] string Scope);

public sealed record BudgetPack(
    [property: JsonPropertyName("pack")] string Pack,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("tokens")] int Tokens,
    [property: JsonPropertyName("global_skills")] int GlobalSkills,
    [property: JsonPropertyName("project_only_skills")] int ProjectOnlySkills,
    [property: JsonPropertyName("off_skills")] int OffSkills,
    [property: JsonPropertyName("skills")] IReadOnlyList<BudgetSkill> Skills);

public sealed record ContextBudgetReport(
    [property: JsonPropertyName("tokens")] int Tokens,
    [property: JsonPropertyName("characters")] int Characters,
    [property: JsonPropertyName("always_loaded_bodies_tokens")] int AlwaysLoadedBodiesTokens,
    [property: JsonPropertyName("global_skills")] int GlobalSkills,
    [property: JsonPropertyName("project_only_skills")] int ProjectOnlySkills,
    [property: JsonPropertyName("off_skills")] int OffSkills,
    [property: JsonPropertyName("safe_tokens")] int SafeTokens,
    [property: JsonPropertyName("limit_tokens")] int LimitTokens,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("packs")] IReadOnlyList<BudgetPack> Packs);

/// <summary>
/// The always-loaded cost of the library: every globally synced skill puts its name, description and "when to use" into
/// every conversation of every tool that lists skills. Rule-file tools that load whole rules (Cline, Roo Code) also pay
/// for the body. Estimates use four characters per token, like Skill Doctor.
/// </summary>
public static class ContextBudget
{
    /// <summary>
    /// Claude Code shares one skill-listing budget (a small share of the context window) between every skill, command
    /// and plugin; once it is used up, it lists further skills by name only, so their descriptions can no longer
    /// trigger them. Keeping SkillHub's share near 2k tokens leaves room for plugins; past 4k descriptions get dropped.
    /// </summary>
    public const int SafeTokens = 2000;
    public const int LimitTokens = 4000;

    public const string PersonalPack = "personal";
    public const string SystemPack = "skillhub";

    public static string PackOf(CanonicalSkillModel skill) =>
        skill.SourceCategory == "system" ? SystemPack
        : skill.ImportSource ?? (skill.SourceCategory == "template" ? "built-in:" + skill.OriginProvider : PersonalPack);

    public static string PackLabel(string pack) => pack switch
    {
        PersonalPack => "Personal skills",
        SystemPack => "SkillHub system",
        _ when pack.StartsWith("built-in:", StringComparison.Ordinal) => pack["built-in:".Length..] + " (built-in)",
        _ => pack
    };

    private static bool ListsDescription(CanonicalSkillModel skill) =>
        skill.Targets.Claude.Enabled || skill.Targets.Gemini.Enabled || skill.Targets.Codex.Enabled || skill.Targets.Cursor.Enabled ||
        skill.Targets.Copilot.Enabled || skill.Targets.Windsurf.Enabled;

    private static bool LoadsBody(CanonicalSkillModel skill) => skill.Targets.Cline.Enabled || skill.Targets.RooCode.Enabled;

    public static int DescriptionCharacters(CanonicalSkillModel skill) =>
        skill.Name.Length + 2 + (skill.Description ?? string.Empty).Trim().Length +
        (string.IsNullOrWhiteSpace(skill.WhenToUse) ? 0 : 1 + skill.WhenToUse.Trim().Length);

    /// <summary>What one skill adds to every conversation today (0 when it is not global).</summary>
    public static int SkillTokens(CanonicalSkillModel skill) =>
        skill.IsGlobal && ListsDescription(skill) ? (int)Math.Ceiling(DescriptionCharacters(skill) / 4.0) : 0;

    public static ContextBudgetReport Calculate(IEnumerable<CanonicalSkillModel> library)
    {
        var managed = library.Where(skill => !skill.IsCatalogOnly).ToList();
        var packs = managed.GroupBy(PackOf, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var skills = group.Select(skill => new BudgetSkill(skill.Name, skill.GetEffectiveTitle(), SkillTokens(skill), skill.Scope))
                .OrderByDescending(item => item.Tokens).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
            var kind = group.Key == PersonalPack ? "personal" : group.Key == SystemPack ? "system" : "imported";
            return new BudgetPack(group.Key, PackLabel(group.Key), kind, skills.Sum(item => item.Tokens),
                group.Count(skill => skill.Scope == CanonicalSkillModel.ScopeGlobal),
                group.Count(skill => skill.Scope == CanonicalSkillModel.ScopeProject),
                group.Count(skill => skill.Scope == CanonicalSkillModel.ScopeOff), skills);
        }).OrderByDescending(pack => pack.Tokens).ThenBy(pack => pack.Label, StringComparer.OrdinalIgnoreCase).ToList();

        var global = managed.Where(skill => skill.IsGlobal && ListsDescription(skill)).ToList();
        var characters = global.Sum(DescriptionCharacters);
        var tokens = packs.Sum(pack => pack.Tokens);
        var bodies = managed.Where(skill => skill.IsGlobal && LoadsBody(skill))
            .Sum(skill => SkillDoctor.EstimateTokens(skill.InstructionsMarkdown));
        var state = tokens > LimitTokens ? "over" : tokens > SafeTokens ? "warning" : "ok";
        var message = state switch
        {
            "over" => $"About {Format(tokens)} tokens of skill descriptions load into every chat. Claude Code shares a small listing budget with plugins and starts listing skills by name only, so some descriptions never trigger. Move niche packs to Project-only.",
            "warning" => $"About {Format(tokens)} tokens of skill descriptions load into every chat. That is getting close to where Claude Code drops descriptions; consider making niche packs Project-only.",
            _ => $"About {Format(tokens)} tokens of skill descriptions load into every chat. That is within a comfortable budget."
        };
        if (bodies > 0) message += $" Cline or Roo Code also load about {Format(bodies)} tokens of full rule text.";
        return new ContextBudgetReport(tokens, characters, bodies, managed.Count(skill => skill.IsGlobal),
            managed.Count(skill => skill.Scope == CanonicalSkillModel.ScopeProject), managed.Count(skill => skill.Scope == CanonicalSkillModel.ScopeOff),
            SafeTokens, LimitTokens, state, message, packs);
    }

    public static string Format(int tokens) => tokens >= 1000 ? (tokens / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k" : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Shared text signals: tokenizing descriptions, TF-IDF similarity and references to other skills.</summary>
public static class SkillTextSignals
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "when", "use", "this", "that", "your", "you", "are", "from", "into", "user", "asks", "skill",
        "using", "used", "will", "should", "any", "all", "can", "also", "about", "need", "like", "such", "their", "them", "file",
        "not", "only", "before", "after", "then", "than", "what", "which", "where", "how", "more", "most", "each", "other", "its",
        "per", "one", "two", "via", "was", "were", "has", "have", "had", "but", "stay", "keep", "make", "build", "review", "design",
        "an", "as", "at", "be", "by", "do", "go", "if", "in", "is", "it", "me", "my", "no", "of", "on", "or", "so", "to", "up", "us", "we",
        "new", "get", "set", "want", "help", "please", "some", "there", "these", "those", "does", "doing", "done", "here", "out", "own"
    };

    private static readonly Regex Word = new("[a-z0-9][a-z0-9+#]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        foreach (Match match in Word.Matches((text ?? string.Empty).ToLowerInvariant()))
        {
            var word = match.Value;
            if (word.Length < 2 || word.All(char.IsAsciiDigit) || StopWords.Contains(word)) continue;
            if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal)) word = word[..^3] + "y";
            else if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)) word = word[..^1];
            if (!StopWords.Contains(word)) tokens.Add(word);
        }
        return tokens;
    }

    /// <summary>The trigger text agents see: the name (as words), description and "when to use".</summary>
    public static string TriggerText(CanonicalSkillModel skill) =>
        skill.Name.Replace('-', ' ') + " " + skill.Description + " " + skill.WhenToUse;

    public sealed class Corpus
    {
        private readonly Dictionary<string, double> _idf;
        private readonly int _count;
        public Dictionary<string, Dictionary<string, double>> Vectors { get; }

        public Corpus(IEnumerable<CanonicalSkillModel> skills)
        {
            var documents = skills.GroupBy(skill => skill.Name, StringComparer.Ordinal).Select(group => group.First())
                .ToDictionary(skill => skill.Name, skill => Tokenize(TriggerText(skill)), StringComparer.Ordinal);
            _count = documents.Count;
            var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var words in documents.Values)
                foreach (var word in words.Distinct())
                    frequency[word] = frequency.GetValueOrDefault(word) + 1;
            _idf = frequency.ToDictionary(pair => pair.Key, pair => Math.Log((_count + 1) / (pair.Value + 0.5)), StringComparer.Ordinal);
            Vectors = documents.ToDictionary(pair => pair.Key, pair => Vector(pair.Value), StringComparer.Ordinal);
        }

        private Dictionary<string, double> Vector(IEnumerable<string> words) => words.GroupBy(word => word, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (1 + Math.Log(group.Count())) * _idf.GetValueOrDefault(group.Key, Math.Log(_count + 1)), StringComparer.Ordinal);

        public double Similarity(string first, string second) =>
            Vectors.TryGetValue(first, out var a) && Vectors.TryGetValue(second, out var b) ? Cosine(a, b) : 0;

        public IReadOnlyList<string> SharedTerms(IReadOnlyCollection<string> names, int take = 5)
        {
            var vectors = names.Select(name => Vectors.GetValueOrDefault(name)).Where(vector => vector is not null).Select(vector => vector!).ToList();
            if (vectors.Count < 2) return [];
            return vectors.SelectMany(vector => vector.Keys).Distinct(StringComparer.Ordinal)
                .Select(word => (Word: word, Count: vectors.Count(vector => vector.ContainsKey(word)), Weight: vectors.Sum(vector => vector.GetValueOrDefault(word))))
                .Where(item => item.Count >= 2).OrderByDescending(item => item.Count).ThenByDescending(item => item.Weight)
                .Take(take).Select(item => item.Word).ToList();
        }

        private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            var dot = a.Sum(pair => pair.Value * b.GetValueOrDefault(pair.Key));
            var norm = Math.Sqrt(a.Values.Sum(value => value * value)) * Math.Sqrt(b.Values.Sum(value => value * value));
            return norm == 0 ? 0 : dot / norm;
        }
    }

    /// <summary>
    /// Two skills are likely to fire for the same request when their trigger texts are similar, or when they belong to
    /// the same named sub-family (for example roblox-ui-*) and are at least loosely similar.
    /// </summary>
    public const double OverlapThreshold = 0.2;
    public const double FamilyOverlapThreshold = 0.1;

    public static bool Overlaps(Corpus corpus, string first, string second, out double similarity)
    {
        similarity = corpus.Similarity(first, second);
        return similarity >= OverlapThreshold || SharedNamePrefix(first, second) >= 2 && similarity >= FamilyOverlapThreshold;
    }

    public static int SharedNamePrefix(string first, string second)
    {
        var a = first.Split('-');
        var b = second.Split('-');
        var count = 0;
        while (count < Math.Min(a.Length, b.Length) && a[count] == b[count]) count++;
        return count;
    }

    private static readonly Regex Reference = new(
        @"`([a-z0-9]+(?:-[a-z0-9]+)+)`|\b([a-z0-9]+(?:-[a-z0-9]+)+)\b(?=[^\n]{0,30}\bskills?\b)|(?:\bin|\bwith|\bsee|\bload|\bpair with|\buse|\bto)\s+`?([a-z0-9]+(?:-[a-z0-9]+)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Names this skill mentions that look like skills of a family the library has (two or more skills share the first
    /// name segment) but that are not installed. Typical after importing part of a pack whose skills route to each other.
    /// </summary>
    public static IReadOnlyList<string> MissingReferences(CanonicalSkillModel skill, ISet<string> knownNames, IReadOnlyDictionary<string, int> families)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Reference.Matches(skill.Description + "\n" + skill.WhenToUse + "\n" + skill.InstructionsMarkdown))
        {
            var name = match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value;
            if (name == skill.Name || knownNames.Contains(name)) continue;
            if (families.GetValueOrDefault(name.Split('-')[0]) < 2) continue;
            missing.Add(name);
        }
        return [.. missing];
    }

    public static Dictionary<string, int> Families(IEnumerable<string> names) => names
        .GroupBy(name => name.Split('-')[0], StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}

public sealed record TriggerCluster(
    [property: JsonPropertyName("skills")] IReadOnlyList<string> Skills,
    [property: JsonPropertyName("shared_terms")] IReadOnlyList<string> SharedTerms,
    [property: JsonPropertyName("max_similarity")] double MaxSimilarity,
    [property: JsonPropertyName("all_global")] bool AllGlobal);

public sealed record MissingReference(
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing);

public sealed record LibrarySkillReport(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("pack")] string Pack,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("findings")] IReadOnlyList<DoctorFinding> Findings);

public sealed record LibraryReport(
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("skills_checked")] int SkillsChecked,
    [property: JsonPropertyName("errors")] int Errors,
    [property: JsonPropertyName("warnings")] int Warnings,
    [property: JsonPropertyName("budget")] ContextBudgetReport Budget,
    [property: JsonPropertyName("collisions")] IReadOnlyList<TriggerCluster> Collisions,
    [property: JsonPropertyName("missing_references")] IReadOnlyList<MissingReference> MissingReferences,
    [property: JsonPropertyName("skills")] IReadOnlyList<LibrarySkillReport> Skills);

/// <summary>Skill Doctor across the whole library: per-skill findings, trigger collisions, missing companions and the budget.</summary>
public static class LibraryDoctor
{
    /// <param name="library">Every skill SkillHub knows, with contents (catalog entries count as known names only).</param>
    public static LibraryReport Audit(IReadOnlyList<CanonicalSkillModel> library)
    {
        var managed = library.Where(skill => !skill.IsCatalogOnly).ToList();
        var active = managed.Where(skill => skill.SourceCategory != "system" && skill.Scope != CanonicalSkillModel.ScopeOff).ToList();
        var corpus = new SkillTextSignals.Corpus(active);
        var known = library.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        var families = SkillTextSignals.Families(managed.Select(skill => skill.Name));

        // Collisions: connected groups of overlapping skills.
        var parent = active.ToDictionary(skill => skill.Name, skill => skill.Name, StringComparer.Ordinal);
        string Find(string name) { while (parent[name] != name) name = parent[name] = parent[parent[name]]; return name; }
        var overlaps = new Dictionary<string, List<(string Other, double Similarity)>>(StringComparer.Ordinal);
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < active.Count; i++)
            for (var j = i + 1; j < active.Count; j++)
            {
                var (a, b) = (active[i].Name, active[j].Name);
                if (!SkillTextSignals.Overlaps(corpus, a, b, out var similarity)) continue;
                parent[Find(a)] = Find(b);
                (overlaps.TryGetValue(a, out var la) ? la : overlaps[a] = []).Add((b, similarity));
                (overlaps.TryGetValue(b, out var lb) ? lb : overlaps[b] = []).Add((a, similarity));
            }
        var byName = active.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        var collisions = active.GroupBy(skill => Find(skill.Name)).Where(group => group.Count() > 1).Select(group =>
        {
            var names = group.Select(skill => skill.Name).Order(StringComparer.Ordinal).ToList();
            var max = names.SelectMany(name => overlaps.GetValueOrDefault(name) ?? []).Select(item => item.Similarity).DefaultIfEmpty(0).Max();
            return new TriggerCluster(names, corpus.SharedTerms(names), Math.Round(max, 2), names.All(name => byName[name].IsGlobal));
        }).OrderByDescending(cluster => cluster.Skills.Count).ThenByDescending(cluster => cluster.MaxSimilarity).ToList();

        var missingReferences = new List<MissingReference>();
        var reports = new List<LibrarySkillReport>();
        foreach (var skill in managed.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            var findings = SkillDoctor.Lint(skill).Findings.ToList();
            var missing = SkillTextSignals.MissingReferences(skill, known, families);
            if (missing.Count > 0)
            {
                missingReferences.Add(new(skill.Name, missing));
                findings.Add(SkillDoctor.MissingCompanionFinding(missing));
            }
            if (overlaps.TryGetValue(skill.Name, out var others))
                findings.Add(SkillDoctor.OverlapFinding(others.OrderByDescending(item => item.Similarity).Select(item => item.Other).ToList()));
            reports.Add(new(skill.Name, skill.GetEffectiveTitle(), skill.Scope, ContextBudget.PackOf(skill), SkillDoctor.ScoreOf(findings), findings));
        }

        var budget = ContextBudget.Calculate(managed);
        var average = reports.Count == 0 ? 100 : reports.Average(report => report.Score);
        var penalty = collisions.Sum(cluster => cluster.AllGlobal ? 3 : 1) + budget.State switch { "over" => 15, "warning" => 5, _ => 0 };
        var score = (int)Math.Clamp(Math.Round(average - penalty), 0, 100);
        return new LibraryReport(score, reports.Count,
            reports.Sum(report => report.Findings.Count(finding => finding.Severity == "error")),
            reports.Sum(report => report.Findings.Count(finding => finding.Severity == "warning")),
            budget, collisions, missingReferences, reports.OrderBy(report => report.Score).ThenBy(report => report.Name, StringComparer.Ordinal).ToList());
    }
}

public sealed record TriggerCandidate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("relative")] double Relative,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("matched_terms")] IReadOnlyList<string> MatchedTerms);

public sealed record TriggerTestResult(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("candidates")] IReadOnlyList<TriggerCandidate> Candidates,
    [property: JsonPropertyName("collision")] bool Collision,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("project_only_matches")] IReadOnlyList<string> ProjectOnlyMatches);

/// <summary>
/// Estimates which skills an agent would pick for a prompt by ranking trigger texts (name, description, when to use,
/// tags) with BM25. Agents decide with a language model, so this is a lexical approximation: good at showing ties and
/// skills that never match, not a guarantee.
/// </summary>
public static class TriggerTester
{
    public const double TieRatio = 0.85;

    public static TriggerTestResult Rank(string prompt, IReadOnlyList<CanonicalSkillModel> library, bool includeProjectOnly = false, int take = 5)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Enter a sample prompt.");
        var query = SkillTextSignals.Tokenize(prompt).Distinct(StringComparer.Ordinal).ToList();
        var pool = library.Where(skill => !skill.IsCatalogOnly && skill.Scope != CanonicalSkillModel.ScopeOff).ToList();
        var documents = pool.ToDictionary(skill => skill.Name, skill => SkillTextSignals.Tokenize(
            string.Join(' ', Enumerable.Repeat(skill.Name.Replace('-', ' '), 3)) + " " + skill.Description + " " + skill.WhenToUse + " " +
            string.Join(' ', skill.Tags)), StringComparer.Ordinal);
        var average = documents.Count == 0 ? 1 : Math.Max(1, documents.Values.Average(words => words.Count));
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var words in documents.Values)
            foreach (var word in words.Distinct())
                frequency[word] = frequency.GetValueOrDefault(word) + 1;

        var scored = pool.Select(skill =>
        {
            var words = documents[skill.Name];
            double score = 0;
            var matched = new List<string>();
            foreach (var term in query)
            {
                var count = words.Count(word => word == term);
                if (count == 0) continue;
                matched.Add(term);
                var df = frequency[term];
                var idf = Math.Log(1 + (documents.Count - df + 0.5) / (df + 0.5));
                score += idf * count * 2.2 / (count + 1.2 * (0.25 + 0.75 * words.Count / average));
            }
            return (Skill: skill, Score: score, Matched: matched);
        }).Where(item => item.Score > 0).OrderByDescending(item => item.Score).ThenBy(item => item.Skill.Name, StringComparer.Ordinal).ToList();

        var eligible = scored.Where(item => includeProjectOnly || item.Skill.IsGlobal).ToList();
        var top = eligible.FirstOrDefault().Score;
        var candidates = eligible.Take(take).Select((item, index) =>
        {
            var relative = top <= 0 ? 0 : Math.Round(item.Score / top, 2);
            var verdict = index == 0 ? "likely" : relative >= TieRatio ? "tie" : relative >= 0.5 ? "possible" : "unlikely";
            return new TriggerCandidate(item.Skill.Name, item.Skill.GetEffectiveTitle(), item.Skill.Scope, Math.Round(item.Score, 2), relative, verdict, item.Matched);
        }).ToList();
        var ties = candidates.Where(candidate => candidate.Verdict == "tie").Select(candidate => candidate.Name).ToList();
        if (ties.Count > 0 && candidates.Count > 0) candidates[0] = candidates[0] with { Verdict = "tie" };
        var projectOnly = includeProjectOnly ? [] : scored.Where(item => !item.Skill.IsGlobal && item.Score >= top * TieRatio)
            .Select(item => item.Skill.Name).Take(5).ToList();
        var summary = candidates.Count == 0
            ? "No active skill matches this prompt; the agent would work without a skill."
            : ties.Count > 0
                ? $"Collision: {candidates[0].Name} and {string.Join(", ", ties)} score within {(1 - TieRatio):P0} of each other, so the agent may load any of them."
                : $"{candidates[0].Name} is the clear match.";
        if (projectOnly.Count > 0) summary += $" Project-only skills that would also match inside a project: {string.Join(", ", projectOnly)}.";
        return new TriggerTestResult(prompt.Trim(), candidates, ties.Count > 0, summary, projectOnly);
    }
}
