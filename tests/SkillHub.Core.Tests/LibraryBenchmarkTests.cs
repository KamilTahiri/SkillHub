using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Watchers;
using Xunit;
using Xunit.Abstractions;

namespace SkillHub.Core.Tests;

public sealed class LibraryBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task CatalogBaseline()
    {
        if (Environment.GetEnvironmentVariable("SKILLHUB_BENCHMARK") != "1") return;

        foreach (var count in new[] { 500, 5000 })
        {
            var root = Path.Combine(Path.GetTempPath(), "skillhub-benchmark-" + Guid.NewGuid().ToString("N"));
            try
            {
                var body = string.Join('\n', Enumerable.Repeat("Use this fixture to validate a representative instruction body.", 36));
                for (var index = 0; index < count; index++)
                {
                    var slug = $"fixture-{index:D5}";
                    var directory = Path.Combine(root, ".codex", "plugins", "cache", "openai-bundled", "fixture-plugin", "1.0.0", "skills", slug);
                    Directory.CreateDirectory(Path.Combine(directory, "references"));
                    await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"),
                        $"---\nname: {slug}\ndescription: Benchmark catalog entry {index}\n---\n{body}");
                    await File.WriteAllTextAsync(Path.Combine(directory, "references", "guide.md"), body);
                }
                var before = GC.GetTotalMemory(true);
                var start = Stopwatch.StartNew();
                await using var engine = new SyncEngine(new ProviderDiscoveryService(root));
                var rows = engine.ListSkillSummaries();
                var cold = start.ElapsedMilliseconds;
                Assert.Equal(count, rows.Count);
                var warm = new List<double>();
                for (var repeat = 0; repeat < 25; repeat++)
                {
                    start.Restart();
                    Assert.NotEmpty(engine.ListSkillSummaries(query: "fixture-000"));
                    warm.Add(start.Elapsed.TotalMilliseconds);
                }
                warm.Sort();
                var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(rows));
                var heap = GC.GetTotalMemory(false) - before;
                output.WriteLine($"{count} catalog rows: cold {cold} ms, warm query p95 {warm[23]:F1} ms, list {bytes} bytes, managed heap delta {heap} bytes");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
