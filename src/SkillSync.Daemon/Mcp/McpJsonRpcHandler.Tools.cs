using System.Text.Json;
using System.Text.Json.Nodes;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Mcp;

/// <summary>The MCP tool implementations behind tools/call.</summary>
public sealed partial class McpJsonRpcHandler
{
    private string ExecuteListSkills(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>();
        var tag = args["tag"]?.GetValue<string>();
        var category = args["category"]?.GetValue<string>();
        var includeDisabled = args["include_disabled"]?.GetValue<bool>() ?? true;

        var skills = _syncEngine.ListSkills(query, tag, includeDisabled, category)
            .Select(s => new
            {
                name = s.Name,
                title = s.GetEffectiveTitle(),
                version = s.Version,
                source_category = s.SourceCategory,
                origin_provider = s.OriginProvider,
                is_catalog_only = s.IsCatalogOnly,
                description = s.Description,
                when_to_use = s.WhenToUse,
                summary_short = s.SummaryShort,
                tags = s.Tags,
                enabled_providers = new
                {
                    claude = s.Targets.Claude.Enabled,
                    gemini = s.Targets.Gemini.Enabled,
                    codex = s.Targets.Codex.Enabled,
                    cursor = s.Targets.Cursor.Enabled
                },
                destinations = s.Destinations
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            count = skills.Count,
            skills
        }, IndentedJson);
    }

    private string ExecuteGetSkill(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var includeFiles = args["include_supporting_files"]?.GetValue<bool>() ?? false;

        var skill = _syncEngine.GetSkill(name)
            ?? throw new KeyNotFoundException($"Skill '{name}' was not found in SkillHub.");

        var response = new
        {
            name = skill.Name,
            title = skill.GetEffectiveTitle(),
            version = skill.Version,
            source_category = skill.SourceCategory,
            origin_provider = skill.OriginProvider,
            is_catalog_only = skill.IsCatalogOnly,
            description = skill.Description,
            when_to_use = skill.WhenToUse,
            summary_short = skill.SummaryShort,
            tags = skill.Tags,
            targets = skill.Targets,
            instructions_markdown = skill.InstructionsMarkdown,
            supporting_file_paths = skill.SupportingFiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            supporting_files = includeFiles ? skill.SupportingFiles : null,
            revision = skill.Revision,
            catalog_id = skill.CatalogId,
            destinations = skill.Destinations
        };

        return JsonSerializer.Serialize(response, IndentedJson);
    }

    private async Task<string> ExecuteCreateSkillAsync(JsonObject args, CancellationToken ct)
    {
        var rawName = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var slug = rawName;
        if (string.IsNullOrEmpty(slug))
        {
            throw new ArgumentException($"Invalid skill slug '{rawName}'.");
        }

        var description = args["description"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'description' is required.");
        var instructions = args["instructions_markdown"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'instructions_markdown' is required.");

        var model = new CanonicalSkillModel
        {
            Name = slug,
            Title = args["title"]?.GetValue<string>()?.Trim() ?? CanonicalSkillModel.HumanizeSlug(slug),
            Version = "1.0.0",
            SourceCategory = "user",
            OriginProvider = "My Skill",
            Description = description.Trim(),
            WhenToUse = args["when_to_use"]?.GetValue<string>()?.Trim() ?? string.Empty,
            SummaryShort = args["summary_short"]?.GetValue<string>()?.Trim() ?? string.Empty,
            InstructionsMarkdown = instructions.Trim(),
            Tags = ReadStringList(args["tags"]).Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        var allowedTools = ReadStringList(args["allowed_tools"]);
        if (allowedTools.Count > 0)
        {
            model.Targets.Claude.AllowedTools = allowedTools;
        }

        var cursorGlobs = ReadStringList(args["cursor_globs"]);
        if (cursorGlobs.Count > 0)
        {
            model.Targets.Cursor.Globs = cursorGlobs;
        }

        if (args["cursor_always_apply"] is not null)
        {
            model.Targets.Cursor.AlwaysApply = args["cursor_always_apply"]!.GetValue<bool>();
        }

        if (args["supporting_files"] is JsonArray filesArr)
        {
            foreach (var item in filesArr.OfType<JsonObject>())
            {
                var relPath = item["relative_path"]?.GetValue<string>();
                var content = item["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(relPath) && content is not null)
                {
                    var path = SkillPaths.SupportingPath(relPath);
                    if (!model.SupportingFiles.TryAdd(path, content))
                    {
                        throw new ArgumentException($"Duplicate supporting file '{path}'.");
                    }
                }
            }
        }

        var created = await _syncEngine.CreateSkillAsync(model, "mcp:create_skill", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "created",
            name = created.Name,
            message = $"Skill '{created.Name}' created in canonical store and synced to all enabled AI providers."
        }, IndentedJson);
    }

    private async Task<string> ExecuteUpdateSkillAsync(JsonObject args, CancellationToken ct)
    {
        var rawName = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var slug = rawName;

        var saved = await _syncEngine.EditSkillAsync(slug, updated =>
        {
            if (args.TryGetPropertyValue("title", out var titleNode) && titleNode is not null)
            {
                updated.Title = titleNode.GetValue<string>().Trim();
            }

            if (args.TryGetPropertyValue("description", out var descNode) && descNode is not null)
            {
                updated.Description = descNode.GetValue<string>().Trim();
            }

            if (args.TryGetPropertyValue("when_to_use", out var whenNode) && whenNode is not null)
            {
                updated.WhenToUse = whenNode.GetValue<string>().Trim();
            }

            if (args.TryGetPropertyValue("summary_short", out var shortNode) && shortNode is not null)
            {
                updated.SummaryShort = shortNode.GetValue<string>().Trim();
            }

            if (args.TryGetPropertyValue("instructions_markdown", out var bodyNode) && bodyNode is not null)
            {
                updated.InstructionsMarkdown = bodyNode.GetValue<string>().Trim();
            }

            if (args.TryGetPropertyValue("tags", out var tagsNode) && tagsNode is not null)
            {
                updated.Tags = ReadStringList(tagsNode).Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (args.TryGetPropertyValue("allowed_tools", out var toolsNode) && toolsNode is not null)
            {
                updated.Targets.Claude.AllowedTools = ReadStringList(toolsNode);
            }

            if (args.TryGetPropertyValue("cursor_globs", out var globsNode) && globsNode is not null)
            {
                var globs = ReadStringList(globsNode);
                updated.Targets.Cursor.Globs = globs;
            }

            if (args.TryGetPropertyValue("cursor_always_apply", out var alwaysNode) && alwaysNode is not null)
            {
                updated.Targets.Cursor.AlwaysApply = alwaysNode.GetValue<bool>();
            }

            if (args["upsert_supporting_files"] is JsonArray upsertArr)
            {
                foreach (var item in upsertArr.OfType<JsonObject>())
                {
                    var relPath = item["relative_path"]?.GetValue<string>();
                    var content = item["content"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(relPath) && content is not null)
                    {
                        updated.SupportingFiles[SkillPaths.SupportingPath(relPath)] = content;
                    }
                }
            }

            if (args["delete_supporting_files"] is JsonArray deleteArr)
            {
                foreach (var path in ReadStringList(deleteArr))
                {
                    var cleanRel = SkillPaths.SupportingPath(path);
                    updated.SupportingFiles.Remove(cleanRel);
                }
            }
        }, "mcp:update_skill", ct, args["revision"]?.GetValue<long>()).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "updated",
            name = saved.Name,
            message = $"Skill '{saved.Name}' updated non-destructively and synced across providers."
        }, IndentedJson);
    }

    private async Task<string> ExecuteDeleteSkillAsync(JsonObject args, CancellationToken ct)
    {
        var rawName = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var slug = rawName;

        var deleted = await _syncEngine.DeleteSkillDirectAsync(slug, "mcp:delete_skill", ct).ConfigureAwait(false);
        if (!deleted)
        {
            throw new KeyNotFoundException($"Skill '{slug}' was not found.");
        }

        return JsonSerializer.Serialize(new
        {
            status = "deleted",
            name = slug,
            message = $"Skill '{slug}' removed from canonical store and all AI providers."
        }, IndentedJson);
    }

    private async Task<string> ExecuteManageSkillTagsAsync(JsonObject args, CancellationToken ct)
    {
        var action = (args["action"]?.GetValue<string>() ?? "list").Trim().ToLowerInvariant();
        switch (action)
        {
            case "list":
            {
                return JsonSerializer.Serialize(new { action, tags = _syncEngine.ListTags() }, StructuredJson);
            }

            case "create":
            {
                var tag = await _syncEngine.CreateTagAsync(args["tag"]?.GetValue<string>()
                    ?? throw new ArgumentException("tag is required."), ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { action, tag }, StructuredJson);
            }

            case "undo":
            {
                if (!Guid.TryParse(args["undo_id"]?.GetValue<string>(), out var id))
                {
                    throw new ArgumentException("undo_id is required.");
                }
                var updated = await _syncEngine.UndoTagChangeAsync(id, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { action, updated_skills = updated }, StructuredJson);
            }

            case "add":
            case "remove":
            {
                var skillName = args["skill_name"]?.GetValue<string>()
                    ?? args["name"]?.GetValue<string>()
                    ?? throw new ArgumentException($"Parameter 'skill_name' is required for action '{action}'.");
                var rawTags = ReadStringList(args["tags"]).Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (args["tag"]?.GetValue<string>() is { } singleTagVal && !string.IsNullOrWhiteSpace(singleTagVal))
                {
                    rawTags.Add(singleTagVal);
                }
                var tags = rawTags
                    .Select(SyncEngine.NormalizeTag)
                    .ToList();

                var saved = await _syncEngine.EditSkillAsync(skillName, updated =>
                {
                    updated.Tags = action == "add"
                        ? updated.Tags.Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : updated.Tags.Except(tags, StringComparer.OrdinalIgnoreCase).ToList();
                }, $"mcp:tags_{action}", ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    status = "ok",
                    action,
                    skill_name = saved.Name,
                    tags = saved.Tags
                }, IndentedJson);
            }

            case "rename":
            {
                var oldTag = args["old_tag"]?.GetValue<string>()
                    ?? throw new ArgumentException("Parameter 'old_tag' is required for action 'rename'.");
                var newTag = args["new_tag"]?.GetValue<string>()
                    ?? throw new ArgumentException("Parameter 'new_tag' is required for action 'rename'.");

                var change = await _syncEngine.ChangeTagAsync(oldTag, newTag, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    status = "renamed",
                    old_tag = oldTag,
                    new_tag = SyncEngine.NormalizeTag(newTag),
                    updated_skills = change.UpdatedSkills,
                    undo_id = change.UndoId
                }, IndentedJson);
            }

            case "delete":
            {
                var tags = ReadStringList(args["tags"]);
                if (tags.Count == 0 && args["old_tag"]?.GetValue<string>() is { } singleTag)
                {
                    tags.Add(singleTag);
                }

                if (tags.Count == 0) throw new ArgumentException("Supply at least one tag.");
                tags = tags.Select(SyncEngine.NormalizeTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var totalUpdated = 0;
                Guid? undoId = null;
                foreach (var tag in tags)
                {
                    var change = await _syncEngine.ChangeTagAsync(tag, null, ct).ConfigureAwait(false);
                    totalUpdated += change.UpdatedSkills;
                    undoId = change.UndoId ?? undoId;
                }

                return JsonSerializer.Serialize(new
                {
                    status = "deleted",
                    deleted_tags = tags,
                    updated_skills = totalUpdated,
                    undo_id = tags.Count == 1 ? undoId : null
                }, IndentedJson);
            }

            default:
                throw new ArgumentException($"Unsupported tag action '{action}'.");
        }
    }

    private async Task<string> ExecuteToggleSkillProviderAsync(JsonObject args, CancellationToken ct)
    {
        var name = args["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'name' is required.");
        var providerStr = args["provider"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'provider' is required.");
        var enabled = args["enabled"]?.GetValue<bool>()
            ?? throw new ArgumentException("Parameter 'enabled' is required.");

        if (!Enum.TryParse<ProviderType>(providerStr, ignoreCase: true, out var provider) ||
            !Enum.IsDefined(provider) || provider == ProviderType.Canonical)
        {
            throw new ArgumentException($"Invalid provider '{providerStr}'. Expected claude, gemini, codex, or cursor.");
        }

        var saved = await _syncEngine.SetProviderAsync(name, provider, enabled, "mcp:toggle_skill_provider", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "updated",
            name = saved.Name,
            provider = provider.ToString().ToLowerInvariant(),
            enabled,
            source_category = saved.SourceCategory,
            enabled_providers = new
            {
                claude = saved.Targets.Claude.Enabled,
                gemini = saved.Targets.Gemini.Enabled,
                codex = saved.Targets.Codex.Enabled,
                cursor = saved.Targets.Cursor.Enabled
            }
        }, IndentedJson);
    }

    private async Task<string> ExecuteSearchCommunitySkillsAsync(JsonObject args, CancellationToken ct)
    {
        var query = args["query"]?.GetValue<string>();
        var repo = args["repo"]?.GetValue<string>();
        var source = args["source"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "all";
        var (limit, offset) = ReadPage(args, 50, 200);

        CommunityBrowseResult browse;
        if (!string.IsNullOrWhiteSpace(repo))
        {
            browse = await _importer.BrowseRepositoryAsync(repo, query, ct).ConfigureAwait(false);
        }
        else if (source is "skills.sh" or "skillssh")
        {
            browse = await _importer.BrowseRepositoryAsync(SkillsShDirectory.SourceId, query, ct).ConfigureAwait(false);
        }
        else if (source == "catalog")
        {
            browse = await _importer.BrowseRepositoryAsync("local/catalog", query, ct).ConfigureAwait(false);
        }
        else
        {
            browse = await _importer.SearchCommunityAsync(query, ct).ConfigureAwait(false);
            if (source == "curated")
            {
                browse = browse with
                {
                    Skills = browse.Skills.Where(skill => skill.Source != SkillsShDirectory.SourceId && skill.Repo != "local/catalog").ToList()
                };
            }
        }

        var rows = FilterKind(browse.Skills, args["kind"]?.GetValue<string>());
        var page = rows.Skip(offset).Take(limit).ToList();
        return JsonSerializer.Serialize(new
        {
            count = rows.Count,
            returned = page.Count,
            offset,
            has_more = offset + page.Count < rows.Count,
            searched = string.IsNullOrWhiteSpace(repo)
                ? source switch
                {
                    "skills.sh" or "skillssh" => new[] { "skills.sh" },
                    "catalog" => new[] { "local/catalog" },
                    "curated" => SkillImporterService.GetCuratedRepositories().Select(item => item.Repo).ToArray(),
                    _ => new[] { "skills.sh", "local/catalog" }.Concat(SkillImporterService.GetCuratedRepositories().Select(item => item.Repo)).ToArray()
                }
                : [browse.Repository?.Repo ?? repo],
            still_indexing = browse.PendingSources ?? [],
            repository = browse.Repository,
            curated_repositories = SkillImporterService.GetCuratedRepositories(),
            next_step = page.Count > 0
                ? "Call preview_community_skill with a result's repo, path and git_ref to read it before importing."
                : "Try other keywords, a specific repo, or source 'skills.sh'.",
            skills = page
        }, IndentedJson);
    }

    private async Task<string> ExecuteBrowseCommunityRepoAsync(JsonObject args, CancellationToken ct)
    {
        var repo = args["repo"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("Parameter 'repo' is required, for example 'anthropics/skills'.");
        var (limit, offset) = ReadPage(args, 100, 500);
        var browse = await _importer.BrowseRepositoryAsync(repo, args["query"]?.GetValue<string>(), ct).ConfigureAwait(false);
        var rows = FilterKind(browse.Skills, args["kind"]?.GetValue<string>());
        var page = rows.Skip(offset).Take(limit).ToList();
        return JsonSerializer.Serialize(new
        {
            repository = browse.Repository,
            count = rows.Count,
            returned = page.Count,
            offset,
            has_more = offset + page.Count < rows.Count,
            by_kind = rows.GroupBy(row => row.Kind).ToDictionary(group => group.Key, group => group.Count()),
            bundles = rows.Count(row => row.FileCount > 0),
            next_step = "Call preview_community_skill with an entry's repo, path and git_ref to read its SKILL.md and files.",
            skills = page.Select(row => new
            {
                row.Slug, row.Title, row.Description, row.Kind, row.Group, row.Repo, row.Path, row.GitRef,
                companion_files = row.FileCount, files_known = row.FilesKnown, row.IsInstalled, row.ExistingName, row.Topic
            })
        }, StructuredJson);
    }

    private async Task<string> ExecutePreviewCommunitySkillAsync(JsonObject args, CancellationToken ct)
    {
        var repo = args["repo"]?.GetValue<string>();
        var path = args["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Parameters 'repo' and 'path' are required. Use the values from search_community_skills or browse_community_repo.");
        var gitRef = args["git_ref"]?.GetValue<string>();
        var preview = await _importer.PreviewCommunitySkillAsync(repo, path, string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef, ct).ConfigureAwait(false);
        var maxChars = Math.Clamp(args["max_chars"]?.GetValue<int>() ?? 60000, 1000, 200000);
        var file = args["file"]?.GetValue<string>();
        var includeFiles = args["include_files"]?.GetValue<bool>() ?? false;

        async Task<string> ReadFile(string relative) =>
            await _importer.ReadPreviewFileAsync(preview.Repo, preview.Path, preview.Revision, relative, ct).ConfigureAwait(false);

        string? requestedText = null;
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!preview.SupportingPaths.Contains(file, StringComparer.Ordinal))
                throw new ArgumentException($"'{file}' is not part of this skill. Choose one of: {string.Join(", ", preview.SupportingPaths.Take(30))}.");
            requestedText = await ReadFile(file).ConfigureAwait(false);
        }
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        if (includeFiles)
        {
            var budget = 200 * 1024;
            foreach (var relative in preview.SupportingPaths.Take(20))
            {
                if (budget <= 0) break;
                try
                {
                    var text = await ReadFile(relative).ConfigureAwait(false);
                    contents[relative] = text.Length > budget ? text[..budget] + "\n… (truncated)" : text;
                    budget -= text.Length;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or CommunitySourceException)
                {
                    contents[relative] = "(could not read: " + error.Message + ")";
                }
            }
        }

        var instructions = preview.InstructionsMarkdown.Length > maxChars
            ? preview.InstructionsMarkdown[..maxChars] + "\n… (truncated; raise max_chars to read more)"
            : preview.InstructionsMarkdown;
        return JsonSerializer.Serialize(new
        {
            preview.Slug, preview.Title, preview.Repo, preview.Path, preview.Source, revision = preview.Revision, preview.License,
            preview.Kind, preview.Description, preview.WhenToUse,
            bundle = new
            {
                total_files = preview.SupportingPaths.Count + 1,
                is_single_file = preview.SupportingPaths.Count == 0,
                main_file = preview.Path.Split('/')[^1],
                companion_files = preview.SupportingPaths,
                not_importable = preview.ExcludedPaths ?? []
            },
            script_review = preview.Scripts ?? [],
            already_in_library = preview.IsInstalled, preview.ExistingName, preview.NameExists,
            instructions_markdown = instructions,
            file = requestedText is null ? null : new { path = file, text = requestedText },
            files = includeFiles ? contents : null,
            next_step = preview.IsInstalled
                ? "This skill is already in the library as '" + (preview.ExistingName ?? preview.Slug) + "'."
                : "To import, call prepare_skill_import with repo, path and git_ref = revision, then apply_skill_import (or import_community_skill with source '" + preview.Repo + "/" + preview.Path + "')."
        }, StructuredJson);
    }

    private static (int Limit, int Offset) ReadPage(JsonObject args, int fallback, int maximum)
    {
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? fallback, 1, maximum);
        var offset = Math.Max(0, args["offset"]?.GetValue<int>() ?? 0);
        return (limit, offset);
    }

    private static List<CommunitySkillSummary> FilterKind(IEnumerable<CommunitySkillSummary> rows, string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? rows.ToList()
            : rows.Where(row => string.Equals(row.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase) ||
                (kind.Trim().Equals("doc", StringComparison.OrdinalIgnoreCase) && row.Group == "docs")).ToList();

    private static readonly JsonSerializerOptions StructuredJson = new(CompactJson)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private async Task<string> ExecutePrepareImportAsync(JsonObject args, CancellationToken ct)
    {
        var selection = args.Deserialize<SkillImportSelection>(StructuredJson)!;
        var plan = await _importer.PrepareImportAsync(selection, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(plan, StructuredJson);
    }

    private async Task<string> ExecuteApplyImportAsync(JsonObject args, CancellationToken ct)
    {
        if (!Guid.TryParse(args["plan_id"]?.GetValue<string>(), out var id))
        {
            throw new ArgumentException("Invalid plan_id.");
        }
        var request = new SkillImportApplyRequest(args["revision"]!.GetValue<string>(), args["name"]?.GetValue<string>(),
            ReadStringList(args["providers"]));
        var model = await _importer.ApplyImportAsync(id, request, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { model.Name, model.Revision, model.Destinations }, StructuredJson);
    }

    private async Task<string> ExecuteImportCommunitySkillAsync(JsonObject args, CancellationToken ct)
    {
        var source = args["source"]?.GetValue<string>()
            ?? throw new ArgumentException("Parameter 'source' is required.");

        var res = await _importer.ImportAsync(new SkillImportRequest(Source: source, RawMarkdown: null, FileName: null), ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            status = "imported",
            imported_count = res.ImportedCount,
            skills = res.Skills.Select(s => new { name = s.Name, title = s.GetEffectiveTitle(), origin = s.OriginProvider }),
            message = res.Message
        }, IndentedJson);
    }

    private string ExecuteGetSyncStatus()
    {
        var providers = _discovery.DiscoverProviders()
            .Where(p => p.Provider != ProviderType.Canonical)
            .Select(p => new
            {
                provider = p.Provider.ToString().ToLowerInvariant(),
                display_name = p.DisplayName,
                is_installed = p.IsInstalled,
                skills_directory = p.SkillsRootDirectory
            })
            .ToList();

        var allSkills = _syncEngine.ListSkills();
        var userCount = allSkills.Count(s => string.Equals(s.SourceCategory, "user", StringComparison.OrdinalIgnoreCase));
        var syncedCount = _syncEngine.ListSkills(category: "synced").Count;
        var templateCount = allSkills.Count(s => string.Equals(s.SourceCategory, "template", StringComparison.OrdinalIgnoreCase));

        return JsonSerializer.Serialize(new
        {
            canonical_skills_root = _discovery.CanonicalSkillsRoot,
            counts = new
            {
                my_skills = userCount,
                active_and_synced = syncedCount,
                templates = templateCount,
                total_discovered = allSkills.Count
            },
            providers,
            recent_activity = _syncEngine.GetRecentActivity().Reverse().Take(10)
        }, IndentedJson);
    }

    private async Task<string> ExecuteCatalogCommandAsync(JsonObject args, bool activate, CancellationToken ct)
    {
        var id = args["catalog_id"]?.GetValue<string>() ?? throw new ArgumentException("catalog_id is required.");
        var skill = await _syncEngine.MaterializeCatalogSkillAsync(id, null, activate, "mcp:catalog", ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { skill.Name, skill.Revision, skill.Destinations }, CompactJson);
    }

    private async Task<string> ExecuteRestoreAsync(JsonObject args, CancellationToken ct)
    {
        var name = args["name"]?.GetValue<string>() ?? throw new ArgumentException("name is required.");
        var skill = await _syncEngine.RestoreSkillAsync(name, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { skill.Name, skill.Revision, skill.Destinations }, CompactJson);
    }
}
