using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Core.Transpilers;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed record SkillImportRequest(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("raw_markdown")] string? RawMarkdown,
    [property: JsonPropertyName("file_name")] string? FileName
);

public sealed record SkillImportResult(
    int ImportedCount,
    IReadOnlyList<CanonicalSkillModel> Skills,
    string Message
);

public sealed record CommunityRepoSummary(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("pinned")] bool Pinned = false
);

public sealed record CommunitySkillSummary(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("git_ref")] string GitRef,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("is_installed")] bool IsInstalled,
    [property: JsonPropertyName("topic")] string Topic = "coding",
    [property: JsonPropertyName("existing_name")] string? ExistingName = null,
    [property: JsonPropertyName("kind")] string Kind = "skill",
    [property: JsonPropertyName("group")] string Group = "skills",
    [property: JsonPropertyName("file_count")] int FileCount = 0,
    [property: JsonPropertyName("has_frontmatter")] bool HasFrontmatter = true,
    [property: JsonPropertyName("installs")] long? Installs = null,
    [property: JsonIgnore] int SearchScore = 0
);

/// <summary>Context for a repository view in Explore: what was indexed, its README and how documents are grouped.</summary>
public sealed record CommunityRepositoryInfo(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("readme_path")] string? ReadmePath,
    [property: JsonPropertyName("readme_excerpt")] string? ReadmeExcerpt,
    [property: JsonPropertyName("license_path")] string? LicensePath,
    [property: JsonPropertyName("counts")] IReadOnlyDictionary<string, int> Counts,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("pinned")] bool Pinned,
    [property: JsonPropertyName("url")] string Url);

public sealed record CommunityBrowseResult(
    IReadOnlyList<CommunitySkillSummary> Skills,
    CommunityRepositoryInfo? Repository,
    IReadOnlyList<string>? PendingSources = null);

public sealed record CommunitySkillPreview(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("when_to_use")] string WhenToUse,
    [property: JsonPropertyName("instructions_markdown")] string InstructionsMarkdown,
    [property: JsonPropertyName("is_installed")] bool IsInstalled,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("supporting_paths")] IReadOnlyList<string> SupportingPaths,
    [property: JsonPropertyName("name_exists")] bool NameExists,
    [property: JsonPropertyName("existing_name")] string? ExistingName,
    [property: JsonPropertyName("kind")] string Kind = "skill",
    [property: JsonPropertyName("excluded_paths")] IReadOnlyList<string>? ExcludedPaths = null,
    [property: JsonPropertyName("has_frontmatter")] bool HasFrontmatter = true
);

public sealed partial class SkillImporterService
{
    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly HttpClient _http;
    private readonly RepositoryIndexer _indexer;
    private readonly SkillsShDirectory _skillsSh;

    private readonly SyncEngine _engine;
    private readonly CodexTranspiler _codexTranspiler = new();
    private readonly CanonicalTranspiler _canonicalTranspiler = new();
    private readonly CursorMdcTranspiler _cursorTranspiler = new();
    private readonly ClaudeTranspiler _claudeTranspiler = new();

    public SkillImporterService(SyncEngine engine, HttpClient? remote = null)
    {
        _engine = engine;
        _http = remote ?? SharedHttp;
        _indexer = new RepositoryIndexer(_http, Path.Combine(engine.Discovery.SkillHubRoot, "cache", "github"));
        _skillsSh = new SkillsShDirectory(_http);
    }

    public RepositoryIndexer Indexer => _indexer;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SkillHub", ProductVersionText()));
        return client;
    }

    private static string ProductVersionText() =>
        typeof(SkillImporterService).Assembly.GetName().Version?.ToString(3) ?? "1.0";

    internal static TimeSpan SearchIndexWait { get; set; } = TimeSpan.FromSeconds(8);

    private static readonly string[] CuratedSearchRepositories = ["anthropics/skills", "vercel-labs/agent-skills", "github/awesome-copilot"];

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

    public async Task<IReadOnlyList<CommunitySkillSummary>> BrowseRepositorySkillsAsync(
        string repoInput,
        string? filterQuery = null,
        CancellationToken ct = default) =>
        (await BrowseRepositoryAsync(repoInput, filterQuery, ct).ConfigureAwait(false)).Skills;

    public async Task<CommunityBrowseResult> BrowseRepositoryAsync(string repoInput, string? filterQuery = null, CancellationToken ct = default)
    {
        if (string.Equals(repoInput, "local/catalog", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(repoInput, "local", StringComparison.OrdinalIgnoreCase))
        {
            return new(FilterLocal(LocalCatalogSummaries(), filterQuery)
                .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }

        if (string.Equals(repoInput, SkillsShDirectory.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            var entries = string.IsNullOrWhiteSpace(filterQuery) || filterQuery.Trim().Length < 2
                ? await _skillsSh.PopularAsync(ct).ConfigureAwait(false)
                : await _skillsSh.SearchAsync(filterQuery, 60, ct).ConfigureAwait(false);
            return new(entries.Select(FromSkillsSh).ToList(), null);
        }

        if (string.Equals(repoInput, "featured/all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(repoInput, "all", StringComparison.OrdinalIgnoreCase))
        {
            var highlights = new List<CommunitySkillSummary>(GetFallbackCuratedSkills(string.Empty));
            var popularKeywords = new[] { "playwright", "stripe", "supabase", "cloudflare", "docker", "figma", "notion", "github", "react", "sql", "git" };
            highlights.AddRange(LocalCatalogSummaries().Where(summary => popularKeywords.Any(keyword =>
                summary.Slug.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                summary.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))).Take(25));
            return new(FilterLocal(highlights, filterQuery).ToList(), null);
        }

        var cleaned = NormalizeCliAndSkillsShInput(repoInput, out var skillFilter);
        var (owner, repo, sha, hint) = await ResolveSelectionAsync(new SkillImportSelection(Source: cleaned), ct).ConfigureAwait(false);
        var fullRepo = $"{owner}/{repo}";
        var index = await _indexer.GetIndexAsync(owner, repo, sha, ct).ConfigureAwait(false);
        // Skills first, then commands, rules and agents; documentation last.
        var rows = index.Documents.Select(document => Summarize(fullRepo, sha, document))
            .OrderBy(row => GroupOrder(row.Group)).ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ToList();

        var query = filterQuery?.Trim();
        if (string.IsNullOrEmpty(query)) query = skillFilter;
        IEnumerable<CommunitySkillSummary> visible = rows;
        if (!string.IsNullOrWhiteSpace(hint) && string.IsNullOrWhiteSpace(filterQuery))
        {
            // A link to a folder or file narrows the listing to that part of the repository.
            var scope = hint.Trim('/');
            var scoped = rows.Where(row => row.Path.Equals(scope, StringComparison.OrdinalIgnoreCase) ||
                row.Path.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase) ||
                row.Slug.Equals(ProviderDiscoveryService.NormalizeSlug(scope), StringComparison.Ordinal)).ToList();
            if (scoped.Count > 0) visible = scoped;
        }
        if (!string.IsNullOrWhiteSpace(query) && !LooksLikeRepositoryInput(query))
        {
            var candidates = visible.ToList();
            var ranked = Rank(candidates, query).ToList();
            // Then look inside cached document bodies. This reads local files only and never calls GitHub.
            var rankedPaths = ranked.Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
            var bodyMatches = candidates.Where(row => !rankedPaths.Contains(row.Path)).Where(row =>
                _indexer.TryReadCachedText(index, row.Path) is { } body && body.Contains(query, StringComparison.OrdinalIgnoreCase));
            visible = ranked.Concat(bodyMatches);
        }

        var pinned = ReadPinnedSources().Contains(fullRepo, StringComparer.OrdinalIgnoreCase) ||
            GetCuratedRepositories().Any(item => item.Repo.Equals(fullRepo, StringComparison.OrdinalIgnoreCase));
        var counts = rows.GroupBy(row => row.Group).ToDictionary(group => group.Key, group => group.Count());
        var info = new CommunityRepositoryInfo(fullRepo, sha, index.Mode, index.ReadmePath, index.ReadmeExcerpt, index.LicensePath,
            counts, index.Truncated, pinned, $"https://github.com/{fullRepo}");
        return new(visible.ToList(), info);
    }

    /// <summary>
    /// A skills.sh entry knows its repository and skill id but not the file path. The path field carries the skill id,
    /// which <see cref="SelectDocument"/> resolves against the indexed repository when the skill is opened.
    /// </summary>
    private CommunitySkillSummary FromSkillsSh(SkillsShEntry entry)
    {
        var slug = ProviderDiscoveryService.NormalizeSlug(entry.Name);
        if (slug.Length == 0) slug = ProviderDiscoveryService.NormalizeSlug(entry.SkillId);
        var existing = _engine.ListSkills().FirstOrDefault(skill => !skill.IsCatalogOnly &&
            string.Equals(skill.Passthrough.GetValueOrDefault("import_source")?.ToString(), entry.Source, StringComparison.OrdinalIgnoreCase) &&
            (skill.Name == slug || Compact(skill.Name) == Compact(entry.SkillId)))?.Name;
        return new CommunitySkillSummary(slug, CanonicalSkillModel.HumanizeSlug(slug), entry.Source, "HEAD", entry.SkillId,
            SkillsShDirectory.SourceId, $"{entry.Installs:N0} installs on skills.sh · {entry.Source}", existing is not null,
            InferTopic(slug, entry.Source), existing, Installs: entry.Installs);
    }

    private static string Compact(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", string.Empty);

    private static int GroupOrder(string group) => group switch
    {
        "skills" => 0, "commands" => 1, "rules" => 2, "agents" => 3, _ => 4
    };

    private CommunitySkillSummary Summarize(string fullRepo, string sha, IndexedDocument document)
    {
        var existingName = _engine.FindImportedSkill(fullRepo, document.Path);
        return new CommunitySkillSummary(document.Name, document.Title, fullRepo, sha, document.Path, fullRepo,
            document.Description, existingName is not null, InferTopic(document.Name, document.Description), existingName,
            document.Kind, document.Group, document.Files.Count, document.HasFrontmatter);
    }

    /// <summary>Orders matches by where the query appears: title, then name, then description, then path.</summary>
    private static IEnumerable<CommunitySkillSummary> Rank(IEnumerable<CommunitySkillSummary> rows, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return rows.Select(row =>
            {
                var score = 0;
                foreach (var term in terms)
                {
                    var best = row.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ? 8
                        : row.Slug.Contains(term, StringComparison.OrdinalIgnoreCase) ? 6
                        : row.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ? 3
                        : row.Path.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Kind.Equals(term, StringComparison.OrdinalIgnoreCase) ||
                          row.Topic.Equals(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    if (best == 0) return (Row: row, Score: -1);
                    score += best;
                }
                if (row.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 4;
                if (row.Group == "docs") score -= 6;
                else if (row.Group == "skills") score += 3;
                return (Row: row, Score: score);
            })
            .Where(item => item.Score >= 0).OrderByDescending(item => item.Score)
            .ThenBy(item => item.Row.Title, StringComparer.OrdinalIgnoreCase).Select(item => item.Row with { SearchScore = Math.Max(item.Score, 1) });
    }

    private static IEnumerable<CommunitySkillSummary> FilterLocal(IEnumerable<CommunitySkillSummary> rows, string? filterQuery)
    {
        if (string.IsNullOrWhiteSpace(filterQuery)) return rows;
        var q = filterQuery.Trim();
        return rows.Where(s =>
            s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Slug.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Topic.Equals(q, StringComparison.OrdinalIgnoreCase) ||
            s.Repo.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<CommunitySkillSummary> LocalCatalogSummaries() =>
        _engine.ListSkills(category: "plugin").Select(p =>
        {
            var existingName = _engine.FindManagedCatalogCopy(p.CatalogId!);
            return new CommunitySkillSummary(
                Slug: p.Name,
                Title: p.GetEffectiveTitle(),
                Repo: "local/catalog",
                GitRef: "LOCAL",
                Path: $"plugins/{p.CatalogId}/SKILL.md",
                Source: $"local/catalog/{p.CatalogId}",
                Description: p.Description ?? $"Offline catalog skill: {p.GetEffectiveTitle()}",
                IsInstalled: existingName is not null,
                ExistingName: existingName,
                Topic: InferTopic(p.Name, p.Description),
                FileCount: p.SupportingFiles.Count);
        });

    public async Task<IReadOnlyList<CommunitySkillSummary>> SearchCommunitySkillsAsync(
        string? query,
        CancellationToken ct = default) =>
        (await SearchCommunityAsync(query, ct).ConfigureAwait(false)).Skills;

    public async Task<CommunityBrowseResult> SearchCommunityAsync(string? query, CancellationToken ct = default)
    {
        var q = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            return await BrowseRepositoryAsync("featured/all", null, ct).ConfigureAwait(false);
        }

        if (LooksLikeRepositoryInput(q))
        {
            return await BrowseRepositoryAsync(q, null, ct).ConfigureAwait(false);
        }

        var pending = new System.Collections.Concurrent.ConcurrentBag<string>();
        async Task<IReadOnlyList<CommunitySkillSummary>> SearchSkillsShAsync()
        {
            try { return (await _skillsSh.SearchAsync(q, 40, ct).ConfigureAwait(false)).Select(FromSkillsSh).ToArray(); }
            catch (Exception error) when (error is CommunitySourceException or InvalidDataException or JsonException) { return []; }
        }
        async Task<(IReadOnlyList<CommunitySkillSummary> Skills, CommunitySourceException? Error)> SearchAsync(string source)
        {
            try
            {
                var browse = BrowseRepositoryAsync(source, q, ct);
                // A repository that is being indexed for the first time keeps indexing in the background.
                // The search answers with what is ready instead of waiting for a large download.
                if (source != "local/catalog" && await Task.WhenAny(browse, Task.Delay(SearchIndexWait, ct)).ConfigureAwait(false) != browse)
                {
                    pending.Add(source);
                    _ = browse.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
                    return ([], null);
                }
                return ((await browse.ConfigureAwait(false)).Skills, null);
            }
            catch (CommunitySourceException exception) { return ([], exception); }
            catch (InvalidDataException) { return ([], null); }
        }

        var sources = new[] { "local/catalog" }.Concat(CuratedSearchRepositories).Concat(ReadPinnedSources())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var directory = SearchSkillsShAsync();
        var results = await Task.WhenAll(sources.Select(SearchAsync)).ConfigureAwait(false);
        var indexed = results.SelectMany(result => result.Skills.Take(60)).ToList();
        var listed = (await directory.ConfigureAwait(false))
            .Where(entry => !indexed.Any(row => row.Repo.Equals(entry.Repo, StringComparison.OrdinalIgnoreCase) &&
                (row.Slug == entry.Slug || Compact(row.Slug) == Compact(entry.Path))));
        // Metadata matches from every source rank together, then skills.sh listings, then matches found only inside a
        // document body, and documentation last.
        var strong = indexed.Where(row => row.Group != "docs" && row.SearchScore > 0).OrderByDescending(row => row.SearchScore);
        var bodyOnly = indexed.Where(row => row.Group != "docs" && row.SearchScore == 0);
        var combined = strong.Concat(listed).Concat(bodyOnly).Concat(indexed.Where(row => row.Group == "docs"))
            .DistinctBy(skill => (skill.Repo.ToLowerInvariant(), skill.Path)).ToArray();
        if (combined.Length == 0 && pending.IsEmpty && results.FirstOrDefault(result => result.Error is not null).Error is { } failure)
            throw failure;
        return new(combined, null, pending.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<CommunitySkillPreview> PreviewCommunitySkillAsync(string repoInput, string path, string gitRef = "HEAD",
        CancellationToken ct = default)
    {
        if (repoInput == "local/catalog")
        {
            var catalog = GetImportableCatalogSkill(null, path).Clone();
            catalog.IsCatalogOnly = false;
            catalog.SourceCategory = "user";
            var catalogExisting = _engine.FindManagedCatalogCopy(catalog.CatalogId!);
            var exists = ImportNameExists(catalog.Name);
            return new CommunitySkillPreview(catalog.Name, catalog.GetEffectiveTitle(), repoInput, path,
                $"Local catalog · {catalog.OriginProvider}", catalog.Description, catalog.WhenToUse, catalog.InstructionsMarkdown,
                exists || catalogExisting is not null, ContentHasher.ComputeSemanticHash(catalog),
                catalog.Passthrough.GetValueOrDefault("license")?.ToString() ?? UnknownLicense,
                catalog.SupportingFiles.Keys.Order(StringComparer.Ordinal).ToArray(), exists, catalogExisting);
        }

        var (owner, repo, sha, _) = await ResolveSelectionAsync(new SkillImportSelection(Repo: repoInput, GitRef: gitRef), ct).ConfigureAwait(false);
        var fullRepo = $"{owner}/{repo}";
        var index = await _indexer.GetIndexAsync(owner, repo, sha, ct).ConfigureAwait(false);
        var document = SelectDocument(index, path);
        var raw = await _indexer.ReadTextAsync(index, document.Path, ct).ConfigureAwait(false);
        var model = ParseMarkdownToSkillModel(raw, SkillDocumentClassifier.FallbackName(repo, document.Path), $"GitHub ({fullRepo})");
        var existingName = _engine.FindImportedSkill(fullRepo, document.Path);
        var nameExists = ImportNameExists(model.Name);
        return new CommunitySkillPreview(model.Name, model.GetEffectiveTitle(), fullRepo, document.Path, fullRepo,
            model.Description, model.WhenToUse, model.InstructionsMarkdown, nameExists || existingName is not null,
            sha, License(index, document, raw), document.Files, nameExists, existingName,
            document.Kind, document.ExcludedFiles, document.HasFrontmatter);
    }

    private static string License(RepositoryIndex index, IndexedDocument document, string raw)
    {
        var declared = ReadDeclaredLicense(raw);
        if (declared is not null) return declared;
        var sibling = document.Files.FirstOrDefault(file => Regex.IsMatch(file, @"\A(?:LICENSE|LICENCE)(?:\.[A-Za-z]+)?\z", RegexOptions.IgnoreCase));
        return sibling is not null ? $"License file present · {RepositoryIndexer.SkillDirectoryPrefix(document.Path)}{sibling}"
            : index.LicensePath is not null ? $"License file present · {index.LicensePath}" : UnknownLicense;
    }

    /// <summary>Finds the indexed document for a path, a skill folder, or a skill name.</summary>
    private static IndexedDocument SelectDocument(RepositoryIndex index, string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            var skills = index.Documents.Where(document => document.Group == "skills").ToArray();
            return skills.Length == 1 ? skills[0]
                : index.Documents.Count == 1 ? index.Documents[0]
                : index.Documents.Count == 0 ? throw new KeyNotFoundException("No skill matches that repository and path.")
                : throw new ArgumentException("This repository contains several skills. Choose a result in Explore or paste a link to a specific skill.");
        }
        var path = SkillPaths.NormalizeRelative(hint.Trim().TrimEnd('/'));
        var exact = index.Documents.FirstOrDefault(document => document.Path == path ||
            document.Path.Equals(path + "/SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var slug = ProviderDiscoveryService.NormalizeSlug(path.Split('/')[^1]);
        var compact = Compact(path.Split('/')[^1]);
        var matches = index.Documents.Where(document => document.Name == slug ||
            ProviderDiscoveryService.NormalizeSlug(SkillDocumentClassifier.FallbackName(index.Repo, document.Path)) == slug).ToArray();
        if (matches.Length == 0)
            matches = index.Documents.Where(document => Compact(document.Name) == compact ||
                Compact(SkillDocumentClassifier.FallbackName(index.Repo, document.Path)) == compact).ToArray();
        if (matches.Length > 1) matches = matches.Where(document => document.Group == "skills").DefaultIfEmpty(matches[0]).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException("No skill matches that repository and path."),
            _ => throw new ArgumentException("This repository contains several skills with that name. Choose a result in Explore.")
        };
    }

    private IEnumerable<CommunitySkillSummary> GetFallbackCuratedSkills(string fullRepo)
    {
        var items = new List<(string Repo, string Slug, string Title, string Path, string Desc)>
        {
            ("vercel-labs/agent-skills", "react-best-practices", "React & Next.js Best Practices", "skills/react-best-practices/SKILL.md", "React and Next.js performance optimization guidelines from Vercel Engineering."),
            ("vercel-labs/agent-skills", "web-design-guidelines", "Web Design Guidelines", "skills/web-design-guidelines/SKILL.md", "Review UI code for Web Interface Guidelines, accessibility, and modern visual polish."),
            ("vercel-labs/agent-skills", "composition-patterns", "React Composition Patterns", "skills/composition-patterns/SKILL.md", "Modern component composition patterns and state architecture."),
            ("anthropics/skills", "frontend-design", "Frontend Design", "skills/frontend-design/SKILL.md", "Create distinctive, production-grade frontend interfaces with high design quality."),
            ("anthropics/skills", "mcp-builder", "MCP Server Builder", "skills/mcp-builder/SKILL.md", "Guide for creating high-quality Model Context Protocol (MCP) servers in TypeScript or Python."),
            ("anthropics/skills", "webapp-testing", "Web Application Testing", "skills/webapp-testing/SKILL.md", "Toolkit for interacting with and testing local web applications using Playwright."),
            ("anthropics/skills", "skill-creator", "Anthropic Skill Creator", "skills/skill-creator/SKILL.md", "Guide for creating effective, modular AI agent skills with progressive disclosure."),
            ("anthropics/skills", "pdf", "PDF Processing & Generation", "skills/pdf/SKILL.md", "Comprehensive PDF manipulation toolkit for extracting text, tables, and generating PDFs."),
            ("anthropics/skills", "docx", "DOCX Document Creation & Editing", "skills/docx/SKILL.md", "Create, edit, and analyze Word documents with tracked changes and formatting.")
        };

        return items
            .Where(x => string.IsNullOrWhiteSpace(fullRepo) || string.Equals(x.Repo, fullRepo, StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var dirPrefix = x.Path[..x.Path.LastIndexOf('/')];
                var existing = _engine.FindImportedSkill(x.Repo, x.Path);
                return new CommunitySkillSummary(
                    Slug: x.Slug,
                    Title: x.Title,
                    Repo: x.Repo,
                    GitRef: "HEAD",
                    Path: x.Path,
                    Source: $"{x.Repo}/{dirPrefix}",
                    Description: x.Desc,
                    IsInstalled: existing is not null,
                    ExistingName: existing,
                    Topic: InferTopic(x.Slug, x.Desc));
            });
    }

    public async Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken ct = default)
    {
        // 1. Local file upload / raw Markdown content
        if (!string.IsNullOrWhiteSpace(request.RawMarkdown))
        {
            var skill = ParseMarkdownToSkillModel(
                request.RawMarkdown!,
                request.FileName ?? "imported-skill.md",
                originProvider: "Imported File");

            var saved = await _engine.CreateSkillAsync(skill, "webui:import-file", ct).ConfigureAwait(false);
            return new SkillImportResult(
                1,
                [saved],
                $"Imported '{saved.GetEffectiveTitle()}' and synced across your AI providers.");
        }

        // 2. Local Catalog Skill 1-Click Import
        var rawSource = (request.Source ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(rawSource) &&
            (rawSource.StartsWith("local/catalog/", StringComparison.OrdinalIgnoreCase) ||
             rawSource.StartsWith("local/", StringComparison.OrdinalIgnoreCase)))
        {
            var slug = rawSource[(rawSource.LastIndexOf('/') + 1)..].Trim();
            var saved = await _engine.MaterializeCatalogSkillAsync(slug, null, activate: false, "webui:import-catalog", ct).ConfigureAwait(false);
            return new SkillImportResult(
                1,
                [saved],
                $"Imported '{saved.GetEffectiveTitle()}' from Local Catalog into My Skills as an inactive copy.");
        }

        // 3. GitHub URL or skills.sh shorthand
        if (string.IsNullOrWhiteSpace(rawSource))
        {
            throw new InvalidOperationException("Please provide a GitHub repository, skills.sh shorthand, or skill file.");
        }

        return await ImportFromRemoteSourceAsync(rawSource, ct).ConfigureAwait(false);
    }

    private async Task<SkillImportResult> ImportFromRemoteSourceAsync(string rawSource, CancellationToken ct)
    {
        var plan = await PrepareImportAsync(new SkillImportSelection(Source: rawSource), ct).ConfigureAwait(false);
        var saved = await ApplyImportAsync(plan.PlanId, new SkillImportApplyRequest(plan.Revision, null, []), ct).ConfigureAwait(false);
        return new SkillImportResult(1, [saved], $"Imported '{saved.GetEffectiveTitle()}' at revision {plan.Revision}.");
    }

    public CanonicalSkillModel ParseMarkdownToSkillModel(string rawMarkdown, string fallbackNameOrFile, string originProvider)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(rawMarkdown) > SkillStorage.MaxMarkdownBytes)
        {
            throw new ArgumentException("The imported document exceeds the supported size.");
        }
        var fallbackSlug = ProviderDiscoveryService.NormalizeSlug(fallbackNameOrFile.Contains('.') || fallbackNameOrFile.Contains('/')
            ? SkillDocumentClassifier.FallbackName("imported-skill", fallbackNameOrFile.Replace('\\', '/'))
            : fallbackNameOrFile);

        if (string.IsNullOrWhiteSpace(fallbackSlug))
        {
            fallbackSlug = "imported-skill";
        }

        CanonicalSkillModel model;
        if (!FrontmatterParser.TrySplitFrontmatter(rawMarkdown, out var yaml, out var plainBody))
        {
            throw new ArgumentException("The imported document has incomplete frontmatter or is empty.");
        }
        if (yaml.Length > 0)
        {
            Dictionary<string, object?> dict;
            try { dict = FrontmatterParser.ParseYamlToDictionary(yaml); }
            catch (YamlDotNet.Core.YamlException error)
            {
                throw new ArgumentException("The skill document contains invalid YAML frontmatter.", error);
            }
            var slugFromYaml = dict.TryGetValue("name", out var n) && n is not null
                ? ProviderDiscoveryService.NormalizeSlug(n.ToString()!)
                : fallbackSlug;

            if (string.IsNullOrWhiteSpace(slugFromYaml))
            {
                slugFromYaml = fallbackSlug;
            }

            if (dict.ContainsKey("targets") || dict.ContainsKey("when_to_use"))
            {
                model = _canonicalTranspiler.MergeInbound(slugFromYaml, rawMarkdown, null);
            }
            else if (dict.ContainsKey("globs") || dict.ContainsKey("alwaysApply"))
            {
                model = _cursorTranspiler.MergeInbound(slugFromYaml, rawMarkdown, null);
            }
            else if (dict.ContainsKey("allowed-tools") || dict.ContainsKey("allowed_tools") || dict.ContainsKey("model"))
            {
                model = _claudeTranspiler.MergeInbound(slugFromYaml, rawMarkdown, null);
            }
            else
            {
                model = _codexTranspiler.MergeInbound(slugFromYaml, rawMarkdown, null);
            }
            if (dict.GetValueOrDefault("title") is string title && !string.IsNullOrWhiteSpace(title))
                model.Title = title.Trim();
            // GitHub Copilot instructions scope themselves with applyTo; Cursor expresses the same idea with globs.
            if (dict.GetValueOrDefault("applyTo") is string applyTo && !dict.ContainsKey("globs"))
            {
                var globs = applyTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(glob => glob.Length is > 0 and <= 256).Take(32).ToList();
                if (globs.Count > 0) model.Targets.Cursor.Globs = globs;
            }
        }
        else
        {
            // Plain Markdown (rules, prompts, READMEs) has no metadata, so infer it from the document itself.
            var body = plainBody.TrimStart('﻿').Trim();
            if (body.Length == 0)
            {
                throw new ArgumentException("The imported document is empty.");
            }
            var heading = FrontmatterParser.ExtractFirstMarkdownHeading(body);
            var description = SkillDocumentClassifier.FirstParagraph(body);
            if (description.Length > 300) description = description[..297].TrimEnd() + "…";
            model = new CanonicalSkillModel
            {
                Name = fallbackSlug,
                Title = heading ?? CanonicalSkillModel.HumanizeSlug(fallbackSlug),
                Description = string.IsNullOrWhiteSpace(description) ? heading ?? CanonicalSkillModel.HumanizeSlug(fallbackSlug) : description,
                InstructionsMarkdown = body
            };
        }

        model.Name = ProviderDiscoveryService.NormalizeSlug(model.Name);
        model.Title = model.GetEffectiveTitle();
        model.SourceCategory = "user";
        model.OriginProvider = originProvider;
        model.IsCatalogOnly = false;
        return model;
    }

    private static string NormalizeCliAndSkillsShInput(string input, out string? skillFilter)
    {
        skillFilter = null;
        var s = input.Trim();

        var skillFlagMatch = Regex.Match(s, @"(?:--skill|-s)\s+[""']?([a-zA-Z0-9._-]+)[""']?", RegexOptions.IgnoreCase);
        if (skillFlagMatch.Success)
        {
            skillFilter = skillFlagMatch.Groups[1].Value;
            s = s.Remove(skillFlagMatch.Index, skillFlagMatch.Length).Trim();
        }

        s = Regex.Replace(s, @"^(?:npx|bunx|pnpm\s+dlx)\s+(?:-y\s+)?(?:@[\w-]+/)?skills(?:\.sh)?\s+(?:add|install|i)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        s = Regex.Replace(s, @"^skills(?:\.sh)?\s+(?:add|install|i)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();

        if (s.StartsWith("https://skills.sh/", StringComparison.OrdinalIgnoreCase))
        {
            s = s["https://skills.sh/".Length..].Trim('/');
        }
        else if (s.StartsWith("skills.sh/", StringComparison.OrdinalIgnoreCase))
        {
            s = s["skills.sh/".Length..].Trim('/');
        }

        var atIdx = s.LastIndexOf('@');
        if (atIdx > 0 && !s.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            skillFilter ??= s[(atIdx + 1)..].Trim();
            s = s[..atIdx].Trim();
        }

        return s.Trim('"', '\'', ' ');
    }

    /// <summary>
    /// Splits a GitHub link into owner, repository, a provisional ref and sub-path. For <c>tree/</c>, <c>blob/</c> and raw
    /// links the remaining segments are also returned, because a branch name may itself contain slashes.
    /// </summary>
    private static (string Owner, string Repo, string GitRef, string? SubPath, string[]? RefTail) ParseGitHubCoordinates(string cleaned)
    {
        var s = cleaned.Trim();
        if (s.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            s = s["https://github.com/".Length..];
        }
        else if (s.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            s = s["http://github.com/".Length..];
        }
        else if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            s = s["github.com/".Length..];
        }

        var isRaw = s.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);
        if (isRaw) s = s["https://raw.githubusercontent.com/".Length..];
        s = s.Split('?', '#')[0].Trim('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }

        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException(
                "Invalid repository format. Use 'owner/repo', 'owner/repo/skill-name', or a full GitHub URL.");
        }

        var owner = parts[0];
        var repo = parts[1];
        var gitRef = "HEAD";
        string? subPath = null;
        string[]? tail = null;

        if (isRaw && parts.Length >= 4)
        {
            tail = parts.Skip(2).Select(Uri.UnescapeDataString).ToArray();
            gitRef = tail[0];
            subPath = string.Join("/", tail.Skip(1));
        }
        else if (parts.Length >= 4 && (parts[2].Equals("tree", StringComparison.OrdinalIgnoreCase) ||
                                      parts[2].Equals("blob", StringComparison.OrdinalIgnoreCase)))
        {
            tail = parts.Skip(3).SelectMany(part => Uri.UnescapeDataString(part).Split('/')).ToArray();
            gitRef = tail[0];
            if (tail.Length > 1)
            {
                subPath = string.Join("/", tail.Skip(1));
            }
        }
        else if (parts.Length > 2)
        {
            subPath = string.Join("/", parts.Skip(2).Select(Uri.UnescapeDataString));
        }

        return (owner, repo, gitRef, string.IsNullOrEmpty(subPath) ? null : subPath, tail);
    }
}
