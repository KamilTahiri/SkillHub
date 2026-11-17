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
    // False when the listing does not know the skill's folder yet (skills.sh, featured picks); the preview lists its files.
    [property: JsonPropertyName("files_known")] bool FilesKnown = true,
    [property: JsonPropertyName("stars")] int? Stars = null,
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
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("stars")] int? Stars = null);

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
    [property: JsonPropertyName("has_frontmatter")] bool HasFrontmatter = true,
    [property: JsonPropertyName("scripts")] IReadOnlyList<ScriptFinding>? Scripts = null,
    [property: JsonPropertyName("stars")] int? Stars = null
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
