using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Engine;

public sealed record DoctorFinding(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("line")] int? Line = null);

public sealed record ScriptFinding(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("concerns")] IReadOnlyList<string> Concerns);

public sealed record DoctorReport(
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("findings")] IReadOnlyList<DoctorFinding> Findings,
    [property: JsonPropertyName("scripts")] IReadOnlyList<ScriptFinding> Scripts,
    [property: JsonPropertyName("tokens")] IReadOnlyDictionary<string, TokenBudget> Tokens);

public sealed record TokenBudget(
    [property: JsonPropertyName("used")] int Used,
    [property: JsonPropertyName("budget")] int Budget,
    [property: JsonPropertyName("label")] string Label);

/// <summary>
/// Skill Doctor: checks a skill for provider compliance, broken bundle links, overlapping triggers with other skills,
/// leaked secrets and executable content. It never changes the skill.
/// </summary>
public static class SkillDoctor
{
    public const int DescriptionLimit = 1024;
    public const int NameLimit = 64;
    private const int BodyLineGuide = 500;

    // Approximate per-provider budgets for what is always loaded (frontmatter description) and for the body.
    public static readonly IReadOnlyDictionary<string, (int Budget, string Label)> TokenBudgets = new Dictionary<string, (int, string)>
    {
        ["description"] = (256, "Description (always in context)"),
        ["body"] = (5000, "Instructions (loaded when triggered)")
    };

    private static readonly (string Name, Regex Pattern)[] SecretPatterns =
    [
        ("OpenAI or Anthropic API key", new Regex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{24,}", RegexOptions.Compiled)),
        ("GitHub token", new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}", RegexOptions.Compiled)),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.Compiled)),
        ("AWS access key", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled)),
        ("Stripe secret key", new Regex(@"\b[rs]k_live_[A-Za-z0-9]{20,}", RegexOptions.Compiled)),
        ("Private key", new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", RegexOptions.Compiled))
    ];

    private static readonly Dictionary<string, string> ScriptLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "Python", [".sh"] = "Shell", [".bash"] = "Shell", [".zsh"] = "Shell", [".ps1"] = "PowerShell", [".psm1"] = "PowerShell",
        [".bat"] = "Batch", [".cmd"] = "Batch", [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript",
        [".ts"] = "TypeScript", [".rb"] = "Ruby", [".pl"] = "Perl", [".php"] = "PHP", [".go"] = "Go", [".rs"] = "Rust", [".lua"] = "Lua"
    };

    private static readonly (string Concern, Regex Pattern)[] RiskyPatterns =
    [
        ("Downloads and runs remote code", new Regex(@"(curl|wget|iwr|Invoke-WebRequest)[^\n|]{0,200}\|\s*(sh|bash|zsh|python\d?|iex|Invoke-Expression|pwsh|powershell)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Evaluates dynamic code", new Regex(@"\b(Invoke-Expression|iex\s*\(|eval\s*\(|exec\s*\(|new Function\s*\()", RegexOptions.Compiled)),
        ("Encoded command", new Regex(@"-(enc|EncodedCommand)\s+[A-Za-z0-9+/=]{20,}|base64\s+(-d|--decode)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Deletes files recursively", new Regex(@"\brm\s+-[a-z]*r[a-z]*f|\bRemove-Item\b[^\n]*-Recurse|\brmdir\s+/s\b|shutil\.rmtree", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Elevates privileges", new Regex(@"\bsudo\b|\bRunAs\b|-Verb\s+RunAs|\bchmod\s+\+s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Reads credential stores", new Regex(@"\.ssh[\\/]|\.aws[\\/]credentials|\.netrc|\bKeychain\b|Credential Manager|id_rsa", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Sends data over the network", new Regex(@"\b(requests\.(post|put)|fetch\(|curl\s+[^\n]*-d\b|Invoke-RestMethod|urllib\.request|axios\.(post|put))", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    private static readonly Regex InlineCode = new(@"(`+)[^`]*?\1", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink =new(@"\[[^\]\n]*\]\((?!https?:|mailto:|#|/)<?([^)\s>]+)>?(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
    private static readonly Regex ShellFence = new(@"^\s*(```|~~~)\s*(bash|sh|shell|zsh|powershell|pwsh|ps1|cmd|bat|console)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    public static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    /// <summary>Lists executable files in a bundle and the risky patterns found in each.</summary>
    public static IReadOnlyList<ScriptFinding> InspectScripts(IReadOnlyDictionary<string, string?> files, string? instructions = null)
    {
        var results = new List<ScriptFinding>();
        foreach (var (path, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ScriptLanguages.TryGetValue(Path.GetExtension(path), out var language)) continue;
            var concerns = content is null ? [] : RiskyPatterns.Where(item => item.Pattern.IsMatch(content)).Select(item => item.Concern).ToList();
            results.Add(new ScriptFinding(path, language, concerns));
        }
        if (!string.IsNullOrEmpty(instructions) && ShellFence.IsMatch(instructions))
        {
            var concerns = RiskyPatterns.Where(item => item.Pattern.IsMatch(instructions)).Select(item => item.Concern).ToList();
            results.Insert(0, new ScriptFinding("SKILL.md", "Shell commands in instructions", concerns));
        }
        return results;
    }

    public static DoctorReport Lint(CanonicalSkillModel skill, IEnumerable<CanonicalSkillModel>? others = null)
    {
        var findings = new List<DoctorFinding>();

        // 1. Provider frontmatter compliance and budgets.
        if (string.IsNullOrWhiteSpace(skill.Name) || !Regex.IsMatch(skill.Name, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z"))
            findings.Add(new("name-format", "error", "Name must use lowercase letters, digits and single hyphens (Claude, Codex and Copilot reject other names)."));
        if (skill.Name.Length > NameLimit)
            findings.Add(new("name-length", "error", $"Name is {skill.Name.Length} characters; providers allow at most {NameLimit}."));
        if (string.IsNullOrWhiteSpace(skill.Description))
            findings.Add(new("description-missing", "error", "Description is empty. Agents use it to decide when to load the skill."));
        else
        {
            if (skill.Description.Length > DescriptionLimit)
                findings.Add(new("description-length", "error", $"Description is {skill.Description.Length} characters; Claude and Codex truncate or reject descriptions over {DescriptionLimit}."));
            else if (skill.Description.Trim().Length + 1 + (skill.WhenToUse ?? string.Empty).Trim().Length > DescriptionLimit && !string.IsNullOrWhiteSpace(skill.WhenToUse))
                findings.Add(new("description-combined-length", "error", $"Description and “When to use” together are {skill.Description.Trim().Length + 1 + skill.WhenToUse.Trim().Length} characters. Codex, Gemini, Copilot and Windsurf receive them as one description and truncate or reject it over {DescriptionLimit}."));
            else if (skill.Description.Length < 40)
                findings.Add(new("description-short", "warning", "Description is very short. Say what the skill does and when to use it so agents trigger it reliably."));
            if (!Regex.IsMatch(skill.Description + " " + skill.WhenToUse, @"\b(use|when|whenever|if)\b", RegexOptions.IgnoreCase))
                findings.Add(new("description-trigger", "info", "Neither the description nor “When to use” says when to use the skill (for example “Use when …”)."));
            if (skill.Description.Contains('<') || skill.Description.Contains('>'))
                findings.Add(new("description-angle", "warning", "Description contains angle brackets, which some providers strip from frontmatter."));
        }
        if (skill.Targets.Cursor.Enabled && skill.Targets.Cursor.Globs.Count == 0 && !skill.Targets.Cursor.AlwaysApply)
            findings.Add(new("cursor-globs", "warning", "Cursor is enabled but the rule has no globs and is not always applied, so Cursor never attaches it automatically."));
        if (skill.Targets.Cursor.Enabled && skill.SupportingFiles.Count > 0)
            findings.Add(new("cursor-bundle", "info", $"Cursor receives the main file only; the {skill.SupportingFiles.Count} bundled files stay with the other providers."));

        var lines = (skill.InstructionsMarkdown ?? string.Empty).Split('\n').Length;
        if (lines > BodyLineGuide)
            findings.Add(new("body-length", "warning", $"Instructions are {lines} lines. Keep SKILL.md under {BodyLineGuide} lines and move detail into bundled reference files."));
        if (string.IsNullOrWhiteSpace(skill.InstructionsMarkdown))
            findings.Add(new("body-empty", "error", "The skill has no instructions."));

        // 2. Broken relative links to bundle files.
        var available = new HashSet<string>(skill.SupportingFiles.Keys, StringComparer.OrdinalIgnoreCase) { "SKILL.md" };
        void CheckLinks(string file, string text)
        {
            var directory = file.Contains('/') ? file[..file.LastIndexOf('/')] : string.Empty;
            var lineNumber = 0;
            var inFence = false;
            foreach (var raw in text.Split('\n'))
            {
                lineNumber++;
                var trimmed = raw.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;
                // Link syntax shown as an example inside inline code is not a link.
                var line = InlineCode.Replace(raw, string.Empty);
                foreach (Match match in MarkdownLink.Matches(line))
                {
                    var target = Uri.UnescapeDataString(match.Groups[1].Value.Split('#')[0].Split('?')[0]);
                    if (target.Length == 0) continue;
                    var resolved = Resolve(directory, target);
                    if (resolved is null || !available.Contains(resolved) && !available.Any(path => path.StartsWith(resolved + "/", StringComparison.OrdinalIgnoreCase)))
                        findings.Add(new("broken-link", "error", $"Link to “{target}” points to a file that is not in the bundle.", file, lineNumber));
                }
            }
        }
        CheckLinks("SKILL.md", skill.InstructionsMarkdown ?? string.Empty);
        foreach (var (path, content) in skill.SupportingFiles.Where(pair => pair.Key.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            CheckLinks(path, content);

        // 3. Triggers that overlap with other active skills, and references to companion skills that are not installed.
        if (others is not null)
        {
            var library = others.Where(other => other.Name != skill.Name).ToList();
            var active = library.Where(other => !other.IsCatalogOnly && other.SourceCategory != "system" && other.Scope != CanonicalSkillModel.ScopeOff)
                .Append(skill).ToList();
            var corpus = new SkillTextSignals.Corpus(active);
            var overlapping = active.Where(other => other.Name != skill.Name)
                .Select(other => (other.Name, Overlaps: SkillTextSignals.Overlaps(corpus, skill.Name, other.Name, out var similarity), Similarity: similarity))
                .Where(item => item.Overlaps).OrderByDescending(item => item.Similarity).Select(item => item.Name).ToList();
            if (overlapping.Count > 0) findings.Add(OverlapFinding(overlapping));
            var known = library.Select(other => other.Name).Append(skill.Name).ToHashSet(StringComparer.Ordinal);
            var families = SkillTextSignals.Families(library.Where(other => !other.IsCatalogOnly).Select(other => other.Name).Append(skill.Name));
            var missing = SkillTextSignals.MissingReferences(skill, known, families);
            if (missing.Count > 0) findings.Add(MissingCompanionFinding(missing));
        }

        // 4. Secrets in instructions or bundle files.
        void CheckSecrets(string file, string text)
        {
            foreach (var (name, pattern) in SecretPatterns)
            {
                var match = pattern.Match(text);
                if (!match.Success) continue;
                var line = text[..match.Index].Count(character => character == '\n') + 1;
                findings.Add(new("secret", "error", $"Possible {name} found. Remove it before syncing; skills are copied into several folders.", file, line));
            }
        }
        CheckSecrets("SKILL.md", skill.InstructionsMarkdown + "\n" + skill.Description + "\n" + skill.WhenToUse);
        foreach (var (path, content) in skill.SupportingFiles) CheckSecrets(path, content);

        // 5. Executable content.
        var scripts = InspectScripts(skill.SupportingFiles.ToDictionary(pair => pair.Key, pair => (string?)pair.Value), skill.InstructionsMarkdown);
        foreach (var script in scripts.Where(script => script.Concerns.Count > 0))
            findings.Add(new("script-risk", "warning", $"{script.Path}: {string.Join(", ", script.Concerns)}.", script.Path));

        var tokens = new Dictionary<string, TokenBudget>
        {
            ["description"] = new(EstimateTokens(skill.Description + " " + skill.WhenToUse), TokenBudgets["description"].Budget, TokenBudgets["description"].Label),
            ["body"] = new(EstimateTokens(skill.InstructionsMarkdown), TokenBudgets["body"].Budget, TokenBudgets["body"].Label)
        };
        if (tokens["description"].Used > tokens["description"].Budget)
            findings.Add(new("description-tokens", "warning", $"Description and “When to use” take about {tokens["description"].Used} tokens of every conversation. Aim for under {tokens["description"].Budget}."));
        if (tokens["body"].Used > tokens["body"].Budget)
            findings.Add(new("body-tokens", "warning", $"Instructions are about {tokens["body"].Used} tokens. Move reference material into bundle files the agent reads on demand."));

        return new DoctorReport(ScoreOf(findings), findings, scripts, tokens);
    }

    public static int ScoreOf(IEnumerable<DoctorFinding> findings) =>
        Math.Max(0, 100 - findings.Sum(finding => finding.Severity switch { "error" => 25, "warning" => 8, _ => 2 }));

    public static DoctorFinding OverlapFinding(IReadOnlyList<string> others) => new("trigger-overlap", "warning",
        $"Its trigger competes with {string.Join(", ", others.Take(5))}{(others.Count > 5 ? $" and {others.Count - 5} more" : string.Empty)}; agents may load the wrong one. Narrow the descriptions, merge them, or make the niche ones Project-only.");

    public static DoctorFinding MissingCompanionFinding(IReadOnlyList<string> missing) => new("missing-companion", "warning",
        $"It refers to {missing.Count} skill{(missing.Count == 1 ? string.Empty : "s")} that are not in your library: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", …" : string.Empty)}. Import them from the same pack or remove the references.");

    private static string? Resolve(string directory, string target)
    {
        var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
        foreach (var segment in target.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

}
