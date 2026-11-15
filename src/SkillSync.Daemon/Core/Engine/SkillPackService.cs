using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

public sealed record SkillPackEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("content_hash")] string ContentHash);

public sealed record SkillPackManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("skills")] IReadOnlyList<SkillPackEntry> Skills);

public sealed record SkillPackPreviewSkill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("name_exists")] bool NameExists,
    [property: JsonPropertyName("same_as_existing")] bool SameAsExisting,
    [property: JsonPropertyName("suggested_name")] string SuggestedName,
    [property: JsonPropertyName("scripts")] IReadOnlyList<ScriptFinding> Scripts,
    [property: JsonPropertyName("instructions_markdown")] string InstructionsMarkdown,
    [property: JsonPropertyName("providers")] IReadOnlyList<string> Providers);

public sealed record SkillPackPreview(
    [property: JsonPropertyName("pack_id")] string PackId,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("created_by")] string? CreatedBy,
    [property: JsonPropertyName("skills")] IReadOnlyList<SkillPackPreviewSkill> Skills,
    [property: JsonPropertyName("skipped")] IReadOnlyList<string> Skipped);

/// <summary>What to do with one skill from a pack: import (new name allowed), replace the existing skill, or skip.</summary>
public sealed record SkillPackChoice(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("new_name")] string? NewName = null);

public sealed record SkillPackApplyRequest(
    [property: JsonPropertyName("choices")] IReadOnlyList<SkillPackChoice> Choices,
    [property: JsonPropertyName("keep_sync_settings")] bool KeepSyncSettings = false);

public sealed record SkillPackApplyResult(
    [property: JsonPropertyName("imported")] IReadOnlyList<string> Imported,
    [property: JsonPropertyName("replaced")] IReadOnlyList<string> Replaced,
    [property: JsonPropertyName("skipped")] IReadOnlyList<string> Skipped);

/// <summary>
/// <c>.skillpack.zip</c>: a portable archive of canonical skills. Layout: <c>skillpack.json</c> (manifest) and
/// <c>skills/&lt;name&gt;/SKILL.md</c> with every companion file beside it. Imports are reviewed first: names that
/// collide, executable content and unreadable entries are reported before anything is written.
/// </summary>
public sealed class SkillPackService(SyncEngine engine)
{
    public const string Format = "skillhub.skillpack";
    public const int MaxPackBytes = 20 * 1024 * 1024;
    private const int MaxEntries = 4096;
    private const long MaxExpandedBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, List<(SkillPackPreviewSkill Preview, CanonicalSkillModel Model)> Skills)> _plans = new();

    public static string Version => typeof(SkillPackService).Assembly.GetName().Version?.ToString(3) ?? "1";

    /// <summary>Zips the named skills. With no names, every skill in My Skills (a full backup).</summary>
    public byte[] Export(IReadOnlyList<string>? names)
    {
        var models = (names is { Count: > 0 }
                ? names.Select(name => engine.GetSkill(name) ?? throw new KeyNotFoundException($"Skill '{name}' was not found."))
                : engine.ListSkills(category: "user").Select(skill => engine.GetSkill(skill.Name)!).Where(skill => skill is not null))
            .DistinctBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (models.Count == 0) throw new ArgumentException("There are no skills to export.");

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = new List<SkillPackEntry>();
            foreach (var model in models)
            {
                var copy = model.Clone();
                copy.IsCatalogOnly = false;
                var markdown = engine.RenderForProvider(ProviderType.Canonical, copy);
                Write(zip, $"skills/{copy.Name}/SKILL.md", markdown);
                var files = (copy.SupportingFiles ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                foreach (var (relative, content) in files) Write(zip, $"skills/{copy.Name}/{SkillPaths.NormalizeRelative(relative)}", content);
                entries.Add(new SkillPackEntry(copy.Name, copy.GetEffectiveTitle(), copy.Description, copy.Revision,
                    files.Select(pair => SkillPaths.NormalizeRelative(pair.Key)).ToArray(), ContentHasher.ComputeSemanticHash(copy)));
            }
            Write(zip, "skillpack.json", JsonSerializer.Serialize(new SkillPackManifest(Format, 1, DateTimeOffset.UtcNow, "SkillHub " + Version, entries), Json));
        }
        return buffer.ToArray();
    }

    public static string SuggestFileName(IReadOnlyList<string>? names) =>
        names is { Count: 1 } ? ProviderDiscoveryService.NormalizeSlug(names[0]) + ".skillpack.zip"
        : names is { Count: > 1 } ? $"skillhub-{names.Count}-skills.skillpack.zip"
        : $"skillhub-backup-{DateTime.Now:yyyy-MM-dd}.skillpack.zip";

    private static void Write(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")));
    }

    /// <summary>Reads a pack and returns what would be imported. Nothing is written until <see cref="ApplyAsync"/>.</summary>
    public SkillPackPreview Preview(byte[] bytes, string? fileName)
    {
        if (bytes.Length == 0) throw new ArgumentException("The file is empty.");
        if (bytes.Length > MaxPackBytes) throw new ArgumentException("Skill packs can be at most 20 MB.");
        foreach (var expired in _plans.Where(pair => pair.Value.Expires < DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
            _plans.TryRemove(expired, out _);

        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new ArgumentException("This file is not a .skillpack.zip archive."); }
        using var archive = zip;
        if (archive.Entries.Count > MaxEntries) throw new ArgumentException("This pack has too many files.");

        SkillPackManifest? manifest = null;
        var manifestEntry = archive.GetEntry("skillpack.json");
        if (manifestEntry is not null)
        {
            try { manifest = JsonSerializer.Deserialize<SkillPackManifest>(ReadText(manifestEntry) ?? "null"); }
            catch (JsonException) { throw new ArgumentException("The pack's skillpack.json is not valid."); }
            if (manifest is not null && manifest.Format != Format) throw new ArgumentException("This archive is not a SkillHub skill pack.");
        }

        var skipped = new List<string>();
        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path == "skillpack.json") continue;
            var segments = path.Split('/');
            if (segments.Length < 3 || segments[0] != "skills") { skipped.Add(path + " (outside skills/)"); continue; }
            expanded += entry.Length;
            if (expanded > MaxExpandedBytes) throw new ArgumentException("This pack expands to more than 64 MB.");
            string relative;
            try { relative = segments.Length == 3 && segments[2] == "SKILL.md" ? "SKILL.md" : SkillPaths.SupportingPath(string.Join('/', segments.Skip(2))); }
            catch (ArgumentException) { skipped.Add(path + " (unsupported file)"); continue; }
            var text = entry.Length > SkillStorage.MaxFileBytes ? null : ReadText(entry);
            if (text is null) { skipped.Add(path + " (binary or too large)"); continue; }
            if (!groups.TryGetValue(segments[1], out var files)) groups[segments[1]] = files = new(StringComparer.Ordinal);
            files[relative] = text;
        }

        var plan = new List<(SkillPackPreviewSkill, CanonicalSkillModel)>();
        var taken = engine.ListSkills().Select(skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, files) in groups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!files.Remove("SKILL.md", out var markdown)) { skipped.Add("skills/" + folder + " (no SKILL.md)"); continue; }
            var slug = ProviderDiscoveryService.NormalizeSlug(folder);
            if (slug.Length == 0) { skipped.Add("skills/" + folder + " (invalid name)"); continue; }
            CanonicalSkillModel model;
            try
            {
                model = engine.ParseCanonicalText(slug, markdown);
                model.Name = slug;
                model.SupportingFiles = new Dictionary<string, string>(files, SkillPaths.Comparer);
                SkillStorage.ValidateModel(model);
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or YamlDotNet.Core.YamlException)
            {
                skipped.Add("skills/" + folder + " (" + error.Message + ")");
                continue;
            }
            var existing = engine.GetSkill(slug);
            var exists = existing is not null && !existing.IsCatalogOnly;
            var same = exists && ContentHasher.ComputeSemanticHash(existing!) == ContentHasher.ComputeSemanticHash(model);
            var suggested = slug;
            for (var suffix = 2; taken.Contains(suggested); suffix++) suggested = $"{slug[..Math.Min(slug.Length, 58)]}-{suffix}";
            var providers = Enum.GetValues<ProviderType>().Where(provider => provider != ProviderType.Canonical && model.IsEnabledFor(provider))
                .Select(provider => provider.ToString().ToLowerInvariant()).ToArray();
            plan.Add((new SkillPackPreviewSkill(slug, model.GetEffectiveTitle(), model.Description, files.Keys.Order(StringComparer.Ordinal).ToArray(),
                exists, same, suggested, SkillDoctor.InspectScripts(files.ToDictionary(pair => pair.Key, pair => (string?)pair.Value), model.InstructionsMarkdown),
                model.InstructionsMarkdown, providers), model));
        }
        if (plan.Count == 0) throw new ArgumentException("No skills were found in this pack." + (skipped.Count > 0 ? " Skipped: " + string.Join("; ", skipped.Take(5)) : string.Empty));

        var id = Guid.NewGuid().ToString("N");
        _plans[id] = (DateTimeOffset.UtcNow.AddMinutes(30), plan);
        return new SkillPackPreview(id, fileName ?? "skillpack.zip", manifest?.CreatedAt, manifest?.CreatedBy, plan.Select(item => item.Item1).ToArray(), skipped);
    }

    public async Task<SkillPackApplyResult> ApplyAsync(string packId, SkillPackApplyRequest request, CancellationToken ct = default)
    {
        if (!_plans.TryGetValue(packId, out var plan) || plan.Expires < DateTimeOffset.UtcNow)
            throw new KeyNotFoundException("This pack review expired. Choose the file again.");
        var imported = new List<string>();
        var replaced = new List<string>();
        var skipped = new List<string>();
        var choices = request.Choices.ToDictionary(choice => choice.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (preview, source) in plan.Skills)
        {
            var choice = choices.GetValueOrDefault(preview.Name) ?? new SkillPackChoice(preview.Name, preview.NameExists ? "skip" : "import");
            if (choice.Action == "skip") { skipped.Add(preview.Name); continue; }
            var model = source.Clone();
            model.SourceCategory = "user";
            model.IsCatalogOnly = false;
            model.CatalogId = null;
            model.Passthrough["import_source"] = "skillpack";
            if (!request.KeepSyncSettings)
                foreach (var provider in Enum.GetValues<ProviderType>().Where(provider => provider != ProviderType.Canonical))
                    model.SetEnabledFor(provider, false);
            if (choice.Action == "replace")
            {
                if (!preview.NameExists) throw new ArgumentException($"'{preview.Name}' does not exist yet; import it instead.");
                await engine.UpsertSkillDirectAsync(model, "skillpack", ct).ConfigureAwait(false);
                replaced.Add(preview.Name);
            }
            else if (choice.Action == "import")
            {
                var name = ProviderDiscoveryService.NormalizeSlug(string.IsNullOrWhiteSpace(choice.NewName) ? preview.Name : choice.NewName);
                if (name.Length == 0 || name.Length > 64) throw new ArgumentException("Use 1 to 64 letters, numbers, or hyphens for " + preview.Name + ".");
                if (engine.GetSkill(name) is { IsCatalogOnly: false }) throw new ArgumentException($"A skill named '{name}' already exists. Choose another name or replace it.");
                model.Name = name;
                if (name != preview.Name) model.Title = model.GetEffectiveTitle();
                await engine.CreateSkillAsync(model, "skillpack", ct).ConfigureAwait(false);
                imported.Add(name);
            }
            else throw new ArgumentException("Choose import, replace or skip for " + preview.Name + ".");
        }
        _plans.TryRemove(packId, out _);
        return new SkillPackApplyResult(imported, replaced, skipped);
    }

    private static string? ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        if (bytes.AsSpan().IndexOf((byte)0) >= 0) return null;
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return null; }
    }
}
