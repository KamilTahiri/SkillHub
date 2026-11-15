using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Core.Transpilers;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>Explore sources (curated and pinned repositories) and topic inference.</summary>
public sealed partial class SkillImporterService
{
    public static IReadOnlyList<CommunityRepoSummary> GetCuratedRepositories() =>
    [
        new(
            "featured/all",
            "Featured",
            "Top recommended agent skills across Anthropic, Vercel Labs, and your pre-installed local catalog."),
        new(
            "local/catalog",
            "Local Plugin Catalog",
            "Skills from plugin caches on this PC."),
        new(
            SkillsShDirectory.SourceId,
            "skills.sh • Community directory",
            "The open skills.sh directory of public SKILL.md repositories, ranked by installs."),
        new(
            "anthropics/skills",
            "Anthropic • Official Skills",
            "Frontend design, MCP server builder, webapp testing, PDF/DOCX/XLSX document skills, and more."),
        new(
            "vercel-labs/agent-skills",
            "Vercel Labs • Agent Skills",
            "React & Next.js performance patterns, web design guidelines, and Composition patterns."),
        new(
            "github/awesome-copilot",
            "GitHub • Awesome Copilot & Agent Skills",
            "Community-curated instructions, prompts, chat modes, agents and skills.")
    ];

    /// <summary>Curated sources followed by the repositories the user pinned in <c>~/.skillhub/sources.json</c>.</summary>
    public IReadOnlyList<CommunityRepoSummary> GetSources()
    {
        var curated = GetCuratedRepositories();
        var pinned = ReadPinnedSources()
            .Where(repo => !curated.Any(item => item.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
            .Select(repo => new CommunityRepoSummary(repo, repo, "Pinned repository", Pinned: true));
        return [.. curated, .. pinned];
    }

    public async Task<IReadOnlyList<CommunityRepoSummary>> PinSourceAsync(string input, CancellationToken ct = default)
    {
        var (owner, repo, _, _, _) = ParseSelection(new SkillImportSelection(Source: input));
        var key = $"{owner}/{repo}";
        var pinned = ReadPinnedSources();
        if (!pinned.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            if (pinned.Count >= 50) throw new ArgumentException("Unpin a repository before pinning another one.");
            pinned.Add(key);
            await WritePinnedSourcesAsync(pinned, ct).ConfigureAwait(false);
        }
        return GetSources();
    }

    public async Task<IReadOnlyList<CommunityRepoSummary>> UnpinSourceAsync(string input, CancellationToken ct = default)
    {
        var (owner, repo, _, _, _) = ParseSelection(new SkillImportSelection(Source: input));
        var pinned = ReadPinnedSources();
        if (pinned.RemoveAll(item => item.Equals($"{owner}/{repo}", StringComparison.OrdinalIgnoreCase)) > 0)
            await WritePinnedSourcesAsync(pinned, ct).ConfigureAwait(false);
        return GetSources();
    }

    private string SourcesPath => Path.Combine(_engine.Discovery.SkillHubRoot, "sources.json");

    private List<string> ReadPinnedSources()
    {
        try
        {
            if (!File.Exists(SourcesPath)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(SourcesPath));
            return document.RootElement.TryGetProperty("pinned", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(item => item.GetString()).OfType<string>()
                    .Where(item => Regex.IsMatch(item, @"\A[A-Za-z0-9-]{1,100}/[A-Za-z0-9_.-]{1,100}\z")).ToList()
                : [];
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return [];
        }
    }

    private Task WritePinnedSourcesAsync(List<string> pinned, CancellationToken ct) =>
        AtomicFileWriter.WriteTextAtomicallyAsync(SourcesPath,
            JsonSerializer.Serialize(new { pinned }, new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);

    // Keywords match whole words. Keywords longer than three letters also match as a word prefix
    // ("test" matches "testing"), so short keywords such as "ai" or "ci" no longer hit "email" or "decision".
    private static readonly (string Topic, string[] Words)[] TopicKeywords =
    [
        ("testing", ["test", "tests", "playwright", "cypress", "jest", "vitest", "audit", "security", "auth", "oauth", "auth0", "authentication", "qa", "e2e"]),
        ("web", ["react", "nextjs", "vue", "svelte", "angular", "frontend", "css", "tailwind", "design", "ui", "ux", "figma", "canva", "browser", "html", "web"]),
        ("data", ["data", "sql", "postgres", "postgresql", "supabase", "csv", "spreadsheet", "sheets", "xlsx", "analytics", "ai", "ml", "databricks", "lakehouse", "spark", "pandas"]),
        ("devops", ["docker", "kubernetes", "k8s", "terraform", "cloud", "deploy", "deployment", "aws", "azure", "gcp", "vercel", "cloudflare", "railway", "netlify", "ci", "cd", "linux", "infra", "infrastructure"]),
        ("productivity", ["docs", "document", "documents", "pdf", "docx", "pptx", "notion", "linear", "jira", "slack", "mail", "email", "gmail", "calendar", "dropbox", "workflow", "task", "tasks", "todo"])
    ];

    public static string InferTopic(string name, string? desc)
    {
        var tokens = Regex.Matches($"{name} {desc}".ToLowerInvariant(), "[a-z0-9]+").Select(match => match.Value).ToArray();
        foreach (var (topic, words) in TopicKeywords)
        {
            if (tokens.Any(token => words.Any(word => word.Length <= 3 ? token == word : token.StartsWith(word, StringComparison.Ordinal))))
                return topic;
        }
        return "coding";
    }

    /// <summary>True when Explore search text names a repository or skill link rather than a keyword.</summary>
    public static bool LooksLikeRepositoryInput(string? query)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return false;
        if (Regex.IsMatch(q, @"\A(?:https?://|github\.com/|skills\.sh/|(?:npx|bunx|pnpm\s+dlx|skills(?:\.sh)?)\s)", RegexOptions.IgnoreCase))
            return true;
        return Regex.IsMatch(q, @"\A[A-Za-z0-9-]{1,100}/[A-Za-z0-9_.-]{1,100}(?:[/@][^\s]*)?\z");
    }
}
