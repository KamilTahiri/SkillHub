using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Transpilers;

public sealed class CodexTranspiler : ISkillTranspiler
{
    public const int MaxDescriptionLength = 1024;

    public ProviderType Provider => ProviderType.Codex;

    public static string BuildConciseDescription(CanonicalSkillModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.SummaryShort))
        {
            return TruncateAtBoundary(FrontmatterParser.CleanSingleLine(model.SummaryShort), MaxDescriptionLength);
        }

        var combined = string.IsNullOrWhiteSpace(model.WhenToUse)
            ? FrontmatterParser.CleanSingleLine(model.Description)
            : $"{FrontmatterParser.CleanSingleLine(model.Description)} {FrontmatterParser.CleanSingleLine(model.WhenToUse)}";

        return TruncateAtBoundary(combined, MaxDescriptionLength);
    }

    public string TranspileOutbound(CanonicalSkillModel model, string? canonicalSkillDirPath = null)
    {
        var frontmatter = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = model.Name.Trim().ToLowerInvariant(),
            ["description"] = BuildConciseDescription(model)
        };

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
            throw new InvalidOperationException($"Incomplete or invalid frontmatter in Codex skill '{skillSlug}'.");
        }

        var dict = FrontmatterParser.ParseYamlToDictionary(yaml);
        var merged = existingCanonical?.Clone() ?? new CanonicalSkillModel();
        merged.Name = skillSlug.Trim().ToLowerInvariant();

        if (dict.TryGetValue("description", out var descObj) && descObj is not null)
        {
            var incomingDesc = FrontmatterParser.CleanSingleLine(descObj.ToString());
            var previousSynthesized = ledgerEntry?.LastSynthesizedCodexDescription
                ?? (existingCanonical is not null ? BuildConciseDescription(existingCanonical) : null);

            if (!string.Equals(incomingDesc, previousSynthesized, StringComparison.Ordinal))
            {
                if (existingCanonical is null || string.IsNullOrWhiteSpace(existingCanonical.Description))
                {
                    if (FrontmatterParser.TrySplitEmbeddedTrigger(incomingDesc, out var whatItDoes, out var whenToUse))
                    {
                        merged.Description = whatItDoes;
                        merged.WhenToUse = whenToUse;
                        merged.SummaryShort = string.Empty;
                    }
                    else
                    {
                        merged.Description = incomingDesc;
                        merged.SummaryShort = string.Empty;
                    }
                }
                else
                {
                    // Update SummaryShort without wiping the full canonical Description or WhenToUse
                    merged.SummaryShort = incomingDesc;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(merged.SummaryShort) &&
            dict.TryGetValue("metadata", out var metaObj) &&
            metaObj is Dictionary<string, object?> metaDict &&
            metaDict.TryGetValue("short-description", out var shortDescObj) &&
            shortDescObj is not null)
        {
            var shortDesc = FrontmatterParser.CleanSingleLine(shortDescObj.ToString());
            if (!string.IsNullOrWhiteSpace(shortDesc))
            {
                merged.Passthrough["short_description"] = shortDesc;
            }
        }

        merged.InstructionsMarkdown = body;
        return merged;
    }

    private static string TruncateAtBoundary(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        var slice = text[..(maxLength - 3)];
        var lastSpace = slice.LastIndexOf(' ');
        if (lastSpace > maxLength / 2)
        {
            slice = slice[..lastSpace];
        }

        return $"{slice.TrimEnd('.', ',', ';', ' ')}...";
    }
}
