using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;
using Xunit;

namespace SkillSync.Tests;

public sealed class LoopPreventionTests : IDisposable
{
    private readonly string _tempRoot;

    public LoopPreventionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"skillhub-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".claude", "skills"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".gemini", "skills"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".codex", "skills"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".cursor", "rules"));
    }

    [Fact]
    public void ContentHasher_NormalizesCrLfAndBom_ToIdenticalSha256()
    {
        const string unixText = "---\nname: test\n---\n\nHello World\n";
        const string windowsTextWithBom = "\uFEFF---\r\nname: test\r\n---\r\n\r\nHello World\r\n   ";

        var hash1 = ContentHasher.ComputeRawFileHash(unixText);
        var hash2 = ContentHasher.ComputeRawFileHash(windowsTextWithBom);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task SyncEngine_DetectsCodexViaDotCodex_AndDualSyncsToBothCodexAndAgentsDirs()
    {
        // Seed a pre-existing skill inside ~/.codex/skills/capture-reusable-lessons/SKILL.md
        var codexSkillDir = Path.Combine(_tempRoot, ".codex", "skills", "capture-reusable-lessons");
        Directory.CreateDirectory(codexSkillDir);
        await File.WriteAllTextAsync(
            Path.Combine(codexSkillDir, "SKILL.md"),
            "---\nname: capture-reusable-lessons\ndescription: Recognize valuable reusable lessons in the current chat.\n---\n\n# Capture Reusable Lessons\n");

        var discovery = new ProviderDiscoveryService(_tempRoot);
        Assert.True(discovery.DetectOpenAiCodexOrChatGpt());

        await using var engine = new SyncEngine(discovery);
        await engine.SynchronizeAllAsync();

        // 1. Pre-existing ~/.codex/skills skill must be imported into Canonical store (~/.skillhub/skills)
        var imported = engine.GetSkill("capture-reusable-lessons");
        Assert.NotNull(imported);
        Assert.Equal("Capture Reusable Lessons", imported!.GetEffectiveTitle());

        // 2. And synced out to Claude, Gemini, Cursor, AND both ~/.codex/skills & ~/.agents/skills
        Assert.True(File.Exists(Path.Combine(_tempRoot, ".claude", "skills", "capture-reusable-lessons", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_tempRoot, ".gemini", "skills", "capture-reusable-lessons", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_tempRoot, ".agents", "skills", "capture-reusable-lessons", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_tempRoot, ".cursor", "rules", "capture-reusable-lessons.mdc")));
    }

    [Fact]
    public async Task SyncEngine_DiscoversSystemAndTemplateSkills_AndSplitsEmbeddedTriggers()
    {
        // 1. Seed a Codex System skill in ~/.codex/skills/.system/skill-creator/SKILL.md
        var sysDir = Path.Combine(_tempRoot, ".codex", "skills", ".system", "skill-creator");
        Directory.CreateDirectory(sysDir);
        await File.WriteAllTextAsync(
            Path.Combine(sysDir, "SKILL.md"),
            "---\nname: skill-creator\ndescription: Create or update a Codex skill. Use when the user asks to author a new skill.\n---\n\n# Skill Creator\n\nFollow progressive disclosure principles.\n");

        // 2. Seed an OpenAI Template skill in ~/.codex/plugins/cache/openai-curated-remote/openai-templates/0.1.1/skills/artifact-template-analytics-dashboard/SKILL.md
        var tmplDir = Path.Combine(
            _tempRoot,
            ".codex",
            "plugins",
            "cache",
            "openai-curated-remote",
            "openai-templates",
            "0.1.1",
            "skills",
            "artifact-template-analytics-dashboard");
        Directory.CreateDirectory(tmplDir);
        await File.WriteAllTextAsync(
            Path.Combine(tmplDir, "SKILL.md"),
            "---\nname: artifact-template-analytics-dashboard\ndescription: Create a spreadsheet using the Analytics Dashboard template. Use when the user selects Analytics Dashboard.\n---\n\n# Analytics Dashboard\n\nMonitor funnel KPIs.\n");

        var discovery = new ProviderDiscoveryService(_tempRoot);
        await using var engine = new SyncEngine(discovery);
        await engine.SynchronizeAllAsync();

        var sysSkill = engine.GetSkill("skill-creator");
        Assert.NotNull(sysSkill);
        Assert.Equal("system", sysSkill!.SourceCategory);
        Assert.Equal("Codex System", sysSkill.OriginProvider);
        Assert.Equal("Create or update a Codex skill.", sysSkill.Description);
        Assert.Equal("Use when the user asks to author a new skill.", sysSkill.WhenToUse);

        var tmplSkill = engine.GetSkill("artifact-template-analytics-dashboard");
        Assert.NotNull(tmplSkill);
        Assert.Equal("template", tmplSkill!.SourceCategory);
        Assert.Equal("OpenAI Template", tmplSkill.OriginProvider);
        Assert.Equal("Analytics Dashboard", tmplSkill.GetEffectiveTitle());

        // Search across instructions body ("progressive disclosure")
        var matched = engine.ListSkills(query: "progressive disclosure");
        Assert.Contains(matched, s => s.Name == "skill-creator");
    }

    [Fact]
    public async Task SyncEngine_PreRegistersRawHashes_AndPreventsInfiniteWatcherLoops()
    {
        var discovery = new ProviderDiscoveryService(_tempRoot);
        await using var engine = new SyncEngine(discovery);

        var model = new CanonicalSkillModel
        {
            Name = "docker-healthcheck",
            Title = "Docker Healthcheck",
            Description = "Diagnoses container healthchecks and exit codes.",
            WhenToUse = "Use when a Docker container is unhealthy or crashing.",
            Targets = new SkillTargetsConfig
            {
                Claude = new ClaudeTargetConfig
                {
                    Enabled = true,
                    AllowedTools = ["Bash(docker ps:*)", "Bash(docker logs:*)"]
                }
            },
            InstructionsMarkdown = "# Docker Healthcheck\n\nInspect container logs first.",
            SupportingFiles = new Dictionary<string, string>
            {
                ["scripts/check.sh"] = "#!/usr/bin/env bash\ndocker ps\n"
            }
        };

        await engine.UpsertSkillDirectAsync(model, "unit-test");

        var claudePath = discovery.GetSkillPrimaryFilePath(ProviderType.Claude, "docker-healthcheck");
        var geminiPath = discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, "docker-healthcheck");
        var codexPath = discovery.GetSkillPrimaryFilePath(ProviderType.Codex, "docker-healthcheck");
        var cursorPath = discovery.GetSkillPrimaryFilePath(ProviderType.Cursor, "docker-healthcheck");

        Assert.True(File.Exists(claudePath));
        Assert.True(File.Exists(geminiPath));
        Assert.True(File.Exists(codexPath));
        Assert.True(File.Exists(cursorPath));
        Assert.True(File.Exists(Path.Combine(discovery.GetSkillDirectoryPath(ProviderType.Claude, "docker-healthcheck")!, "scripts", "check.sh")));

        // Verify Layer-1 Loop Guard matches the written file hashes for every provider
        foreach (var provider in new[] { ProviderType.Canonical, ProviderType.Claude, ProviderType.Gemini, ProviderType.Codex, ProviderType.Cursor })
        {
            var path = discovery.GetSkillPrimaryFilePath(provider, "docker-healthcheck");
            var diskContent = await File.ReadAllTextAsync(path);
            var rawHash = ContentHasher.ComputeRawFileHash(diskContent);
            Assert.True(engine.IsLoopGuardMatch(provider, "docker-healthcheck", rawHash));
        }

        // Simulate an external direct edit to ~/.gemini/skills/docker-healthcheck/SKILL.md
        var geminiDisk = await File.ReadAllTextAsync(geminiPath);
        var externallyEditedGemini = geminiDisk.Replace("Inspect container logs first.", "Inspect `docker inspect` state first.", StringComparison.Ordinal);
        await File.WriteAllTextAsync(geminiPath, externallyEditedGemini);

        engine.EnqueueFileSystemEvent(ProviderType.Gemini, "docker-healthcheck", SyncChangeKind.CreatedOrModified, geminiPath);

        for (var i = 0; i < 30; i++)
        {
            try
            {
                await using var stream = new FileStream(claudePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if ((await reader.ReadToEndAsync()).Contains("Inspect `docker inspect` state first.", StringComparison.Ordinal))
                    break;
            }
            catch (IOException) when (i < 29)
            {
                // Windows can briefly deny a read while the atomic rename is in progress.
            }

            await Task.Delay(50);
        }

        var updatedClaudeDisk = await File.ReadAllTextAsync(claudePath);
        Assert.Contains("Inspect `docker inspect` state first.", updatedClaudeDisk);
        Assert.Contains("Bash(docker ps:*)", updatedClaudeDisk);
    }

    [Fact]
    public async Task McpJsonRpcHandler_SupportsFullCrudLifecycle_AndProtectsBootstrapSkill()
    {
        var discovery = new ProviderDiscoveryService(_tempRoot);
        await using var engine = new SyncEngine(discovery);
        await BootstrapManager.EnsureBootstrapSkillSeededAsync(engine);

        var rpc = new McpJsonRpcHandler(engine, discovery: discovery);

        // 1. initialize
        var initResp = await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");
        Assert.NotNull(initResp);
        Assert.Contains("skillhub", initResp);

        // 2. tools/list
        var listToolsResp = await rpc.ProcessMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.NotNull(listToolsResp);
        Assert.Contains("create_skill", listToolsResp);
        Assert.Contains("update_skill", listToolsResp);

        // 3. tools/call -> create_skill
        var createReq = """
        {
          "jsonrpc": "2.0",
          "id": 3,
          "method": "tools/call",
          "params": {
            "name": "create_skill",
            "arguments": {
              "name": "sql-query-optimizer",
              "title": "SQL Query Optimizer",
              "description": "Optimizes PostgreSQL execution plans and indexing strategies.",
              "when_to_use": "Use when analyzing slow SQL queries or EXPLAIN ANALYZE output.",
              "instructions_markdown": "# SQL Optimizer\n\nAlways run EXPLAIN (ANALYZE, BUFFERS).",
              "allowed_tools": ["Bash(psql:*)"]
            }
          }
        }
        """;
        var createResp = await rpc.ProcessMessageAsync(createReq);
        Assert.NotNull(createResp);
        var createNode = JsonNode.Parse(createResp!)!;
        Assert.False(createNode["result"]!["isError"]!.GetValue<bool>());

        // Verify Gemini received single-line transpiled SKILL.md
        var geminiFile = discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, "sql-query-optimizer");
        Assert.True(File.Exists(geminiFile));
        var geminiText = await File.ReadAllTextAsync(geminiFile);
        Assert.Contains("Optimizes PostgreSQL execution plans and indexing strategies. | Trigger: Use when analyzing slow SQL queries or EXPLAIN ANALYZE output.", geminiText);

        // 4. Attempting to delete 00-skillhub-bootstrap via MCP must return tool error
        var deleteBootstrapReq = """
        {
          "jsonrpc": "2.0",
          "id": 4,
          "method": "tools/call",
          "params": {
            "name": "delete_skill",
            "arguments": {
              "name": "00-skillhub-bootstrap"
            }
          }
        }
        """;
        var deleteBootstrapResp = await rpc.ProcessMessageAsync(deleteBootstrapReq);
        var deleteBootstrapNode = JsonNode.Parse(deleteBootstrapResp!)!;
        Assert.True(deleteBootstrapNode["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SkillImporterService_ImportsMarkdownFile_AndSyncsAcrossProviders()
    {
        var discovery = new ProviderDiscoveryService(_tempRoot);
        await using var engine = new SyncEngine(discovery);
        var importer = new SkillImporterService(engine);

        const string markdown = """
        ---
        name: vercel-react-best-practices
        description: Modern React and Next.js performance patterns. Use when optimizing React components or server actions.
        ---

        # Vercel React Best Practices

        Prefer React Server Components by default and minimize client bundle size.
        """;

        var result = await importer.ImportAsync(new SkillImportRequest(
            Source: null,
            RawMarkdown: markdown,
            FileName: "SKILL.md"));
        Assert.Equal(1, result.ImportedCount);
        Assert.Single(result.Skills);
        Assert.Equal("vercel-react-best-practices", result.Skills[0].Name);
        Assert.Equal("Vercel React Best Practices", result.Skills[0].GetEffectiveTitle());
        Assert.Equal("Modern React and Next.js performance patterns.", result.Skills[0].Description);
        Assert.Equal("Use when optimizing React components or server actions.", result.Skills[0].WhenToUse);

        // Verify it was synced to Claude, Gemini, Codex, and Cursor
        Assert.True(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Claude, "vercel-react-best-practices")));
        Assert.True(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Gemini, "vercel-react-best-practices")));
        Assert.True(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Codex, "vercel-react-best-practices")));
        Assert.True(File.Exists(discovery.GetSkillPrimaryFilePath(ProviderType.Cursor, "vercel-react-best-practices")));
    }

    [Fact]
    public async Task SyncEngine_PreservesTemplateCategoryWhenSynced_AndSupportsTagCrudAndMcpTools()
    {
        var discovery = new ProviderDiscoveryService(_tempRoot);
        await using var engine = new SyncEngine(discovery);

        var templateModel = new CanonicalSkillModel
        {
            Name = "artifact-template-kpi-tracker",
            Title = "KPI Tracker",
            SourceCategory = "template",
            OriginProvider = "OpenAI Template",
            Description = "Track executive KPIs across quarters.",
            WhenToUse = "Use when building a KPI spreadsheet.",
            Tags = ["analytics", "finance"],
            InstructionsMarkdown = "# KPI Tracker\n\nBuild quarterly KPI tables."
        };

        var syncedTemplate = await engine.UpsertSkillDirectAsync(templateModel, "template-sync-test");
        Assert.False(syncedTemplate.IsCatalogOnly);
        Assert.Equal("template", syncedTemplate.SourceCategory);
        Assert.Equal("OpenAI Template", syncedTemplate.OriginProvider);

        // Must appear in Active & Synced ("synced") and Built-in & Templates ("template"), NOT in My Skills ("user")
        Assert.Contains(engine.ListSkills(category: "synced"), s => s.Name == "artifact-template-kpi-tracker");
        Assert.Contains(engine.ListSkills(category: "template"), s => s.Name == "artifact-template-kpi-tracker");
        Assert.DoesNotContain(engine.ListSkills(category: "user"), s => s.Name == "artifact-template-kpi-tracker");

        // Rename tag across skills
        var renamedCount = await engine.RenameTagAcrossSkillsAsync("finance", "fp-and-a");
        Assert.Equal(1, renamedCount);
        Assert.Contains("fp-and-a", engine.GetSkill("artifact-template-kpi-tracker")!.Tags);
        Assert.DoesNotContain("finance", engine.GetSkill("artifact-template-kpi-tracker")!.Tags);

        // Delete tag across skills
        var deletedCount = await engine.DeleteTagAcrossSkillsAsync("analytics");
        Assert.Equal(1, deletedCount);
        Assert.DoesNotContain("analytics", engine.GetSkill("artifact-template-kpi-tracker")!.Tags);

        // Test new MCP tools: manage_skill_tags, toggle_skill_provider, get_sync_status
        var rpc = new McpJsonRpcHandler(engine, discovery: discovery);
        var tagResp = await rpc.ProcessMessageAsync("""
        {
          "jsonrpc": "2.0",
          "id": 10,
          "method": "tools/call",
          "params": {
            "name": "manage_skill_tags",
            "arguments": {
              "action": "add",
              "skill_name": "artifact-template-kpi-tracker",
              "tag": "reporting"
            }
          }
        }
        """);
        var tagNode = JsonNode.Parse(tagResp!)!;
        Assert.False(tagNode["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("reporting", engine.GetSkill("artifact-template-kpi-tracker")!.Tags);

        var toggleResp = await rpc.ProcessMessageAsync("""
        {
          "jsonrpc": "2.0",
          "id": 11,
          "method": "tools/call",
          "params": {
            "name": "toggle_skill_provider",
            "arguments": {
              "name": "artifact-template-kpi-tracker",
              "provider": "cursor",
              "enabled": false
            }
          }
        }
        """);
        var toggleNode = JsonNode.Parse(toggleResp!)!;
        Assert.False(toggleNode["result"]!["isError"]!.GetValue<bool>());
        Assert.False(engine.GetSkill("artifact-template-kpi-tracker")!.Targets.Cursor.Enabled);

        var statusResp = await rpc.ProcessMessageAsync("""
        {
          "jsonrpc": "2.0",
          "id": 12,
          "method": "tools/call",
          "params": {
            "name": "get_sync_status",
            "arguments": {}
          }
        }
        """);
        var statusNode = JsonNode.Parse(statusResp!)!;
        Assert.False(statusNode["result"]!["isError"]!.GetValue<bool>());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
