using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed record SkillImportSelection(
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("repo")] string? Repo = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("git_ref")] string? GitRef = null,
    [property: JsonPropertyName("raw_markdown")] string? RawMarkdown = null,
    [property: JsonPropertyName("file_name")] string? FileName = null);

public sealed record SkillImportPlan(Guid PlanId, string Revision, string Source, string? Repository,
    string Path, string License, string Name, string Title, string Description, string InstructionsMarkdown,
    IReadOnlyDictionary<string, string> SupportingFiles, IReadOnlyList<string> SkippedFiles, bool NameExists, DateTimeOffset ExpiresAt,
    string? ExistingName = null);

public sealed record SkillImportApplyRequest(string Revision, string? Name, IReadOnlyList<string>? Providers);
public sealed class CommunitySourceException(string message, int statusCode) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed partial class SkillImporterService
{
    private const int MaxPlanCount = 16;
    private const int MaxTextFile = 256 * 1024;
    private const int MaxTreeBytes = 8 * 1024 * 1024;
    private readonly object _plansLock = new();
    private readonly Dictionary<Guid, PendingImport> _plans = new();
    private sealed record PendingImport(SkillImportPlan Plan, CanonicalSkillModel Model)
    {
        public bool Applying { get; set; }
    }

    public async Task<SkillImportPlan> PrepareImportAsync(SkillImportSelection selection, CancellationToken ct = default)
    {
        CanonicalSkillModel model;
        string revision;
        string source;
        string? repository = null;
        string path;
        var license = UnknownLicense;
        var skipped = new List<string>();

        if (selection.RawMarkdown is not null)
        {
            path = System.IO.Path.GetFileName(selection.FileName ?? "SKILL.md");
            model = ParseMarkdownToSkillModel(selection.RawMarkdown, path, "Imported file");
            source = "Local file";
            revision = ContentHasher.ComputeFileRevisionHash(selection.RawMarkdown);
            license = ReadDeclaredLicense(selection.RawMarkdown) ?? license;
        }
        else if (selection.Repo == "local/catalog" || selection.Source?.StartsWith("local/catalog/", StringComparison.OrdinalIgnoreCase) == true)
        {
            model = GetImportableCatalogSkill(selection.Source, selection.Path).Clone();
            model.IsCatalogOnly = false;
            model.SourceCategory = "user";
            path = selection.Path ?? $"plugins/{model.CatalogId}/SKILL.md";
            source = $"Local catalog · {model.OriginProvider}";
            revision = ContentHasher.ComputeSemanticHash(model);
            license = model.Passthrough.GetValueOrDefault("license")?.ToString() ?? license;
        }
        else
        {
            var (owner, repo, requestedRef, hint) = ParseSelection(selection);
            repository = $"{owner}/{repo}";
            revision = await ResolveCommitAsync(owner, repo, requestedRef, ct).ConfigureAwait(false);
            var entries = await GetRepositoryBlobPathsAsync(owner, repo, revision, ct).ConfigureAwait(false);
            path = SelectSkillPath(entries, selection.Path ?? hint);
            var document = await GetRemoteSkillAsync(owner, repo, revision, path, entries, ct).ConfigureAwait(false);
            model = document.Model.Clone();
            license = document.License;
            var files = SupportingPaths(entries, path);
            var prefix = SkillDirectoryPrefix(path);
            if (files.Length > 128)
            {
                throw new InvalidDataException("The skill includes more than 128 supporting files. Review and import a smaller bundle.");
            }
            foreach (var relative in files)
            {
                var safePath = SkillPaths.SupportingPath(relative);
                try
                {
                    model.SupportingFiles.Add(safePath, await ReadRemoteTextAsync(
                        RawUri(owner, repo, revision, prefix + relative), MaxTextFile, ct).ConfigureAwait(false));
                }
                catch (InvalidDataException exception)
                {
                    skipped.Add($"{relative} · {exception.Message}");
                }
            }
            source = repository;
        }

        SkillStorage.ValidateModel(model);
        var existingName = model.CatalogId is { } catalogId ? _engine.FindManagedCatalogCopy(catalogId)
            : repository is not null ? _engine.FindImportedSkill(repository, path) : null;
        var plan = new SkillImportPlan(Guid.NewGuid(), revision, source, repository, path, license, model.Name,
            model.GetEffectiveTitle(), model.Description, model.InstructionsMarkdown,
            new Dictionary<string, string>(model.SupportingFiles), skipped.ToArray(),
            ImportNameExists(model.Name),
            DateTimeOffset.UtcNow.AddMinutes(15), existingName);
        lock (_plansLock)
        {
            foreach (var id in _plans.Where(pair => !pair.Value.Applying && pair.Value.Plan.ExpiresAt < DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
            {
                _plans.Remove(id);
            }
            if (_plans.Count >= MaxPlanCount)
            {
                var oldest = _plans.Values.Where(item => !item.Applying).MinBy(item => item.Plan.ExpiresAt)
                    ?? throw new SkillConflictException("Too many imports are in progress. Try again shortly.");
                _plans.Remove(oldest.Plan.PlanId);
            }
            _plans.Add(plan.PlanId, new PendingImport(plan, model));
        }
        return plan;
    }

    public async Task<CanonicalSkillModel> ApplyImportAsync(Guid id, SkillImportApplyRequest request, CancellationToken ct = default)
    {
        PendingImport pending;
        lock (_plansLock)
        {
            if (!_plans.TryGetValue(id, out pending!) || pending.Plan.ExpiresAt < DateTimeOffset.UtcNow)
            {
                throw new KeyNotFoundException("This review expired. Preview the skill again.");
            }
            if (pending.Applying || pending.Plan.Revision != request.Revision)
            {
                throw new SkillConflictException("The reviewed import no longer matches this request.");
            }
            pending.Applying = true;
        }
        try
        {
            var model = pending.Model.Clone();
            // Preserve origin as metadata while creating a new managed identity.
            model.Passthrough["import_source"] = pending.Plan.Source;
            model.Passthrough["import_revision"] = pending.Plan.Revision;
            model.Passthrough["import_path"] = pending.Plan.Path;
            model.Passthrough["import_license"] = pending.Plan.License;
            if (model.CatalogId is not null)
            {
                model.Passthrough["import_catalog_id"] = model.CatalogId;
            }
            model.CatalogId = null;
            model.Name = request.Name ?? model.Name;
            var enabled = (request.Providers ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = new[] { ProviderType.Codex, ProviderType.Gemini, ProviderType.Claude, ProviderType.Cursor };
            if (enabled.Any(provider => !available.Any(value => value.ToString().Equals(provider, StringComparison.OrdinalIgnoreCase))))
            {
                throw new ArgumentException("The import contains an unknown provider.");
            }
            foreach (var provider in available)
            {
                model.SetEnabledFor(provider, enabled.Contains(provider.ToString()));
            }
            var saved = await _engine.CreateSkillAsync(model, "webui:reviewed-import", ct).ConfigureAwait(false);
            lock (_plansLock)
            {
                _plans.Remove(id);
            }
            return saved;
        }
        finally
        {
            lock (_plansLock)
            {
                pending.Applying = false;
            }
        }
    }

    private static (string Owner, string Repo, string Ref, string? Hint) ParseSelection(SkillImportSelection selection)
    {
        var cleaned = NormalizeCliAndSkillsShInput(selection.Repo ?? selection.Source ?? "", out var filter);
        var (owner, repo, gitRef, subpath) = ParseGitHubCoordinates(cleaned);
        if (!Regex.IsMatch(owner, @"\A[A-Za-z0-9-]{1,100}\z") ||
            !Regex.IsMatch(repo, @"\A[A-Za-z0-9_.-]{1,100}\z") || repo is "." or "..")
        {
            throw new ArgumentException("Enter a valid GitHub repository, skill link, or skills.sh link.");
        }
        var requested = selection.GitRef ?? gitRef;
        if (string.IsNullOrWhiteSpace(requested) || requested.Length > 256 || requested.Any(char.IsControl))
        {
            throw new ArgumentException("The Git ref is invalid.");
        }
        return (owner, repo, requested, subpath ?? filter);
    }

    private Task<string> ResolveCommitAsync(string owner, string repo, string gitRef, CancellationToken ct)
    {
        if (Regex.IsMatch(gitRef, @"\A[0-9a-fA-F]{40}\z"))
            return Task.FromResult(gitRef.ToLowerInvariant());

        return _revisionCache.GetAsync((owner.ToLowerInvariant(), repo.ToLowerInvariant(), gitRef), async token =>
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/commits/{Uri.EscapeDataString(gitRef)}";
            using var doc = JsonDocument.Parse(await ReadRemoteTextAsync(url, 2 * 1024 * 1024, token).ConfigureAwait(false));
            var sha = doc.RootElement.GetProperty("sha").GetString();
            if (sha is null || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{40}\z"))
                throw new InvalidDataException("GitHub did not return an immutable revision.");
            return sha.ToLowerInvariant();
        }, ct);
    }

    private static bool IsSkillDocument(string path) =>
        Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase);

    private static string SkillFallbackName(string repo, string path) =>
        Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path) is { Length: > 0 } directory ? Path.GetFileName(directory) : repo
            : Path.GetFileNameWithoutExtension(path);

    private static string SelectSkillPath(List<string> entries, string? hint)
    {
        var skills = entries.Where(IsSkillDocument).ToArray();
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var path = SkillPaths.NormalizeRelative(hint.TrimEnd('/'));
            var exact = entries.FirstOrDefault(item => item == path ||
                item.Equals(path + "/SKILL.md", StringComparison.OrdinalIgnoreCase));
            if (exact is not null && (exact.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || exact.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase)))
                return exact;
            skills = skills.Where(item => SkillFallbackName(string.Empty, item).Equals(path, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(item).Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        return skills.Length switch
        {
            1 => skills[0],
            0 => throw new KeyNotFoundException("No skill matches that repository and path."),
            _ => throw new ArgumentException("This repository contains several skills. Choose a result in Explore or paste a link to a specific skill.")
        };
    }

    private static string RawUri(string owner, string repo, string commit, string path) =>
        $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{string.Join('/', SkillPaths.NormalizeRelative(path).Split('/').Select(Uri.EscapeDataString))}";

    private static string? ReadDeclaredLicense(string raw)
    {
        if (!FrontmatterParser.TrySplitFrontmatter(raw, out var yaml, out _))
        {
            return null;
        }
        return FrontmatterParser.ParseYamlToDictionary(yaml).GetValueOrDefault("license")?.ToString();
    }

    private async Task<string> ReadRemoteTextAsync(string url, int maximum, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var quota) && quota.Contains("0");
                throw new CommunitySourceException(rateLimited ? "GitHub rate limit reached. Try again later or browse the local catalog."
                    : response.StatusCode == HttpStatusCode.NotFound ? "GitHub could not find that repository, revision, or file."
                    : $"GitHub returned HTTP {(int)response.StatusCode}.", rateLimited ? 429 : response.StatusCode == HttpStatusCode.NotFound ? 404 : 502);
            }
            if (response.Content.Headers.ContentLength > maximum)
            {
                throw new InvalidDataException("File exceeds the supported size.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var size = await stream.ReadAsync(bytes, ct).ConfigureAwait(false);
                if (size == 0) break;
                if (buffer.Length + size > maximum)
                {
                    throw new InvalidDataException("File exceeds the supported size.");
                }
                buffer.Write(bytes, 0, size);
            }
            var text = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            return text.Contains('\0') ? throw new InvalidDataException("Binary files are excluded from text imports.") : text;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("File is not UTF-8 text.");
        }
        catch (HttpRequestException)
        {
            throw new CommunitySourceException("GitHub is unreachable. Check your connection or browse the local catalog.", 503);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CommunitySourceException("GitHub timed out. Try again or browse the local catalog.", 504);
        }
    }
}
