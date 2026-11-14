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
    [property: JsonPropertyName("topic")] string Topic = "coding",
    [property: JsonPropertyName("existing_name")] string? ExistingName = null
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
    [property: JsonPropertyName("is_installed")] bool IsInstalled,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("supporting_paths")] IReadOnlyList<string> SupportingPaths,
    [property: JsonPropertyName("name_exists")] bool NameExists,
    [property: JsonPropertyName("existing_name")] string? ExistingName
);

public sealed partial class SkillImporterService
{
    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly HttpClient _http;

    private readonly SyncEngine _engine;
    private readonly CodexTranspiler _codexTranspiler = new();
    private readonly CanonicalTranspiler _canonicalTranspiler = new();
    private readonly CursorMdcTranspiler _cursorTranspiler = new();
    private readonly ClaudeTranspiler _claudeTranspiler = new();

    public SkillImporterService(SyncEngine engine, HttpClient? remote = null)
    {
        _engine = engine;
        _http = remote ?? SharedHttp;
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
            "Featured",
            "Top recommended agent skills across Anthropic, Vercel Labs, and your pre-installed local catalog."),
        new(
            "local/catalog",
            "Local Plugin Catalog",
            "Skills from plugin caches on this PC."),
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
        CancellationToken ct = default)
    {
        if (string.Equals(repoInput, "local/catalog", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(repoInput, "local", StringComparison.OrdinalIgnoreCase))
        {
            var plugins = _engine.ListSkills(category: "plugin");
            var localResults = new List<CommunitySkillSummary>();
            foreach (var p in plugins)
            {
                var existingName = _engine.FindManagedCatalogCopy(p.CatalogId!);

                var topic = InferTopic(p.Name, p.Description);
                localResults.Add(new CommunitySkillSummary(
                    Slug: p.Name,
                    Title: p.GetEffectiveTitle(),
                    Repo: "local/catalog",
                    GitRef: "LOCAL",
                    Path: $"plugins/{p.CatalogId}/SKILL.md",
                    Source: $"local/catalog/{p.CatalogId}",
                    Description: p.Description ?? $"Offline catalog skill: {p.GetEffectiveTitle()}",
                    IsInstalled: existingName is not null,
                    ExistingName: existingName,
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
                var existingName = _engine.FindManagedCatalogCopy(p.CatalogId!);

                allHighlights.Add(new CommunitySkillSummary(
                    Slug: p.Name,
                    Title: p.GetEffectiveTitle(),
                    Repo: "local/catalog",
                    GitRef: "LOCAL",
                    Path: $"plugins/{p.CatalogId}/SKILL.md",
                    Source: $"local/catalog/{p.CatalogId}",
                    Description: p.Description ?? $"Offline catalog skill: {p.GetEffectiveTitle()}",
                    IsInstalled: existingName is not null,
                    ExistingName: existingName,
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

        var cleaned = NormalizeCliAndSkillsShInput(repoInput, out var skillFilter);
        if (string.IsNullOrWhiteSpace(filterQuery)) filterQuery = skillFilter;
        var (owner, repo, gitRef, _) = ParseSelection(new SkillImportSelection(Source: cleaned));
        var fullRepo = $"{owner}/{repo}";

        var revision = await ResolveCommitAsync(owner, repo, gitRef, ct).ConfigureAwait(false);
        var entries = await GetRepositoryBlobPathsAsync(owner, repo, revision, ct).ConfigureAwait(false);
        var results = entries.Where(IsSkillDocument).Order(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var slug = ProviderDiscoveryService.NormalizeSlug(SkillFallbackName(repo, path));
            var existingName = _engine.FindImportedSkill(fullRepo, path);
            var item = new CommunitySkillSummary(slug, CanonicalSkillModel.HumanizeSlug(slug), fullRepo, revision,
                path, fullRepo, string.Empty, existingName is not null, InferTopic(slug, null), existingName);
            return _previewCache.TryGet(PreviewKey(owner, repo, path, revision), out var document)
                ? WithSkillMetadata(item, document.Model) : item;
        }).ToList();

        var query = filterQuery?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            // Read path matches first, then look for terms that only appear in the document.
            const int searchReadLimit = 24;
            var candidates = results.OrderByDescending(MatchesPath).ToArray();
            var matches = new CommunitySkillSummary?[Math.Min(searchReadLimit, candidates.Length)];
            await Parallel.ForEachAsync(Enumerable.Range(0, matches.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
                {
                    var item = candidates[index];
                    try
                    {
                        var document = await GetRemoteSkillAsync(owner, repo, revision, item.Path, entries, token).ConfigureAwait(false);
                        var model = document.Model;
                        var topic = InferTopic(model.Name, model.Description);
                        if (MatchesPath(item) || Contains(model.Name) || Contains(model.GetEffectiveTitle()) || Contains(model.Description) ||
                            Contains(model.WhenToUse) || Contains(model.InstructionsMarkdown) || Contains(topic))
                            matches[index] = WithSkillMetadata(item, model);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidDataException or YamlDotNet.Core.YamlException ||
                        error is CommunitySourceException { StatusCode: 404 })
                    {
                        // One invalid or unavailable document should not hide the rest of the repository.
                    }
                }).ConfigureAwait(false);
            return matches.OfType<CommunitySkillSummary>().Concat(candidates.Skip(matches.Length).Where(MatchesPath)).ToArray();

            bool Contains(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool MatchesPath(CommunitySkillSummary item) => Contains(item.Path) || Contains(item.Slug) ||
                Contains(item.Title) || Contains(item.Repo);
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

        if (LooksLikeRepositoryInput(q))
        {
            var (owner, repo, gitRef, path) = ParseSelection(new SkillImportSelection(Source: q));
            return await BrowseRepositorySkillsAsync($"https://github.com/{owner}/{repo}/tree/{Uri.EscapeDataString(gitRef)}", path, ct)
                .ConfigureAwait(false);
        }

        async Task<(IReadOnlyList<CommunitySkillSummary> Skills, CommunitySourceException? Error)> SearchAsync(string source)
        {
            try { return (await BrowseRepositorySkillsAsync(source, q, ct).ConfigureAwait(false), null); }
            catch (CommunitySourceException exception) { return ([], exception); }
        }

        var results = await Task.WhenAll(new[] { "local/catalog", "vercel-labs/agent-skills", "anthropics/skills", "github/awesome-copilot" }.Select(SearchAsync))
            .ConfigureAwait(false);
        var combined = results.SelectMany(result => result.Skills)
            .DistinctBy(skill => (skill.Repo.ToLowerInvariant(), skill.Path)).ToArray();
        if (combined.Length == 0 && results.FirstOrDefault(result => result.Error is not null).Error is { } failure)
            throw failure;
        return combined;
    }

    public async Task<CommunitySkillPreview> PreviewCommunitySkillAsync(string repoInput, string path, string gitRef = "HEAD",
        CancellationToken ct = default)
    {
        CanonicalSkillModel model;
        string source;
        string revision;
        string license;
        IReadOnlyList<string> supportingPaths;
        string? existingName;
        if (repoInput == "local/catalog")
        {
            model = GetImportableCatalogSkill(null, path).Clone();
            model.IsCatalogOnly = false;
            model.SourceCategory = "user";
            source = $"Local catalog · {model.OriginProvider}";
            revision = ContentHasher.ComputeSemanticHash(model);
            license = model.Passthrough.GetValueOrDefault("license")?.ToString() ?? UnknownLicense;
            supportingPaths = model.SupportingFiles.Keys.Order(StringComparer.Ordinal).ToArray();
            existingName = _engine.FindManagedCatalogCopy(model.CatalogId!);
        }
        else
        {
            var (owner, repo, requested, _) = ParseSelection(new SkillImportSelection(Repo: repoInput, GitRef: gitRef));
            repoInput = $"{owner}/{repo}";
            source = repoInput;
            revision = await ResolveCommitAsync(owner, repo, requested, ct).ConfigureAwait(false);
            var entries = await GetRepositoryBlobPathsAsync(owner, repo, revision, ct).ConfigureAwait(false);
            path = SelectSkillPath(entries, path);
            var document = await GetRemoteSkillAsync(owner, repo, revision, path, entries, ct).ConfigureAwait(false);
            model = document.Model;
            license = document.License;
            supportingPaths = SupportingPaths(entries, path);
            existingName = _engine.FindImportedSkill(repoInput, path);
        }

        var nameExists = ImportNameExists(model.Name);
        return new CommunitySkillPreview(model.Name, model.GetEffectiveTitle(), repoInput, path, source,
            model.Description, model.WhenToUse, model.InstructionsMarkdown, nameExists || existingName is not null,
            revision, license, supportingPaths, nameExists, existingName);
    }

    private Task<List<string>> GetRepositoryBlobPathsAsync(string owner, string repo, string gitRef, CancellationToken ct) =>
        _treeCache.GetAsync((owner.ToLowerInvariant(), repo.ToLowerInvariant(), gitRef), async token =>
        {
            var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/trees/{Uri.EscapeDataString(gitRef)}?recursive=1";
            using var doc = JsonDocument.Parse(await ReadRemoteTextAsync(url, MaxTreeBytes, token).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
                throw new InvalidDataException("GitHub truncated the file tree. An incomplete bundle cannot be reviewed.");

            return doc.RootElement.GetProperty("tree").EnumerateArray()
                .Where(entry => entry.GetProperty("type").GetString() == "blob" &&
                    (!entry.TryGetProperty("mode", out var mode) || mode.GetString() != "120000"))
                .Select(entry => entry.GetProperty("path").GetString()!).ToList();
        }, ct);

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
        }
        else
        {
            throw new ArgumentException("The imported document has incomplete frontmatter or is empty.");
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

        if (isRaw && parts.Length >= 4)
        {
            gitRef = Uri.UnescapeDataString(parts[2]);
            subPath = string.Join("/", parts.Skip(3).Select(Uri.UnescapeDataString));
        }
        else if (parts.Length >= 4 && (parts[2].Equals("tree", StringComparison.OrdinalIgnoreCase) ||
                                      parts[2].Equals("blob", StringComparison.OrdinalIgnoreCase)))
        {
            gitRef = Uri.UnescapeDataString(parts[3]);
            if (parts.Length > 4)
            {
                subPath = string.Join("/", parts.Skip(4).Select(Uri.UnescapeDataString));
            }
        }
        else if (parts.Length > 2)
        {
            subPath = string.Join("/", parts.Skip(2).Select(Uri.UnescapeDataString));
        }

        return (owner, repo, gitRef, subPath);
    }
}
