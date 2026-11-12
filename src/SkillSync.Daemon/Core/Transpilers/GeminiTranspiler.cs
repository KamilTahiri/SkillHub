using System.Text;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Transpilers;

public sealed class GeminiTranspiler : ISkillTranspiler
{
    public const string TriggerDelimiter = " | Trigger: ";
    private static readonly string[] LegacyTriggerDelimiters = [" | Trigger: ", " — Trigger: ", " - Trigger: "];

    public ProviderType Provider => ProviderType.Gemini;

    public static string BuildSingleLineDescription(string description, string? whenToUse)
    {
        var cleanDesc = FrontmatterParser.CleanSingleLine(description);
        var cleanTrigger = FrontmatterParser.CleanSingleLine(whenToUse);

        if (string.IsNullOrEmpty(cleanTrigger))
        {
            return cleanDesc;
        }

        if (string.IsNullOrEmpty(cleanDesc))
        {
            return cleanTrigger;
        }

        return $"{cleanDesc}{TriggerDelimiter}{cleanTrigger}";
    }

    public string TranspileOutbound(CanonicalSkillModel model, string? canonicalSkillDirPath = null)
    {
        var folderSlug = model.Name.Trim().ToLowerInvariant();
        var singleLineDescription = BuildSingleLineDescription(model.Description, model.WhenToUse);
        var escapedDescription = singleLineDescription
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"name: {folderSlug}\n");
        sb.Append($"description: \"{escapedDescription}\"\n");
        sb.Append("---\n\n");
        sb.Append(ContentHasher.NormalizeLineEndings(model.InstructionsMarkdown ?? string.Empty).Trim());
        sb.Append('\n');
        return sb.ToString();
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
            throw new InvalidOperationException($"Incomplete or invalid frontmatter in Gemini skill '{skillSlug}'.");
        }

        var dict = FrontmatterParser.ParseYamlToDictionary(yaml);
        var merged = existingCanonical?.Clone() ?? new CanonicalSkillModel();

        // Enforce that name matches folder slug for Gemini
        merged.Name = skillSlug.Trim().ToLowerInvariant();

        if (dict.TryGetValue("description", out var descObj) && descObj is not null)
        {
            var incomingCombined = FrontmatterParser.CleanSingleLine(descObj.ToString());
            var previousSynthesized = ledgerEntry?.LastSynthesizedGeminiDescription
                ?? (existingCanonical is not null
                    ? BuildSingleLineDescription(existingCanonical.Description, existingCanonical.WhenToUse)
                    : null);

            // Only modify canonical description / when_to_use if the incoming combined description actually changed
            if (!string.Equals(incomingCombined, previousSynthesized, StringComparison.Ordinal))
            {
                var matchedDelim = LegacyTriggerDelimiters.FirstOrDefault(d => incomingCombined.Contains(d, StringComparison.OrdinalIgnoreCase));
                if (matchedDelim is not null)
                {
                    var delimiterIdx = incomingCombined.IndexOf(matchedDelim, StringComparison.OrdinalIgnoreCase);
                    merged.Description = incomingCombined[..delimiterIdx].Trim();
                    merged.WhenToUse = incomingCombined[(delimiterIdx + matchedDelim.Length)..].Trim();
                }
                else if (FrontmatterParser.TrySplitEmbeddedTrigger(incomingCombined, out var cleanDesc, out var extractedTrigger))
                {
                    merged.Description = cleanDesc;
                    merged.WhenToUse = extractedTrigger;
                }
                else
                {
                    merged.Description = incomingCombined;
                }
            }
        }

        merged.InstructionsMarkdown = body;
        return merged;
    }
}

