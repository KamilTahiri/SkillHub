using System.Text.Json.Serialization;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Api;

public sealed record RestoreRevisionRequest([property: JsonPropertyName("revision")] long? Revision);
public sealed record ResolveUpdateRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("revision")] string Revision);

/// <summary>Skill Doctor, revision history and the upstream update inbox.</summary>
public static class SkillInsightEndpoints
{
    public static void MapSkillInsightEndpoints(this RouteGroupBuilder api)
    {
        // Skill Doctor for a saved skill, and for an unsaved draft from the editor.
        api.MapGet("/skills/{name}/doctor", (string name, SyncEngine engine) =>
        {
            var skill = engine.GetSkill(name) ?? throw new KeyNotFoundException("That skill was not found.");
            return Results.Ok(SkillDoctor.Lint(skill, engine.ListSkills()));
        });
        api.MapPost("/doctor", (CanonicalSkillModel draft, SyncEngine engine) =>
        {
            draft.SupportingFiles ??= [];
            return Results.Ok(SkillDoctor.Lint(draft, engine.ListSkills().Where(skill => skill.Name != draft.Name)));
        });

        api.MapGet("/skills/{name}/history", (string name, SyncEngine engine) =>
            Results.Ok(new { revisions = engine.ListRevisions(name) }));
        api.MapGet("/skills/{name}/history/{id}", (string name, string id, SyncEngine engine) =>
            Results.Ok(engine.GetRevision(name, id) ?? throw new KeyNotFoundException("That revision is no longer in history.")));
        api.MapPost("/skills/{name}/history/{id}/restore", async (string name, string id, RestoreRevisionRequest request, SyncEngine engine,
            CancellationToken ct) => Results.Ok(new { skill = await engine.RestoreRevisionAsync(name, id, request.Revision, ct).ConfigureAwait(false) }));

        api.MapGet("/updates", async (bool? force, SkillImporterService importer, CancellationToken ct) =>
            Results.Ok(new { updates = await importer.CheckUpdatesAsync(force == true, ct).ConfigureAwait(false) }));
        api.MapGet("/skills/{name}/update", async (string name, SkillImporterService importer, CancellationToken ct) =>
            Results.Ok(await importer.GetUpdateDiffAsync(name, ct).ConfigureAwait(false)));
        api.MapPost("/skills/{name}/update", async (string name, ResolveUpdateRequest request, SkillImporterService importer, CancellationToken ct) =>
            Results.Ok(new { skill = await importer.ResolveUpdateAsync(name, request.Action, request.Revision, ct).ConfigureAwait(false) }));
    }
}
