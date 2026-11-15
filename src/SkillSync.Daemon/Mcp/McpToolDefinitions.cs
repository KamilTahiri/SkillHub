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
                "description": "Creates a new canonical AI skill in SkillHub and automatically transpiles and syncs it to all installed AI providers (Claude, Gemini/Antigravity, OpenAI Codex, Cursor). Always use this tool instead of writing directly to ~/.claude/skills, ~/.gemini/skills, ~/.codex/skills, ~/.agents/skills, or .cursor/rules.",
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
                      "description": "Clear explanation of what the skill does and the capabilities it provides."
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
                      "description": "Updated description (omit to keep existing)."
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
                "description": "Imports an open-source skill from GitHub or skills.sh (e.g., 'vercel-labs/agent-skills/skills/react-best-practices' or 'anthropics/skills/skills/frontend-design') into SkillHub and syncs it across all enabled AI providers.",
                "inputSchema": {
                  "type": "object",
                  "properties": {
                    "source": {
                      "type": "string",
                      "description": "GitHub URL, owner/repo/path shorthand, or skills.sh command."
                    }
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
