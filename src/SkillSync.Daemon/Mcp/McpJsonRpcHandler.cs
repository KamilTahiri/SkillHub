using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Mcp;

public sealed partial class McpJsonRpcHandler
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
        ProviderDiscoveryService? discovery = null,
        WorkspaceService? workspaces = null)
    {
        _syncEngine = syncEngine;
        _importer = importer ?? new SkillImporterService(syncEngine);
        _discovery = discovery ?? syncEngine.Discovery;
        _proposals = new SkillProposalService(syncEngine);
        _workspaceService = workspaces;
    }

    private WorkspaceService? _workspaceService;

    /// <summary>Project workspaces; the host passes its shared instance, tests get their own.</summary>
    private WorkspaceService Workspaces => _workspaceService ??= new WorkspaceService(_syncEngine, _discovery);

    private readonly SkillProposalService _proposals;

    /// <summary>Self-update and saved preferences, provided by the desktop host.</summary>
    public McpDesktopFeatures? Desktop { get; set; }

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
                "resources/list" => HandleResourcesList(responseId),
                "resources/templates/list" => HandleResourceTemplatesList(responseId),
                "resources/read" => HandleResourcesRead(responseId, paramsObj),
                "prompts/list" => HandlePromptsList(responseId),
                "prompts/get" => HandlePromptsGet(responseId, paramsObj),
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
                },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "skillhub",
                ["version"] = ProductVersion.Current
            },
            ["instructions"] = "Use SkillHub MCP tools (list_skills, get_skill, create_skill, update_skill, delete_skill, manage_skill_tags, toggle_skill_provider, search_community_skills, browse_community_repo, preview_community_skill, prepare_skill_import, apply_skill_import, list_tags, manage_tags, lint_skill, lint_library, test_skill_trigger, get_context_budget, set_skill_scope, list_projects, create_project, inspect_project_skills, set_project_skills, get_sync_status) as the Single Source of Truth whenever managing AI skills across Claude, Gemini/Antigravity, OpenAI Codex, and Cursor. Niche skill packs belong in Project-only scope, enabled per repository with set_project_skills. After importing a domain-specific pack, or when get_context_budget reports warning or over, suggest in one sentence making that pack Project-only. When the current workspace matches a Project-only pack it does not have yet (for example Roblox or Blender files), offer once to attach it with set_project_skills. Prefer an umbrella folder that groups related projects (create_project and inspect_project_skills return an umbrella hint), but if those projects have their own .git, warn that IDE tools such as Cursor, Copilot, Windsurf and Antigravity IDE may stop at that boundary and let the user choose. Audit tags with list_tags and project skills with inspect_project_skills instead of reading skill files, and when unsure whether a skill belongs to a project or a tag should change, ask the user about those specific items before changing anything."
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
                "browse_community_repo" => await ExecuteBrowseCommunityRepoAsync(arguments, ct).ConfigureAwait(false),
                "preview_community_skill" => await ExecutePreviewCommunitySkillAsync(arguments, ct).ConfigureAwait(false),
                "prepare_skill_import" => await ExecutePrepareImportAsync(arguments, ct).ConfigureAwait(false),
                "apply_skill_import" => await ExecuteApplyImportAsync(arguments, ct).ConfigureAwait(false),
                "import_community_skill" => await ExecuteImportCommunitySkillAsync(arguments, ct).ConfigureAwait(false),
                "activate_catalog_skill" => await ExecuteCatalogCommandAsync(arguments, true, ct).ConfigureAwait(false),
                "clone_catalog_skill" => await ExecuteCatalogCommandAsync(arguments, false, ct).ConfigureAwait(false),
                "restore_skill" => await ExecuteRestoreAsync(arguments, ct).ConfigureAwait(false),
                "get_sync_status" => ExecuteGetSyncStatus(),
                "check_skillhub_update" => await ExecuteCheckUpdateAsync(ct).ConfigureAwait(false),
                "apply_skillhub_update" => await ExecuteApplyUpdateAsync(arguments, ct).ConfigureAwait(false),
                "set_skillhub_preferences" => await ExecuteSetPreferencesAsync(arguments, ct).ConfigureAwait(false),
                "propose_skill_improvement" => await ExecuteProposeAsync(arguments, ct).ConfigureAwait(false),
                "list_skill_proposals" => JsonSerializer.Serialize(new { proposals = _proposals.List(arguments["skill"]?.GetValue<string>()) }, IndentedJson),
                "apply_skill_proposal" => await ExecuteApplyProposalAsync(arguments, ct).ConfigureAwait(false),
                "lint_skill" => ExecuteLintSkill(arguments),
                "lint_library" => ExecuteLintLibrary(arguments),
                "get_context_budget" => ExecuteGetContextBudget(),
                "set_skill_scope" => await ExecuteSetSkillScopeAsync(arguments, ct).ConfigureAwait(false),
                "test_skill_trigger" => ExecuteTestSkillTrigger(arguments),
                "list_projects" => ExecuteListProjects(),
                "set_project_skills" => await ExecuteSetProjectSkillsAsync(arguments, ct).ConfigureAwait(false),
                "list_tags" => ExecuteListTags(arguments),
                "manage_tags" => await ExecuteManageTagsAsync(arguments, ct).ConfigureAwait(false),
                "create_project" => await ExecuteCreateProjectAsync(arguments, ct).ConfigureAwait(false),
                "inspect_project_skills" => ExecuteInspectProjectSkills(arguments),
                _ => throw new ArgumentException($"Unknown tool '{toolName}'.")
            };

            return SerializeToolCallSuccess(idNode, payload, version != "2024-11-05");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or KeyNotFoundException or FormatException)
        {
            return SerializeToolCallError(idNode, ex.Message);
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
