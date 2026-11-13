using System.Text;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Engine;

public sealed partial class SkillImporterService
{
    private const string UnknownLicense = "No license identified";
    private readonly PreviewCache<(string Owner, string Repo, string Path, string Revision), RemoteSkill> _previewCache =
        new(64, TimeSpan.FromMinutes(15), 8 * 1024 * 1024, document => document.Bytes);
    private readonly PreviewCache<(string Owner, string Repo, string Ref), string> _revisionCache = new(32, TimeSpan.FromMinutes(3));
    private readonly PreviewCache<(string Owner, string Repo, string Revision), List<string>> _treeCache = new(24, TimeSpan.FromMinutes(10));
    private readonly SemaphoreSlim _previewDownloads = new(6);
    private sealed record RemoteSkill(CanonicalSkillModel Model, string License, int Bytes);

    private static (string Owner, string Repo, string Path, string Revision) PreviewKey(string owner, string repo, string path, string revision) =>
        (owner.ToLowerInvariant(), repo.ToLowerInvariant(), path, revision);

    private Task<RemoteSkill> GetRemoteSkillAsync(string owner, string repo, string revision, string path, List<string> entries, CancellationToken ct) =>
        _previewCache.GetAsync(PreviewKey(owner, repo, path, revision), async token =>
        {
            await _previewDownloads.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var raw = await ReadRemoteTextAsync(RawUri(owner, repo, revision, path), SkillStorage.MaxMarkdownBytes, token).ConfigureAwait(false);
                var model = ParseMarkdownToSkillModel(raw, SkillFallbackName(repo, path), $"GitHub ({owner}/{repo})");
                var prefix = SkillDirectoryPrefix(path);
                var license = ReadDeclaredLicense(raw);
                var licensePath = FindLicense(prefix) ?? FindLicense(string.Empty);
                license ??= licensePath is null ? UnknownLicense : $"License file present · {licensePath}";
                return new RemoteSkill(model, license, Encoding.UTF8.GetByteCount(raw));
            }
            finally
            {
                _previewDownloads.Release();
            }

            string? FindLicense(string prefix) => entries.FirstOrDefault(entry =>
                entry.Equals(prefix + "LICENSE", StringComparison.OrdinalIgnoreCase) ||
                entry.Equals(prefix + "LICENSE.md", StringComparison.OrdinalIgnoreCase));
        }, ct);

    private static CommunitySkillSummary WithSkillMetadata(CommunitySkillSummary summary, CanonicalSkillModel model) =>
        summary with { Slug = model.Name, Title = model.GetEffectiveTitle(), Description = model.Description, Topic = InferTopic(model.Name, model.Description) };

    private CanonicalSkillModel GetImportableCatalogSkill(string? source, string? path)
    {
        var id = source?.Split('/').LastOrDefault() ?? path?.Split('/').Reverse().Skip(1).FirstOrDefault();
        var catalog = _engine.GetCatalogSkill(id ?? string.Empty) ?? throw new KeyNotFoundException("This catalog entry is no longer available.");
        return catalog.SourceCategory != "system" ? catalog : throw new ArgumentException("System skills cannot be imported.");
    }

    private bool ImportNameExists(string name) => _engine.GetSkill(name) is { IsCatalogOnly: false };

    private static string SkillDirectoryPrefix(string path) => path[..(path.LastIndexOf('/') + 1)];

    private static string[] SupportingPaths(List<string> entries, string path)
    {
        var prefix = SkillDirectoryPrefix(path);
        return entries.Where(entry => entry.StartsWith(prefix, StringComparison.Ordinal) && entry != path)
            .Select(entry => entry[prefix.Length..])
            .Where(relative => relative.Contains('/') && SkillPaths.IsSupportingFolder(relative.Split('/')[0]))
            .Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<string> ReadPreviewFileAsync(string repoInput, string path, string revision, string file, CancellationToken ct)
    {
        file = SkillPaths.SupportingPath(file);
        if (repoInput == "local/catalog")
        {
            var catalog = GetImportableCatalogSkill(null, path).Clone();
            catalog.IsCatalogOnly = false;
            catalog.SourceCategory = "user";
            if (ContentHasher.ComputeSemanticHash(catalog) != revision)
                throw new SkillConflictException("The local catalog changed. Reopen the preview.");
            return catalog.SupportingFiles.GetValueOrDefault(file) ?? throw new KeyNotFoundException("That file is outside this bundle.");
        }
        var (owner, repo, _, _) = ParseSelection(new SkillImportSelection(Repo: repoInput));
        if (revision.Length != 40 || revision.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Choose an immutable GitHub revision.");
        var entries = await GetRepositoryBlobPathsAsync(owner, repo, revision, ct).ConfigureAwait(false);
        path = SelectSkillPath(entries, path);
        if (!SupportingPaths(entries, path).Contains(file, StringComparer.Ordinal))
            throw new KeyNotFoundException("That file is outside this bundle.");
        return await ReadRemoteTextAsync(RawUri(owner, repo, revision, SkillDirectoryPrefix(path) + file), MaxTextFile, ct).ConfigureAwait(false);
    }

    private sealed class PreviewCache<TKey, TValue>(int capacity, TimeSpan lifetime, int byteLimit = int.MaxValue,
        Func<TValue, int>? measure = null) where TKey : notnull
    {
        private sealed record Entry(TValue Value, DateTimeOffset ExpiresAt, int Size);
        private readonly object _gate = new();
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, Entry Entry)>> _entries = [];
        private readonly Dictionary<TKey, TaskCompletionSource<TValue>> _pending = [];
        private readonly LinkedList<(TKey Key, Entry Entry)> _recent = [];
        private int _bytes;

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var node))
                {
                    if (node.Value.Entry.ExpiresAt > DateTimeOffset.UtcNow)
                    {
                        _recent.Remove(node);
                        _recent.AddLast(node);
                        value = node.Value.Entry.Value;
                        return true;
                    }
                    Remove(node);
                }
            }
            value = default!;
            return false;
        }

        public Task<TValue> GetAsync(TKey key, Func<CancellationToken, Task<TValue>> fetch, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<TValue> completion;
            lock (_gate)
            {
                if (TryGet(key, out var value)) return Task.FromResult(value);
                if (_pending.TryGetValue(key, out var pending)) return pending.Task.WaitAsync(ct);
                if (_pending.Count >= capacity) throw new CommunitySourceException("Too many previews are loading. Try again shortly.", 503);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(key, completion);
            }
            _ = FillAsync(key, completion, fetch);
            return completion.Task.WaitAsync(ct);
        }

        private async Task FillAsync(TKey key, TaskCompletionSource<TValue> completion, Func<CancellationToken, Task<TValue>> fetch)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var value = await fetch(timeout.Token).ConfigureAwait(false);
                var size = measure?.Invoke(value) ?? 0;
                lock (_gate)
                {
                    if (size <= byteLimit)
                    {
                        while (_recent.First is { } oldest && (_entries.Count >= capacity || _bytes > byteLimit - size))
                            Remove(oldest);
                        _entries.Add(key, _recent.AddLast((key, new Entry(value, DateTimeOffset.UtcNow + lifetime, size))));
                        _bytes += size;
                    }
                    _pending.Remove(key);
                }
                completion.SetResult(value);
            }
            catch (Exception error)
            {
                lock (_gate) _pending.Remove(key);
                completion.SetException(error is OperationCanceledException
                    ? new CommunitySourceException("GitHub timed out. Try again or browse the local catalog.", 504) : error);
                // The requesting view may already have been canceled.
                _ = completion.Task.Exception;
            }
        }

        private void Remove(LinkedListNode<(TKey Key, Entry Entry)> node)
        {
            _bytes -= node.Value.Entry.Size;
            _entries.Remove(node.Value.Key);
            _recent.Remove(node);
        }
    }
}
