using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>Revision history: list and read snapshots of a managed skill, and restore one as a new revision.</summary>
public sealed partial class SyncEngine
{
    public IReadOnlyList<SkillRevisionInfo> ListRevisions(string name) => _history.List(RequireSlug(name));

    public SkillRevisionSnapshot? GetRevision(string name, string id) => _history.Get(RequireSlug(name), id);

    /// <summary>Restores a saved revision. The current state is already in history, so a restore can itself be undone.</summary>
    public Task<CanonicalSkillModel> RestoreRevisionAsync(string name, string id, long? expectedRevision, CancellationToken ct = default)
    {
        var slug = RequireSlug(name);
        var snapshot = _history.Get(slug, id) ?? throw new KeyNotFoundException("That revision is no longer in history.");
        var restored = _transpilers[ProviderType.Canonical].MergeInbound(slug, snapshot.Markdown, null);
        return EditSkillAsync(slug, current =>
        {
            current.Title = restored.Title;
            current.Description = restored.Description;
            current.WhenToUse = restored.WhenToUse;
            current.SummaryShort = restored.SummaryShort;
            current.InstructionsMarkdown = restored.InstructionsMarkdown;
            current.Tags = restored.Tags;
            current.Targets = restored.Targets;
            current.Passthrough = restored.Passthrough;
            current.SupportingFiles = new Dictionary<string, string>(snapshot.Files, StringComparer.Ordinal);
        }, "webui:history-restore", ct, expectedRevision);
    }
}
