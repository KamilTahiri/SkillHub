using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Transpilers;

namespace SkillSync.Daemon.Mcp;

/// <summary>
/// MCP prompts and resources: every active skill is an invokable prompt (shown by clients as a slash command such as
/// <c>/skillhub:&lt;slug&gt;</c>) and a readable resource at <c>skillhub://skills/&lt;slug&gt;</c>, with bundle files at
/// <c>skillhub://skills/&lt;slug&gt;/files/&lt;path&gt;</c>. Clients without a skills folder still get every skill.
/// </summary>
public sealed partial class McpJsonRpcHandler
{
    private const string ResourceScheme = "skillhub://skills/";
    private static readonly CanonicalTranspiler ResourceTranspiler = new();

    private IEnumerable<CanonicalSkillModel> ActiveSkills() =>
        _syncEngine.ListSkills().Where(skill => !skill.IsCatalogOnly && skill.SourceCategory != "system")
            .OrderBy(skill => skill.Name, StringComparer.Ordinal);

    private string HandlePromptsList(JsonNode idNode)
    {
        var prompts = new JsonArray();
        foreach (var skill in ActiveSkills())
        {
            prompts.Add(new JsonObject
            {
                ["name"] = skill.Name,
                ["title"] = skill.GetEffectiveTitle(),
                ["description"] = Truncate(skill.Description, 1024),
                ["arguments"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "task",
                        ["description"] = "What you want to do with this skill (optional).",
                        ["required"] = false
                    }
                }
            });
        }
        return SerializeResult(idNode, new JsonObject { ["prompts"] = prompts });
    }

    private string HandlePromptsGet(JsonNode idNode, JsonObject? paramsObj)
    {
        var name = paramsObj?["name"]?.GetValue<string>() ?? string.Empty;
        if (name.StartsWith("skillhub:", StringComparison.Ordinal)) name = name["skillhub:".Length..];
        var skill = ActiveSkills().Any(item => item.Name == name) ? _syncEngine.GetSkill(name) : null;
        if (skill is null) return SerializeError(idNode, -32602, $"Unknown prompt '{name}'.");
        var task = paramsObj?["arguments"]?["task"]?.GetValue<string>();

        var text = new StringBuilder();
        text.Append("Follow the skill “").Append(skill.GetEffectiveTitle()).Append("” for this request.\n\n");
        if (!string.IsNullOrWhiteSpace(skill.WhenToUse)) text.Append("When to use: ").Append(skill.WhenToUse.Trim()).Append("\n\n");
        text.Append(skill.InstructionsMarkdown.Trim()).Append('\n');
        if (skill.SupportingFiles.Count > 0)
        {
            text.Append("\nBundled files (read them as MCP resources when the instructions refer to them):\n");
            foreach (var path in skill.SupportingFiles.Keys.Order(StringComparer.Ordinal).Take(200))
                text.Append("- ").Append(ResourceScheme).Append(skill.Name).Append("/files/").Append(path).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(task)) text.Append("\nTask: ").Append(task.Trim()).Append('\n');

        return SerializeResult(idNode, new JsonObject
        {
            ["description"] = Truncate(skill.Description, 1024),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = text.ToString() }
                }
            }
        });
    }

    private string HandleResourcesList(JsonNode idNode)
    {
        var resources = new JsonArray();
        foreach (var skill in ActiveSkills())
        {
            resources.Add(new JsonObject
            {
                ["uri"] = ResourceScheme + skill.Name,
                ["name"] = skill.Name,
                ["title"] = skill.GetEffectiveTitle(),
                ["description"] = Truncate(skill.Description, 1024),
                ["mimeType"] = "text/markdown"
            });
        }
        return SerializeResult(idNode, new JsonObject { ["resources"] = resources });
    }

    private static string HandleResourceTemplatesList(JsonNode idNode) => SerializeResult(idNode, new JsonObject
    {
        ["resourceTemplates"] = new JsonArray
        {
            new JsonObject
            {
                ["uriTemplate"] = ResourceScheme + "{slug}",
                ["name"] = "skill",
                ["title"] = "SkillHub skill",
                ["description"] = "The canonical SKILL.md of a SkillHub skill.",
                ["mimeType"] = "text/markdown"
            },
            new JsonObject
            {
                ["uriTemplate"] = ResourceScheme + "{slug}/files/{path}",
                ["name"] = "skill-file",
                ["title"] = "SkillHub bundle file",
                ["description"] = "A file bundled with a SkillHub skill, such as a reference document or script."
            }
        }
    });

    private string HandleResourcesRead(JsonNode idNode, JsonObject? paramsObj)
    {
        var uri = paramsObj?["uri"]?.GetValue<string>() ?? string.Empty;
        if (!uri.StartsWith(ResourceScheme, StringComparison.Ordinal)) return SerializeError(idNode, -32602, $"Unknown resource '{uri}'.");
        var rest = Uri.UnescapeDataString(uri[ResourceScheme.Length..]);
        var slash = rest.IndexOf('/');
        var slug = slash < 0 ? rest : rest[..slash];
        // ListSkills returns summaries; the full skill carries instructions and bundle files.
        var skill = ActiveSkills().Any(item => item.Name == slug) ? _syncEngine.GetSkill(slug) : null;
        if (skill is null) return SerializeError(idNode, -32002, $"Resource not found: '{uri}'.");

        string text;
        string mime;
        if (slash < 0)
        {
            text = ResourceTranspiler.TranspileOutbound(skill);
            mime = "text/markdown";
        }
        else
        {
            const string marker = "files/";
            var file = rest[(slash + 1)..];
            if (!file.StartsWith(marker, StringComparison.Ordinal) || !skill.SupportingFiles.TryGetValue(file[marker.Length..], out var content))
                return SerializeError(idNode, -32002, $"Resource not found: '{uri}'.");
            text = content;
            mime = file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown" : "text/plain";
        }
        return SerializeResult(idNode, new JsonObject
        {
            ["contents"] = new JsonArray { new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text } }
        });
    }

    private string ExecuteLintSkill(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>() ?? throw new ArgumentException("A skill name is required.");
        var skill = _syncEngine.GetSkill(name) ?? throw new KeyNotFoundException($"Skill '{name}' was not found.");
        return JsonSerializer.Serialize(SkillDoctor.Lint(skill, _syncEngine.ListSkills()), IndentedJson);
    }

    private static string Truncate(string? value, int length) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= length ? value : value[..(length - 1)] + "…";
}
