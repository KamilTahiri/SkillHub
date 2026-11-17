using System.Text.RegularExpressions;

namespace SkillSync.Daemon.Core.Parsing;

/// <summary>
/// The continuous-learning note appended to provider copies of personal skills. AI tools read skills straight from their
/// folders without calling MCP, so the note travels with the skill. It is only ever added on the way out and always removed
/// on the way in, so it never becomes part of the canonical skill and never causes a sync loop.
/// </summary>
public static partial class LearningFooter
{
    public const string Start = "<!-- skillhub:learning:start -->";
    public const string End = "<!-- skillhub:learning:end -->";

    public static string Text(string skillName) =>
        $"""
        {Start}
        ---
        *SkillHub continuous learning:* do not judge this skill early and do not suggest changes on routine tasks. Only after you solved a non-trivial problem, found an outdated step here, or discovered a reusable project pattern that would clearly improve this skill, ask once at the end of your reply: "Want me to update your `{skillName}` skill in SkillHub with <one-line improvement>?" If the user says yes, update it with SkillHub's `update_skill` MCP tool (SkillHub keeps the previous version in History). If they want to decide later, save it with `propose_skill_improvement`.
        {End}
        """;

    public static string Append(string markdown, string skillName) =>
        markdown.TrimEnd('\r', '\n') + "\n\n" + Text(skillName).Replace("\r\n", "\n") + "\n";

    [GeneratedRegex(@"(?:\r?\n)*<!-- skillhub:learning:start -->[\s\S]*?<!-- skillhub:learning:end -->(?:\r?\n)*", RegexOptions.CultureInvariant)]
    private static partial Regex Block();

    /// <summary>Removes the note (and the blank lines around it) wherever it appears.</summary>
    public static string Strip(string markdown) =>
        markdown.Contains(Start, StringComparison.Ordinal) ? Block().Replace(markdown, "\n").TrimEnd('\r', '\n') + "\n" : markdown;
}
