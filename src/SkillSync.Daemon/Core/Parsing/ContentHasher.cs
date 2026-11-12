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

    public static string ComputeSemanticHash(CanonicalSkillModel model)
    {
        var canonicalProjection = new
        {
            name = (model.Name ?? string.Empty).Trim().ToLowerInvariant(),
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
            instructions = NormalizeLineEndings(model.InstructionsMarkdown ?? string.Empty).Trim(),
            supporting_files = (model.SupportingFiles ?? new Dictionary<string, string>())
                .OrderBy(kvp => kvp.Key.Replace('\\', '/'), StringComparer.Ordinal)
                .Select(kvp => new
                {
                    path = kvp.Key.Replace('\\', '/'),
                    hash = ComputeRawFileHash(kvp.Value)
                })
                .ToArray()
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(canonicalProjection);
        var hash = SHA256.HashData(jsonBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

