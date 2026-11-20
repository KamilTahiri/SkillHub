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
    public static readonly IReadOnlyList<string> All = ["claude", "codex", "gemini", "cursor", "copilot", "windsurf", "cline", "roocode"];

    /// <summary>Written for a new project unless the user changes it. The rest are opt-in.</summary>
    public static readonly IReadOnlyList<string> Defaults = ["claude", "codex", "gemini", "cursor"];

    /// <summary>Adapters checked only against documented paths and fixtures, not live against the installed tool.</summary>
    public static bool IsExperimental(string target) => target is "cursor" or "copilot" or "windsurf" or "cline" or "roocode";

    public static string Label(string target) => target switch
    {
        "claude" => "Claude (.claude/skills)",
        "codex" => "Codex, Antigravity & agents (.agents/skills)",
        "gemini" => "Gemini & Antigravity (.gemini/skills)",
        "cursor" => "Cursor (.cursor/rules)",
        "copilot" => "GitHub Copilot (.github/skills)",
        "windsurf" => "Windsurf (.windsurf/skills)",
        "cline" => "Cline (.clinerules)",
        "roocode" => "Roo Code (.roo/rules)",
        _ => target
    };
}

public sealed class WorkspaceRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("skills")] public List<string> Skills { get; set; } = [];
    [JsonPropertyName("targets")] public List<string> Targets { get; set; } = [.. WorkspaceTargets.Defaults];
    /// <summary>Files SkillHub wrote, by project-relative path, with the normalized hash it wrote.</summary>
    [JsonPropertyName("owned")] public Dictionary<string, string> Owned { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Keep the generated skill folders out of Git by listing them in <c>.git/info/exclude</c> (never committed).
    /// Null means the default: on. Turn it off to commit the skills so a team shares them.
    /// </summary>
    [JsonPropertyName("keep_local")] public bool? KeepLocal { get; set; }
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
    [property: JsonPropertyName("missing_skills")] IReadOnlyList<string> MissingSkills,
    [property: JsonPropertyName("keep_local")] bool KeepLocal = true,
    [property: JsonPropertyName("git_repo")] bool GitRepo = false,
    [property: JsonPropertyName("also_global")] IReadOnlyList<string>? AlsoGlobal = null,
    [property: JsonPropertyName("off_skills")] IReadOnlyList<string>? OffSkills = null);

/// <summary>
/// Projects chosen library skills into a project's own AI folders (<c>.claude/skills</c>, <c>.agents/skills</c>,
/// <c>.gemini/skills</c>, <c>.cursor/rules</c>, optionally <c>.github/skills</c> and others). Projection is one way: SkillHub stays the
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

    /// <summary>The workspace registered for this folder, or null.</summary>
    public WorkspaceStatus? FindByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a project folder.");
        var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
        var record = ReadAll().FirstOrDefault(item =>
            string.Equals(Path.GetFullPath(item.Path).TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase));
        return record is null ? null : Status(record);
    }

    /// <summary>
    /// Chooses skills for the project at <paramref name="path"/>, registering it first when needed. <paramref name="mode"/>
    /// is add (default), remove or replace. Returns the workspace after its files were written.
    /// </summary>
    public async Task<WorkspaceStatus> SetSkillsByPathAsync(string path, IReadOnlyList<string> skills, string? mode,
        IReadOnlyList<string>? targets, bool? keepLocal, CancellationToken ct = default)
    {
        var existing = FindByPath(path) ?? await RegisterAsync(path, null, ct).ConfigureAwait(false);
        var current = existing.Skills.ToList();
        var chosen = (mode ?? "add").Trim().ToLowerInvariant() switch
        {
            "add" => current.Concat(skills).ToList(),
            "remove" => current.Where(skill => !skills.Contains(skill, StringComparer.OrdinalIgnoreCase)).ToList(),
            "replace" => skills.ToList(),
            _ => throw new ArgumentException("mode must be add, remove or replace.")
        };
        return await UpdateAsync(existing.Id, chosen, targets, null, ct, keepLocal).ConfigureAwait(false);
    }

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
        CancellationToken ct = default, bool? keepLocal = null)
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
            if (keepLocal is not null) record.KeepLocal = keepLocal;
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
            if (Directory.Exists(record.Path))
            {
                try { GitExclude.Write(record.Path, []); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { kept.Add(new(".git/info/exclude", error.Message)); }
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
            if (model.Scope == CanonicalSkillModel.ScopeOff)
            {
                notes?.Add(new(key, "This skill is turned Off in SkillHub, so it was not written. Set it to Project-only or Global to use it here."));
                continue;
            }
            var slug = ProviderDiscoveryService.NormalizeSlug(model.Name);
            if (slug.Length == 0 || !names.Add(slug))
            {
                notes?.Add(new(key, $"Another chosen skill is also named '{slug}', so this one was not written."));
                continue;
            }
            model.Name = slug;
            foreach (var target in record.Targets)
            {
                // Project folders as each tool documents them: skill folders where the tool supports on-demand skills,
                // one rule file per skill for Cline (.clinerules/) and Roo Code (.roo/rules/), which load rules into every task.
                var folder = target switch
                {
                    "claude" => ".claude/skills/",
                    "codex" => ".agents/skills/",
                    "gemini" => ".gemini/skills/",
                    "copilot" => ".github/skills/",
                    "windsurf" => ".windsurf/skills/",
                    _ => null
                };
                if (folder is not null)
                {
                    var provider = target switch
                    {
                        "claude" => ProviderType.Claude, "gemini" => ProviderType.Gemini, "copilot" => ProviderType.Copilot,
                        "windsurf" => ProviderType.Windsurf, _ => ProviderType.Codex
                    };
                    files[folder + slug + "/SKILL.md"] = _engine.RenderForProvider(provider, model);
                    foreach (var (relative, content) in model.SupportingFiles ?? [])
                        files[folder + slug + "/" + SkillPaths.NormalizeRelative(relative)] = content;
                }
                else if (target == "cursor")
                {
                    files[".cursor/rules/" + slug + ".mdc"] = _engine.RenderForProvider(ProviderType.Cursor, model);
                }
                else if (target == "cline")
                {
                    files[".clinerules/" + slug + ".md"] = _engine.RenderForProvider(ProviderType.Cline, model);
                }
                else if (target == "roocode")
                {
                    files[".roo/rules/" + slug + ".md"] = _engine.RenderForProvider(ProviderType.RooCode, model);
                }
            }
        }
        return files;
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
        try
        {
            GitExclude.Write(record.Path, record.KeepLocal != false ? GitExclude.EntriesFor(record.Owned.Keys) : []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            issues.Add(new(".git/info/exclude", "Could not keep the skill folders out of Git: " + error.Message));
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
        var models = record.Skills.Select(key => (Key: key, Model: _engine.GetSkill(key))).ToArray();
        var missing = models.Where(item => item.Model is null).Select(item => item.Key).ToArray();
        // Chosen here and also delivered to the tools' global folders: the tools see two copies.
        var alsoGlobal = models.Where(item => item.Model is { } model && ContextBudget.SkillTokens(model) > 0).Select(item => item.Key).ToArray();
        var off = models.Where(item => item.Model?.Scope == CanonicalSkillModel.ScopeOff).Select(item => item.Key).ToArray();
        var exists = Directory.Exists(record.Path);
        return new WorkspaceStatus(record.Id, record.Name, record.Path, exists, record.Skills, record.Targets,
            record.Owned.Count, record.LastSyncedAt, issues, missing, record.KeepLocal != false,
            exists && GitExclude.FindRepository(record.Path) is not null, alsoGlobal, off);
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

/// <summary>
/// Keeps SkillHub's generated project skill folders out of Git through the repository's <c>info/exclude</c> file, which
/// works like .gitignore but is never committed. SkillHub only edits its own marked block.
/// </summary>
public static class GitExclude
{
    public const string Begin = "# >>> SkillHub project skills (managed by SkillHub; edit in the Projects tab) >>>";
    public const string End = "# <<< SkillHub project skills <<<";

    /// <summary>One entry per generated skill folder or rule file, relative to the project folder.</summary>
    public static IReadOnlyList<string> EntriesFor(IEnumerable<string> ownedFiles)
    {
        var entries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ownedFiles)
        {
            var parts = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var skills = Array.FindIndex(parts, part => part.Equals("skills", StringComparison.OrdinalIgnoreCase));
            entries.Add(skills >= 0 && skills + 1 < parts.Length - 1 ? string.Join('/', parts[..(skills + 2)]) + "/" : string.Join('/', parts));
        }
        return [.. entries];
    }

    /// <summary>The repository root and the exclude file for a folder inside a Git work tree, or null.</summary>
    public static (string Root, string ExcludePath)? FindRepository(string folder)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(folder)); current is not null; current = current.Parent)
        {
            var dotGit = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGit)) return (current.FullName, Path.Combine(dotGit, "info", "exclude"));
            if (!File.Exists(dotGit)) continue;
            // A linked worktree or submodule: ".git" is a file that points at the real Git directory.
            var line = File.ReadLines(dotGit).FirstOrDefault(text => text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;
            var gitDir = Path.GetFullPath(Path.Combine(current.FullName, line["gitdir:".Length..].Trim()));
            var common = Path.Combine(gitDir, "commondir");
            if (File.Exists(common)) gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(common).Trim()));
            return (current.FullName, Path.Combine(gitDir, "info", "exclude"));
        }
        return null;
    }

    /// <summary>Replaces SkillHub's block with <paramref name="entries"/>; an empty list removes it. Outside Git: nothing to do.</summary>
    public static void Write(string projectFolder, IReadOnlyList<string> entries)
    {
        if (FindRepository(projectFolder) is not { } repository) return;
        var prefix = Path.GetRelativePath(repository.Root, Path.GetFullPath(projectFolder)).Replace('\\', '/');
        prefix = prefix == "." ? string.Empty : prefix.TrimEnd('/') + "/";
        var existing = File.Exists(repository.ExcludePath) ? File.ReadAllText(repository.ExcludePath) : string.Empty;
        var kept = new List<string>();
        var inside = false;
        foreach (var line in existing.Replace("\r\n", "\n").Split('\n'))
        {
            if (line == Begin) { inside = true; continue; }
            if (line == End) { inside = false; continue; }
            if (!inside) kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
        if (entries.Count > 0)
        {
            if (kept.Count > 0) kept.Add(string.Empty);
            kept.Add(Begin);
            kept.AddRange(entries.Select(entry => "/" + prefix + entry.TrimStart('/')));
            kept.Add(End);
        }
        var next = kept.Count == 0 ? string.Empty : string.Join('\n', kept) + "\n";
        if (next == existing.Replace("\r\n", "\n") || next.Length == 0 && !File.Exists(repository.ExcludePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(repository.ExcludePath)!);
        AtomicFileWriter.WriteTextAtomicallyAsync(repository.ExcludePath, next).GetAwaiter().GetResult();
    }
}
