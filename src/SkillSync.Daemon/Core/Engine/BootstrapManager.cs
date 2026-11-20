using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Engine;

public static class BootstrapManager
{
    public static CanonicalSkillModel CreateBootstrapSkillModel(bool continuousLearning = true)
    {
        return new CanonicalSkillModel
        {
            Name = SyncEngine.BootstrapSkillName,
            Title = "SkillHub Bootstrap Protocol",
            Version = "1.3.0",
            SourceCategory = "system",
            OriginProvider = "SkillHub System",
            Description = "Mandatory protocol for creating, reading, updating, tagging, importing, or deleting AI agent skills and rules across all local AI environments (Claude, Gemini/Antigravity, OpenAI Codex, and Cursor) via the SkillHub MCP server.",
            WhenToUse = "ALWAYS activate whenever the user asks to create, update, delete, list, tag or import skills, toggles provider sync, starts work in a Roblox, Blender or other domain-specific project, or modifies files inside ~/.claude/skills, ~/.gemini/skills, ~/.codex/skills, ~/.agents/skills, or .cursor/rules.",
            SummaryShort = "Mandatory skill management protocol: use SkillHub MCP tools (list_skills, get_skill, create_skill, update_skill, delete_skill, manage_skill_tags, prepare_skill_import, apply_skill_import) for all skill operations.",
            Tags = ["system", "skillhub", "meta-skill"],
            Targets = new SkillTargetsConfig
            {
                Claude = new ClaudeTargetConfig
                {
                    Enabled = true,
                    AllowedTools =
                    [
                        "mcp__skillhub__list_skills",
                        "mcp__skillhub__get_skill",
                        "mcp__skillhub__create_skill",
                        "mcp__skillhub__update_skill",
                        "mcp__skillhub__delete_skill",
                        "mcp__skillhub__manage_skill_tags",
                        "mcp__skillhub__toggle_skill_provider",
                        "mcp__skillhub__search_community_skills",
                        "mcp__skillhub__browse_community_repo",
                        "mcp__skillhub__preview_community_skill",
                        "mcp__skillhub__prepare_skill_import",
                        "mcp__skillhub__apply_skill_import",
                        "mcp__skillhub__activate_catalog_skill",
                        "mcp__skillhub__clone_catalog_skill",
                        "mcp__skillhub__restore_skill",
                        "mcp__skillhub__get_sync_status",
                        "mcp__skillhub__check_skillhub_update",
                        "mcp__skillhub__set_skillhub_preferences",
                        "mcp__skillhub__propose_skill_improvement",
                        "mcp__skillhub__list_skill_proposals",
                        "mcp__skillhub__apply_skill_proposal",
                        "mcp__skillhub__lint_skill",
                        "mcp__skillhub__lint_library",
                        "mcp__skillhub__get_context_budget",
                        "mcp__skillhub__set_skill_scope",
                        "mcp__skillhub__test_skill_trigger",
                        "mcp__skillhub__list_projects",
                        "mcp__skillhub__set_project_skills"
                    ]
                },
                Gemini = new GeminiTargetConfig { Enabled = true },
                Codex = new CodexTargetConfig { Enabled = true },
                Cursor = new CursorTargetConfig
                {
                    Enabled = true,
                    Globs = ["**/*"],
                    AlwaysApply = true
                }
            },
            InstructionsMarkdown =
                """
                # SkillHub Universal Skill Management Protocol

                This machine runs **SkillHub**, a centralized Single Source of Truth and multi-provider transpiler that synchronizes AI agent skills across Claude, Google Gemini / Antigravity, OpenAI Codex / ChatGPT, and Cursor.

                ## Mandatory Rules for Skill Operations

                1. **PREFER SKILLHUB MCP TOOLS OVER DIRECT FILE WRITES:**
                   Whenever the user asks you to create, edit, inspect, tag, import, or delete a skill (or a reusable agent rule/workflow), you **MUST** use the `skillhub` MCP server tools:
                   - `list_skills` — Check existing skills (supports `category`: `"user"`, `"synced"`, `"template"`, `"system"`).
                   - `get_skill` — Read the full canonical skill definition (including cross-provider metadata and bundled scripts).
                   - `create_skill` — Create a new skill once in the canonical store; SkillHub automatically transpiles and distributes it to Claude, Gemini, Codex, and Cursor.
                   - `update_skill` — Modify an existing skill without losing provider-specific metadata (`allowed_tools`, `cursor_globs`, `when_to_use`, `summary_short`).
                   - `delete_skill` — Cleanly remove a skill from all providers simultaneously.
                   - `manage_skill_tags` — List, add, remove, rename, or delete tags across skills (`action`: `"list"`, `"add"`, `"remove"`, `"rename"`, `"delete"`).
                   - `toggle_skill_provider` — Enable or disable syncing a skill or built-in template to a specific provider (`claude`, `gemini`, `codex`, `cursor`).
                   - `search_community_skills` — Search the whole ecosystem at once: the `skills.sh` public directory, curated GitHub repositories, repositories the user pinned in Explore, and the offline catalog. Pass `repo` to search inside any GitHub repository, or `source: "skills.sh"` for the directory only.
                   - `browse_community_repo` — List every skill, command, rule and agent in any public GitHub `owner/repo` (indexed on the fly), with its README, license and per-skill companion file counts.
                   - `preview_community_skill` — Read a candidate's full `SKILL.md`, its bundle manifest and script review (and, with `include_files` or `file`, its companion files) before importing.
                   - `prepare_skill_import` — Review a pinned community skill with its origin, license, and files.
                   - `apply_skill_import` — Import the reviewed revision and deploy only to explicitly selected providers.
                   - `get_sync_status` — Inspect connected AI providers, skill counts, and recent sync events.

                2. **FINDING SKILLS FOR THE USER'S PROJECT:**
                   When the user asks "are there good skills for my project?" or "check `owner/repo` for useful skills":
                   1. Work out what the project needs (languages, frameworks, tools, recurring tasks) from the conversation and the workspace.
                   2. Call `list_skills` first so you do not recommend something the user already has.
                   3. Search: `search_community_skills` with focused keywords (one call per need, e.g. `"playwright"`, `"stripe webhooks"`), or `browse_community_repo` when the user names a repository. Any public GitHub repository works; it does not need to be curated or pinned.
                   4. Read the two to five most promising results with `preview_community_skill` (use the `repo`, `path` and `git_ref` from the result). Check the instructions, license, companion files and `script_review` before recommending anything.
                   5. Recommend the best fits with a one-line reason each, and import only what the user approves (`prepare_skill_import` then `apply_skill_import`, or `import_community_skill`). Imports stay local unless the user asks for provider deployment.

                3. **DO NOT MANUALLY COPY SKILLS BETWEEN PROVIDERS:**
                   Never run shell commands (`cp`, `Copy-Item`, `rsync`) to copy `SKILL.md` files between `~/.claude/skills/`, `~/.gemini/skills/`, `~/.codex/skills/`, `~/.agents/skills/`, or `.cursor/rules/`. SkillHub handles cross-provider transpilation automatically in real time.

                4. **PRESERVE CROSS-PROVIDER METADATA WHEN AUTHORING SKILLS:**
                   When calling `create_skill` or `update_skill`, always provide:
                   - `name`: Lowercase kebab-case slug (e.g., `api-contract-validator`).
                   - `description`: Detailed explanation of what the skill does.
                   - `when_to_use`: Explicit trigger conditions (SkillHub combines this into a single-line description for Gemini and keeps it separate for Claude).
                   - `summary_short`: A concise (<160 chars) summary optimized for OpenAI Codex's token budget.
                   - `instructions_markdown`: Clean, well-structured Markdown body (without YAML frontmatter fences, as SkillHub generates the frontmatter for each target provider).

                5. **FALLBACK IF MCP TOOLS ARE UNAVAILABLE:**
                   Only if the `skillhub` MCP server is unreachable or not loaded in your current session may you write directly to `~/.skillhub/skills/<name>/SKILL.md` (or your native skills directory). SkillHub's background bi-directional file watcher will ingest and transpile the change automatically.

                6. **SKILLHUB ITSELF:**
                   - "Is SkillHub up to date?" → `check_skillhub_update`. Install only when the user agrees → `apply_skillhub_update` (SkillHub restarts).
                   - "Turn continuous skill learning on/off" → `set_skillhub_preferences` with `continuous_learning`.
                   - Importing a community skill: `import_community_skill` syncs to the detected AI tools by default. If it reports scripts, show the user the script list with the repository's stars and installs, and only call it again with `confirm_scripts: true` after the user agrees.

                7. **SCOPE, PROJECTS AND LIBRARY HEALTH:**
                   - Every global skill puts its description into every chat. `get_context_budget` shows that always-loaded cost by pack. Niche packs (a game engine, a 3D tool, one client's stack) belong in **Project-only** scope: `set_skill_scope` with `scope: "project"` and `source: "<owner/repo>"` or `tag`.
                   - "Use the Roblox pack in this repo" → `set_project_skills` with the repository's absolute `path`, the `source` or `tag` (or `skills`), and `make_project_only: true`. SkillHub writes them into the project's `.claude/skills`, `.agents/skills`, `.gemini/skills` and `.cursor/rules` and, by default, keeps them out of Git via `.git/info/exclude`. `list_projects` shows what each project has.
                   - "Audit my skills" → `lint_library` (collisions, missing companion skills, over-long descriptions, budget). After changing a description, check it with `test_skill_trigger` and a realistic prompt.

                8. **SUGGEST PROJECT-ONLY PROACTIVELY (one short sentence, never act without a yes):**
                   - **After importing a domain-specific pack** (a game engine such as Roblox or Unity, a 3D tool such as Blender, game dev, one framework or client stack), or whenever `get_context_budget` reports `warning` or `over`: suggest moving the niche packs to Project-only, naming the pack and the tokens it saves, e.g. "This Roblox pack adds ~2.2k tokens to every chat. Make it Project-only so it loads only in your Roblox repos?" On a yes → `set_skill_scope` with `scope: "project"` and that `source` or `tag`. General-purpose skills (git, testing, writing, UI basics) stay global.
                   - **When the workspace matches a Project-only pack that this project does not have yet** (e.g. `.luau`, `default.project.json` or `.rbxlx` for Roblox; `.blend` or `bpy` scripts for Blender): check `list_skills` with `scope: "project"` and `list_projects`, then offer once: "SkillHub has a Project-only Roblox pack. Attach it to this project?" On a yes → `set_project_skills` with the project's absolute `path` and the pack's `source` or `tag`. Do not ask again in the same conversation after a no.
                """ + (continuousLearning ? ContinuousLearningSection : string.Empty)
        };
    }

    /// <summary>Included while continuous skill learning is on (Settings or <c>set_skillhub_preferences</c>).</summary>
    public const string ContinuousLearningSection =
        """

        9. **CONTINUOUS SKILL LEARNING (ON):**
           - Never judge a skill early at the start of a task, and never suggest changes on routine tasks.
           - Imported community skills (those with an `import_source`) belong to their upstream repository: do not offer to edit them; suggest a personal skill instead.
           - Only **after** you solved a non-trivial problem, hit an outdated or wrong instruction in a skill, or found a reusable project pattern or workaround that would clearly make a relevant skill better, ask **once, at the end of your reply**: "Want me to update your `<skill>` skill in SkillHub with <one-line summary>?"
           - When the user says yes, apply it right away with `update_skill` (SkillHub keeps the previous version in History and syncs every AI tool).
           - When the user says "save it for later", call `propose_skill_improvement`. Saved proposals are reviewed with `list_skill_proposals` and applied with `apply_skill_proposal` (or in the SkillHub app).
        """;

    /// <summary>Turns continuous skill learning on or off: rewrites the bootstrap skill and every provider copy of personal skills.</summary>
    public static async Task ApplyContinuousLearningAsync(SyncEngine engine, bool enabled, CancellationToken ct = default)
    {
        engine.ContinuousLearning = enabled;
        await EnsureBootstrapSkillSeededAsync(engine, ct).ConfigureAwait(false);
        await engine.SynchronizeAllAsync(ct).ConfigureAwait(false);
    }

    public static async Task EnsureBootstrapSkillSeededAsync(SyncEngine engine, CancellationToken ct = default)
    {
        var model = CreateBootstrapSkillModel(engine.ContinuousLearning);
        await engine.UpsertSkillDirectAsync(model, "bootstrap-seed", ct).ConfigureAwait(false);
    }
}
