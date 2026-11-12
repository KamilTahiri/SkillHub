using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Mcp;

public sealed class McpJsonRpcHandler
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly SyncEngine _syncEngine;
    private readonly SkillImporterService _importer;
    private readonly ProviderDiscoveryService _discovery;

    public McpJsonRpcHandler(
        SyncEngine syncEngine,
        SkillImporterService? importer = null,
        ProviderDiscoveryService? discovery = null)
    {
        _syncEngine = syncEngine;
        _importer = importer ?? new SkillImporterService(syncEngine);
        _discovery = discovery ?? new ProviderDiscoveryService();
    }

    /// <summary>
    /// Processes a single JSON-RPC 2.0 request or notification line.
    /// Returns null for JSON-RPC notifications (messages without an "id" property).
    /// </summary>
    public async Task<string?> ProcessMessageAsync(string rawJsonMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawJsonMessage))
        {
            return null;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(rawJsonMessage);
        }
        catch (Exception ex)
        {
            return SerializeError(null, -32700, $"Parse error: {ex.Message}");
        }

        if (rootNode is not JsonObject requestObj)
        {
            return SerializeError(null, -32600, "Invalid Request: expected JSON object.");
        }

        var hasId = requestObj.TryGetPropertyValue("id", out var idNode);
        var method = requestObj["method"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(method))
        {
            return hasId ? SerializeError(idNode, -32600, "Invalid Request: missing 'method'.") : null;
        }

        // Notifications (no "id") do not receive a response in JSON-RPC 2.0
        if (!hasId || idNode is null)
        {
            return null;
        }

        var paramsObj = requestObj["params"] as JsonObject;

        try
        {
            return method switch
            {
                "initialize" => HandleInitialize(idNode, paramsObj),
                "ping" => SerializeResult(idNode, new JsonObject()),
                "tools/list" => HandleToolsList(idNode),
                "tools/call" => await HandleToolsCallAsync(idNode, paramsObj, ct).ConfigureAwait(false),
                "resources/list" => SerializeResult(idNode, new JsonObject { ["resources"] = new JsonArray() }),
                "resources/templates/list" => SerializeResult(idNode, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
                "prompts/list" => SerializeResult(idNode, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => SerializeError(idNode, -32601, $"Method not found: '{method}'")
            };
        }
        catch (Exception ex)
        {
            return SerializeError(idNode, -32603, $"Internal error: {ex.Message}");
        }
    }

    private static string HandleInitialize(JsonNode idNode, JsonObject? paramsObj)
    {
        var clientProtocolVersion = paramsObj?["protocolVersion"]?.GetValue<string>();
        var negotiatedVersion = string.IsNullOrWhiteSpace(clientProtocolVersion)
            ? "2024-11-05"
            : clientProtocolVersion;

        var result = new JsonObject
        {
            ["protocolVersion"] = negotiatedVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false
                }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "skillhub",
                ["version"] = "1.0.0"
            },
            ["instructions"] = "Use SkillHub MCP tools (list_skills, get_skill, create_skill, update_skill, delete_skill, manage_skill_tags, toggle_skill_provider, search_community_skills, import_community_skill, get_sync_status) as the Single Source of Truth whenever managing AI skills across Claude, Gemini/Antigravity, OpenAI Codex, and Cursor."
        };

        return SerializeResult(idNode, result);
    }

    private static string HandleToolsList(JsonNode idNode)
    {
        var result = new JsonObject
        {
            ["tools"] = McpToolDefinitions.GetToolsJsonArray()
        };

        return SerializeResult(idNode, result);
    }

    private async Task<string> HandleToolsCallAsync(JsonNode idNode, JsonObject? paramsObj, CancellationToken ct)
    {
        var toolName = paramsObj?["name"]?.GetValue<string>();
        var arguments = paramsObj?["arguments"] as JsonObject ?? new JsonObject();

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return SerializeError(idNode, -32602, "Missing tool 'name' in tools/call.");
        }

        try
        {
            var payload = toolName switch
            {
                "list_skills" => ExecuteListSkills(arguments),
                "get_skill" => ExecuteGetSkill(arguments),
                "create_skill" => await ExecuteCreateSkillAsync(arguments, ct).ConfigureAwait(false),
                "update_skill" => await ExecuteUpdateSkillAsync(arguments, ct).ConfigureAwait(false),
                "delete_skill" => await ExecuteDeleteSkillAsync(arguments, ct).ConfigureAwait(false),
                "manage_skill_tags" => await ExecuteManageSkillTagsAsync(arguments, ct).ConfigureAwait(false),
                "toggle_skill_provider" => await ExecuteToggleSkillProviderAsync(arguments, ct).ConfigureAwait(false),
                "search_community_skills" => await ExecuteSearchCommunitySkillsAsync(arguments, ct).ConfigureAwait(false),
                "import_community_skill" => await ExecuteImportCommunitySkillAsync(arguments, ct).ConfigureAwait(false),
                "get_sync_status" => ExecuteGetSyncStatus(),
                _ => throw new ArgumentException($"Unknown tool '{toolName}'.")
            };

            return SerializeToolCallSuccess(idNode, payload);
        }
        catch (Exception ex)
        {
            return SerializeToolCallError(idNode, ex.Message);
        }
    }

    private string ExecuteListSkills(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>();
        var tag = args["tag"]?.GetValue<string>();
        var category = args["category"]?.GetValue<string>();
        var includeDisabled = args["include_disabled"]?.GetValue<bool>() ?? true;

        var skills = _syncEngine.ListSkills(query, tag, includeDisabled, category)
            .Select(s => new
            {
                name = s.Name,
                title = s.GetEffectiveTitle(),
                version = s.Version,
                source_category = s.SourceCategory,
                origin_provider = s.OriginProvider,
                is_catalog_only = s.IsCatalogOnly,
                description = s.Description,
                when_to_use = s.WhenToUse,
                summary_short = s.SummaryShort,
                tags = s.Tags,
                enabled_providers = new
                {
                    claude = s.Targets.Claude.Enabled,
                    gemini = s.Targets.Gemini.Enabled,
                    codex = s.Targets.Codex.Enabled,
                    cursor = s.Targets.Cursor.Enabled
                },
                supporting_file_count = s.SupportingFiles.Count
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            count = skills.Count,
            skills
        }, IndentedJson);
    }

    private string ExecuteGetSkill(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var includeFiles = args["include_supporting_files"]?.GetValue<bool>() ?? false;

        var skill = _syncEngine.GetSkill(name)
            ?? throw new KeyNotFoundException($"Skill '{name}' was not found in SkillHub.");

        var response = new
        {
            name = skill.Name,
            title = skill.GetEffectiveTitle(),
            version = skill.Version,
            source_category = skill.SourceCategory,
            origin_provider = skill.OriginProvider,
            is_catalog_only = skill.IsCatalogOnly,
            description = skill.Description,
            when_to_use = skill.WhenToUse,
            summary_short = skill.SummaryShort,
            tags = skill.Tags,
            targets = skill.Targets,
            instructions_markdown = skill.InstructionsMarkdown,
            supporting_file_paths = skill.SupportingFiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            supporting_files = includeFiles ? skill.SupportingFiles : null
        };

        return JsonSerializer.Serialize(response, IndentedJson);
    }

    private async Task<string> ExecuteCreateSkillAsync(JsonObject args, CancellationToken ct)
    {
        var rawName = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var slug = ProviderDiscoveryService.NormalizeSlug(rawName);
        if (string.IsNullOrEmpty(slug))
        {
            throw new ArgumentException($"Invalid skill slug '{rawName}'.");
        }

        var description = args["description"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'description' is required.");
        var instructions = args["instructions_markdown"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'instructions_markdown' is required.");

        var model = new CanonicalSkillModel
        {
            Name = slug,
            Title = args["title"]?.GetValue<string>()?.Trim() ?? CanonicalSkillModel.HumanizeSlug(slug),
            Version = "1.0.0",
            SourceCategory = "user",
            OriginProvider = "My Skill",
            Description = description.Trim(),
            WhenToUse = args["when_to_use"]?.GetValue<string>()?.Trim() ?? string.Empty,
            SummaryShort = args["summary_short"]?.GetValue<string>()?.Trim() ?? string.Empty,
            InstructionsMarkdown = instructions.Trim(),
            Tags = ReadStringList(args["tags"])
        };

        var allowedTools = ReadStringList(args["allowed_tools"]);
        if (allowedTools.Count > 0)
        {
            model.Targets.Claude.AllowedTools = allowedTools;
        }

        var cursorGlobs = ReadStringList(args["cursor_globs"]);
        if (cursorGlobs.Count > 0)
        {
            model.Targets.Cursor.Globs = cursorGlobs;
        }

        if (args["cursor_always_apply"] is not null)
        {
            model.Targets.Cursor.AlwaysApply = args["cursor_always_apply"]!.GetValue<bool>();
        }

        if (args["supporting_files"] is JsonArray filesArr)
        {
            foreach (var item in filesArr.OfType<JsonObject>())
            {
                var relPath = item["relative_path"]?.GetValue<string>()?.Replace('\\', '/').TrimStart('/');
                var content = item["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(relPath) && content is not null)
                {
                    ValidateSupportingFilePath(relPath);
                    model.SupportingFiles[relPath] = content;
                }
            }
        }

        var created = await _syncEngine.UpsertSkillDirectAsync(model, "mcp:create_skill", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "created",
            name = created.Name,
            message = $"Skill '{created.Name}' created in canonical store and synced to all enabled AI providers."
        }, IndentedJson);
    }

    private async Task<string> ExecuteUpdateSkillAsync(JsonObject args, CancellationToken ct)
    {
        var rawName = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var slug = ProviderDiscoveryService.NormalizeSlug(rawName);

        var existing = _syncEngine.GetSkill(slug)
            ?? throw new KeyNotFoundException($"Skill '{slug}' does not exist. Use 'create_skill' to create it first.");

        var updated = existing.Clone();

        if (args.TryGetPropertyValue("title", out var titleNode) && titleNode is not null)
        {
            updated.Title = titleNode.GetValue<string>().Trim();
        }

        if (args.TryGetPropertyValue("description", out var descNode) && descNode is not null)
        {
            updated.Description = descNode.GetValue<string>().Trim();
        }

        if (args.TryGetPropertyValue("when_to_use", out var whenNode) && whenNode is not null)
        {
            updated.WhenToUse = whenNode.GetValue<string>().Trim();
        }

        if (args.TryGetPropertyValue("summary_short", out var shortNode) && shortNode is not null)
        {
            updated.SummaryShort = shortNode.GetValue<string>().Trim();
        }

        if (args.TryGetPropertyValue("instructions_markdown", out var bodyNode) && bodyNode is not null)
        {
            updated.InstructionsMarkdown = bodyNode.GetValue<string>().Trim();
        }

        if (args.TryGetPropertyValue("tags", out var tagsNode) && tagsNode is not null)
        {
            updated.Tags = ReadStringList(tagsNode);
        }

        if (args.TryGetPropertyValue("allowed_tools", out var toolsNode) && toolsNode is not null)
        {
            updated.Targets.Claude.AllowedTools = ReadStringList(toolsNode);
        }

        if (args.TryGetPropertyValue("cursor_globs", out var globsNode) && globsNode is not null)
        {
            var globs = ReadStringList(globsNode);
            updated.Targets.Cursor.Globs = globs.Count > 0 ? globs : ["**/*"];
        }

        if (args.TryGetPropertyValue("cursor_always_apply", out var alwaysNode) && alwaysNode is not null)
        {
            updated.Targets.Cursor.AlwaysApply = alwaysNode.GetValue<bool>();
        }

        if (args["upsert_supporting_files"] is JsonArray upsertArr)
        {
            foreach (var item in upsertArr.OfType<JsonObject>())
            {
                var relPath = item["relative_path"]?.GetValue<string>()?.Replace('\\', '/').TrimStart('/');
                var content = item["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(relPath) && content is not null)
                {
                    ValidateSupportingFilePath(relPath);
                    updated.SupportingFiles[relPath] = content;
                }
            }
        }

        if (args["delete_supporting_files"] is JsonArray deleteArr)
        {
            foreach (var path in ReadStringList(deleteArr))
            {
                var cleanRel = path.Replace('\\', '/').TrimStart('/');
                updated.SupportingFiles.Remove(cleanRel);
            }
        }

        var saved = await _syncEngine.UpsertSkillDirectAsync(updated, "mcp:update_skill", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "updated",
            name = saved.Name,
            message = $"Skill '{saved.Name}' updated non-destructively and synced across providers."
        }, IndentedJson);
    }

    private async Task<string> ExecuteDeleteSkillAsync(JsonObject args, CancellationToken ct)
    {
        var rawName = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var slug = ProviderDiscoveryService.NormalizeSlug(rawName);

        var deleted = await _syncEngine.DeleteSkillDirectAsync(slug, "mcp:delete_skill", allowBootstrapDelete: false, ct).ConfigureAwait(false);
        if (!deleted)
        {
            throw new KeyNotFoundException($"Skill '{slug}' was not found.");
        }

        return JsonSerializer.Serialize(new
        {
            status = "deleted",
            name = slug,
            message = $"Skill '{slug}' removed from canonical store and all AI providers."
        }, IndentedJson);
    }

    private async Task<string> ExecuteManageSkillTagsAsync(JsonObject args, CancellationToken ct)
    {
        var action = (args["action"]?.GetValue<string>() ?? "list").Trim().ToLowerInvariant();
        switch (action)
        {
            case "list":
            {
                var allSkills = _syncEngine.ListSkills();
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in allSkills.Where(s => !string.Equals(s.SourceCategory, "system", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var t in s.Tags)
                    {
                        var lower = t.ToLowerInvariant();
                        if (lower is "system" or "skillhub" or "meta-skill") continue;
                        counts[lower] = counts.GetValueOrDefault(lower) + 1;
                    }
                }

                var tagsList = counts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new { tag = kv.Key, skill_count = kv.Value })
                    .ToList();

                return JsonSerializer.Serialize(new { action = "list", tags = tagsList }, IndentedJson);
            }

            case "add":
            case "remove":
            {
                var skillName = args["skill_name"]?.GetValue<string>()
                    ?? args["name"]?.GetValue<string>()
                    ?? throw new ArgumentException($"Parameter 'skill_name' is required for action '{action}'.");
                var rawTags = ReadStringList(args["tags"]);
                if (args["tag"]?.GetValue<string>() is { } singleTagVal && !string.IsNullOrWhiteSpace(singleTagVal))
                {
                    rawTags.Add(singleTagVal);
                }
                var tags = rawTags
                    .Select(ProviderDiscoveryService.NormalizeSlug)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                var existing = _syncEngine.GetSkill(skillName)
                    ?? throw new KeyNotFoundException($"Skill '{skillName}' was not found.");

                var updated = existing.Clone();
                if (action == "add")
                {
                    updated.Tags = updated.Tags.Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                else
                {
                    var removeSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
                    updated.Tags = updated.Tags.Where(t => !removeSet.Contains(t)).ToList();
                }

                var saved = await _syncEngine.UpsertSkillDirectAsync(updated, $"mcp:tags_{action}", ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    action,
                    skill_name = saved.Name,
                    tags = saved.Tags
                }, IndentedJson);
            }

            case "rename":
            {
                var oldTag = args["old_tag"]?.GetValue<string>()
                    ?? throw new ArgumentException("Parameter 'old_tag' is required for action 'rename'.");
                var newTag = args["new_tag"]?.GetValue<string>()
                    ?? throw new ArgumentException("Parameter 'new_tag' is required for action 'rename'.");

                var updatedCount = await _syncEngine.RenameTagAcrossSkillsAsync(oldTag, newTag, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    status = "renamed",
                    old_tag = oldTag,
                    new_tag = ProviderDiscoveryService.NormalizeSlug(newTag),
                    updated_skills = updatedCount
                }, IndentedJson);
            }

            case "delete":
            {
                var tags = ReadStringList(args["tags"]);
                if (tags.Count == 0 && args["old_tag"]?.GetValue<string>() is { } singleTag)
                {
                    tags.Add(singleTag);
                }

                var totalUpdated = 0;
                foreach (var t in tags)
                {
                    totalUpdated += await _syncEngine.DeleteTagAcrossSkillsAsync(t, ct).ConfigureAwait(false);
                }

                return JsonSerializer.Serialize(new
                {
                    status = "deleted",
                    deleted_tags = tags,
                    updated_skills = totalUpdated
                }, IndentedJson);
            }

            default:
                throw new ArgumentException($"Unsupported tag action '{action}'.");
        }
    }

    private async Task<string> ExecuteToggleSkillProviderAsync(JsonObject args, CancellationToken ct)
    {
        var name = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var providerStr = args["provider"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'provider' is required.");
        var enabled = args["enabled"]?.GetValue<bool>()
            ?? throw new ArgumentException("Parameter 'enabled' is required.");

        if (!Enum.TryParse<ProviderType>(providerStr, ignoreCase: true, out var provider) ||
            provider == ProviderType.Canonical)
        {
            throw new ArgumentException($"Invalid provider '{providerStr}'. Expected claude, gemini, codex, or cursor.");
        }

        var existing = _syncEngine.GetSkill(name)
            ?? throw new KeyNotFoundException($"Skill '{name}' was not found.");

        var updated = existing.Clone();
        updated.SetEnabledFor(provider, enabled);

        var saved = await _syncEngine.UpsertSkillDirectAsync(updated, "mcp:toggle_skill_provider", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "updated",
            name = saved.Name,
            provider = provider.ToString().ToLowerInvariant(),
            enabled,
            source_category = saved.SourceCategory,
            enabled_providers = new
            {
                claude = saved.Targets.Claude.Enabled,
                gemini = saved.Targets.Gemini.Enabled,
                codex = saved.Targets.Codex.Enabled,
                cursor = saved.Targets.Cursor.Enabled
            }
        }, IndentedJson);
    }

    private async Task<string> ExecuteSearchCommunitySkillsAsync(JsonObject args, CancellationToken ct)
    {
        var query = args["query"]?.GetValue<string>();
        var repo = args["repo"]?.GetValue<string>();

        IReadOnlyList<CommunitySkillSummary> results;
        if (!string.IsNullOrWhiteSpace(repo))
        {
            results = await _importer.BrowseRepositorySkillsAsync(repo, query, ct).ConfigureAwait(false);
        }
        else
        {
            results = await _importer.SearchCommunitySkillsAsync(query, ct).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new
        {
            count = results.Count,
            curated_repositories = SkillImporterService.GetCuratedRepositories(),
            skills = results
        }, IndentedJson);
    }

    private async Task<string> ExecuteImportCommunitySkillAsync(JsonObject args, CancellationToken ct)
    {
        var source = args["source"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'source' is required.");

        var res = await _importer.ImportAsync(new SkillImportRequest(Source: source, RawMarkdown: null, FileName: null), ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "imported",
            imported_count = res.ImportedCount,
            skills = res.Skills.Select(s => new { name = s.Name, title = s.GetEffectiveTitle(), origin = s.OriginProvider }),
            message = res.Message
        }, IndentedJson);
    }

    private string ExecuteGetSyncStatus()
    {
        var providers = _discovery.DiscoverProviders()
            .Where(p => p.Provider != ProviderType.Canonical)
            .Select(p => new
            {
                provider = p.Provider.ToString().ToLowerInvariant(),
                display_name = p.DisplayName,
                is_installed = p.IsInstalled,
                skills_directory = p.SkillsRootDirectory
            })
            .ToList();

        var allSkills = _syncEngine.ListSkills();
        var userCount = allSkills.Count(s => string.Equals(s.SourceCategory, "user", StringComparison.OrdinalIgnoreCase));
        var syncedCount = allSkills.Count(s => !s.IsCatalogOnly && !string.Equals(s.SourceCategory, "system", StringComparison.OrdinalIgnoreCase));
        var templateCount = allSkills.Count(s => string.Equals(s.SourceCategory, "template", StringComparison.OrdinalIgnoreCase));

        return JsonSerializer.Serialize(new
        {
            canonical_skills_root = _discovery.CanonicalSkillsRoot,
            counts = new
            {
                my_skills = userCount,
                active_and_synced = syncedCount,
                templates = templateCount,
                total_discovered = allSkills.Count
            },
            providers,
            recent_activity = _syncEngine.GetRecentActivity().Take(10)
        }, IndentedJson);
    }

    private static void ValidateSupportingFilePath(string relativePath)
    {
        if (relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid supporting file path '{relativePath}': path traversal is forbidden.");
        }

        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ProviderDiscoveryService.IsAllowedSupportingFolder(parts[0]))
        {
            throw new ArgumentException($"Supporting file '{relativePath}' must reside under scripts/, resources/, references/, or examples/.");
        }
    }

    private static List<string> ReadStringList(JsonNode? node)
    {
        if (node is not JsonArray arr)
        {
            return [];
        }

        return arr
            .Select(x => x?.GetValue<string>()?.Trim() ?? string.Empty)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();
    }

    private static string SerializeToolCallSuccess(JsonNode idNode, string textPayload)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = textPayload
                }
            },
            ["isError"] = false
        };

        return SerializeResult(idNode, result);
    }

    private static string SerializeToolCallError(JsonNode idNode, string errorMessage)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Error: {errorMessage}"
                }
            },
            ["isError"] = true
        };

        return SerializeResult(idNode, result);
    }

    private static string SerializeResult(JsonNode idNode, JsonObject resultObj)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode.DeepClone(),
            ["result"] = resultObj
        };

        return envelope.ToJsonString(CompactJson);
    }

    private static string SerializeError(JsonNode? idNode, int code, string message)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        return envelope.ToJsonString(CompactJson);
    }
}
