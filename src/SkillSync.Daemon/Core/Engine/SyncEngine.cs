using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Core.Transpilers;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed class SyncEngine : IAsyncDisposable
{
    public const string BootstrapSkillName = "00-skillhub-bootstrap";

    private static readonly JsonSerializerOptions LedgerJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ProviderDiscoveryService _discovery;
    private readonly Dictionary<ProviderType, ISkillTranspiler> _transpilers;
    private readonly Channel<SyncEvent> _eventChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, CanonicalSkillModel> _skillsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CanonicalSkillModel> _catalogCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<SyncActivityNotification> _recentActivity = new();
    private readonly object _ledgerLock = new();
    private SyncLedger _ledger = new();

    public event Action<SyncActivityNotification>? ActivityPublished;

    public SyncEngine(ProviderDiscoveryService discovery)
    {
        _discovery = discovery;
        _transpilers = new Dictionary<ProviderType, ISkillTranspiler>
        {
            [ProviderType.Canonical] = new CanonicalTranspiler(),
            [ProviderType.Claude] = new ClaudeTranspiler(),
            [ProviderType.Gemini] = new GeminiTranspiler(),
            [ProviderType.Codex] = new CodexTranspiler(),
            [ProviderType.Cursor] = new CursorMdcTranspiler()
        };

        _eventChannel = Channel.CreateUnbounded<SyncEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        LoadLedgerSync();
        LoadCanonicalSkillsSync();
        LoadExternalCatalogSkillsSync();

        _consumerTask = Task.Run(() => ProcessQueueLoopAsync(_cts.Token));
    }

    public IReadOnlyCollection<SyncActivityNotification> GetRecentActivity() => _recentActivity.ToArray();

    public IReadOnlyList<CanonicalSkillModel> ListSkills(
        string? query = null,
        string? tag = null,
        bool includeDisabled = true,
        string? category = null)
    {
        var combined = new List<CanonicalSkillModel>(_skillsCache.Count + _catalogCache.Count);

        foreach (var canonical in _skillsCache.Values)
        {
            var clone = canonical.Clone();
            clone.IsCatalogOnly = false;
            if (string.Equals(clone.Name, BootstrapSkillName, StringComparison.OrdinalIgnoreCase))
            {
                clone.SourceCategory = "system";
                clone.OriginProvider = "SkillHub System";
            }
            combined.Add(clone);
        }

        foreach (var catalog in _catalogCache.Values)
        {
            if (!_skillsCache.ContainsKey(catalog.Name))
            {
                var clone = catalog.Clone();
                clone.IsCatalogOnly = true;
                combined.Add(clone);
            }
        }

        var items = combined.AsEnumerable();

        if (!includeDisabled)
        {
            items = items.Where(s =>
                s.IsCatalogOnly ||
                s.Targets.Claude.Enabled ||
                s.Targets.Gemini.Enabled ||
                s.Targets.Codex.Enabled ||
                s.Targets.Cursor.Enabled);
        }

        if (!string.IsNullOrWhiteSpace(category) &&
            !string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
        {
            var cleanCat = category.Trim();
            if (string.Equals(cleanCat, "synced", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cleanCat, "active", StringComparison.OrdinalIgnoreCase))
            {
                items = items.Where(s =>
                    !s.IsCatalogOnly &&
                    !string.Equals(s.SourceCategory, "system", StringComparison.OrdinalIgnoreCase) &&
                    (s.Targets.Claude.Enabled ||
                     s.Targets.Gemini.Enabled ||
                     s.Targets.Codex.Enabled ||
                     s.Targets.Cursor.Enabled));
            }
            else
            {
                items = items.Where(s => string.Equals(s.SourceCategory, cleanCat, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var cleanTag = tag.Trim();
            items = items.Where(s => s.Tags.Any(t => string.Equals(t, cleanTag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            items = items.Where(s =>
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.GetEffectiveTitle().Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.WhenToUse.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.OriginProvider.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.InstructionsMarkdown.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        return items
            .OrderBy(GetCategorySortRank)
            .ThenBy(GetOriginSortRank)
            .ThenBy(s => s.GetEffectiveTitle(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetCategorySortRank(CanonicalSkillModel s) => s.SourceCategory.ToLowerInvariant() switch
    {
        "user" => 0,
        "template" => 1,
        "system" => 2,
        "plugin" => 3,
        _ => 4
    };

    private static int GetOriginSortRank(CanonicalSkillModel s)
    {
        if (!s.IsCatalogOnly) return 0;
        if (s.OriginProvider.StartsWith("OpenAI Template", StringComparison.OrdinalIgnoreCase)) return 1;
        if (s.OriginProvider.StartsWith("OpenAI Built-in", StringComparison.OrdinalIgnoreCase)) return 2;
        if (s.OriginProvider.StartsWith("Antigravity Built-in", StringComparison.OrdinalIgnoreCase)) return 3;
        if (s.OriginProvider.StartsWith("Codex System", StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    public CanonicalSkillModel? GetSkill(string skillSlug)
    {
        var cleanSlug = ProviderDiscoveryService.NormalizeSlug(skillSlug);
        if (_skillsCache.TryGetValue(cleanSlug, out var model))
        {
            var clone = model.Clone();
            clone.IsCatalogOnly = false;
            return clone;
        }

        if (_catalogCache.TryGetValue(cleanSlug, out var catalogModel))
        {
            var clone = catalogModel.Clone();
            clone.IsCatalogOnly = true;
            return clone;
        }

        return null;
    }

    public Dictionary<string, string> PreviewAllTranspilations(CanonicalSkillModel model)
    {
        var cleanSlug = ProviderDiscoveryService.NormalizeSlug(string.IsNullOrWhiteSpace(model.Name) ? "preview-skill" : model.Name);
        var previewModel = model.Clone();
        previewModel.Name = cleanSlug;
        var canonicalDir = _discovery.GetSkillDirectoryPath(ProviderType.Canonical, cleanSlug);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["canonical"] = _transpilers[ProviderType.Canonical].TranspileOutbound(previewModel, canonicalDir),
            ["claude"] = _transpilers[ProviderType.Claude].TranspileOutbound(previewModel, canonicalDir),
            ["gemini"] = _transpilers[ProviderType.Gemini].TranspileOutbound(previewModel, canonicalDir),
            ["codex"] = _transpilers[ProviderType.Codex].TranspileOutbound(previewModel, canonicalDir),
            ["cursor"] = _transpilers[ProviderType.Cursor].TranspileOutbound(previewModel, canonicalDir)
        };
    }

    public async Task<CanonicalSkillModel> UpsertSkillDirectAsync(
        CanonicalSkillModel model,
        string originator = "api",
        CancellationToken ct = default)
    {
        var cleanSlug = ProviderDiscoveryService.NormalizeSlug(
            !string.IsNullOrWhiteSpace(model.Name) ? model.Name : model.Title);

        if (string.IsNullOrWhiteSpace(cleanSlug))
        {
            throw new ArgumentException("Skill name must be a valid non-empty slug or title.", nameof(model));
        }

        model.Name = cleanSlug;
        model.Title = model.GetEffectiveTitle();
        model.IsCatalogOnly = false;

        if (string.Equals(cleanSlug, BootstrapSkillName, StringComparison.OrdinalIgnoreCase))
        {
            model.SourceCategory = "system";
            model.OriginProvider = "SkillHub System";
        }
        else if (string.Equals(model.SourceCategory, "template", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(model.SourceCategory, "plugin", StringComparison.OrdinalIgnoreCase))
        {
            // Keep template/plugin category intact when enabling sync or toggling providers on a built-in template
            if (string.IsNullOrWhiteSpace(model.OriginProvider) &&
                _catalogCache.TryGetValue(cleanSlug, out var catOrig))
            {
                model.OriginProvider = catOrig.OriginProvider;
            }
        }
        else if (originator.StartsWith("webui", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(originator, "api", StringComparison.OrdinalIgnoreCase) ||
                 originator.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase))
        {
            model.SourceCategory = "user";
            if (string.IsNullOrWhiteSpace(model.OriginProvider))
            {
                model.OriginProvider = "My Skill";
            }
        }

        var tcs = new TaskCompletionSource<CanonicalSkillModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evt = new SyncEvent(
            ProviderType.Canonical,
            cleanSlug,
            SyncChangeKind.CreatedOrModified,
            _discovery.GetSkillPrimaryFilePath(ProviderType.Canonical, cleanSlug),
            originator,
            DateTimeOffset.UtcNow,
            DirectModelOverride: model.Clone(),
            CompletionSource: tcs);

        await _eventChannel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
        var result = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        return result ?? model;
    }

    public async Task<bool> DeleteSkillDirectAsync(
        string skillSlug,
        string originator = "api",
        bool allowBootstrapDelete = false,
        CancellationToken ct = default)
    {
        var cleanSlug = ProviderDiscoveryService.NormalizeSlug(skillSlug);
        if (!allowBootstrapDelete && string.Equals(cleanSlug, BootstrapSkillName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The system meta-skill '{BootstrapSkillName}' is protected and cannot be deleted.");
        }

        if (!_skillsCache.ContainsKey(cleanSlug))
        {
            return false;
        }

        var tcs = new TaskCompletionSource<CanonicalSkillModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evt = new SyncEvent(
            ProviderType.Canonical,
            cleanSlug,
            SyncChangeKind.Deleted,
            _discovery.GetSkillPrimaryFilePath(ProviderType.Canonical, cleanSlug),
            originator,
            DateTimeOffset.UtcNow,
            CompletionSource: tcs);

        await _eventChannel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<int> RenameTagAcrossSkillsAsync(string oldTag, string newTag, CancellationToken ct = default)
    {
        var cleanOld = oldTag.Trim().TrimStart('#').ToLowerInvariant();
        var cleanNew = ProviderDiscoveryService.NormalizeSlug(newTag);
        if (string.IsNullOrWhiteSpace(cleanOld) || string.IsNullOrWhiteSpace(cleanNew) ||
            string.Equals(cleanOld, cleanNew, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var affectedCount = 0;
        foreach (var skill in _skillsCache.Values.ToList())
        {
            if (skill.Tags.Any(t => string.Equals(t, cleanOld, StringComparison.OrdinalIgnoreCase)))
            {
                var updated = skill.Clone();
                updated.Tags = updated.Tags
                    .Select(t => string.Equals(t, cleanOld, StringComparison.OrdinalIgnoreCase) ? cleanNew : t)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await UpsertSkillDirectAsync(updated, "webui:tag-rename", ct).ConfigureAwait(false);
                affectedCount++;
            }
        }

        foreach (var catSkill in _catalogCache.Values.ToList())
        {
            if (catSkill.Tags.Any(t => string.Equals(t, cleanOld, StringComparison.OrdinalIgnoreCase)))
            {
                catSkill.Tags = catSkill.Tags
                    .Select(t => string.Equals(t, cleanOld, StringComparison.OrdinalIgnoreCase) ? cleanNew : t)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return affectedCount;
    }

    public async Task<int> DeleteTagAcrossSkillsAsync(string tagToDelete, CancellationToken ct = default)
    {
        var cleanTag = tagToDelete.Trim().TrimStart('#').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanTag))
        {
            return 0;
        }

        var affectedCount = 0;
        foreach (var skill in _skillsCache.Values.ToList())
        {
            if (skill.Tags.Any(t => string.Equals(t, cleanTag, StringComparison.OrdinalIgnoreCase)))
            {
                var updated = skill.Clone();
                updated.Tags = updated.Tags
                    .Where(t => !string.Equals(t, cleanTag, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                await UpsertSkillDirectAsync(updated, "webui:tag-delete", ct).ConfigureAwait(false);
                affectedCount++;
            }
        }

        foreach (var catSkill in _catalogCache.Values.ToList())
        {
            if (catSkill.Tags.Any(t => string.Equals(t, cleanTag, StringComparison.OrdinalIgnoreCase)))
            {
                catSkill.Tags = catSkill.Tags
                    .Where(t => !string.Equals(t, cleanTag, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        return affectedCount;
    }

    public void EnqueueFileSystemEvent(ProviderType provider, string skillSlug, SyncChangeKind kind, string fullPath)
    {
        var cleanSlug = ProviderDiscoveryService.NormalizeSlug(skillSlug);
        if (string.IsNullOrEmpty(cleanSlug) ||
            string.Equals(cleanSlug, ProviderDiscoveryService.LegacyBootstrapSkillName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var evt = new SyncEvent(
            provider,
            cleanSlug,
            kind,
            fullPath,
            $"watcher:{provider.ToString().ToLowerInvariant()}",
            DateTimeOffset.UtcNow);

        _eventChannel.Writer.TryWrite(evt);
    }

    public async Task SynchronizeAllAsync(CancellationToken ct = default)
    {
        // 1. Discover any native provider user skills (including nested subdirectories that do not start with '.')
        var directoryScanTargets = new List<(ProviderType Provider, string SkillsDir)>
        {
            (ProviderType.Claude, Path.Combine(_discovery.UserHomeRoot, ".claude", "skills")),
            (ProviderType.Claude, Path.Combine(_discovery.UserHomeRoot, ".claude", "commands")),
            (ProviderType.Gemini, Path.Combine(_discovery.UserHomeRoot, ".gemini", "skills")),
            (ProviderType.Gemini, Path.Combine(_discovery.UserHomeRoot, ".gemini", "antigravity", "skills")),
            (ProviderType.Codex, Path.Combine(_discovery.UserHomeRoot, ".codex", "skills")),
            (ProviderType.Codex, Path.Combine(_discovery.UserHomeRoot, ".agents", "skills")),
            (ProviderType.Cursor, Path.Combine(_discovery.UserHomeRoot, ".cursor", "skills"))
        };

        foreach (var (provider, skillsDir) in directoryScanTargets)
        {
            if (!Directory.Exists(skillsDir))
            {
                continue;
            }

            foreach (var skillMd in EnumerateUserSkillMarkdownFiles(skillsDir))
            {
                var dir = Path.GetDirectoryName(skillMd)!;
                var folderName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(folderName) ||
                    folderName.StartsWith('.') ||
                    string.Equals(folderName, ProviderDiscoveryService.LegacyBootstrapSkillName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var slug = ProviderDiscoveryService.NormalizeSlug(folderName);
                if (!string.IsNullOrEmpty(slug) && !_skillsCache.ContainsKey(slug))
                {
                    await WaitForSingleEventAsync(new SyncEvent(
                        provider,
                        slug,
                        SyncChangeKind.CreatedOrModified,
                        skillMd,
                        $"autodiscovery:{provider.ToString().ToLowerInvariant()}",
                        DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
                }
            }
        }

        // Check Claude ~/.claude/commands/*.md top-level command files with frontmatter
        var claudeCommandsDir = Path.Combine(_discovery.UserHomeRoot, ".claude", "commands");
        if (Directory.Exists(claudeCommandsDir))
        {
            foreach (var cmdFile in Directory.EnumerateFiles(claudeCommandsDir, "*.md", SearchOption.TopDirectoryOnly))
            {
                if (AtomicFileWriter.IsTemporaryOrIgnoredPath(cmdFile))
                {
                    continue;
                }

                var slug = ProviderDiscoveryService.NormalizeSlug(Path.GetFileNameWithoutExtension(cmdFile));
                if (!string.IsNullOrEmpty(slug) &&
                    !string.Equals(slug, ProviderDiscoveryService.LegacyBootstrapSkillName, StringComparison.OrdinalIgnoreCase) &&
                    !_skillsCache.ContainsKey(slug))
                {
                    await WaitForSingleEventAsync(new SyncEvent(
                        ProviderType.Claude,
                        slug,
                        SyncChangeKind.CreatedOrModified,
                        cmdFile,
                        "autodiscovery:claude-commands",
                        DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
                }
            }
        }

        // Check Cursor ~/.cursor/rules/*.mdc
        var cursorRulesDir = Path.Combine(_discovery.UserHomeRoot, ".cursor", "rules");
        if (Directory.Exists(cursorRulesDir))
        {
            foreach (var mdcFile in Directory.EnumerateFiles(cursorRulesDir, "*.mdc", SearchOption.TopDirectoryOnly))
            {
                if (AtomicFileWriter.IsTemporaryOrIgnoredPath(mdcFile))
                {
                    continue;
                }

                var slug = ProviderDiscoveryService.NormalizeSlug(Path.GetFileNameWithoutExtension(mdcFile));
                if (!string.IsNullOrEmpty(slug) &&
                    !string.Equals(slug, ProviderDiscoveryService.LegacyBootstrapSkillName, StringComparison.OrdinalIgnoreCase) &&
                    !_skillsCache.ContainsKey(slug))
                {
                    await WaitForSingleEventAsync(new SyncEvent(
                        ProviderType.Cursor,
                        slug,
                        SyncChangeKind.CreatedOrModified,
                        mdcFile,
                        "autodiscovery:cursor",
                        DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
                }
            }
        }

        // 2. Refresh external catalog skills (System skills, OpenAI Templates, Built-in capabilities & Plugins)
        LoadExternalCatalogSkillsSync();

        // 3. Push all canonical skills to all installed & enabled providers
        foreach (var skill in _skillsCache.Values.ToList())
        {
            await UpsertSkillDirectAsync(skill, "startup-sync", ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> EnumerateUserSkillMarkdownFiles(string rootSkillsDir)
    {
        var results = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(rootSkillsDir);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                var name = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(name) ||
                    name.StartsWith('.') ||
                    ProviderDiscoveryService.IsAllowedSupportingFolder(name))
                {
                    continue;
                }

                var candidateSkillMd = Path.Combine(subDir, "SKILL.md");
                if (File.Exists(candidateSkillMd))
                {
                    results.Add(candidateSkillMd);
                }
                else
                {
                    queue.Enqueue(subDir);
                }
            }
        }

        return results;
    }

    public bool IsLoopGuardMatch(ProviderType provider, string skillSlug, string normalizedRawHash)
    {
        lock (_ledgerLock)
        {
            if (!_ledger.Skills.TryGetValue(skillSlug, out var skillEntry))
            {
                return false;
            }

            var key = provider.ToString().ToLowerInvariant();
            if (skillEntry.Targets.TryGetValue(key, out var targetLedger) &&
                string.Equals(targetLedger.LastWrittenRawHash, normalizedRawHash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (provider == ProviderType.Codex &&
                skillEntry.Targets.TryGetValue("codex_agents", out var agentsLedger) &&
                string.Equals(agentsLedger.LastWrittenRawHash, normalizedRawHash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }

    private async Task WaitForSingleEventAsync(SyncEvent evt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<CanonicalSkillModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var withTcs = evt with { CompletionSource = tcs };
        await _eventChannel.Writer.WriteAsync(withTcs, ct).ConfigureAwait(false);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task ProcessQueueLoopAsync(CancellationToken ct)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var result = await HandleSyncEventInternalAsync(evt, ct).ConfigureAwait(false);
                evt.CompletionSource?.TrySetResult(result);
            }
            catch (Exception ex)
            {
                PublishActivity(evt.SkillSlug, evt.Originator, "error", ex.Message);
                evt.CompletionSource?.TrySetException(ex);
            }
        }
    }

    private async Task<CanonicalSkillModel?> HandleSyncEventInternalAsync(SyncEvent evt, CancellationToken ct)
    {
        var slug = ProviderDiscoveryService.NormalizeSlug(evt.SkillSlug);
        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        if (evt.ChangeKind == SyncChangeKind.Deleted)
        {
            await HandleSkillDeletionAsync(slug, evt.SourceProvider, evt.Originator, ct).ConfigureAwait(false);
            return null;
        }

        CanonicalSkillModel mergedModel;
        var existingCanonical = _skillsCache.TryGetValue(slug, out var current) ? current.Clone() : null;
        var canonicalDir = _discovery.GetSkillDirectoryPath(ProviderType.Canonical, slug)!;

        if (evt.DirectModelOverride is not null)
        {
            mergedModel = evt.DirectModelOverride.Clone();
            mergedModel.Name = slug;
            mergedModel.Title = mergedModel.GetEffectiveTitle();
            if (existingCanonical is not null && mergedModel.SupportingFiles.Count == 0 && existingCanonical.SupportingFiles.Count > 0)
            {
                mergedModel.SupportingFiles = new Dictionary<string, string>(existingCanonical.SupportingFiles, StringComparer.Ordinal);
            }
            else if (existingCanonical is null && _catalogCache.TryGetValue(slug, out var catalogSource) && mergedModel.SupportingFiles.Count == 0)
            {
                mergedModel.SupportingFiles = new Dictionary<string, string>(catalogSource.SupportingFiles, StringComparer.Ordinal);
            }
        }
        else
        {
            var primaryFilePath = ResolveActualSkillFilePath(evt.SourceProvider, slug, evt.FullPath);
            var (readSuccess, rawContent) = await AtomicFileWriter.ReadTextWithRetryAsync(primaryFilePath, ct).ConfigureAwait(false);
            if (!readSuccess || string.IsNullOrWhiteSpace(rawContent))
            {
                return existingCanonical;
            }

            // STEP 4: Layer-1 Loop Guard (Raw File Hash Check)
            var incomingRawHash = ContentHasher.ComputeRawFileHash(rawContent);
            if (IsLoopGuardMatch(evt.SourceProvider, slug, incomingRawHash))
            {
                return existingCanonical;
            }

            if (!FrontmatterParser.TrySplitFrontmatter(rawContent, out _, out _))
            {
                return existingCanonical;
            }

            SkillLedgerEntry? ledgerEntry;
            lock (_ledgerLock)
            {
                _ledger.Skills.TryGetValue(slug, out ledgerEntry);
            }

            var transpiler = _transpilers[evt.SourceProvider];
            mergedModel = transpiler.MergeInbound(slug, rawContent, existingCanonical, ledgerEntry, canonicalDir);
            mergedModel.Title = mergedModel.GetEffectiveTitle();

            var sourceSkillDir = evt.SourceProvider == ProviderType.Cursor
                ? null
                : Path.GetDirectoryName(primaryFilePath);

            if (!string.IsNullOrEmpty(sourceSkillDir) && Directory.Exists(sourceSkillDir))
            {
                mergedModel.SupportingFiles = ReadSupportingFilesFromDisk(sourceSkillDir);
            }
            else if (existingCanonical is not null)
            {
                mergedModel.SupportingFiles = new Dictionary<string, string>(existingCanonical.SupportingFiles, StringComparer.Ordinal);
            }

            // STEP 5: Layer-2 Loop Guard (Semantic Hash Check)
            var incomingSemanticHash = ContentHasher.ComputeSemanticHash(mergedModel);
            lock (_ledgerLock)
            {
                if (_ledger.Skills.TryGetValue(slug, out var entry) &&
                    string.Equals(entry.CanonicalSemanticHash, incomingSemanticHash, StringComparison.OrdinalIgnoreCase) &&
                    evt.SourceProvider != ProviderType.Canonical)
                {
                    var providerKey = evt.SourceProvider.ToString().ToLowerInvariant();
                    entry.Targets[providerKey] = new TargetFileLedger
                    {
                        Path = primaryFilePath.Replace('\\', '/'),
                        LastWrittenRawHash = incomingRawHash,
                        LastWriteTimestampUtc = DateTimeOffset.UtcNow
                    };
                    SaveLedgerSync();
                    return existingCanonical;
                }
            }
        }

        // STEP 6 & 7: Propagate to Canonical and all installed/enabled Provider Targets
        await PropagateCanonicalModelToAllTargetsAsync(mergedModel, evt.SourceProvider, evt.Originator, ct).ConfigureAwait(false);
        return mergedModel;
    }

    private string ResolveActualSkillFilePath(ProviderType provider, string slug, string? eventFullPath)
    {
        if (!string.IsNullOrWhiteSpace(eventFullPath))
        {
            if (provider == ProviderType.Cursor && eventFullPath.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase))
            {
                return eventFullPath;
            }

            if (eventFullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && File.Exists(eventFullPath))
            {
                return eventFullPath;
            }
        }

        if (provider == ProviderType.Codex)
        {
            foreach (var dir in _discovery.GetAllCodexSkillDirectories(slug))
            {
                var candidate = Path.Combine(dir, "SKILL.md");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return _discovery.GetSkillPrimaryFilePath(provider, slug);
    }

    private async Task PropagateCanonicalModelToAllTargetsAsync(
        CanonicalSkillModel model,
        ProviderType sourceProvider,
        string originator,
        CancellationToken ct)
    {
        var slug = ProviderDiscoveryService.NormalizeSlug(model.Name);
        model.Name = slug;
        model.Title = model.GetEffectiveTitle();
        model.IsCatalogOnly = false;
        var canonicalPrimaryPath = _discovery.GetSkillPrimaryFilePath(ProviderType.Canonical, slug);
        model.SourcePath = canonicalPrimaryPath.Replace('\\', '/');

        var semanticHash = ContentHasher.ComputeSemanticHash(model);
        var canonicalDir = _discovery.GetSkillDirectoryPath(ProviderType.Canonical, slug)!;

        SkillLedgerEntry ledgerEntry;
        lock (_ledgerLock)
        {
            if (!_ledger.Skills.TryGetValue(slug, out var existingEntry))
            {
                existingEntry = new SkillLedgerEntry();
                _ledger.Skills[slug] = existingEntry;
            }

            existingEntry.CanonicalSemanticHash = semanticHash;
            existingEntry.LastUpdatedUtc = DateTimeOffset.UtcNow;
            existingEntry.LastUpdatedBy = originator;
            existingEntry.LastSynthesizedGeminiDescription = GeminiTranspiler.BuildSingleLineDescription(model.Description, model.WhenToUse);
            existingEntry.LastSynthesizedCodexDescription = CodexTranspiler.BuildConciseDescription(model);
            existingEntry.LastSynthesizedCursorDescription = CursorMdcTranspiler.BuildCursorDescription(model);
            ledgerEntry = existingEntry;
        }

        _skillsCache[slug] = model.Clone();
        var syncedProviders = new List<string>();

        foreach (var providerInfo in _discovery.DiscoverProviders())
        {
            var targetProvider = providerInfo.Provider;
            var isEnabled = model.IsEnabledFor(targetProvider);
            var targetPrimaryPath = _discovery.GetSkillPrimaryFilePath(targetProvider, slug);
            var targetDir = _discovery.GetSkillDirectoryPath(targetProvider, slug);
            var providerKey = targetProvider.ToString().ToLowerInvariant();

            if (!isEnabled && targetProvider != ProviderType.Canonical)
            {
                await RemoveTargetSkillFromDiskAsync(targetProvider, slug, providerKey, ledgerEntry).ConfigureAwait(false);
                continue;
            }

            if (!providerInfo.IsInstalled && targetProvider != ProviderType.Canonical)
            {
                continue;
            }

            var transpiledContent = _transpilers[targetProvider].TranspileOutbound(model, canonicalDir);
            var expectedRawHash = ContentHasher.ComputeRawFileHash(transpiledContent);

            if (targetProvider != sourceProvider && File.Exists(targetPrimaryPath))
            {
                var (existsOk, currentDiskContent) = await AtomicFileWriter.ReadTextWithRetryAsync(targetPrimaryPath, ct).ConfigureAwait(false);
                if (existsOk && !string.IsNullOrWhiteSpace(currentDiskContent))
                {
                    var currentDiskHash = ContentHasher.ComputeRawFileHash(currentDiskContent);
                    string? recordedHash = null;
                    lock (_ledgerLock)
                    {
                        if (ledgerEntry.Targets.TryGetValue(providerKey, out var tl))
                        {
                            recordedHash = tl.LastWrittenRawHash;
                        }
                    }

                    if (!string.IsNullOrEmpty(recordedHash) &&
                        !string.Equals(currentDiskHash, recordedHash, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(currentDiskHash, expectedRawHash, StringComparison.OrdinalIgnoreCase))
                    {
                        await SaveConflictBackupAsync(slug, targetProvider, currentDiskContent, ct).ConfigureAwait(false);
                    }
                }
            }

            // Pre-register expected hash BEFORE writing to disk
            lock (_ledgerLock)
            {
                ledgerEntry.Targets[providerKey] = new TargetFileLedger
                {
                    Path = targetPrimaryPath.Replace('\\', '/'),
                    LastWrittenRawHash = expectedRawHash,
                    LastWriteTimestampUtc = DateTimeOffset.UtcNow
                };
            }

            await WriteIfChangedAsync(targetPrimaryPath, transpiledContent, expectedRawHash, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(targetDir))
            {
                await SyncSupportingFilesToDirectoryAsync(targetDir, model.SupportingFiles, ct).ConfigureAwait(false);
            }

            // Dual-sync for Codex: ensure BOTH ~/.codex/skills/<slug> and ~/.agents/skills/<slug> are populated!
            if (targetProvider == ProviderType.Codex)
            {
                foreach (var codexSkillDir in _discovery.GetAllCodexSkillDirectories(slug))
                {
                    var codexFile = Path.Combine(codexSkillDir, "SKILL.md");
                    lock (_ledgerLock)
                    {
                        ledgerEntry.Targets["codex_agents"] = new TargetFileLedger
                        {
                            Path = codexFile.Replace('\\', '/'),
                            LastWrittenRawHash = expectedRawHash,
                            LastWriteTimestampUtc = DateTimeOffset.UtcNow
                        };
                    }

                    await WriteIfChangedAsync(codexFile, transpiledContent, expectedRawHash, ct).ConfigureAwait(false);
                    await SyncSupportingFilesToDirectoryAsync(codexSkillDir, model.SupportingFiles, ct).ConfigureAwait(false);
                }
            }

            // Mirror to ~/.gemini/antigravity/skills/<slug> if Gemini is enabled and Antigravity directory exists
            if (targetProvider == ProviderType.Gemini)
            {
                var antigravityDir = _discovery.GetAntigravityMirrorSkillDirectory(slug);
                if (!string.IsNullOrEmpty(antigravityDir))
                {
                    var antigravityFile = Path.Combine(antigravityDir, "SKILL.md");
                    await WriteIfChangedAsync(antigravityFile, transpiledContent, expectedRawHash, ct).ConfigureAwait(false);
                    await SyncSupportingFilesToDirectoryAsync(antigravityDir, model.SupportingFiles, ct).ConfigureAwait(false);
                }
            }

            if (targetProvider != ProviderType.Canonical)
            {
                syncedProviders.Add(providerInfo.DisplayName);
            }
        }

        lock (_ledgerLock)
        {
            SaveLedgerSync();
        }

        var providersSummary = syncedProviders.Count > 0 ? string.Join(", ", syncedProviders) : "local canonical store";
        PublishActivity(slug, originator, "synced", $"Synced '{model.GetEffectiveTitle()}' across {providersSummary}.");
    }

    private static async Task WriteIfChangedAsync(string filePath, string content, string expectedRawHash, CancellationToken ct)
    {
        if (File.Exists(filePath))
        {
            var (ok, diskText) = await AtomicFileWriter.ReadTextWithRetryAsync(filePath, ct).ConfigureAwait(false);
            if (ok && string.Equals(ContentHasher.ComputeRawFileHash(diskText), expectedRawHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await AtomicFileWriter.WriteTextAtomicallyAsync(filePath, content, ct).ConfigureAwait(false);
    }

    private async Task HandleSkillDeletionAsync(string slug, ProviderType sourceProvider, string originator, CancellationToken ct)
    {
        if (string.Equals(slug, BootstrapSkillName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(originator, "force-delete", StringComparison.OrdinalIgnoreCase))
        {
            if (_skillsCache.TryGetValue(BootstrapSkillName, out var bootstrapModel))
            {
                await PropagateCanonicalModelToAllTargetsAsync(bootstrapModel, ProviderType.Canonical, "bootstrap-self-heal", ct).ConfigureAwait(false);
            }

            return;
        }

        _skillsCache.TryRemove(slug, out var removedModel);

        SkillLedgerEntry? entry;
        lock (_ledgerLock)
        {
            _ledger.Skills.TryGetValue(slug, out entry);
            _ledger.Skills.Remove(slug);
            SaveLedgerSync();
        }

        foreach (var providerInfo in _discovery.DiscoverProviders())
        {
            var providerKey = providerInfo.Provider.ToString().ToLowerInvariant();
            await RemoveTargetSkillFromDiskAsync(providerInfo.Provider, slug, providerKey, entry).ConfigureAwait(false);
        }

        var title = removedModel?.GetEffectiveTitle() ?? slug;
        PublishActivity(slug, originator, "deleted", $"Removed '{title}' from all connected AI providers.");
    }

    private Task RemoveTargetSkillFromDiskAsync(ProviderType provider, string slug, string providerKey, SkillLedgerEntry? ledgerEntry)
    {
        if (ledgerEntry is not null)
        {
            lock (_ledgerLock)
            {
                ledgerEntry.Targets.Remove(providerKey);
                if (provider == ProviderType.Codex)
                {
                    ledgerEntry.Targets.Remove("codex_agents");
                }
            }
        }

        try
        {
            if (provider == ProviderType.Cursor)
            {
                var mdcPath = _discovery.GetSkillPrimaryFilePath(ProviderType.Cursor, slug);
                if (File.Exists(mdcPath))
                {
                    File.Delete(mdcPath);
                }
            }
            else if (provider == ProviderType.Codex)
            {
                foreach (var dirPath in _discovery.GetAllCodexSkillDirectories(slug))
                {
                    if (Directory.Exists(dirPath))
                    {
                        Directory.Delete(dirPath, recursive: true);
                    }
                }
            }
            else
            {
                var dirPath = _discovery.GetSkillDirectoryPath(provider, slug);
                if (!string.IsNullOrEmpty(dirPath) && Directory.Exists(dirPath))
                {
                    Directory.Delete(dirPath, recursive: true);
                }

                if (provider == ProviderType.Gemini)
                {
                    var agDir = _discovery.GetAntigravityMirrorSkillDirectory(slug);
                    if (!string.IsNullOrEmpty(agDir) && Directory.Exists(agDir))
                    {
                        Directory.Delete(agDir, recursive: true);
                    }
                }
            }
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    private async Task SaveConflictBackupAsync(string slug, ProviderType provider, string conflictingContent, CancellationToken ct)
    {
        var skillConflictDir = Path.Combine(_discovery.ConflictsRoot, slug);
        Directory.CreateDirectory(skillConflictDir);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var backupPath = Path.Combine(skillConflictDir, $"{timestamp}-{provider.ToString().ToLowerInvariant()}.SKILL.md.bak");
        await AtomicFileWriter.WriteTextAtomicallyAsync(backupPath, conflictingContent, ct).ConfigureAwait(false);
        PublishActivity(slug, provider.ToString().ToLowerInvariant(), "conflict-backup", $"Backed up previous {provider} version before syncing.");
    }

    private static Dictionary<string, string> ReadSupportingFilesFromDisk(string skillDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(skillDirectory))
        {
            return result;
        }

        foreach (var folder in new[] { "scripts", "resources", "references", "examples", "agents" })
        {
            var subDir = Path.Combine(skillDirectory, folder);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
            {
                if (AtomicFileWriter.IsTemporaryOrIgnoredPath(file))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(file);
                    // Only load text-based supporting files under 256 KB into the JSON model
                    if (info.Length > 262_144)
                    {
                        continue;
                    }

                    var relPath = Path.GetRelativePath(skillDirectory, file).Replace('\\', '/');
                    var content = ContentHasher.NormalizeLineEndings(File.ReadAllText(file));
                    result[relPath] = content;
                }
                catch
                {
                }
            }
        }

        return result;
    }

    private static async Task SyncSupportingFilesToDirectoryAsync(
        string targetSkillDirectory,
        Dictionary<string, string> supportingFiles,
        CancellationToken ct)
    {
        var expectedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in supportingFiles)
        {
            var relPath = kvp.Key.Replace('\\', '/').TrimStart('/');
            var parts = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !ProviderDiscoveryService.IsAllowedSupportingFolder(parts[0]))
            {
                continue;
            }

            expectedRelativePaths.Add(relPath);
            var fullTargetPath = Path.Combine(targetSkillDirectory, relPath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(fullTargetPath))
            {
                var (ok, existingContent) = await AtomicFileWriter.ReadTextWithRetryAsync(fullTargetPath, ct).ConfigureAwait(false);
                if (ok && string.Equals(
                    ContentHasher.ComputeRawFileHash(existingContent),
                    ContentHasher.ComputeRawFileHash(kvp.Value),
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            await AtomicFileWriter.WriteTextAtomicallyAsync(fullTargetPath, kvp.Value, ct).ConfigureAwait(false);
        }

        foreach (var folder in new[] { "scripts", "resources", "references", "examples" })
        {
            var subDir = Path.Combine(targetSkillDirectory, folder);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            foreach (var existingFile in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
            {
                if (AtomicFileWriter.IsTemporaryOrIgnoredPath(existingFile))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(targetSkillDirectory, existingFile).Replace('\\', '/');
                if (!expectedRelativePaths.Contains(rel))
                {
                    try
                    {
                        File.Delete(existingFile);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private void LoadLedgerSync()
    {
        lock (_ledgerLock)
        {
            if (!File.Exists(_discovery.LedgerFilePath))
            {
                _ledger = new SyncLedger();
                return;
            }

            try
            {
                var json = File.ReadAllText(_discovery.LedgerFilePath);
                _ledger = JsonSerializer.Deserialize<SyncLedger>(json, LedgerJsonOptions) ?? new SyncLedger();
            }
            catch
            {
                _ledger = new SyncLedger();
            }
        }
    }

    private void SaveLedgerSync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_ledger, LedgerJsonOptions);
            var dir = Path.GetDirectoryName(_discovery.LedgerFilePath)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $".sync-ledger.{Guid.NewGuid():N}.skillsync-tmp");
            File.WriteAllText(temp, json);
            File.Move(temp, _discovery.LedgerFilePath, overwrite: true);
        }
        catch
        {
        }
    }

    private void LoadCanonicalSkillsSync()
    {
        if (!Directory.Exists(_discovery.CanonicalSkillsRoot))
        {
            return;
        }

        var canonicalTranspiler = _transpilers[ProviderType.Canonical];
        foreach (var dir in Directory.EnumerateDirectories(_discovery.CanonicalSkillsRoot))
        {
            var folderName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folderName) ||
                folderName.StartsWith('.') ||
                string.Equals(folderName, ProviderDiscoveryService.LegacyBootstrapSkillName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            try
            {
                var slug = ProviderDiscoveryService.NormalizeSlug(folderName);
                var raw = File.ReadAllText(skillFile);
                var model = canonicalTranspiler.MergeInbound(slug, raw, null, null, dir);
                model.SupportingFiles = ReadSupportingFilesFromDisk(dir);
                model.IsCatalogOnly = false;
                model.SourcePath = skillFile.Replace('\\', '/');

                if (string.Equals(slug, BootstrapSkillName, StringComparison.OrdinalIgnoreCase))
                {
                    model.SourceCategory = "system";
                    model.OriginProvider = "SkillHub System";
                }
                else if (string.IsNullOrWhiteSpace(model.SourceCategory))
                {
                    model.SourceCategory = "user";
                }

                _skillsCache[slug] = model;
            }
            catch
            {
            }
        }
    }

    private void LoadExternalCatalogSkillsSync()
    {
        var codexTranspiler = _transpilers[ProviderType.Codex];
        var descriptors = _discovery.DiscoverExternalCatalogSkills();

        foreach (var desc in descriptors)
        {
            try
            {
                var dir = Path.GetDirectoryName(desc.SkillMdPath)!;
                var raw = File.ReadAllText(desc.SkillMdPath);
                var model = codexTranspiler.MergeInbound(desc.Slug, raw, null, null, dir);

                model.Title = CanonicalSkillModel.HumanizeSlug(desc.Slug);
                model.SourceCategory = desc.SourceCategory;
                model.OriginProvider = desc.OriginProvider;
                model.IsCatalogOnly = true;
                model.SourcePath = desc.SkillMdPath.Replace('\\', '/');

                foreach (var tag in desc.SuggestedTags)
                {
                    if (!model.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        model.Tags.Add(tag);
                    }
                }

                model.SupportingFiles = ReadSupportingFilesFromDisk(dir);
                _catalogCache[desc.Slug] = model;
            }
            catch
            {
            }
        }
    }

    private void PublishActivity(string skillSlug, string source, string action, string details)
    {
        var notification = new SyncActivityNotification(
            DateTimeOffset.UtcNow,
            skillSlug,
            source,
            action,
            details);

        _recentActivity.Enqueue(notification);
        while (_recentActivity.Count > 100 && _recentActivity.TryDequeue(out _))
        {
        }

        ActivityPublished?.Invoke(notification);
    }

    public async ValueTask DisposeAsync()
    {
        _eventChannel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
