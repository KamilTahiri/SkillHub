using System.Text.Json.Serialization;

namespace SkillSync.Daemon.Core.Models;

public sealed class SyncLedger
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("skills")]
    public Dictionary<string, SkillLedgerEntry> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SkillLedgerEntry
{
    [JsonPropertyName("canonicalSemanticHash")]
    public string CanonicalSemanticHash { get; set; } = string.Empty;

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastUpdatedBy")]
    public string LastUpdatedBy { get; set; } = "system";

    [JsonPropertyName("lastSynthesizedGeminiDescription")]
    public string? LastSynthesizedGeminiDescription { get; set; }

    [JsonPropertyName("lastSynthesizedCodexDescription")]
    public string? LastSynthesizedCodexDescription { get; set; }

    [JsonPropertyName("lastSynthesizedCursorDescription")]
    public string? LastSynthesizedCursorDescription { get; set; }

    [JsonPropertyName("targets")]
    public Dictionary<string, TargetFileLedger> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TargetFileLedger
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("lastWrittenRawHash")]
    public string LastWrittenRawHash { get; set; } = string.Empty;

    [JsonPropertyName("lastWriteTimestampUtc")]
    public DateTimeOffset LastWriteTimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

