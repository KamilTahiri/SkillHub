using System.Drawing;
using System.Security.Cryptography;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Desktop;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

/// <summary>Custom skill badges stay in SkillHub's side files, and one brand mark is used on every surface.</summary>
public sealed class SkillAppearanceAndBrandTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillhub-appearance-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IconsAndImagesNeverTouchTheSkillOrAnyAiToolFolder()
    {
        foreach (var tool in new[] { ".claude", ".codex", ".gemini", ".cursor", ".agents" }) Directory.CreateDirectory(Path.Combine(_root, tool));
        var discovery = new ProviderDiscoveryService(_root);
        await using var engine = new SyncEngine(discovery);
        await engine.CreateSkillAsync(new CanonicalSkillModel
        {
            Name = "imported-ui", Title = "Imported UI", Description = "From upstream", InstructionsMarkdown = "Body.",
            Passthrough = { ["import_source"] = "acme/skills", ["import_hash"] = "upstream-fingerprint", ["import_content_hash"] = "local-fingerprint" }
        }, "test");
        var before = Snapshot();
        var revision = engine.GetSkill("imported-ui")!.Revision;

        var appearance = new SkillAppearanceService(discovery);
        appearance.SetIcon("imported-ui", "terminal");
        Assert.Equal("terminal", appearance.Get("imported-ui")!.Icon);
        appearance.SetImage("imported-ui", "data:image/png;base64," + Convert.ToBase64String(Png));
        var stored = appearance.Get("imported-ui")!;
        Assert.Null(stored.Icon);
        Assert.EndsWith(".png", stored.Image);
        Assert.True(File.Exists(Path.Combine(appearance.Root, "images", stored.Image!)));
        var listed = Assert.Single(appearance.List());
        Assert.Contains("data:image/png;base64,", System.Text.Json.JsonSerializer.Serialize(listed.Value));

        // Only ~/.skillhub/appearance changed: the canonical skill, its fingerprints and every AI tool folder are byte-identical.
        var after = Snapshot();
        Assert.Equal(before, after);
        var skill = engine.GetSkill("imported-ui")!;
        Assert.Equal(revision, skill.Revision);
        Assert.Equal("upstream-fingerprint", skill.Passthrough["import_hash"]?.ToString());
        Assert.Equal("local-fingerprint", skill.Passthrough["import_content_hash"]?.ToString());
        Assert.False(skill.Passthrough.ContainsKey("icon"));

        Assert.True(appearance.Reset("imported-ui"));
        Assert.Null(appearance.Get("imported-ui"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(appearance.Root, "images")));
        Assert.False(appearance.Reset("imported-ui"));
    }

    [Fact]
    public void SharedImagesSurviveUntilTheLastSkillResets()
    {
        var appearance = new SkillAppearanceService(new ProviderDiscoveryService(_root));
        var data = Convert.ToBase64String(Png);
        var first = appearance.SetImage("one", data).Image!;
        Assert.Equal(first, appearance.SetImage("two", data).Image);
        appearance.Reset("one");
        Assert.True(File.Exists(Path.Combine(appearance.Root, "images", first)));
        appearance.SetIcon("two", "rocket");
        Assert.False(File.Exists(Path.Combine(appearance.Root, "images", first)));
    }

    [Theory]
    [InlineData("PHN2Zz48L3N2Zz4=")] // <svg></svg>: scripts could hide in SVG, so only raster formats are accepted.
    [InlineData("not base64 at all")]
    [InlineData("")]
    public void RejectsAnythingThatIsNotAPngJpegOrWebp(string data)
    {
        var appearance = new SkillAppearanceService(new ProviderDiscoveryService(_root));
        Assert.Throws<ArgumentException>(() => appearance.SetImage("skill", data));
        Assert.Throws<ArgumentException>(() => appearance.SetIcon("skill", "../escape"));
        Assert.Throws<ArgumentException>(() => appearance.SetImage("skill", Convert.ToBase64String(new byte[SkillAppearanceService.MaxImageBytes + 1])));
        Assert.Empty(appearance.List());
    }

    [Fact]
    public void TheSameBrandMarkIsCheckedInForEverySurface()
    {
        var repository = RepositoryRoot();
        var daemon = Path.Combine(repository, "src", "SkillSync.Daemon");
        var svg = SkillHubBrand.Svg();
        Assert.Equal(svg, File.ReadAllText(Path.Combine(daemon, "wwwroot", "img", "skillhub-logo.svg")).ReplaceLineEndings("\n"));
        Assert.Equal(svg, File.ReadAllText(Path.Combine(repository, "docs", "brand", "skillhub-logo.svg")).ReplaceLineEndings("\n"));

        var index = File.ReadAllText(Path.Combine(daemon, "wwwroot", "index.html"));
        Assert.Contains("<link rel=\"icon\" type=\"image/svg+xml\" href=\"/img/skillhub-logo.svg\">", index);
        Assert.Contains("class=\"brand-logo\" src=\"/img/skillhub-logo.svg\"", index);
        Assert.DoesNotContain("brand-mark", index);
        Assert.Contains("docs/brand/skillhub-logo.svg", File.ReadAllText(Path.Combine(repository, "README.md")));
        Assert.Contains(@"SetupIconFile={#Payload}\skillhub-v2.ico", File.ReadAllText(Path.Combine(repository, "installer", "SkillHub.iss")));
        Assert.Contains("<ApplicationIcon Condition=\"Exists('skillhub-v2.ico')\">skillhub-v2.ico</ApplicationIcon>",
            File.ReadAllText(Path.Combine(daemon, "SkillSync.Daemon.csproj")));

        // The checked-in .ico holds the hub mark at 16-256 px: a white hub in the middle of the gradient tile.
        var frames = IconFrames(File.ReadAllBytes(Path.Combine(daemon, "skillhub-v2.ico")));
        Assert.Equal([16, 32, 48, 64, 256], frames.Keys.Order().ToArray());
        using var large = new Bitmap(new MemoryStream(frames[256]));
        AssertWhite(large.GetPixel(128, 128));
        var (nodeX, nodeY) = SkillHubBrand.Node(0);
        AssertWhite(large.GetPixel((int)(nodeX * 4), (int)(nodeY * 4)));
        var tile = large.GetPixel(30, 128);
        Assert.True(tile.A == 255 && tile.B > tile.G + 60, "the tile should be indigo/violet, was " + tile);
        Assert.Equal(0, large.GetPixel(0, 0).A);
        using var rendered = SkillHubIconRenderer.RenderHubBitmap(256);
        AssertWhite(rendered.GetPixel(128, 128));
    }

    private static void AssertWhite(Color color) => Assert.True(color.R > 240 && color.G > 240 && color.B > 240 && color.A > 240, "expected white, was " + color);

    private static Dictionary<int, byte[]> IconFrames(byte[] ico)
    {
        var count = BitConverter.ToUInt16(ico, 4);
        var frames = new Dictionary<int, byte[]>();
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + 16 * index;
            var size = ico[entry] == 0 ? 256 : ico[entry];
            var length = (int)BitConverter.ToUInt32(ico, entry + 8);
            var offset = (int)BitConverter.ToUInt32(ico, entry + 12);
            frames[size] = ico[offset..(offset + length)];
        }
        return frames;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SkillSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("SkillSync.sln was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Every file outside ~/.skillhub/appearance with its content hash.</summary>
    private Dictionary<string, string> Snapshot()
    {
        var appearance = Path.Combine(_root, ".skillhub", "appearance") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(appearance, StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(_root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
