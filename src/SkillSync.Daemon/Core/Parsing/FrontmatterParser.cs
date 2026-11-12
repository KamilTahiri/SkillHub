using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SkillSync.Daemon.Core.Parsing;

public static class FrontmatterParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .WithNewLine("\n")
        .Build();

    private static readonly string[] EmbeddedTriggerPrefixes =
    [
        ". Use when ",
        ". Use for ",
        ". Use at ",
        ". Use this skill ",
        ". Also use when ",
        ". Trigger when ",
        ". Activate when ",
        ". Invoke when "
    ];

    public static bool TrySplitFrontmatter(string rawDocument, out string yamlFrontmatter, out string markdownBody)
    {
        yamlFrontmatter = string.Empty;
        markdownBody = string.Empty;

        if (string.IsNullOrWhiteSpace(rawDocument))
        {
            return false;
        }

        var normalized = ContentHasher.NormalizeLineEndings(rawDocument);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal) &&
            !string.Equals(normalized, "---", StringComparison.Ordinal))
        {
            // No frontmatter block present; treat entire document as markdown body
            markdownBody = normalized.Trim();
            return true;
        }

        // Find the closing "---" on its own line
        const string closingFence = "\n---\n";
        var closeIndex = normalized.IndexOf(closingFence, 3, StringComparison.Ordinal);
        if (closeIndex >= 0)
        {
            yamlFrontmatter = normalized.Substring(4, closeIndex - 4).Trim();
            markdownBody = normalized.Substring(closeIndex + closingFence.Length).Trim();
            return true;
        }

        if (normalized.EndsWith("\n---", StringComparison.Ordinal) && normalized.Length > 4)
        {
            yamlFrontmatter = normalized.Substring(4, normalized.Length - 8).Trim();
            markdownBody = string.Empty;
            return true;
        }

        // Incomplete frontmatter (e.g. partial write in progress)
        return false;
    }

    public static bool TrySplitEmbeddedTrigger(
        string? rawDescription,
        out string descriptionPart,
        out string triggerPart)
    {
        descriptionPart = string.Empty;
        triggerPart = string.Empty;

        if (string.IsNullOrWhiteSpace(rawDescription))
        {
            return false;
        }

        var cleaned = rawDescription.Trim();
        var bestIdx = -1;

        foreach (var prefix in EmbeddedTriggerPrefixes)
        {
            var idx = cleaned.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && (bestIdx < 0 || idx < bestIdx))
            {
                bestIdx = idx;
            }
        }

        if (bestIdx <= 0)
        {
            return false;
        }

        descriptionPart = cleaned[..(bestIdx + 1)].Trim();
        triggerPart = cleaned[(bestIdx + 2)..].Trim();
        return !string.IsNullOrEmpty(descriptionPart) && !string.IsNullOrEmpty(triggerPart);
    }

    public static string? ExtractFirstMarkdownHeading(string? markdownBody)
    {
        if (string.IsNullOrWhiteSpace(markdownBody))
        {
            return null;
        }

        var lines = ContentHasher.NormalizeLineEndings(markdownBody)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var heading = line[2..].Trim();
                if (!string.IsNullOrWhiteSpace(heading))
                {
                    return heading;
                }
            }
        }

        return null;
    }

    public static Dictionary<string, object?> ParseYamlToDictionary(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var rawObj = YamlDeserializer.Deserialize<Dictionary<object, object?>>(yaml);
        if (rawObj is null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return ConvertDictionary(rawObj);
    }

    public static T DeserializeYaml<T>(string yaml) where T : new()
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new T();
        }

        var result = YamlDeserializer.Deserialize<T>(yaml);
        return result ?? new T();
    }

    public static string SerializeToDocument(object frontmatterObject, string markdownBody)
    {
        var yaml = YamlSerializer.Serialize(frontmatterObject).Trim();
        yaml = ContentHasher.NormalizeLineEndings(yaml);
        var cleanBody = ContentHasher.NormalizeLineEndings(markdownBody ?? string.Empty).Trim();

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append(yaml);
        sb.Append("\n---\n\n");
        sb.Append(cleanBody);
        sb.Append('\n');
        return sb.ToString();
    }

    public static string CleanSingleLine(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var lines = ContentHasher.NormalizeLineEndings(input)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines).Trim();
    }

    public static List<string> ExtractStringList(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val is null)
        {
            return [];
        }

        if (val is IEnumerable<object?> list)
        {
            return list
                .Select(x => x?.ToString()?.Trim() ?? string.Empty)
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
        }

        if (val is string str && !string.IsNullOrWhiteSpace(str))
        {
            return str
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return [];
    }

    private static Dictionary<string, object?> ConvertDictionary(Dictionary<object, object?> source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in source)
        {
            var key = kvp.Key?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            result[key] = ConvertValue(kvp.Value);
        }

        return result;
    }

    private static object? ConvertValue(object? val)
    {
        return val switch
        {
            Dictionary<object, object?> childDict => ConvertDictionary(childDict),
            IList<object?> childList => childList.Select(ConvertValue).ToList(),
            _ => val
        };
    }
}
