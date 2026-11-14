using SkillSync.Daemon.Core.Engine;
using Xunit;

namespace SkillSync.Tests;

public sealed class ExploreInputTests
{
    [Theory]
    [InlineData("maintain-email-details", "Keeps a detailed decision log", "productivity")]
    [InlineData("build-guide", "A guide to building things", "coding")]
    [InlineData("webapp-testing", "Toolkit for testing local web applications using Playwright", "testing")]
    [InlineData("react-best-practices", "React and Next.js performance optimization", "web")]
    [InlineData("pipeline", "Deploy with GitHub Actions CI", "devops")]
    [InlineData("pdf", "Comprehensive PDF manipulation toolkit", "productivity")]
    [InlineData("query-helper", "Write SQL against Postgres", "data")]
    public void InferTopicMatchesWholeWords(string name, string description, string expected) =>
        Assert.Equal(expected, SkillImporterService.InferTopic(name, description));

    [Theory]
    [InlineData("anthropics/skills", true)]
    [InlineData("anthropics/skills/skills/pdf", true)]
    [InlineData("vercel-labs/agent-skills@react-best-practices", true)]
    [InlineData("https://github.com/anthropics/skills/tree/main/skills/pdf", true)]
    [InlineData("skills.sh/vercel-labs/agent-skills", true)]
    [InlineData("npx skills add vercel-labs/agent-skills --skill web-design-guidelines", true)]
    [InlineData("react testing", false)]
    [InlineData("pdf", false)]
    [InlineData("", false)]
    public void RepositoryInputIsRecognized(string query, bool expected) =>
        Assert.Equal(expected, SkillImporterService.LooksLikeRepositoryInput(query));
}
