using Microsoft.Extensions.Logging.Abstractions;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;
using System.Text.Json;
using Xunit;

namespace SkillSync.Tests;

public sealed class SyncSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skillhub-safety-{Guid.NewGuid():N}");

    private ProviderDiscoveryService CreateDiscovery(bool mirrors = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "skills"));
        Directory.CreateDirectory(Path.Combine(_root, ".gemini", "skills"));
        if (mirrors)
        {
            Directory.CreateDirectory(Path.Combine(_root, ".codex", "skills"));
            Directory.CreateDirectory(Path.Combine(_root, ".gemini", "antigravity"));
        }
        return new ProviderDiscoveryService(_root);
    }

    private static CanonicalSkillModel Fixture(string name = "safety-probe") => new()
    {
        Name = name,
        Title = "Safety Probe",
        Description = "Test filesystem safety.",
        InstructionsMarkdown = "# Original\n\nOriginal instructions.\n",
        SupportingFiles = new Dictionary<string, string>
        {
            ["scripts/helper.txt"] = "initial helper",
            ["references/guide.md"] = "# Reference\n\nInitial guide.",
            ["assets/asset.txt"] = "initial asset",
            ["agents/settings.yaml"] = "name: example"
        }
    };

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    [Fact]
    public async Task DisableProvider_WithLiveWatcher_WithdrawsOnlyItsOwnedFiles()
    {
        var discovery = CreateDiscovery();
        await using var engine = new SyncEngine(discovery);
        var original = await engine.CreateSkillAsync(Fixture(), "test");
        using var watcher = new MultiRootFileWatcher(discovery, engine, NullLogger<MultiRootFileWatcher>.Instance, 40);
        watcher.StartWatchers();
        var geminiPath = discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, original.Name);
        var claudePath = discovery.GetSkillPrimaryFilePath(ProviderType.Claude, original.Name);
        var unrelated = Path.Combine(Path.GetDirectoryName(geminiPath)!, "untouched.txt");
        await File.WriteAllTextAsync(unrelated, "unowned");

        var updated = await engine.SetProviderAsync(original.Name, ProviderType.Gemini, false, "test");

        var claudeGuide = Path.Combine(Path.GetDirectoryName(claudePath)!, "references", "guide.md");
        await File.WriteAllTextAsync(claudeGuide, "the watcher is still processing");
        await UntilAsync(() => engine.GetSkill(original.Name)?.SupportingFiles["references/guide.md"] == "the watcher is still processing");

        Assert.True(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Canonical, original.Name)));
        Assert.True(File.Exists(claudePath));
        Assert.False(File.Exists(geminiPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(geminiPath)!, "assets", "asset.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(geminiPath)!, "agents", "settings.yaml")));
        Assert.Equal("unowned", await File.ReadAllTextAsync(unrelated));
        Assert.Contains(updated.Destinations, destination => destination.Provider == "gemini" && destination.State == "withdrawn");
    }

    [Fact]
    public async Task LiveWatcher_ReconcilesMarkdownCompanionsAndMarksDeletedProjectionMissing()
    {
        var discovery = CreateDiscovery();
        await using var engine = new SyncEngine(discovery);
        var original = await engine.CreateSkillAsync(Fixture(), "test");
        using var watcher = new MultiRootFileWatcher(discovery, engine, NullLogger<MultiRootFileWatcher>.Instance, 40);
        watcher.StartWatchers();
        var geminiPath = discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, original.Name);
        var guide = Path.Combine(Path.GetDirectoryName(geminiPath)!, "references", "guide.md");

        await File.WriteAllTextAsync(guide, "# Guide change\n\nThe companion is not the skill body.");
        await UntilAsync(() => engine.GetSkill(original.Name)?.SupportingFiles.GetValueOrDefault("references/guide.md")?.Contains("Guide change") == true);
        Assert.Equal(original.InstructionsMarkdown, engine.GetSkill(original.Name)!.InstructionsMarkdown);

        File.Delete(guide);
        var claudePath = discovery.GetSkillPrimaryFilePath(ProviderType.Claude, original.Name);
        await UntilAsync(() => engine.GetSkill(original.Name)?.SupportingFiles.ContainsKey("references/guide.md") == false &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(claudePath)!, "references", "guide.md")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(claudePath)!, "references", "guide.md")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(claudePath)!, "scripts", "helper.txt")));

        File.Delete(geminiPath);
        await UntilAsync(() => engine.GetSkill(original.Name)?.Destinations.Any(destination =>
            destination.Provider == "gemini" && destination.State == "missing") == true);
        Assert.True(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Canonical, original.Name)));
        Assert.True(File.Exists(claudePath));
        await engine.SynchronizeAllAsync();
        Assert.False(File.Exists(geminiPath));
        await engine.RepairProviderAsync(original.Name, ProviderType.Gemini);
        Assert.True(File.Exists(geminiPath));
    }

    [Fact]
    public async Task SkillNamedMarkdownInsideReferences_IsStillACompanion()
    {
        var discovery = CreateDiscovery();
        await using var engine = new SyncEngine(discovery);
        var model = Fixture();
        model.SupportingFiles["references/SKILL.md"] = "Reference with a reserved primary basename";
        await engine.CreateSkillAsync(model, "test");

        await engine.SynchronizeAllAsync();

        Assert.Single(engine.ListSkills());
        Assert.Contains("references/SKILL.md", engine.GetSkill(model.Name)!.SupportingFiles.Keys);
        Assert.Null(engine.GetSkill("references"));
    }

    [Fact]
    public async Task ExplicitEmptyManifest_DeletesFinalFileFromEveryOwnedBundle()
    {
        var discovery = CreateDiscovery();
        await using var engine = new SyncEngine(discovery);
        var model = Fixture();
        model.SupportingFiles = new Dictionary<string, string> { ["agents/setup.txt"] = "the only companion" };
        await engine.CreateSkillAsync(model, "test");

        await engine.EditSkillAsync(model.Name, current => current.SupportingFiles.Clear(), "test");
        Assert.Empty(engine.GetSkill(model.Name)!.SupportingFiles);
        foreach (var provider in new[] { ProviderType.Canonical, ProviderType.Claude, ProviderType.Gemini })
        {
            var primary = discovery.GetSkillPrimaryFilePath(provider, model.Name);
            Assert.True(File.Exists(primary));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(primary)!, "agents", "setup.txt")));
        }
    }

    [Theory]
    [InlineData("scripts/../../sentinel.txt")]
    [InlineData("scripts/CON.txt")]
    [InlineData("assets/LPT¹")]
    [InlineData("scripts/a.txt:payload")]
    [InlineData("/scripts/file.txt")]
    [InlineData("C:/scripts/file.txt")]
    [InlineData("scripts/subdir/../file.txt")]
    [InlineData("scripts/subdir /file.txt")]
    public async Task Engine_RejectsUnsafeSupportingPathsBeforeCreatingSkill(string path)
    {
        var discovery = CreateDiscovery();
        await using var engine = new SyncEngine(discovery);
        var model = Fixture();
        model.SupportingFiles[path] = "unexpected write";

        await Assert.ThrowsAsync<ArgumentException>(async () => await engine.CreateSkillAsync(model, "test"));
        Assert.Null(engine.GetSkill(model.Name));
        Assert.False(File.Exists(Path.Combine(discovery.CanonicalSkillsRoot, "sentinel.txt")));
        Assert.False(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Canonical, model.Name)));
    }

    [Fact]
    public async Task OfflineEdit_FromSecondCodexRoot_IsIngestedAndMirroredAfterRestart()
    {
        var discovery = CreateDiscovery(mirrors: true);
        var model = Fixture();
        await using (var first = new SyncEngine(discovery))
        {
            await first.CreateSkillAsync(model, "test");
        }
        var agents = Path.Combine(_root, ".agents", "skills", model.Name, "SKILL.md");
        var codex = Path.Combine(_root, ".codex", "skills", model.Name, "SKILL.md");
        var antigravity = Path.Combine(_root, ".gemini", "antigravity", "skills", model.Name, "SKILL.md");
        await File.WriteAllTextAsync(agents, (await File.ReadAllTextAsync(agents)).Replace("Original instructions.", "Offline Agents edit."));

        await using var restarted = new SyncEngine(discovery);
        await restarted.SynchronizeAllAsync();
        var saved = Assert.IsType<CanonicalSkillModel>(restarted.GetSkill(model.Name));
        Assert.Contains("Offline Agents edit.", saved.InstructionsMarkdown);
        Assert.Contains("Offline Agents edit.", await File.ReadAllTextAsync(codex));
        Assert.Contains("Offline Agents edit.", await File.ReadAllTextAsync(antigravity));
        Assert.Equal(2, saved.Destinations.Count(destination => destination.Provider == "codex"));
        Assert.Equal(2, saved.Destinations.Count(destination => destination.Provider == "gemini"));
        var ledger = await File.ReadAllTextAsync(discovery.LedgerFilePath);
        await restarted.SynchronizeAllAsync();
        Assert.Equal(ledger, await File.ReadAllTextAsync(discovery.LedgerFilePath));
    }

    [Fact]
    public async Task TwoOfflineEdits_PreserveBothProviderCopiesAndCanonical()
    {
        var discovery = CreateDiscovery(mirrors: true);
        await using var engine = new SyncEngine(discovery);
        var model = await engine.CreateSkillAsync(Fixture(), "test");
        var paths = discovery.GetAllCodexSkillDirectories(model.Name).Select(directory => Path.Combine(directory, "SKILL.md")).ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            await File.WriteAllTextAsync(paths[index], (await File.ReadAllTextAsync(paths[index])).Replace("Original instructions.", $"Provider edit {index}."));
        }

        await engine.SynchronizeAllAsync();

        var current = engine.GetSkill(model.Name)!;
        Assert.Contains("Original instructions.", current.InstructionsMarkdown);
        Assert.Contains("Provider edit 0.", await File.ReadAllTextAsync(paths[0]));
        Assert.Contains("Provider edit 1.", await File.ReadAllTextAsync(paths[1]));
        Assert.Equal(2, current.Destinations.Count(target => target.Provider == "codex" && target.State == "conflict"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(discovery.ConflictsRoot, model.Name), "*.json").Length);
    }

    [Fact]
    public async Task SerializedPatches_ComposeAndRejectStaleFullReplacement()
    {
        var discovery = CreateDiscovery();
        await using var engine = new SyncEngine(discovery);
        var baseline = await engine.CreateSkillAsync(Fixture(), "test");

        await Task.WhenAll(
            engine.SetProviderAsync(baseline.Name, ProviderType.Gemini, false, "test"),
            engine.EditSkillAsync(baseline.Name, current => current.Tags.Add("review"), "test"));

        var updated = engine.GetSkill(baseline.Name)!;
        Assert.False(updated.Targets.Gemini.Enabled);
        Assert.Contains("review", updated.Tags);
        await Assert.ThrowsAsync<SkillConflictException>(() =>
            engine.EditSkillAsync(baseline.Name, current => current.Description = "stale", "test", expectedRevision: baseline.Revision));
    }

    [Fact]
    public async Task InterruptedCanonicalOperation_ReplaysAnAlreadyWrittenCompanion()
    {
        var discovery = CreateDiscovery();
        CanonicalSkillModel saved;
        await using (var engine = new SyncEngine(discovery))
        {
            saved = await engine.CreateSkillAsync(Fixture(), "test");
        }
        var ledger = JsonSerializer.Deserialize<SyncLedger>(await File.ReadAllTextAsync(discovery.LedgerFilePath))!;
        var entry = ledger.Skills[saved.Name];
        var model = saved.Clone();
        model.InstructionsMarkdown = "# Recovered\n\nResume the durable revision.\n";
        model.SupportingFiles["references/guide.md"] = "already staged";
        var expected = new SkillSync.Daemon.Core.Transpilers.CanonicalTranspiler().TranspileOutbound(model);
        entry.PendingCanonical = new PendingCanonicalWrite(entry.CanonicalDiskHash,
            ContentHasher.ComputeFileRevisionHash(expected), entry.Revision + 1, model, entry.CanonicalFiles);
        await File.WriteAllTextAsync(discovery.LedgerFilePath, JsonSerializer.Serialize(ledger));
        await File.WriteAllTextAsync(Path.Combine(discovery.GetSkillDirectoryPath(ProviderType.Canonical, saved.Name)!, "references", "guide.md"), "already staged");

        await using var restarted = new SyncEngine(discovery);
        await restarted.SynchronizeAllAsync();
        var current = restarted.GetSkill(saved.Name)!;
        Assert.Equal(model.InstructionsMarkdown, current.InstructionsMarkdown);
        Assert.Equal("already staged", current.SupportingFiles["references/guide.md"]);
        Assert.Equal(saved.Revision + 1, current.Revision);
    }

    [Fact]
    public async Task CatalogCollisions_KeepDistinctReadOnlyIdsAndEvictRemovedEntries()
    {
        var discovery = CreateDiscovery(mirrors: true);
        var roots = new[] { "vendor-a", "vendor-b" }.Select(plugin => Path.Combine(_root, ".codex", ".tmp", "plugins", "plugins", plugin, "skills", "shared")).ToArray();
        foreach (var root in roots)
        {
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            await File.WriteAllTextAsync(Path.Combine(root, "SKILL.md"), "---\nname: shared\ndescription: catalog fixture\n---\n\nUnique body for catalog search.");
            await File.WriteAllTextAsync(Path.Combine(root, "assets", "fixture.txt"), "catalog companion");
        }
        await using var engine = new SyncEngine(discovery);

        var results = engine.ListSkillSummaries(query: "unique body", category: "plugin");
        Assert.Equal(2, results.Count);
        Assert.NotEqual(results[0].CatalogId, results[1].CatalogId);
        Assert.All(results, result => Assert.True(result.IsCatalogOnly));
        Assert.False(File.Exists(discovery.LedgerFilePath));
        var detail = engine.GetCatalogSkill(results[0].CatalogId!)!;
        Assert.Equal("catalog companion", detail.SupportingFiles["assets/fixture.txt"]);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SetProviderAsync(detail.Name, ProviderType.Gemini, true, "test"));
        var clone = await engine.MaterializeCatalogSkillAsync(detail.CatalogId!, null, false, "test");
        Assert.Equal("shared-copy", clone.Name);
        Assert.False(clone.Targets.Gemini.Enabled);
        File.Delete(Path.Combine(roots[1], "SKILL.md"));
        await engine.SynchronizeAllAsync();
        Assert.Single(engine.ListSkillSummaries(category: "plugin"));
    }

    [Fact]
    public async Task DeletedSkill_StaysInTrashAfterRestartAndCanBeRestored()
    {
        var discovery = CreateDiscovery();
        var model = Fixture();
        await using (var engine = new SyncEngine(discovery))
        {
            await engine.CreateSkillAsync(model, "test");
            Assert.True(await engine.DeleteSkillDirectAsync(model.Name));
        }
        await using var restarted = new SyncEngine(discovery);
        await restarted.SynchronizeAllAsync();
        Assert.Null(restarted.GetSkill(model.Name));
        var restored = await restarted.RestoreSkillAsync(model.Name);
        Assert.Equal(model.InstructionsMarkdown, restored.InstructionsMarkdown);
        Assert.Equal(model.SupportingFiles.Count, restored.SupportingFiles.Count);
        Assert.True(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, model.Name)));
    }


    [Fact]
    public async Task Engine_RejectsBundleJunctionBeforeWritingCanonical()
    {
        var discovery = CreateDiscovery();
        await using var engine = new SyncEngine(discovery);
        var model = Fixture();
        var bundle = discovery.GetSkillDirectoryPath(ProviderType.Canonical, model.Name)!;
        var outside = Path.Combine(_root, "outside");
        var junction = Path.Combine(bundle, "scripts");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(outside);

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{outside}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());

        try
        {
            await Assert.ThrowsAsync<IOException>(() => engine.CreateSkillAsync(model, "test"));
            Assert.False(File.Exists(Path.Combine(outside, "helper.txt")));
            Assert.False(File.Exists(Path.Combine(bundle, "SKILL.md")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task StoreLock_RejectsCompetingWriterAndReleasesOnShutdown()
    {
        var discovery = CreateDiscovery();
        await using (var engine = new SyncEngine(discovery))
        {
            Assert.Throws<IOException>(() => new SyncEngine(discovery));
        }
        await using var restarted = new SyncEngine(discovery);
        Assert.Empty(restarted.ListSkills());
    }

    [Fact]
    public async Task V1Migration_PreservesUnknownBundlesAndUntouchedLegacyStore()
    {
        var discovery = CreateDiscovery();
        var model = Fixture();
        var legacy = Path.Combine(_root, ".skillsync", "skills", model.Name);
        Directory.CreateDirectory(legacy);
        var document = new SkillSync.Daemon.Core.Transpilers.CanonicalTranspiler().TranspileOutbound(model);
        await File.WriteAllTextAsync(Path.Combine(legacy, "SKILL.md"), document);
        Directory.CreateDirectory(discovery.SkillHubRoot);
        var gemini = discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, model.Name);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(gemini)!, "scripts"));
        var projection = new SkillSync.Daemon.Core.Transpilers.GeminiTranspiler().TranspileOutbound(model);
        await File.WriteAllTextAsync(gemini, projection);
        var unknown = Path.Combine(Path.GetDirectoryName(gemini)!, "scripts", "unowned.txt");
        await File.WriteAllTextAsync(unknown, "not tracked by the v1 ledger");

        var ledger = new SyncLedger { Version = 1 };
        ledger.Skills[model.Name] = new SkillLedgerEntry();
        ledger.Skills[model.Name].Targets["gemini"] = new TargetFileLedger
        {
            Path = gemini,
            LastWrittenRawHash = ContentHasher.ComputeRawFileHash(projection)
        };
        await File.WriteAllTextAsync(discovery.LedgerFilePath, JsonSerializer.Serialize(ledger));

        await using var engine = new SyncEngine(discovery);
        await engine.SynchronizeAllAsync();
        await engine.SetProviderAsync(model.Name, ProviderType.Gemini, false, "test");

        Assert.False(File.Exists(gemini));
        Assert.Equal("not tracked by the v1 ledger", await File.ReadAllTextAsync(unknown));
        Assert.Equal(document, await File.ReadAllTextAsync(Path.Combine(legacy, "SKILL.md")));
        Assert.Equal(2, JsonSerializer.Deserialize<SyncLedger>(await File.ReadAllTextAsync(discovery.LedgerFilePath))!.Version);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
