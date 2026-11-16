using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Core.Transpilers;
using Xunit;

namespace SkillSync.Tests;

public sealed class TranspilerRoundTripTests
{
    private static CanonicalSkillModel CreateSampleRichCanonicalSkill()
    {
        return new CanonicalSkillModel
        {
            Name = "git-commit-helper",
            Version = "1.2.0",
            Description = "Generates conventional commit messages\nand atomic staging plans from working tree diffs.",
            WhenToUse = "Use when the user asks to commit changes,\nwrite a commit message, or split a diff.",
            SummaryShort = "Conventional commit message generator and atomic diff stager.",
            Tags = ["git", "workflow"],
            Targets = new SkillTargetsConfig
            {
                Claude = new ClaudeTargetConfig
                {
                    Enabled = true,
                    AllowedTools = ["Bash(git status:*)", "Bash(git diff:*)", "Read"],
                    Model = "sonnet"
                },
                Gemini = new GeminiTargetConfig { Enabled = true },
                Codex = new CodexTargetConfig { Enabled = true },
                Cursor = new CursorTargetConfig
                {
                    Enabled = true,
                    Globs = ["*.cs", "*.ts"],
                    AlwaysApply = false
                }
            },
            InstructionsMarkdown = "# Git Commit Helper\n\nRun `./scripts/analyze-diff.sh` before staging."
        };
    }

    [Fact]
    public void GeminiTranspiler_FormatsSingleLineDescription_AndPreservesClaudeAndCursorMetadataOnRoundTrip()
    {
        var original = CreateSampleRichCanonicalSkill();
        var geminiTranspiler = new GeminiTranspiler();

        // 1. Outbound Transpile to Gemini SKILL.md
        var geminiContent = geminiTranspiler.TranspileOutbound(original);

        Assert.True(FrontmatterParser.TrySplitFrontmatter(geminiContent, out var yaml, out _));
        var dict = FrontmatterParser.ParseYamlToDictionary(yaml);
        Assert.Equal("git-commit-helper", dict["name"]?.ToString());

        var geminiDesc = dict["description"]?.ToString() ?? string.Empty;
        Assert.DoesNotContain("\n", geminiDesc);
        Assert.Contains("Generates conventional commit messages and atomic staging plans from working tree diffs.", geminiDesc);
        Assert.Contains(" | Trigger: Use when the user asks to commit changes, write a commit message, or split a diff.", geminiDesc);

        // 2. Simulate Gemini editing the Markdown body & trigger condition directly on disk
        var modifiedGeminiContent = geminiContent.Replace(
            "Run `./scripts/analyze-diff.sh` before staging.",
            "Run `./scripts/analyze-diff.sh --strict` before staging.",
            StringComparison.Ordinal);

        // 3. Ingest back into Canonical model
        var merged = geminiTranspiler.MergeInbound("git-commit-helper", modifiedGeminiContent, original);

        // Verify updated instructions took effect
        Assert.Contains("--strict", merged.InstructionsMarkdown);

        // Verify Claude, Cursor, and Codex metadata were NOT lost
        Assert.Equal(["Bash(git status:*)", "Bash(git diff:*)", "Read"], merged.Targets.Claude.AllowedTools);
        Assert.Equal("sonnet", merged.Targets.Claude.Model);
        Assert.Equal(["*.cs", "*.ts"], merged.Targets.Cursor.Globs);
        Assert.False(merged.Targets.Cursor.AlwaysApply);
        Assert.Equal("Conventional commit message generator and atomic diff stager.", merged.SummaryShort);
        Assert.Equal(["git", "workflow"], merged.Tags);
    }

    [Fact]
    public void CodexTranspiler_UsesConciseSummary_AndPreservesFullDescriptionOnBodyEdit()
    {
        var original = CreateSampleRichCanonicalSkill();
        var codexTranspiler = new CodexTranspiler();

        var codexOutput = codexTranspiler.TranspileOutbound(original);
        Assert.Contains("Conventional commit message generator and atomic diff stager.", codexOutput);

        // Simulate editing instructions in ~/.agents/skills/git-commit-helper/SKILL.md
        var editedCodex = codexOutput + "\n## Additional Step\nVerify branch name.\n";

        var merged = codexTranspiler.MergeInbound("git-commit-helper", editedCodex, original);

        // Full multi-line canonical Description and WhenToUse must remain intact
        Assert.Equal(original.Description, merged.Description);
        Assert.Equal(original.WhenToUse, merged.WhenToUse);
        Assert.Equal(original.Targets.Claude.AllowedTools, merged.Targets.Claude.AllowedTools);
        Assert.Contains("Verify branch name.", merged.InstructionsMarkdown);
    }

    [Fact]
    public void CursorMdcTranspiler_RewritesRelativeScriptPaths_AndReversesOnInboundMerge()
    {
        var original = CreateSampleRichCanonicalSkill();
        var cursorTranspiler = new CursorMdcTranspiler();
        const string canonicalSkillDir = "C:/Users/dev/.skillsync/skills/git-commit-helper";

        var mdcContent = cursorTranspiler.TranspileOutbound(original, canonicalSkillDir);

        Assert.Contains("alwaysApply: false", mdcContent);
        Assert.Contains("C:/Users/dev/.skillsync/skills/git-commit-helper/scripts/analyze-diff.sh", mdcContent);

        // Simulate editing globs and body in Cursor .mdc file
        var editedMdc = mdcContent.Replace("alwaysApply: false", "alwaysApply: true", StringComparison.Ordinal);

        var merged = cursorTranspiler.MergeInbound("git-commit-helper", editedMdc, original, null, canonicalSkillDir);

        Assert.True(merged.Targets.Cursor.AlwaysApply);
        // Relative script path restored in Canonical Markdown body
        Assert.Contains("`scripts/analyze-diff.sh`", merged.InstructionsMarkdown);
        // Claude allowed-tools preserved
        Assert.Equal(original.Targets.Claude.AllowedTools, merged.Targets.Claude.AllowedTools);
    }
}

