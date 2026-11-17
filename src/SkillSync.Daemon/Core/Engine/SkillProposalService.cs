using System.Text.Json;
using System.Text.Json.Serialization;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Core.Engine;

/// <summary>A saved "improve this skill later" suggestion, usually written by an AI assistant during continuous learning.</summary>
public sealed record SkillProposal(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("instructions_markdown")] string? InstructionsMarkdown,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("when_to_use")] string? WhenToUse,
    [property: JsonPropertyName("files")] IReadOnlyDictionary<string, string>? Files,
    [property: JsonPropertyName("base_revision")] long BaseRevision,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>
/// Proposals are side files in <c>~/.skillhub/proposals/&lt;id&gt;.json</c> (no database). Applying one updates the skill through
/// the engine, so the previous version is kept in History and every enabled AI tool is synced.
/// </summary>
public sealed class SkillProposalService(SyncEngine engine)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Root => Path.Combine(engine.Discovery.SkillHubRoot, "proposals");

    public async Task<SkillProposal> ProposeAsync(string skill, string summary, string? instructions, string? description, string? whenToUse,
        IReadOnlyDictionary<string, string>? files, string source, CancellationToken ct = default)
    {
        var current = engine.GetSkill(skill) ?? throw new KeyNotFoundException($"Skill '{skill}' was not found.");
        if (current.IsCatalogOnly) throw new ArgumentException("Built-in skills are read-only. Clone it to My Skills first, then propose the change.");
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("Describe the improvement in one line (summary).");
        if (instructions is null && description is null && whenToUse is null && files is not { Count: > 0 })
            throw new ArgumentException("Include at least one change: instructions_markdown, description, when_to_use or files.");
        foreach (var path in files?.Keys ?? []) SkillPaths.SupportingPath(path);
        var proposal = new SkillProposal(Guid.NewGuid().ToString("N")[..12], current.Name, summary.Trim(),
            instructions is null ? null : LearningFooter.Strip(instructions), description, whenToUse, files, current.Revision,
            string.IsNullOrWhiteSpace(source) ? "mcp" : source, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Root);
        await AtomicFileWriter.WriteTextAtomicallyAsync(PathFor(proposal.Id), JsonSerializer.Serialize(proposal, Json), ct).ConfigureAwait(false);
        return proposal;
    }

    public IReadOnlyList<SkillProposal> List(string? skill = null)
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<SkillProposal>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var proposal = JsonSerializer.Deserialize<SkillProposal>(File.ReadAllText(file));
                if (proposal is not null && (skill is null || string.Equals(proposal.Skill, ProviderDiscoveryService.NormalizeSlug(skill), StringComparison.OrdinalIgnoreCase)))
                    result.Add(proposal);
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
        return result.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public SkillProposal Get(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) throw new KeyNotFoundException("That proposal no longer exists.");
        return JsonSerializer.Deserialize<SkillProposal>(File.ReadAllText(path)) ?? throw new InvalidDataException("The proposal is empty.");
    }

    /// <summary>Applies the proposal on top of the current skill and removes it. Returns the updated skill.</summary>
    public async Task<CanonicalSkillModel> ApplyAsync(string id, CancellationToken ct = default)
    {
        var proposal = Get(id);
        var model = engine.GetSkill(proposal.Skill) ?? throw new KeyNotFoundException($"Skill '{proposal.Skill}' no longer exists.");
        if (proposal.InstructionsMarkdown is not null) model.InstructionsMarkdown = proposal.InstructionsMarkdown;
        if (proposal.Description is not null) model.Description = proposal.Description;
        if (proposal.WhenToUse is not null) model.WhenToUse = proposal.WhenToUse;
        foreach (var (path, content) in proposal.Files ?? new Dictionary<string, string>()) model.SupportingFiles[SkillPaths.SupportingPath(path)] = content;
        var saved = await engine.UpsertSkillDirectAsync(model, "proposal:" + proposal.Source, ct).ConfigureAwait(false);
        File.Delete(PathFor(id));
        return saved;
    }

    public void Dismiss(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) throw new KeyNotFoundException("That proposal no longer exists.");
        File.Delete(path);
    }

    private string PathFor(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40 || !id.All(char.IsAsciiLetterOrDigit)) throw new KeyNotFoundException("That proposal does not exist.");
        return Path.Combine(Root, id + ".json");
    }
}
