using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillHub.Core.Tests;

public sealed class StoreMigrationTests
{
    [Fact]
    public async Task LegacyStoreCopiesSkillsOnceAndKeepsTheOriginal()
    {
        var home = Path.Combine(Path.GetTempPath(), "skillhub-migration-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(home, ".skillsync", "skills", "legacy-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        await File.WriteAllTextAsync(original, "---\nname: legacy-skill\ndescription: A legacy skill\n---\nOriginal body.\n");
        try
        {
            var discovery = new ProviderDiscoveryService(home);
            await using (var engine = new SyncEngine(discovery))
            {
                await engine.SynchronizeAllAsync();
                Assert.Contains("Original body.", engine.GetSkill("legacy-skill")!.InstructionsMarkdown);
                Assert.True(File.Exists(original));
            }

            await File.AppendAllTextAsync(original, "Changed legacy source.\n");
            await using var restarted = new SyncEngine(discovery);
            await restarted.SynchronizeAllAsync();
            Assert.DoesNotContain("Changed legacy source.", restarted.GetSkill("legacy-skill")!.InstructionsMarkdown);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
