using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Core.Transpilers;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>Browsing, searching and previewing community sources: GitHub repositories, skills.sh, the local catalog and featured skills.</summary>
public sealed partial class SkillImporterService
{
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
            InferTopic(slug, entry.Source), existing, Installs: entry.Installs, FilesKnown: false);
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
                catalog.SupportingFiles.Keys.Order(StringComparer.Ordinal).ToArray(), exists, catalogExisting,
                Scripts: SkillDoctor.InspectScripts(catalog.SupportingFiles.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
                    catalog.InstructionsMarkdown));
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
            document.Kind, document.ExcludedFiles, document.HasFrontmatter,
            // Script review: content is checked when the file is already cached locally (tarball mode), otherwise by name.
            SkillDoctor.InspectScripts(document.Files.ToDictionary(file => file,
                file => _indexer.TryReadCachedText(index, RepositoryIndexer.SkillDirectoryPrefix(document.Path) + file)), model.InstructionsMarkdown));
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
                    Topic: InferTopic(x.Slug, x.Desc),
                    FilesKnown: false);
            });
    }
}
