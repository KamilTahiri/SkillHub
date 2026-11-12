using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Core.Transpilers;

public interface ISkillTranspiler
{
    ProviderType Provider { get; }

    /// <summary>
    /// Transpiles the canonical skill model into the exact string content for the target provider's file.
    /// </summary>
    string TranspileOutbound(CanonicalSkillModel model, string? canonicalSkillDirPath = null);

    /// <summary>
    /// Ingests raw file content from the provider and performs a non-destructive partial merge
    /// onto an existing canonical skill model (or creates a new one if existingCanonical is null).
    /// </summary>
    CanonicalSkillModel MergeInbound(
        string skillSlug,
        string rawFileContent,
        CanonicalSkillModel? existingCanonical,
        SkillLedgerEntry? ledgerEntry = null,
        string? canonicalSkillDirPath = null);
}

