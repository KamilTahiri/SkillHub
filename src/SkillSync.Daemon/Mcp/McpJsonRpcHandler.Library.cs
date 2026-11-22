using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Mcp;

/// <summary>Library-wide tools: context budget, scope, Skill Doctor for the whole library, trigger tester and project workspaces.</summary>
public sealed partial class McpJsonRpcHandler
{
    private string ExecuteGetContextBudget()
    {
        var budget = ContextBudget.Calculate(_syncEngine.ListLibraryWithContents());
        return JsonSerializer.Serialize(new
        {
            budget.Tokens, budget.Characters, budget.State, budget.Message, budget.SafeTokens, budget.LimitTokens,
            budget.GlobalSkills, budget.ProjectOnlySkills, budget.OffSkills, budget.AlwaysLoadedBodiesTokens,
            packs = budget.Packs.Select(pack => new
            {
                pack.Pack, pack.Label, pack.Kind, pack.Tokens, pack.GlobalSkills, pack.ProjectOnlySkills, pack.OffSkills,
                skills = pack.Skills.Select(skill => new { skill.Name, skill.Tokens, skill.Scope })
            }),
            next_step = budget.State == "ok" ? null
                : "Move niche packs off global sync with set_skill_scope (scope: project, source: <pack>), then enable them per repository with set_project_skills."
        }, SnakeJson);
    }

    private async Task<string> ExecuteSetSkillScopeAsync(JsonObject args, CancellationToken ct)
    {
        var scope = args["scope"]?.GetValue<string>() ?? throw new ArgumentException("scope is required.");
        var names = ResolveSkillSelection(args, required: true);
        var before = ContextBudget.Calculate(_syncEngine.ListLibraryWithContents()).Tokens;
        var result = await _syncEngine.SetScopeAsync(names, scope, "mcp:set_skill_scope", ct).ConfigureAwait(false);
        var after = ContextBudget.Calculate(_syncEngine.ListLibraryWithContents());
        return JsonSerializer.Serialize(new
        {
            status = result.Changed.Count > 0 ? "updated" : "unchanged",
            result.Scope, result.Changed, result.Unchanged, result.Skipped,
            always_loaded_tokens_before = before, always_loaded_tokens_after = after.Tokens, budget_state = after.State,
            message = result.Scope == CanonicalSkillModel.ScopeProject
                ? "These skills left the tools' global folders. Enable them for a repository with set_project_skills."
                : null
        }, SnakeJson);
    }

    private string ExecuteLintLibrary(JsonObject args)
    {
        var report = LibraryDoctor.Audit(_syncEngine.ListLibraryWithContents());
        var all = args["include_clean_skills"]?.GetValue<bool>() ?? false;
        return JsonSerializer.Serialize(new
        {
            report.Score, report.SkillsChecked, report.Errors, report.Warnings,
            budget = new { report.Budget.Tokens, report.Budget.State, report.Budget.Message },
            collisions = report.Collisions,
            missing_references = report.MissingReferences,
            skills = report.Skills.Where(skill => all || skill.Findings.Count > 0).Select(skill => new
            {
                skill.Name, skill.Score, skill.Scope, skill.Pack,
                findings = skill.Findings.Select(finding => new { finding.Severity, finding.Code, finding.Message })
            })
        }, SnakeJson);
    }

    private string ExecuteTestSkillTrigger(JsonObject args)
    {
        var prompt = args["prompt"]?.GetValue<string>() ?? throw new ArgumentException("prompt is required.");
        var result = TriggerTester.Rank(prompt, _syncEngine.ListLibraryWithContents(),
            args["include_project_only"]?.GetValue<bool>() ?? false, (int)Math.Clamp(args["limit"]?.GetValue<long>() ?? 5, 1, 20));
        return JsonSerializer.Serialize(result, SnakeJson);
    }

    private string ExecuteListProjects() => JsonSerializer.Serialize(new
    {
        projects = Workspaces.List().Select(project => new
        {
            project.Id, project.Name, project.Path, project.Exists, project.Skills, project.Targets, project.KeepLocal, project.GitRepo,
            project.Umbrella, project.Children, also_global = project.AlsoGlobal, project.MissingSkills, project.OffSkills, project.LastSyncedAt,
            issues = project.Issues
        }),
        targets = WorkspaceTargets.All,
        hint = "Use set_project_skills with a project path to choose skills (or a whole tag/source pack) for that repository."
    }, SnakeJson);

    private async Task<string> ExecuteSetProjectSkillsAsync(JsonObject args, CancellationToken ct)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("path is required: the project folder.");
        var mode = args["mode"]?.GetValue<string>() ?? "add";
        // Changing only the folder settings (umbrella mode, Keep local, targets) needs no skill names.
        var settingsOnly = args["umbrella"] is not null || args["keep_local"] is not null || args["targets"] is not null;
        var names = ResolveSkillSelection(args, required: mode != "replace" && !settingsOnly);
        var targets = args["targets"] is JsonArray ? ReadStringList(args["targets"]) : null;
        var keepLocal = args["keep_local"]?.GetValue<bool>();
        ScopeChangeResult? scope = null;
        if (args["make_project_only"]?.GetValue<bool>() == true && mode != "remove")
            scope = await _syncEngine.SetScopeAsync(names, CanonicalSkillModel.ScopeProject, "mcp:set_project_skills", ct).ConfigureAwait(false);
        var project = await Workspaces.SetSkillsByPathAsync(path, names, mode, targets, keepLocal, ct, args["umbrella"]?.GetValue<bool>()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "synced",
            project = new
            {
                project.Id, project.Name, project.Path, project.Skills, project.Targets, project.KeepLocal, project.GitRepo,
                files_written = project.FileCount, also_global = project.AlsoGlobal, project.Issues, project.Umbrella, project.Children
            },
            made_project_only = scope?.Changed,
            message = project.AlsoGlobal is { Count: > 0 } alsoGlobal
                ? $"{alsoGlobal.Count} of these skills are also synced globally, so tools see them twice. Pass make_project_only: true (or call set_skill_scope) to keep them only in projects."
                : project.KeepLocal && project.GitRepo ? "Written and kept out of Git through .git/info/exclude." : "Written."
        }, SnakeJson);
    }

    private string ExecuteListTags(JsonObject args)
    {
        var report = _syncEngine.TagReport();
        var filter = args["tag"]?.GetValue<string>() is { Length: > 0 } wanted ? SyncEngine.NormalizeTag(wanted) : null;
        var includeSkills = args["include_skills"]?.GetValue<bool>() ?? true;
        var tags = report.Tags.Where(tag => filter is null || tag.Tag == filter).ToList();
        var skills = report.Skills.Where(skill => filter is null || skill.Tags.Contains(filter)).ToList();
        return JsonSerializer.Serialize(new
        {
            tag_count = report.Tags.Count,
            skill_count = report.Skills.Count,
            tags = tags.Select(tag => new { tag.Tag, tag.Count, tag.Skills, registered_only = tag.Count == 0 ? true : (bool?)null }),
            skills = includeSkills ? skills.Select(skill => new { skill.Slug, skill.Name, skill.Scope, skill.Source, skill.Tags }) : null,
            issues = new
            {
                untagged = report.Untagged,
                near_duplicates = report.NearDuplicates,
                single_use = report.SingleUse,
                unused = report.Unused
            },
            next_step = "Fix with manage_tags (add/remove on skills, rename, merge, delete). Ask the user before merging \"narrower\" pairs or deleting a tag whose purpose is unclear."
        }, SnakeJson);
    }

    private async Task<string> ExecuteManageTagsAsync(JsonObject args, CancellationToken ct)
    {
        var action = (args["action"]?.GetValue<string>() ?? throw new ArgumentException("action is required: add, remove, rename, merge or delete."))
            .Trim().ToLowerInvariant();
        const string originator = "mcp:manage_tags";
        switch (action)
        {
            case "add":
            case "remove":
            {
                var tags = ReadStringList(args["tags"]).Concat(args["tag"]?.GetValue<string>() is { Length: > 0 } one ? [one] : []).ToList();
                var skills = ReadStringList(args["skills"]).Concat(ReadStringList(args["names"])).ToList();
                if (args["source"]?.GetValue<string>() is { Length: > 0 } source) skills.AddRange(_syncEngine.SelectPack(null, source).Select(skill => skill.Name));
                if (skills.Count == 0) throw new ArgumentException("skills is required: the skill slugs to tag (or a source pack).");
                var result = await _syncEngine.EditTagsAsync(skills, action == "add" ? tags : [], action == "remove" ? tags : [], originator, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    action, status = result.Changed.Count > 0 ? "updated" : "unchanged",
                    tags = tags.Select(SyncEngine.NormalizeTag).Distinct(), result.Changed, result.Unchanged, result.Skipped
                }, SnakeJson);
            }
            case "rename":
            case "merge":
            {
                var target = args["to"]?.GetValue<string>() ?? args["into"]?.GetValue<string>()
                    ?? throw new ArgumentException($"'{(action == "merge" ? "into" : "to")}' is required.");
                var into = SyncEngine.NormalizeTag(target);
                var sources = ReadStringList(args["from"]).Concat(args["tag"]?.GetValue<string>() is { Length: > 0 } t ? [t] : [])
                    .Select(SyncEngine.NormalizeTag).Where(tag => tag != into).Distinct().ToList();
                if (args["from"] is JsonValue single && single.TryGetValue<string>(out var fromText) && !string.IsNullOrWhiteSpace(fromText))
                    sources = sources.Append(SyncEngine.NormalizeTag(fromText)).Where(tag => tag != into).Distinct().ToList();
                if (sources.Count == 0) throw new ArgumentException("from is required: the tag (or tags) to rename or merge.");
                var existed = _syncEngine.ListTags().Any(tag => tag.Tag == into && tag.SkillCount > 0);
                var updated = new Dictionary<string, int>();
                var undo = new List<Guid>();
                foreach (var source in sources)
                {
                    var mutation = await _syncEngine.ChangeTagAsync(source, into, ct).ConfigureAwait(false);
                    updated[source] = mutation.UpdatedSkills;
                    if (mutation.UndoId is { } id) undo.Add(id);
                }
                return JsonSerializer.Serialize(new
                {
                    action, status = updated.Values.Sum() > 0 ? "updated" : "unchanged", into,
                    merged_into_existing = existed, updated_skills = updated,
                    undo_id = undo.Count == 1 ? undo[0].ToString() : null,
                    message = undo.Count == 1 ? "Undo with manage_skill_tags action \"undo\" and this undo_id." : null
                }, SnakeJson);
            }
            case "delete":
            {
                var tag = SyncEngine.NormalizeTag(args["tag"]?.GetValue<string>() ?? throw new ArgumentException("tag is required."));
                var mutation = await _syncEngine.ChangeTagAsync(tag, null, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    action, status = mutation.UpdatedSkills > 0 || mutation.UndoId is not null ? "deleted" : "unchanged", tag,
                    updated_skills = mutation.UpdatedSkills, undo_id = mutation.UndoId?.ToString()
                }, SnakeJson);
            }
            default:
                throw new ArgumentException("action must be add, remove, rename, merge or delete.");
        }
    }

    private async Task<string> ExecuteCreateProjectAsync(JsonObject args, CancellationToken ct)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("path is required: the project folder.");
        var names = args["skills"] is not null || args["names"] is not null || args["tag"] is not null || args["source"] is not null
            ? ResolveSkillSelection(args, required: false) : [];
        var targets = args["targets"] is JsonArray ? ReadStringList(args["targets"]) : null;
        var result = await Workspaces.CreateAsync(path, args["name"]?.GetValue<string>(), names, targets, args["keep_local"]?.GetValue<bool>(),
            args["create_folder"]?.GetValue<bool>() ?? false, ct, args["umbrella"]?.GetValue<bool>()).ConfigureAwait(false);
        var project = result.Project;
        return JsonSerializer.Serialize(new
        {
            status = result.Created ? "created" : "already_registered",
            project = new
            {
                project.Id, project.Name, project.Path, project.Skills, project.Targets, project.KeepLocal, project.GitRepo,
                files_written = project.FileCount, also_global = project.AlsoGlobal, project.Issues, project.Umbrella, project.Children
            },
            umbrella = result.Location.Umbrella,
            warnings = result.Location.Warnings,
            message = result.Created
                ? "Registered. Add skills with set_project_skills (or pass skills/tag/source here)."
                : "This folder was already a SkillHub project; nothing was re-registered."
        }, SnakeJson);
    }

    private string ExecuteInspectProjectSkills(JsonObject args)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("path is required: the folder you are working in.");
        var report = Workspaces.Inspect(path);
        return JsonSerializer.Serialize(report, SnakeJson);
    }

    /// <summary>Skill names from "names"/"skills", plus every skill of "tag" and/or "source" (an owner/repo or "personal").</summary>
    private List<string> ResolveSkillSelection(JsonObject args, bool required)
    {
        var names = ReadStringList(args["names"]).Concat(ReadStringList(args["skills"])).ToList();
        var tag = args["tag"]?.GetValue<string>();
        var source = args["source"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(tag) || !string.IsNullOrWhiteSpace(source))
        {
            var pack = _syncEngine.SelectPack(tag, source);
            if (pack.Count == 0) throw new ArgumentException($"No library skills match {(string.IsNullOrWhiteSpace(tag) ? "source '" + source + "'" : "tag '" + tag + "'")}.");
            names.AddRange(pack.Select(skill => skill.Name));
        }
        if (required && names.Count == 0) throw new ArgumentException("Name the skills, or give a tag or source pack.");
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly JsonSerializerOptions SnakeJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
}
