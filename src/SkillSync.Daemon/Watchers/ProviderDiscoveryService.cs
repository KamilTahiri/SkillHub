using SkillSync.Daemon.Core.Models;

namespace SkillSync.Daemon.Watchers;

public sealed record ProviderDirectoryInfo(
    ProviderType Provider,
    string DisplayName,
    string ProviderRootDirectory,
    string SkillsRootDirectory,
    bool IsInstalled,
    string FilePatternDescription
);

public sealed record CatalogSkillDescriptor(
    string Slug,
    string SkillMdPath,
    string SourceCategory,
    string OriginProvider,
    IReadOnlyList<string> SuggestedTags
);

public sealed class ProviderDiscoveryService
{
    public const string LegacyBootstrapSkillName = "00-skillsync-bootstrap";

    private readonly string _userHomeRoot;
    private readonly string _skillHubRoot;
    private readonly bool _isCustomHome;

    public ProviderDiscoveryService(string? customUserHomeRoot = null, string? customSkillHubRoot = null)
    {
        _isCustomHome = !string.IsNullOrWhiteSpace(customUserHomeRoot);
        _userHomeRoot = _isCustomHome
            ? Path.GetFullPath(customUserHomeRoot!)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _skillHubRoot = string.IsNullOrWhiteSpace(customSkillHubRoot)
            ? Path.Combine(_userHomeRoot, ".skillhub")
            : Path.GetFullPath(customSkillHubRoot);

        Directory.CreateDirectory(_skillHubRoot);
        Directory.CreateDirectory(CanonicalSkillsRoot);
        Directory.CreateDirectory(ConflictsRoot);

        MigrateLegacySkillSyncIfPresent();
    }

    public string UserHomeRoot => _userHomeRoot;
    public string SkillSyncRoot => _skillHubRoot;
    public string SkillHubRoot => _skillHubRoot;
    public string CanonicalSkillsRoot => Path.Combine(_skillHubRoot, "skills");
    public string ConflictsRoot => Path.Combine(_skillHubRoot, "conflicts");
    public string LedgerFilePath => Path.Combine(_skillHubRoot, ".sync-ledger.json");

    public IReadOnlyList<ProviderDirectoryInfo> DiscoverProviders()
    {
        var codexInstalled = DetectOpenAiCodexOrChatGpt();
        var codexRoot = Path.Combine(_userHomeRoot, ".codex");
        var agentsRoot = Path.Combine(_userHomeRoot, ".agents");

        // Primary Codex root is ~/.codex/skills if ~/.codex exists or neither exists; ~/.agents/skills if only ~/.agents exists
        var primaryCodexRoot = Directory.Exists(codexRoot) || !Directory.Exists(agentsRoot)
            ? codexRoot
            : agentsRoot;
        var primaryCodexSkills = Path.Combine(primaryCodexRoot, "skills");

        var providers = new List<ProviderDirectoryInfo>
        {
            BuildInfo(
                ProviderType.Canonical,
                "SkillHub Canonical Store",
                _skillHubRoot,
                CanonicalSkillsRoot,
                "~/.skillhub/skills/<name>/SKILL.md",
                forceInstalled: true),
            BuildInfo(
                ProviderType.Claude,
                "Claude",
                Path.Combine(_userHomeRoot, ".claude"),
                Path.Combine(_userHomeRoot, ".claude", "skills"),
                "~/.claude/skills/<name>/SKILL.md"),
            BuildInfo(
                ProviderType.Gemini,
                "Gemini & Antigravity",
                Path.Combine(_userHomeRoot, ".gemini"),
                Path.Combine(_userHomeRoot, ".gemini", "skills"),
                "~/.gemini/skills/<name>/SKILL.md"),
            BuildInfo(
                ProviderType.Codex,
                "ChatGPT / OpenAI Codex",
                primaryCodexRoot,
                primaryCodexSkills,
                "~/.codex/skills/<name>/SKILL.md & ~/.agents/skills/<name>/SKILL.md",
                forceInstalled: codexInstalled),
            BuildInfo(
                ProviderType.Cursor,
                "Cursor",
                Path.Combine(_userHomeRoot, ".cursor"),
                Path.Combine(_userHomeRoot, ".cursor", "rules"),
                "~/.cursor/rules/<name>.mdc")
        };

        return providers;
    }

    /// <summary>
    /// Discovers System Skills (.system) and Built-in / Template / Plugin Skills across Codex and Gemini/Antigravity.
    /// Ordered by priority so official OpenAI Templates, OpenAI Built-in capabilities, and Antigravity Built-in skills take precedence.
    /// </summary>
    public IReadOnlyList<CatalogSkillDescriptor> DiscoverExternalCatalogSkills()
    {
        var results = new List<CatalogSkillDescriptor>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddSkillFile(string skillMdPath, string sourceCategory, string originProvider, IEnumerable<string>? tags = null)
        {
            if (!File.Exists(skillMdPath))
            {
                return;
            }

            var parentDir = Path.GetDirectoryName(skillMdPath);
            var folderName = string.IsNullOrEmpty(parentDir) ? string.Empty : Path.GetFileName(parentDir);
            if (string.IsNullOrWhiteSpace(folderName) ||
                string.Equals(folderName, "index", StringComparison.OrdinalIgnoreCase))
            {
                // If folder is "index" inside a plugin, prepend the grandparent plugin name
                var grandParent = string.IsNullOrEmpty(parentDir) ? null : Path.GetDirectoryName(parentDir);
                var pluginParent = string.IsNullOrEmpty(grandParent) ? null : Path.GetDirectoryName(grandParent);
                var pluginName = string.IsNullOrEmpty(pluginParent) ? null : Path.GetFileName(pluginParent);
                if (!string.IsNullOrWhiteSpace(pluginName))
                {
                    folderName = $"{pluginName}-{folderName}";
                }
            }

            var slug = NormalizeSlug(folderName);
            if (string.IsNullOrEmpty(slug) ||
                string.Equals(slug, LegacyBootstrapSkillName, StringComparison.OrdinalIgnoreCase) ||
                !seenSlugs.Add(slug))
            {
                return;
            }

            var tagList = new List<string>();
            if (tags is not null)
            {
                foreach (var t in tags)
                {
                    var cleanTag = NormalizeSlug(t);
                    if (!string.IsNullOrEmpty(cleanTag) && !tagList.Contains(cleanTag, StringComparer.OrdinalIgnoreCase))
                    {
                        tagList.Add(cleanTag);
                    }
                }
            }

            results.Add(new CatalogSkillDescriptor(slug, skillMdPath, sourceCategory, originProvider, tagList));
        }

        // 1. Codex System Skills (~/.codex/skills/.system/<slug>/SKILL.md and ~/.agents/skills/.system/<slug>/SKILL.md)
        foreach (var sysRoot in new[]
        {
            Path.Combine(_userHomeRoot, ".codex", "skills", ".system"),
            Path.Combine(_userHomeRoot, ".agents", "skills", ".system")
        })
        {
            if (!Directory.Exists(sysRoot))
            {
                continue;
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(sysRoot))
                {
                    TryAddSkillFile(Path.Combine(dir, "SKILL.md"), "system", "Codex System");
                }
            }
            catch
            {
            }
        }

        // 2. Official OpenAI Artifact Templates (~/.codex/plugins/cache/openai-curated-remote/openai-templates/<ver>/skills/<slug>/SKILL.md)
        var openAiTemplatesRoot = Path.Combine(_userHomeRoot, ".codex", "plugins", "cache", "openai-curated-remote", "openai-templates");
        if (Directory.Exists(openAiTemplatesRoot))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(openAiTemplatesRoot, "SKILL.md", SearchOption.AllDirectories))
                {
                    TryAddSkillFile(file, "template", "OpenAI Template");
                }
            }
            catch
            {
            }
        }

        // 3. OpenAI Primary Runtime & Bundled Capabilities (~/.codex/plugins/cache/openai-primary-runtime, openai-bundled, openai-curated-remote)
        foreach (var cacheSub in new[] { "openai-primary-runtime", "openai-bundled", "openai-curated-remote" })
        {
            var cacheDir = Path.Combine(_userHomeRoot, ".codex", "plugins", "cache", cacheSub);
            if (!Directory.Exists(cacheDir))
            {
                continue;
            }

            try
            {
                foreach (var pluginDir in Directory.EnumerateDirectories(cacheDir))
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    if (string.Equals(pluginName, "openai-templates", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(pluginDir, "SKILL.md", SearchOption.AllDirectories))
                    {
                        TryAddSkillFile(file, "template", "OpenAI Built-in");
                    }
                }
            }
            catch
            {
            }
        }

        // 4. OpenAI Bundled Marketplace Capabilities (~/.codex/.tmp/bundled-marketplaces/openai-bundled/plugins/<plugin>/skills/<slug>/SKILL.md)
        var bundledMarketplaceRoot = Path.Combine(_userHomeRoot, ".codex", ".tmp", "bundled-marketplaces", "openai-bundled", "plugins");
        if (Directory.Exists(bundledMarketplaceRoot))
        {
            try
            {
                foreach (var pluginDir in Directory.EnumerateDirectories(bundledMarketplaceRoot))
                {
                    foreach (var file in Directory.EnumerateFiles(pluginDir, "SKILL.md", SearchOption.AllDirectories))
                    {
                        TryAddSkillFile(file, "template", "OpenAI Built-in");
                    }
                }
            }
            catch
            {
            }
        }

        // 5. Gemini & Antigravity Built-in Skills (~/.gemini/antigravity*/builtin/skills/<slug>/SKILL.md & ~/.gemini/skills/.system)
        foreach (var agFolder in new[] { "antigravity", "antigravity-cli", "antigravity-ide" })
        {
            var agBuiltin = Path.Combine(_userHomeRoot, ".gemini", agFolder, "builtin", "skills");
            if (!Directory.Exists(agBuiltin))
            {
                continue;
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(agBuiltin))
                {
                    TryAddSkillFile(Path.Combine(dir, "SKILL.md"), "template", "Antigravity Built-in");
                }
            }
            catch
            {
            }
        }

        var geminiSysRoot = Path.Combine(_userHomeRoot, ".gemini", "skills", ".system");
        if (Directory.Exists(geminiSysRoot))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(geminiSysRoot))
                {
                    TryAddSkillFile(Path.Combine(dir, "SKILL.md"), "system", "Gemini System");
                }
            }
            catch
            {
            }
        }

        // 5b. Claude & Cursor Installed Plugins (~/.claude/plugins/**/SKILL.md, ~/.cursor/plugins/**/SKILL.md)
        foreach (var (pluginRoot, originLabel) in new[]
        {
            (Path.Combine(_userHomeRoot, ".claude", "plugins"), "Claude Built-in"),
            (Path.Combine(_userHomeRoot, ".cursor", "plugins"), "Cursor Built-in")
        })
        {
            if (!Directory.Exists(pluginRoot))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(pluginRoot, "SKILL.md", SearchOption.AllDirectories))
                {
                    TryAddSkillFile(file, "template", originLabel);
                }
            }
            catch
            {
            }
        }

        // 6. Codex Plugin Catalog Skills (~/.codex/.tmp/plugins/plugins/<plugin>/skills/.../SKILL.md)
        // Categorized separately as "plugin" so uninstalled marketplace plugins never clutter "My Skills" or "Built-in & Templates"
        var codexPluginsCatalog = Path.Combine(_userHomeRoot, ".codex", ".tmp", "plugins", "plugins");
        if (Directory.Exists(codexPluginsCatalog))
        {
            try
            {
                foreach (var pluginDir in Directory.EnumerateDirectories(codexPluginsCatalog))
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    var skillsSubDir = Path.Combine(pluginDir, "skills");
                    if (!Directory.Exists(skillsSubDir))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(skillsSubDir, "SKILL.md", SearchOption.AllDirectories))
                    {
                        TryAddSkillFile(file, "plugin", $"Plugin: {CanonicalSkillModel.HumanizeSlug(pluginName)}");
                    }
                }
            }
            catch
            {
            }
        }

        return results;
    }

    /// <summary>
    /// Checks all indicators for OpenAI Codex / ChatGPT installation:
    /// 1. ~/.codex (or ~/.codex/skills, ~/.codex/config.toml, ~/.codex/auth.json)
    /// 2. ~/.agents (or ~/.agents/skills)
    /// 3. VS Code / Antigravity / Cursor extensions matching openai.chatgpt*
    /// 4. Windows Store package %LOCALAPPDATA%\Packages\OpenAI.ChatGPT-Desktop* or OpenAI.Codex*
    /// </summary>
    public bool DetectOpenAiCodexOrChatGpt()
    {
        var codexDir = Path.Combine(_userHomeRoot, ".codex");
        if (Directory.Exists(codexDir) ||
            Directory.Exists(Path.Combine(codexDir, "skills")) ||
            File.Exists(Path.Combine(codexDir, "config.toml")) ||
            File.Exists(Path.Combine(codexDir, "auth.json")))
        {
            return true;
        }

        var agentsDir = Path.Combine(_userHomeRoot, ".agents");
        if (Directory.Exists(agentsDir) || Directory.Exists(Path.Combine(agentsDir, "skills")))
        {
            return true;
        }

        // Check IDE extensions (~/.vscode/extensions, ~/.antigravity/extensions, ~/.cursor/extensions)
        var extensionRoots = new[]
        {
            Path.Combine(_userHomeRoot, ".vscode", "extensions"),
            Path.Combine(_userHomeRoot, ".antigravity", "extensions"),
            Path.Combine(_userHomeRoot, ".cursor", "extensions")
        };

        foreach (var extRoot in extensionRoots)
        {
            if (!Directory.Exists(extRoot))
            {
                continue;
            }

            try
            {
                if (Directory.EnumerateDirectories(extRoot, "openai.chatgpt*", SearchOption.TopDirectoryOnly).Any())
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        // Check Windows Store packages only when scanning real host user profile (not isolated unit test temp dirs)
        if (!_isCustomHome && OperatingSystem.IsWindows())
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var packagesDir = Path.Combine(localAppData, "Packages");
                if (Directory.Exists(packagesDir))
                {
                    if (Directory.EnumerateDirectories(packagesDir, "OpenAI.ChatGPT-Desktop*", SearchOption.TopDirectoryOnly).Any() ||
                        Directory.EnumerateDirectories(packagesDir, "OpenAI.Codex*", SearchOption.TopDirectoryOnly).Any())
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
        }

        return false;
    }

    public IReadOnlyList<string> GetAllCodexSkillsRoots()
    {
        return
        [
            Path.Combine(_userHomeRoot, ".codex", "skills"),
            Path.Combine(_userHomeRoot, ".agents", "skills")
        ];
    }

    public ProviderDirectoryInfo GetProviderInfo(ProviderType provider)
    {
        return DiscoverProviders().First(p => p.Provider == provider);
    }

    public void EnsureProviderSkillsDirectoryExists(ProviderType provider)
    {
        if (provider == ProviderType.Codex)
        {
            foreach (var root in GetAllCodexSkillsRoots())
            {
                Directory.CreateDirectory(root);
            }
            return;
        }

        var info = GetProviderInfo(provider);
        Directory.CreateDirectory(info.ProviderRootDirectory);
        Directory.CreateDirectory(info.SkillsRootDirectory);
    }

    public string GetSkillPrimaryFilePath(ProviderType provider, string skillSlug)
    {
        var cleanSlug = NormalizeSlug(skillSlug);
        var info = GetProviderInfo(provider);

        return provider == ProviderType.Cursor
            ? Path.Combine(info.SkillsRootDirectory, $"{cleanSlug}.mdc")
            : Path.Combine(info.SkillsRootDirectory, cleanSlug, "SKILL.md");
    }

    public string? GetSkillDirectoryPath(ProviderType provider, string skillSlug)
    {
        if (provider == ProviderType.Cursor)
        {
            return null;
        }

        var cleanSlug = NormalizeSlug(skillSlug);
        var info = GetProviderInfo(provider);
        return Path.Combine(info.SkillsRootDirectory, cleanSlug);
    }

    /// <summary>
    /// Returns both ~/.codex/skills/&lt;slug&gt; and ~/.agents/skills/&lt;slug&gt; so Codex skills work seamlessly across all Codex/Agents versions.
    /// </summary>
    public IReadOnlyList<string> GetAllCodexSkillDirectories(string skillSlug)
    {
        var cleanSlug = NormalizeSlug(skillSlug);
        return
        [
            Path.Combine(_userHomeRoot, ".codex", "skills", cleanSlug),
            Path.Combine(_userHomeRoot, ".agents", "skills", cleanSlug)
        ];
    }

    /// <summary>
    /// Additional secondary path for Antigravity IDE (~/.gemini/antigravity/skills/&lt;name&gt;/SKILL.md)
    /// when ~/.gemini/antigravity exists on the host machine.
    /// </summary>
    public string? GetAntigravityMirrorSkillDirectory(string skillSlug)
    {
        var antigravityRoot = Path.Combine(_userHomeRoot, ".gemini", "antigravity");
        if (!Directory.Exists(antigravityRoot))
        {
            return null;
        }

        var cleanSlug = NormalizeSlug(skillSlug);
        return Path.Combine(antigravityRoot, "skills", cleanSlug);
    }

    public bool TryResolveSkillFromFilePath(string fullPath, out ProviderType provider, out string skillSlug)
    {
        provider = ProviderType.Canonical;
        skillSlug = string.Empty;

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var normalizedFullPath = Path.GetFullPath(fullPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var candidateRoots = new List<(ProviderType Provider, string RootDir)>
        {
            (ProviderType.Canonical, CanonicalSkillsRoot),
            (ProviderType.Claude, Path.Combine(_userHomeRoot, ".claude", "skills")),
            (ProviderType.Gemini, Path.Combine(_userHomeRoot, ".gemini", "skills")),
            (ProviderType.Gemini, Path.Combine(_userHomeRoot, ".gemini", "antigravity", "skills")),
            (ProviderType.Codex, Path.Combine(_userHomeRoot, ".codex", "skills")),
            (ProviderType.Codex, Path.Combine(_userHomeRoot, ".agents", "skills")),
            (ProviderType.Cursor, Path.Combine(_userHomeRoot, ".cursor", "skills")),
            (ProviderType.Cursor, Path.Combine(_userHomeRoot, ".cursor", "rules"))
        };

        foreach (var (candProvider, rootDir) in candidateRoots)
        {
            var rootWithSep = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;

            if (!normalizedFullPath.StartsWith(rootWithSep, comparison))
            {
                continue;
            }

            var relative = normalizedFullPath[rootWithSep.Length..];
            var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            if (candProvider == ProviderType.Cursor && rootDir.EndsWith("rules", StringComparison.OrdinalIgnoreCase))
            {
                var file = segments[0];
                if (!file.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                provider = ProviderType.Cursor;
                skillSlug = NormalizeSlug(Path.GetFileNameWithoutExtension(file));
                return !string.IsNullOrEmpty(skillSlug);
            }
            else
            {
                if (segments.Length < 2)
                {
                    return false;
                }

                var candidateSlug = segments[0];
                if (candidateSlug.StartsWith('.'))
                {
                    return false;
                }

                var secondSegment = segments[1];
                var isSkillMd = segments.Length == 2 && string.Equals(secondSegment, "SKILL.md", StringComparison.OrdinalIgnoreCase);
                var isSupportingFile = segments.Length >= 3 && IsAllowedSupportingFolder(secondSegment);

                if (!isSkillMd && !isSupportingFile)
                {
                    return false;
                }

                provider = candProvider;
                skillSlug = NormalizeSlug(candidateSlug);
                return !string.IsNullOrEmpty(skillSlug);
            }
        }

        return false;
    }

    public static bool IsAllowedSupportingFolder(string folderName)
    {
        return string.Equals(folderName, "scripts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderName, "resources", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderName, "references", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderName, "examples", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderName, "agents", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderName, "assets", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeSlug(string slugOrTitle)
    {
        if (string.IsNullOrWhiteSpace(slugOrTitle))
        {
            return string.Empty;
        }

        var cleaned = slugOrTitle.Trim().ToLowerInvariant()
            .Replace("c#", "csharp", StringComparison.Ordinal)
            .Replace("f#", "fsharp", StringComparison.Ordinal)
            .Replace("c++", "cpp", StringComparison.Ordinal)
            .Replace(".net", "dotnet", StringComparison.Ordinal);

        var chars = cleaned.Select(c => (char.IsLetterOrDigit(c) || c == '-') ? c : '-').ToArray();
        var joined = new string(chars);
        while (joined.Contains("--", StringComparison.Ordinal))
        {
            joined = joined.Replace("--", "-", StringComparison.Ordinal);
        }

        return joined.Trim('-');
    }

    private void MigrateLegacySkillSyncIfPresent()
    {
        try
        {
            var legacyRoot = Path.Combine(_userHomeRoot, ".skillsync");
            var legacySkillsDir = Path.Combine(legacyRoot, "skills");
            if (Directory.Exists(legacySkillsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(legacySkillsDir))
                {
                    var folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName) ||
                        string.Equals(folderName, LegacyBootstrapSkillName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var targetDir = Path.Combine(CanonicalSkillsRoot, folderName);
                    if (!Directory.Exists(targetDir))
                    {
                        CopyDirectoryRecursive(dir, targetDir);
                    }
                }
            }

            // Clean up legacy 00-skillsync-bootstrap from all provider folders
            var legacyDirsToDelete = new[]
            {
                Path.Combine(CanonicalSkillsRoot, LegacyBootstrapSkillName),
                Path.Combine(_userHomeRoot, ".claude", "skills", LegacyBootstrapSkillName),
                Path.Combine(_userHomeRoot, ".gemini", "skills", LegacyBootstrapSkillName),
                Path.Combine(_userHomeRoot, ".gemini", "antigravity", "skills", LegacyBootstrapSkillName),
                Path.Combine(_userHomeRoot, ".codex", "skills", LegacyBootstrapSkillName),
                Path.Combine(_userHomeRoot, ".agents", "skills", LegacyBootstrapSkillName)
            };

            foreach (var d in legacyDirsToDelete)
            {
                if (Directory.Exists(d))
                {
                    try { Directory.Delete(d, recursive: true); } catch { }
                }
            }

            var legacyCursorRule = Path.Combine(_userHomeRoot, ".cursor", "rules", $"{LegacyBootstrapSkillName}.mdc");
            if (File.Exists(legacyCursorRule))
            {
                try { File.Delete(legacyCursorRule); } catch { }
            }
        }
        catch
        {
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var destSub = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSub);
        }
    }

    private static ProviderDirectoryInfo BuildInfo(
        ProviderType provider,
        string displayName,
        string providerRoot,
        string skillsRoot,
        string patternDesc,
        bool forceInstalled = false)
    {
        var isInstalled = forceInstalled
            || provider == ProviderType.Canonical
            || Directory.Exists(providerRoot)
            || Directory.Exists(skillsRoot);

        return new ProviderDirectoryInfo(
            provider,
            displayName,
            providerRoot,
            skillsRoot,
            isInstalled,
            patternDesc);
    }
}
