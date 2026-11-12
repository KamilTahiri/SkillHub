using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Transpilers;

public sealed class ClaudeTranspiler : ISkillTranspiler
{
    public ProviderType Provider => ProviderType.Claude;

    public string TranspileOutbound(CanonicalSkillModel model, string? canonicalSkillDirPath = null)
    {
        var frontmatter = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = model.Name.Trim().ToLowerInvariant(),
            ["description"] = model.Description.Trim()
        };

        if (!string.IsNullOrWhiteSpace(model.WhenToUse))
        {
            frontmatter["when_to_use"] = model.WhenToUse.Trim();
        }

        if (model.Targets.Claude.AllowedTools is { Count: > 0 })
        {
            frontmatter["allowed-tools"] = model.Targets.Claude.AllowedTools;
        }

        if (!string.IsNullOrWhiteSpace(model.Targets.Claude.Model))
        {
            frontmatter["model"] = model.Targets.Claude.Model.Trim();
        }

        return FrontmatterParser.SerializeToDocument(frontmatter, model.InstructionsMarkdown);
    }

    public CanonicalSkillModel MergeInbound(
        string skillSlug,
        string rawFileContent,
        CanonicalSkillModel? existingCanonical,
        SkillLedgerEntry? ledgerEntry = null,
        string? canonicalSkillDirPath = null)
    {
        if (!FrontmatterParser.TrySplitFrontmatter(rawFileContent, out var yaml, out var body))
        {
            throw new InvalidOperationException($"Incomplete or invalid frontmatter in Claude skill '{skillSlug}'.");
        }

        var dict = FrontmatterParser.ParseYamlToDictionary(yaml);
        var merged = existingCanonical?.Clone() ?? new CanonicalSkillModel();

        merged.Name = skillSlug.Trim().ToLowerInvariant();

        if (dict.TryGetValue("description", out var descObj) && descObj is not null)
        {
            merged.Description = descObj.ToString()?.Trim() ?? string.Empty;
        }

        if (dict.TryGetValue("when_to_use", out var whenObj))
        {
            merged.WhenToUse = whenObj?.ToString()?.Trim() ?? string.Empty;
        }

        if (dict.ContainsKey("allowed-tools") || dict.ContainsKey("allowed_tools"))
        {
            var key = dict.ContainsKey("allowed-tools") ? "allowed-tools" : "allowed_tools";
            merged.Targets.Claude.AllowedTools = FrontmatterParser.ExtractStringList(dict, key);
        }

        if (dict.TryGetValue("model", out var modelObj))
        {
            merged.Targets.Claude.Model = modelObj?.ToString()?.Trim();
        }

        merged.InstructionsMarkdown = body;
        return merged;
    }
}

