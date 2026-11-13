using System.Text.Json.Serialization;
using SkillSync.Daemon.Core.Engine;

namespace SkillSync.Daemon.Core.Models;

public sealed class SyncLedger
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("skills")]
    public Dictionary<string, SkillLedgerEntry> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SkillLedgerEntry
{
    [JsonPropertyName("canonicalSemanticHash")]
    public string CanonicalSemanticHash { get; set; } = string.Empty;

    public string? CanonicalDiskHash { get; set; }
    public long Revision { get; set; }
    public Dictionary<string, string> CanonicalFiles { get; set; } = new(SkillPaths.Comparer);
    public string? Tombstone { get; set; }
    public bool Deleted { get; set; }
    public string? CatalogId { get; set; }

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
    public Dictionary<string, TargetFileLedger> Targets { get; set; } = new(SkillPaths.Comparer);

    public PendingCanonicalWrite? PendingCanonical { get; set; }
}

public sealed record PendingCanonicalWrite(string? PreviousHash, string ExpectedHash, long Revision, CanonicalSkillModel Model, Dictionary<string, string> PreviousFiles);

public sealed class TargetFileLedger
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("lastWrittenRawHash")]
    public string LastWrittenRawHash { get; set; } = string.Empty;

    [JsonPropertyName("lastWriteTimestampUtc")]
    public DateTimeOffset LastWriteTimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Provider { get; set; } = string.Empty;
    public bool Owned { get; set; }
    public bool Legacy { get; set; }
    public string State { get; set; } = "pending";
    public long AppliedRevision { get; set; }
    public Dictionary<string, string> Files { get; set; } = new(SkillPaths.Comparer);
    public Dictionary<string, string> OwnedFiles { get; set; } = new(SkillPaths.Comparer);
    public PendingProjection? Pending { get; set; }
    public string? ConflictHash { get; set; }
    public string? Error { get; set; }
    public string? AppliedDescription { get; set; }
}

public sealed record PendingProjection(string? RawHash, Dictionary<string, string> Files, bool Withdraw, Dictionary<string, string>? OwnedFiles = null, string? PreviousRawHash = null);

public sealed record SyncDestinationStatus(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("applied_revision")] long AppliedRevision,
    [property: JsonPropertyName("error")] string? Error);

