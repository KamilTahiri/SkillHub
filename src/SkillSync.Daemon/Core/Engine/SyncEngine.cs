using System.Collections.Concurrent;
using System.Threading.Channels;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Core.Transpilers;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed partial class SyncEngine : IAsyncDisposable
{
    public const string BootstrapSkillName = "00-skillhub-bootstrap";
    private readonly ProviderDiscoveryService _discovery;
    private readonly SkillStorage _storage;
    private readonly SkillCatalog _catalog;
    private readonly SyncLedger _ledger;
    private readonly Channel<Func<Task>> _commands = Channel.CreateBounded<Func<Task>>(
        new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writer;
    private readonly FileStream _storeLock;
    private long _activitySequence;
    private readonly ConcurrentDictionary<string, CanonicalSkillModel> _managed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _expectedWithdrawals = new(SkillPaths.Comparer);
    private readonly ConcurrentQueue<SyncActivityNotification> _recentActivity = new();

    private readonly Dictionary<ProviderType, ISkillTranspiler> _transpilers = new()
    {
        [ProviderType.Canonical] = new CanonicalTranspiler(),
        [ProviderType.Claude] = new ClaudeTranspiler(),
        [ProviderType.Gemini] = new GeminiTranspiler(),
        [ProviderType.Codex] = new CodexTranspiler(),
        [ProviderType.Cursor] = new CursorMdcTranspiler()
    };

    public SyncEngine(ProviderDiscoveryService discovery)
    {
        _discovery = discovery;
        _storage = new SkillStorage(discovery);
        _catalog = new SkillCatalog(discovery);
        _discovery.EnsureStorage();
        _storeLock = new FileStream(SkillPaths.Resolve(_discovery.SkillHubRoot, ".writer.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            try
            {
                _ledger = _storage.ReadLedger();
                MigrateLedger();
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            {
                var backup = SkillPaths.Resolve(_discovery.SkillHubRoot, $".sync-ledger.{Guid.NewGuid():N}.bak");
                File.Copy(_discovery.LedgerFilePath, backup);
                _ledger = new SyncLedger();
                PublishActivity(string.Empty, "startup", "error", $"Invalid sync ledger saved to '{backup}'. Existing provider files will be treated as unowned.");
            }

            LoadTags();
            LoadCanonicalSkills();
            _catalog.Refresh();
            _writer = ProcessQueueAsync();
        }
        catch
        {
            _storeLock.Dispose();
            throw;
        }
    }

    public ProviderDiscoveryService Discovery => _discovery;
    public IReadOnlyCollection<SyncActivityNotification> GetRecentActivity() => _recentActivity.ToArray();
    public event Action<SyncActivityNotification>? ActivityPublished;

    private async Task ProcessQueueAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await command().ConfigureAwait(false);
        }
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> command, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(async () =>
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }

            try
            {
                // Once started, a mutation reaches a durable result even if the caller disconnects.
                completion.TrySetResult(await command().ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, ct).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        finally
        {
            await _storeLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IEnumerable<CanonicalSkillModel> SelectSkills(string? query, string? tag, bool includeDisabled, string? category)
    {
        var managed = _managed.Values.ToArray();
        var promoted = managed.Where(skill => skill.SourceCategory is "template" or "plugin" && skill.CatalogId is not null)
            .Select(skill => skill.CatalogId!).ToHashSet(StringComparer.Ordinal);
        IEnumerable<CanonicalSkillModel> source = managed.Concat(_catalog.Summaries().Where(skill => !promoted.Contains(skill.CatalogId!)));

        var scope = category?.Trim().ToLowerInvariant();
        var installed = !includeDisabled || scope is "active" or "synced" ? InstalledProviderNames() : null;
        if (!includeDisabled)
        {
            source = source.Where(skill => skill.IsCatalogOnly || IsActive(skill, installed!));
        }
        if (scope is { Length: > 0 } && scope != "all")
        {
            source = scope is "active" or "synced" ? source.Where(skill => IsActive(skill, installed!))
                : source.Where(skill => string.Equals(skill.SourceCategory, scope, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var requestedTag = ProviderDiscoveryService.NormalizeSlug(tag);
            source = source.Where(skill => skill.Tags.Any(value => ProviderDiscoveryService.NormalizeSlug(value) == requestedTag));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            source = source.Where(skill => MatchesQuery(skill, term) ||
                skill.IsCatalogOnly && skill.CatalogId is { } id && _catalog.MatchesInstructions(id, term));
        }

        return source.OrderBy(skill => skill.SourceCategory switch { "user" => 0, "template" => 1, "system" => 2, _ => 3 })
            .ThenBy(skill => skill.GetEffectiveTitle(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.CatalogId, StringComparer.Ordinal);
    }

    private HashSet<string> InstalledProviderNames() => _discovery.DiscoverProviders()
        .Where(provider => provider.IsInstalled && provider.Provider != ProviderType.Canonical)
        .Select(provider => ProviderName(provider.Provider)).ToHashSet(StringComparer.Ordinal);

    private static bool IsActive(CanonicalSkillModel skill, HashSet<string> installed) =>
        !skill.IsCatalogOnly && skill.SourceCategory != "system" &&
        skill.Destinations.Any(destination => installed.Contains(destination.Provider) &&
            destination.State == "synced" && destination.AppliedRevision == skill.Revision);

    public IReadOnlyList<CanonicalSkillModel> ListSkills(string? query = null, string? tag = null,
        bool includeDisabled = true, string? category = null) =>
        SelectSkills(query, tag, includeDisabled, category).Select(skill => skill.Clone(includeContents: false)).ToList();

    public IReadOnlyList<SkillSummary> ListSkillSummaries(string? query = null, string? tag = null, string? category = null,
        int offset = 0, int limit = int.MaxValue) =>
        SelectSkills(query, tag, includeDisabled: true, category).Skip(offset).Take(limit).Select(skill =>
        {
            var metadata = skill.Clone(includeContents: false);
            return new SkillSummary(metadata.Name, metadata.Title, metadata.Version, metadata.Description,
                metadata.WhenToUse, metadata.SummaryShort, metadata.SourceCategory, metadata.OriginProvider,
                metadata.IsCatalogOnly, metadata.CatalogId, metadata.Revision, metadata.Tags, metadata.Targets, metadata.Destinations,
                FindInstructionExcerpt(skill.InstructionsMarkdown, query));
        }).ToList();

    public IReadOnlyDictionary<string, int> CountSkills() => SelectSkills(null, null, true, null)
        .GroupBy(skill => skill.SourceCategory)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static bool MatchesQuery(CanonicalSkillModel skill, string query)
    {
        string[] fields = [skill.Name, skill.Title, skill.Description, skill.WhenToUse, skill.OriginProvider,
            skill.InstructionsMarkdown, .. skill.Tags];
        return fields.Any(field => field.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
                fields.Any(field => field.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? FindInstructionExcerpt(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Prepend(query))
        {
            var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var start = Math.Max(0, index - 50);
                return text.Substring(start, Math.Min(text.Length - start, 180));
            }
        }
        return null;
    }

    public CanonicalSkillModel? GetSkill(string name) =>
        _managed.TryGetValue(ProviderDiscoveryService.NormalizeSlug(name), out var managed)
            ? managed.Clone() : _catalog.Get(name);

    public CanonicalSkillModel? GetCatalogSkill(string id) => _catalog.Get(id);

    public bool HasManagedCatalogCopy(string id) => _managed.Values.Any(skill => skill.CatalogId == id ||
        string.Equals(skill.Passthrough.GetValueOrDefault("import_catalog_id")?.ToString(), id, StringComparison.OrdinalIgnoreCase));

    public bool HasImportedSkill(string repository, string path) => _managed.Values.Any(skill =>
        string.Equals(skill.Passthrough.GetValueOrDefault("import_source")?.ToString(), repository, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(skill.Passthrough.GetValueOrDefault("import_path")?.ToString(), path, StringComparison.Ordinal));

    public Dictionary<string, string[]> PreviewDestinationPaths(CanonicalSkillModel draft)
    {
        var slug = string.IsNullOrWhiteSpace(draft.Name) && string.IsNullOrWhiteSpace(draft.Title) ? "preview-skill" : GetSlug(draft);
        return GetDestinations(slug, new SkillLedgerEntry(), []).GroupBy(destination => ProviderName(destination.Provider))
            .ToDictionary(group => group.Key, group => group.Select(destination => destination.Path).ToArray());
    }

    public Dictionary<string, string> PreviewAllTranspilations(CanonicalSkillModel model)
    {
        var slug = string.IsNullOrWhiteSpace(model.Name) && string.IsNullOrWhiteSpace(model.Title) ? "preview-skill" : GetSlug(model);
        SkillStorage.ValidateModel(model);
        var draft = model.Clone();
        draft.Name = slug;
        var directory = Path.GetDirectoryName(_storage.CanonicalPath(slug))!;
        return _transpilers.ToDictionary(pair => ProviderName(pair.Key), pair => pair.Value.TranspileOutbound(draft, directory));
    }

    public Task<CanonicalSkillModel> CreateSkillAsync(CanonicalSkillModel model, string originator, CancellationToken ct = default) =>
        SaveDirectAsync(model, originator, createOnly: true, ct);

    public Task<CanonicalSkillModel> UpsertSkillDirectAsync(CanonicalSkillModel model, string originator = "api",
        CancellationToken ct = default) => SaveDirectAsync(model, originator, createOnly: false, ct);

    private Task<CanonicalSkillModel> SaveDirectAsync(CanonicalSkillModel model, string originator, bool createOnly, CancellationToken ct)
    {
        var slug = GetSlug(model);
        SkillStorage.ValidateModel(model);
        var input = model.Clone();
        return RunAsync(async () =>
        {
            ProtectSystemSkill(slug, originator);
            var current = await LoadCurrentAsync(slug, GetEntry(slug)).ConfigureAwait(false);
            ProtectSystemCategory(current, originator);
            if (current is not null && (createOnly || input.Revision > 0 && input.Revision != current.Revision))
            {
                throw new SkillConflictException($"Skill '{slug}' already exists or was changed by another operation.");
            }
            if (input.IsCatalogOnly || input.CatalogId is not null && current?.CatalogId != input.CatalogId)
            {
                throw new SkillConflictException("Use the catalog activation or clone command to create a managed copy.");
            }
            if (current is not null && input.Revision == 0 && originator != "bootstrap-seed")
            {
                throw new SkillConflictException($"A current revision is required to replace '{slug}'.");
            }
            if (current is not null)
            {
                input.SourceCategory = current.SourceCategory;
                input.OriginProvider = current.OriginProvider;
                input.CatalogId = current.CatalogId;
            }
            else if (originator == "bootstrap-seed")
            {
                input.SourceCategory = "system";
                input.OriginProvider = "SkillHub System";
            }
            else if (originator.StartsWith("webui:", StringComparison.Ordinal) || originator.StartsWith("mcp:", StringComparison.Ordinal) || originator == "api")
            {
                input.SourceCategory = "user";
            }

            return await SaveAndProjectAsync(slug, input, originator).ConfigureAwait(false);
        }, ct);
    }

    public Task<CanonicalSkillModel> EditSkillAsync(string name, Action<CanonicalSkillModel> patch,
        string originator, CancellationToken ct = default, long? expectedRevision = null)
    {
        var slug = RequireSlug(name);
        return RunAsync(async () =>
        {
            ProtectSystemSkill(slug, originator);
            var current = await LoadCurrentAsync(slug, GetEntry(slug)).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Managed skill '{slug}' was not found.");
            ProtectSystemCategory(current, originator);
            if (expectedRevision is not null && expectedRevision != current.Revision)
            {
                throw new SkillConflictException($"Skill '{slug}' changed since it was opened.");
            }

            patch(current);
            return await SaveAndProjectAsync(slug, current, originator).ConfigureAwait(false);
        }, ct);
    }

    public Task<CanonicalSkillModel> SetProviderAsync(string name, ProviderType provider, bool enabled, string originator,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(provider) || provider == ProviderType.Canonical)
        {
            throw new ArgumentException("An external provider is required.");
        }
        return EditSkillAsync(name, model => model.SetEnabledFor(provider, enabled), originator, ct);
    }

    public Task<CanonicalSkillModel> MaterializeCatalogSkillAsync(string id, CanonicalSkillModel? draft, bool activate,
        string originator, CancellationToken ct = default) => RunAsync(async () =>
    {
        var source = _catalog.Get(id) ?? throw new KeyNotFoundException("The catalog entry no longer exists.");
        if (source.SourceCategory == "system")
        {
            throw new SkillConflictException("System catalog entries are read-only.");
        }

        var model = draft ?? source.Clone();
        var slug = draft is not null ? GetSlug(draft) : activate ? source.Name : RequireSlug($"{source.Name}-copy");
        ProtectSystemSkill(slug, originator);
        if (await LoadCurrentAsync(slug, GetEntry(slug)).ConfigureAwait(false) is not null)
        {
            throw new SkillConflictException($"A managed skill already uses the name '{slug}'.");
        }
        model.CatalogId = source.CatalogId;
        model.SourceCategory = activate ? source.SourceCategory : "user";
        model.OriginProvider = source.OriginProvider;
        model.IsCatalogOnly = false;
        foreach (var (key, value) in source.Passthrough)
        {
            model.Passthrough.TryAdd(key, value);
        }
        model.Targets.Claude.Model ??= source.Targets.Claude.Model;
        if (!activate && draft is null)
        {
            foreach (var provider in _transpilers.Keys.Where(provider => provider != ProviderType.Canonical))
            {
                model.SetEnabledFor(provider, false);
            }
        }
        return await SaveAndProjectAsync(slug, model, originator).ConfigureAwait(false);
    }, ct);

    public Task<bool> DeleteSkillDirectAsync(string name, string originator = "api",
        CancellationToken ct = default)
    {
        var slug = RequireSlug(name);
        return RunAsync(async () =>
        {
            ProtectSystemSkill(slug, originator);
            var entry = GetEntry(slug);
            var current = await LoadCurrentAsync(slug, entry).ConfigureAwait(false);
            if (current is null)
            {
                return false;
            }
            ProtectSystemCategory(current, originator);
            entry.Deleted = true;
            entry.Tombstone = $"{slug}-{Guid.NewGuid():N}";
            await PersistAsync().ConfigureAwait(false);
            await FinishDeleteAsync(slug, entry).ConfigureAwait(false);
            PublishActivity(slug, originator, "deleted", $"Moved '{slug}' to SkillHub trash and withdrew its owned projections.");
            return true;
        }, ct);
    }

    public Task<CanonicalSkillModel> RestoreSkillAsync(string name, CancellationToken ct = default)
    {
        var slug = RequireSlug(name);
        return RunAsync(async () =>
        {
            var entry = GetEntry(slug);
            if (!entry.Deleted || entry.Tombstone is null)
            {
                throw new KeyNotFoundException($"No deleted skill named '{slug}' is available.");
            }
            var source = SkillPaths.Resolve(_discovery.TrashRoot, entry.Tombstone);
            var destination = Path.GetDirectoryName(_storage.CanonicalPath(slug))!;
            if (Directory.Exists(destination))
            {
                throw new SkillConflictException($"A folder already occupies '{slug}'.");
            }
            Directory.Move(source, destination);
            entry.Deleted = false;
            entry.Tombstone = null;
            entry.CanonicalDiskHash = null;
            await PersistAsync().ConfigureAwait(false);
            var model = await LoadCurrentAsync(slug, entry).ConfigureAwait(false)
                ?? throw new InvalidDataException("The restored bundle does not contain SKILL.md.");
            await ReconcileSkillAsync(slug, [], "restore").ConfigureAwait(false);
            return _managed.GetValueOrDefault(slug)?.Clone() ?? model;
        }, ct);
    }

    public async Task<int> RenameTagAcrossSkillsAsync(string oldTag, string newTag, CancellationToken ct = default) =>
        (await ChangeTagAsync(oldTag, newTag, ct).ConfigureAwait(false)).UpdatedSkills;

    public async Task<int> DeleteTagAcrossSkillsAsync(string tag, CancellationToken ct = default) =>
        (await ChangeTagAsync(tag, null, ct).ConfigureAwait(false)).UpdatedSkills;

    public static string NormalizeTag(string tag)
    {
        var normalized = ProviderDiscoveryService.NormalizeSlug(tag);
        if (normalized.Length is 0 or > 64 || normalized is "system" or "skillhub" or "meta-skill")
        {
            throw new ArgumentException("A valid non-reserved tag is required.");
        }
        return normalized;
    }

    private static void ProtectSystemSkill(string slug, string originator)
    {
        if ((slug == BootstrapSkillName && originator != "bootstrap-seed") || slug == ProviderDiscoveryService.LegacyBootstrapSkillName)
        {
            throw new SkillConflictException($"System skill '{slug}' is protected.");
        }
    }

    private static void ProtectSystemCategory(CanonicalSkillModel? model, string originator)
    {
        if (model?.SourceCategory == "system" && originator != "bootstrap-seed")
        {
            throw new SkillConflictException($"System skill '{model.Name}' is protected.");
        }
    }

    private static string GetSlug(CanonicalSkillModel model) => RequireSlug(
        string.IsNullOrWhiteSpace(model.Name) ? ProviderDiscoveryService.NormalizeSlug(model.Title) : model.Name);

    private static string RequireSlug(string name)
    {
        SkillPaths.ValidateSegment(name);
        var slug = ProviderDiscoveryService.NormalizeSlug(name);
        if (slug.Length is 0 or > 64)
        {
            throw new ArgumentException("A skill name must contain 1 to 64 slug characters.");
        }
        SkillPaths.ValidateSegment(slug);
        return slug;
    }

    private static string ProviderName(ProviderType provider) => provider.ToString().ToLowerInvariant();

    private SkillLedgerEntry GetEntry(string slug) => _ledger.Skills.TryGetValue(slug, out var entry)
        ? entry : _ledger.Skills[slug] = new SkillLedgerEntry();

    private Task PersistAsync() => _storage.SaveLedgerAsync(_ledger);

    private void PublishActivity(string slug, string source, string action, string details)
    {
        var item = new SyncActivityNotification(DateTimeOffset.UtcNow, slug, source, action, details, Interlocked.Increment(ref _activitySequence));
        _recentActivity.Enqueue(item);
        while (_recentActivity.Count > 100 && _recentActivity.TryDequeue(out _))
        {
        }
        var listeners = ActivityPublished?.GetInvocationList() ?? [];
        foreach (var listener in listeners.Cast<Action<SyncActivityNotification>>())
        {
            try
            {
                listener(item);
            }
            catch
            {
                // A disconnected subscriber must never affect a committed operation.
            }
        }
    }
}
