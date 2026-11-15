using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Parsing;

public static class ContentHasher
{
    public static string NormalizeLineEndings(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        // Strip UTF-8 BOM char if present
        var span = content.AsSpan();
        if (span.Length > 0 && span[0] == '\uFEFF')
        {
            content = content[1..];
        }

        return content.Replace("\r\n", "\n", StringComparison.Ordinal)
                      .Replace("\r", "\n", StringComparison.Ordinal);
    }

    public static string ComputeRawFileHash(string rawContent)
    {
        var normalized = NormalizeLineEndings(rawContent).TrimEnd();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeFileRevisionHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeLineEndings(content)))).ToLowerInvariant();

    public static string ComputeRawFileHash(ReadOnlySpan<byte> rawBytes)
    {
        // Strip UTF-8 BOM bytes (0xEF, 0xBB, 0xBF) if present
        if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
        {
            rawBytes = rawBytes[3..];
        }

        var text = Encoding.UTF8.GetString(rawBytes);
        return ComputeRawFileHash(text);
    }

    private static object? NormalizeMetadata(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(property => property.Name, property => NormalizeMetadata(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(NormalizeMetadata).ToArray(),
        _ => element
    };

    public static string ComputeSemanticHash(CanonicalSkillModel model)
    {
        var canonicalProjection = new
        {
            name = (model.Name ?? string.Empty).Trim().ToLowerInvariant(),
            title = model.Title,
            source_category = model.SourceCategory,
            origin_provider = model.OriginProvider,
            passthrough = NormalizeMetadata(JsonSerializer.SerializeToElement(model.Passthrough)),
            version = (model.Version ?? "1.0.0").Trim(),
            description = FrontmatterParser.CleanSingleLine(model.Description),
            when_to_use = FrontmatterParser.CleanSingleLine(model.WhenToUse),
            summary_short = FrontmatterParser.CleanSingleLine(model.SummaryShort),
            tags = (model.Tags ?? [])
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => !string.IsNullOrEmpty(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToArray(),
            targets = new
            {
                claude = new
                {
                    enabled = model.Targets.Claude.Enabled,
                    allowed_tools = (model.Targets.Claude.AllowedTools ?? [])
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .OrderBy(t => t, StringComparer.Ordinal)
                        .ToArray(),
                    model = model.Targets.Claude.Model ?? string.Empty
                },
                gemini = new
                {
                    enabled = model.Targets.Gemini.Enabled
                },
                codex = new
                {
                    enabled = model.Targets.Codex.Enabled
                },
                cursor = new
                {
                    enabled = model.Targets.Cursor.Enabled,
                    globs = (model.Targets.Cursor.Globs ?? [])
                        .Select(g => g.Trim())
                        .Where(g => !string.IsNullOrEmpty(g))
                        .OrderBy(g => g, StringComparer.Ordinal)
                        .ToArray(),
                    always_apply = model.Targets.Cursor.AlwaysApply
                }
            },
            instructions = NormalizeLineEndings(model.InstructionsMarkdown ?? string.Empty),
            supporting_files = (model.SupportingFiles ?? new Dictionary<string, string>())
                .OrderBy(kvp => kvp.Key.Replace('\\', '/'), StringComparer.Ordinal)
                .Select(kvp => new
                {
                    path = kvp.Key.Replace('\\', '/'),
                    hash = ComputeFileRevisionHash(kvp.Value)
                })
                .ToArray()
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(canonicalProjection);
        // Targets added later only affect the hash when they differ from their defaults, so existing skills keep their hashes.
        var extra = new List<string>();
        if (!model.Targets.Copilot.Enabled) extra.Add("copilot=off");
        if (!model.Targets.Windsurf.Enabled) extra.Add("windsurf=off");
        if (model.Targets.Cline.Enabled) extra.Add("cline=on");
        if (model.Targets.RooCode.Enabled) extra.Add("roocode=on");
        if (extra.Count > 0) jsonBytes = [.. jsonBytes, .. System.Text.Encoding.UTF8.GetBytes("|" + string.Join(',', extra))];
        var hash = SHA256.HashData(jsonBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

