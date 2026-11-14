using SkillSync.Daemon.Core.Models;
using SkillSync.Daemon.Core.Parsing;

namespace SkillSync.Daemon.Core.Engine;

public sealed partial class SkillImporterService
{
    private const string UnknownLicense = "No license identified";

    private CanonicalSkillModel GetImportableCatalogSkill(string? source, string? path)
    {
        var id = source?.Split('/').LastOrDefault() ?? path?.Split('/').Reverse().Skip(1).FirstOrDefault();
        var catalog = _engine.GetCatalogSkill(id ?? string.Empty) ?? throw new KeyNotFoundException("This catalog entry is no longer available.");
        return catalog.SourceCategory != "system" ? catalog : throw new ArgumentException("System skills cannot be imported.");
    }

    private bool ImportNameExists(string name) => _engine.GetSkill(name) is { IsCatalogOnly: false };

    /// <summary>Reads one file of a reviewed bundle, or the repository README when <paramref name="file"/> is <c>README</c>.</summary>
    public async Task<string> ReadPreviewFileAsync(string repoInput, string path, string revision, string file, CancellationToken ct)
    {
        if (repoInput == "local/catalog")
        {
            file = SkillPaths.SupportingPath(file);
            var catalog = GetImportableCatalogSkill(null, path).Clone();
            catalog.IsCatalogOnly = false;
            catalog.SourceCategory = "user";
            if (ContentHasher.ComputeSemanticHash(catalog) != revision)
                throw new SkillConflictException("The local catalog changed. Reopen the preview.");
            return catalog.SupportingFiles.GetValueOrDefault(file) ?? throw new KeyNotFoundException("That file is outside this bundle.");
        }
        if (revision.Length != 40 || revision.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Choose an immutable GitHub revision.");
        var (owner, repo, sha, _) = await ResolveSelectionAsync(new SkillImportSelection(Repo: repoInput, GitRef: revision), ct).ConfigureAwait(false);
        var index = await _indexer.GetIndexAsync(owner, repo, sha, ct).ConfigureAwait(false);
        var document = SelectDocument(index, path);
        var relative = SkillPaths.NormalizeRelative(file);
        if (!document.Files.Contains(relative, StringComparer.Ordinal))
            throw new KeyNotFoundException("That file is outside this bundle.");
        return await _indexer.ReadTextAsync(index, RepositoryIndexer.SkillDirectoryPrefix(document.Path) + relative, ct).ConfigureAwait(false);
    }
}
