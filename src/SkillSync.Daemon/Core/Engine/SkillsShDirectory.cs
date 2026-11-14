using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>One skill listed in the skills.sh directory. <see cref="Source"/> is the GitHub <c>owner/repo</c> that hosts it.</summary>
public sealed record SkillsShEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("skillId")] string SkillId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("installs")] long Installs);

/// <summary>
/// Searches the public skills.sh directory (the index behind <c>npx skills find</c>). The directory only knows
/// where each skill lives; SkillHub reads the skill itself from the source repository through <see cref="RepositoryIndexer"/>.
/// </summary>
public sealed class SkillsShDirectory(HttpClient http)
{
    public const string SourceId = "skills.sh";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly string[] PopularSeeds = ["react", "python", "testing", "design", "typescript", "docs", "git", "api"];
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<SkillsShEntry> Entries)> _cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<SkillsShEntry>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length < 2) return [];
        limit = Math.Clamp(limit, 1, 100);
        var key = q.ToLowerInvariant() + "|" + limit;
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < Lifetime) return cached.Entries;

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"https://skills.sh/api/search?q={Uri.EscapeDataString(q)}&limit={limit}",
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new CommunitySourceException("skills.sh is unreachable. GitHub sources and the local catalog still work.", 503);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CommunitySourceException("skills.sh timed out. Try again shortly.", 504);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new CommunitySourceException(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "skills.sh is limiting requests. Try again in a minute." : $"skills.sh returned HTTP {(int)response.StatusCode}.", 502);
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("The skills.sh response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var entries = new List<SkillsShEntry>();
            if (document.RootElement.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in skills.EnumerateArray())
                {
                    var source = item.TryGetProperty("source", out var s) ? s.GetString() : null;
                    var skillId = item.TryGetProperty("skillId", out var id) ? id.GetString() : null;
                    // Only GitHub-hosted skills can be reviewed and imported; other hosts are skipped.
                    if (source is null || skillId is null || !Regex.IsMatch(source, @"\A[A-Za-z0-9-]{1,100}/[A-Za-z0-9_.-]{1,100}\z") ||
                        !Regex.IsMatch(skillId, @"\A[A-Za-z0-9._:-]{1,120}\z")) continue;
                    entries.Add(new SkillsShEntry(
                        item.TryGetProperty("id", out var full) ? full.GetString() ?? source + "/" + skillId : source + "/" + skillId,
                        source, skillId,
                        item.TryGetProperty("name", out var name) ? name.GetString() ?? skillId : skillId,
                        item.TryGetProperty("installs", out var installs) && installs.TryGetInt64(out var count) ? count : 0));
                }
            }
            _cache[key] = (DateTimeOffset.UtcNow, entries);
            if (_cache.Count > 200) foreach (var stale in _cache.Keys.Take(50)) _cache.TryRemove(stale, out _);
            return entries;
        }
    }

    /// <summary>The most-installed skills across a spread of common topics, for browsing without a query.</summary>
    public async Task<IReadOnlyList<SkillsShEntry>> PopularAsync(CancellationToken ct)
    {
        var batches = await Task.WhenAll(PopularSeeds.Select(async seed =>
        {
            try { return await SearchAsync(seed, 25, ct).ConfigureAwait(false); }
            catch (CommunitySourceException) { return (IReadOnlyList<SkillsShEntry>)[]; }
        })).ConfigureAwait(false);
        return batches.SelectMany(batch => batch).DistinctBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(entry => entry.Installs).Take(80).ToArray();
    }
}
