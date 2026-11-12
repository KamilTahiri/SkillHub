namespace SkillSync.Daemon.Core.Models;

public enum SyncChangeKind
{
    CreatedOrModified = 0,
    Deleted = 1,
    FullResync = 2
}

public sealed record SyncEvent(
    ProviderType SourceProvider,
    string SkillSlug,
    SyncChangeKind ChangeKind,
    string? FullPath,
    string Originator,
    DateTimeOffset TimestampUtc,
    CanonicalSkillModel? DirectModelOverride = null,
    TaskCompletionSource<CanonicalSkillModel?>? CompletionSource = null
);

public sealed record SyncActivityNotification(
    DateTimeOffset TimestampUtc,
    string SkillSlug,
    string Source,
    string Action,
    string Details
);

