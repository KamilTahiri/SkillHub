using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Transpilers;

/// <summary>
/// The open Agent Skills format (<c>&lt;slug&gt;/SKILL.md</c> with <c>name</c> and <c>description</c>) used by
/// GitHub Copilot (<c>~/.copilot/skills</c>) and Windsurf (<c>~/.codeium/windsurf/skills</c>).
/// Inbound edits only change what the format carries; everything else is merged from the canonical skill.
/// </summary>
public sealed class AgentSkillTranspiler(ProviderType provider) : ISkillTranspiler
{
    public ProviderType Provider => provider;

    public string TranspileOutbound(CanonicalSkillModel model, string? canonicalSkillDirPath = null)
    {
        var description = string.IsNullOrWhiteSpace(model.WhenToUse)
            ? model.Description.Trim()
            : model.Description.Trim() + " " + model.WhenToUse.Trim();
        var frontmatter = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = model.Name.Trim().ToLowerInvariant(),
            ["description"] = FrontmatterParser.CleanSingleLine(description.Length > 1024 ? description[..1021] + "..." : description)
        };
        return FrontmatterParser.SerializeToDocument(frontmatter, model.InstructionsMarkdown);
    }

    public CanonicalSkillModel MergeInbound(string skillSlug, string rawFileContent, CanonicalSkillModel? existingCanonical,
        SkillLedgerEntry? ledgerEntry = null, string? canonicalSkillDirPath = null)
    {
        if (!FrontmatterParser.TrySplitFrontmatter(rawFileContent, out var yaml, out var body))
            throw new InvalidOperationException($"Incomplete or invalid frontmatter in {provider} skill '{skillSlug}'.");
        var values = FrontmatterParser.ParseYamlToDictionary(yaml);
        var merged = existingCanonical?.Clone() ?? new CanonicalSkillModel();
        merged.Name = skillSlug.Trim().ToLowerInvariant();
        var description = values.GetValueOrDefault("description")?.ToString()?.Trim();
        // The outbound description folds "when to use" in; only accept an edited description that changed that text.
        if (description is not null && existingCanonical is not null &&
            FrontmatterParser.CleanSingleLine(description) != FrontmatterParser.CleanSingleLine(TranspileDescription(existingCanonical)))
            merged.Description = description;
        else if (existingCanonical is null && description is not null)
            merged.Description = description;
        merged.InstructionsMarkdown = body;
        return merged;
    }

    private static string TranspileDescription(CanonicalSkillModel model) =>
        string.IsNullOrWhiteSpace(model.WhenToUse) ? model.Description.Trim() : model.Description.Trim() + " " + model.WhenToUse.Trim();
}

/// <summary>
/// Plain Markdown rule files for Cline (<c>~/Documents/Cline/Rules/&lt;slug&gt;.md</c>) and Roo Code
/// (<c>~/.roo/rules/&lt;slug&gt;.md</c>). These tools load every rule into every conversation, so the file carries the
/// instructions only; metadata stays in the canonical skill.
/// </summary>
public sealed class PlainRuleTranspiler(ProviderType provider) : ISkillTranspiler
{
    public ProviderType Provider => provider;

    public string TranspileOutbound(CanonicalSkillModel model, string? canonicalSkillDirPath = null) =>
        ContentHasher.NormalizeLineEndings(model.InstructionsMarkdown).TrimEnd() + "\n";

    public CanonicalSkillModel MergeInbound(string skillSlug, string rawFileContent, CanonicalSkillModel? existingCanonical,
        SkillLedgerEntry? ledgerEntry = null, string? canonicalSkillDirPath = null)
    {
        var merged = existingCanonical?.Clone() ?? new CanonicalSkillModel { Description = CanonicalSkillModel.HumanizeSlug(skillSlug) };
        merged.Name = skillSlug.Trim().ToLowerInvariant();
        var body = FrontmatterParser.TrySplitFrontmatter(rawFileContent, out var yaml, out var rest) && yaml.Length > 0 ? rest : rawFileContent;
        var normalized = ContentHasher.NormalizeLineEndings(body).TrimEnd() + "\n";
        // Keep the canonical text as is when the rule file only differs by trailing whitespace.
        if (existingCanonical is null || normalized != TranspileOutbound(existingCanonical)) merged.InstructionsMarkdown = normalized;
        return merged;
    }
}
