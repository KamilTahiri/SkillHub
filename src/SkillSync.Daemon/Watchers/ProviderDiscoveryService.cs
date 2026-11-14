using System.Security.Cryptography;
using System.Text;
using SkillSync.Daemon.Core.Engine;
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
    IReadOnlyList<string> SuggestedTags,
    string Id,
    Version? CacheVersion
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


    }

    public string UserHomeRoot => _userHomeRoot;
    public string SkillSyncRoot => _skillHubRoot;
    public string SkillHubRoot => _skillHubRoot;
    public string CanonicalSkillsRoot => Path.Combine(_skillHubRoot, "skills");
    public string ConflictsRoot => Path.Combine(_skillHubRoot, "conflicts");
    public string LedgerFilePath => Path.Combine(_skillHubRoot, ".sync-ledger.json");
    public string TrashRoot => Path.Combine(_skillHubRoot, "trash");

    private static readonly EnumerationOptions CatalogScan = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MaxRecursionDepth = 24
    };

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
        var seenPaths = new HashSet<string>(SkillPaths.Comparer);

        void TryAddSkillFile(string skillMdPath, string sourceCategory, string originProvider, IEnumerable<string>? tags = null)
        {
            try
            {
                SkillPaths.AssertNoReparsePoints(skillMdPath);
            }
            catch (IOException)
            {
                return;
            }
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
                !seenPaths.Add(skillMdPath))
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

            var relative = Path.GetRelativePath(_userHomeRoot, skillMdPath).Replace('\\', '/');
            var segments = relative.Split('/').ToList();
            Version? cacheVersion = null;
            if (segments.Count > 7 && segments[0] == ".codex" && segments[1] == "plugins" && segments[2] == "cache" &&
                Version.TryParse(segments[5].Split('-')[0], out cacheVersion))
            {
                segments.RemoveAt(5);
                relative = string.Join('/', segments);
            }
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                OperatingSystem.IsWindows() ? relative.ToLowerInvariant() : relative))).ToLowerInvariant();
            results.Add(new CatalogSkillDescriptor(slug, skillMdPath, sourceCategory, originProvider, tagList, $"catalog-{identity}", cacheVersion));
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
                foreach (var dir in EnumerateCatalogDirectories(sysRoot))
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
                foreach (var file in EnumerateCatalogFiles(openAiTemplatesRoot))
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
                foreach (var pluginDir in EnumerateCatalogDirectories(cacheDir))
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    if (string.Equals(pluginName, "openai-templates", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var file in EnumerateCatalogFiles(pluginDir))
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
                foreach (var pluginDir in EnumerateCatalogDirectories(bundledMarketplaceRoot))
                {
                    foreach (var file in EnumerateCatalogFiles(pluginDir))
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
                foreach (var dir in EnumerateCatalogDirectories(agBuiltin))
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
                foreach (var dir in EnumerateCatalogDirectories(geminiSysRoot))
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
                foreach (var file in EnumerateCatalogFiles(pluginRoot))
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
                foreach (var pluginDir in EnumerateCatalogDirectories(codexPluginsCatalog))
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    var skillsSubDir = Path.Combine(pluginDir, "skills");
                    if (!Directory.Exists(skillsSubDir))
                    {
                        continue;
                    }

                    foreach (var file in EnumerateCatalogFiles(skillsSubDir))
                    {
                        TryAddSkillFile(file, "plugin", $"Plugin: {CanonicalSkillModel.HumanizeSlug(pluginName)}");
                    }
                }
            }
            catch
            {
            }
        }

        return results.GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.CacheVersion)
                .ThenBy(item => item.SkillMdPath, SkillPaths.Comparer).First()).ToArray();
    }

    private static IEnumerable<string> EnumerateCatalogDirectories(string root)
    {
        SkillPaths.AssertNoReparsePoints(root);
        return Directory.EnumerateDirectories(root, "*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        });
    }

    private static IEnumerable<string> EnumerateCatalogFiles(string root)
    {
        SkillPaths.AssertNoReparsePoints(root);
        return Directory.EnumerateFiles(root, "SKILL.md", CatalogScan);
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

    public IReadOnlyList<(ProviderType Provider, string Root)> GetObservableRoots() =>
    [
        (ProviderType.Canonical, CanonicalSkillsRoot),
        (ProviderType.Claude, Path.Combine(_userHomeRoot, ".claude", "skills")),
        (ProviderType.Claude, Path.Combine(_userHomeRoot, ".claude", "commands")),
        (ProviderType.Gemini, Path.Combine(_userHomeRoot, ".gemini", "skills")),
        (ProviderType.Gemini, Path.Combine(_userHomeRoot, ".gemini", "antigravity", "skills")),
        (ProviderType.Codex, Path.Combine(_userHomeRoot, ".codex", "skills")),
        (ProviderType.Codex, Path.Combine(_userHomeRoot, ".agents", "skills")),
        (ProviderType.Cursor, Path.Combine(_userHomeRoot, ".cursor", "skills")),
        (ProviderType.Cursor, Path.Combine(_userHomeRoot, ".cursor", "rules"))
    ];

    public bool TryResolveSkillFromFilePath(string fullPath, out ProviderType provider, out string skillSlug)
    {
        var location = ResolveObservedPath(fullPath);
        provider = location?.Provider ?? ProviderType.Canonical;
        skillSlug = location?.Slug ?? string.Empty;
        return location is not null;
    }

    public ObservedSkillPath? ResolveObservedPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || AtomicFileWriter.IsTemporaryOrIgnoredPath(fullPath))
        {
            return null;
        }

        var path = Path.GetFullPath(fullPath);
        foreach (var (provider, root) in GetObservableRoots())
        {
            if (!SkillPaths.IsInside(root, path))
            {
                continue;
            }

            var segments = SkillPaths.NormalizeRelative(Path.GetRelativePath(root, path)).Split('/');
            if (segments.Any(segment => segment.StartsWith('.')))
            {
                return null;
            }

            var flatExtension = root.EndsWith($"{Path.DirectorySeparatorChar}rules", StringComparison.OrdinalIgnoreCase)
                ? ".mdc"
                : root.EndsWith($"{Path.DirectorySeparatorChar}commands", StringComparison.OrdinalIgnoreCase) ? ".md" : null;
            if (flatExtension != ".mdc")
            {
                for (var index = 1; index < segments.Length - 1; index++)
                {
                    if (SkillPaths.IsSupportingFolder(segments[index]))
                    {
                        var primary = SkillPaths.Resolve(root, string.Join('/', segments.Take(index).Append("SKILL.md")));
                        return Observed(provider, segments[index - 1], primary, string.Join('/', segments.Skip(index)));
                    }
                }

                if (segments.Length >= 2 && segments[^1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    return Observed(provider, segments[^2], path, null);
                }

                // A text file beside SKILL.md (reference.md, forms.md, LICENSE.txt) belongs to that bundle.
                if (flatExtension is null && segments.Length >= 2 && SkillPaths.IsRootSiblingFile(segments[^1]))
                {
                    var primary = SkillPaths.Resolve(root, string.Join('/', segments.SkipLast(1).Append("SKILL.md")));
                    if (File.Exists(primary)) return Observed(provider, segments[^2], primary, segments[^1]);
                }
            }

            if (flatExtension is not null && Path.GetExtension(path).Equals(flatExtension, StringComparison.OrdinalIgnoreCase))
            {
                return Observed(provider, Path.GetFileNameWithoutExtension(path), path, null);
            }
            return null;
        }

        return null;
    }

    private static ObservedSkillPath? Observed(ProviderType provider, string slug, string primary, string? supporting)
    {
        var normalized = NormalizeSlug(slug);
        return normalized.Length == 0 || normalized == LegacyBootstrapSkillName
            ? null
            : new ObservedSkillPath(provider, normalized, primary, supporting);
    }

    public static bool IsAllowedSupportingFolder(string folderName) => SkillPaths.IsSupportingFolder(folderName);

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

    public void EnsureStorage()
    {
        SkillPaths.AssertNoReparsePoints(_skillHubRoot);
        SkillPaths.AssertNoReparsePoints(CanonicalSkillsRoot);
        Directory.CreateDirectory(CanonicalSkillsRoot);
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

public sealed record ObservedSkillPath(ProviderType Provider, string Slug, string PrimaryPath, string? SupportingPath);
