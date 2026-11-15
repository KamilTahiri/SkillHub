using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
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
        WriteIndented = false
    };

    public static void MapSkillRestApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch (CommunitySourceException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
            }
            catch (SkillConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // 0. Native Desktop Window Focus & Theme Endpoints
        api.MapPost("/window/show", (SkillHubWindowController windowController) =>
        {
            windowController.RequestShowWindow();
            return Results.Ok(new { status = "shown" });
        });

        api.MapGet("/preferences", ([FromServices] DesktopPreferencesStore preferences, IDesktopStartup startup) =>
        {
            var settings = preferences.Read();
            return Results.Ok(new { theme = settings.Theme, close_to_tray = settings.CloseToTray, auto_start = startup.IsRegistered });
        });
        api.MapPatch("/preferences/theme", (ThemeRequest request, [FromServices] DesktopPreferencesStore preferences) =>
            Results.Ok(new { theme = preferences.SetTheme(request.Theme).Theme }));

        api.MapPatch("/preferences/tray", (BooleanPreferenceRequest request, [FromServices] DesktopPreferencesStore preferences) =>
            Results.Ok(new { enabled = preferences.SetCloseToTray(RequireEnabled(request)).CloseToTray }));
        api.MapPatch("/preferences/startup", (BooleanPreferenceRequest request, IDesktopStartup startup, HttpContext context) =>
        {
            var enabled = RequireEnabled(request);
            UpdateStartup(startup, context, enabled);
            return Results.Ok(new { enabled });
        });

        api.MapPut("/preferences", (DesktopPreferenceRequest request, [FromServices] DesktopPreferencesStore preferences,
            IDesktopStartup startup, HttpContext context) =>
        {
            if (request.Theme is not ("system" or "light" or "dark")) throw new ArgumentException("Choose a valid theme.");
            if (request.CloseToTray is not { } closeToTray || request.AutoStart is not { } autoStart)
                throw new ArgumentException("Supply close_to_tray and auto_start explicitly.");
            UpdateStartup(startup, context, autoStart);
            var settings = preferences.SetOptions(request.Theme, closeToTray);
            return Results.Ok(new { theme = settings.Theme, close_to_tray = settings.CloseToTray, auto_start = autoStart });
        });

        static bool RequireEnabled(BooleanPreferenceRequest request) =>
            request.Enabled ?? throw new ArgumentException("Supply enabled explicitly.");

        static void UpdateStartup(IDesktopStartup startup, HttpContext context, bool enabled)
        {
            var registered = startup.IsRegistered;
            if (registered != enabled)
            {
                if (enabled)
                {
                    var update = startup.RegisterOsAutoStart(port: context.Connection.LocalPort);
                    if (!update.Success) throw new ArgumentException(update.Details);
                }
                else
                {
                    var update = startup.UnregisterOsAutoStart();
                    if (!update.Success) throw new ArgumentException(update.Details);
                }
            }
        }

        api.MapPost("/mcp-clients/{id}", (string id, ClientConfigurationRequest request, InstallManager install) =>
            Results.Json(install.ConfigureClient(id, request.Action), WebJsonOptions));
        api.MapGet("/readiness", (MultiRootFileWatcher watcher) =>
        {
            var health = watcher.GetHealth();
            return Results.Json(new { ready = health.Running, watched_roots = health.Roots, error = health.LastError },
                statusCode: health.Running ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        api.MapGet("/skills/{name}/destination-review", async (string name, string path, SyncEngine engine, CancellationToken ct) =>
            Results.Json(await engine.ReviewDestinationAsync(name, path, ct).ConfigureAwait(false), WebJsonOptions));
        api.MapPost("/skills/{name}/destination-review", async (string name, ResolveDestinationRequest request,
            SyncEngine engine, CancellationToken ct) => SkillResult(engine,
                await engine.ResolveDestinationAsync(name, request.Path, request.Version, request.Action, ct).ConfigureAwait(false)));

        api.MapGet("/diagnostics", async (SyncEngine engine, MultiRootFileWatcher watcher, ProviderDiscoveryService discovery, CancellationToken ct) =>
        {
            var data = await engine.GetDiagnosticsAsync(ct).ConfigureAwait(false);
            var health = watcher.GetHealth();
            var names = discovery.DiscoverProviders().ToDictionary(item => item.Provider, item => item.DisplayName);
            var observable = discovery.GetObservableRoots();
            var folders = watcher.GetWatchedRoots().Select(path =>
            {
                var match = observable.FirstOrDefault(item => string.Equals(Path.GetFullPath(item.Root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                var isHome = string.Equals(Path.GetFullPath(path), Path.GetFullPath(discovery.UserHomeRoot), StringComparison.OrdinalIgnoreCase);
                return new
                {
                    path,
                    provider = isHome ? "home" : match.Root is null ? "other" : match.Provider.ToString().ToLowerInvariant(),
                    label = isHome ? "Your user folder" : match.Root is null ? "Other folder" : names.GetValueOrDefault(match.Provider, match.Provider.ToString()),
                    purpose = isHome ? "Notices when a new AI tool creates its folder, so SkillHub can start syncing it."
                        : match.Provider == ProviderType.Canonical ? "SkillHub's own copy of every skill (the source of truth)."
                        : "Picks up edits you make to skills inside this tool."
                };
            }).ToArray();
            return Results.Json(new
            {
                watcher = new { running = health.Running, roots = health.Roots, last_error = health.LastError, folders },
                conflicts = data.Conflicts,
                issues = data.Issues,
                trash = data.Trash,
                recent_activity = engine.GetRecentActivity().Reverse().Take(40)
            }, WebJsonOptions);
        });
        api.MapGet("/diagnostics/conflicts/preview", async (string id, SyncEngine engine, CancellationToken ct) =>
            Results.Json(await engine.GetConflictPreviewAsync(id, ct).ConfigureAwait(false), WebJsonOptions));
        api.MapPost("/diagnostics/conflicts/restore", async (ConflictRecoveryRequest request, SyncEngine engine, CancellationToken ct) =>
            Results.Json(await engine.RestoreConflictOverCurrentAsync(request.Id, request.Revision, ct).ConfigureAwait(false), WebJsonOptions));
        api.MapPost("/diagnostics/folders/open", (FolderOpenRequest request, MultiRootFileWatcher watcher) =>
        {
            // Only folders SkillHub itself watches can be opened, and only in File Explorer (never a browser).
            var match = watcher.GetWatchedRoots().FirstOrDefault(root => string.Equals(root, request.Path, StringComparison.OrdinalIgnoreCase));
            if (match is null || !Directory.Exists(match))
            {
                return Results.NotFound(new { error = "SkillHub only opens folders it is watching." });
            }
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(match);
            System.Diagnostics.Process.Start(start)?.Dispose();
            return Results.Json(new { opened = match }, WebJsonOptions);
        });
        api.MapPost("/diagnostics/conflicts/recover", async (ConflictRecoveryRequest request, SyncEngine engine, CancellationToken ct) =>
            SkillResult(engine, await engine.RecoverConflictCopyAsync(request.Id, request.Revision, ct).ConfigureAwait(false)));

        api.MapPost("/diagnostics/conflicts/undo", async (ConflictRecoveryRequest request, SyncEngine engine, CancellationToken ct) =>
            SkillResult(engine, await engine.UndoDestinationReplacementAsync(request.Id, request.Revision, ct).ConfigureAwait(false)));

        // 1. Daemon Status, Install Status & MCP Configuration Snippets
        api.MapGet("/status", (
            HttpContext context,
            ProviderDiscoveryService discovery,
            InstallManager installManager,
            SyncEngine engine) =>
        {
            var (stdioCmd, stdioArgs) = installManager.BuildMcpStdioCommandSpec();
            var report = installManager.GetStatusReport();
            var counts = engine.CountSkills();
            var listenPort = context.Connection.LocalPort;

            return Results.Ok(new
            {
                status = "online",
                product = "SkillHub",
                version = ProductVersion.Current,
                listen_port = listenPort,
                canonical_root = discovery.CanonicalSkillsRoot,
                ledger_path = discovery.LedgerFilePath,
                conflicts_root = discovery.ConflictsRoot,
                skill_count = counts.GetValueOrDefault("user"),
                template_skill_count = counts.GetValueOrDefault("template"),
                system_skill_count = counts.GetValueOrDefault("system"),
                plugin_skill_count = counts.GetValueOrDefault("plugin"),
                total_skill_count = counts.Values.Sum(),
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
                        is_registered = c.IsSkillHubRegistered,
                        detected = installManager.CanConfigure(c),
                        restart_required = installManager.IsRestartPending(c.ClientId),
                        install_evidence = installManager.DescribeInstall(c.ClientId),
                        error = c.Error
                    })
                },
                mcp_endpoints = new
                {
                    streamable_http = $"http://127.0.0.1:{listenPort}/mcp",
                    sse = $"http://127.0.0.1:{listenPort}/sse",
                    stdio_command = stdioCmd,
                    stdio_args = stdioArgs
                },
                tunnel = new { status = "disabled" },
                recent_activity = engine.GetRecentActivity().OrderByDescending(a => a.TimestampUtc).Take(30)
            });
        });

        // Repair client registrations independently of the login startup setting.
        api.MapPost("/mcp-clients/inject", (InstallManager installManager) =>
        {
            var updatedClients = installManager.InjectMcpConfigIntoAllDetectedClients();

            return Results.Ok(new
            {
                status = "injected",
                mcp_clients = updatedClients.Select(c => new
                {
                    client_id = c.ClientId,
                    display_name = c.DisplayName,
                    config_path = c.ConfigFilePath,
                    is_registered = c.IsSkillHubRegistered,
                    error = c.Error
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
                !Enum.IsDefined(provider) || provider == ProviderType.Canonical)
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
            int? offset,
            int? limit,
            SyncEngine engine) =>
        {
            if (offset is < 0 || limit is < 1 or > 500)
            {
                return Results.BadRequest(new { error = "Provide a nonnegative offset and a page size from 1 through 500." });
            }
            var skills = engine.ListSkillSummaries(query, tag, category, offset ?? 0, limit ?? int.MaxValue);
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

        api.MapGet("/skills/{name}/bundle", async (string name, SyncEngine engine, CancellationToken ct) =>
        {
            var skill = engine.GetSkill(name) ?? throw new KeyNotFoundException("Skill not found.");
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var files = new Dictionary<string, string> { ["SKILL.md"] = engine.PreviewAllTranspilations(skill)["canonical"] };
                foreach (var (path, content) in skill.SupportingFiles) files.Add(SkillPaths.SupportingPath(path), content);
                foreach (var (path, content) in files)
                {
                    await using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                    await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
                }
            }
            return Results.File(buffer.ToArray(), "application/zip", $"{skill.Name}.zip");
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

            var saved = await engine.CreateSkillAsync(model, "webui:save", ct).ConfigureAwait(false);
            var previews = engine.PreviewAllTranspilations(saved);
            return Results.Ok(new
            {
                skill = saved,
                transpiled_previews = previews
            });
        });

        api.MapPost("/imports/prepare", async (SkillImportSelection selection, SkillImporterService importer, CancellationToken ct) =>
            Results.Json(await importer.PrepareImportAsync(selection, ct).ConfigureAwait(false), WebJsonOptions));
        api.MapPost("/imports/{id:guid}/apply", async (Guid id, SkillImportApplyRequest request, SkillImporterService importer,
            SyncEngine engine, CancellationToken ct) =>
            SkillResult(engine, await importer.ApplyImportAsync(id, request, ct).ConfigureAwait(false)));

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
        api.MapGet("/community/repos", (SkillImporterService importer) =>
            Results.Ok(new { repositories = importer.GetSources() }));
        api.MapPost("/community/sources", async (CommunitySourceRequest request, SkillImporterService importer, CancellationToken ct) =>
            Results.Ok(new { repositories = await importer.PinSourceAsync(request.Repo ?? string.Empty, ct).ConfigureAwait(false) }));
        api.MapPost("/community/sources/remove", async (CommunitySourceRequest request, SkillImporterService importer, CancellationToken ct) =>
            Results.Ok(new { repositories = await importer.UnpinSourceAsync(request.Repo ?? string.Empty, ct).ConfigureAwait(false) }));

        // 5d. Browse or Search Community Skills in GitHub / skills.sh
        api.MapGet("/community/skills", async (string? repo, string? q, SkillImporterService importer, CancellationToken ct) =>
        {
            // A pasted repository or skill link opens that repository whichever source is selected.
            var result = string.IsNullOrWhiteSpace(repo) || repo == "featured/all" && !string.IsNullOrWhiteSpace(q) ||
                SkillImporterService.LooksLikeRepositoryInput(q)
                ? await importer.SearchCommunityAsync(q, ct).ConfigureAwait(false)
                : await importer.BrowseRepositoryAsync(repo, q, ct).ConfigureAwait(false);
            return Results.Ok(new { count = result.Skills.Count, skills = result.Skills, repository = result.Repository,
                pending_sources = result.PendingSources ?? [] });
        });

        api.MapGet("/community/preview", async (string repo, string path, string? git_ref,
            SkillImporterService importer, CancellationToken ct) =>
            Results.Ok(await importer.PreviewCommunitySkillAsync(repo, path, git_ref ?? "HEAD", ct).ConfigureAwait(false)));

        api.MapGet("/community/preview-file", async (string repo, string path, string revision, string file,
            SkillImporterService importer, CancellationToken ct) =>
            Results.Ok(new { text = await importer.ReadPreviewFileAsync(repo, path, revision, file, ct).ConfigureAwait(false) }));

        api.MapGet("/tags", (SyncEngine engine) => Results.Json(engine.ListTags(), WebJsonOptions));
        api.MapPost("/tags", async (TagDeleteRequest request, SyncEngine engine, CancellationToken ct) =>
            Results.Ok(new { tag = await engine.CreateTagAsync(request.Tag, ct).ConfigureAwait(false) }));
        api.MapPost("/tags/rename", async (TagRenameRequest request, SyncEngine engine, CancellationToken ct) =>
            Results.Json(await engine.ChangeTagAsync(request.OldTag, request.NewTag, ct).ConfigureAwait(false), WebJsonOptions));
        api.MapPost("/tags/delete", async (TagDeleteRequest request, SyncEngine engine, CancellationToken ct) =>
            Results.Json(await engine.ChangeTagAsync(request.Tag, null, ct).ConfigureAwait(false), WebJsonOptions));
        api.MapPost("/tags/undo/{id:guid}", async (Guid id, SyncEngine engine, CancellationToken ct) =>
            Results.Ok(new { updated_skills = await engine.UndoTagChangeAsync(id, ct).ConfigureAwait(false) }));
        api.MapPatch("/skills/{name}/tags", async (string name, SkillTagPatch request, SyncEngine engine, CancellationToken ct) =>
            SkillResult(engine, await engine.SetSkillTagsAsync(name, request.Tag, request.Add, ct).ConfigureAwait(false)));

        api.MapPut("/skills/{name}", async (string name, JsonElement body, SyncEngine engine, CancellationToken ct) =>
        {
            var model = body.Deserialize<CanonicalSkillModel>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new ArgumentException("A skill object is required.");
            if (model.Revision <= 0)
            {
                throw new SkillConflictException("A current skill revision is required.");
            }
            var saved = await engine.EditSkillAsync(name, current =>
            {
                if (body.TryGetProperty("title", out _)) current.Title = model.Title;
                if (body.TryGetProperty("description", out _)) current.Description = model.Description;
                if (body.TryGetProperty("when_to_use", out _)) current.WhenToUse = model.WhenToUse;
                if (body.TryGetProperty("summary_short", out _)) current.SummaryShort = model.SummaryShort;
                if (body.TryGetProperty("instructions_markdown", out _)) current.InstructionsMarkdown = model.InstructionsMarkdown;
                if (body.TryGetProperty("tags", out _)) current.Tags = model.Tags;
                if (body.TryGetProperty("supporting_files", out _)) current.SupportingFiles = model.SupportingFiles;
                if (body.TryGetProperty("targets", out var targets))
                {
                    if (targets.ValueKind != JsonValueKind.Object ||
                        targets.EnumerateObject().Any(target => target.Value.ValueKind != JsonValueKind.Object))
                    {
                        throw new ArgumentException("Each target must be a JSON object.");
                    }
                    if (targets.TryGetProperty("claude", out var claude))
                    {
                        if (claude.TryGetProperty("enabled", out _)) current.Targets.Claude.Enabled = model.Targets.Claude.Enabled;
                        if (claude.TryGetProperty("allowed_tools", out _)) current.Targets.Claude.AllowedTools = model.Targets.Claude.AllowedTools;
                        if (claude.TryGetProperty("model", out _)) current.Targets.Claude.Model = model.Targets.Claude.Model;
                    }
                    if (targets.TryGetProperty("gemini", out var gemini) && gemini.TryGetProperty("enabled", out _))
                        current.Targets.Gemini.Enabled = model.Targets.Gemini.Enabled;
                    if (targets.TryGetProperty("codex", out var codex) && codex.TryGetProperty("enabled", out _))
                        current.Targets.Codex.Enabled = model.Targets.Codex.Enabled;
                    if (targets.TryGetProperty("cursor", out var cursor))
                    {
                        if (cursor.TryGetProperty("enabled", out _)) current.Targets.Cursor.Enabled = model.Targets.Cursor.Enabled;
                        if (cursor.TryGetProperty("globs", out _)) current.Targets.Cursor.Globs = model.Targets.Cursor.Globs;
                        if (cursor.TryGetProperty("always_apply", out _)) current.Targets.Cursor.AlwaysApply = model.Targets.Cursor.AlwaysApply;
                    }
                }
            }, "webui:update", ct, model.Revision).ConfigureAwait(false);
            return SkillResult(engine, saved);
        });

        api.MapPost("/catalog/{id}/activate", async (string id, SyncEngine engine, CancellationToken ct) =>
            SkillResult(engine, await engine.MaterializeCatalogSkillAsync(id, null, activate: true, "webui:activate", ct).ConfigureAwait(false)));
        api.MapPost("/catalog/{id}/clone", async (string id, CanonicalSkillModel draft, SyncEngine engine, CancellationToken ct) =>
            SkillResult(engine, await engine.MaterializeCatalogSkillAsync(id, draft, activate: false, "webui:clone", ct).ConfigureAwait(false)));
        api.MapPost("/skills/{name}/restore", async (string name, SyncEngine engine, CancellationToken ct) =>
            SkillResult(engine, await engine.RestoreSkillAsync(name, ct).ConfigureAwait(false)));

        // 7. Toggle Provider Enablement for a Skill
        api.MapPatch("/skills/{name}/providers/{providerName}", async (
            string name,
            string providerName,
            ProviderToggleRequest body,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<ProviderType>(providerName, ignoreCase: true, out var provider) ||
                !Enum.IsDefined(provider) || provider == ProviderType.Canonical)
            {
                return Results.BadRequest(new { error = $"Invalid provider '{providerName}'." });
            }
            var saved = await engine.SetProviderAsync(name, provider, body.Enabled, $"webui:toggle-{providerName}", ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                skill = saved,
                transpiled_previews = engine.PreviewAllTranspilations(saved)
            });
        });

        api.MapPost("/skills/{name}/providers/{providerName}/repair", async (string name, string providerName,
            SyncEngine engine, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ProviderType>(providerName, ignoreCase: true, out var provider))
            {
                throw new ArgumentException("An external provider is required.");
            }
            return SkillResult(engine, await engine.RepairProviderAsync(name, provider, ct).ConfigureAwait(false));
        });

        // 8. Delete Skill
        api.MapDelete("/skills/{name}", async (
            string name,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            var deleted = await engine.DeleteSkillDirectAsync(name, "webui:delete", ct: ct).ConfigureAwait(false);
            return deleted ? Results.Ok(new { status = "deleted", name }) :
                Results.NotFound(new { error = $"Skill '{name}' not found." });
        });

        // 9. Live Transpilation Preview
        api.MapPost("/preview", (CanonicalSkillModel draft, SyncEngine engine) =>
        {
            if (string.IsNullOrWhiteSpace(draft.Name) && !string.IsNullOrWhiteSpace(draft.Title))
            {
                draft.Name = ProviderDiscoveryService.NormalizeSlug(draft.Title);
            }

            var previews = engine.PreviewAllTranspilations(draft);
            return Results.Ok(new { transpiled_previews = previews, effective_paths = engine.PreviewDestinationPaths(draft) });
        });

        api.MapSkillInsightEndpoints();

        // 10. Force Full Resync
        api.MapPost("/resync", async (
            MultiRootFileWatcher watcher,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            watcher.RefreshProviderWatchers();
            var report = await engine.SynchronizeAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "resynced", count = report.ManagedSkills, report });
        });

        api.MapPost("/tunnel/toggle", () => Results.Problem(
            "Public Quick Tunnel access is disabled.", statusCode: StatusCodes.Status410Gone));

        // 12. Real-time Server-Sent Events Stream (/api/v1/events)
        api.MapGet("/events", async (
            HttpContext context,
            SyncEngine engine,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var channel = Channel.CreateBounded<SyncActivityNotification>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            void Handler(SyncActivityNotification notification) => channel.Writer.TryWrite(notification);

            engine.ActivityPublished += Handler;
            try
            {
                await context.Response.WriteAsync("event: connected\ndata: {\"status\":\"listening\"}\n\n", ct).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                var last = 0L;
                if (long.TryParse(context.Request.Headers["Last-Event-ID"], out var after) && after > 0)
                {
                    var history = engine.GetRecentActivity();
                    if (history.Count == 0 || after > history.Max(item => item.Sequence) || after < history.Min(item => item.Sequence) - 1)
                    {
                        await context.Response.WriteAsync("event: reset\ndata: {}\n\n", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var item in history.Where(item => item.Sequence > after).OrderBy(item => item.Sequence))
                        {
                            await WriteActivityAsync(context, item, ct).ConfigureAwait(false);
                            last = item.Sequence;
                        }
                    }
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
                await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (item.Sequence <= last)
                    {
                        continue;
                    }
                    if (last > 0 && item.Sequence > last + 1)
                    {
                        await context.Response.WriteAsync("event: reset\ndata: {}\n\n", ct).ConfigureAwait(false);
                    }
                    await WriteActivityAsync(context, item, ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    last = item.Sequence;
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

    private static Task WriteActivityAsync(HttpContext context, SyncActivityNotification item, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(item, WebJsonOptions);
        return context.Response.WriteAsync($"id: {item.Sequence}\nevent: activity\ndata: {json}\n\n", ct);
    }

    private static IResult SkillResult(SyncEngine engine, CanonicalSkillModel skill) =>
        Results.Ok(new { skill, transpiled_previews = engine.PreviewAllTranspilations(skill) });
}

public sealed record ProviderToggleRequest(bool Enabled);
public sealed record ClientConfigurationRequest(string Action);
public sealed record ConflictRecoveryRequest(string Id, string Revision);
public sealed record FolderOpenRequest(string Path);
public sealed record TagRenameRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("old_tag")] string OldTag,
    [property: System.Text.Json.Serialization.JsonPropertyName("new_tag")] string NewTag);
public sealed record TagDeleteRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("tag")] string Tag);


public sealed record SkillTagPatch(string Tag, bool Add);

internal sealed record ThemeRequest(string Theme);

internal sealed record BooleanPreferenceRequest(bool? Enabled);

internal sealed record ResolveDestinationRequest(string Path, string Version, string Action);

public sealed record CommunitySourceRequest([property: System.Text.Json.Serialization.JsonPropertyName("repo")] string? Repo);
