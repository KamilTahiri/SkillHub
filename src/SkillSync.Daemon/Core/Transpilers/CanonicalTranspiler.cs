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
        merged.Name = skillSlug.ToLowerInvariant();
        merged.Title = dict.ContainsKey("title") && !string.IsNullOrWhiteSpace(parsed.Title)
            ? parsed.Title.Trim()
            : (string.IsNullOrWhiteSpace(merged.Title) ? CanonicalSkillModel.HumanizeSlug(merged.Name) : merged.Title);
        if (dict.ContainsKey("version") && !string.IsNullOrWhiteSpace(parsed.Version))
        {
            merged.Version = parsed.Version.Trim();
        }

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
            if (dict["targets"] is not Dictionary<string, object?> targetFields || parsed.Targets is null)
            {
                throw new ArgumentException("Skill targets must be a mapping.");
            }
            var fields = targetFields.ToDictionary(pair => pair.Key, pair =>
                pair.Value as Dictionary<string, object?> ?? throw new ArgumentException("Each target must be a mapping."));
            if (fields.TryGetValue("claude", out var claude))
            {
                if (claude.ContainsKey("enabled")) merged.Targets.Claude.Enabled = parsed.Targets.Claude.Enabled;
                if (claude.ContainsKey("allowed_tools")) merged.Targets.Claude.AllowedTools = parsed.Targets.Claude.AllowedTools ?? [];
                if (claude.ContainsKey("model")) merged.Targets.Claude.Model = parsed.Targets.Claude.Model;
            }
            if (fields.TryGetValue("gemini", out var gemini) && gemini.ContainsKey("enabled"))
                merged.Targets.Gemini.Enabled = parsed.Targets.Gemini.Enabled;
            if (fields.TryGetValue("codex", out var codex) && codex.ContainsKey("enabled"))
                merged.Targets.Codex.Enabled = parsed.Targets.Codex.Enabled;
            if (fields.TryGetValue("cursor", out var cursor))
            {
                if (cursor.ContainsKey("enabled")) merged.Targets.Cursor.Enabled = parsed.Targets.Cursor.Enabled;
                if (cursor.ContainsKey("globs")) merged.Targets.Cursor.Globs = parsed.Targets.Cursor.Globs ?? [];
                if (cursor.ContainsKey("always_apply")) merged.Targets.Cursor.AlwaysApply = parsed.Targets.Cursor.AlwaysApply;
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

        foreach (var (key, value) in dict)
        {
            if (key is not ("name" or "title" or "version" or "description" or "when_to_use" or
                "summary_short" or "source_category" or "origin_provider" or "tags" or "targets" or
                "allowed-tools" or "allowed_tools" or "x_passthrough"))
            {
                merged.Passthrough[key] = value;
            }
        }

        merged.InstructionsMarkdown = body;
        return merged;
    }
}
