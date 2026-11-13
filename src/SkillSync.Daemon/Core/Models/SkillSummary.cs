using System.Text.Json.Serialization;

namespace SkillSync.Daemon.Core.Models;

public sealed record SkillSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("when_to_use")] string WhenToUse,
    [property: JsonPropertyName("summary_short")] string SummaryShort,
    [property: JsonPropertyName("source_category")] string SourceCategory,
    [property: JsonPropertyName("origin_provider")] string OriginProvider,
    [property: JsonPropertyName("is_catalog_only")] bool IsCatalogOnly,
    [property: JsonPropertyName("catalog_id")] string? CatalogId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("targets")] SkillTargetsConfig Targets,
    [property: JsonPropertyName("destinations")] IReadOnlyList<SyncDestinationStatus> Destinations,
    [property: JsonPropertyName("instruction_match")] string? InstructionMatch);
