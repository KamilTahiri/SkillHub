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
            also_global = project.AlsoGlobal, project.MissingSkills, project.OffSkills, project.LastSyncedAt,
            issues = project.Issues
        }),
        targets = WorkspaceTargets.All,
        hint = "Use set_project_skills with a project path to choose skills (or a whole tag/source pack) for that repository."
    }, SnakeJson);

    private async Task<string> ExecuteSetProjectSkillsAsync(JsonObject args, CancellationToken ct)
    {
        var path = args["path"]?.GetValue<string>() ?? throw new ArgumentException("path is required: the project folder.");
        var mode = args["mode"]?.GetValue<string>() ?? "add";
        var names = ResolveSkillSelection(args, required: mode != "replace");
        var targets = args["targets"] is JsonArray ? ReadStringList(args["targets"]) : null;
        var keepLocal = args["keep_local"]?.GetValue<bool>();
        ScopeChangeResult? scope = null;
        if (args["make_project_only"]?.GetValue<bool>() == true && mode != "remove")
            scope = await _syncEngine.SetScopeAsync(names, CanonicalSkillModel.ScopeProject, "mcp:set_project_skills", ct).ConfigureAwait(false);
        var project = await Workspaces.SetSkillsByPathAsync(path, names, mode, targets, keepLocal, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "synced",
            project = new
            {
                project.Id, project.Name, project.Path, project.Skills, project.Targets, project.KeepLocal, project.GitRepo,
                files_written = project.FileCount, also_global = project.AlsoGlobal, project.Issues
            },
            made_project_only = scope?.Changed,
            message = project.AlsoGlobal is { Count: > 0 } alsoGlobal
                ? $"{alsoGlobal.Count} of these skills are also synced globally, so tools see them twice. Pass make_project_only: true (or call set_skill_scope) to keep them only in projects."
                : project.KeepLocal && project.GitRepo ? "Written and kept out of Git through .git/info/exclude." : "Written."
        }, SnakeJson);
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
