using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkillSync.Daemon.Core.Engine;

public sealed record SkillRevisionInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("saved_at")] DateTimeOffset SavedAt,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("bytes")] long Bytes);

public sealed record SkillRevisionSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("saved_at")] DateTimeOffset SavedAt,
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("files")] Dictionary<string, string> Files,
    [property: JsonPropertyName("hash")] string Hash);

/// <summary>
/// Plain-file revision history: one JSON snapshot per canonical write under <c>~/.skillhub/history/&lt;slug&gt;/</c>,
/// newest 30 kept. Consecutive identical states are stored once.
/// </summary>
public sealed class SkillHistory(string root)
{
    public const int MaxRevisions = 30;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _gate = new();

    private string Directory(string slug)
    {
        SkillPaths.ValidateSegment(slug);
        return Path.Combine(root, slug);
    }

    public static string HashOf(string markdown, IReadOnlyDictionary<string, string> files)
    {
        var builder = new StringBuilder(markdown.Replace("\r\n", "\n"));
        foreach (var (path, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append('\0').Append(path).Append('\0').Append(content.Replace("\r\n", "\n"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public void Record(string slug, long revision, string markdown, IReadOnlyDictionary<string, string> files)
    {
        lock (_gate)
        {
            var directory = Directory(slug);
            System.IO.Directory.CreateDirectory(directory);
            var hash = HashOf(markdown, files);
            var latest = List(slug).FirstOrDefault();
            if (latest is not null && Get(slug, latest.Id)?.Hash == hash) return;
            var now = DateTimeOffset.UtcNow;
            var id = $"{now:yyyyMMddHHmmssfff}-r{revision}";
            var snapshot = new SkillRevisionSnapshot(id, revision, now, markdown, new Dictionary<string, string>(files, StringComparer.Ordinal), hash);
            var target = Path.Combine(directory, id + ".json");
            var temporary = target + ".skillsync-tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Json), new UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);
            foreach (var stale in List(slug).Skip(MaxRevisions)) File.Delete(Path.Combine(directory, stale.Id + ".json"));
        }
    }

    /// <summary>
    /// Saves the version that is about to be replaced when history does not have it yet (for example a skill written before
    /// history existed, or edited outside SkillHub). Skipped when any stored snapshot already has this revision or content.
    /// Returns true when a baseline was written.
    /// </summary>
    public bool RecordBaseline(string slug, long revision, string markdown, IReadOnlyDictionary<string, string> files)
    {
        lock (_gate)
        {
            var hash = HashOf(markdown, files);
            foreach (var item in List(slug))
            {
                if (item.Revision == revision && revision > 0) return false;
                if (Get(slug, item.Id)?.Hash == hash) return false;
            }
            var directory = Directory(slug);
            System.IO.Directory.CreateDirectory(directory);
            // A millisecond earlier than "now" keeps the baseline ordered before the revision written right after it.
            var at = DateTimeOffset.UtcNow.AddMilliseconds(-1);
            var id = $"{at:yyyyMMddHHmmssfff}-r{revision}";
            var snapshot = new SkillRevisionSnapshot(id, revision, at, markdown, new Dictionary<string, string>(files, StringComparer.Ordinal), hash);
            var target = Path.Combine(directory, id + ".json");
            var temporary = target + ".skillsync-tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Json), new UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);
            return true;
        }
    }

    public IReadOnlyList<SkillRevisionInfo> List(string slug)
    {
        var directory = Directory(slug);
        if (!System.IO.Directory.Exists(directory)) return [];
        return System.IO.Directory.EnumerateFiles(directory, "*.json")
            .Select(path => new FileInfo(path))
            .Select(file =>
            {
                var id = Path.GetFileNameWithoutExtension(file.Name);
                var dash = id.LastIndexOf("-r", StringComparison.Ordinal);
                long.TryParse(dash > 0 ? id[(dash + 2)..] : "0", out var revision);
                var saved = DateTimeOffset.TryParseExact(id[..Math.Min(17, id.Length)], "yyyyMMddHHmmssfff", null,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : new DateTimeOffset(file.LastWriteTimeUtc);
                return new SkillRevisionInfo(id, revision, saved, CountFiles(file.FullName), file.Length);
            })
            .OrderByDescending(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    public SkillRevisionSnapshot? Get(string slug, string id)
    {
        if (id.Length > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) return null;
        var path = Path.Combine(Directory(slug), id + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SkillRevisionSnapshot>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    private static int CountFiles(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("files", out var files) ? files.EnumerateObject().Count() : 0;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return 0;
        }
    }
}
