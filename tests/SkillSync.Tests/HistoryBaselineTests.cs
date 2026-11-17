using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>The first edit of a skill that has no History entries keeps its previous version.</summary>
public sealed class HistoryBaselineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FirstEditOfASkillWithoutHistoryKeepsItsBaseline()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        await engine.CreateSkillAsync(new CanonicalSkillModel
        {
            Name = "legacy", Title = "Legacy", Description = "An old skill", InstructionsMarkdown = "Original body.",
            SupportingFiles = { ["references/notes.md"] = "Original notes." }
        }, "test");

        // A skill written before History existed has no snapshots at all.
        var history = Path.Combine(discovery.SkillHubRoot, "history", "legacy");
        Directory.Delete(history, recursive: true);
        Assert.Empty(engine.ListRevisions("legacy"));

        var current = engine.GetSkill("legacy")!;
        current.InstructionsMarkdown = "Edited body.";
        await engine.UpsertSkillDirectAsync(current, "test");

        var revisions = engine.ListRevisions("legacy");
        Assert.Equal([2L, 1L], revisions.Select(item => item.Revision).ToArray());
        var baseline = engine.GetRevision("legacy", revisions[1].Id)!;
        Assert.Contains("Original body.", baseline.Markdown);
        Assert.Equal("Original notes.", baseline.Files["references/notes.md"]);
        Assert.Contains("Edited body.", engine.GetRevision("legacy", revisions[0].Id)!.Markdown);

        // The baseline can be restored, and later edits do not duplicate it.
        var again = engine.GetSkill("legacy")!;
        again.InstructionsMarkdown = "Third body.";
        await engine.UpsertSkillDirectAsync(again, "test");
        Assert.Equal([3L, 2L, 1L], engine.ListRevisions("legacy").Select(item => item.Revision).ToArray());
        var restored = await engine.RestoreRevisionAsync("legacy", baseline.Id, engine.GetSkill("legacy")!.Revision);
        Assert.Contains("Original body.", restored.InstructionsMarkdown);
    }

    [Fact]
    public async Task ProposalAppliedToASkillWithoutHistoryKeepsItsBaseline()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        await engine.CreateSkillAsync(new CanonicalSkillModel { Name = "runtime", Title = "Runtime", Description = "Runs things", InstructionsMarkdown = "Step one." }, "test");
        Directory.Delete(Path.Combine(discovery.SkillHubRoot, "history", "runtime"), recursive: true);

        var proposals = new SkillProposalService(engine);
        var proposal = await proposals.ProposeAsync("runtime", "Add step two", "Step one.\nStep two.", null, null, null, "test");
        await proposals.ApplyAsync(proposal.Id);

        var revisions = engine.ListRevisions("runtime");
        Assert.Equal(2, revisions.Count);
        Assert.Contains("Step one.", engine.GetRevision("runtime", revisions[1].Id)!.Markdown);
        Assert.DoesNotContain("Step two.", engine.GetRevision("runtime", revisions[1].Id)!.Markdown);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
