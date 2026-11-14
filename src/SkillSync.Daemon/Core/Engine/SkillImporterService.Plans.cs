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
            var (owner, repo, sha, hint) = await ResolveSelectionAsync(selection, ct).ConfigureAwait(false);
            repository = $"{owner}/{repo}";
            revision = sha;
            var index = await _indexer.GetIndexAsync(owner, repo, sha, ct).ConfigureAwait(false);
            var document = SelectDocument(index, selection.Path ?? hint);
            path = document.Path;
            var raw = await _indexer.ReadTextAsync(index, document.Path, ct).ConfigureAwait(false);
            model = ParseMarkdownToSkillModel(raw, SkillDocumentClassifier.FallbackName(repo, document.Path), $"GitHub ({repository})");
            license = License(index, document, raw);
            var prefix = RepositoryIndexer.SkillDirectoryPrefix(document.Path);
            skipped.AddRange(document.ExcludedFiles.Select(file => $"{file} · binary, too large, or outside a supported folder"));
            foreach (var relative in document.Files)
            {
                try
                {
                    model.SupportingFiles.Add(SkillPaths.SupportingPath(relative),
                        await _indexer.ReadTextAsync(index, prefix + relative, ct).ConfigureAwait(false));
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

    private static (string Owner, string Repo, string Ref, string? Hint, string[]? RefTail) ParseSelection(SkillImportSelection selection)
    {
        var cleaned = NormalizeCliAndSkillsShInput(selection.Repo ?? selection.Source ?? "", out var filter);
        var (owner, repo, gitRef, subpath, tail) = ParseGitHubCoordinates(cleaned);
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
        return (owner, repo, requested, subpath ?? filter, selection.GitRef is null ? tail : null);
    }

    /// <summary>Resolves a selection to an immutable commit. Branch names with slashes are resolved segment by segment.</summary>
    private async Task<(string Owner, string Repo, string Sha, string? Hint)> ResolveSelectionAsync(SkillImportSelection selection,
        CancellationToken ct)
    {
        var (owner, repo, requested, hint, tail) = ParseSelection(selection);
        if (tail is { Length: > 1 } && !Regex.IsMatch(tail[0], @"\A[0-9a-fA-F]{40}\z"))
        {
            var (resolved, path) = await _indexer.ResolveRefAndPathAsync(owner, repo, tail, ct).ConfigureAwait(false);
            return (owner, repo, resolved, path ?? (selection.Path is null ? null : hint));
        }
        var sha = await _indexer.ResolveCommitAsync(owner, repo, requested, ct).ConfigureAwait(false);
        return (owner, repo, sha, hint);
    }

    private static string? ReadDeclaredLicense(string raw)
    {
        if (!FrontmatterParser.TrySplitFrontmatter(raw, out var yaml, out _))
        {
            return null;
        }
        return FrontmatterParser.ParseYamlToDictionary(yaml).GetValueOrDefault("license")?.ToString();
    }
}
