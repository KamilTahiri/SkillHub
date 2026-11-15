using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>Where a project receives its skills. Every path is relative to the project folder.</summary>
public static class WorkspaceTargets
{
    public static readonly IReadOnlyList<string> All = ["claude", "codex", "gemini", "cursor", "copilot"];

    public static string Label(string target) => target switch
    {
        "claude" => "Claude (.claude/skills)",
        "codex" => "Codex & agents (.agents/skills)",
        "gemini" => "Gemini & Antigravity (.gemini/skills)",
        "cursor" => "Cursor (.cursor/rules)",
        "copilot" => "GitHub Copilot (.github/instructions)",
        _ => target
    };
}

public sealed class WorkspaceRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("skills")] public List<string> Skills { get; set; } = [];
    [JsonPropertyName("targets")] public List<string> Targets { get; set; } = [.. WorkspaceTargets.All.Where(target => target != "copilot")];
    /// <summary>Files SkillHub wrote, by project-relative path, with the normalized hash it wrote.</summary>
    [JsonPropertyName("owned")] public Dictionary<string, string> Owned { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("last_synced_at")] public DateTimeOffset? LastSyncedAt { get; set; }
}

public sealed record WorkspaceIssue(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("problem")] string Problem);

public sealed record WorkspaceStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("skills")] IReadOnlyList<string> Skills,
    [property: JsonPropertyName("targets")] IReadOnlyList<string> Targets,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("last_synced_at")] DateTimeOffset? LastSyncedAt,
    [property: JsonPropertyName("issues")] IReadOnlyList<WorkspaceIssue> Issues,
    [property: JsonPropertyName("missing_skills")] IReadOnlyList<string> MissingSkills);

/// <summary>
/// Projects chosen library skills into a project's own AI folders (<c>.claude/skills</c>, <c>.agents/skills</c>,
/// <c>.gemini/skills</c>, <c>.cursor/rules</c>, <c>.github/instructions</c>). Projection is one way: SkillHub stays the
/// source of truth. Like the provider ledger, a workspace only overwrites or deletes files whose content still matches
/// what SkillHub wrote, so files a person created or edited in the project are never lost. Records live as JSON files
/// in <c>~/.skillhub/workspaces/</c>.
/// </summary>
public sealed class WorkspaceService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SyncEngine _engine;
    private readonly ProviderDiscoveryService _discovery;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<WorkspaceIssue>> _issues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;

    public WorkspaceService(SyncEngine engine, ProviderDiscoveryService discovery)
    {
        _engine = engine;
        _discovery = discovery;
        _debounce = new Timer(_ => _ = ReprojectChangedAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _engine.SkillChanged += OnSkillChanged;
    }

    public string Root => System.IO.Path.Combine(_discovery.SkillHubRoot, "workspaces");

    private void OnSkillChanged(string slug)
    {
        lock (_changed) _changed.Add(slug);
        _debounce.Change(600, Timeout.Infinite);
    }

    private async Task ReprojectChangedAsync()
    {
        string[] changed;
        lock (_changed)
        {
            changed = [.. _changed];
            _changed.Clear();
        }
        foreach (var workspace in ReadAll().Where(item => item.Skills.Any(skill => changed.Contains(skill, StringComparer.OrdinalIgnoreCase))))
        {
            try { await SyncAsync(workspace.Id).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException) { }
        }
    }

    public IReadOnlyList<WorkspaceStatus> List() => ReadAll().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(Status).ToArray();

    public WorkspaceStatus Get(string id) => Status(Read(id));

    public async Task<WorkspaceStatus> RegisterAsync(string path, string? name, CancellationToken ct = default)
    {
        var full = ValidateProjectPath(path);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ReadAll().Any(item => string.Equals(System.IO.Path.GetFullPath(item.Path), full, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("This project folder is already a workspace.");
            var record = new WorkspaceRecord
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(full.TrimEnd('\\', '/')) : name.Trim(),
                Path = full
            };
            await WriteAsync(record).ConfigureAwait(false);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceStatus> UpdateAsync(string id, IReadOnlyList<string>? skills, IReadOnlyList<string>? targets, string? name,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = Read(id);
            if (skills is not null)
                record.Skills = skills.Select(ProviderDiscoveryService.NormalizeSlug).Where(slug => slug.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (targets is not null)
            {
                var unknown = targets.Where(target => !WorkspaceTargets.All.Contains(target)).ToArray();
                if (unknown.Length > 0) throw new ArgumentException("Unknown target: " + string.Join(", ", unknown) + ". Use " + string.Join(", ", WorkspaceTargets.All) + ".");
                record.Targets = targets.Distinct().ToList();
            }
            if (!string.IsNullOrWhiteSpace(name)) record.Name = name.Trim();
            await ProjectAsync(record, ct).ConfigureAwait(false);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceStatus> SyncAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = Read(id);
            await ProjectAsync(record, ct).ConfigureAwait(false);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Forgets a workspace. With <paramref name="removeFiles"/>, SkillHub first deletes the unedited files it wrote.</summary>
    public async Task<IReadOnlyList<WorkspaceIssue>> RemoveAsync(string id, bool removeFiles, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = Read(id);
            var kept = new List<WorkspaceIssue>();
            if (removeFiles && Directory.Exists(record.Path))
            {
                record.Skills.Clear();
                await ProjectAsync(record, ct).ConfigureAwait(false);
                kept.AddRange(_issues.GetValueOrDefault(record.Id) ?? []);
            }
            File.Delete(RecordPath(record.Id));
            _issues.Remove(record.Id);
            return kept;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Every file the workspace should contain, keyed by project-relative path with forward slashes.</summary>
    public Dictionary<string, string> DesiredFiles(WorkspaceRecord record, List<string> missing, List<WorkspaceIssue>? notes = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in record.Skills)
        {
            // Keys are library names, or catalog ids for built-ins whose name exists in several tools.
            var model = _engine.GetSkill(key);
            if (model is null) { missing.Add(key); continue; }
            var slug = ProviderDiscoveryService.NormalizeSlug(model.Name);
            if (slug.Length == 0 || !names.Add(slug))
            {
                notes?.Add(new(key, $"Another chosen skill is also named '{slug}', so this one was not written."));
                continue;
            }
            model.Name = slug;
            foreach (var target in record.Targets)
            {
                var folder = target switch
                {
                    "claude" => ".claude/skills/",
                    "codex" => ".agents/skills/",
                    "gemini" => ".gemini/skills/",
                    _ => null
                };
                if (folder is not null)
                {
                    var provider = target switch { "claude" => ProviderType.Claude, "gemini" => ProviderType.Gemini, _ => ProviderType.Codex };
                    files[folder + slug + "/SKILL.md"] = _engine.RenderForProvider(provider, model);
                    foreach (var (relative, content) in model.SupportingFiles ?? [])
                        files[folder + slug + "/" + SkillPaths.NormalizeRelative(relative)] = content;
                }
                else if (target == "cursor")
                {
                    files[".cursor/rules/" + slug + ".mdc"] = _engine.RenderForProvider(ProviderType.Cursor, model);
                }
                else if (target == "copilot")
                {
                    files[".github/instructions/" + slug + ".instructions.md"] = CopilotInstructions(model);
                }
            }
        }
        return files;
    }

    /// <summary>Copilot repository instructions: <c>applyTo</c> frontmatter, a description line, then the skill body.</summary>
    public static string CopilotInstructions(CanonicalSkillModel model)
    {
        var builder = new StringBuilder();
        builder.Append("---\napplyTo: \"**\"\ndescription: ")
            .Append(JsonSerializer.Serialize(FrontmatterParser.CleanSingleLine(model.Description))).Append("\n---\n\n");
        builder.Append("# ").Append(model.GetEffectiveTitle()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(model.WhenToUse)) builder.Append("Use when: ").Append(model.WhenToUse.Trim()).Append("\n\n");
        builder.Append(model.InstructionsMarkdown.Trim()).Append('\n');
        return builder.ToString();
    }

    private async Task ProjectAsync(WorkspaceRecord record, CancellationToken ct)
    {
        var issues = new List<WorkspaceIssue>();
        var missing = new List<string>();
        if (!Directory.Exists(record.Path))
        {
            issues.Add(new(record.Path, "The project folder no longer exists."));
            _issues[record.Id] = issues;
            return;
        }
        var desired = DesiredFiles(record, missing, issues);
        foreach (var slug in missing) issues.Add(new(slug, "This skill is no longer in your library."));

        foreach (var (relative, content) in desired)
        {
            ct.ThrowIfCancellationRequested();
            var path = SkillPaths.Resolve(record.Path, relative);
            var hash = ContentHasher.ComputeRawFileHash(content);
            var onDisk = ReadHash(path);
            if (onDisk == hash)
            {
                record.Owned[relative] = hash;
                continue;
            }
            var owned = record.Owned.GetValueOrDefault(relative);
            if (onDisk is not null && onDisk != owned)
            {
                issues.Add(new(relative, owned is null
                    ? "A file the project already had is in the way. SkillHub left it untouched."
                    : "This copy was edited in the project. SkillHub kept the edit; edit the skill in SkillHub instead."));
                continue;
            }
            try
            {
                SkillPaths.AssertNoReparsePoints(Path.GetDirectoryName(path)!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await AtomicFileWriter.WriteTextAtomicallyAsync(path, content, ct).ConfigureAwait(false);
                record.Owned[relative] = hash;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(relative, error.Message));
            }
        }

        foreach (var relative in record.Owned.Keys.Where(key => !desired.ContainsKey(key)).ToArray())
        {
            var path = SkillPaths.Resolve(record.Path, relative);
            var onDisk = ReadHash(path);
            if (onDisk is null || onDisk == record.Owned[relative])
            {
                try
                {
                    if (onDisk is not null) File.Delete(path);
                    record.Owned.Remove(relative);
                    PruneEmptyFolders(record.Path, Path.GetDirectoryName(path)!);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new(relative, error.Message));
                }
            }
            else
            {
                record.Owned.Remove(relative);
                issues.Add(new(relative, "Removed from the workspace, but the file was edited in the project, so SkillHub left it in place."));
            }
        }
        record.LastSyncedAt = DateTimeOffset.UtcNow;
        _issues[record.Id] = issues;
        await WriteAsync(record).ConfigureAwait(false);
    }

    private static void PruneEmptyFolders(string projectRoot, string directory)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/');
        var current = Path.GetFullPath(directory);
        // Remove the now-empty skill folder and its empty parents, but never a top-level folder like .claude itself.
        while (current.Length > root.Length && SkillPaths.IsInside(root, current) && Directory.Exists(current) &&
               !Directory.EnumerateFileSystemEntries(current).Any() &&
               Path.GetDirectoryName(current) is { } parent && !string.Equals(Path.GetFullPath(parent).TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(current);
            current = parent;
        }
    }

    private static string? ReadHash(string path)
    {
        if (!File.Exists(path)) return null;
        try { return ContentHasher.ComputeRawFileHash(File.ReadAllBytes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "unreadable"; }
    }

    private string ValidateProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a project folder.");
        var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
        if (!Directory.Exists(full)) throw new ArgumentException("That folder does not exist: " + full);
        if (Path.GetPathRoot(full)?.TrimEnd('\\', '/') == full) throw new ArgumentException("Choose a project folder, not a whole drive.");
        var home = Path.GetFullPath(_discovery.UserHomeRoot).TrimEnd('\\', '/');
        // The user folder holds the global AI folders; projecting there would fight the global sync.
        if (string.Equals(full, home, StringComparison.OrdinalIgnoreCase) || SkillPaths.IsInside(full, home))
            throw new ArgumentException("Your user folder already receives the global library. Choose a project folder inside it instead.");
        if (SkillPaths.IsInside(_discovery.SkillHubRoot, full) ||
            new[] { ".claude", ".codex", ".agents", ".gemini", ".cursor", ".copilot" }.Any(folder => SkillPaths.IsInside(Path.Combine(home, folder), full)))
            throw new ArgumentException("That folder belongs to SkillHub or an AI tool. Choose your project's own folder.");
        SkillPaths.AssertNoReparsePoints(full);
        return full;
    }

    private WorkspaceStatus Status(WorkspaceRecord record)
    {
        var issues = _issues.GetValueOrDefault(record.Id) ?? [];
        var missing = record.Skills.Where(key => _engine.GetSkill(key) is null).ToArray();
        return new WorkspaceStatus(record.Id, record.Name, record.Path, Directory.Exists(record.Path), record.Skills, record.Targets,
            record.Owned.Count, record.LastSyncedAt, issues, missing);
    }

    private string RecordPath(string id)
    {
        if (id.Length is 0 or > 40 || !id.All(char.IsAsciiLetterOrDigit)) throw new KeyNotFoundException("This workspace does not exist.");
        return Path.Combine(Root, id + ".json");
    }

    public WorkspaceRecord Read(string id)
    {
        var path = RecordPath(id);
        if (!File.Exists(path)) throw new KeyNotFoundException("This workspace does not exist.");
        var record = JsonSerializer.Deserialize<WorkspaceRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("The workspace record is empty.");
        record.Owned = new Dictionary<string, string>(record.Owned, StringComparer.OrdinalIgnoreCase);
        return record;
    }

    private IEnumerable<WorkspaceRecord> ReadAll()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            WorkspaceRecord? record = null;
            try { record = Read(Path.GetFileNameWithoutExtension(file)); }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidDataException) { }
            if (record is not null) yield return record;
        }
    }

    private async Task WriteAsync(WorkspaceRecord record)
    {
        Directory.CreateDirectory(Root);
        await AtomicFileWriter.WriteTextAtomicallyAsync(RecordPath(record.Id), JsonSerializer.Serialize(record, Json)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _engine.SkillChanged -= OnSkillChanged;
        _debounce.Dispose();
        _gate.Dispose();
    }
}
