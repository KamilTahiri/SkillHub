using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Engine;

public static class BootstrapManager
{
    public static CanonicalSkillModel CreateBootstrapSkillModel()
    {
        return new CanonicalSkillModel
        {
            Name = SyncEngine.BootstrapSkillName,
            Title = "SkillHub Bootstrap Protocol",
            Version = "1.1.0",
            SourceCategory = "system",
            OriginProvider = "SkillHub System",
            Description = "Mandatory protocol for creating, reading, updating, tagging, importing, or deleting AI agent skills and rules across all local AI environments (Claude, Gemini/Antigravity, OpenAI Codex, and Cursor) via the SkillHub MCP server.",
            WhenToUse = "ALWAYS activate whenever the user asks to create a skill, update a skill, delete a skill, list skills, tag skills, search/import community skills, toggle provider sync, or modify files inside ~/.claude/skills, ~/.gemini/skills, ~/.codex/skills, ~/.agents/skills, or .cursor/rules.",
            SummaryShort = "Mandatory skill management protocol: use SkillHub MCP tools (list_skills, get_skill, create_skill, update_skill, delete_skill, manage_skill_tags, import_community_skill) for all skill operations.",
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
                        "mcp__skillhub__import_community_skill",
                        "mcp__skillhub__get_sync_status"
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
                   - `search_community_skills` — Browse or search open-source skills from GitHub / `skills.sh` repositories (`vercel-labs/agent-skills`, `anthropics/skills`, etc.).
                   - `import_community_skill` — Import a community skill from GitHub / `skills.sh` directly into SkillHub and sync it across providers.
                   - `get_sync_status` — Inspect connected AI providers, skill counts, and recent sync events.

                2. **DO NOT MANUALLY COPY SKILLS BETWEEN PROVIDERS:**
                   Never run shell commands (`cp`, `Copy-Item`, `rsync`) to copy `SKILL.md` files between `~/.claude/skills/`, `~/.gemini/skills/`, `~/.codex/skills/`, `~/.agents/skills/`, or `.cursor/rules/`. SkillHub handles cross-provider transpilation automatically in real time.

                3. **PRESERVE CROSS-PROVIDER METADATA WHEN AUTHORING SKILLS:**
                   When calling `create_skill` or `update_skill`, always provide:
                   - `name`: Lowercase kebab-case slug (e.g., `api-contract-validator`).
                   - `description`: Detailed explanation of what the skill does.
                   - `when_to_use`: Explicit trigger conditions (SkillHub combines this into a single-line description for Gemini and keeps it separate for Claude).
                   - `summary_short`: A concise (<160 chars) summary optimized for OpenAI Codex's token budget.
                   - `instructions_markdown`: Clean, well-structured Markdown body (without YAML frontmatter fences, as SkillHub generates the frontmatter for each target provider).

                4. **FALLBACK IF MCP TOOLS ARE UNAVAILABLE:**
                   Only if the `skillhub` MCP server is unreachable or not loaded in your current session may you write directly to `~/.skillhub/skills/<name>/SKILL.md` (or your native skills directory). SkillHub's background bi-directional file watcher will ingest and transpile the change automatically.
                """
        };
    }

    public static async Task EnsureBootstrapSkillSeededAsync(SyncEngine engine, CancellationToken ct = default)
    {
        var model = CreateBootstrapSkillModel();
        await engine.UpsertSkillDirectAsync(model, "bootstrap-seed", ct).ConfigureAwait(false);
    }
}
