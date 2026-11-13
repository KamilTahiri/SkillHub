using System.Text.Json.Serialization;

namespace SkillSync.Daemon.Core.Models;

public enum SyncChangeKind
{
    CreatedOrModified = 0,
    Deleted = 1
}

public sealed record SyncActivityNotification(
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("skill_slug")] string SkillSlug,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("sequence")] long Sequence
);



public sealed record SyncReconciliationReport(Guid OperationId, int ManagedSkills, int Projected,
    int SyncedDestinations, int ConflictedDestinations, int FailedDestinations, int MissingDestinations);
