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
        _discovery = discovery ?? syncEngine.Discovery;
    }

    /// <summary>
    /// Processes a single JSON-RPC 2.0 request or notification line.
    /// Returns null for JSON-RPC notifications (messages without an "id" property).
    /// </summary>
    public Task<string?> ProcessMessageAsync(string rawJsonMessage, CancellationToken ct = default) =>
        ProcessMessageAsync(rawJsonMessage, null, ct);

    internal async Task<string?> ProcessMessageAsync(string rawJsonMessage, McpSession? session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawJsonMessage))
        {
            return null;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(rawJsonMessage);
            if (rootNode is JsonObject obj) _ = obj.Count;
        }
        catch (JsonException)
        {
            return SerializeError(null, -32700, "Invalid JSON.");
        }
        catch (ArgumentException)
        {
            return SerializeError(null, -32600, "JSON-RPC request keys must be unique.");
        }

        if (rootNode is not JsonObject requestObj)
        {
            return SerializeError(null, -32600, "Invalid Request: expected JSON object.");
        }

        var hasId = requestObj.TryGetPropertyValue("id", out var idNode);
        if (requestObj["jsonrpc"] is not JsonValue version || !version.TryGetValue<string>(out var protocol) || protocol != "2.0" ||
            requestObj["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method) ||
            string.IsNullOrWhiteSpace(method) || hasId && (idNode is null ||
            idNode.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number)))
        {
            return SerializeError(null, -32600, "Invalid JSON-RPC 2.0 request.");
        }

        var paramsObj = requestObj["params"] as JsonObject;
        if (requestObj.TryGetPropertyValue("params", out _) && paramsObj is null)
        {
            return hasId ? SerializeError(idNode, -32602, "MCP parameters must be a JSON object.") : null;
        }
        if (!hasId)
        {
            if (method == "notifications/initialized") session?.MarkInitialized();
            if (method == "notifications/cancelled") session?.Cancel(paramsObj?["requestId"]);
            return null;
        }
        if (method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return SerializeError(idNode, -32600, "Notifications cannot have an ID.");
        }
        if (session is not null && !session.IsReady && method is not ("initialize" or "ping"))
        {
            return SerializeError(idNode, -32002, "Complete MCP initialization before calling this method.");
        }
        var responseId = idNode!;

        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (session is not null && !session.Track(responseId, request))
        {
            return SerializeError(responseId, -32600, "This request ID is already in use.");
        }
        try
        {
            return method switch
            {
                "initialize" => HandleInitialize(responseId, paramsObj, session),
                "ping" => SerializeResult(responseId, new JsonObject()),
                "tools/list" => HandleToolsList(responseId),
                "tools/call" => await HandleToolsCallAsync(responseId, paramsObj, session?.Version, request.Token).ConfigureAwait(false),
                "resources/list" => SerializeResult(responseId, new JsonObject { ["resources"] = new JsonArray() }),
                "resources/templates/list" => SerializeResult(responseId, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
                "prompts/list" => SerializeResult(responseId, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => SerializeError(responseId, -32601, $"Method not found: '{method}'")
            };
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            return SerializeError(responseId, -32603, "The request could not be processed.");
        }
        finally
        {
            session?.Complete(responseId);
        }
    }

    internal static bool IsCancellationNotification(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("id", out _) &&
                root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String &&
                method.GetString() == "notifications/cancelled";
        }
        catch (JsonException) { return false; }
    }

    public static bool IsSupportedVersion(string version) => version is "2024-11-05" or "2025-06-18";

    private static string HandleInitialize(JsonNode idNode, JsonObject? paramsObj, McpSession? session)
    {
        if (paramsObj?["protocolVersion"] is not JsonValue proposed || !proposed.TryGetValue<string>(out var clientProtocolVersion)
            || string.IsNullOrWhiteSpace(clientProtocolVersion))
        {
            return SerializeError(idNode, -32602, "A protocolVersion string is required.");
        }
        var negotiatedVersion = session?.TransportVersion ?? (IsSupportedVersion(clientProtocolVersion)
            ? clientProtocolVersion : "2025-06-18");

        if (session is not null && !session.TryInitialize(negotiatedVersion))
        {
            return SerializeError(idNode, -32600, "This MCP session is already initialized.");
        }
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
                ["version"] = ProductVersion.Current
            },
            ["instructions"] = "Use SkillHub MCP tools (list_skills, get_skill, create_skill, update_skill, delete_skill, manage_skill_tags, toggle_skill_provider, search_community_skills, prepare_skill_import, apply_skill_import, get_sync_status) as the Single Source of Truth whenever managing AI skills across Claude, Gemini/Antigravity, OpenAI Codex, and Cursor."
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

    private async Task<string> HandleToolsCallAsync(JsonNode idNode, JsonObject? paramsObj, string? version, CancellationToken ct)
    {
        if (paramsObj?["name"] is not JsonValue tool || !tool.TryGetValue<string>(out var toolName) ||
            string.IsNullOrWhiteSpace(toolName))
        {
            return SerializeError(idNode, -32602, "A tool name string is required.");
        }
        if (paramsObj.TryGetPropertyValue("arguments", out var supplied) && supplied is not JsonObject)
        {
            return SerializeError(idNode, -32602, "Tool arguments must be an object.");
        }
        var arguments = supplied as JsonObject ?? new JsonObject();
        if (McpToolDefinitions.ValidateArguments(toolName, arguments) is { } validationError)
        {
            return SerializeError(idNode, -32602, validationError);
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
                "prepare_skill_import" => await ExecutePrepareImportAsync(arguments, ct).ConfigureAwait(false),
                "apply_skill_import" => await ExecuteApplyImportAsync(arguments, ct).ConfigureAwait(false),
                "import_community_skill" => await ExecuteImportCommunitySkillAsync(arguments, ct).ConfigureAwait(false),
                "activate_catalog_skill" => await ExecuteCatalogCommandAsync(arguments, true, ct).ConfigureAwait(false),
                "clone_catalog_skill" => await ExecuteCatalogCommandAsync(arguments, false, ct).ConfigureAwait(false),
                "restore_skill" => await ExecuteRestoreAsync(arguments, ct).ConfigureAwait(false),
                "get_sync_status" => ExecuteGetSyncStatus(),
                _ => throw new ArgumentException($"Unknown tool '{toolName}'.")
            };

            return SerializeToolCallSuccess(idNode, payload, version != "2024-11-05");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or KeyNotFoundException or FormatException)
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
                destinations = s.Destinations
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
            supporting_files = includeFiles ? skill.SupportingFiles : null,
            revision = skill.Revision,
            catalog_id = skill.CatalogId,
            destinations = skill.Destinations
        };

        return JsonSerializer.Serialize(response, IndentedJson);
    }

    private async Task<string> ExecuteCreateSkillAsync(JsonObject args, CancellationToken ct)
    {
        var rawName = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var slug = rawName;
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
            Tags = ReadStringList(args["tags"]).Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
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
                var relPath = item["relative_path"]?.GetValue<string>();
                var content = item["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(relPath) && content is not null)
                {
                    var path = SkillPaths.SupportingPath(relPath);
                    if (!model.SupportingFiles.TryAdd(path, content))
                    {
                        throw new ArgumentException($"Duplicate supporting file '{path}'.");
                    }
                }
            }
        }

        var created = await _syncEngine.CreateSkillAsync(model, "mcp:create_skill", ct).ConfigureAwait(false);
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
        var slug = rawName;

        var saved = await _syncEngine.EditSkillAsync(slug, updated =>
        {
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
                updated.Tags = ReadStringList(tagsNode).Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (args.TryGetPropertyValue("allowed_tools", out var toolsNode) && toolsNode is not null)
            {
                updated.Targets.Claude.AllowedTools = ReadStringList(toolsNode);
            }

            if (args.TryGetPropertyValue("cursor_globs", out var globsNode) && globsNode is not null)
            {
                var globs = ReadStringList(globsNode);
                updated.Targets.Cursor.Globs = globs;
            }

            if (args.TryGetPropertyValue("cursor_always_apply", out var alwaysNode) && alwaysNode is not null)
            {
                updated.Targets.Cursor.AlwaysApply = alwaysNode.GetValue<bool>();
            }

            if (args["upsert_supporting_files"] is JsonArray upsertArr)
            {
                foreach (var item in upsertArr.OfType<JsonObject>())
                {
                    var relPath = item["relative_path"]?.GetValue<string>();
                    var content = item["content"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(relPath) && content is not null)
                    {
                        updated.SupportingFiles[SkillPaths.SupportingPath(relPath)] = content;
                    }
                }
            }

            if (args["delete_supporting_files"] is JsonArray deleteArr)
            {
                foreach (var path in ReadStringList(deleteArr))
                {
                    var cleanRel = SkillPaths.SupportingPath(path);
                    updated.SupportingFiles.Remove(cleanRel);
                }
            }
        }, "mcp:update_skill", ct, args["revision"]?.GetValue<long>()).ConfigureAwait(false);
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
        var slug = rawName;

        var deleted = await _syncEngine.DeleteSkillDirectAsync(slug, "mcp:delete_skill", ct).ConfigureAwait(false);
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
                return JsonSerializer.Serialize(new { action, tags = _syncEngine.ListTags() }, StructuredJson);
            }

            case "create":
            {
                var tag = await _syncEngine.CreateTagAsync(args["tag"]?.GetValue<string>()
                    ?? throw new ArgumentException("tag is required."), ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { action, tag }, StructuredJson);
            }

            case "undo":
            {
                if (!Guid.TryParse(args["undo_id"]?.GetValue<string>(), out var id))
                {
                    throw new ArgumentException("undo_id is required.");
                }
                var updated = await _syncEngine.UndoTagChangeAsync(id, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { action, updated_skills = updated }, StructuredJson);
            }

            case "add":
            case "remove":
            {
                var skillName = args["skill_name"]?.GetValue<string>()
                    ?? args["name"]?.GetValue<string>()
                    ?? throw new ArgumentException($"Parameter 'skill_name' is required for action '{action}'.");
                var rawTags = ReadStringList(args["tags"]).Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (args["tag"]?.GetValue<string>() is { } singleTagVal && !string.IsNullOrWhiteSpace(singleTagVal))
                {
                    rawTags.Add(singleTagVal);
                }
                var tags = rawTags
                    .Select(SyncEngine.NormalizeTag)
                    .ToList();

                var saved = await _syncEngine.EditSkillAsync(skillName, updated =>
                {
                    updated.Tags = action == "add"
                        ? updated.Tags.Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : updated.Tags.Except(tags, StringComparer.OrdinalIgnoreCase).ToList();
                }, $"mcp:tags_{action}", ct).ConfigureAwait(false);
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

                var change = await _syncEngine.ChangeTagAsync(oldTag, newTag, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    status = "renamed",
                    old_tag = oldTag,
                    new_tag = SyncEngine.NormalizeTag(newTag),
                    updated_skills = change.UpdatedSkills,
                    undo_id = change.UndoId
                }, IndentedJson);
            }

            case "delete":
            {
                var tags = ReadStringList(args["tags"]);
                if (tags.Count == 0 && args["old_tag"]?.GetValue<string>() is { } singleTag)
                {
                    tags.Add(singleTag);
                }

                if (tags.Count == 0) throw new ArgumentException("Supply at least one tag.");
                tags = tags.Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var totalUpdated = 0;
                Guid? undoId = null;
                foreach (var tag in tags)
                {
                    var change = await _syncEngine.ChangeTagAsync(tag, null, ct).ConfigureAwait(false);
                    totalUpdated += change.UpdatedSkills;
                    undoId = change.UndoId ?? undoId;
                }

                return JsonSerializer.Serialize(new
                {
                    status = "deleted",
                    deleted_tags = tags,
                    updated_skills = totalUpdated,
                    undo_id = tags.Count == 1 ? undoId : null
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
            !Enum.IsDefined(provider) || provider == ProviderType.Canonical)
        {
            throw new ArgumentException($"Invalid provider '{providerStr}'. Expected claude, gemini, codex, or cursor.");
        }

        var saved = await _syncEngine.SetProviderAsync(name, provider, enabled, "mcp:toggle_skill_provider", ct).ConfigureAwait(false);
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

    private static readonly JsonSerializerOptions StructuredJson = new(CompactJson)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private async Task<string> ExecutePrepareImportAsync(JsonObject args, CancellationToken ct)
    {
        var selection = args.Deserialize<SkillImportSelection>(StructuredJson)!;
        var plan = await _importer.PrepareImportAsync(selection, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(plan, StructuredJson);
    }

    private async Task<string> ExecuteApplyImportAsync(JsonObject args, CancellationToken ct)
    {
        if (!Guid.TryParse(args["plan_id"]?.GetValue<string>(), out var id))
        {
            throw new ArgumentException("Invalid plan_id.");
        }
        var request = new SkillImportApplyRequest(args["revision"]!.GetValue<string>(), args["name"]?.GetValue<string>(),
            ReadStringList(args["providers"]));
        var model = await _importer.ApplyImportAsync(id, request, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { model.Name, model.Revision, model.Destinations }, StructuredJson);
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
        var syncedCount = _syncEngine.ListSkills(category: "synced").Count;
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
            recent_activity = _syncEngine.GetRecentActivity().Reverse().Take(10)
        }, IndentedJson);
    }

    private async Task<string> ExecuteCatalogCommandAsync(JsonObject args, bool activate, CancellationToken ct)
    {
        var id = args["catalog_id"]?.GetValue<string>() ?? throw new ArgumentException("catalog_id is required.");
        var skill = await _syncEngine.MaterializeCatalogSkillAsync(id, null, activate, "mcp:catalog", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { skill.Name, skill.Revision, skill.Destinations }, CompactJson);
    }

    private async Task<string> ExecuteRestoreAsync(JsonObject args, CancellationToken ct)
    {
        var name = args["name"]?.GetValue<string>() ?? throw new ArgumentException("name is required.");
        var skill = await _syncEngine.RestoreSkillAsync(name, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { skill.Name, skill.Revision, skill.Destinations }, CompactJson);
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

    private static string SerializeToolCallSuccess(JsonNode idNode, string textPayload, bool includeStructured)
    {
        var structured = JsonNode.Parse(textPayload);
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = structured?.ToJsonString(CompactJson) ?? textPayload
                }
            },
            ["isError"] = false
        };

        if (includeStructured && structured is JsonObject) result["structuredContent"] = structured;
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
