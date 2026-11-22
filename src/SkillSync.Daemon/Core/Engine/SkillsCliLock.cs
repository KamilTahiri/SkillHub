using System.Text.Json;
using System.Text.RegularExpressions;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>
/// Links skills installed by the skills.sh CLI (<c>npx skills add owner/repo</c>) to their upstream repository. The CLI
/// records each install in a lock file next to the skills folder (<c>~/.agents/.skill-lock.json</c> for global installs,
/// <c>skills-lock.json</c> in a project). When SkillHub adopts such a skill it stores the source as <c>import_source</c>, so the
/// skill joins its source pack, never gets the personal learning footer, and can be shared again as an <c>npx skills add</c> command.
/// </summary>
public static class SkillsCliLock
{
    private static readonly string[] LockNames = [".skill-lock.json", "skills-lock.json", ".skills-lock.json"];
    private static readonly Regex Repository = new(@"\A[A-Za-z0-9-]{1,100}/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.Compiled);

    public sealed record Link(string Source, string? SkillPath);

    /// <summary>The lock entry for the skill whose main file is <paramref name="skillFile"/>, or null.</summary>
    public static Link? Find(string skillFile, string slug)
    {
        var skillFolder = Path.GetDirectoryName(Path.GetFullPath(skillFile));
        var skillsRoot = skillFolder is null ? null : Path.GetDirectoryName(skillFolder);
        if (skillsRoot is null) return null;
        var folders = new List<string> { skillsRoot };
        if (Path.GetDirectoryName(skillsRoot) is { } toolRoot)
        {
            folders.Add(toolRoot);
            if (Path.GetDirectoryName(toolRoot) is { } projectRoot) folders.Add(projectRoot);
        }
        foreach (var folder in folders)
        foreach (var name in LockNames)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                if (Read(File.ReadAllText(path), slug, Path.GetFileName(skillFolder!)) is { } link) return link;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    /// <summary>Reads one lock file: <c>{"skills": {"name": {"source": "owner/repo", "skillPath": "skills/name/SKILL.md"}}}</c>
    /// (also an array of entries with a <c>name</c>). Sources may be owner/repo or a GitHub URL.</summary>
    public static Link? Read(string json, string slug, string folderName)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        var skills = document.RootElement.TryGetProperty("skills", out var value) ? value : document.RootElement;
        IEnumerable<(string Name, JsonElement Entry)> entries = skills.ValueKind switch
        {
            JsonValueKind.Object => skills.EnumerateObject().Select(property => (property.Name, property.Value)),
            JsonValueKind.Array => skills.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => (Text(item, "name") ?? string.Empty, item)),
            _ => []
        };
        foreach (var (name, entry) in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(name, slug, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase)) continue;
            var source = NormalizeSource(Text(entry, "source") ?? Text(entry, "sourceUrl") ?? Text(entry, "repo") ?? Text(entry, "repository"));
            if (source is null) continue;
            var skillPath = (Text(entry, "skillPath") ?? Text(entry, "path"))?.Replace('\\', '/').TrimStart('/');
            return new Link(source, string.IsNullOrWhiteSpace(skillPath) || skillPath.Contains("..") ? null : skillPath);
        }
        return null;
    }

    /// <summary>owner/repo from "owner/repo", "github:owner/repo", "https://github.com/owner/repo(.git)", or null.</summary>
    public static string? NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var value = source.Trim();
        value = Regex.Replace(value, @"\A(?:github:|git\+)", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\A(?:https?://)?(?:www\.)?github\.com/", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\Agit@github\.com:", string.Empty, RegexOptions.IgnoreCase);
        var parts = value.Split(['?', '#'])[0].Trim('/').Split('/');
        if (parts.Length < 2) return null;
        var repo = parts[0] + "/" + Regex.Replace(parts[1], @"\.git\z", string.Empty, RegexOptions.IgnoreCase);
        return Repository.IsMatch(repo) ? repo : null;
    }

    /// <summary>Records the upstream link on a skill SkillHub is adopting from disk. Existing import metadata always wins.</summary>
    public static bool Apply(CanonicalSkillModel model, string skillFile)
    {
        if (model.Passthrough.ContainsKey("import_source")) return false;
        if (Find(skillFile, model.Name) is not { } link) return false;
        model.Passthrough["import_source"] = link.Source;
        if (link.SkillPath is not null) model.Passthrough["import_path"] = link.SkillPath;
        model.Passthrough["import_via"] = "npx skills";
        return true;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
