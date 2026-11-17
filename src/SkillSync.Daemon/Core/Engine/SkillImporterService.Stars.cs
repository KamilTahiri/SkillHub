using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>
/// GitHub stars for trust context in Explore and MCP ("★ 18.4k stars · 420k installs"). One call per repository, cached on disk
/// for 12 hours and revalidated with ETags, so browsing never burns through the anonymous GitHub API limit.
/// </summary>
public sealed partial class SkillImporterService
{
    private static readonly TimeSpan StarsLifetime = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, StarsEntry> _stars = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _starsFile = new(1, 1);
    private bool _starsLoaded;

    private sealed record StarsEntry(int? Stars, string? ETag, DateTimeOffset FetchedAt);

    private string StarsCachePath => Path.Combine(_engine.Discovery.SkillHubRoot, "cache", "github", "stars.json");

    private void LoadStars()
    {
        if (_starsLoaded) return;
        _starsLoaded = true;
        try
        {
            if (!File.Exists(StarsCachePath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, StarsEntry>>(File.ReadAllText(StarsCachePath));
            foreach (var (repo, entry) in saved ?? []) _stars.TryAdd(repo, entry);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
    }

    /// <summary>The cached star count, without a network call.</summary>
    public int? CachedStars(string repo)
    {
        LoadStars();
        return _stars.TryGetValue(repo, out var entry) ? entry.Stars : null;
    }

    /// <summary>Star count of an <c>owner/repo</c>, or null when GitHub cannot say (private, renamed, offline or rate-limited).</summary>
    public async Task<int?> GetStarsAsync(string repo, CancellationToken ct = default)
    {
        LoadStars();
        if (string.IsNullOrWhiteSpace(repo) || repo.Count(c => c == '/') != 1 || repo.StartsWith("local/", StringComparison.OrdinalIgnoreCase) ||
            repo.Equals(SkillsShDirectory.SourceId, StringComparison.OrdinalIgnoreCase)) return null;
        _stars.TryGetValue(repo, out var cached);
        if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < StarsLifetime) return cached.Stars;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            StarsEntry entry;
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) entry = cached with { FetchedAt = DateTimeOffset.UtcNow };
            else if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                var stars = document.RootElement.TryGetProperty("stargazers_count", out var count) && count.TryGetInt32(out var value) ? value : (int?)null;
                entry = new StarsEntry(stars, response.Headers.ETag?.ToString(), DateTimeOffset.UtcNow);
            }
            else if (response.StatusCode == HttpStatusCode.NotFound) entry = new StarsEntry(null, null, DateTimeOffset.UtcNow);
            else return cached?.Stars; // Rate limited or failing: keep what we knew.
            _stars[repo] = entry;
            await SaveStarsAsync().ConfigureAwait(false);
            return entry.Stars;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            return cached?.Stars;
        }
    }

    private async Task SaveStarsAsync()
    {
        await _starsFile.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StarsCachePath)!);
            await AtomicFileWriter.WriteTextAtomicallyAsync(StarsCachePath, JsonSerializer.Serialize(_stars.ToDictionary(pair => pair.Key, pair => pair.Value))).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { _starsFile.Release(); }
    }

    /// <summary>Adds stars to listing rows: cached values for every row, fresh lookups for the first few repositories only.</summary>
    public async Task<IReadOnlyList<CommunitySkillSummary>> WithStarsAsync(IReadOnlyList<CommunitySkillSummary> rows, int fetchLimit = 8, CancellationToken ct = default)
    {
        var repos = rows.Select(row => row.Repo).Where(repo => repo.Count(c => c == '/') == 1 && !repo.StartsWith("local/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(fetchLimit).ToArray();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try { await Task.WhenAll(repos.Select(repo => GetStarsAsync(repo, budget.Token))).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return rows.Select(row => CachedStars(row.Repo) is { } stars ? row with { Stars = stars } : row).ToArray();
    }
}
