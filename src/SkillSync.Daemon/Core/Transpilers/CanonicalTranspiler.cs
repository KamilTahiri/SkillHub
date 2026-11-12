using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Transpilers;

public sealed class CanonicalTranspiler : ISkillTranspiler
{
    public ProviderType Provider => ProviderType.Canonical;

    public string TranspileOutbound(CanonicalSkillModel model, string? canonicalSkillDirPath = null)
    {
        var clone = model.Clone();
        clone.Name = model.Name.Trim().ToLowerInvariant();
        clone.Title = clone.GetEffectiveTitle();

        if (string.IsNullOrWhiteSpace(clone.Version))
        {
            clone.Version = "1.0.0";
        }

        if (string.IsNullOrWhiteSpace(clone.SourceCategory))
        {
            clone.SourceCategory = "user";
        }

        if (string.IsNullOrWhiteSpace(clone.OriginProvider))
        {
            clone.OriginProvider = "My Skill";
        }

        if (clone.Targets.Cursor.Globs.Count == 0)
        {
            clone.Targets.Cursor.Globs.Add("**/*");
        }

        return FrontmatterParser.SerializeToDocument(clone, clone.InstructionsMarkdown);
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
            throw new InvalidOperationException($"Incomplete or invalid frontmatter in canonical skill '{skillSlug}'.");
        }

        var parsed = FrontmatterParser.DeserializeYaml<CanonicalSkillModel>(yaml);
        var dict = FrontmatterParser.ParseYamlToDictionary(yaml);

        var merged = existingCanonical?.Clone() ?? new CanonicalSkillModel();
        merged.Name = !string.IsNullOrWhiteSpace(parsed.Name) ? parsed.Name.Trim().ToLowerInvariant() : skillSlug.ToLowerInvariant();
        merged.Title = dict.ContainsKey("title") && !string.IsNullOrWhiteSpace(parsed.Title)
            ? parsed.Title.Trim()
            : (string.IsNullOrWhiteSpace(merged.Title) ? CanonicalSkillModel.HumanizeSlug(merged.Name) : merged.Title);
        merged.Version = !string.IsNullOrWhiteSpace(parsed.Version) ? parsed.Version.Trim() : (merged.Version ?? "1.0.0");

        if (dict.ContainsKey("source_category") && !string.IsNullOrWhiteSpace(parsed.SourceCategory))
        {
            merged.SourceCategory = parsed.SourceCategory.Trim();
        }

        if (dict.ContainsKey("origin_provider") && !string.IsNullOrWhiteSpace(parsed.OriginProvider))
        {
            merged.OriginProvider = parsed.OriginProvider.Trim();
        }

        if (dict.ContainsKey("description"))
        {
            merged.Description = parsed.Description ?? string.Empty;
        }

        if (dict.ContainsKey("when_to_use"))
        {
            merged.WhenToUse = parsed.WhenToUse ?? string.Empty;
        }

        if (dict.ContainsKey("summary_short"))
        {
            merged.SummaryShort = parsed.SummaryShort ?? string.Empty;
        }

        // Automatically extract embedded trigger sentences if when_to_use was left blank on earlier imports
        if (string.IsNullOrWhiteSpace(merged.WhenToUse) &&
            FrontmatterParser.TrySplitEmbeddedTrigger(merged.Description, out var splitDesc, out var splitTrigger))
        {
            if (string.Equals(merged.SummaryShort, merged.Description, StringComparison.Ordinal))
            {
                merged.SummaryShort = string.Empty;
            }

            merged.Description = splitDesc;
            merged.WhenToUse = splitTrigger;
        }

        if (dict.ContainsKey("tags"))
        {
            merged.Tags = parsed.Tags ?? [];
        }

        if (dict.ContainsKey("targets"))
        {
            merged.Targets = parsed.Targets ?? new SkillTargetsConfig();
            if (merged.Targets.Cursor.Globs.Count == 0)
            {
                merged.Targets.Cursor.Globs.Add("**/*");
            }
        }

        if (dict.ContainsKey("allowed-tools") || dict.ContainsKey("allowed_tools"))
        {
            var tools = FrontmatterParser.ExtractStringList(dict, dict.ContainsKey("allowed-tools") ? "allowed-tools" : "allowed_tools");
            if (tools.Count > 0)
            {
                merged.Targets.Claude.AllowedTools = tools;
            }
        }

        if (dict.ContainsKey("x_passthrough") && parsed.Passthrough is not null)
        {
            merged.Passthrough = parsed.Passthrough;
        }

        merged.InstructionsMarkdown = body;
        return merged;
    }
}
