using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SkillSync.Daemon.Mcp;

public static class McpToolDefinitions
{
    private static readonly JsonArray CachedToolsArray = BuildToolsArray();

    public static JsonArray GetToolsJsonArray() => JsonNode.Parse(CachedToolsArray.ToJsonString())!.AsArray();

    private static readonly Dictionary<string, JsonObject> InputSchemas = CachedToolsArray
        .OfType<JsonObject>().ToDictionary(tool => tool["name"]!.GetValue<string>(), tool => tool["inputSchema"]!.AsObject());

    internal static string? ValidateArguments(string name, JsonObject args) =>
        InputSchemas.TryGetValue(name, out var schema) ? ValidateValue(args, schema, "arguments") : $"Unknown tool '{name}'.";

    private static string? ValidateValue(JsonNode? value, JsonObject schema, string path)
    {
        var type = schema["type"]!.GetValue<string>();
        var kind = value?.GetValueKind();
        var valid = type switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "integer" => kind == JsonValueKind.Number && ((JsonValue)value!).TryGetValue<long>(out _),
            _ => false
        };
        if (!valid) return $"{path} must be of type {type}.";
        if (schema["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(value, choice)))
            return $"{path} is not one of the supported values.";

        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
                foreach (var field in required)
                    if (!obj.ContainsKey(field!.GetValue<string>())) return $"{path}.{field.GetValue<string>()} is required.";
            var properties = schema["properties"] as JsonObject;
            foreach (var (key, field) in obj)
            {
                if (properties?[key] is JsonObject property)
                {
                    if (ValidateValue(field, property, $"{path}.{key}") is { } error) return error;
                }
                else if (schema["additionalProperties"]?.GetValue<bool>() == false)
                    return $"{path}.{key} is not supported.";
            }
        }
        if (value is JsonArray array && schema["items"] is JsonObject items)
        {
            for (var index = 0; index < array.Count; index++)
                if (ValidateValue(array[index], items, $"{path}[{index}]") is { } error) return error;
        }
        if (type == "string")
        {
            var input = value!.GetValue<string>();
            var length = input.EnumerateRunes().Count();
            if (schema["minLength"] is JsonValue min && length < min.GetValue<int>() ||
                schema["maxLength"] is JsonValue max && length > max.GetValue<int>())
                return $"{path} is outside the allowed length.";
            if (schema["pattern"] is JsonValue pattern &&
                !Regex.IsMatch(input, pattern.GetValue<string>(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                return $"{path} does not match the required format.";
        }
        if (type == "integer" && schema["minimum"] is JsonValue minimum &&
            value!.GetValue<long>() < minimum.GetValue<long>())
            return $"{path} is below the allowed minimum.";
        return null;
    }

    private static JsonArray BuildToolsArray()
    {
        const string schemaJson =
            """
            [
              {
                "name": "list_skills",
                "description": "Lists all AI agent skills stored in the central SkillHub repository across Claude, Gemini/Antigravity, OpenAI Codex, and Cursor. Always call this before creating a skill to check if a relevant skill already exists.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "query": {
                      "type": "string",
                      "description": "Optional case-insensitive substring filter matching skill name, title, description, instructions, or tags."
                    },
                    "tag": {
                      "type": "string",
                      "description": "Optional tag filter."
                    },
                    "scope": {
                      "type": "string",
                      "enum": ["global", "project", "off"],
                      "description": "Optional scope filter: global (synced to the tools' user folders), project (Project-only) or off."
                    },
                    "category": {
                      "type": "string",
                      "enum": ["all", "user", "synced", "template", "system", "plugin"],
                      "description": "Optional category filter ('user' for personal skills, 'synced' for all active synced skills, 'template' for built-in templates)."
                    },
                    "include_disabled": {
                      "type": "boolean",
                      "description": "Whether to include skills disabled for specific providers. Defaults to true.",
                      "default": true
                    }
                  },
                  "additionalProperties": false
                }
              },
              {
                "name": "get_skill",
                "description": "Retrieves the full canonical metadata, provider-specific configurations, Markdown instructions, and list of bundled script/resource files for a specific skill.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "name": {
                      "type": "string",
                      "pattern": "^[a-z0-9]+(-[a-z0-9]+)*$",
                      "description": "The kebab-case identifier (slug) of the skill, e.g., 'git-commit-helper'."
                    },
                    "include_supporting_files": {
                      "type": "boolean",
                      "description": "If true, returns the text contents of files inside scripts/, resources/, examples/, and references/.",
                      "default": false
                    }
                  },
                  "required": ["name"],
                  "additionalProperties": false
                }
              },
              {
                "name": "create_skill",
                "description": "Creates a new canonical AI skill in SkillHub and automatically transpiles and syncs it to all installed AI providers (Claude, Gemini/Antigravity, OpenAI Codex, Cursor). Always use this tool instead of writing directly to ~/.claude/skills, ~/.gemini/skills, ~/.codex/skills, ~/.agents/skills, or .cursor/rules. Write the description as a trigger + pointer (when to use it, and that the full SKILL.md must be read first when it has guardrails), never as a summary of its rules.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "name": {
                      "type": "string",
                      "pattern": "^[a-z0-9]+(-[a-z0-9]+)*$",
                      "minLength": 2,
                      "maxLength": 64,
                      "description": "Unique kebab-case skill name (e.g., 'docker-debugger'). Must match folder naming rules."
                    },
                    "title": {
                      "type": "string",
                      "maxLength": 100,
                      "description": "Optional human-friendly display title (e.g., 'Docker Debugger')."
                    },
                    "description": {
                      "type": "string",
                      "minLength": 10,
                      "maxLength": 500,
                      "description": "A trigger + pointer, never a summary of the rules: say what the skill is for and when it must activate. If the body has mandatory steps or guardrails (never, must, only if), say explicitly that the AI MUST read the full SKILL.md before acting, e.g. 'Use whenever you commit, push, tag or release. Read the full SKILL.md before running any git command.' Agents that only see the description act on a summary and skip the rules."
                    },
                    "when_to_use": {
                      "type": "string",
                      "maxLength": 500,
                      "description": "Specific trigger conditions and user intents that should activate this skill."
                    },
                    "summary_short": {
                      "type": "string",
                      "maxLength": 160,
                      "description": "Ultra-concise (<160 chars) description optimized for strict token budgets (used by OpenAI Codex)."
                    },
                    "instructions_markdown": {
                      "type": "string",
                      "minLength": 1,
                      "description": "The full Markdown body of the skill (excluding YAML frontmatter)."
                    },
                    "tags": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Optional categorization tags."
                    },
                    "allowed_tools": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Optional list of pre-approved tool patterns for Claude (e.g., ['Bash(git:*)', 'Read'])."
                    },
                    "cursor_globs": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Optional file glob patterns for Cursor rules (defaults to ['**/*'])."
                    },
                    "cursor_always_apply": {
                      "type": "boolean",
                      "default": false,
                      "description": "Whether Cursor should always inject this rule regardless of context."
                    },
                    "supporting_files": {
                      "type": "array",
                      "description": "Optional helper scripts, templates, or reference files to bundle inside the skill folder.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "relative_path": {
                            "type": "string",
                            "pattern": "^(scripts|resources|references|examples)/[a-zA-Z0-9._/-]+$",
                            "description": "Relative path inside the skill folder, restricted to scripts/, resources/, references/, or examples/."
                          },
                          "content": {
                            "type": "string",
                            "description": "UTF-8 file content."
                          }
                        },
                        "required": ["relative_path", "content"],
                        "additionalProperties": false
                      }
                    }
                  },
                  "required": ["name", "description", "instructions_markdown"],
                  "additionalProperties": false
                }
              },
              {
                "name": "update_skill",
                "description": "Updates an existing skill in the canonical SkillHub store and propagates transpiled updates to all enabled AI providers. Omitted optional fields preserve their existing canonical values (non-destructive partial update).",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "name": {
                      "type": "string",
                      "pattern": "^[a-z0-9]+(-[a-z0-9]+)*$",
                      "description": "The kebab-case name of the existing skill to update."
                    },
                    "title": {
                      "type": "string",
                      "description": "Updated human-friendly title (omit to keep existing)."
                    },
                    "description": {
                      "type": "string",
                      "description": "Updated description (omit to keep existing). A trigger + pointer, never a summary of the rules: say what the skill is for and when it must activate. If the body has mandatory steps or guardrails (never, must, only if), say explicitly that the AI MUST read the full SKILL.md before acting, e.g. 'Use whenever you commit, push, tag or release. Read the full SKILL.md before running any git command.' Agents that only see the description act on a summary and skip the rules."
                    },
                    "when_to_use": {
                      "type": "string",
                      "description": "Updated trigger conditions (omit to keep existing)."
                    },
                    "summary_short": {
                      "type": "string",
                      "maxLength": 160,
                      "description": "Updated short summary for OpenAI Codex (omit to keep existing)."
                    },
                    "instructions_markdown": {
                      "type": "string",
                      "description": "Updated Markdown body (omit to keep existing)."
                    },
                    "tags": {
                      "type": "array",
                      "items": { "type": "string" }
                    },
                    "allowed_tools": {
                      "type": "array",
                      "items": { "type": "string" }
                    },
                    "cursor_globs": {
                      "type": "array",
                      "items": { "type": "string" }
                    },
                    "cursor_always_apply": {
                      "type": "boolean"
                    },
                    "revision": {
                  "type": "integer",
                  "minimum": 1,
                  "description": "Expected canonical revision from get_skill. Use it for edits based on a previously read document."
                },
                "upsert_supporting_files": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "relative_path": {
                            "type": "string",
                            "pattern": "^(scripts|resources|references|examples)/[a-zA-Z0-9._/-]+$"
                          },
                          "content": { "type": "string" }
                        },
                        "required": ["relative_path", "content"],
                        "additionalProperties": false
                      }
                    },
                    "delete_supporting_files": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Relative paths of supporting files to remove."
                    }
                  },
                  "required": ["name"],
                  "additionalProperties": false
                }
              },
              {
                "name": "delete_skill",
                "description": "Deletes a skill from the canonical SkillHub repository and removes its transpiled counterparts from all connected AI providers (Claude, Gemini, Codex, Cursor). System bootstrap skills ('00-skillhub-bootstrap') are protected and cannot be deleted.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "name": {
                      "type": "string",
                      "pattern": "^[a-z0-9]+(-[a-z0-9]+)*$",
                      "description": "The kebab-case name of the skill to delete."
                    }
                  },
                  "required": ["name"],
                  "additionalProperties": false
                }
              },
              {
                "name": "manage_skill_tags",
                "description": "Lists, adds, removes, renames, or deletes tags across skills in SkillHub.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "action": {
                      "type": "string",
                      "enum": ["list", "create", "add", "remove", "rename", "delete", "undo"],
                      "description": "Tag operation to perform."
                    },
                    "tag": { "type": "string" },
                    "name": { "type": "string", "description": "Alias for skill_name." },
                    "undo_id": { "type": "string" },
                    "skill_name": {
                      "type": "string",
                      "description": "Skill slug (required when action is 'add' or 'remove')."
                    },
                    "tags": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Tags to add, remove, or delete."
                    },
                    "old_tag": {
                      "type": "string",
                      "description": "Existing tag name to rename (when action is 'rename')."
                    },
                    "new_tag": {
                      "type": "string",
                      "description": "New tag name (when action is 'rename')."
                    }
                  },
                  "required": ["action"],
                  "additionalProperties": false
                }
              },
              {
                "name": "toggle_skill_provider",
                "description": "Enables or disables syncing a managed skill to an AI provider (claude, gemini, codex, cursor).",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "name": {
                      "type": "string",
                      "description": "The kebab-case skill name."
                    },
                    "provider": {
                      "type": "string",
                      "enum": ["claude", "gemini", "codex", "cursor"],
                      "description": "Target AI provider."
                    },
                    "enabled": {
                      "type": "boolean",
                      "description": "True to enable syncing to this provider, false to disable."
                    }
                  },
                  "required": ["name", "provider", "enabled"],
                  "additionalProperties": false
                }
              },
              {
                "name": "search_community_skills",
                "description": "Searches the whole skill ecosystem in one call: the skills.sh public directory, SkillHub's curated GitHub repositories, repositories the user pinned in Explore, and the offline plugin catalog. Pass `repo` (any public GitHub 'owner/repo', URL or skills.sh link) to search inside that repository instead; it is indexed on the fly. Each result has repo, path and git_ref for preview_community_skill, plus file_count (companion files besides SKILL.md; files_known=false means the listing has not read the folder yet). Use this to answer 'are there good skills for my project?' then preview the best candidates before importing.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "query": {
                      "type": "string",
                      "description": "Keywords describing what the skill should do (for example 'playwright testing', 'stripe', 'react performance'), or an owner/repo shorthand."
                    },
                    "repo": {
                      "type": "string",
                      "description": "Optional GitHub repository to search inside, e.g. 'vercel-labs/agent-skills', 'https://github.com/anthropics/skills', or 'skills.sh' for the directory only."
                    },
                    "source": {
                      "type": "string",
                      "enum": ["all", "skills.sh", "curated", "catalog"],
                      "description": "Narrow a keyword search: 'skills.sh' (public directory only), 'curated' (curated and pinned GitHub repositories), 'catalog' (offline plugin catalog). Default 'all'."
                    },
                    "kind": {
                      "type": "string",
                      "enum": ["skill", "command", "rule", "agent", "doc"],
                      "description": "Only return documents of this kind."
                    },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 200, "description": "Maximum results to return (default 50)." },
                    "offset": { "type": "integer", "minimum": 0, "description": "Skip this many results (for paging)." }
                  },
                  "additionalProperties": false
                }
              },
              {
                "name": "browse_community_repo",
                "description": "Lists every skill, command, rule and agent inside any public GitHub repository (indexed on the fly and cached per commit), with the repository README excerpt, license, commit SHA, counts per kind and each skill's companion file count. Use it when the user names a repository ('check owner/repo for useful skills').",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "repo": { "type": "string", "description": "GitHub 'owner/repo', a github.com URL (a folder URL narrows the listing), or a skills.sh link." },
                    "query": { "type": "string", "description": "Optional keywords to filter the listing; also searches cached document bodies." },
                    "kind": { "type": "string", "enum": ["skill", "command", "rule", "agent", "doc"], "description": "Only list documents of this kind." },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "Maximum entries to return (default 100)." },
                    "offset": { "type": "integer", "minimum": 0 }
                  },
                  "required": ["repo"],
                  "additionalProperties": false
                }
              },
              {
                "name": "preview_community_skill",
                "description": "Reads one community skill before importing it: the full SKILL.md instructions, description, license, pinned commit, the complete bundle manifest (every companion file, plus files that cannot be imported), and a script review of executable content. Set include_files to also read the companion files' text, or file to read one of them. Nothing is imported or changed. Use the result to judge whether the skill fits the user's project, then call prepare_skill_import / apply_skill_import or import_community_skill.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "repo": { "type": "string", "description": "The repo value from a search or browse result ('owner/repo' or 'local/catalog')." },
                    "path": { "type": "string", "description": "The path value from the result (for skills.sh rows this is the skill id), or a skill folder." },
                    "git_ref": { "type": "string", "description": "The git_ref from the result; defaults to the default branch." },
                    "file": { "type": "string", "description": "Optional companion file to read, as listed in supporting_paths (e.g. 'scripts/run.py')." },
                    "include_files": { "type": "boolean", "description": "Also return the text of companion files (up to 20 files and 200 KB in total)." },
                    "max_chars": { "type": "integer", "minimum": 1000, "maximum": 200000, "description": "Truncate the instructions to this many characters (default 60000)." }
                  },
                  "required": ["repo", "path"],
                  "additionalProperties": false
                }
              },
              {
                "name": "prepare_skill_import",
                "description": "Review one exact skill, including its source, revision, license, files, and name collision, before importing.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "source": { "type": "string" },
                    "repo": { "type": "string" },
                    "path": { "type": "string" },
                    "git_ref": { "type": "string" },
                    "raw_markdown": { "type": "string" },
                    "file_name": { "type": "string" }
                  },
                  "additionalProperties": false
                }
              },
              {
                "name": "apply_skill_import",
                "description": "Import the reviewed revision. Defaults to a local copy with no provider deployments.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "plan_id": { "type": "string" },
                    "revision": { "type": "string" },
                    "name": { "type": "string" },
                    "providers": { "type": "array", "items": { "type": "string", "enum": ["claude", "gemini", "codex", "cursor"] } }
                  },
                  "required": ["plan_id", "revision"],
                  "additionalProperties": false
                }
              },
              {
                "name": "import_community_skill",
                "description": "Imports an open-source skill from GitHub or skills.sh (e.g., 'vercel-labs/agent-skills/skills/react-best-practices') into SkillHub and, by default, syncs it to every detected AI tool (Codex, Gemini/Antigravity, Claude) so it works immediately. If the skill ships executable scripts, nothing is imported until confirmed: the response has status 'needs_confirmation' with the script list, risk flags, GitHub stars and installs. Show these to the user, and only after they agree call again with confirm_scripts: true.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "source": {
                      "type": "string",
                      "description": "GitHub URL, owner/repo/path shorthand, or skills.sh command."
                    },
                    "auto_sync": { "type": "boolean", "description": "Sync to the detected AI tools right away (default true). False keeps it in SkillHub only." },
                    "providers": { "type": "array", "items": { "type": "string", "enum": ["claude", "gemini", "codex", "cursor", "copilot", "windsurf"] }, "description": "Exact AI tools to sync to instead of the detected defaults." },
                    "confirm_scripts": { "type": "boolean", "description": "Set to true only after the user agreed to import a skill that contains scripts." },
                    "name": { "type": "string", "description": "Optional name to save it as." }
                  },
                  "required": ["source"],
                  "additionalProperties": false
                }
              },
              {
                "name": "activate_catalog_skill",
                "description": "Create a managed deployment from a read-only catalog entry.",
                "inputSchema": {
                  "type": "object",
                  "properties": { "catalog_id": { "type": "string" } },
                  "required": ["catalog_id"],
                  "additionalProperties": false
                }
              },
              {
                "name": "clone_catalog_skill",
                "description": "Create a personal, disabled copy of a read-only catalog entry.",
                "inputSchema": {
                  "type": "object",
                  "properties": { "catalog_id": { "type": "string" } },
                  "required": ["catalog_id"],
                  "additionalProperties": false
                }
              },
              {
                "name": "restore_skill",
                "description": "Restore a managed skill from SkillHub trash.",
                "inputSchema": {
                  "type": "object",
                  "properties": { "name": { "type": "string" } },
                  "required": ["name"],
                  "additionalProperties": false
                }
              },
              {
                "name": "lint_skill",
                "description": "Runs Skill Doctor on a SkillHub skill: provider frontmatter rules, description and token budgets, broken links to bundle files, triggers that overlap other skills, leaked secrets and risky scripts. Returns a score with findings to fix.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "The skill name (slug)." }
                  },
                  "required": ["name"],
                  "additionalProperties": false
                }
              },
              {
                "name": "lint_library",
                "description": "Runs Skill Doctor across the whole library in one call: per-skill findings, trigger collisions (groups of skills that compete for the same requests), references to companion skills that are not installed, descriptions over the 1,024-character limit, and the always-loaded token budget. Returns a library score.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "include_clean_skills": { "type": "boolean", "description": "Also list skills without findings (default false)." }
                  },
                  "additionalProperties": false
                }
              },
              {
                "name": "get_context_budget",
                "description": "Returns the always-loaded token budget: how many tokens of skill names and descriptions every chat receives from globally synced skills, broken down by pack (personal skills, each imported repository, SkillHub), with a warning state when tools such as Claude Code start dropping descriptions.",
                "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false }
              },
              {
                "name": "set_skill_scope",
                "description": "Sets where skills are delivered, for named skills and/or a whole pack. global: the tools' user folders (costs always-loaded tokens in every chat). project: Project-only, written only into project workspaces that choose it (0 global tokens). off: nowhere. Use source (owner/repo or 'personal') or tag to move a pack in one call.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "scope": { "type": "string", "enum": ["global", "project", "off"] },
                    "names": { "type": "array", "items": { "type": "string" }, "description": "Skill names (slugs)." },
                    "tag": { "type": "string", "description": "Every library skill with this tag." },
                    "source": { "type": "string", "description": "Every skill imported from this repository (owner/repo), or 'personal' for skills written in SkillHub." }
                  },
                  "required": ["scope"],
                  "additionalProperties": false
                }
              },
              {
                "name": "test_skill_trigger",
                "description": "Trigger tester: ranks which skills an agent would most likely load for a sample prompt (lexical match on names, descriptions, when-to-use and tags) and flags ties where several skills compete. Use it after editing descriptions to check that the right skill wins.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "prompt": { "type": "string", "minLength": 1, "description": "A request a user might type, e.g. 'Make a Roblox HUD'." },
                    "include_project_only": { "type": "boolean", "description": "Rank Project-only skills too, as inside a project that enables them (default false)." },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 20, "description": "How many candidates to return (default 5)." }
                  },
                  "required": ["prompt"],
                  "additionalProperties": false
                }
              },
              {
                "name": "list_projects",
                "description": "Lists project workspaces: each project folder, the skills SkillHub writes into its own .claude/.agents/.gemini/.cursor folders, whether those are kept out of Git, and chosen skills that are also synced globally (duplicates).",
                "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false }
              },
              {
                "name": "set_project_skills",
                "description": "Chooses skills for a project folder (registered automatically on first use) and writes them into the project's AI folders. Name skills and/or a whole pack by tag or source. make_project_only: true also moves those skills off global sync so they cost no tokens in other chats. Use for requests like 'use the Roblox pack in this repo'.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 1, "description": "Absolute path of the project folder (the repository root)." },
                    "skills": { "type": "array", "items": { "type": "string" }, "description": "Skill names (slugs)." },
                    "tag": { "type": "string", "description": "Add every library skill with this tag." },
                    "source": { "type": "string", "description": "Add every skill imported from this repository (owner/repo), or 'personal'." },
                    "mode": { "type": "string", "enum": ["add", "remove", "replace"], "description": "add (default) keeps the project's other skills; remove takes these out; replace sets exactly these." },
                    "make_project_only": { "type": "boolean", "description": "Also set these skills to Project-only scope (removes them from global sync)." },
                    "targets": { "type": "array", "items": { "type": "string", "enum": ["claude", "codex", "gemini", "cursor", "copilot", "windsurf", "cline", "roocode"] }, "description": "Project folders to write (default claude, codex, gemini, cursor)." },
                    "keep_local": { "type": "boolean", "description": "Keep the generated folders out of Git via .git/info/exclude (default true). false: commit them for a team." },
                    "umbrella": { "type": "boolean", "description": "Umbrella folder mode for a path that groups several projects (e.g. 'Roblox Games'): true writes the skills into each direct subfolder (and each child repository's .git/info/exclude), never into the umbrella folder itself, and new subfolders get them automatically. false returns to a single project folder. Omit to keep the current mode." }
                  },
                  "required": ["path"],
                  "additionalProperties": false
                }
              },
              {
                "name": "list_tags",
                "description": "Token-efficient tag audit. Returns every tag with its skill count and skill slugs, each skill's {slug, name, scope, source, tags} (never bodies or companion files), and hygiene issues: untagged skills, near-duplicate tags (spelling, separator, plural, typo, narrower), single-use and unused tags. Fix them with manage_tags.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "tag": { "type": "string", "description": "Only this tag and the skills that carry it." },
                    "include_skills": { "type": "boolean", "description": "Include the per-skill list (default true). false returns tags and issues only." }
                  },
                  "additionalProperties": false
                }
              },
              {
                "name": "manage_tags",
                "description": "Batch tag edits without resending skill Markdown. add/remove: tags on several skills (skills and/or a source pack). rename: one tag to a new name across the library. merge: one or more tags into another (e.g. from ['3d-art'] into '3d'). delete: remove a tag from every skill. Each changed skill is saved once (History keeps the previous version) and synced. Ask the user first when a merge or delete is not clearly right.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "action": { "type": "string", "enum": ["add", "remove", "rename", "merge", "delete"] },
                    "skills": { "type": "array", "items": { "type": "string" }, "description": "add/remove: skill slugs." },
                    "source": { "type": "string", "description": "add/remove: also every skill imported from this owner/repo, or 'personal'." },
                    "tags": { "type": "array", "items": { "type": "string" }, "description": "add/remove: the tags." },
                    "tag": { "type": "string", "description": "delete: the tag. add/remove: a single tag. rename/merge: the tag to replace." },
                    "from": { "type": "array", "items": { "type": "string" }, "description": "rename/merge: the tag(s) to replace." },
                    "to": { "type": "string", "description": "rename: the new name (an existing tag makes it a merge)." },
                    "into": { "type": "string", "description": "merge: the tag to keep." }
                  },
                  "required": ["action"],
                  "additionalProperties": false
                }
              },
              {
                "name": "create_project",
                "description": "Registers a folder as a SkillHub project (idempotent: an existing project is returned as is), optionally creating the folder and choosing skills. Defaults: Claude, Codex, Gemini and Cursor folders, and Keep local on (generated folders listed in .git/info/exclude). umbrella: true makes it an umbrella folder whose direct subfolders each receive the skills. Also returns an umbrella hint when the folder sits next to sibling projects, and .git boundary warnings.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "minLength": 1, "description": "Absolute path of the project or umbrella folder." },
                    "name": { "type": "string", "description": "Display name (default: the folder name)." },
                    "skills": { "type": "array", "items": { "type": "string" } },
                    "tag": { "type": "string", "description": "Also add every library skill with this tag." },
                    "source": { "type": "string", "description": "Also add every skill imported from this owner/repo, or 'personal'." },
                    "targets": { "type": "array", "items": { "type": "string", "enum": ["claude", "codex", "gemini", "cursor", "copilot", "windsurf", "cline", "roocode"] } },
                    "keep_local": { "type": "boolean", "description": "Keep generated folders out of Git (default true)." },
                    "create_folder": { "type": "boolean", "description": "Create the folder when it does not exist yet." },
                    "umbrella": { "type": "boolean", "description": "Umbrella folder mode: write the skills into each direct subfolder (hidden, system and tooling folders such as .git, node_modules and .vs are skipped), never into this folder; new subfolders get them automatically. Use it when the subfolders are separate Git repositories, so IDE tools that stop at .git still see the skills." }
                  },
                  "required": ["path"],
                  "additionalProperties": false
                }
              },
              {
                "name": "inspect_project_skills",
                "description": "Shows which skills reach the AI tools in a folder: skills of a SkillHub project registered for this folder or inherited from a registered parent (umbrella) folder, with compact metadata {slug, description, tags, scope, via, also_global}, skill files found on disk (managed or not), the global skill names, the Git root, umbrella projects with their detected children, umbrella hints and .git boundary warnings. Use it to judge which skills are relevant or redundant without reading skill files.",
                "inputSchema": {
                  "type": "object",
                  "properties": { "path": { "type": "string", "minLength": 1, "description": "Absolute path of the folder you are working in." } },
                  "required": ["path"],
                  "additionalProperties": false
                }
              },
              {
                "name": "check_skillhub_update",
                "description": "Checks whether a newer SkillHub is available (GitHub Release, or origin/main on a developer checkout). Returns the current and latest version and commit and whether an update is available.",
                "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false }
              },
              {
                "name": "apply_skillhub_update",
                "description": "Downloads, verifies and installs the newer SkillHub found by check_skillhub_update, then restarts SkillHub. SkillHub's MCP server disconnects while it restarts. Only call after the user agreed.",
                "inputSchema": { "type": "object", "properties": { "confirm": { "type": "boolean", "description": "Must be true: the user agreed to install the update now." } }, "required": ["confirm"], "additionalProperties": false }
              },
              {
                "name": "set_skillhub_preferences",
                "description": "Changes SkillHub settings from chat. continuous_learning: when on, AI tools are told (in the bootstrap skill and a short note in synced personal skills) to offer a skill improvement only after solving something non-trivial. Omit a field to leave it unchanged; the current values are returned.",
                "inputSchema": { "type": "object", "properties": { "continuous_learning": { "type": "boolean" } }, "additionalProperties": false }
              },
              {
                "name": "propose_skill_improvement",
                "description": "Saves a suggested improvement to a skill for later review (when the user says 'save that for later'). Provide a one-line summary and the changed fields. Apply later with apply_skill_proposal.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "skill": { "type": "string" },
                    "summary": { "type": "string", "description": "One line: what the improvement is and why." },
                    "instructions_markdown": { "type": "string", "description": "The full new instructions (replaces the current body)." },
                    "description": { "type": "string" },
                    "when_to_use": { "type": "string" },
                    "files": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Companion files to add or replace, keyed by relative path." }
                  },
                  "required": ["skill", "summary"],
                  "additionalProperties": false
                }
              },
              {
                "name": "list_skill_proposals",
                "description": "Lists saved skill improvement proposals, optionally for one skill.",
                "inputSchema": { "type": "object", "properties": { "skill": { "type": "string" } }, "additionalProperties": false }
              },
              {
                "name": "apply_skill_proposal",
                "description": "Applies a saved proposal to its skill (the previous version stays in History) and syncs it, or dismisses it with dismiss: true.",
                "inputSchema": { "type": "object", "properties": { "id": { "type": "string" }, "dismiss": { "type": "boolean" } }, "required": ["id"], "additionalProperties": false }
              },
              {
                "name": "get_sync_status",
                "description": "Returns the current status of SkillHub, including installed AI providers, skill directory paths, synced skill counts, and recent synchronization events.",
                "inputSchema": {
                  "type": "object",
                  "properties": {},
                  "additionalProperties": false
                }
              }
            ]
            """;

        return JsonNode.Parse(schemaJson)!.AsArray();
    }
}
