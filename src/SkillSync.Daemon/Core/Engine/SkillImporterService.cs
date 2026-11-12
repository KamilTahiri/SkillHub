using System.Collections.Concurrent;
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
    [property: JsonPropertyName("description")] string Description
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
    [property: JsonPropertyName("topic")] string Topic = "coding"
);

public sealed record CommunitySkillPreview(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("when_to_use")] string WhenToUse,
    [property: JsonPropertyName("instructions_markdown")] string InstructionsMarkdown,
    [property: JsonPropertyName("is_installed")] bool IsInstalled
);

public sealed class SkillImporterService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, List<string> BlobPaths)> RepoTreeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> RawMarkdownCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly SyncEngine _engine;
    private readonly CodexTranspiler _codexTranspiler = new();
    private readonly CanonicalTranspiler _canonicalTranspiler = new();
    private readonly CursorMdcTranspiler _cursorTranspiler = new();

    public SkillImporterService(SyncEngine engine)
    {
        _engine = engine;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SkillHub", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static IReadOnlyList<CommunityRepoSummary> GetCuratedRepositories() =>
    [
        new(
            "featured/all",
            "Featured • Curated Highlights",
            "Top recommended agent skills across Anthropic, Vercel Labs, and your pre-installed local catalog."),
        new(
            "local/catalog",
            "Featured • Local Plugin Catalog",
            "500+ rich offline skills pre-indexed on your system: Stripe, Supabase, Cloudflare, Playwright, Figma, and more."),
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
            "Community-curated coding workflows, architecture reviews, and testing rules.")
    ];

    public static string InferTopic(string name, string? desc)
    {
        var text = $"{name} {desc}".ToLowerInvariant();
        if (text.Contains("test") || text.Contains("playwright") || text.Contains("cypress") || text.Contains("audit") || text.Contains("security") || text.Contains("auth0") || text.Contains("auth") || text.Contains("qa"))
            return "testing";
        if (text.Contains("react") || text.Contains("next") || text.Contains("vue") || text.Contains("frontend") || text.Contains("css") || text.Contains("tailwind") || text.Contains("design") || text.Contains("ui") || text.Contains("ux") || text.Contains("figma") || text.Contains("canva") || text.Contains("browser"))
            return "web";
        if (text.Contains("data") || text.Contains("sql") || text.Contains("postgres") || text.Contains("supabase") || text.Contains("csv") || text.Contains("sheet") || text.Contains("database") || text.Contains("analytics") || text.Contains("gemini") || text.Contains("ai") || text.Contains("databricks") || text.Contains("lake") || text.Contains("spark"))
            return "data";
        if (text.Contains("docker") || text.Contains("cloud") || text.Contains("deploy") || text.Contains("aws") || text.Contains("azure") || text.Contains("gcp") || text.Contains("vercel") || text.Contains("cloudflare") || text.Contains("railway") || text.Contains("netlify") || text.Contains("ci") || text.Contains("cd") || text.Contains("linux") || text.Contains("server") || text.Contains("infra"))
            return "devops";
        if (text.Contains("doc") || text.Contains("pdf") || text.Contains("docx") || text.Contains("pptx") || text.Contains("notion") || text.Contains("linear") || text.Contains("jira") || text.Contains("slack") || text.Contains("mail") || text.Contains("gmail") || text.Contains("calendar") || text.Contains("dropbox") || text.Contains("workflow") || text.Contains("task") || text.Contains("todo"))
            return "productivity";
        return "coding";
    }

    public async Task<IReadOnlyList<CommunitySkillSummary>> BrowseRepositorySkillsAsync(
        string repoInput,
        string? filterQuery = null,
        CancellationToken ct = default)
    {
        if (string.Equals(repoInput, "local/catalog", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(repoInput, "local", StringComparison.OrdinalIgnoreCase))
        {
            var plugins = _engine.ListSkills(category: "plugin");
            var localResults = new List<CommunitySkillSummary>();
            foreach (var p in plugins)
            {
                var userCopy = _engine.GetSkill(p.Name);
                var isUserInstalled = userCopy is not null &&
                                      string.Equals(userCopy.SourceCategory, "user", StringComparison.OrdinalIgnoreCase) &&
                                      !userCopy.IsCatalogOnly;

                var topic = InferTopic(p.Name, p.Description);
                localResults.Add(new CommunitySkillSummary(
                    Slug: p.Name,
                    Title: p.GetEffectiveTitle(),
                    Repo: "local/catalog",
                    GitRef: "LOCAL",
                    Path: $"plugins/{p.Name}/SKILL.md",
                    Source: $"local/catalog/{p.Name}",
                    Description: p.Description ?? $"Offline catalog skill: {p.GetEffectiveTitle()}",
                    IsInstalled: isUserInstalled,
                    Topic: topic
                ));
            }

            if (!string.IsNullOrWhiteSpace(filterQuery))
            {
                var q = filterQuery.Trim();
                localResults = localResults
                    .Where(s =>
                        s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Slug.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Topic.Equals(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return localResults.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (string.Equals(repoInput, "featured/all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(repoInput, "all", StringComparison.OrdinalIgnoreCase))
        {
            var allHighlights = new List<CommunitySkillSummary>();
            allHighlights.AddRange(GetFallbackCuratedSkills(string.Empty));

            // Surface top popular local plugins (e.g. stripe, supabase, playwright, cloudflare, docker, figma, notion)
            var localPlugins = _engine.ListSkills(category: "plugin");
            var popularKeywords = new[] { "playwright", "stripe", "supabase", "cloudflare", "docker", "figma", "notion", "github", "react", "sql", "git" };
            var featuredPlugins = localPlugins
                .Where(p => popularKeywords.Any(k => p.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || (p.Description ?? string.Empty).Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Take(25);

            foreach (var p in featuredPlugins)
            {
                var userCopy = _engine.GetSkill(p.Name);
                var isUserInstalled = userCopy is not null &&
                                      string.Equals(userCopy.SourceCategory, "user", StringComparison.OrdinalIgnoreCase) &&
                                      !userCopy.IsCatalogOnly;

                allHighlights.Add(new CommunitySkillSummary(
                    Slug: p.Name,
                    Title: p.GetEffectiveTitle(),
                    Repo: "local/catalog",
                    GitRef: "LOCAL",
                    Path: $"plugins/{p.Name}/SKILL.md",
                    Source: $"local/catalog/{p.Name}",
                    Description: p.Description ?? $"Offline catalog skill: {p.GetEffectiveTitle()}",
                    IsInstalled: isUserInstalled,
                    Topic: InferTopic(p.Name, p.Description)
                ));
            }

            if (!string.IsNullOrWhiteSpace(filterQuery))
            {
                var q = filterQuery.Trim();
                allHighlights = allHighlights
                    .Where(s =>
                        s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Slug.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Topic.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Repo.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return allHighlights;
        }

        var cleaned = NormalizeCliAndSkillsShInput(repoInput, out _);
        var (owner, repo, gitRef, _) = ParseGitHubCoordinates(cleaned);
        var fullRepo = $"{owner}/{repo}";

        var results = new List<CommunitySkillSummary>();
        try
        {
            var allBlobs = await GetRepositoryBlobPathsAsync(owner, repo, gitRef, ct).ConfigureAwait(false);
            var skillMdPaths = allBlobs
                .Where(p => p.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var relPath in skillMdPaths)
            {
                var dirPrefix = relPath.Contains('/', StringComparison.Ordinal)
                    ? relPath[..relPath.LastIndexOf('/')]
                    : string.Empty;
                var rawFolderSlug = !string.IsNullOrEmpty(dirPrefix)
                    ? Path.GetFileName(dirPrefix)
                    : repo;
                var slug = ProviderDiscoveryService.NormalizeSlug(rawFolderSlug);
                var title = CanonicalSkillModel.HumanizeSlug(slug);
                var sourceSpec = !string.IsNullOrEmpty(dirPrefix)
                    ? $"{fullRepo}/{dirPrefix}"
                    : fullRepo;

                var existing = _engine.GetSkill(slug);
                var isInstalled = existing is not null && !existing.IsCatalogOnly;
                var desc = GetCuratedSkillHint(fullRepo, slug) ?? $"Community skill from {fullRepo} ({relPath})";

                results.Add(new CommunitySkillSummary(
                    Slug: slug,
                    Title: title,
                    Repo: fullRepo,
                    GitRef: gitRef,
                    Path: relPath,
                    Source: sourceSpec,
                    Description: desc,
                    IsInstalled: isInstalled,
                    Topic: InferTopic(slug, desc)));
            }
        }
        catch
        {
            // Fallback to curated catalog entries if offline or GitHub API rate-limited
            results.AddRange(GetFallbackCuratedSkills(fullRepo));
        }

        if (results.Count == 0)
        {
            results.AddRange(GetFallbackCuratedSkills(fullRepo));
        }

        if (!string.IsNullOrWhiteSpace(filterQuery))
        {
            var q = filterQuery.Trim();
            results = results
                .Where(s =>
                    s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Slug.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Topic.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                    s.Repo.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return results;
    }

    public async Task<IReadOnlyList<CommunitySkillSummary>> SearchCommunitySkillsAsync(
        string? query,
        CancellationToken ct = default)
    {
        var q = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            return await BrowseRepositorySkillsAsync("featured/all", null, ct).ConfigureAwait(false);
        }

        // If user entered an explicit owner/repo shorthand or GitHub URL, browse that repository directly
        if (q.Contains('/', StringComparison.Ordinal) || q.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cleaned = NormalizeCliAndSkillsShInput(q, out var subFilter);
                var (owner, repo, _, subPath) = ParseGitHubCoordinates(cleaned);
                return await BrowseRepositorySkillsAsync($"{owner}/{repo}", subFilter ?? subPath, ct).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        // Search across curated sources + local catalog in parallel
        var combined = new List<CommunitySkillSummary>();

        // 1. Local catalog search
        try
        {
            var localMatches = await BrowseRepositorySkillsAsync("local/catalog", q, ct).ConfigureAwait(false);
            combined.AddRange(localMatches);
        }
        catch
        {
        }

        // 2. Curated repos search
        var curatedRepos = new[] { "vercel-labs/agent-skills", "anthropics/skills" };
        foreach (var repo in curatedRepos)
        {
            try
            {
                var items = await BrowseRepositorySkillsAsync(repo, q, ct).ConfigureAwait(false);
                combined.AddRange(items);
            }
            catch
            {
            }
        }

        return combined
            .GroupBy(s => $"{s.Repo}:{s.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public async Task<CommunitySkillPreview> PreviewCommunitySkillAsync(
        string repoInput,
        string path,
        string gitRef = "HEAD",
        CancellationToken ct = default)
    {
        if (string.Equals(repoInput, "local/catalog", StringComparison.OrdinalIgnoreCase) ||
            repoInput.StartsWith("local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(gitRef, "LOCAL", StringComparison.OrdinalIgnoreCase))
        {
            var rawSlug = Path.GetFileName(path.TrimEnd('/').Replace("/SKILL.md", "", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(rawSlug) || rawSlug.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                rawSlug = Path.GetFileNameWithoutExtension(path);
            }
            var slug = ProviderDiscoveryService.NormalizeSlug(rawSlug);
            var model = _engine.GetSkill(slug) ??
                        _engine.ListSkills(category: "plugin").FirstOrDefault(s => s.Name.Equals(slug, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(slug, StringComparison.OrdinalIgnoreCase));

            if (model is null)
            {
                throw new InvalidOperationException($"Local skill '{slug}' not found in catalog.");
            }

            var existingUser = _engine.GetSkill(model.Name);
            var isInstalled = existingUser is not null &&
                              string.Equals(existingUser.SourceCategory, "user", StringComparison.OrdinalIgnoreCase) &&
                              !existingUser.IsCatalogOnly;

            return new CommunitySkillPreview(
                Slug: model.Name,
                Title: model.GetEffectiveTitle(),
                Repo: "local/catalog",
                Path: path,
                Source: $"local/catalog/{model.Name}",
                Description: model.Description ?? $"Offline catalog skill: {model.GetEffectiveTitle()}",
                WhenToUse: model.WhenToUse ?? string.Empty,
                InstructionsMarkdown: model.InstructionsMarkdown,
                IsInstalled: isInstalled);
        }

        var (owner, repo, parsedRef, _) = ParseGitHubCoordinates(repoInput);
        var effectiveRef = string.IsNullOrWhiteSpace(gitRef) ? parsedRef : gitRef;
        var cleanPath = path.TrimStart('/');
        var fullRepo = $"{owner}/{repo}";
        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{effectiveRef}/{cleanPath}";

        string rawMarkdown;
        if (!RawMarkdownCache.TryGetValue(rawUrl, out rawMarkdown!))
        {
            rawMarkdown = await Http.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            RawMarkdownCache[rawUrl] = rawMarkdown;
        }

        var dirPrefix = cleanPath.Contains('/', StringComparison.Ordinal)
            ? cleanPath[..cleanPath.LastIndexOf('/')]
            : string.Empty;
        var folderSlug = !string.IsNullOrEmpty(dirPrefix) ? Path.GetFileName(dirPrefix) : repo;
        var parsedModel = ParseMarkdownToSkillModel(rawMarkdown, folderSlug, $"GitHub ({fullRepo})");

        var sourceSpec = !string.IsNullOrEmpty(dirPrefix)
            ? $"{fullRepo}/{dirPrefix}"
            : fullRepo;

        var existing = _engine.GetSkill(parsedModel.Name);
        var isSkillInstalled = existing is not null && !existing.IsCatalogOnly;

        return new CommunitySkillPreview(
            Slug: parsedModel.Name,
            Title: parsedModel.GetEffectiveTitle(),
            Repo: fullRepo,
            Path: cleanPath,
            Source: sourceSpec,
            Description: parsedModel.Description,
            WhenToUse: parsedModel.WhenToUse,
            InstructionsMarkdown: parsedModel.InstructionsMarkdown,
            IsInstalled: isSkillInstalled);
    }

    private static async Task<List<string>> GetRepositoryBlobPathsAsync(
        string owner,
        string repo,
        string gitRef,
        CancellationToken ct)
    {
        var cacheKey = $"{owner}/{repo}@{gitRef}";
        if (RepoTreeCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(10))
        {
            return cached.BlobPaths;
        }

        var treeUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/trees/{Uri.EscapeDataString(gitRef)}?recursive=1";
        using var treeResponse = await Http.GetAsync(treeUrl, ct).ConfigureAwait(false);
        if (!treeResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not load GitHub repository '{owner}/{repo}' (HTTP {(int)treeResponse.StatusCode}).");
        }

        var treeJson = await treeResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(treeJson);
        if (!doc.RootElement.TryGetProperty("tree", out var treeArray) || treeArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Unexpected response from GitHub for '{owner}/{repo}'.");
        }

        var allBlobPaths = new List<string>();
        foreach (var item in treeArray.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var typeProp) &&
                string.Equals(typeProp.GetString(), "blob", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("path", out var pathProp))
            {
                var p = pathProp.GetString();
                if (!string.IsNullOrWhiteSpace(p))
                {
                    allBlobPaths.Add(p);
                }
            }
        }

        RepoTreeCache[cacheKey] = (DateTimeOffset.UtcNow, allBlobPaths);
        return allBlobPaths;
    }

    private IEnumerable<CommunitySkillSummary> GetFallbackCuratedSkills(string fullRepo)
    {
        var items = new List<(string Repo, string Slug, string Title, string Path, string Desc)>
        {
            ("vercel-labs/agent-skills", "react-best-practices", "React & Next.js Best Practices", "skills/react-best-practices/SKILL.md", "React and Next.js performance optimization guidelines from Vercel Engineering."),
            ("vercel-labs/agent-skills", "web-design-guidelines", "Web Design Guidelines", "skills/web-design-guidelines/SKILL.md", "Review UI code for Web Interface Guidelines, accessibility, and modern visual polish."),
            ("vercel-labs/agent-skills", "claude-3-7-sonnet-composition-patterns", "React Composition Patterns", "skills/composition-patterns/SKILL.md", "Modern component composition patterns and state architecture."),
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
                var existing = _engine.GetSkill(x.Slug);
                return new CommunitySkillSummary(
                    Slug: x.Slug,
                    Title: x.Title,
                    Repo: x.Repo,
                    GitRef: "HEAD",
                    Path: x.Path,
                    Source: $"{x.Repo}/{dirPrefix}",
                    Description: x.Desc,
                    IsInstalled: existing is not null && !existing.IsCatalogOnly,
                    Topic: InferTopic(x.Slug, x.Desc));
            });
    }

    private static string? GetCuratedSkillHint(string repo, string slug) => (repo.ToLowerInvariant(), slug.ToLowerInvariant()) switch
    {
        ("vercel-labs/agent-skills", "react-best-practices") => "40+ rules for React and Next.js performance optimization from Vercel Engineering.",
        ("vercel-labs/agent-skills", "web-design-guidelines") => "UI/UX audit rules covering layout, typography, accessibility, and interactive states.",
        ("vercel-labs/agent-skills", "composition-patterns") => "React component composition patterns to avoid boolean prop proliferation.",
        ("anthropics/skills", "frontend-design") => "Create distinctive, production-grade frontend interfaces with high design quality.",
        ("anthropics/skills", "mcp-builder") => "End-to-end guide for building Model Context Protocol (MCP) servers.",
        ("anthropics/skills", "webapp-testing") => "Automated browser verification and UI testing workflows with Playwright.",
        ("anthropics/skills", "skill-creator") => "Best practices for authoring modular SKILL.md agent instructions.",
        ("anthropics/skills", "pdf") => "Extract text/tables, merge/split, and generate formatted PDF documents.",
        ("anthropics/skills", "docx") => "Read, create, and edit Word (.docx) documents with rich formatting.",
        ("anthropics/skills", "xlsx") => "Create and analyze spreadsheets with formulas and financial modeling conventions.",
        ("anthropics/skills", "pptx") => "Build and edit presentation slide decks with clean visual layouts.",
        _ => null
    };

    public async Task<SkillImportResult> ImportAsync(SkillImportRequest request, CancellationToken ct = default)
    {
        // 1. Local file upload / raw Markdown content
        if (!string.IsNullOrWhiteSpace(request.RawMarkdown))
        {
            var skill = ParseMarkdownToSkillModel(
                request.RawMarkdown!,
                request.FileName ?? "imported-skill.md",
                originProvider: "Imported File");

            var saved = await _engine.UpsertSkillDirectAsync(skill, "webui:import-file", ct).ConfigureAwait(false);
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
            var localSkill = _engine.GetSkill(slug) ?? _engine.GetSkill(ProviderDiscoveryService.NormalizeSlug(slug));
            if (localSkill is null)
            {
                throw new InvalidOperationException($"Local catalog skill '{slug}' not found.");
            }

            var userSkill = new CanonicalSkillModel
            {
                Name = localSkill.Name,
                Title = localSkill.Title,
                Version = localSkill.Version,
                Description = localSkill.Description,
                WhenToUse = localSkill.WhenToUse,
                SummaryShort = localSkill.SummaryShort,
                SourceCategory = "user",
                OriginProvider = "Local Catalog",
                IsCatalogOnly = false,
                Tags = new List<string>(localSkill.Tags),
                Targets = localSkill.Targets,
                Passthrough = new Dictionary<string, object?>(localSkill.Passthrough, StringComparer.Ordinal),
                InstructionsMarkdown = localSkill.InstructionsMarkdown,
                SupportingFiles = new Dictionary<string, string>(localSkill.SupportingFiles, StringComparer.Ordinal)
            };

            var saved = await _engine.UpsertSkillDirectAsync(userSkill, "webui:import-catalog", ct).ConfigureAwait(false);
            return new SkillImportResult(
                1,
                [saved],
                $"Imported '{saved.GetEffectiveTitle()}' from Local Catalog into My Skills and synced across your AI providers.");
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
        var cleaned = NormalizeCliAndSkillsShInput(rawSource, out var explicitSkillFilter);

        // Case A: Direct raw URL or GitHub /blob/ URL to a single .md / .mdc file
        if (cleaned.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            (cleaned.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
             cleaned.Contains("/blob/", StringComparison.OrdinalIgnoreCase)))
        {
            var rawUrl = ConvertGitHubBlobToRawUrl(cleaned);
            var markdown = await Http.GetStringAsync(rawUrl, ct).ConfigureAwait(false);
            var slugHint = ExtractSlugFromUrl(rawUrl);
            var model = ParseMarkdownToSkillModel(markdown, slugHint, "GitHub Import");
            var saved = await _engine.UpsertSkillDirectAsync(model, "webui:import-github", ct).ConfigureAwait(false);
            return new SkillImportResult(
                1,
                [saved],
                $"Imported '{saved.GetEffectiveTitle()}' from GitHub and synced across your AI providers.");
        }

        // Case B: GitHub Repo / Tree URL or owner/repo[/subpath] shorthand
        var (owner, repo, gitRef, subPath) = ParseGitHubCoordinates(cleaned);
        if (!string.IsNullOrWhiteSpace(explicitSkillFilter) && string.IsNullOrWhiteSpace(subPath))
        {
            subPath = explicitSkillFilter;
        }

        var allBlobPaths = await GetRepositoryBlobPathsAsync(owner, repo, gitRef, ct).ConfigureAwait(false);

        var candidateSkillMdPaths = allBlobPaths
            .Where(p => p.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(subPath))
        {
            var normFilter = subPath.Trim('/').ToLowerInvariant();
            var filtered = candidateSkillMdPaths.Where(p =>
            {
                var lowerPath = p.ToLowerInvariant();
                var parentDir = Path.GetDirectoryName(p)?.Replace('\\', '/').Trim('/') ?? string.Empty;
                var folderName = Path.GetFileName(parentDir).ToLowerInvariant();
                return lowerPath.StartsWith(normFilter + "/", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(parentDir, normFilter, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(folderName, normFilter, StringComparison.OrdinalIgnoreCase) ||
                       folderName.Contains(normFilter, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (filtered.Count > 0)
            {
                candidateSkillMdPaths = filtered;
            }
            else
            {
                throw new InvalidOperationException(
                    $"No skill matching '{subPath}' was found in '{owner}/{repo}'.");
            }
        }

        if (candidateSkillMdPaths.Count == 0)
        {
            throw new InvalidOperationException(
                $"No SKILL.md files were found in '{owner}/{repo}'.");
        }

        var pathsToImport = candidateSkillMdPaths.Take(12).ToList();
        var importedSkills = new List<CanonicalSkillModel>();

        foreach (var skillMdRelPath in pathsToImport)
        {
            var rawSkillUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{gitRef}/{skillMdRelPath}";
            string rawMarkdown;
            if (!RawMarkdownCache.TryGetValue(rawSkillUrl, out rawMarkdown!))
            {
                rawMarkdown = await Http.GetStringAsync(rawSkillUrl, ct).ConfigureAwait(false);
                RawMarkdownCache[rawSkillUrl] = rawMarkdown;
            }

            var skillDirPrefix = skillMdRelPath.Contains('/', StringComparison.Ordinal)
                ? skillMdRelPath[..skillMdRelPath.LastIndexOf('/')]
                : string.Empty;

            var folderSlug = !string.IsNullOrEmpty(skillDirPrefix)
                ? Path.GetFileName(skillDirPrefix)
                : repo;

            var model = ParseMarkdownToSkillModel(rawMarkdown, folderSlug, $"GitHub ({owner}/{repo})");

            if (!string.IsNullOrEmpty(skillDirPrefix))
            {
                var prefixWithSlash = skillDirPrefix + "/";
                var companionPaths = allBlobPaths
                    .Where(p => p.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(p, skillMdRelPath, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();

                foreach (var compPath in companionPaths)
                {
                    var relInsideSkill = compPath[prefixWithSlash.Length..];
                    var firstSeg = relInsideSkill.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    if (!ProviderDiscoveryService.IsAllowedSupportingFolder(firstSeg))
                    {
                        continue;
                    }

                    try
                    {
                        var compUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{gitRef}/{compPath}";
                        var compContent = await Http.GetStringAsync(compUrl, ct).ConfigureAwait(false);
                        if (compContent.Length <= 131_072)
                        {
                            model.SupportingFiles[relInsideSkill] = ContentHasher.NormalizeLineEndings(compContent);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            var saved = await _engine.UpsertSkillDirectAsync(model, "webui:import-github", ct).ConfigureAwait(false);
            importedSkills.Add(saved);
        }

        var summaryMessage = importedSkills.Count == 1
            ? $"Imported '{importedSkills[0].GetEffectiveTitle()}' from {owner}/{repo} into My Skills and synced across your AI providers."
            : $"Imported {importedSkills.Count} skills from {owner}/{repo} into My Skills and synced across your AI providers.";

        return new SkillImportResult(importedSkills.Count, importedSkills, summaryMessage);
    }

    public CanonicalSkillModel ParseMarkdownToSkillModel(string rawMarkdown, string fallbackNameOrFile, string originProvider)
    {
        var fallbackSlug = ProviderDiscoveryService.NormalizeSlug(
            Path.GetFileNameWithoutExtension(fallbackNameOrFile).Equals("SKILL", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(Path.GetDirectoryName(fallbackNameOrFile) ?? "imported-skill")
                : Path.GetFileNameWithoutExtension(fallbackNameOrFile));

        if (string.IsNullOrWhiteSpace(fallbackSlug))
        {
            fallbackSlug = "imported-skill";
        }

        CanonicalSkillModel model;
        if (FrontmatterParser.TrySplitFrontmatter(rawMarkdown, out var yaml, out _))
        {
            var dict = FrontmatterParser.ParseYamlToDictionary(yaml);
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
            else
            {
                model = _codexTranspiler.MergeInbound(slugFromYaml, rawMarkdown, null);
            }
        }
        else
        {
            var normalized = ContentHasher.NormalizeLineEndings(rawMarkdown).Trim();
            var h1Match = Regex.Match(normalized, @"^#\s+(.+)$", RegexOptions.Multiline);
            var title = h1Match.Success ? h1Match.Groups[1].Value.Trim() : CanonicalSkillModel.HumanizeSlug(fallbackSlug);
            var slug = ProviderDiscoveryService.NormalizeSlug(title);
            if (string.IsNullOrWhiteSpace(slug)) slug = fallbackSlug;

            model = new CanonicalSkillModel
            {
                Name = slug,
                Title = title,
                Description = $"Imported skill: {title}",
                InstructionsMarkdown = normalized
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

    private static string ConvertGitHubBlobToRawUrl(string url)
    {
        if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var m = Regex.Match(url, @"^https?://github\.com/([^/]+)/([^/]+)/blob/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return $"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}/{m.Groups[4].Value}";
        }

        return url;
    }

    private static string ExtractSlugFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var file = segments[^1];
                if (file.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[^2];
                }
                return Path.GetFileNameWithoutExtension(file);
            }
        }
        catch
        {
        }

        return "imported-skill";
    }

    private static (string Owner, string Repo, string GitRef, string? SubPath) ParseGitHubCoordinates(string cleaned)
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

        s = s.Trim('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }

        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException(
                "Invalid repository format. Use 'owner/repo', 'owner/repo/skill-name', or a full GitHub URL.");
        }

        var owner = parts[0];
        var repo = parts[1];
        var gitRef = "HEAD";
        string? subPath = null;

        if (parts.Length >= 4 && string.Equals(parts[2], "tree", StringComparison.OrdinalIgnoreCase))
        {
            gitRef = parts[3];
            if (parts.Length > 4)
            {
                subPath = string.Join("/", parts.Skip(4));
            }
        }
        else if (parts.Length > 2)
        {
            subPath = string.Join("/", parts.Skip(2));
        }

        return (owner, repo, gitRef, subPath);
    }
}
