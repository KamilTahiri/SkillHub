using System.Text.Json;
using System.Threading.Channels;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Desktop;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Api;

public static class SkillRestEndpoints
{
    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static void MapSkillRestApi(this WebApplication app, int listenPort)
    {
        var api = app.MapGroup("/api/v1");

        // 0. Native Desktop Window Focus & Theme Endpoints
        api.MapPost("/window/show", (SkillHubWindowController windowController) =>
        {
            windowController.RequestShowWindow();
            return Results.Ok(new { status = "shown" });
        });

        api.MapPost("/window/theme", (WindowThemeRequest req, SkillHubWindowController windowController) =>
        {
            var isDark = !string.Equals(req.Theme, "light", StringComparison.OrdinalIgnoreCase);
            windowController.RequestSetTheme(isDark);
            return Results.Ok(new { status = "applied", theme = isDark ? "dark" : "light" });
        });

        // 1. Daemon Status, Install Status & MCP Configuration Snippets
        api.MapGet("/status", (
            ProviderDiscoveryService discovery,
            InstallManager installManager,
            SyncEngine engine,
            TunnelSupervisorService tunnel) =>
        {
            var (stdioCmd, stdioArgs) = installManager.BuildMcpStdioCommandSpec();
            var report = installManager.GetStatusReport();
            var allSkills = engine.ListSkills();
            var userSkillCount = allSkills.Count(s => string.Equals(s.SourceCategory, "user", StringComparison.OrdinalIgnoreCase));
            var templateSkillCount = allSkills.Count(s => string.Equals(s.SourceCategory, "template", StringComparison.OrdinalIgnoreCase));
            var systemSkillCount = allSkills.Count(s => string.Equals(s.SourceCategory, "system", StringComparison.OrdinalIgnoreCase));
            var pluginSkillCount = allSkills.Count(s => string.Equals(s.SourceCategory, "plugin", StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new
            {
                status = "online",
                product = "SkillHub",
                version = "1.0.0",
                listen_port = listenPort,
                canonical_root = discovery.CanonicalSkillsRoot,
                ledger_path = discovery.LedgerFilePath,
                conflicts_root = discovery.ConflictsRoot,
                skill_count = userSkillCount,
                template_skill_count = templateSkillCount,
                system_skill_count = systemSkillCount,
                plugin_skill_count = pluginSkillCount,
                total_skill_count = allSkills.Count,
                install = new
                {
                    is_auto_start_registered = report.IsAutoStartRegistered,
                    auto_start_method = report.AutoStartMethod,
                    auto_start_target = report.AutoStartTarget,
                    executable_path = report.ExecutablePath,
                    mcp_clients = report.McpClientConfigs.Select(c => new
                    {
                        client_id = c.ClientId,
                        display_name = c.DisplayName,
                        config_path = c.ConfigFilePath,
                        parent_exists = c.ParentDirectoryExists,
                        file_exists = c.ConfigFileExists,
                        is_registered = c.IsSkillHubRegistered
                    })
                },
                mcp_endpoints = new
                {
                    streamable_http = $"http://127.0.0.1:{listenPort}/mcp",
                    sse = $"http://127.0.0.1:{listenPort}/sse",
                    stdio_command = stdioCmd,
                    stdio_args = stdioArgs
                },
                tunnel = new
                {
                    status = tunnel.Status,
                    public_base_url = tunnel.PublicBaseUrl,
                    public_mcp_url = tunnel.PublicMcpUrl,
                    last_error = tunnel.LastError
                },
                recent_activity = engine.GetRecentActivity().OrderByDescending(a => a.TimestampUtc).Take(30)
            });
        });

        // 1b. Inject SkillHub MCP Server into all detected AI client config files & register OS Auto-Start
        api.MapPost("/mcp-clients/inject", (InstallManager installManager) =>
        {
            var updatedClients = installManager.InjectMcpConfigIntoAllDetectedClients();
            var (autoOk, method, details) = installManager.RegisterOsAutoStart(port: listenPort);

            return Results.Ok(new
            {
                status = "injected",
                auto_start_registered = autoOk,
                auto_start_method = method,
                auto_start_details = details,
                mcp_clients = updatedClients.Select(c => new
                {
                    client_id = c.ClientId,
                    display_name = c.DisplayName,
                    config_path = c.ConfigFilePath,
                    is_registered = c.IsSkillHubRegistered
                })
            });
        });

        // 2. Discovered Providers List
        api.MapGet("/providers", (ProviderDiscoveryService discovery) =>
        {
            var list = discovery.DiscoverProviders().Select(p => new
            {
                provider = p.Provider.ToString().ToLowerInvariant(),
                display_name = p.DisplayName,
                provider_root = p.ProviderRootDirectory,
                skills_root = p.SkillsRootDirectory,
                is_installed = p.IsInstalled,
                file_pattern = p.FilePatternDescription
            });

            return Results.Ok(list);
        });

        api.MapPost("/providers/{providerName}/initialize", async (
            string providerName,
            ProviderDiscoveryService discovery,
            MultiRootFileWatcher watcher,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<ProviderType>(providerName, ignoreCase: true, out var provider) ||
                provider == ProviderType.Canonical)
            {
                return Results.BadRequest(new { error = $"Invalid provider '{providerName}'." });
            }

            discovery.EnsureProviderSkillsDirectoryExists(provider);
            watcher.RefreshProviderWatchers();
            await engine.SynchronizeAllAsync(ct).ConfigureAwait(false);

            return Results.Ok(new
            {
                status = "initialized",
                provider = provider.ToString().ToLowerInvariant()
            });
        });

        // 3. List Skills
        api.MapGet("/skills", (
            string? query,
            string? tag,
            string? category,
            SyncEngine engine) =>
        {
            var skills = engine.ListSkills(query, tag, includeDisabled: true, category: category);
            return Results.Ok(skills);
        });

        // 4. Get Single Skill + Transpiled Previews
        api.MapGet("/skills/{name}", (string name, SyncEngine engine) =>
        {
            var skill = engine.GetSkill(name);
            if (skill is null)
            {
                return Results.NotFound(new { error = $"Skill '{name}' not found." });
            }

            var previews = engine.PreviewAllTranspilations(skill);
            return Results.Ok(new
            {
                skill,
                transpiled_previews = previews
            });
        });

        // 5. Create or Upsert Skill
        api.MapPost("/skills", async (
            CanonicalSkillModel model,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(model.Name) && string.IsNullOrWhiteSpace(model.Title))
            {
                return Results.BadRequest(new { error = "Skill name is required." });
            }

            if (string.IsNullOrWhiteSpace(model.Name))
            {
                model.Name = ProviderDiscoveryService.NormalizeSlug(model.Title);
            }

            var saved = await engine.UpsertSkillDirectAsync(model, "webui:save", ct).ConfigureAwait(false);
            var previews = engine.PreviewAllTranspilations(saved);
            return Results.Ok(new
            {
                skill = saved,
                transpiled_previews = previews
            });
        });

        // 5b. Import Skill from GitHub / skills.sh or Local File
        api.MapPost("/skills/import", async (
            SkillImportRequest req,
            SkillImporterService importer,
            CancellationToken ct) =>
        {
            try
            {
                var result = await importer.ImportAsync(req, ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    imported_count = result.ImportedCount,
                    count = result.ImportedCount,
                    skills = result.Skills,
                    primary_skill = result.Skills.FirstOrDefault(),
                    message = result.Message
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 5c. Browse Curated Community Skill Repositories
        api.MapGet("/community/repos", () =>
        {
            return Results.Ok(new
            {
                repositories = SkillImporterService.GetCuratedRepositories()
            });
        });

        // 5d. Browse or Search Community Skills in GitHub / skills.sh
        api.MapGet("/community/skills", async (
            string? repo,
            string? q,
            SkillImporterService importer,
            CancellationToken ct) =>
        {
            try
            {
                IReadOnlyList<CommunitySkillSummary> skills;
                if (!string.IsNullOrWhiteSpace(repo))
                {
                    skills = await importer.BrowseRepositorySkillsAsync(repo, q, ct).ConfigureAwait(false);
                }
                else
                {
                    skills = await importer.SearchCommunitySkillsAsync(q, ct).ConfigureAwait(false);
                }

                return Results.Ok(new
                {
                    count = skills.Count,
                    skills
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 5e. Preview a Community Skill's Full Markdown Before Importing
        api.MapGet("/community/preview", async (
            string repo,
            string path,
            string? git_ref,
            SkillImporterService importer,
            CancellationToken ct) =>
        {
            try
            {
                var preview = await importer.PreviewCommunitySkillAsync(
                    repo,
                    path,
                    string.IsNullOrWhiteSpace(git_ref) ? "HEAD" : git_ref,
                    ct).ConfigureAwait(false);
                return Results.Ok(preview);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 5f. Rename a Tag Across All Skills
        api.MapPost("/tags/rename", async (
            TagRenameRequest req,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.OldTag) || string.IsNullOrWhiteSpace(req.NewTag))
            {
                return Results.BadRequest(new { error = "Both old_tag and new_tag are required." });
            }

            var updatedCount = await engine.RenameTagAcrossSkillsAsync(req.OldTag, req.NewTag, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                status = "renamed",
                old_tag = req.OldTag.Trim().TrimStart('#').ToLowerInvariant(),
                new_tag = ProviderDiscoveryService.NormalizeSlug(req.NewTag),
                updated_skills = updatedCount
            });
        });

        // 5g. Delete a Tag Across All Skills
        api.MapPost("/tags/delete", async (
            TagDeleteRequest req,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Tag))
            {
                return Results.BadRequest(new { error = "Parameter 'tag' is required." });
            }

            var updatedCount = await engine.DeleteTagAcrossSkillsAsync(req.Tag, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                status = "deleted",
                tag = req.Tag.Trim().TrimStart('#').ToLowerInvariant(),
                updated_skills = updatedCount
            });
        });

        // 6. Update Skill
        api.MapPut("/skills/{name}", async (
            string name,
            CanonicalSkillModel model,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            model.Name = ProviderDiscoveryService.NormalizeSlug(name);
            var saved = await engine.UpsertSkillDirectAsync(model, "webui:update", ct).ConfigureAwait(false);
            var previews = engine.PreviewAllTranspilations(saved);
            return Results.Ok(new
            {
                skill = saved,
                transpiled_previews = previews
            });
        });

        // 7. Toggle Provider Enablement for a Skill
        api.MapPatch("/skills/{name}/providers/{providerName}", async (
            string name,
            string providerName,
            ProviderToggleRequest body,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            var existing = engine.GetSkill(name);
            if (existing is null)
            {
                return Results.NotFound(new { error = $"Skill '{name}' not found." });
            }

            if (!Enum.TryParse<ProviderType>(providerName, ignoreCase: true, out var provider) ||
                provider == ProviderType.Canonical)
            {
                return Results.BadRequest(new { error = $"Invalid provider '{providerName}'." });
            }

            existing.SetEnabledFor(provider, body.Enabled);
            var saved = await engine.UpsertSkillDirectAsync(existing, $"webui:toggle-{provider.ToString().ToLowerInvariant()}", ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                skill = saved,
                transpiled_previews = engine.PreviewAllTranspilations(saved)
            });
        });

        // 8. Delete Skill
        api.MapDelete("/skills/{name}", async (
            string name,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            try
            {
                var deleted = await engine.DeleteSkillDirectAsync(name, "webui:delete", allowBootstrapDelete: false, ct).ConfigureAwait(false);
                if (!deleted)
                {
                    return Results.NotFound(new { error = $"Skill '{name}' not found." });
                }

                return Results.Ok(new { status = "deleted", name });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 9. Live Transpilation Preview
        api.MapPost("/preview", (CanonicalSkillModel draft, SyncEngine engine) =>
        {
            if (string.IsNullOrWhiteSpace(draft.Name) && !string.IsNullOrWhiteSpace(draft.Title))
            {
                draft.Name = ProviderDiscoveryService.NormalizeSlug(draft.Title);
            }

            var previews = engine.PreviewAllTranspilations(draft);
            return Results.Ok(new { transpiled_previews = previews });
        });

        // 10. Force Full Resync
        api.MapPost("/resync", async (
            MultiRootFileWatcher watcher,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            watcher.RefreshProviderWatchers();
            await engine.SynchronizeAllAsync(ct).ConfigureAwait(false);
            var userCount = engine.ListSkills().Count(s => !string.Equals(s.Name, SyncEngine.BootstrapSkillName, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { status = "resynced", count = userCount });
        });

        // 11. Toggle Cloudflare Quick Tunnel
        api.MapPost("/tunnel/toggle", (TunnelToggleRequest req, TunnelSupervisorService tunnel) =>
        {
            if (req.Enable)
            {
                var (started, msg) = tunnel.TryStartTunnel();
                return Results.Ok(new
                {
                    started,
                    message = msg,
                    status = tunnel.Status,
                    public_mcp_url = tunnel.PublicMcpUrl,
                    last_error = tunnel.LastError
                });
            }
            else
            {
                tunnel.StopTunnel();
                return Results.Ok(new
                {
                    started = false,
                    message = "Cloudflare Quick Tunnel stopped.",
                    status = tunnel.Status
                });
            }
        });

        // 12. Real-time Server-Sent Events Stream (/api/v1/events)
        api.MapGet("/events", async (
            HttpContext context,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var channel = Channel.CreateUnbounded<SyncActivityNotification>();
            void Handler(SyncActivityNotification notification) => channel.Writer.TryWrite(notification);

            engine.ActivityPublished += Handler;
            try
            {
                await context.Response.WriteAsync("event: connected\ndata: {\"status\":\"listening\"}\n\n", ct).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var json = JsonSerializer.Serialize(item, WebJsonOptions);
                    await context.Response.WriteAsync($"event: activity\ndata: {json}\n\n", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                engine.ActivityPublished -= Handler;
                channel.Writer.TryComplete();
            }
        });
    }
}

public sealed record ProviderToggleRequest(bool Enabled);
public sealed record TunnelToggleRequest(bool Enable);
public sealed record WindowThemeRequest(string Theme);
public sealed record TagRenameRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("old_tag")] string OldTag,
    [property: System.Text.Json.Serialization.JsonPropertyName("new_tag")] string NewTag);
public sealed record TagDeleteRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("tag")] string Tag);
