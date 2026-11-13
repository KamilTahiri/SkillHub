using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Transpilers;

public sealed partial class CursorMdcTranspiler : ISkillTranspiler
{
    public const string TriggerDelimiter = " — Trigger: ";

    public ProviderType Provider => ProviderType.Cursor;

    public static string BuildCursorDescription(CanonicalSkillModel model)
    {
        var cleanDesc = FrontmatterParser.CleanSingleLine(model.Description);
        var cleanTrigger = FrontmatterParser.CleanSingleLine(model.WhenToUse);

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
        var globs = model.Targets.Cursor.Globs;

        var frontmatter = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["description"] = BuildCursorDescription(model),
            ["globs"] = globs.Count == 1 ? globs[0] : globs,
            ["alwaysApply"] = model.Targets.Cursor.AlwaysApply
        };

        var body = model.InstructionsMarkdown ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(canonicalSkillDirPath))
        {
            var normalizedDir = canonicalSkillDirPath.Replace('\\', '/').TrimEnd('/');
            body = RelativeAssetPathRegex().Replace(body, match =>
            {
                var prefix = match.Groups["prefix"].Value;
                var folder = match.Groups["folder"].Value;
                var rest = match.Groups["rest"].Value;
                return $"{prefix}{normalizedDir}/{folder}/{rest}";
            });
        }

        return FrontmatterParser.SerializeToDocument(frontmatter, body);
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
            throw new InvalidOperationException($"Incomplete or invalid frontmatter in Cursor rule '{skillSlug}'.");
        }

        var dict = FrontmatterParser.ParseYamlToDictionary(yaml);
        var merged = existingCanonical?.Clone() ?? new CanonicalSkillModel();
        merged.Name = skillSlug.Trim().ToLowerInvariant();

        if (dict.TryGetValue("description", out var descObj) && descObj is not null)
        {
            var incomingDesc = FrontmatterParser.CleanSingleLine(descObj.ToString());
            var previousSynthesized = ledgerEntry?.LastSynthesizedCursorDescription
                ?? (existingCanonical is not null ? BuildCursorDescription(existingCanonical) : null);

            if (!string.Equals(incomingDesc, previousSynthesized, StringComparison.Ordinal))
            {
                var delimiterIdx = incomingDesc.IndexOf(TriggerDelimiter, StringComparison.Ordinal);
                if (delimiterIdx >= 0)
                {
                    merged.Description = incomingDesc[..delimiterIdx].Trim();
                    merged.WhenToUse = incomingDesc[(delimiterIdx + TriggerDelimiter.Length)..].Trim();
                }
                else
                {
                    merged.Description = incomingDesc;
                }
            }
        }

        if (dict.ContainsKey("globs"))
        {
            var globs = FrontmatterParser.ExtractStringList(dict, "globs");
            merged.Targets.Cursor.Globs = globs;
        }

        if (dict.TryGetValue("alwaysApply", out var alwaysObj) || dict.TryGetValue("always_apply", out alwaysObj))
        {
            if (alwaysObj is bool b)
            {
                merged.Targets.Cursor.AlwaysApply = b;
            }
            else if (bool.TryParse(alwaysObj?.ToString(), out var parsedBool))
            {
                merged.Targets.Cursor.AlwaysApply = parsedBool;
            }
        }

        if (!string.IsNullOrWhiteSpace(canonicalSkillDirPath))
        {
            var normalizedDir = canonicalSkillDirPath.Replace('\\', '/').TrimEnd('/');
            var escapedDir = Regex.Escape(normalizedDir);
            body = Regex.Replace(
                body,
                $@"{escapedDir}/(?<folder>scripts|resources|references|examples)/(?<rest>[a-zA-Z0-9._/-]+)",
                "${folder}/${rest}",
                RegexOptions.CultureInvariant);
        }

        merged.InstructionsMarkdown = body;
        return merged;
    }

    [GeneratedRegex(@"(?<prefix>[\(\[`""\s])(?:\./)?(?<folder>scripts|resources|references|examples)/(?<rest>[a-zA-Z0-9._/-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeAssetPathRegex();
}

