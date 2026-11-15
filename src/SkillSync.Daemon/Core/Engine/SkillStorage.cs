using System.Text;
using System.Text.Json;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

internal sealed record SkillDiskSnapshot(string? Markdown, Dictionary<string, string> Files)
{
    public string? Hash => Markdown is null ? null : ContentHasher.ComputeFileRevisionHash(Markdown);

    public Dictionary<string, string> FileHashes() => Files.ToDictionary(
        pair => pair.Key, pair => ContentHasher.ComputeFileRevisionHash(pair.Value), SkillPaths.Comparer);
}

internal sealed class SkillStorage(ProviderDiscoveryService discovery)
{
    internal const int MaxMarkdownBytes = 2 * 1024 * 1024;
    internal const int MaxFileBytes = 1024 * 1024;
    private const int MaxBundleBytes = 24 * 1024 * 1024;
    internal const int MaxBundleFiles = 512;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string CanonicalPath(string slug)
    {
        SkillPaths.ValidateSegment(slug);
        return SkillPaths.Resolve(discovery.CanonicalSkillsRoot, $"{slug}/SKILL.md");
    }

    public static void ValidateModel(CanonicalSkillModel model)
    {
        if (model.InstructionsMarkdown is null || model.Description is null || model.WhenToUse is null ||
            model.SummaryShort is null || model.Tags is null || model.Targets?.Claude?.AllowedTools is null ||
            model.Targets.Gemini is null || model.Targets.Codex is null || model.Targets.Cursor?.Globs is null ||
            model.SupportingFiles is null || model.Passthrough is null ||
            model.Tags.Any(string.IsNullOrWhiteSpace) || model.Targets.Claude.AllowedTools.Any(string.IsNullOrWhiteSpace) ||
            model.Targets.Cursor.Globs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Skill text, tag, target and bundle fields must have valid values.");
        }

        if (Utf8.GetByteCount(model.InstructionsMarkdown) > MaxMarkdownBytes)
        {
            throw new ArgumentException("Skill instructions exceed the supported size.");
        }

        if (model.SupportingFiles.Count > MaxBundleFiles)
        {
            throw new ArgumentException("Too many supporting files.");
        }

        var files = new Dictionary<string, string>(SkillPaths.Comparer);
        var size = 0;
        foreach (var (input, content) in model.SupportingFiles)
        {
            var path = SkillPaths.SupportingPath(input);
            if (content is null || content.Contains('\0') || AtomicFileWriter.IsTemporaryOrIgnoredPath(path))
            {
                throw new ArgumentException($"Supporting file '{path}' must contain UTF-8 text and have a supported filename.");
            }

            var bytes = Utf8.GetByteCount(content);
            size += bytes;
            if (bytes > MaxFileBytes || size > MaxBundleBytes || !files.TryAdd(path, content))
            {
                throw new ArgumentException($"Supporting file '{path}' exceeds the bundle limit or duplicates a path.");
            }
        }

        foreach (var path in files.Keys)
        {
            for (var separator = path.IndexOf('/'); separator >= 0; separator = path.IndexOf('/', separator + 1))
            {
                if (files.ContainsKey(path[..separator]))
                {
                    throw new ArgumentException($"Supporting path '{path}' conflicts with another bundle file.");
                }
            }
        }

        model.SupportingFiles = files;
        model.Passthrough = JsonSerializer.SerializeToElement(model.Passthrough).EnumerateObject().ToDictionary(
            property => property.Name, property => ToYamlValue(property.Value), StringComparer.Ordinal);
    }

    private static object? ToYamlValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => ToYamlValue(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(ToYamlValue).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var number) ? (object)number : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    public static void PreflightBundle(string directory, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var target = SkillPaths.Resolve(directory, SkillPaths.SupportingPath(path));
            if (Directory.Exists(target))
            {
                throw new IOException($"Supporting file '{path}' would replace a directory.");
            }
            for (var parent = Path.GetDirectoryName(target); parent is not null && SkillPaths.IsInside(directory, parent); parent = Path.GetDirectoryName(parent))
            {
                if (File.Exists(parent))
                {
                    throw new IOException($"Supporting file '{path}' has a file in its parent path.");
                }
            }
        }
    }

    public static SkillDiskSnapshot Read(string primary, bool includeBundle = true)
    {
        SkillPaths.AssertNoReparsePoints(primary);
        var markdown = ReadText(primary, MaxMarkdownBytes);
        return new SkillDiskSnapshot(markdown, includeBundle
            ? ReadBundle(Path.GetDirectoryName(primary)!) : new(SkillPaths.Comparer));
    }

    /// <summary>Every bundle file inside a skill folder, at any depth (see <see cref="SkillPaths.IsBundleFile"/>).</summary>
    public static IEnumerable<IEnumerable<string>> EnumerateBundleFileGroups(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        yield return EnumerateSafeFiles(directory)
            .Where(file => SkillPaths.IsBundleFile(Path.GetRelativePath(directory, file).Replace('\\', '/')))
            .Order(StringComparer.Ordinal).ToArray();
    }

    public static Dictionary<string, string> ReadBundle(string directory)
    {
        var files = new Dictionary<string, string>(SkillPaths.Comparer);
        var size = 0;
        var scanned = 0;
        foreach (var candidates in EnumerateBundleFileGroups(directory))
        {
            foreach (var file in candidates)
            {
                if (++scanned > 4096)
                {
                    throw new InvalidDataException($"Bundle at '{directory}' has too many files to scan safely.");
                }
                if (AtomicFileWriter.IsTemporaryOrIgnoredPath(file))
                {
                    continue;
                }

                var path = SkillPaths.SupportingPath(Path.GetRelativePath(directory, file));
                string? content;
                try
                {
                    content = ReadText(file, MaxFileBytes);
                }
                catch (InvalidDataException)
                {
                    // A file outside the supported text format stays on disk and outside the managed manifest.
                    continue;
                }

                if (content is not null)
                {
                    size += Utf8.GetByteCount(content);
                    if (files.Count >= MaxBundleFiles || size > MaxBundleBytes)
                    {
                        throw new InvalidDataException($"Bundle at '{directory}' exceeds the supported size.");
                    }
                    files.Add(path, content);
                }
            }
        }
        return files;
    }

    public static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        SkillPaths.AssertNoReparsePoints(root);
        return Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 32
        });
    }

    public static string? ReadText(string path, int maxBytes)
    {
        SkillPaths.AssertNoReparsePoints(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > maxBytes)
            {
                throw new InvalidDataException($"File '{path}' exceeds the supported size.");
            }
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var text = Utf8.GetString(bytes);
            if (text.Contains('\0'))
            {
                throw new InvalidDataException($"File '{path}' must contain UTF-8 text.");
            }
            return ContentHasher.NormalizeLineEndings(text);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"File '{path}' is not valid UTF-8 text.", exception);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static async Task ApplyFilesAsync(string directory, Dictionary<string, string> desired,
        IReadOnlyDictionary<string, string> previousHashes, Action<string>? beforeDelete = null)
    {
        PreflightBundle(directory, desired.Keys);
        foreach (var (relative, text) in desired)
        {
            var file = SkillPaths.Resolve(directory, SkillPaths.SupportingPath(relative));
            await WriteIfUnchangedAsync(file, text, previousHashes.GetValueOrDefault(relative)).ConfigureAwait(false);
        }
        foreach (var (relative, hash) in previousHashes)
        {
            if (!desired.ContainsKey(relative))
            {
                var file = SkillPaths.Resolve(directory, SkillPaths.SupportingPath(relative));
                DeleteOwnedFile(file, hash, beforeDelete);
            }
        }
    }

    public static async Task WriteIfUnchangedAsync(string path, string text, string? previousHash)
    {
        var previous = ReadText(path, MaxMarkdownBytes);
        var currentHash = previous is null ? null : ContentHasher.ComputeFileRevisionHash(previous);
        if (currentHash == ContentHasher.ComputeFileRevisionHash(text))
        {
            return;
        }
        if (currentHash != previousHash)
        {
            throw new SkillConflictException($"'{path}' changed while SkillHub was preparing the write.");
        }
        await AtomicFileWriter.WriteTextAtomicallyAsync(path, text).ConfigureAwait(false);
    }

    public static void DeleteOwnedFile(string path, string hash, Action<string>? beforeDelete = null)
    {
        var content = ReadText(path, MaxMarkdownBytes);
        if (content is null)
        {
            return;
        }
        if (!string.Equals(ContentHasher.ComputeFileRevisionHash(content), hash, StringComparison.Ordinal))
        {
            throw new SkillConflictException($"'{path}' changed before it could be withdrawn.");
        }

        beforeDelete?.Invoke(path);
        File.Delete(path);
    }

    public SyncLedger ReadLedger()
    {
        SkillPaths.AssertNoReparsePoints(discovery.LedgerFilePath);
        if (!File.Exists(discovery.LedgerFilePath))
        {
            return new SyncLedger();
        }

        var json = File.ReadAllText(discovery.LedgerFilePath, Utf8);
        var ledger = JsonSerializer.Deserialize<SyncLedger>(json, JsonOptions)
            ?? throw new InvalidDataException("The sync ledger is empty.");
        if (ledger.Version is < 1 or > 2 || ledger.Skills is null)
        {
            throw new InvalidDataException("The sync ledger version is unsupported.");
        }
        return ledger;
    }

    public Task SaveLedgerAsync(SyncLedger ledger) => AtomicFileWriter.WriteTextAtomicallyAsync(
        discovery.LedgerFilePath, JsonSerializer.Serialize(ledger, JsonOptions) + "\n");
}
