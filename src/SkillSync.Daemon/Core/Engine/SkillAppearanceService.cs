using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>How a skill's badge looks in SkillHub: a built-in icon, an uploaded image, or (no entry) its initials.</summary>
public sealed record SkillAppearance(
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

/// <summary>
/// Skill badges are SkillHub-only cosmetics, so they live in side files under <c>~/.skillhub/appearance/</c>:
/// <c>skill-icons.json</c> maps a skill key to its choice and uploaded images sit in <c>images/</c> under a content hash.
/// Nothing is written into the skill, its passthrough metadata or any AI tool folder, so semantic hashes and
/// upstream fingerprints (<c>import_hash</c>, <c>import_content_hash</c>) never change when a badge does.
/// </summary>
public sealed partial class SkillAppearanceService(ProviderDiscoveryService discovery)
{
    public const int MaxImageBytes = 256 * 1024;
    private const int MaxKeyLength = 200;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string Root => Path.Combine(discovery.SkillHubRoot, "appearance");
    private string IndexPath => Path.Combine(Root, "skill-icons.json");
    private string ImageRoot => Path.Combine(Root, "images");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IconName();

    /// <summary>Every customised badge, with uploaded images inlined as data URLs (the page's CSP allows <c>data:</c> images).</summary>
    public IReadOnlyDictionary<string, object> List()
    {
        lock (_gate)
        {
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var (key, entry) in Load())
            {
                if (entry.Icon is not null) result[key] = new { icon = entry.Icon, updated_at = entry.UpdatedAt };
                else if (entry.Image is not null && ReadImage(entry.Image) is { } url) result[key] = new { image = url, updated_at = entry.UpdatedAt };
            }
            return result;
        }
    }

    public SkillAppearance? Get(string key)
    {
        lock (_gate) return Load().GetValueOrDefault(RequireKey(key));
    }

    public SkillAppearance SetIcon(string key, string icon)
    {
        key = RequireKey(key);
        icon = (icon ?? string.Empty).Trim().ToLowerInvariant();
        if (!IconName().IsMatch(icon)) throw new ArgumentException("Choose one of the built-in icons.");
        lock (_gate) return Save(key, new SkillAppearance(icon, null, DateTimeOffset.UtcNow));
    }

    /// <summary>Stores a PNG, JPEG or WebP image (already resized by the app) and points the skill's badge at it.</summary>
    public SkillAppearance SetImage(string key, string data)
    {
        key = RequireKey(key);
        var bytes = DecodeImage(data, out var extension);
        var file = Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant() + extension;
        lock (_gate)
        {
            Directory.CreateDirectory(ImageRoot);
            var path = Path.Combine(ImageRoot, file);
            if (!File.Exists(path))
            {
                var temp = path + ".skillsync-tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            return Save(key, new SkillAppearance(null, file, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Back to the two-letter monogram. Returns false when the skill had no custom badge.</summary>
    public bool Reset(string key)
    {
        key = RequireKey(key);
        lock (_gate)
        {
            var index = Load();
            if (!index.Remove(key, out var previous)) return false;
            Write(index);
            DeleteUnused(previous.Image, index);
            return true;
        }
    }

    private SkillAppearance Save(string key, SkillAppearance entry)
    {
        var index = Load();
        index.TryGetValue(key, out var previous);
        index[key] = entry;
        Write(index);
        if (previous?.Image != entry.Image) DeleteUnused(previous?.Image, index);
        return entry;
    }

    private void DeleteUnused(string? image, Dictionary<string, SkillAppearance> index)
    {
        if (image is null || index.Values.Any(item => item.Image == image)) return;
        try { File.Delete(Path.Combine(ImageRoot, image)); } catch (IOException) { }
    }

    private Dictionary<string, SkillAppearance> Load()
    {
        try
        {
            if (File.Exists(IndexPath) &&
                JsonSerializer.Deserialize<Dictionary<string, SkillAppearance>>(File.ReadAllText(IndexPath)) is { } index)
                return new Dictionary<string, SkillAppearance>(index, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or JsonException) { }
        return new Dictionary<string, SkillAppearance>(StringComparer.Ordinal);
    }

    private void Write(Dictionary<string, SkillAppearance> index)
    {
        Directory.CreateDirectory(Root);
        var temp = IndexPath + ".skillsync-tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index, Json));
        File.Move(temp, IndexPath, overwrite: true);
    }

    private string? ReadImage(string file)
    {
        if (file.IndexOfAny(['/', '\\']) >= 0 || file.Contains("..", StringComparison.Ordinal)) return null;
        var path = Path.Combine(ImageRoot, file);
        try
        {
            var bytes = File.ReadAllBytes(path);
            return "data:" + MimeFor(file) + ";base64," + Convert.ToBase64String(bytes);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string MimeFor(string file) => Path.GetExtension(file) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        _ => "image/webp"
    };

    private static string RequireKey(string key)
    {
        key = (key ?? string.Empty).Trim();
        if (key.Length == 0 || key.Length > MaxKeyLength || key.Any(char.IsControl)) throw new ArgumentException("A skill name is required.");
        return key;
    }

    /// <summary>Accepts a data URL or bare base64 and checks the bytes really are PNG, JPEG or WebP.</summary>
    public static byte[] DecodeImage(string data, out string extension)
    {
        var text = (data ?? string.Empty).Trim();
        var comma = text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? text.IndexOf(',') : -1;
        if (comma >= 0) text = text[(comma + 1)..];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { throw new ArgumentException("The image could not be read. Choose a PNG, JPG or WebP file."); }
        if (bytes.Length == 0) throw new ArgumentException("The image is empty.");
        if (bytes.Length > MaxImageBytes) throw new ArgumentException($"The image is larger than {MaxImageBytes / 1024} KB after resizing.");
        extension = bytes switch
        {
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => ".png",
            [0xFF, 0xD8, 0xFF, ..] => ".jpg",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
            _ => throw new ArgumentException("Only PNG, JPG and WebP images can be used as a skill icon.")
        };
        return bytes;
    }
}
