using System.Globalization;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace SkillSync.Daemon.Core.Models;

public sealed class CanonicalSkillModel
{
    [YamlMember(Alias = "name")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "title")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [YamlMember(Alias = "version")]
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [YamlMember(Alias = "description")]
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [YamlMember(Alias = "when_to_use")]
    [JsonPropertyName("when_to_use")]
    public string WhenToUse { get; set; } = string.Empty;

    [YamlMember(Alias = "summary_short")]
    [JsonPropertyName("summary_short")]
    public string SummaryShort { get; set; } = string.Empty;

    [YamlMember(Alias = "source_category")]
    [JsonPropertyName("source_category")]
    public string SourceCategory { get; set; } = "user";

    [YamlMember(Alias = "origin_provider")]
    [JsonPropertyName("origin_provider")]
    public string OriginProvider { get; set; } = "My Skill";

    [YamlMember(Alias = "tags")]
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [YamlMember(Alias = "targets")]
    [JsonPropertyName("targets")]
    public SkillTargetsConfig Targets { get; set; } = new();

    [YamlMember(Alias = "x_passthrough")]
    [JsonPropertyName("x_passthrough")]
    public Dictionary<string, object?> Passthrough { get; set; } = new(StringComparer.Ordinal);

    [YamlIgnore]
    [JsonPropertyName("instructions_markdown")]
    public string InstructionsMarkdown { get; set; } = string.Empty;

    [YamlIgnore]
    [JsonPropertyName("supporting_files")]
    public Dictionary<string, string> SupportingFiles { get; set; } = new(StringComparer.Ordinal);

    [YamlIgnore]
    [JsonPropertyName("is_catalog_only")]
    public bool IsCatalogOnly { get; set; }

    [YamlIgnore]
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [YamlIgnore]
    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [YamlIgnore]
    [JsonPropertyName("catalog_id")]
    public string? CatalogId { get; set; }

    [YamlIgnore]
    [JsonPropertyName("destinations")]
    public IReadOnlyList<SyncDestinationStatus> Destinations { get; set; } = [];

    public string GetEffectiveTitle()
    {
        if (!string.IsNullOrWhiteSpace(Title))
        {
            return Title.Trim();
        }

        return HumanizeSlug(Name);
    }

    public static string HumanizeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "Untitled Skill";
        }

        var normalized = slug.Trim();
        if (normalized.StartsWith("artifact-template-", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["artifact-template-".Length..];
        }

        var words = normalized.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var lower = words[i].ToLowerInvariant();
            words[i] = lower switch
            {
                "csharp" => "C#",
                "fsharp" => "F#",
                "cpp" => "C++",
                "dotnet" => ".NET",
                "api" => "API",
                "ui" => "UI",
                "ux" => "UX",
                "sql" => "SQL",
                "mcp" => "MCP",
                "cli" => "CLI",
                "pr" => "PR",
                "ci" => "CI",
                "cd" => "CD",
                "ai" => "AI",
                "llm" => "LLM",
                "json" => "JSON",
                "yaml" => "YAML",
                "html" => "HTML",
                "css" => "CSS",
                "js" => "JS",
                "ts" => "TS",
                "skillhub" => "SkillHub",
                "openai" => "OpenAI",
                "chatgpt" => "ChatGPT",
                "docx" => "DOCX",
                "pdf" => "PDF",
                "ios" => "iOS",
                "macos" => "macOS",
                "qa" => "QA",
                "sdk" => "SDK",
                "2d" => "2D",
                "3d" => "3D",
                "kpi" => "KPI",
                "kpis" => "KPIs",
                "dcf" => "DCF",
                "oauth" => "OAuth",
                _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower)
            };
        }

        return string.Join(" ", words);
    }

    public CanonicalSkillModel Clone(bool includeContents = true)
    {
        return new CanonicalSkillModel
        {
            Name = Name,
            Title = string.IsNullOrWhiteSpace(Title) ? HumanizeSlug(Name) : Title,
            Version = Version,
            Description = Description,
            WhenToUse = WhenToUse,
            SummaryShort = SummaryShort,
            SourceCategory = string.IsNullOrWhiteSpace(SourceCategory) ? "user" : SourceCategory,
            OriginProvider = string.IsNullOrWhiteSpace(OriginProvider) ? "My Skill" : OriginProvider,
            IsCatalogOnly = IsCatalogOnly,
            SourcePath = SourcePath,
            Revision = Revision,
            CatalogId = CatalogId,
            Destinations = Destinations.ToArray(),
            Tags = [.. Tags],
            Targets = new SkillTargetsConfig
            {
                Claude = new ClaudeTargetConfig
                {
                    Enabled = Targets.Claude.Enabled,
                    AllowedTools = [.. Targets.Claude.AllowedTools],
                    Model = Targets.Claude.Model
                },
                Gemini = new GeminiTargetConfig
                {
                    Enabled = Targets.Gemini.Enabled
                },
                Codex = new CodexTargetConfig
                {
                    Enabled = Targets.Codex.Enabled
                },
                Cursor = new CursorTargetConfig
                {
                    Enabled = Targets.Cursor.Enabled,
                    Globs = [.. Targets.Cursor.Globs],
                    AlwaysApply = Targets.Cursor.AlwaysApply
                },
                Copilot = new SimpleTargetConfig { Enabled = Targets.Copilot.Enabled },
                Windsurf = new SimpleTargetConfig { Enabled = Targets.Windsurf.Enabled },
                Cline = new SimpleTargetConfig { Enabled = Targets.Cline.Enabled },
                RooCode = new SimpleTargetConfig { Enabled = Targets.RooCode.Enabled }
            },
            Passthrough = includeContents ? CopyMetadata(Passthrough) : new(StringComparer.Ordinal),
            InstructionsMarkdown = includeContents ? InstructionsMarkdown : string.Empty,
            SupportingFiles = includeContents ? new Dictionary<string, string>(SupportingFiles, StringComparer.Ordinal) : new(StringComparer.Ordinal)
        };
    }

    private static Dictionary<string, object?> CopyMetadata(Dictionary<string, object?> values) =>
        values.ToDictionary(pair => pair.Key, pair => CopyValue(pair.Value), StringComparer.Ordinal);

    private static object? CopyValue(object? value) => value switch
    {
        Dictionary<string, object?> dictionary => CopyMetadata(dictionary),
        IEnumerable<object?> list => list.Select(CopyValue).ToList(),
        System.Text.Json.JsonElement element => element.Clone(),
        _ => value
    };

    public bool IsEnabledFor(ProviderType provider) => provider switch
    {
        ProviderType.Canonical => true,
        ProviderType.Claude => Targets.Claude.Enabled,
        ProviderType.Gemini => Targets.Gemini.Enabled,
        ProviderType.Codex => Targets.Codex.Enabled,
        ProviderType.Cursor => Targets.Cursor.Enabled,
        ProviderType.Copilot => Targets.Copilot.Enabled,
        ProviderType.Windsurf => Targets.Windsurf.Enabled,
        ProviderType.Cline => Targets.Cline.Enabled,
        ProviderType.RooCode => Targets.RooCode.Enabled,
        _ => true
    };

    public void SetEnabledFor(ProviderType provider, bool enabled)
    {
        switch (provider)
        {
            case ProviderType.Claude:
                Targets.Claude.Enabled = enabled;
                break;
            case ProviderType.Gemini:
                Targets.Gemini.Enabled = enabled;
                break;
            case ProviderType.Codex:
                Targets.Codex.Enabled = enabled;
                break;
            case ProviderType.Cursor:
                Targets.Cursor.Enabled = enabled;
                break;
            case ProviderType.Copilot:
                Targets.Copilot.Enabled = enabled;
                break;
            case ProviderType.Windsurf:
                Targets.Windsurf.Enabled = enabled;
                break;
            case ProviderType.Cline:
                Targets.Cline.Enabled = enabled;
                break;
            case ProviderType.RooCode:
                Targets.RooCode.Enabled = enabled;
                break;
        }
    }
}

public sealed class SkillTargetsConfig
{
    [YamlMember(Alias = "claude")]
    [JsonPropertyName("claude")]
    public ClaudeTargetConfig Claude { get; set; } = new();

    [YamlMember(Alias = "gemini")]
    [JsonPropertyName("gemini")]
    public GeminiTargetConfig Gemini { get; set; } = new();

    [YamlMember(Alias = "codex")]
    [JsonPropertyName("codex")]
    public CodexTargetConfig Codex { get; set; } = new();

    [YamlMember(Alias = "cursor")]
    [JsonPropertyName("cursor")]
    public CursorTargetConfig Cursor { get; set; } = new();

    // Copilot and Windsurf load skills on demand, so they are on by default. Cline and Roo Code load every rule into
    // every conversation, so skills reach them only when the user turns them on.
    [YamlMember(Alias = "copilot")]
    [JsonPropertyName("copilot")]
    public SimpleTargetConfig Copilot { get; set; } = new() { Enabled = true };

    [YamlMember(Alias = "windsurf")]
    [JsonPropertyName("windsurf")]
    public SimpleTargetConfig Windsurf { get; set; } = new() { Enabled = true };

    [YamlMember(Alias = "cline")]
    [JsonPropertyName("cline")]
    public SimpleTargetConfig Cline { get; set; } = new() { Enabled = false };

    [YamlMember(Alias = "roocode")]
    [JsonPropertyName("roocode")]
    public SimpleTargetConfig RooCode { get; set; } = new() { Enabled = false };
}

public sealed class SimpleTargetConfig
{
    [YamlMember(Alias = "enabled")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class ClaudeTargetConfig
{
    [YamlMember(Alias = "enabled")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "allowed_tools")]
    [JsonPropertyName("allowed_tools")]
    public List<string> AllowedTools { get; set; } = [];

    [YamlMember(Alias = "model")]
    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

public sealed class GeminiTargetConfig
{
    [YamlMember(Alias = "enabled")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class CodexTargetConfig
{
    [YamlMember(Alias = "enabled")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class CursorTargetConfig
{
    [YamlMember(Alias = "enabled")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "globs")]
    [JsonPropertyName("globs")]
    public List<string> Globs { get; set; } = ["**/*"];

    [YamlMember(Alias = "always_apply")]
    [JsonPropertyName("always_apply")]
    public bool AlwaysApply { get; set; }
}
