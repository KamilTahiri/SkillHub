using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>One Markdown document found in a repository, classified by the AI tool format it uses.</summary>
public sealed record IndexedDocument(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("excluded_files")] IReadOnlyList<string> ExcludedFiles,
    [property: JsonPropertyName("has_frontmatter")] bool HasFrontmatter);

public sealed record IndexedFile(
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("text")] bool Text);

/// <summary>An immutable view of one repository commit. It is written once per SHA and never invalidated.</summary>
public sealed record RepositoryIndex(
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("indexed_at")] DateTimeOffset IndexedAt,
    [property: JsonPropertyName("files")] Dictionary<string, IndexedFile> Files,
    [property: JsonPropertyName("documents")] List<IndexedDocument> Documents,
    [property: JsonPropertyName("readme_path")] string? ReadmePath,
    [property: JsonPropertyName("readme_excerpt")] string? ReadmeExcerpt,
    [property: JsonPropertyName("license_path")] string? LicensePath,
    [property: JsonPropertyName("truncated")] bool Truncated)
{
    public const int SchemaVersion = 2;
    [JsonPropertyName("schema")] public int Schema { get; init; } = SchemaVersion;
}

/// <summary>
/// Downloads a repository once per commit (as a codeload tarball, which does not use the GitHub REST quota),
/// classifies every Markdown document, and caches the result under <c>~/.skillhub/cache/github/{owner}/{repo}/{sha}/</c>.
/// Falls back to the Git Trees API plus raw.githubusercontent.com when the tarball is unavailable or too large.
/// </summary>
public sealed class RepositoryIndexer
{
    public const int MaxTextFileBytes = 256 * 1024;
    private const long MaxTarballBytes = 256L * 1024 * 1024;
    private const long MaxTextBytes = 80L * 1024 * 1024;
    private const int MaxEntries = 20_000;
    private const int MaxBundleFiles = 128;
    private const int MaxFallbackMetadataReads = 80;
    private const int MaxCachedCommits = 40;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "vendor", "dist", "build", "out", "target", "bin", "obj", "__pycache__", ".venv", "venv"
    };
    private static readonly HashSet<string> AllowedDotDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".github", ".cursor", ".claude", ".agents", ".gemini", ".codex", ".windsurf", ".clinerules"
    };
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".pdf", ".zip", ".gz", ".tgz", ".tar", ".7z", ".rar",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".mov",
        ".wav", ".ogg", ".webm", ".jar", ".class", ".pyc", ".docx", ".xlsx", ".pptx", ".psd", ".sqlite", ".db"
    };

    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, Lazy<Task<RepositoryIndex>>> _indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refLocks = new(StringComparer.Ordinal);

    private readonly TimeSpan _refLifetime;

    public RepositoryIndexer(HttpClient http, string cacheRoot, TimeSpan? refLifetime = null)
    {
        _http = http;
        _refLifetime = refLifetime ?? TimeSpan.FromMinutes(10);
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// Resolves the ref and optional in-repository path from the segments that follow <c>tree/</c> or <c>blob/</c>.
    /// Branch names may contain slashes, so the shortest prefix that GitHub recognizes as a ref wins.
    /// </summary>
    public async Task<(string Sha, string? Path)> ResolveRefAndPathAsync(string owner, string repo, IReadOnlyList<string> segments,
        CancellationToken ct)
    {
        if (segments.Count == 0) return (await ResolveCommitAsync(owner, repo, "HEAD", ct).ConfigureAwait(false), null);
        CommunitySourceException? last = null;
        for (var length = 1; length <= Math.Min(segments.Count, 8); length++)
        {
            var gitRef = string.Join('/', segments.Take(length));
            try
            {
                var sha = await ResolveCommitAsync(owner, repo, gitRef, ct).ConfigureAwait(false);
                var rest = segments.Skip(length).ToArray();
                return (sha, rest.Length == 0 ? null : string.Join('/', rest));
            }
            catch (CommunitySourceException error) when (error.StatusCode is 404 or 422)
            {
                last = error;
            }
        }
        throw last ?? new CommunitySourceException("GitHub could not find that branch, tag, or commit.", 404);
    }

    /// <summary>Resolves a branch, tag or SHA to a commit, revalidating cached refs with ETags (304 responses cost no quota).</summary>
    public async Task<string> ResolveCommitAsync(string owner, string repo, string gitRef, CancellationToken ct)
    {
        if (Regex.IsMatch(gitRef, @"\A[0-9a-fA-F]{40}\z")) return gitRef.ToLowerInvariant();
        var directory = RepoDirectory(owner, repo);
        var gate = _refLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var refs = ReadRefs(directory);
            refs.TryGetValue(gitRef, out var cached);
            if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < _refLifetime) return cached.Sha;

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/commits/{Uri.EscapeDataString(gitRef)}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (cached?.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            string sha;
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                sha = cached.Sha;
            }
            else
            {
                EnsureSuccess(response);
                using var document = JsonDocument.Parse(await ReadLimitedAsync(response, 2 * 1024 * 1024, ct).ConfigureAwait(false));
                sha = document.RootElement.GetProperty("sha").GetString() ?? string.Empty;
                if (!Regex.IsMatch(sha, @"\A[0-9a-fA-F]{40}\z"))
                    throw new InvalidDataException("GitHub did not return an immutable revision.");
                sha = sha.ToLowerInvariant();
            }
            refs[gitRef] = new RefRecord(sha, response.Headers.ETag?.ToString() ?? cached?.ETag, DateTimeOffset.UtcNow);
            await WriteRefsAsync(directory, refs).ConfigureAwait(false);
            return sha;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<RepositoryIndex> GetIndexAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        var key = $"{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}@{sha}";
        var lazy = _indexes.GetOrAdd(key, _ => new Lazy<Task<RepositoryIndex>>(() => LoadOrBuildAsync(owner, repo, sha)));
        var task = lazy.Value;
        _ = task.ContinueWith(completed => _indexes.TryRemove(new KeyValuePair<string, Lazy<Task<RepositoryIndex>>>(key, lazy)),
            CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        if (_indexes.Count > 64)
        {
            foreach (var stale in _indexes.Keys.Take(16)) _indexes.TryRemove(stale, out _);
        }
        return task.WaitAsync(ct);
    }

    /// <summary>Reads a text file from the indexed commit, downloading and caching it when it is not stored yet.</summary>
    public async Task<string> ReadTextAsync(RepositoryIndex index, string path, CancellationToken ct)
    {
        if (!index.Files.TryGetValue(path, out var file))
            throw new KeyNotFoundException("That file is not part of this repository revision.");
        if (file.Size > MaxTextFileBytes) throw new InvalidDataException("File exceeds the supported size.");
        var blob = BlobPath(index.Owner, index.Repo, index.Sha, path);
        if (File.Exists(blob)) return await File.ReadAllTextAsync(blob, Encoding.UTF8, ct).ConfigureAwait(false);
        if (index.Mode == "tarball" && !file.Text) throw new InvalidDataException("Binary files are excluded from text imports.");
        var text = await DownloadRawAsync(index.Owner, index.Repo, index.Sha, path, ct).ConfigureAwait(false);
        await WriteBlobAsync(blob, text).ConfigureAwait(false);
        return text;
    }

    /// <summary>Returns a file's text only when it is already cached on disk. Never touches the network.</summary>
    public string? TryReadCachedText(RepositoryIndex index, string path)
    {
        var blob = BlobPath(index.Owner, index.Repo, index.Sha, path);
        try { return File.Exists(blob) ? File.ReadAllText(blob, Encoding.UTF8) : null; }
        catch (IOException) { return null; }
    }

    private async Task<RepositoryIndex> LoadOrBuildAsync(string owner, string repo, string sha)
    {
        var directory = CommitDirectory(owner, repo, sha);
        var indexPath = Path.Combine(directory, "index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<RepositoryIndex>(await File.ReadAllTextAsync(indexPath).ConfigureAwait(false), Json);
                if (stored is { Schema: RepositoryIndex.SchemaVersion }) return stored;
            }
            catch (JsonException)
            {
                // A damaged cache entry is rebuilt below.
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        Dictionary<string, IndexedFile> files;
        string mode;
        bool truncated;
        try
        {
            (files, truncated) = await DownloadTarballAsync(owner, repo, sha, timeout.Token).ConfigureAwait(false);
            mode = "tarball";
        }
        catch (Exception error) when (error is CommunitySourceException or InvalidDataException or HttpRequestException ||
            error is OperationCanceledException && !timeout.IsCancellationRequested || error is IOException)
        {
            (files, truncated) = await ListTreeAsync(owner, repo, sha, timeout.Token).ConfigureAwait(false);
            mode = "tree";
        }

        var partial = new RepositoryIndex(owner, repo, sha, mode, DateTimeOffset.UtcNow, files, [], null, null, null, truncated);
        var documents = await ClassifyAsync(partial, timeout.Token).ConfigureAwait(false);
        var readme = files.Keys.Where(path => !path.Contains('/') && Path.GetFileNameWithoutExtension(path).Equals("README", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Length).FirstOrDefault();
        string? excerpt = null;
        if (readme is not null)
        {
            try { excerpt = Excerpt(await ReadTextAsync(partial, readme, timeout.Token).ConfigureAwait(false), 1600); }
            catch (Exception error) when (error is InvalidDataException or CommunitySourceException or KeyNotFoundException) { }
        }
        var license = files.Keys.Where(path => !path.Contains('/') &&
                Regex.IsMatch(path, @"\A(?:LICENSE|LICENCE|COPYING)(?:\.[A-Za-z]+)?\z", RegexOptions.IgnoreCase))
            .OrderBy(path => path.Length).FirstOrDefault();
        var index = partial with { Documents = documents, ReadmePath = readme, ReadmeExcerpt = excerpt, LicensePath = license };
        Directory.CreateDirectory(directory);
        await AtomicFileWriter.WriteTextAtomicallyAsync(indexPath, JsonSerializer.Serialize(index, Json)).ConfigureAwait(false);
        TrimCache();
        return index;
    }

    private async Task<(Dictionary<string, IndexedFile> Files, bool Truncated)> DownloadTarballAsync(string owner, string repo, string sha,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://codeload.github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/tar.gz/{sha}");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > MaxTarballBytes)
            throw new InvalidDataException("The repository archive is too large to index.");

        var files = new Dictionary<string, IndexedFile>(StringComparer.Ordinal);
        var truncated = false;
        long textBytes = 0;
        await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var limited = new LimitedReadStream(network, MaxTarballBytes);
        await using var gzip = new GZipStream(limited, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var buffer = new byte[MaxTextFileBytes];
        while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var separator = entry.Name.IndexOf('/');
            if (separator < 0) continue;
            var path = entry.Name[(separator + 1)..];
            if (!IsIndexablePath(path)) continue;
            if (files.Count >= MaxEntries) { truncated = true; break; }

            var size = entry.Length;
            var text = false;
            if (size <= MaxTextFileBytes && !BinaryExtensions.Contains(Path.GetExtension(path)) && entry.DataStream is { } data &&
                textBytes + size <= MaxTextBytes)
            {
                var read = 0;
                while (read < size)
                {
                    var count = await data.ReadAsync(buffer.AsMemory(read, (int)size - read), ct).ConfigureAwait(false);
                    if (count == 0) break;
                    read += count;
                }
                if (TryDecode(buffer.AsSpan(0, read), out var content))
                {
                    text = true;
                    textBytes += read;
                    await WriteBlobAsync(BlobPath(owner, repo, sha, path), content).ConfigureAwait(false);
                }
            }
            files[path] = new IndexedFile(size, text);
        }
        return (files, truncated);
    }

    private async Task<(Dictionary<string, IndexedFile> Files, bool Truncated)> ListTreeAsync(string owner, string repo, string sha,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/trees/{sha}?recursive=1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        EnsureSuccess(response);
        using var document = JsonDocument.Parse(await ReadLimitedAsync(response, 16 * 1024 * 1024, ct).ConfigureAwait(false));
        var truncated = document.RootElement.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
        var files = new Dictionary<string, IndexedFile>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "blob" ||
                entry.TryGetProperty("mode", out var mode) && mode.GetString() == "120000") continue;
            var path = entry.GetProperty("path").GetString()!;
            if (!IsIndexablePath(path)) continue;
            var size = entry.TryGetProperty("size", out var value) && value.TryGetInt64(out var bytes) ? bytes : 0;
            files[path] = new IndexedFile(size, !BinaryExtensions.Contains(Path.GetExtension(path)));
            if (files.Count >= MaxEntries) { truncated = true; break; }
        }
        return (files, truncated);
    }

    private async Task<List<IndexedDocument>> ClassifyAsync(RepositoryIndex index, CancellationToken ct)
    {
        var paths = index.Files.Keys.Order(StringComparer.Ordinal).ToArray();
        var skillDirectories = paths.Where(path => SkillDocumentClassifier.Classify(path) == "skill")
            .Select(path => SkillDirectoryPrefix(path)).ToHashSet(StringComparer.Ordinal);
        string? OwningSkill(string path) => skillDirectories.Where(prefix => path.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(prefix => prefix.Length).FirstOrDefault();

        var candidates = new List<(string Path, string Kind)>();
        foreach (var path in paths)
        {
            var kind = SkillDocumentClassifier.Classify(path);
            if (kind is null) continue;
            // Markdown inside a skill folder belongs to that bundle, not to the repository listing.
            if (kind != "skill" && OwningSkill(path) is not null) continue;
            candidates.Add((path, kind));
        }

        var documents = new IndexedDocument?[candidates.Count];
        var reads = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (position, token) =>
            {
                var (path, kind) = candidates[position];
                string? text = null;
                var stored = index.Mode == "tarball" || Interlocked.Increment(ref reads) <= MaxFallbackMetadataReads;
                if (stored && index.Files[path].Size <= MaxTextFileBytes)
                {
                    try { text = await ReadTextAsync(index, path, token).ConfigureAwait(false); }
                    catch (Exception error) when (error is InvalidDataException or CommunitySourceException or KeyNotFoundException) { }
                }
                var (files, excluded) = kind == "skill" ? Bundle(index, path, skillDirectories) : ([], []);
                documents[position] = SkillDocumentClassifier.Describe(path, kind, text, index.Repo, files, excluded);
            }).ConfigureAwait(false);
        return documents.OfType<IndexedDocument>().ToList();
    }

    private static (IReadOnlyList<string> Files, IReadOnlyList<string> Excluded) Bundle(RepositoryIndex index, string skillPath,
        HashSet<string> skillDirectories)
    {
        var prefix = SkillDirectoryPrefix(skillPath);
        var nested = skillDirectories.Where(other => other.Length > prefix.Length && other.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var files = new List<string>();
        var excluded = new List<string>();
        foreach (var (path, file) in index.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (path == skillPath || !path.StartsWith(prefix, StringComparison.Ordinal) ||
                nested.Any(other => path.StartsWith(other, StringComparison.Ordinal))) continue;
            var relative = path[prefix.Length..];
            if (!file.Text || file.Size > MaxTextFileBytes || !IsBundlePath(relative)) { excluded.Add(relative); continue; }
            if (files.Count >= MaxBundleFiles) { excluded.Add(relative); continue; }
            files.Add(relative);
        }
        return (files, excluded);
    }

    /// <summary>True when the relative path can be stored as a supporting file in SkillHub's canonical bundle.</summary>
    public static bool IsBundlePath(string relative)
    {
        try
        {
            SkillPaths.SupportingPath(relative);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string SkillDirectoryPrefix(string path) => path[..(path.LastIndexOf('/') + 1)];

    private static bool IsIndexablePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400) return false;
        var segments = path.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0 || segment is "." or "..") return false;
            if (index < segments.Length - 1 && (SkippedDirectories.Contains(segment) ||
                segment.StartsWith('.') && !AllowedDotDirectories.Contains(segment))) return false;
        }
        return true;
    }

    private static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;
        if (bytes.IndexOf((byte)0) >= 0) return false;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private async Task<string> DownloadRawAsync(string owner, string repo, string sha, string path, CancellationToken ct)
    {
        var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{sha}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        EnsureSuccess(response);
        var bytes = await ReadLimitedBytesAsync(response, MaxTextFileBytes, ct).ConfigureAwait(false);
        return TryDecode(bytes, out var text) ? text : throw new InvalidDataException("Binary files are excluded from text imports.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new CommunitySourceException("GitHub is unreachable. Check your connection or browse the local catalog.", 503);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CommunitySourceException("GitHub timed out. Try again or browse the local catalog.", 504);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var limited = response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var quota) && quota.Contains("0");
        if (limited)
        {
            var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
                long.TryParse(values.FirstOrDefault(), out var seconds)
                ? " It resets at " + DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("HH:mm") + "."
                : string.Empty;
            throw new CommunitySourceException("GitHub rate limit reached." + reset + " Cached repositories still work.", 429);
        }
        throw response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity
            ? new CommunitySourceException("GitHub could not find that repository, revision, or file.", (int)response.StatusCode)
            : new CommunitySourceException($"GitHub returned HTTP {(int)response.StatusCode}.", 502);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        var bytes = await ReadLimitedBytesAsync(response, maximum, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("File exceeds the supported size.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximum) throw new InvalidDataException("File exceeds the supported size.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Excerpt(string markdown, int maximum)
    {
        var body = FrontmatterParser.TrySplitFrontmatter(markdown, out _, out var rest) ? rest : markdown;
        body = ContentHasher.NormalizeLineEndings(body).Trim();
        if (body.Length <= maximum) return body;
        var cut = body.LastIndexOf("\n\n", maximum, StringComparison.Ordinal);
        return body[..(cut > maximum / 2 ? cut : maximum)].TrimEnd() + "\n\n…";
    }

    private string RepoDirectory(string owner, string repo) =>
        SkillPaths.Resolve(_cacheRoot, $"{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}");

    private string CommitDirectory(string owner, string repo, string sha) => Path.Combine(RepoDirectory(owner, repo), sha);

    private string BlobPath(string owner, string repo, string sha, string path) =>
        Path.Combine(CommitDirectory(owner, repo, sha), "blobs",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant() + ".txt");

    private static async Task WriteBlobAsync(string path, string text)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".skillsync-tmp";
        await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false)).ConfigureAwait(false);
        try { File.Move(temporary, path, overwrite: false); }
        catch (IOException) { File.Delete(temporary); }
    }

    private sealed record RefRecord(
        [property: JsonPropertyName("sha")] string Sha,
        [property: JsonPropertyName("etag")] string? ETag,
        [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt);

    private static Dictionary<string, RefRecord> ReadRefs(string directory)
    {
        var path = Path.Combine(directory, "refs.json");
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, RefRecord>>(File.ReadAllText(path), Json) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static async Task WriteRefsAsync(string directory, Dictionary<string, RefRecord> refs)
    {
        Directory.CreateDirectory(directory);
        var trimmed = refs.OrderByDescending(pair => pair.Value.CheckedAt).Take(64).ToDictionary(pair => pair.Key, pair => pair.Value);
        await AtomicFileWriter.WriteTextAtomicallyAsync(Path.Combine(directory, "refs.json"), JsonSerializer.Serialize(trimmed, Json))
            .ConfigureAwait(false);
    }

    private void TrimCache()
    {
        try
        {
            var commits = Directory.Exists(_cacheRoot)
                ? Directory.EnumerateDirectories(_cacheRoot).SelectMany(Directory.EnumerateDirectories)
                    .SelectMany(Directory.EnumerateDirectories)
                    .Where(directory => File.Exists(Path.Combine(directory, "index.json")))
                    .OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, "index.json"))).ToArray()
                : [];
            foreach (var stale in commits.Skip(MaxCachedCommits))
                Directory.Delete(stale, recursive: true);
        }
        catch (IOException)
        {
            // Cache trimming is best effort. A locked directory is retried on the next index build.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Counts compressed bytes so a huge archive is rejected while streaming.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new InvalidDataException("The repository archive is too large to index.") : read;
        }
    }
}

/// <summary>Recognizes the Markdown formats used by Claude, Codex, Gemini, Cursor and GitHub Copilot.</summary>
public static class SkillDocumentClassifier
{
    public static readonly IReadOnlyDictionary<string, string> Groups = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["skill"] = "skills", ["command"] = "commands", ["prompt"] = "commands", ["rule"] = "rules",
        ["instructions"] = "rules", ["project-rules"] = "rules", ["agent"] = "agents", ["chatmode"] = "agents", ["doc"] = "docs"
    };

    private static readonly string[] CompoundSuffixes = [".instructions.md", ".prompt.md", ".chatmode.md", ".agent.md"];

    public static string? Classify(string path)
    {
        var name = Path.GetFileName(path);
        var lower = name.ToLowerInvariant();
        if (lower == "skill.md") return "skill";
        if (lower.EndsWith(".mdc", StringComparison.Ordinal)) return "rule";
        if (lower.EndsWith(".instructions.md", StringComparison.Ordinal) || lower == "copilot-instructions.md") return "instructions";
        if (lower.EndsWith(".prompt.md", StringComparison.Ordinal)) return "prompt";
        if (lower.EndsWith(".chatmode.md", StringComparison.Ordinal)) return "chatmode";
        if (lower.EndsWith(".agent.md", StringComparison.Ordinal)) return "agent";
        if (!lower.EndsWith(".md", StringComparison.Ordinal) && !lower.EndsWith(".markdown", StringComparison.Ordinal)) return null;
        var directories = path.Split('/')[..^1].Select(segment => segment.ToLowerInvariant()).ToArray();
        // Only repository-level AGENTS.md, CLAUDE.md and GEMINI.md are project rules. Files with those names deeper in a
        // repository (website pages, examples, nested packages) are ordinary documents.
        if (lower is "agents.md" or "claude.md" or "gemini.md")
            return directories.Length == 0 || directories is [".github" or ".claude" or ".cursor" or ".gemini"] ? "project-rules"
                : IsUsefulDoc(lower, directories) ? "doc" : null;
        if (directories.Contains("commands")) return "command";
        if (directories.Contains("agents")) return "agent";
        if (directories.Contains("rules")) return "rule";
        if (directories.Contains("prompts")) return "prompt";
        return IsUsefulDoc(lower, directories) ? "doc" : null;
    }

    private static readonly HashSet<string> NoiseDocNames = new(StringComparer.Ordinal)
    {
        "changelog.md", "code_of_conduct.md", "security.md", "third_party_notices.md", "notice.md", "license.md",
        "pull_request_template.md", "issue_template.md", "support.md", "codeowners.md"
    };
    private static readonly HashSet<string> NoiseDocFolders = new(StringComparer.Ordinal)
    {
        "test", "tests", "__tests__", "fixtures", "fixture", "testdata", "website", "site", "blog", "issue_template", ".github", "examples", "example"
    };

    /// <summary>
    /// Only top-level documentation is listed (README, CONTRIBUTING, guides in a shallow docs folder). Deep website pages,
    /// test fixtures and repository housekeeping files would otherwise outnumber the actual skills.
    /// </summary>
    private static bool IsUsefulDoc(string fileName, string[] directories) =>
        directories.Length <= 1 && !NoiseDocNames.Contains(fileName) && !directories.Any(NoiseDocFolders.Contains);

    /// <summary>The file name without its Markdown or compound format suffix, for example <c>review.prompt.md</c> → <c>review</c>.</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var suffix in CompoundSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        }
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string FallbackName(string repo, string path) =>
        Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path) is { Length: > 0 } directory ? Path.GetFileName(directory) : repo
            : Path.GetFileName(path).ToLowerInvariant() is "agents.md" or "claude.md" or "gemini.md" or "readme.md"
                ? (Path.GetDirectoryName(path) is { Length: > 0 } folder ? Path.GetFileName(folder) + "-" : repo + "-") + BaseName(path)
                : BaseName(path);

    public static IndexedDocument Describe(string path, string kind, string? text, string repo,
        IReadOnlyList<string> files, IReadOnlyList<string> excluded)
    {
        var name = ProviderDiscoveryService.NormalizeSlug(FallbackName(repo, path));
        string? title = null;
        var description = string.Empty;
        var hasFrontmatter = false;
        if (text is not null)
        {
            var body = text;
            if (FrontmatterParser.TrySplitFrontmatter(text, out var yaml, out var rest))
            {
                body = rest;
                hasFrontmatter = yaml.Length > 0;
                try
                {
                    var values = FrontmatterParser.ParseYamlToDictionary(yaml);
                    if (values.GetValueOrDefault("name")?.ToString() is { Length: > 0 } declared &&
                        ProviderDiscoveryService.NormalizeSlug(declared) is { Length: > 0 } slug) name = slug;
                    title = values.GetValueOrDefault("title")?.ToString();
                    description = FrontmatterParser.CleanSingleLine(values.GetValueOrDefault("description")?.ToString());
                }
                catch (YamlDotNet.Core.YamlException)
                {
                    hasFrontmatter = false;
                }
            }
            title = string.IsNullOrWhiteSpace(title) ? FrontmatterParser.ExtractFirstMarkdownHeading(body) : title;
            if (string.IsNullOrWhiteSpace(description)) description = FirstParagraph(body);
        }
        title = string.IsNullOrWhiteSpace(title) ? CanonicalSkillModel.HumanizeSlug(name) : title.Trim();
        if (description.Length > 300) description = description[..297].TrimEnd() + "…";
        return new IndexedDocument(path, kind, SkillDocumentClassifier.Groups[kind], name, title, description, files, excluded, hasFrontmatter);
    }

    /// <summary>The first prose paragraph of a Markdown body with inline markup removed.</summary>
    public static string FirstParagraph(string markdown)
    {
        var lines = ContentHasher.NormalizeLineEndings(markdown).Split('\n');
        var paragraph = new List<string>();
        var fenced = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced) continue;
            if (line.Length == 0) { if (paragraph.Count > 0) break; continue; }
            if (line.StartsWith('#') || line.StartsWith('|') || line.StartsWith("<", StringComparison.Ordinal) ||
                line.StartsWith("![", StringComparison.Ordinal) || Regex.IsMatch(line, @"\A(?:---+|\*\*\*+|___+)\z"))
            {
                if (paragraph.Count > 0) break;
                continue;
            }
            paragraph.Add(Regex.Replace(line, @"\A(?:[-*+]|\d+[.)]|>)\s+", string.Empty));
        }
        var text = string.Join(' ', paragraph);
        text = Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"[`*_]{1,3}", string.Empty);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
