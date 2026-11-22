using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>A folder that groups several projects (e.g. <c>Roblox Games\</c>), where one SkillHub project can serve all of them.</summary>
public sealed record UmbrellaHint(string Path, IReadOnlyList<string> Projects, IReadOnlyList<string> SeparateGitRepos, string Advice);

public sealed record ProjectLocation(UmbrellaHint? Umbrella, IReadOnlyList<string> Warnings);

public sealed record ProjectCreateResult(bool Created, WorkspaceStatus Project, ProjectLocation Location);

/// <summary>Compact skill metadata for relevance checks: never the body or companion files.</summary>
public sealed record InspectedSkill(string Slug, string Description, IReadOnlyList<string> Tags, string Scope, string Via, bool Inherited, bool AlsoGlobal);

public sealed record InspectedProject(string Id, string Name, string Path, bool Inherited, IReadOnlyList<string> Targets, bool KeepLocal,
    bool Umbrella = false, IReadOnlyList<string>? Children = null);

public sealed record SkillFolderOnDisk(string Tool, string Slug, string Path, bool ManagedBySkillHub);

public sealed record ProjectInspection(
    string Path,
    bool Exists,
    string? GitRoot,
    IReadOnlyList<InspectedProject> Projects,
    IReadOnlyList<InspectedSkill> Skills,
    IReadOnlyList<SkillFolderOnDisk> OnDisk,
    IReadOnlyList<string> GlobalSkills,
    UmbrellaHint? Umbrella,
    IReadOnlyList<string> Warnings,
    string Guidance);

public sealed partial class WorkspaceService
{
    private static readonly string[] ProjectMarkers =
    [
        ".git", "package.json", "pyproject.toml", "Cargo.toml", "go.mod", "default.project.json", "aftman.toml", "rokit.toml",
        "wally.toml", "selene.toml", "project.godot"
    ];

    private static readonly string[] ProjectExtensions = [".sln", ".csproj", ".rbxlx", ".rbxl", ".blend", ".uproject"];

    private static readonly (string Tool, string Folder, string? Extension)[] SkillFolders =
    [
        ("claude", ".claude/skills", null), ("codex", ".agents/skills", null), ("gemini", ".gemini/skills", null),
        ("copilot", ".github/skills", null), ("windsurf", ".windsurf/skills", null), ("cursor", ".cursor/rules", ".mdc"),
        ("cline", ".clinerules", ".md"), ("roocode", ".roo/rules", ".md")
    ];

    /// <summary>
    /// Registers <paramref name="path"/> as a project (or returns the existing one), optionally creating the folder and
    /// choosing skills. Defaults: the standard targets and Keep local on, so Git repositories get <c>.git/info/exclude</c> entries.
    /// </summary>
    public async Task<ProjectCreateResult> CreateAsync(string path, string? name, IReadOnlyList<string> skills, IReadOnlyList<string>? targets,
        bool? keepLocal, bool createFolder, CancellationToken ct = default, bool? umbrella = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a project folder.");
        var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
        if (!Directory.Exists(full))
        {
            if (!createFolder) throw new ArgumentException("That folder does not exist: " + full + ". Pass create_folder: true to create it.");
            ValidateProjectPath(full, mustExist: false);
            Directory.CreateDirectory(full);
        }
        var existing = FindByPath(full);
        var created = existing is null;
        var project = existing ?? await RegisterAsync(full, name, ct).ConfigureAwait(false);
        project = created || skills.Count > 0 || targets is not null || keepLocal is not null || umbrella is not null
            ? await UpdateAsync(project.Id, project.Skills.Concat(skills).ToList(), targets, created ? null : name, ct, keepLocal, umbrella).ConfigureAwait(false)
            : project;
        return new ProjectCreateResult(created, project, Locate(full));
    }

    /// <summary>
    /// What reaches the AI tools in <paramref name="path"/>: skills from a SkillHub project registered for this folder or
    /// for a parent folder, skill folders found on disk, and where the Git boundary sits. Metadata only.
    /// </summary>
    public ProjectInspection Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Give the folder you are working in.");
        var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
        var exists = Directory.Exists(full);
        var git = exists ? GitExclude.FindRepository(full)?.Root.TrimEnd('\\', '/') : null;
        var records = ReadAll()
            .Select(record => (Record: record, Root: Path.GetFullPath(record.Path).TrimEnd('\\', '/')))
            .Where(item => string.Equals(item.Root, full, StringComparison.OrdinalIgnoreCase) || SkillPaths.IsInside(item.Root, full))
            .OrderByDescending(item => item.Root.Length)
            .ToList();

        var warnings = new List<string>();
        var skills = new List<InspectedSkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (record, root) in records)
        {
            var inherited = !string.Equals(root, full, StringComparison.OrdinalIgnoreCase);
            // An umbrella project writes real copies into each direct subfolder, so there is no .git boundary to cross.
            if (inherited && record.Umbrella != true && git is not null && SkillPaths.IsInside(root, git))
                warnings.Add($"Skills come from the parent project '{root}', but this folder is its own Git repository ('{git}'). " +
                    "Claude Code reads parent folders, while IDE tools (Cursor, Copilot, Windsurf, Antigravity IDE) opened on this repository may stop at its .git boundary and not see them. " +
                    "Ask the user whether to keep the umbrella project or attach the skills to this repository too.");
            foreach (var key in record.Skills)
            {
                var model = _engine.GetSkill(key);
                var slug = model?.Name ?? key;
                if (!seen.Add(slug)) continue;
                skills.Add(model is null
                    ? new InspectedSkill(slug, "(no longer in the library)", [], "missing", root, inherited, false)
                    : new InspectedSkill(slug, model.Description,
                        model.Tags.Select(ProviderDiscoveryService.NormalizeSlug).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        model.Scope, root, inherited, ContextBudget.SkillTokens(model) > 0));
            }
        }
        var also = skills.Where(skill => skill.AlsoGlobal).Select(skill => skill.Slug).ToList();
        if (also.Count > 0)
            warnings.Add($"{also.Count} chosen skill(s) are also synced globally, so tools see them twice: {string.Join(", ", also)}. Suggest Project-only (set_skill_scope).");

        var owned = records.SelectMany(item => item.Record.Owned.Keys.Select(relative =>
            Path.GetFullPath(Path.Combine(item.Root, relative)))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var onDisk = exists ? FindSkillFolders(full, owned) : [];
        var foreign = onDisk.Where(item => !item.ManagedBySkillHub).Select(item => item.Slug).Distinct().ToList();
        if (foreign.Count > 0)
            warnings.Add($"Skill files not managed by SkillHub are in this folder: {string.Join(", ", foreign)}. Leave them alone unless the user asks.");

        var globals = _engine.ListSkills().Where(skill => !skill.IsCatalogOnly && ContextBudget.SkillTokens(skill) > 0)
            .Select(skill => skill.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var location = exists && records.Count == 0 ? Locate(full) : new ProjectLocation(null, []);
        warnings.AddRange(location.Warnings);

        return new ProjectInspection(full, exists, git,
            records.Select(item => new InspectedProject(item.Record.Id, item.Record.Name, item.Root,
                !string.Equals(item.Root, full, StringComparison.OrdinalIgnoreCase), item.Record.Targets, item.Record.KeepLocal != false,
                item.Record.Umbrella == true, item.Record.Umbrella == true && Directory.Exists(item.Root) ? UmbrellaChildren(item.Root) : null)).ToList(),
            skills, onDisk, globals, location.Umbrella, warnings,
            "Judge relevance from slug, description and tags against the files in this workspace. If you are unsure whether a skill belongs here, " +
            "ask the user about those specific skills before adding or removing anything (set_project_skills).");
    }

    /// <summary>Umbrella and Git-boundary hints for a project folder.</summary>
    public ProjectLocation Locate(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var warnings = new List<string>();
        UmbrellaHint? umbrella = null;
        var children = ChildProjects(full);
        if (children.Count >= 2)
        {
            var repos = children.Where(child => IsGitRoot(Path.Combine(full, child))).ToList();
            if (repos.Count > 0)
                warnings.Add($"This umbrella folder holds separate Git repositories ({string.Join(", ", repos.Take(6))}{(repos.Count > 6 ? ", …" : string.Empty)}). " +
                    "Claude Code finds skills in parent folders, but IDE tools (Cursor, Copilot, Windsurf, Antigravity IDE) opened on one of those repositories may stop at its .git boundary. " +
                    "Offer umbrella mode (umbrella: true), which copies the pack into each direct subfolder and its .git/info/exclude so every tool sees it, " +
                    "or attach the pack to the specific repositories they open in an IDE.");
        }
        var parent = Path.GetDirectoryName(full);
        if (parent is not null && IsAllowedUmbrella(parent))
        {
            var siblings = ChildProjects(parent);
            if (siblings.Count >= 2 && siblings.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase))
            {
                var repos = siblings.Where(child => IsGitRoot(Path.Combine(parent, child))).ToList();
                umbrella = new UmbrellaHint(parent, siblings.Take(12).ToList(), repos.Take(12).ToList(),
                    $"'{Path.GetFileName(full)}' sits in '{parent}' with {siblings.Count - 1} other project(s). Prefer one SkillHub project on the umbrella folder so the pack is not copied into every project" +
                    (repos.Count > 0 ? "; these projects have their own .git and IDE tools may stop at that boundary, so ask the user whether to use umbrella mode (umbrella: true), which copies the pack into each subfolder." : "."));
            }
        }
        return new ProjectLocation(umbrella, warnings);
    }

    /// <summary>Generic containers that hold unrelated projects: never suggested as a skill umbrella.</summary>
    private static readonly HashSet<string> GenericContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop", "documents", "downloads", "onedrive", "source", "repos", "repositories", "projects", "dev", "code", "src",
        "git", "github", "gitlab", "workspace", "workspaces", "work", "sandbox", "temp", "tmp", "users", "home"
    };

    private bool IsAllowedUmbrella(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (GenericContainers.Contains(name) || name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return false;
        var special = new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile }
            .Select(Environment.GetFolderPath).Where(path => path.Length > 0);
        if (special.Any(path => string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) return false;
        try { ValidateProjectPath(folder); return true; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsGitRoot(string folder) => Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"));

    /// <summary>Names of direct subfolders that look like projects (a .git or a common project file).</summary>
    private static List<string> ChildProjects(string folder)
    {
        var result = new List<string>();
        try
        {
            foreach (var child in Directory.EnumerateDirectories(folder).Take(200))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") continue;
                if (ProjectMarkers.Any(marker => Directory.Exists(Path.Combine(child, marker)) || File.Exists(Path.Combine(child, marker))) ||
                    Directory.EnumerateFiles(child).Take(200).Any(file => ProjectExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
                    result.Add(name);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<SkillFolderOnDisk> FindSkillFolders(string folder, HashSet<string> owned)
    {
        var found = new List<SkillFolderOnDisk>();
        foreach (var (tool, relative, extension) in SkillFolders)
        {
            var root = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root)) continue;
            try
            {
                if (extension is null)
                {
                    foreach (var directory in Directory.EnumerateDirectories(root).Take(200))
                    {
                        var skillFile = Path.Combine(directory, "SKILL.md");
                        if (!File.Exists(skillFile)) continue;
                        found.Add(new SkillFolderOnDisk(tool, Path.GetFileName(directory), relative + "/" + Path.GetFileName(directory),
                            owned.Contains(Path.GetFullPath(skillFile))));
                    }
                }
                else
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*" + extension).Take(200))
                        found.Add(new SkillFolderOnDisk(tool, Path.GetFileNameWithoutExtension(file), relative + "/" + Path.GetFileName(file),
                            owned.Contains(Path.GetFullPath(file))));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }
}
